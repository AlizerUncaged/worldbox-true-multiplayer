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
        public bool InGodPower;   // set while a god power runs, so its drops aren't double-mirrored
        private bool _applyingDisaster;

        private readonly Dictionary<long, Actor> _kingToLocal = new Dictionary<long, Actor>();
        // DEBUG move/object flow counters (read+reset by MoveDiag).
        internal static int DbgMovesSent, DbgMovesRecv, DbgMovesApplied, DbgMovesNullResolve, DbgMovesNoTile, DbgMovesDead, DbgMovesEx, DbgObjSent, DbgObjRecv;
        internal static string DbgLastMoveEx = "";
        private static MethodInfo _clickedFinal;
        private static MethodInfo _die;
        private static MethodInfo _setStatsDirty, _updateStats;

        /// <summary>Mark a follower actor stats-dirty so the game's own parallel update_stats
        /// job recomputes its derived stats (speed/combat/etc.) from the synced
        /// subspecies+traits. We only MARK it — we do NOT call updateStats ourselves
        /// (doing that mid-frame corrupted render data). The batch recomputes at the safe time.</summary>
        private void MarkStatsDirty(Actor a)
        {
            if (a == null) return;
            try { _setStatsDirty?.Invoke(a, null); } catch { }
        }
        private static MethodInfo _checkSprite;
        private static FieldInfo _statsField;        // Actor.stats (BaseStats)
        private static FieldInfo _statsDictField;    // BaseStats._stats_dict
        private static FieldInfo _statValueField;    // BaseStatsContainer.value

        /// <summary>King: read an actor's fully-computed stats into a name→value map.</summary>
        private System.Collections.Generic.Dictionary<string, float> ReadStats(Actor a)
        {
            var d = new System.Collections.Generic.Dictionary<string, float>();
            try
            {
                var stats = _statsField?.GetValue(a);
                var dict = stats != null ? _statsDictField?.GetValue(stats) as System.Collections.IDictionary : null;
                if (dict == null) return d;
                foreach (System.Collections.DictionaryEntry e in dict)
                {
                    var key = e.Key as string;
                    var v = _statValueField?.GetValue(e.Value);
                    if (key != null && v != null) d[key] = Convert.ToSingle(v);
                }
            }
            catch { }
            return d;
        }

        /// <summary>Follower: overwrite an actor's stats with the king's computed values.</summary>
        private void ApplyStats(Actor a, System.Collections.Generic.Dictionary<string, float> dict)
        {
            if (dict == null || dict.Count == 0) return;
            try
            {
                var stats = _statsField?.GetValue(a) as BaseStats;
                if (stats == null) return;
                foreach (var kv in dict) stats[kv.Key] = kv.Value;
            }
            catch { }
        }
        // Actor.data / Subspecies.data are internal at runtime (the publicized DLL
        // lies), so read them via reflection.
        private static FieldInfo _actorDataField;
        private static FieldInfo _subDataField;
        private static FieldInfo _worldLawsField;   // MapBox.world_laws is internal
        private static FieldInfo _mapStatsField;    // MapBox.map_stats is internal
        private static FieldInfo _worldTimeField;   // MapStats.world_time (public field, reached via internal map_stats)
        private static MethodInfo _mapStatsLoad;    // MapStats.load() refreshes year cache
        private static FieldInfo _itemDataField;    // Item.data is internal
        private static MethodInfo _addStatusMethod; // Actor.addStatusEffect(string,float,bool) is internal

        // Registry of structural objects synced generically (Data + manager.loadObject).
        private class ObjReg { public byte Kind; public Type ObjType; public Type DataType; public Func<object> Mgr; public FieldInfo DataField; public MethodInfo PrepareForSave; public MethodInfo GetData; public MethodInfo Save; public MethodInfo ReloadInstance; public MethodInfo IdGetter; }
        private static readonly List<ObjReg> _objReg = new List<ObjReg>();
        private bool _applyingStatus, _applyingEquip, _applyingObject, _applyingMember, _applyingCapture, _applyingKProp;

        private static long IdOf(object o)
        {
            if (o == null) return 0;
            try { var p = o.GetType().GetProperty("id"); if (p != null) return Convert.ToInt64(p.GetValue(o)); } catch { }
            try { var f = AccessTools.Field(o.GetType(), "id"); if (f != null) return Convert.ToInt64(f.GetValue(o)); } catch { }
            return 0;
        }

        // Movement is event-based: the king's per-tile moveTo commits are collected
        // here and flushed once per frame (no polling/interval).
        private readonly List<MoveMsg> _pendingMoves = new List<MoveMsg>(128);
        private bool _applyingMove;
        private float _snapshotTimer;
        private float _timeTimer;
        private float _shortPollTimer, _longPollTimer, _extraLongPollTimer;
        // Periodic full resync is DISABLED (0): each snapshot is a full world reload,
        // which is far too disruptive to run on a timer. The world is sent once on
        // join, and otherwise only when a player explicitly presses "Resync". All
        // ongoing sync is granular (spawns, births, deaths, terrain, fire, traits, …).
        public float PeriodicSnapshotSeconds = 0f;
        private bool _lastPaused;
        private string _lastSpeedId = "";
        private static string SafeSpeedId()
        {
            try { return Config.time_scale_asset != null ? Config.time_scale_asset.id : ""; } catch { return ""; }
        }

        public static void Init(Harmony harmony)
        {
            if (Instance != null) return;
            Instance = new MirrorManager();
            _clickedFinal = AccessTools.Method(typeof(PlayerControl), "clickedFinal",
                new[] { typeof(Vector2Int), typeof(GodPower), typeof(bool) });
            _die = AccessTools.Method(typeof(Actor), "die");
            _checkSprite = AccessTools.Method(typeof(Actor), "checkSpriteToRender");
            _setStatsDirty = AccessTools.Method(typeof(Actor), "setStatsDirty");
            _updateStats = AccessTools.Method(typeof(Actor), "updateStats");
            _actorDataField = AccessTools.Field(typeof(Actor), "data");
            _subDataField = AccessTools.Field(typeof(Subspecies), "data");
            _worldLawsField = AccessTools.Field(typeof(MapBox), "world_laws");
            _mapStatsField = AccessTools.Field(typeof(MapBox), "map_stats");
            _worldTimeField = AccessTools.Field(typeof(MapStats), "world_time");
            _mapStatsLoad = AccessTools.Method(typeof(MapStats), "load");
            _itemDataField = AccessTools.Field(typeof(Item), "data");
            _addStatusMethod = AccessTools.Method(typeof(Actor), "addStatusEffect", new[] { typeof(string), typeof(float), typeof(bool) });
            _statsField = AccessTools.Field(typeof(Actor), "stats");
            _statsDictField = AccessTools.Field(typeof(BaseStats), "_stats_dict");
            _statValueField = AccessTools.Field(AccessTools.TypeByName("BaseStatsContainer"), "value");
            BuildObjectRegistry();
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
            DbgMovesSent++;
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
                // The live actor.data has save-derived fields (asset_id, coordinates,
                // equipment, profession, home, …) UNSET — they're only populated when the
                // game saves the actor. Without this the follower's loadObject sees a null
                // asset_id and throws ArgumentNullException. prepareForSave() fills them.
                try { actor.prepareForSave(); } catch (Exception pe) { ModLog.Warn("[KING] prepareForSave: " + pe.Message); }

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

        public void OnKingDisaster(WorldTile tile, string dropId)
        {
            if (!Active || !IsKing || InGodPower || ApplyingRemote || _applyingDisaster || tile == null || string.IsNullOrEmpty(dropId)) return;
            Send(MirrorKind.Disaster, MirrorCodec.Write(new DisasterMsg { X = tile.pos.x, Y = tile.pos.y, DropId = dropId }));
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

        private static void BuildObjectRegistry()
        {
            void Reg(byte kind, Type obj, Type data, Func<object> mgr) =>
                _objReg.Add(new ObjReg { Kind = kind, ObjType = obj, DataType = data, Mgr = mgr,
                    DataField = AccessTools.Field(obj, "data"),
                    // The game populates save-derived fields (e.g. BuildingData.asset_id) only
                    // in getData()/prepareForSave(); without them a building loads with a null
                    // asset and crashes the follower sim. Call them before serializing, like save does.
                    PrepareForSave = AccessTools.Method(obj, "prepareForSave"),
                    GetData = AccessTools.Method(obj, "getData"),
                    // Cities/kingdoms have NO prepareForSave/getData — they populate their data
                    // (incl. City.data.zones = territory) in save(). Without this the synced
                    // CityData has empty zones -> no territory/banner on the follower.
                    Save = AccessTools.Method(obj, "save"),
                    // id is a PROPERTY on BaseSystemData (public long id { get; set; }), NOT a
                    // field — AccessTools.Field would return null. Use the property getter.
                    IdGetter = AccessTools.PropertyGetter(data, "id") });
            Reg(1, typeof(Kingdom), typeof(KingdomData), () => World.world.kingdoms);
            Reg(2, typeof(City), typeof(CityData), () => World.world.cities);
            // Cities expand and their name/stats change continuously, but we only get a create
            // event once. On a re-send (long_poll override) update the EXISTING city in place via
            // loadCity(data) — re-applies territory zones + name + stats — instead of skipping it.
            _objReg.Find(r => r.Kind == 2).ReloadInstance = AccessTools.Method(typeof(City), "loadCity", new[] { typeof(CityData) });
            Reg(3, typeof(War), typeof(WarData), () => World.world.wars);
            Reg(4, typeof(Alliance), typeof(AllianceData), () => World.world.alliances);
            Reg(5, typeof(Plot), typeof(PlotData), () => World.world.plots);
            Reg(6, typeof(Religion), typeof(ReligionData), () => World.world.religions);
            Reg(7, typeof(Culture), typeof(CultureData), () => World.world.cultures);
            Reg(8, typeof(Language), typeof(LanguageData), () => World.world.languages);
            Reg(9, typeof(Building), typeof(BuildingData), () => World.world.buildings);
        }

        public void OnKingHealth(Actor a)
        {
            if (!Active || !IsKing || a == null) return;
            var data = _actorDataField?.GetValue(a) as ActorData;
            if (data != null) Send(MirrorKind.Health, MirrorCodec.Write(new HealthMsg { Id = a.id, Health = data.health }));
        }

        public void OnKingStatus(Actor a, string statusId, float timer, bool add)
        {
            if (!Active || !IsKing || _applyingStatus || a == null || string.IsNullOrEmpty(statusId)) return;
            Send(MirrorKind.Status, MirrorCodec.Write(new StatusMsg { Id = a.id, StatusId = statusId, Timer = timer, Add = add }));
        }

        public void OnKingEquip(Actor a, Item item)
        {
            if (!Active || !IsKing || _applyingEquip || a == null || item == null) return;
            try
            {
                var data = _itemDataField?.GetValue(item);
                if (data == null) return;
                Send(MirrorKind.Equip, MirrorCodec.Write(new EquipMsg
                { ActorId = a.id, ItemJson = JsonConvert.SerializeObject(data, JsonHelper.read_settings) }));
            }
            catch (Exception e) { ModLog.Warn("OnKingEquip failed: " + e.Message); }
        }

        private int _objDbg;
        public void OnKingObject(object obj)
        {
            if (_objDbg < 16)
            {
                _objDbg++;
                ModLog.Info($"[KING] OnKingObject({obj?.GetType().Name}): Active={Active} IsKing={IsKing} applyingObj={_applyingObject}");
            }
            if (!Active || !IsKing || _applyingObject || obj == null) return;
            try
            {
                bool matched = false;
                foreach (var r in _objReg)
                    if (r.ObjType.IsInstanceOfType(obj))
                    {
                        matched = true;
                        // Populate save-derived fields on the live data first (asset_id, etc.).
                        try { r.PrepareForSave?.Invoke(obj, null); } catch (Exception pe) { ModLog.Warn($"[KING] {r.ObjType.Name}.prepareForSave: " + pe.Message); }
                        // Cities/kingdoms populate their data (incl. zones/territory) in save().
                        try { r.Save?.Invoke(obj, null); } catch (Exception se) { ModLog.Warn($"[KING] {r.ObjType.Name}.save: " + se.Message); }
                        object data = null;
                        try { data = r.GetData?.Invoke(obj, null); } catch (Exception ge) { ModLog.Warn($"[KING] {r.ObjType.Name}.getData: " + ge.Message); }
                        if (data == null) data = r.DataField?.GetValue(obj);   // fallback to live data field
                        if (data == null) { ModLog.Warn($"[KING] OnKingObject({r.ObjType.Name}): data null (getData+field both null) — not synced"); return; }
                        Send(MirrorKind.Object, MirrorCodec.Write(new ObjectMsg
                        { Kind = r.Kind, Json = JsonConvert.SerializeObject(data, JsonHelper.read_settings) }));
                        DbgObjSent++;
                        return;
                    }
                if (!matched && _objDbg < 16) ModLog.Warn($"[KING] OnKingObject: NO registry match for {obj.GetType().Name}");
            }
            catch (Exception e) { ModLog.Warn("OnKingObject failed: " + e.Message); }
        }

        public void OnKingObjectRemove(object obj)
        {
            if (!Active || !IsKing || _applyingObject || obj == null) return;
            foreach (var r in _objReg)
                if (r.ObjType.IsInstanceOfType(obj))
                {
                    Send(MirrorKind.ObjectRemove, MirrorCodec.Write(new ObjectRemoveMsg { Kind = r.Kind, Id = IdOf(obj) }));
                    return;
                }
        }

        public void OnKingMember(Actor a, object target)
        {
            if (!Active || !IsKing || _applyingMember || a == null || target == null) return;
            byte kind = target is Kingdom ? MemberKind.Kingdom
                      : target is Religion ? MemberKind.Religion
                      : target is Culture ? MemberKind.Culture
                      : target is Language ? MemberKind.Language
                      : target is City ? MemberKind.City : (byte)0;
            if (kind == 0) return;
            Send(MirrorKind.Member, MirrorCodec.Write(new MemberMsg { ActorId = a.id, Kind = kind, TargetId = IdOf(target) }));
        }

        public void OnKingCapture(City city, Kingdom k, bool captured, bool rebellion)
        {
            if (!Active || !IsKing || _applyingCapture || city == null) return;
            Send(MirrorKind.Capture, MirrorCodec.Write(new CaptureMsg
            { CityId = city.id, KingdomId = k != null ? k.id : 0, Captured = captured, Rebellion = rebellion }));
        }

        public void OnKingdomProp(Kingdom k, byte prop, object value)
        {
            if (!Active || !IsKing || _applyingKProp || k == null) return;
            Send(MirrorKind.KingdomProp, MirrorCodec.Write(new KingdomPropMsg { KingdomId = k.id, Prop = prop, Value = IdOf(value) }));
        }

        public void OnKingdomColor(Kingdom k, long colorId)
        {
            if (!Active || !IsKing || _applyingKProp || k == null) return;
            Send(MirrorKind.KingdomProp, MirrorCodec.Write(new KingdomPropMsg { KingdomId = k.id, Prop = KingdomPropKind.Color, Value = colorId }));
        }

        public void OnKingWarEnd(object war, object winner)
        {
            if (!Active || !IsKing || war == null) return;
            Send(MirrorKind.WarEnd, MirrorCodec.Write(new WarEndMsg
            { WarId = IdOf(war), Winner = winner != null ? Convert.ToByte(winner) : (byte)0 }));
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
                case MirrorKind.ShortPoll:
                case MirrorKind.LongPoll:
                case MirrorKind.ExtraLongPoll:
                    if (IsFollower) { var sb = MirrorCodec.ReadScalarBatch(msg.Payload, out var pk); ApplyPoll(sb, pk); }
                    break;
                case MirrorKind.ActorResync:
                    if (IsFollower) ApplyActorResync(MirrorCodec.ReadActorResync(msg.Payload));
                    break;
                case MirrorKind.Rename:
                    if (IsFollower) ApplyRename(MirrorCodec.ReadRename(msg.Payload));
                    break;
                case MirrorKind.Health:
                    if (IsFollower) ApplyHealth(MirrorCodec.ReadHealth(msg.Payload));
                    break;
                case MirrorKind.Status:
                    if (IsFollower) ApplyStatus(MirrorCodec.ReadStatus(msg.Payload));
                    break;
                case MirrorKind.Equip:
                    if (IsFollower) ApplyEquip(MirrorCodec.ReadEquip(msg.Payload));
                    break;
                case MirrorKind.Object:
                    if (IsFollower) ApplyObject(MirrorCodec.ReadObject(msg.Payload));
                    break;
                case MirrorKind.ObjectRemove:
                    if (IsFollower) ApplyObjectRemove(MirrorCodec.ReadObjectRemove(msg.Payload));
                    break;
                case MirrorKind.Member:
                    if (IsFollower) ApplyMember(MirrorCodec.ReadMember(msg.Payload));
                    break;
                case MirrorKind.Capture:
                    if (IsFollower) ApplyCapture(MirrorCodec.ReadCapture(msg.Payload));
                    break;
                case MirrorKind.KingdomProp:
                    if (IsFollower) ApplyKingdomProp(MirrorCodec.ReadKingdomProp(msg.Payload));
                    break;
                case MirrorKind.WarEnd:
                    if (IsFollower) ApplyWarEnd(MirrorCodec.ReadWarEnd(msg.Payload));
                    break;
                case MirrorKind.Disaster:
                    if (IsFollower) ApplyDisaster(MirrorCodec.ReadDisaster(msg.Payload));
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
                    // stats are authoritative from the king (extra_long_poll resync); the
                    // follower does not recompute (updateStats is suppressed).
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
                    DbgMovesRecv++;
                    var a = Resolve(m.KingId);
                    if (a == null) { DbgMovesNullResolve++; continue; }
                    if (!a.isAlive()) { DbgMovesDead++; continue; }   // don't walk a corpse
                    var tile = World.world?.GetTile(m.X, m.Y);
                    if (tile == null) { DbgMovesNoTile++; continue; }
                    // Feed the king's step into the actor's OWN path queue so the game's
                    // updatePathMovement walks node-to-node CONTINUOUSLY. Replaying moveTo
                    // per tile made the actor dart to each tile then stopMovement() (which
                    // clears next_step_position + deregisters it) and wait for the next
                    // packet -> idle most of the time -> looked extremely slow. With a path,
                    // isUsingPath() stays true so updateMovement chains to the next node.
                    try
                    {
                        var path = a.current_path;
                        if (a.current_path_index > 0 && a.current_path_index >= path.Count)
                        { path.Clear(); a.current_path_index = 0; }              // fully consumed → reset
                        else if (a.current_path_index > 128)
                        { path.RemoveRange(0, a.current_path_index); a.current_path_index = 0; }  // trim consumed
                        path.Add(tile);
                        if (!a.is_moving) a.updatePathMovement();                // kick off continuous walking
                        DbgMovesApplied++;
                    }
                    catch (Exception me) { DbgMovesEx++; DbgLastMoveEx = (me.InnerException ?? me).Message; }
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

        private void ApplyHealth(HealthMsg m)
        {
            var a = Resolve(m.Id);
            if (a == null) return;
            try { var data = _actorDataField?.GetValue(a) as ActorData; if (data != null) data.health = m.Health; }
            catch (Exception e) { ModLog.Warn("ApplyHealth failed: " + e.Message); }
        }

        private void ApplyStatus(StatusMsg m)
        {
            var a = Resolve(m.Id);
            if (a == null) return;
            _applyingStatus = true;
            try
            {
                if (m.Add) _addStatusMethod?.Invoke(a, new object[] { m.StatusId, m.Timer, true });
                else a.finishStatusEffect(m.StatusId);
            }
            catch (Exception e) { ModLog.Warn("ApplyStatus failed: " + e.Message); }
            finally { _applyingStatus = false; }
        }

        private void ApplyEquip(EquipMsg m)
        {
            var a = Resolve(m.ActorId);
            if (a == null || string.IsNullOrEmpty(m.ItemJson)) return;
            _applyingEquip = true;
            try
            {
                var itemData = (ItemData)JsonConvert.DeserializeObject(m.ItemJson, typeof(ItemData), JsonHelper.read_settings);
                var item = World.world.items.loadObject(itemData);
                if (item != null) a.equipment.setItem(item, a);
            }
            catch (Exception e) { ModLog.Warn("ApplyEquip failed: " + e.Message); }
            finally { _applyingEquip = false; }
        }

        private void ApplyObject(ObjectMsg m)
        {
            _applyingObject = true;
            try
            {
                foreach (var r in _objReg)
                    if (r.Kind == m.Kind)
                    {
                        var data = JsonConvert.DeserializeObject(m.Json, r.DataType, JsonHelper.read_settings);
                        var mgr = r.Mgr();
                        if (mgr == null) return;
                        // If this object already exists (re-sent on long_poll), update it in place
                        // (e.g. City.loadCity re-applies grown territory + name + stats) instead of
                        // loadObject, which no-ops for an existing id and would freeze it.
                        if (r.ReloadInstance != null && r.IdGetter != null)
                        {
                            long id = Convert.ToInt64(r.IdGetter.Invoke(data, null));
                            var get = AccessTools.Method(mgr.GetType(), "get", new[] { typeof(long) });
                            var existing = id != 0 ? get?.Invoke(mgr, new object[] { id }) : null;
                            if (existing != null) { r.ReloadInstance.Invoke(existing, new[] { data }); DbgObjRecv++; return; }
                        }
                        var load = AccessTools.Method(mgr.GetType(), "loadObject", new[] { r.DataType });
                        load?.Invoke(mgr, new[] { data });
                        DbgObjRecv++;
                        return;
                    }
            }
            catch (Exception e) { ModLog.Warn($"ApplyObject(kind={m.Kind}) failed: " + e.Message); }
            finally { _applyingObject = false; }
        }

        private void ApplyObjectRemove(ObjectRemoveMsg m)
        {
            foreach (var r in _objReg)
                if (r.Kind == m.Kind)
                {
                    try
                    {
                        var mgr = r.Mgr(); if (mgr == null) return;
                        var get = AccessTools.Method(mgr.GetType(), "get", new[] { typeof(long) });
                        var obj = get?.Invoke(mgr, new object[] { m.Id });
                        if (obj == null) return;
                        _applyingObject = true;
                        var rem = AccessTools.Method(mgr.GetType(), "removeObject", new[] { r.ObjType });
                        rem?.Invoke(mgr, new[] { obj });
                    }
                    catch (Exception e) { ModLog.Warn($"ApplyObjectRemove(kind={m.Kind}) failed: " + e.Message); }
                    finally { _applyingObject = false; }
                    return;
                }
        }

        private void ApplyMember(MemberMsg m)
        {
            var a = Resolve(m.ActorId); if (a == null) return;
            _applyingMember = true;
            try
            {
                switch (m.Kind)
                {
                    case MemberKind.Kingdom: var k = World.world.kingdoms.get(m.TargetId); if (k != null) a.joinKingdom(k); break;
                    case MemberKind.Religion: var r = World.world.religions.get(m.TargetId); if (r != null) a.setReligion(r); break;
                    case MemberKind.Culture: var c = World.world.cultures.get(m.TargetId); if (c != null) a.setCulture(c); break;
                    case MemberKind.Language: var l = World.world.languages.get(m.TargetId); if (l != null) a.joinLanguage(l); break;
                    case MemberKind.City: var ci = World.world.cities.get(m.TargetId); if (ci != null) a.joinCity(ci); break;
                }
            }
            catch (Exception e) { ModLog.Warn("ApplyMember failed: " + e.Message); }
            finally { _applyingMember = false; }
        }

        private void ApplyCapture(CaptureMsg m)
        {
            var city = World.world.cities.get(m.CityId);
            var k = World.world.kingdoms.get(m.KingdomId);
            if (city == null) return;
            _applyingCapture = true;
            try { city.joinAnotherKingdom(k, m.Captured, m.Rebellion); }
            catch (Exception e) { ModLog.Warn("ApplyCapture failed: " + e.Message); }
            finally { _applyingCapture = false; }
        }

        private void ApplyKingdomProp(KingdomPropMsg m)
        {
            var k = World.world.kingdoms.get(m.KingdomId);
            if (k == null) return;
            _applyingKProp = true;
            try
            {
                switch (m.Prop)
                {
                    case KingdomPropKind.King: var a = World.world.units.get(m.Value); if (a != null) k.setKing(a, false); break;
                    case KingdomPropKind.Capital: var c = World.world.cities.get(m.Value); if (c != null) k.setCapital(c); break;
                    // Color isn't a granular setter on Kingdom; it arrives via KingdomData on create/resync.
                }
            }
            catch (Exception e) { ModLog.Warn("ApplyKingdomProp failed: " + e.Message); }
            finally { _applyingKProp = false; }
        }

        private void ApplyDisaster(DisasterMsg m)
        {
            var tile = World.world?.GetTile(m.X, m.Y);
            if (tile == null) return;
            _applyingDisaster = true;
            try { World.world.drop_manager.spawn(tile, m.DropId); }
            catch (Exception e) { ModLog.Warn("ApplyDisaster failed: " + e.Message); }
            finally { _applyingDisaster = false; }
        }

        private void ApplyWarEnd(WarEndMsg m)
        {
            try
            {
                var war = World.world.wars.get(m.WarId);
                if (war != null) World.world.wars.endWar(war, (WarWinner)m.Winner);
            }
            catch (Exception e) { ModLog.Warn("ApplyWarEnd failed: " + e.Message); }
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

        // Follower: apply a poll batch. Age forces getAge()==king by nudging age_overgrowth
        // (created_time+world_time are already synced, this corrects any residual drift),
        // and every poll re-marks the actor stats-dirty so derived stats stay correct.
        private void ApplyPoll(List<ScalarMsg> list, byte kind)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                var a = Resolve(m.Id);
                if (a == null || !a.isAlive()) continue;
                try
                {
                    if (kind == PollKind.Age)
                    {
                        var data = _actorDataField?.GetValue(a) as ActorData;
                        if (data != null)
                        {
                            int delta = m.V - a.getAge();      // king age - follower age
                            if (delta != 0) data.age_overgrowth += delta;
                        }
                    }
                }
                catch { }
            }
        }

        private int _resyncErr;
        // Follower: force-override each actor's traits + vitals from the king's ActorData,
        // then recompute derived stats. Fixes drifted traits/statuses (health 1/1, low damage,
        // wrong speed) that granular events missed. We re-apply traits + vitals only (kingdom/
        // city/equipment sync via their own events); we do NOT call the full loadFromSave.
        private void ApplyActorResync(List<ActorStateMsg> list)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                var a = Resolve(m.Id);
                if (a == null || !a.isAlive()) continue;
                try
                {
                    var data = JsonConvert.DeserializeObject<ActorData>(m.Json, JsonHelper.read_settings);
                    if (data == null) continue;
                    a.setData(data);
                    try { TraitTools.loadTraits(a, data.saved_traits); } catch { }   // force trait list to match king
                    // Authoritative stats from the king (the follower can't derive them —
                    // its genome/subspecies is incomplete). Apply BEFORE vitals so maxHealth is right.
                    try { ApplyStats(a, JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, float>>(m.StatsJson)); } catch { }
                    try { a.setHealth(data.health, false); } catch { }
                    try { a.setNutrition(data.nutrition, false); } catch { }
                    try { a.setStamina(data.stamina, false); } catch { }
                    try { a.setMana(data.mana, false); } catch { }
                    try { a.setHappiness(data.happiness, false); } catch { }
                }
                catch (Exception e) { if (_resyncErr < 5) { _resyncErr++; ModLog.Warn("ApplyActorResync: " + e.Message); } }
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
            string speed = SafeSpeedId();
            // Broadcast on pause OR speed change — the follower runs a passive sim, so it
            // must match the king's game speed (ticks/frame) or movement replays too slow.
            if (paused != _lastPaused || speed != _lastSpeedId)
            {
                _lastPaused = paused;
                _lastSpeedId = speed;
                Send(MirrorKind.SimControl, MirrorCodec.Write(new SimControlMsg { Paused = paused, SpeedId = speed }));
            }

            // Flush this frame's movement events (event-based, not polled).
            if (_pendingMoves.Count > 0)
            {
                Send(MirrorKind.Move, MirrorCodec.WriteMoveBatch(_pendingMoves));
                _pendingMoves.Clear();
            }

            // Low-rate authoritative clock correction (single scalar — not position polling)
            // so the year/season/day stays locked across clients without drift.
            _timeTimer += dt;
            if (_timeTimer >= 4f)
            {
                _timeTimer = 0f;
                try { Send(MirrorKind.Time, MirrorCodec.Write(new TimeMsg { WorldTime = World.world.getCurWorldTime() })); } catch { }
            }

            if (PeriodicSnapshotSeconds > 0f)
            {
                _snapshotTimer += dt;
                if (_snapshotTimer >= PeriodicSnapshotSeconds) { _snapshotTimer = 0f; ForceSnapshotToAll(); }
            }

            // ── Poll tiers (continuous/Update()-based values we don't send per-frame) ──
            _shortPollTimer += dt;
            if (_shortPollTimer >= 0.1f) { _shortPollTimer = 0f; /* reserved: stats that truly need 100ms */ }
            _longPollTimer += dt;
            if (_longPollTimer >= 0.5f) { _longPollTimer = 0f; SendLongPoll(); }
            _extraLongPollTimer += dt;
            if (_extraLongPollTimer >= 1.0f) { _extraLongPollTimer = 0f; SendActorResync(); }   // age/stats are continuous; structural is event-based
        }

        // extra_long_poll (1000ms): force-override each actor's full state on followers, so
        // traits/statuses/vitals that drifted (or whose events were missed) get corrected and
        // derived stats (max health, speed, damage) recompute correctly. Heavier than scalars,
        // so it's on the slowest tier.
        private void SendActorResync()
        {
            try
            {
                var units = World.world?.units?.getSimpleList();
                if (units == null) return;
                var list = new List<ActorStateMsg>(units.Count);
                for (int i = 0; i < units.Count; i++)
                {
                    var a = units[i];
                    if (a == null || !a.isAlive()) continue;
                    try { a.prepareForSave(); } catch { }
                    var data = _actorDataField?.GetValue(a) as ActorData;
                    if (data == null) continue;
                    list.Add(new ActorStateMsg
                    {
                        Id = a.id,
                        Json = JsonConvert.SerializeObject(data, JsonHelper.read_settings),
                        StatsJson = JsonConvert.SerializeObject(ReadStats(a))   // king's computed stats
                    });
                }
                if (list.Count > 0) Send(MirrorKind.ActorResync, MirrorCodec.WriteActorResync(list));
            }
            catch (Exception e) { ModLog.Warn("SendActorResync failed: " + e.Message); }
        }

        // long_poll (500ms): age — derived from world-time + age_overgrowth on the king; we
        // send the authoritative age so followers (whose passive sim doesn't age identically)
        // stay matched. Cheap (id+int per actor). Extend here for other ~2Hz scalars.
        private void SendLongPoll()
        {
            try
            {
                var units = World.world?.units?.getSimpleList();
                if (units == null) return;
                var list = new List<ScalarMsg>(units.Count);
                for (int i = 0; i < units.Count; i++)
                {
                    var a = units[i];
                    if (a != null && a.isAlive()) list.Add(new ScalarMsg { Id = a.id, V = a.getAge() });
                }
                if (list.Count > 0)
                    Send(MirrorKind.LongPoll, MirrorCodec.WriteScalarBatch(PollKind.Age, list));

                // City/kingdom override (user-approved): cities expand and their name/stats change
                // continuously, but we only get a create event once. Re-send each city/kingdom so
                // the follower's territory + name + stats track the king (ApplyObject updates the
                // existing one in place via loadCity). Cheap: a handful of cities, not per-tile.
                RefreshStructural(World.world?.cities);
                RefreshStructural(World.world?.kingdoms);
            }
            catch (Exception e) { ModLog.Warn("SendLongPoll failed: " + e.Message); }
        }

        // Re-broadcast every live object of a manager (cities/kingdoms) via OnKingObject, which
        // save()s + serializes current state. Followers apply as an in-place update.
        // NOTE: CityManager/KingdomManager : CoreSystemManager — they have NO getSimpleList
        // (that's on the ActorManager/BuildingManager hierarchy). They expose a public `list`
        // field (List<TObject>) and are IEnumerable. Enumerate via `list`.
        private void RefreshStructural(object mgr)
        {
            if (mgr == null) return;
            try
            {
                var lf = AccessTools.Field(mgr.GetType(), "list");
                var listObj = lf?.GetValue(mgr) as System.Collections.IEnumerable;
                if (listObj == null) listObj = mgr as System.Collections.IEnumerable; // CoreSystemManager is IEnumerable
                if (listObj == null) { ModLog.Warn($"RefreshStructural: no list on {mgr.GetType().Name}"); return; }
                var snapshot = new List<object>();
                foreach (var o in listObj) if (o != null) snapshot.Add(o);
                foreach (var o in snapshot) OnKingObject(o);
            }
            catch (Exception e) { ModLog.Warn($"RefreshStructural({mgr.GetType().Name}) failed: " + e.Message); }
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
            // Make the joiner adopt our current game speed/pause immediately (so their
            // passive sim replays movement at the right rate). Re-broadcast next Pump.
            _lastSpeedId = "\0"; _lastPaused = !SafePaused();
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
