using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using WorldboxMP.Client.Net;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Client.Mirror
{
    /// <summary>
    /// The mirroring brain. Implements the king-authoritative model:
    ///
    ///  • A player's action is captured by <see cref="GameHooks"/>. On a follower it
    ///    is NOT applied locally — an intent is sent to the king. On the king it is
    ///    applied and broadcast as an authoritative event.
    ///  • The king is the only simulator. It streams entity positions; followers have
    ///    their AI/movement suppressed and simply apply what the king broadcasts.
    ///  • Spawns are authoritative: the king broadcasts SpawnUnit with its own actor
    ///    id; followers create the unit and remember king-id → local actor, so later
    ///    position updates map correctly. (Spawn-type god powers are not re-run on
    ///    followers; the SpawnUnit event is the single source of truth.)
    ///  • A joining player receives a full world snapshot first, then live deltas.
    ///
    /// Known gaps to iterate on live: natural births/deaths aren't broadcast yet, and
    /// only position (not full per-entity state) is streamed.
    /// </summary>
    public class MirrorManager
    {
        public static MirrorManager Instance { get; private set; }

        // Flags read by the Harmony hooks.
        public bool ApplyingRemote;   // applying a god-power/terrain event from the network
        public bool ApplyingSpawn;    // applying an authoritative SpawnUnit event

        private readonly Dictionary<long, Actor> _kingToLocal = new Dictionary<long, Actor>();
        private static MethodInfo _clickedFinal;

        private float _streamTimer;
        private const float StreamInterval = 0.1f;   // 10 Hz entity position stream
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

        // ── Role / state ────────────────────────────────────────────────────
        public bool Active => NetClient.Instance != null && NetClient.Instance.InRoom &&
                              NetClient.Instance.State == ConnState.Connected;
        public bool IsKing => NetClient.Instance != null && NetClient.Instance.IsKing;
        public bool IsFollower => Active && !IsKing;

        private void Reset()
        {
            _kingToLocal.Clear();
            ApplyingRemote = ApplyingSpawn = false;
            _lastPaused = SafePaused();
        }

        // ── Called by hooks (local action capture) ──────────────────────────
        /// <summary>A local god-power application. Returns true if it should also apply locally.</summary>
        public bool OnLocalGodPower(Vector2Int pos, GodPower power)
        {
            if (!Active || power == null) return true;          // singleplayer or inspect → normal
            Send(MirrorKind.GodPower, MirrorCodec.Write(new GodPowerMsg { PowerId = power.id, X = pos.x, Y = pos.y }));
            return IsKing;                                       // king applies; follower waits for the broadcast
        }

        /// <summary>The king spawned a unit → broadcast it authoritatively.</summary>
        public void OnKingSpawned(Actor actor, string assetId)
        {
            if (!Active || !IsKing || ApplyingSpawn || actor == null) return;
            var t = actor.current_tile;
            int x = t != null ? t.pos.x : Mathf.RoundToInt(actor.current_position.x);
            int y = t != null ? t.pos.y : Mathf.RoundToInt(actor.current_position.y);
            Send(MirrorKind.SpawnUnit, MirrorCodec.Write(new SpawnMsg
            { KingId = actor.id, AssetId = assetId, X = x, Y = y, Height = actor.position_height }));
        }

        // ── Incoming network events ─────────────────────────────────────────
        private void OnMirror(MirrorMessage msg)
        {
            switch ((MirrorKind)msg.Kind)
            {
                case MirrorKind.GodPower:
                    var gp = MirrorCodec.ReadGodPower(msg.Payload);
                    ApplyGodPower(gp);
                    if (IsKing) Send(MirrorKind.GodPower, msg.Payload); // rebroadcast intent to all followers
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
            // On followers, spawn-type powers are handled solely via SpawnUnit events.
            if (IsFollower && !string.IsNullOrEmpty(power.actor_asset_id)) return;

            var tile = World.world?.GetTile(m.X, m.Y);
            if (tile == null || _clickedFinal == null) return;

            ApplyingRemote = true;
            try { _clickedFinal.Invoke(World.world.player_control, new object[] { new Vector2Int(m.X, m.Y), power, false }); }
            catch (Exception e) { ModLog.Warn("ApplyGodPower failed: " + e.Message); }
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
                a.current_position = new Vector2(e.X, e.Y);
                a.next_step_position = a.current_position;
                a.position_height = e.H;
                var tile = World.world?.GetTile(Mathf.RoundToInt(e.X), Mathf.RoundToInt(e.Y));
                if (tile != null) a.current_tile = tile;
                try { a.updatePos(); } catch { /* render refresh best-effort */ }
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
            // After a snapshot load, follower actors carry the king's ids → direct lookup.
            return World.world?.units?.get(kingId);
        }

        // ── King: per-frame streaming (pumped by NetPump) ───────────────────
        public void Pump(float dt)
        {
            if (!Active || !IsKing) return;

            // Pause / speed lockstep.
            bool paused = SafePaused();
            if (paused != _lastPaused)
            {
                _lastPaused = paused;
                string speed = "";
                try { speed = Config.time_scale_asset != null ? Config.time_scale_asset.id : ""; } catch { }
                Send(MirrorKind.SimControl, MirrorCodec.Write(new SimControlMsg { Paused = paused, SpeedId = speed }));
            }

            // Entity position stream.
            _streamTimer += dt;
            if (_streamTimer < StreamInterval) return;
            _streamTimer = 0f;
            StreamEntities();
        }

        private void StreamEntities()
        {
            var units = World.world?.units;
            if (units == null) return;
            var list = new List<EntityPos>(256);
            try
            {
                var all = units.getSimpleList();
                foreach (var a in all)
                {
                    if (a == null) continue;
                    list.Add(new EntityPos { KingId = a.id, X = a.current_position.x, Y = a.current_position.y, H = a.position_height });
                    if (list.Count >= 500) break;   // cap packet size; refine with chunking later
                }
            }
            catch (Exception e) { ModLog.Warn("StreamEntities failed: " + e.Message); return; }
            if (list.Count > 0) Send(MirrorKind.EntityState, MirrorCodec.WriteEntityState(list), false);
        }

        // ── Snapshots ────────────────────────────────────────────────────────
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

        // ── Helpers ──────────────────────────────────────────────────────────
        private static bool SafePaused() { try { return Config.paused; } catch { return false; } }

        private void Send(MirrorKind kind, byte[] payload, bool reliable = true)
        {
            // EntityState is high-frequency → tolerate loss; everything else reliable.
            NetClient.Instance.SendMirror(kind, payload);
        }
    }
}
