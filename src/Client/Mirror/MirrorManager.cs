using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using WorldboxMP.Client.Net;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Client.Mirror
{
    /// <summary>
    /// King-authoritative mirroring.
    ///
    ///  • Player actions are captured by <see cref="GameHooks"/>. A follower defers
    ///    its action (sends an intent to the king); the king applies it and the
    ///    result is broadcast to everyone.
    ///  • The king is the authority. It streams unit positions and broadcasts spawns
    ///    and terrain changes. Followers' AI *decisions* are suppressed, but their
    ///    movement/animation/render keep running so creatures look correct and move
    ///    smoothly — we just feed each unit's next_step_position from the king.
    ///  • For everything granular hooks can't cover (genes, traits, kingdoms, plots,
    ///    equipment, meta, fire, biomes, …) the king periodically — and on demand —
    ///    broadcasts a full world snapshot, which followers load. This is the
    ///    catch-all that keeps the whole simulation eventually consistent.
    /// </summary>
    public class MirrorManager
    {
        public static MirrorManager Instance { get; private set; }

        // Flags read by the Harmony hooks.
        public bool ApplyingRemote;
        public bool ApplyingSpawn;

        private readonly Dictionary<long, Actor> _kingToLocal = new Dictionary<long, Actor>();
        private static MethodInfo _clickedFinal;

        private float _streamTimer;
        private const float StreamInterval = 0.066f;   // ~15 Hz position stream
        private float _snapshotTimer;
        // Periodic full resync interval (seconds). Long by default — each resync is a
        // full world reload (a brief hitch). Use the in-game "Resync" button for an
        // immediate one. Set <= 0 to disable periodic resync.
        public float PeriodicSnapshotSeconds = 25f;
        private bool _lastPaused;

        public static void Init(Harmony harmony)
        {
            if (Instance != null) return;
            Instance = new MirrorManager();
            _clickedFinal = AccessTools.Method(typeof(PlayerControl), "clickedFinal",
                new[] { typeof(Vector2Int), typeof(GodPower), typeof(bool) });
            GameHooks.Apply(harmony);

            var net = NetClient.Instance;
            net.OnMirror += Instance.OnMirror;
            net.OnSnapshotRequested += Instance.OnSnapshotRequested;
            net.OnSnapshotReceived += Instance.OnSnapshotReceived;
            net.OnRoomJoined += _ => Instance.Reset();
            ModLog.Info("Mirroring initialized.");
        }

        public bool Active => NetClient.Instance != null && NetClient.Instance.InRoom &&
                              NetClient.Instance.State == ConnState.Connected;
        public bool IsKing => NetClient.Instance != null && NetClient.Instance.IsKing;
        public bool IsFollower => Active && !IsKing;

        private void Reset()
        {
            _kingToLocal.Clear();
            ApplyingRemote = ApplyingSpawn = false;
            _lastPaused = SafePaused();
            _snapshotTimer = 0f;
        }

        // ── Local action capture (from hooks) ───────────────────────────────
        public bool OnLocalGodPower(Vector2Int pos, GodPower power)
        {
            if (!Active || power == null) return true;
            Send(MirrorKind.GodPower, MirrorCodec.Write(new GodPowerMsg { PowerId = power.id, X = pos.x, Y = pos.y }));
            return IsKing;
        }

        public void OnKingSpawned(Actor actor, string assetId)
        {
            if (!Active || !IsKing || ApplyingSpawn || actor == null) return;
            var t = actor.current_tile;
            int x = t != null ? t.pos.x : Mathf.RoundToInt(actor.current_position.x);
            int y = t != null ? t.pos.y : Mathf.RoundToInt(actor.current_position.y);
            Send(MirrorKind.SpawnUnit, MirrorCodec.Write(new SpawnMsg
            { KingId = actor.id, AssetId = assetId, X = x, Y = y, Height = actor.position_height }));
        }

        public void OnKingTerraform(WorldTile tile, TileType main, TopTileType top)
        {
            if (!Active || !IsKing || ApplyingRemote || tile == null || main == null) return;
            Send(MirrorKind.Terrain, MirrorCodec.Write(new TerrainMsg
            { X = tile.pos.x, Y = tile.pos.y, TileId = main.id, TopId = top != null ? top.id : "" }));
        }

        // ── Incoming ────────────────────────────────────────────────────────
        private void OnMirror(MirrorMessage msg)
        {
            switch ((MirrorKind)msg.Kind)
            {
                case MirrorKind.GodPower:
                    ApplyGodPower(MirrorCodec.ReadGodPower(msg.Payload));
                    if (IsKing) Send(MirrorKind.GodPower, msg.Payload);
                    break;
                case MirrorKind.Terrain:
                    ApplyTerrain(MirrorCodec.ReadTerrain(msg.Payload));
                    if (IsKing) Send(MirrorKind.Terrain, msg.Payload);
                    break;
                case MirrorKind.SpawnUnit:
                    if (IsFollower) ApplySpawn(MirrorCodec.ReadSpawn(msg.Payload));
                    break;
                case MirrorKind.EntityState:
                    if (IsFollower) ApplyEntityState(MirrorCodec.ReadEntityState(msg.Payload));
                    break;
                case MirrorKind.SimControl:
                    ApplySimControl(MirrorCodec.ReadSimControl(msg.Payload));
                    break;
            }
        }

        private void ApplyGodPower(GodPowerMsg m)
        {
            var power = AssetManager.powers.get(m.PowerId);
            if (power == null) return;
            if (IsFollower && !string.IsNullOrEmpty(power.actor_asset_id)) return; // spawns come via SpawnUnit
            var tile = World.world?.GetTile(m.X, m.Y);
            if (tile == null || _clickedFinal == null) return;
            ApplyingRemote = true;
            try { _clickedFinal.Invoke(World.world.player_control, new object[] { new Vector2Int(m.X, m.Y), power, false }); }
            catch (Exception e) { ModLog.Warn("ApplyGodPower failed: " + e.Message); }
            finally { ApplyingRemote = false; }
        }

        private void ApplyTerrain(TerrainMsg m)
        {
            if (IsKing) return; // king is the source
            var tile = World.world?.GetTile(m.X, m.Y);
            if (tile == null) return;
            var main = AssetManager.tiles.get(m.TileId);
            if (main == null) return;
            var top = string.IsNullOrEmpty(m.TopId) ? null : AssetManager.top_tiles.get(m.TopId);
            ApplyingRemote = true;
            try { MapAction.terraformTile(tile, main, top); }
            catch (Exception e) { ModLog.Warn("ApplyTerrain failed: " + e.Message); }
            finally { ApplyingRemote = false; }
        }

        private void ApplySpawn(SpawnMsg m)
        {
            var tile = World.world?.GetTile(m.X, m.Y);
            if (tile == null) return;
            ApplyingSpawn = true;
            try
            {
                var a = World.world.units.spawnNewUnit(m.AssetId, tile, false, false, m.Height);
                if (a != null) _kingToLocal[m.KingId] = a;
            }
            catch (Exception e) { ModLog.Warn("ApplySpawn failed: " + e.Message); }
            finally { ApplyingSpawn = false; }
        }

        private void ApplyEntityState(List<EntityPos> list)
        {
            foreach (var e in list)
            {
                var a = Resolve(e.KingId);
                if (a == null) continue;
                var target = new Vector2(e.X, e.Y);
                // Feed the game's own smooth movement: it interpolates current → next_step.
                a.next_step_position = target;
                a.position_height = e.H;
                // Snap on large jumps (teleports / first sight) to avoid long glides.
                if ((a.current_position - target).sqrMagnitude > 9f)
                    a.current_position = target;
                var tile = World.world?.GetTile(Mathf.RoundToInt(e.X), Mathf.RoundToInt(e.Y));
                if (tile != null) a.current_tile = tile;
            }
        }

        private void ApplySimControl(SimControlMsg m)
        {
            try
            {
                Config.paused = m.Paused;
                if (!string.IsNullOrEmpty(m.SpeedId)) Config.setWorldSpeed(m.SpeedId, false);
            }
            catch (Exception e) { ModLog.Warn("ApplySimControl failed: " + e.Message); }
        }

        private Actor Resolve(long kingId)
        {
            if (_kingToLocal.TryGetValue(kingId, out var a) && a != null) return a;
            return World.world?.units?.get(kingId);
        }

        // ── King streaming (pumped by NetPump) ──────────────────────────────
        public void Pump(float dt)
        {
            if (!Active || !IsKing) return;

            bool paused = SafePaused();
            if (paused != _lastPaused)
            {
                _lastPaused = paused;
                string speed = "";
                try { speed = Config.time_scale_asset != null ? Config.time_scale_asset.id : ""; } catch { }
                Send(MirrorKind.SimControl, MirrorCodec.Write(new SimControlMsg { Paused = paused, SpeedId = speed }));
            }

            _streamTimer += dt;
            if (_streamTimer >= StreamInterval) { _streamTimer = 0f; StreamEntities(); }

            if (PeriodicSnapshotSeconds > 0f)
            {
                _snapshotTimer += dt;
                if (_snapshotTimer >= PeriodicSnapshotSeconds) { _snapshotTimer = 0f; ForceSnapshotToAll(); }
            }
        }

        private void StreamEntities()
        {
            var units = World.world?.units;
            if (units == null) return;
            var list = new List<EntityPos>(256);
            try
            {
                foreach (var a in units.getSimpleList())
                {
                    if (a == null) continue;
                    list.Add(new EntityPos { KingId = a.id, X = a.current_position.x, Y = a.current_position.y, H = a.position_height });
                    if (list.Count >= 500) break;
                }
            }
            catch (Exception e) { ModLog.Warn("StreamEntities failed: " + e.Message); return; }
            if (list.Count > 0) Send(MirrorKind.EntityState, MirrorCodec.WriteEntityState(list), DeliveryMethod.Sequenced);
        }

        // ── Snapshots ───────────────────────────────────────────────────────
        /// <summary>King: broadcast a full world snapshot to every follower (full resync).</summary>
        public void ForceSnapshotToAll()
        {
            if (!Active || !IsKing) return;
            var data = WorldSync.Save();
            if (data.Length > 0) { NetClient.Instance.SendSnapshot("", data); ModLog.Info($"Broadcast world resync ({data.Length} bytes)."); }
        }

        private void OnSnapshotRequested(SnapshotRequest req)
        {
            if (!IsKing) return;
            var data = WorldSync.Save();
            if (data.Length > 0) NetClient.Instance.SendSnapshot(req.ForId4, data);
        }

        private void OnSnapshotReceived(WorldSnapshot snap)
        {
            WorldSync.Load(snap.Data);
            _kingToLocal.Clear();   // post-load actors carry king ids; new spawns repopulate the map
        }

        // ── Helpers ─────────────────────────────────────────────────────────
        private static bool SafePaused() { try { return Config.paused; } catch { return false; } }

        private void Send(MirrorKind kind, byte[] payload, DeliveryMethod method = DeliveryMethod.ReliableOrdered) =>
            NetClient.Instance.SendMirror(kind, payload, method);
    }
}
