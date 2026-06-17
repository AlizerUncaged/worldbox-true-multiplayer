using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using Newtonsoft.Json;
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
        private static MethodInfo _die;
        private static MethodInfo _checkSprite;
        // Actor.data / Subspecies.data are internal at runtime (the publicized DLL
        // lies), so read them via reflection.
        private static FieldInfo _actorDataField;
        private static FieldInfo _subDataField;
        private static FieldInfo _worldLawsField;   // MapBox.world_laws is internal
        private static FieldInfo _mapStatsField;    // MapBox.map_stats is internal
        private static FieldInfo _worldTimeField;   // MapStats.world_time (public field, reached via internal map_stats)
        private static MethodInfo _mapStatsLoad;    // MapStats.load() refreshes year cache

        // Movement is event-based: the king's per-tile moveTo commits are collected
        // here and flushed once per frame (no polling/interval).
        private readonly List<MoveMsg> _pendingMoves = new List<MoveMsg>(128);
        private bool _applyingMove;
        private float _snapshotTimer;
        // Periodic full resync is DISABLED (0): each snapshot is a full world reload,
        // which is far too disruptive to run on a timer. The world is sent once on
        // join, and otherwise only when a player explicitly presses "Resync". All
        // ongoing sync is granular (spawns, births, deaths, terrain, fire, traits, …).
        public float PeriodicSnapshotSeconds = 0f;
        private bool _lastPaused;

        public static void Init(Harmony harmony)
        {
            if (Instance != null) return;
            Instance = new MirrorManager();
            _clickedFinal = AccessTools.Method(typeof(PlayerControl), "clickedFinal",
                new[] { typeof(Vector2Int), typeof(GodPower), typeof(bool) });
            _die = AccessTools.Method(typeof(Actor), "die");
            _checkSprite = AccessTools.Method(typeof(Actor), "checkSpriteToRender");
            _actorDataField = AccessTools.Field(typeof(Actor), "data");
            _subDataField = AccessTools.Field(typeof(Subspecies), "data");
            _worldLawsField = AccessTools.Field(typeof(MapBox), "world_laws");
            _mapStatsField = AccessTools.Field(typeof(MapBox), "map_stats");
            _worldTimeField = AccessTools.Field(typeof(MapStats), "world_time");
            _mapStatsLoad = AccessTools.Method(typeof(MapStats), "load");
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
            _pendingMoves.Clear();
            ApplyingRemote = ApplyingSpawn = _applyingMove = false;
            _lastPaused = SafePaused();
            _snapshotTimer = 0f;
        }

        /// <summary>King: an actor committed to step to a tile → queue it for this frame's batch.</summary>
        public void OnKingMove(Actor actor, WorldTile target)
        {
            if (!Active || !IsKing || _applyingMove || actor == null || target == null) return;
            _pendingMoves.Add(new MoveMsg { KingId = actor.id, X = target.pos.x, Y = target.pos.y });
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
            try
            {
                var data = _actorDataField?.GetValue(actor) as ActorData;
                if (data == null) { ModLog.Warn("[KING] spawn: actor.data null/inaccessible"); return; }

                long subId = 0;
                string subJson = "";
                try
                {
                    subId = data.subspecies;
                    if (subId != 0)
                    {
                        var sub = World.world.subspecies.get(subId);
                        var subData = sub != null ? _subDataField?.GetValue(sub) as SubspeciesData : null;
                        if (subData != null) subJson = JsonConvert.SerializeObject(subData, JsonHelper.read_settings);
                    }
                }
                catch (Exception se) { ModLog.Warn("[KING] subspecies serialize skipped: " + se.Message); }

                string actorJson = JsonConvert.SerializeObject(data, JsonHelper.read_settings);
                Send(MirrorKind.SpawnUnit, MirrorCodec.Write(new SpawnMsg { SubId = subId, SubspeciesJson = subJson, ActorJson = actorJson }));
                ModLog.Info($"[KING] broadcast spawn id={actor.id} sub={subId} actor={actorJson.Length}B sub={subJson.Length}B");
            }
            catch (Exception e) { ModLog.Error("OnKingSpawned failed: " + e); }
        }

        public void OnKingTerraform(WorldTile tile, TileType main, TopTileType top)
        {
            if (!Active || !IsKing || ApplyingRemote || tile == null || main == null) return;
            Send(MirrorKind.Terrain, MirrorCodec.Write(new TerrainMsg
            { X = tile.pos.x, Y = tile.pos.y, TileId = main.id, TopId = top != null ? top.id : "" }));
        }

        public void OnKingDeath(Actor actor)
        {
            if (!Active || !IsKing || actor == null) return;
            Send(MirrorKind.Death, MirrorCodec.Write(new DeathMsg { KingId = actor.id }));
        }

        public void OnKingFire(WorldTile tile, bool on)
        {
            if (!Active || !IsKing || tile == null) return;
            Send(MirrorKind.Fire, MirrorCodec.Write(new FireMsg { X = tile.pos.x, Y = tile.pos.y, On = on }));
        }

        public void OnKingTrait(Actor actor, string traitId, bool add)
        {
            if (!Active || !IsKing || actor == null || string.IsNullOrEmpty(traitId)) return;
            Send(MirrorKind.Trait, MirrorCodec.Write(new TraitMsg { KingId = actor.id, TraitId = traitId, Add = add }));
        }

        private bool _applyingLaw;
        public void OnKingLaw(string id, bool value)
        {
            if (!Active || !IsKing || _applyingLaw || string.IsNullOrEmpty(id)) return;
            Send(MirrorKind.WorldLaw, MirrorCodec.Write(new WorldLawMsg { Id = id, Value = value }));
        }

        public void OnKingYear()
        {
            if (!Active || !IsKing) return;
            try { Send(MirrorKind.Time, MirrorCodec.Write(new TimeMsg { WorldTime = World.world.getCurWorldTime() })); }
            catch (Exception e) { ModLog.Warn("OnKingYear failed: " + e.Message); }
        }

        private bool _applyingRename;
        public void OnKingRename(Actor actor, string name)
        {
            if (!Active || !IsKing || _applyingRename || actor == null) return;
            Send(MirrorKind.Rename, MirrorCodec.Write(new RenameMsg { Id = actor.id, Name = name }));
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
                case MirrorKind.Move:
                    if (IsFollower) ApplyMoveBatch(MirrorCodec.ReadMoveBatch(msg.Payload));
                    break;
                case MirrorKind.Death:
                    if (IsFollower) ApplyDeath(MirrorCodec.ReadDeath(msg.Payload));
                    break;
                case MirrorKind.Fire:
                    if (IsFollower) ApplyFire(MirrorCodec.ReadFire(msg.Payload));
                    break;
                case MirrorKind.Trait:
                    if (IsFollower) ApplyTrait(MirrorCodec.ReadTrait(msg.Payload));
                    break;
                case MirrorKind.WorldLaw:
                    if (IsFollower) ApplyLaw(MirrorCodec.ReadWorldLaw(msg.Payload));
                    break;
                case MirrorKind.Time:
                    if (IsFollower) ApplyTime(MirrorCodec.ReadTime(msg.Payload));
                    break;
                case MirrorKind.Rename:
                    if (IsFollower) ApplyRename(MirrorCodec.ReadRename(msg.Payload));
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
            if (string.IsNullOrEmpty(m.ActorJson)) return;
            ApplyingSpawn = true;
            try
            {
                // 1) Ensure the subspecies exists first (carries color/genes/base stats).
                if (m.SubId != 0 && !string.IsNullOrEmpty(m.SubspeciesJson) && World.world.subspecies.get(m.SubId) == null)
                {
                    var subData = JsonConvert.DeserializeObject<SubspeciesData>(m.SubspeciesJson, JsonHelper.read_settings);
                    if (subData != null) World.world.subspecies.loadObject(subData);
                }
                // 2) Rebuild the exact actor from its data (same id, stats, traits, equipment…).
                //    Pass the data straight to the game's own loader (it reads the data
                //    fields internally, so we never touch ActorData fields ourselves).
                var actorData = JsonConvert.DeserializeObject<ActorData>(m.ActorJson, JsonHelper.read_settings);
                if (actorData == null) { ModLog.Warn("[FOLLOWER] spawn: actorData deserialized null"); return; }
                var a = World.world.units.loadObject(actorData);
                if (a != null)
                {
                    _kingToLocal[a.id] = a;   // a.id == king's id (loadObject preserves it)
                    try { _checkSprite?.Invoke(a, null); } catch { }
                    ModLog.Info($"[FOLLOWER] spawned id={a.id}");
                }
                else ModLog.Warn("[FOLLOWER] loadObject returned null (tile/asset missing on this client?)");
            }
            catch (Exception e) { ModLog.Error("ApplySpawn failed: " + e); }
            finally { ApplyingSpawn = false; }
        }

        private void ApplyMoveBatch(List<MoveMsg> list)
        {
            _applyingMove = true;   // so the follower's own moveTo postfix doesn't echo
            try
            {
                foreach (var m in list)
                {
                    var a = Resolve(m.KingId);
                    if (a == null) continue;
                    var tile = World.world?.GetTile(m.X, m.Y);
                    if (tile == null) continue;
                    // Replay the exact step: the game registers the actor as moving,
                    // sets next_step_position, and its own u10_checkSmoothMovement walks
                    // it there (smooth + animated + keeps current_tile valid for detailed LOD).
                    try { a.moveTo(tile); } catch { }
                }
            }
            finally { _applyingMove = false; }
        }

        private void ApplyDeath(DeathMsg m)
        {
            var a = Resolve(m.KingId);
            _kingToLocal.Remove(m.KingId);
            if (a == null || _die == null) return;
            try { _die.Invoke(a, new object[] { false, (AttackType)0, false, false }); }
            catch (Exception e) { ModLog.Warn("ApplyDeath failed: " + e.Message); }
        }

        private void ApplyFire(FireMsg m)
        {
            var tile = World.world?.GetTile(m.X, m.Y);
            if (tile == null) return;
            try
            {
                if (m.On) WorldBehaviourActionFire.addFire(tile);
                else WorldBehaviourActionFire.removeFire(tile);
            }
            catch (Exception e) { ModLog.Warn("ApplyFire failed: " + e.Message); }
        }

        private void ApplyTime(TimeMsg m)
        {
            try
            {
                var stats = _mapStatsField?.GetValue(World.world);
                if (stats == null) return;
                _worldTimeField?.SetValue(stats, m.WorldTime);
                _mapStatsLoad?.Invoke(stats, null);   // refresh year/month cache
            }
            catch (Exception e) { ModLog.Warn("ApplyTime failed: " + e.Message); }
        }

        private void ApplyRename(RenameMsg m)
        {
            var a = Resolve(m.Id);
            if (a == null) return;
            _applyingRename = true;
            try { a.setName(m.Name, false); }
            catch (Exception e) { ModLog.Warn("ApplyRename failed: " + e.Message); }
            finally { _applyingRename = false; }
        }

        private void ApplyLaw(WorldLawMsg m)
        {
            _applyingLaw = true;
            try
            {
                var laws = _worldLawsField?.GetValue(World.world) as WorldLaws;
                laws?.setAgeEnabled(m.Id, m.Value);
            }
            catch (Exception e) { ModLog.Warn("ApplyLaw failed: " + e.Message); }
            finally { _applyingLaw = false; }
        }

        private void ApplyTrait(TraitMsg m)
        {
            var a = Resolve(m.KingId);
            if (a == null) return;
            try { if (m.Add) a.addTrait(m.TraitId, false); else a.removeTrait(m.TraitId); }
            catch (Exception e) { ModLog.Warn("ApplyTrait failed: " + e.Message); }
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

            // Flush this frame's movement-step events (event-based, not polled).
            if (_pendingMoves.Count > 0)
            {
                Send(MirrorKind.Move, MirrorCodec.WriteMoveBatch(_pendingMoves));
                _pendingMoves.Clear();
            }

            if (PeriodicSnapshotSeconds > 0f)
            {
                _snapshotTimer += dt;
                if (_snapshotTimer >= PeriodicSnapshotSeconds) { _snapshotTimer = 0f; ForceSnapshotToAll(); }
            }
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
