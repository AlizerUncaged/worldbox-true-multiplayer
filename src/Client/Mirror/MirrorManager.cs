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
    ///    smoothly - we just feed each unit's next_step_position from the king.
    ///  • For everything granular hooks can't cover (genes, traits, kingdoms, plots,
    ///    equipment, meta, fire, biomes, …) the king periodically - and on demand - ///    broadcasts a full world snapshot, which followers load. This is the
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
        /// subspecies+traits. We only MARK it - we do NOT call updateStats ourselves
        /// (doing that mid-frame corrupted render data). The batch recomputes at the safe time.</summary>
        private void MarkStatsDirty(Actor a)
        {
            if (a == null) return;
            try { _setStatsDirty?.Invoke(a, null); } catch { }
        }
        private static MethodInfo _checkSprite;
        private static MethodInfo _cacheTags;
        private static FieldInfo _isSapientField;
        private static FieldInfo _eraField;
        private static MethodInfo _eraLoadAge;
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

        // Forced-animation sync (attack/eat/sleep/mine/chop/build/cast). The renderer's
        // calculateMainSprite reads ai.action.force_animation(_id); all of these are internal
        // at runtime → reflection. Follower assigns a cached synthetic BehaviourActionActor
        // (pure render data - its startExecute never runs, the follower AI is suppressed).
        private static FieldInfo _actorAiField;        // Actor.ai (AiSystemActor)
        private static FieldInfo _aiActionField;       // AiSystem`5.action (via AiSystemActor)
        private static FieldInfo _aiTaskField;         // AiSystem`5.task (via AiSystemActor)
        private static FieldInfo _forceAnimField;      // BehaviourActionActor.force_animation
        private static FieldInfo _forceAnimIdField;    // BehaviourActionActor.force_animation_id
        private static FieldInfo _animContainerField;  // Actor.animation_container
        private static MethodInfo _checkAnimContainer; // Actor.checkAnimationContainer()
        private static FieldInfo _animSpritesField;    // AnimationContainerUnit.sprites (Dictionary<string,Sprite>)
        private static FieldInfo _jumpTimerField;      // Actor.timer_jump_animation (the lunge/hop)
        private static MethodInfo _punchAnim;          // Actor.punchTargetAnimation (flip+tilt toward target)
        private static object _taskLibrary;            // AssetManager.tasks_actor
        private static MethodInfo _taskLibGet;         // AssetLibrary<BehaviourTaskActor>.get(string)
        private static MethodInfo _kingdomLoad2;       // Kingdom.load2() (internal): links capital + king + King profession
        private static FieldInfo _professionField;     // Actor._profession (internal)
        private static MethodInfo _setProfession;      // Actor.setProfession(UnitProfession, bool) (internal)

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
        // Slash effects, same pattern: collected per frame, flushed with the moves.
        private readonly List<SlashMsg> _pendingSlashes = new List<SlashMsg>(32);
        private bool _applyingSlash;
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

        // ── World epoch ─────────────────────────────────────────────────────
        // The king bumps _epoch when it loads/generates a different world; every mirror
        // is stamped with the sender's epoch and receivers drop mismatches, so stale
        // messages can't corrupt a world that is mid-transfer. Followers start at 0
        // ("no synced world") and adopt the epoch carried by each applied snapshot -
        // which also means a fresh follower applies NOTHING until its first snapshot.
        private byte _epoch = 1;          // king's current world generation (0 reserved)
        private byte _appliedEpoch;       // follower: epoch of the world we actually loaded
        private byte CurrentEpoch => IsKing ? _epoch : _appliedEpoch;
        /// <summary>The world generation this client is on (dev REST/state diagnostics).</summary>
        public byte WorldEpoch => CurrentEpoch;
        private int _epochDropLog;

        // King world-change detection: MapBox.clearWorld sets the flag; once loading has
        // settled for a moment, the king bumps the epoch and re-broadcasts the world.
        private bool _worldChangePending;
        private float _worldChangeSettle;
        private int _pumpErr;

        // ── Chunked snapshot transfers (channel 1, see NetProtocol) ─────────
        private class OutgoingSnapshot
        {
            public string TargetId4;
            public int TransferId;
            public byte Epoch;
            public byte[] Data;
            public int NextIndex;
            public int Count;
        }
        private readonly List<OutgoingSnapshot> _outgoingSnapshots = new List<OutgoingSnapshot>();
        private int _nextTransferId = 1;

        // Show the transfer progress bar only past this many chunks (~a quarter MB with
        // 48KB chunks) - the size where a transfer becomes humanly noticeable.
        private const int ProgressBarMinChunks = 6;

        // Follower-side reassembly (chunks are reliable-ordered, so counting suffices).
        private int _rxTransferId = -1;
        private byte[] _rxSnapshot;
        private int _rxChunksGot, _rxChunkCount;
        private byte _rxEpoch;
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
            _cacheTags = AccessTools.Method(typeof(Subspecies), "cacheTags");
            _isSapientField = AccessTools.Field(typeof(Subspecies), "_is_sapient");
            _eraField = AccessTools.Field(typeof(MapBox), "era_manager");
            _eraLoadAge = AccessTools.Method(AccessTools.TypeByName("WorldAgeManager"), "loadAge");
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
            _actorAiField = AccessTools.Field(typeof(Actor), "ai");
            _aiActionField = AccessTools.Field(typeof(AiSystemActor), "action");   // walks up to AiSystem`5
            _aiTaskField = AccessTools.Field(typeof(AiSystemActor), "task");
            var behActionType = AccessTools.TypeByName("ai.behaviours.BehaviourActionActor");
            _forceAnimField = AccessTools.Field(behActionType, "force_animation");
            _forceAnimIdField = AccessTools.Field(behActionType, "force_animation_id");
            _animContainerField = AccessTools.Field(typeof(Actor), "animation_container");
            _checkAnimContainer = AccessTools.Method(typeof(Actor), "checkAnimationContainer");
            _animSpritesField = AccessTools.Field(AccessTools.TypeByName("AnimationContainerUnit"), "sprites");
            _jumpTimerField = AccessTools.Field(typeof(Actor), "timer_jump_animation");
            _punchAnim = AccessTools.Method(typeof(Actor), "punchTargetAnimation");
            _kingdomLoad2 = AccessTools.Method(typeof(Kingdom), "load2");
            _professionField = AccessTools.Field(typeof(Actor), "_profession");
            _setProfession = AccessTools.Method(typeof(Actor), "setProfession");
            try
            {
                _taskLibrary = AccessTools.Field(typeof(AssetManager), "tasks_actor")?.GetValue(null)
                            ?? AccessTools.Property(typeof(AssetManager), "tasks_actor")?.GetValue(null);
                if (_taskLibrary != null) _taskLibGet = AccessTools.Method(_taskLibrary.GetType(), "get", new[] { typeof(string) });
            }
            catch (Exception te) { ModLog.Warn("tasks_actor library lookup failed: " + te.Message); }
            BuildObjectRegistry();
            GameHooks.Apply(harmony);

            var net = NetClient.Instance;
            net.OnMirror += Instance.OnMirror;
            net.OnSnapshotRequested += Instance.OnSnapshotRequested;
            net.OnSnapshotChunk += Instance.OnSnapshotChunk;
            net.OnRoomJoined += _ => Instance.Reset();
            Api.WbmpApi.WireInternal();   // third-party plugin surface
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
            _lastAnim.Clear();
            ApplyingRemote = ApplyingSpawn = _applyingMove = _applyingProjectile = false;
            _lastPaused = SafePaused();
            _snapshotTimer = 0f;
            _appliedEpoch = 0;                 // follower: no synced world until a snapshot lands
            _outgoingSnapshots.Clear();
            _rxTransferId = -1; _rxSnapshot = null;
            _worldChangePending = false;
            RemoteMagnet.Clear();
            UI.PingHud.ClearTransfer();
        }

        /// <summary>King: an actor committed to step to a tile → queue it for this frame's batch.</summary>
        public void OnKingMove(Actor actor, WorldTile target)
        {
            if (!Active || !IsKing || _applyingMove || actor == null || target == null) return;
            _pendingMoves.Add(new MoveMsg { KingId = actor.id, X = target.pos.x, Y = target.pos.y });
            DbgMovesSent++;
        }

        /// <summary>King: a slash arc played (attack/punch/kick/talk) → queue for this frame's batch.</summary>
        public void OnKingSlash(Actor actor, Vector2 towards, string slashType, float distMod, float targetZ, float startZ, float? angle)
        {
            if (!Active || !IsKing || _applyingSlash || ApplyingRemote || actor == null) return;
            _pendingSlashes.Add(new SlashMsg
            {
                Id = actor.id, TX = towards.x, TY = towards.y, SlashType = slashType ?? "",
                DistMod = distMod, TargetZ = targetZ, StartZ = startZ,
                HasAngle = angle.HasValue, Angle = angle ?? 0f
            });
        }


        // ── Local action capture (from hooks) ───────────────────────────────
        public bool OnLocalGodPower(Vector2Int pos, GodPower power)
        {
            if (!Active || power == null) return true;
            // Inspection / info tools (Village Info, Kingdom Info, Inspect Unit, …) open a UI
            // window instead of changing the world - they set unselect_when_window=true. These
            // are LOCAL UI: do NOT mirror them (else clicking a village opens the dialogue on the
            // other client too) and do NOT suppress them locally (let the clicker see their own
            // window). Only world-changing powers get mirrored.
            // Identify a PURE inspection/info tool (Village Info, Kingdom Info, Inspect Unit):
            // opens a UI window AND has no world effect. unselect_when_window alone is too broad
            // (spawns set it too!), so require: NOT a spawn (no actor_asset_id), NOT terraform,
            // NO drop, NO tile placement. Those are local UI - run for the clicker, don't mirror.
            bool windowTool = false, terra = false; string ptype = "?", dropId = null, tileType = null, actorAsset = null;
            try { windowTool = power.unselect_when_window; } catch { }
            try { terra = power.terraform; } catch { }
            try { ptype = power.type.ToString(); } catch { }
            try { dropId = power.drop_id; } catch { }
            try { tileType = power.tile_type; } catch { }
            try { actorAsset = power.actor_asset_id; } catch { }
            // Selection/inspection tools open a UI window with NO world effect and must NOT be
            // mirrored. Confirmed ids from the decompiled PowerLibrary: "city_select", "inspect".
            // (These have unselect_when_window=FALSE, so the flag heuristic alone misses them.)
            bool inspection = _inspectionPowers.Contains(power.id)
                              || (windowTool && string.IsNullOrEmpty(actorAsset) && !terra
                                  && string.IsNullOrEmpty(dropId) && string.IsNullOrEmpty(tileType)
                                  && ptype == "PowerSpecial");
            if (_godLog < 30)
            {
                _godLog++;
                ModLog.Info($"[GODPOWER] local id='{power.id}' type={ptype} unselWindow={windowTool} terraform={terra} actorAsset='{actorAsset}' drop='{dropId}' tile='{tileType}' -> {(inspection ? "LOCAL-ONLY(inspection,skip mirror)" : "MIRROR")}");
            }
            if (inspection) return true;
            // Drag powers (magnet) read the LOCAL mouse inside their own state machine, so
            // replaying them via clickedFinal is meaningless (the replayer's mouse isn't
            // held). Everyone runs them locally for instant feedback; followers separately
            // stream drag positions (MirrorKind.MagnetDrag, sent from the magnetAction
            // hook) that drive an authoritative RemoteMagnet session on the king.
            if (power.id == "magnet") return true;
            Send(MirrorKind.GodPower, MirrorCodec.Write(new GodPowerMsg { PowerId = power.id, X = pos.x, Y = pos.y }));
            return IsKing;
        }

        /// <summary>Follower (from the Magnet.magnetAction hook): stream the held-drag
        /// position to the king, throttled. The king's RemoteMagnet session drops the
        /// carried units after a short silence, so no explicit release message is needed.</summary>
        private float _lastMagnetSend;
        public void OnFollowerMagnet(WorldTile tile)
        {
            if (!Active || IsKing || tile == null) return;
            if (UnityEngine.Time.unscaledTime - _lastMagnetSend < 0.05f) return;   // ~20 Hz
            _lastMagnetSend = UnityEngine.Time.unscaledTime;
            Send(MirrorKind.MagnetDrag, MirrorCodec.Write(new GodPowerMsg { PowerId = "magnet", X = tile.pos.x, Y = tile.pos.y }));
        }

        public void OnKingSpawned(Actor actor, string assetId)
        {
            if (!Active || !IsKing || ApplyingSpawn || actor == null) return;
            try
            {
                // The live actor.data has save-derived fields (asset_id, coordinates,
                // equipment, profession, home, …) UNSET - they're only populated when the
                // game saves the actor. Without this the follower's loadObject sees a null
                // asset_id and throws ArgumentNullException. prepareForSave() fills them.
                try { actor.prepareForSave(); } catch (Exception pe) { ModLog.Warn("[KING] prepareForSave: " + pe.Message); }

                var data = _actorDataField?.GetValue(actor) as ActorData;
                if (data == null) { ModLog.Warn("[KING] spawn: actor.data null/inaccessible"); return; }

                long subId = 0;
                string subJson = "";
                bool subSapient = false;
                try
                {
                    subId = data.subspecies;
                    if (subId != 0)
                    {
                        var sub = World.world.subspecies.get(subId);
                        try { subSapient = sub != null && sub.isSapient(); } catch { }
                        // NOTE: calling sub.save() here to populate data.saved_chromosome_data (the
                        // genome - needed for the follower to derive isSapient/base_stats) DESTABILIZED
                        // the follower (snapshot reload + "duplicate key" → world wiped). Reverted;
                        // sapient-count parity needs a safer genome-sync path (TODO). Stats stay
                        // king-authoritative via the resync, which is stable.
                        var subData = sub != null ? _subDataField?.GetValue(sub) as SubspeciesData : null;
                        if (subData != null) subJson = JsonConvert.SerializeObject(subData, JsonHelper.read_settings);
                    }
                }
                catch (Exception se) { ModLog.Warn("[KING] subspecies serialize skipped: " + se.Message); }

                string actorJson = JsonConvert.SerializeObject(data, JsonHelper.read_settings);
                Send(MirrorKind.SpawnUnit, MirrorCodec.Write(new SpawnMsg { SubId = subId, SubspeciesJson = subJson, ActorJson = actorJson, SubSapient = subSapient }));
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
            if (DbgDeath < 12) { DbgDeath++; ModLog.Info($"[DEATH] king: actor {actor.id} died -> broadcast"); }
            Send(MirrorKind.Death, MirrorCodec.Write(new DeathMsg { KingId = actor.id }));
        }

        /// <summary>ObjectRemove sub-kind for actors (not part of the structural registry).</summary>
        public const byte ActorRemoveKind = 10;
        private static MethodInfo _unitsDestroy;   // ActorManager.scheduleDestroyOnPlay(Actor), internal
        private int _actorRemoveLog;

        /// <summary>King: an actor was destroyed for real (corpse decayed/cleaned) - followers
        /// remove the same actor so corpses do not pile up and city member lists stay clean.</summary>
        public void OnKingActorDestroy(Actor a)
        {
            if (!Active || !IsKing || a == null) return;
            try { if (SmoothLoader.isLoading()) return; } catch { }   // world clear/reload storms
            Send(MirrorKind.ObjectRemove, MirrorCodec.Write(new ObjectRemoveMsg { Kind = ActorRemoveKind, Id = a.id }));
        }

        private bool _applyingWeather;
        private int _weatherLog;
        /// <summary>King: a cloud spawned (the "clouds" world behaviour only runs here).</summary>
        public void OnKingCloud(string cloudId, float x, float y)
        {
            if (!Active || !IsKing || _applyingWeather || string.IsNullOrEmpty(cloudId)) return;
            Send(MirrorKind.Weather, MirrorCodec.Write(new WeatherMsg { CloudId = cloudId, X = x, Y = y }));
            if (_weatherLog < 8) { _weatherLog++; ModLog.Info($"[WEATHER] king: cloud '{cloudId}' at ({x:F0},{y:F0})"); }
        }

        // Follower: spawn the same cloud at the king's position. Its drift/rain runs in the
        // passive sim's stack_effects update; rain's world changes stay king-authoritative
        // (terraform/fire prefixes block the local side effects).
        private void ApplyWeather(WeatherMsg m)
        {
            if (string.IsNullOrEmpty(m.CloudId)) return;
            _applyingWeather = true;
            try
            {
                EffectsLibrary.spawn("fx_cloud", null, m.CloudId, null, 0f, m.X, m.Y);
                if (_weatherLog < 8) { _weatherLog++; ModLog.Info($"[WEATHER] follower: cloud '{m.CloudId}' at ({m.X:F0},{m.Y:F0})"); }
            }
            catch (Exception e) { ModLog.Warn("ApplyWeather failed: " + (e.InnerException ?? e).Message); }
            finally { _applyingWeather = false; }
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

        private bool _applyingProjectile;
        private bool _applyingAnnounce;
        private int _projSendLog, _projApplyLog, _projErr, _announceLog, _announceErr;

        /// <summary>King: a world-log announcement was posted (the top-left feed) - mirror
        /// the DTO field-by-field so followers see the same message with the same colors/args.
        /// (No JSON: WorldLogMessage's Vector2 property self-references via .normalized.)</summary>
        public void OnKingAnnounce(WorldLogMessage msg)
        {
            if (!Active || !IsKing || _applyingAnnounce || msg == null) return;
            try
            {
                Send(MirrorKind.Announce, MirrorCodec.Write(new AnnounceMsg
                {
                    AssetId = msg.asset_id, Timestamp = msg.timestamp,
                    S1 = msg.special1, S2 = msg.special2, S3 = msg.special3,
                    C1 = msg.color_special_1, C2 = msg.color_special_2, C3 = msg.color_special_3,
                    UnitId = msg.unit_id, KingdomId = msg.kingdom_id,
                    HasPos = msg.x.HasValue && msg.y.HasValue, X = msg.x ?? 0, Y = msg.y ?? 0
                }));
                if (_announceLog < 12) { _announceLog++; ModLog.Info($"[ANNOUNCE] king: '{msg.asset_id}' {msg.special1}/{msg.special2}"); }
            }
            catch (Exception e) { if (_announceErr < 4) { _announceErr++; ModLog.Warn("OnKingAnnounce failed: " + e.Message); } }
        }

        // Follower: re-add the king's announcement locally (fills the HistoryHud ticker AND
        // the log window/DB). The add() postfix cannot echo: OnKingAnnounce is king-only.
        private void ApplyAnnounce(AnnounceMsg m)
        {
            if (string.IsNullOrEmpty(m.AssetId)) return;
            _applyingAnnounce = true;
            try
            {
                var msg = new WorldLogMessage
                {
                    asset_id = m.AssetId, timestamp = m.Timestamp,
                    special1 = m.S1, special2 = m.S2, special3 = m.S3,
                    color_special_1 = m.C1, color_special_2 = m.C2, color_special_3 = m.C3,
                    unit_id = m.UnitId, kingdom_id = m.KingdomId
                };
                if (m.HasPos) { msg.x = m.X; msg.y = m.Y; }
                msg.add();
                if (_announceLog < 12) { _announceLog++; ModLog.Info($"[ANNOUNCE] follower: '{m.AssetId}'"); }
            }
            catch (Exception e) { if (_announceErr < 4) { _announceErr++; ModLog.Warn("ApplyAnnounce failed: " + (e.InnerException ?? e).Message); } }
            finally { _applyingAnnounce = false; }
        }

        /// <summary>King: a projectile launched (ProjectileManager.spawn) → mirror the launch args.
        /// God-power projectiles are skipped (the power itself replays via clickedFinal on followers,
        /// spawning its own) - same guard as drops.</summary>
        public void OnKingProjectile(BaseSimObject initiator, BaseSimObject target, string assetId,
            Vector3 launch, Vector3 targetPos, float targetZ, float startZ)
        {
            if (!Active || !IsKing || InGodPower || ApplyingRemote || _applyingProjectile || string.IsNullOrEmpty(assetId)) return;
            Send(MirrorKind.Projectile, MirrorCodec.Write(new ProjectileMsg
            {
                InitiatorId = initiator != null ? initiator.id : 0,
                TargetId = target != null ? target.id : 0,
                AssetId = assetId,
                LX = launch.x, LY = launch.y, TX = targetPos.x, TY = targetPos.y,
                TargetZ = targetZ, StartZ = startZ
            }));
            if (_projSendLog < 12) { _projSendLog++; ModLog.Info($"[PROJ] king: broadcast '{assetId}' ({launch.x:F0},{launch.y:F0})->({targetPos.x:F0},{targetPos.y:F0})"); }
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
                    // Cities/kingdoms have NO prepareForSave/getData - they populate their data
                    // (incl. City.data.zones = territory) in save(). Without this the synced
                    // CityData has empty zones -> no territory/banner on the follower.
                    Save = AccessTools.Method(obj, "save"),
                    // id is a PROPERTY on BaseSystemData (public long id { get; set; }), NOT a
                    // field - AccessTools.Field would return null. Use the property getter.
                    IdGetter = AccessTools.PropertyGetter(data, "id") });
            Reg(1, typeof(Kingdom), typeof(KingdomData), () => World.world.kingdoms);
            // Kingdoms have no loadKingdom(data); adopt fresh data via the base
            // CoreSystemObject.setData (plain swap) so 500ms re-sends keep kingID/capital/
            // name current - LinkObjectRefs then re-links from the NEW data. (Without this,
            // load2 kept re-linking the STALE creation-time kingID and fought the live
            // KingdomProp updates: crown markers froze on dead/old kings.)
            _objReg.Find(r => r.Kind == 1).ReloadInstance = AccessTools.Method(typeof(Kingdom), "setData", new[] { typeof(KingdomData) });
            Reg(2, typeof(City), typeof(CityData), () => World.world.cities);
            // Cities expand and their name/stats change continuously, but we only get a create
            // event once. On a re-send (long_poll override) update the EXISTING city in place via
            // loadCity(data) - re-applies territory zones + name + stats - instead of skipping it.
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
        private int _applyObjLog;
        private int _kingObjLog;
        private int _godLog;
        private int _cityJoinLog;
        // God powers that only open a UI window (selection/inspection) - never mirror these.
        private static readonly System.Collections.Generic.HashSet<string> _inspectionPowers =
            new System.Collections.Generic.HashSet<string> { "city_select", "inspect" };
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
                        if (data == null) { ModLog.Warn($"[KING] OnKingObject({r.ObjType.Name}): data null (getData+field both null) - not synced"); return; }
                        var json = JsonConvert.SerializeObject(data, JsonHelper.read_settings);
                        // Heavy diag: for cities/kingdoms log what we actually serialize (name/leader/zones)
                        // so we can confirm the king's side holds correct data before it leaves.
                        if ((r.Kind == 2 || r.Kind == 1) && _kingObjLog < 12)
                        {
                            _kingObjLog++;
                            string nm = "?"; long lid = -1; int zc = -1;
                            try { nm = (string)AccessTools.PropertyGetter(r.DataType, "name")?.Invoke(data, null); } catch { }
                            try { lid = Convert.ToInt64(AccessTools.PropertyGetter(r.DataType, "leaderID")?.Invoke(data, null) ?? -1L); } catch { }
                            try { zc = (AccessTools.Field(r.DataType, "zones")?.GetValue(data) as System.Collections.ICollection)?.Count ?? -1; } catch { }
                            bool jsonHasName = json.Contains("\"name\"");
                            ModLog.Info($"[KING] OnKingObject SEND kind={r.Kind} name='{nm}' leaderID={lid} dataZones={zc} jsonLen={json.Length} jsonHasName={jsonHasName}");
                        }
                        Send(MirrorKind.Object, MirrorCodec.Write(new ObjectMsg { Kind = r.Kind, Json = json }));
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
        private int _rxLog;
        private void OnMirror(MirrorMessage msg)
        {
            // Epoch gate: drop anything from a different world generation than the one
            // we're on. Also silences everything on a fresh follower (_appliedEpoch 0)
            // until its first snapshot lands - mirrors must never touch the menu world.
            if (msg.Epoch != CurrentEpoch)
            {
                if (_epochDropLog < 10) { _epochDropLog++; ModLog.Info($"[RX] epoch drop kind={(MirrorKind)msg.Kind} theirs={msg.Epoch} ours={CurrentEpoch}"); }
                return;
            }
            if (_rxLog < 60) { _rxLog++; ModLog.Info($"[RX] kind={(MirrorKind)msg.Kind} bytes={msg.Payload?.Length} isKing={IsKing} isFollower={IsFollower}"); }
            switch ((MirrorKind)msg.Kind)
            {
                case MirrorKind.MagnetDrag:
                    // A follower is dragging the magnet: drive their RemoteMagnet session.
                    if (IsKing) RemoteMagnet.OnDrag(msg.SenderId4, MirrorCodec.ReadGodPower(msg.Payload));
                    break;
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
                case MirrorKind.WorldAge:
                    if (IsFollower) ApplyWorldAge(MirrorCodec.ReadWorldAge(msg.Payload));
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
                case MirrorKind.Anim:
                    if (IsFollower) ApplyAnimBatch(MirrorCodec.ReadAnimBatch(msg.Payload));
                    break;
                case MirrorKind.Projectile:
                    if (IsFollower) ApplyProjectile(MirrorCodec.ReadProjectile(msg.Payload));
                    break;
                case MirrorKind.Slash:
                    if (IsFollower) ApplySlashBatch(MirrorCodec.ReadSlashBatch(msg.Payload));
                    break;
                case MirrorKind.Announce:
                    if (IsFollower) ApplyAnnounce(MirrorCodec.ReadAnnounce(msg.Payload));
                    break;
                case MirrorKind.Weather:
                    if (IsFollower) ApplyWeather(MirrorCodec.ReadWeather(msg.Payload));
                    break;
                case MirrorKind.Plugin:
                    // Third-party channel (WbmpApi). King-authoritative routing like GodPower:
                    // a follower's message reaches the king only; the king relays it onward so
                    // every client sees it, then both sides hand it to the registered handler.
                    var pm = MirrorCodec.ReadPlugin(msg.Payload);
                    if (IsKing) Send(MirrorKind.Plugin, msg.Payload);
                    Api.WbmpApi.DispatchInternal(pm, !IsKing);   // we RECEIVE from the king iff we are not the king
                    break;
                case MirrorKind.SimControl:
                    ApplySimControl(MirrorCodec.ReadSimControl(msg.Payload));
                    break;
            }
        }

        private void ApplyGodPower(GodPowerMsg m)
        {
            // Drag powers never replay via clickedFinal: the magnet's state machine reads
            // the LOCAL mouse, so invoking it here would run against the replayer's input
            // (instantly dropping, or hijacking the king's own held magnet). Follower
            // magnet reaches the king via MirrorKind.MagnetDrag -> RemoteMagnet instead.
            if (m.PowerId == "magnet") return;
            var power = AssetManager.powers.get(m.PowerId);
            if (power == null) return;
            if (_godLog < 30) { _godLog++; ModLog.Info($"[GODPOWER] APPLY id='{m.PowerId}' at ({m.X},{m.Y}) unselWindow={power.unselect_when_window} (follower opens this -> if it's an info tool, THIS is the dialogue-sync bug)"); }
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
                // Force the sapient flag from the king. _is_sapient is derived from the genome
                // (nucleus meta tags) which we don't fully sync, so the follower would compute false
                // → humans miscounted as Mobs. Set it directly (whether the subspecies is new or
                // already known) so the world's Sapient-Population stat matches the king.
                if (m.SubId != 0 && _isSapientField != null)
                {
                    try { var s = World.world.subspecies.get(m.SubId); if (s != null) _isSapientField.SetValue(s, m.SubSapient); }
                    catch (Exception se) { if (_ageLog < 8) ModLog.Warn("[FOLLOWER] set _is_sapient: " + se.Message); }
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
            // Minimal, robust corpse transition. setAlive(false) is public: it sets _alive=false +
            // data.died_time, which switches the sprite to a corpse and makes the move replay skip
            // it (so it stops walking) + lets it decay. We deliberately do NOT call the full private
            // die() - its side effects (exitBuilding/equipment/kingdom.removeKing/city changes) can
            // throw on the follower's mirrored state and leave the actor half-dead (the "walking
            // corpse/blob"). We also keep the _kingToLocal mapping so the king's later corpse removal
            // still resolves on the follower.
            var a = Resolve(m.KingId);
            if (a == null) { if (DbgDeath < 6) { DbgDeath++; ModLog.Warn($"[DEATH] follower: actor {m.KingId} not resolved"); } return; }
            try
            {
                bool was = a.isAlive();
                ApplyingRemote = true;
                if (a.isAlive())
                {
                    // Feed the battle markers (sword icons + death counters on zoom-out).
                    // On the king this happens in Actor.checkDeath -> addUnitKilled; the
                    // follower's die() is blocked, so register the mirrored death here.
                    // BattleKeeperManager.update already runs in the render loop.
                    try { if (a.current_tile != null) BattleKeeperManager.addUnitKilled(a); } catch { }
                    a.setAlive(false);
                }
                ApplyingRemote = false;
                if (DbgDeath < 12) { DbgDeath++; ModLog.Info($"[DEATH] follower: actor {m.KingId} was={was} now={a.isAlive()} tile={(a.current_tile != null)}"); }
            }
            catch (Exception e) { ApplyingRemote = false; ModLog.Warn("[DEATH] ApplyDeath failed: " + e.Message); }
        }
        private static int DbgDeath;

        private void ApplyFire(FireMsg m)
        {
            var tile = World.world?.GetTile(m.X, m.Y);
            if (tile == null) return;
            ApplyingRemote = true;   // let this synced fire through the Fire_Prefix block
            try
            {
                // setFireData is the game's real state setter: it writes the authoritative
                // tile_manager.fires[] flag AND routes to addFire/removeFire (render set).
                // Calling addFire directly left the flag false, so the fire layer drew
                // NOTHING for the dirty tile - fires were invisible on followers.
                tile.setFireData(m.On);
            }
            catch (Exception e) { ModLog.Warn("ApplyFire failed: " + e.Message); }
            finally { ApplyingRemote = false; }
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
                        // Structural objects are re-sent every long_poll. If this id ALREADY exists,
                        // we must NOT loadObject it again - loadObject on an existing id throws
                        // (TargetInvocationException, e.g. kind=1 Kingdom). Instead: update in place
                        // if we have an updater (City.loadCity re-applies territory/name/stats), else
                        // just skip (we already have it; e.g. kingdoms derive territory from cities).
                        object existing = null;
                        if (r.IdGetter != null)
                        {
                            long id = Convert.ToInt64(r.IdGetter.Invoke(data, null));
                            var get = AccessTools.Method(mgr.GetType(), "get", new[] { typeof(long) });
                            existing = id != 0 ? get?.Invoke(mgr, new object[] { id }) : null;
                        }
                        if (existing != null)
                        {
                            if (r.ReloadInstance != null) { r.ReloadInstance.Invoke(existing, new[] { data }); DbgObjRecv++; }
                            LinkObjectRefs(r.Kind, existing);
                            return;   // already have it - never re-loadObject (throws on duplicate id)
                        }
                        var load = AccessTools.Method(mgr.GetType(), "loadObject", new[] { r.DataType });
                        var created = load?.Invoke(mgr, new[] { data });
                        LinkObjectRefs(r.Kind, created);
                        DbgObjRecv++;
                        return;
                    }
            }
            catch (Exception e) { ModLog.Warn($"ApplyObject(kind={m.Kind}) failed: " + e.Message); }
            finally { _applyingObject = false; }
        }

        // Mid-game synced kingdoms/cities arrive via loadObject, which does NOT run the
        // world loader's link pass - so kingdom.king / city.leader stay null on followers
        // and the map's crown/leader-face icons never draw. Resolve the live references
        // the same way the loader does: Kingdom.load2() links capital + king (and sets the
        // King profession); City.loadLeader() links the leader from data.leaderID.
        // ONLY when the live ref is actually stale: load2/setLeader run setProfession ->
        // clearGraphicsFully, and doing that every 500ms re-send thrashes the actor.
        private int _linkErr;
        private static FieldInfo _kingdomDataF, _cityDataF;
        private static MethodInfo _kingdomKingIdGetter, _cityLeaderIdGetter;   // kingID/leaderID are PROPERTIES (default -1)
        private void LinkObjectRefs(byte kind, object obj)
        {
            if (obj == null) return;
            try
            {
                if (kind == 1 && obj is Kingdom k)
                {
                    if (_kingdomDataF == null) _kingdomDataF = AccessTools.Field(typeof(Kingdom), "data");
                    if (_kingdomKingIdGetter == null) _kingdomKingIdGetter = AccessTools.PropertyGetter(typeof(KingdomData), "kingID");
                    long wantKing = 0;
                    try
                    {
                        var kd = _kingdomDataF?.GetValue(k);
                        if (kd != null && _kingdomKingIdGetter != null) wantKing = Convert.ToInt64(_kingdomKingIdGetter.Invoke(kd, null));
                    }
                    catch { }
                    if (wantKing < 0) wantKing = 0;   // -1 = no king
                    long haveKing = 0;
                    try { haveKing = k.king != null ? k.king.id : 0; } catch { }
                    if (wantKing != haveKing) _kingdomLoad2?.Invoke(k, null);
                }
                else if (kind == 2 && obj is City c)
                {
                    if (_cityDataF == null) _cityDataF = AccessTools.Field(typeof(City), "data");
                    if (_cityLeaderIdGetter == null) _cityLeaderIdGetter = AccessTools.PropertyGetter(typeof(CityData), "leaderID");
                    long wantLeader = 0;
                    try
                    {
                        var cd = _cityDataF?.GetValue(c);
                        if (cd != null && _cityLeaderIdGetter != null) wantLeader = Convert.ToInt64(_cityLeaderIdGetter.Invoke(cd, null));
                    }
                    catch { }
                    if (wantLeader < 0) wantLeader = 0;   // -1 = no leader
                    long haveLeader = 0;
                    try { haveLeader = c.leader != null ? c.leader.id : 0; } catch { }
                    if (wantLeader != haveLeader) c.loadLeader();
                }
            }
            catch (Exception e) { if (_linkErr < 4) { _linkErr++; ModLog.Warn($"LinkObjectRefs(kind={kind}): " + (e.InnerException ?? e).Message); } }
        }

        private void ApplyObjectRemove(ObjectRemoveMsg m)
        {
            // Actors are a special sub-kind. Use the game's OWN removal contract
            // (scheduleDestroyOnPlay -> _to_destroy_objects; the passive sim's
            // checkObjectsToDestroy then runs destroyObject + checkContainer exactly like
            // the king's frame does, incl. trait on-remove actions and city cleanup via
            // removeObject). Calling destroyObject directly would skip those steps.
            if (m.Kind == ActorRemoveKind)
            {
                var a = Resolve(m.Id);
                if (a == null) return;
                _applyingObject = true;
                try
                {
                    if (_unitsDestroy == null) _unitsDestroy = AccessTools.Method(typeof(ActorManager), "scheduleDestroyOnPlay", new[] { typeof(Actor) });
                    _unitsDestroy?.Invoke(World.world.units, new object[] { a });
                    _kingToLocal.Remove(m.Id);
                    if (_actorRemoveLog < 8) { _actorRemoveLog++; ModLog.Info($"[REMOVE] follower: actor {m.Id} scheduled for destroy (king cleanup)"); }
                }
                catch (Exception e) { ModLog.Warn("ApplyObjectRemove(actor) failed: " + (e.InnerException ?? e).Message); }
                finally { _applyingObject = false; }
                return;
            }
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

        // Follower: re-spawn the king's projectile for the visual flight (the passive sim's
        // projectiles.update flies + renders it; checkHitOnNearbyUnits is neutralized so it
        // can't damage anything locally). A non-null kingdom is REQUIRED: the manager's
        // updateProjectiles uses projectile.kingdom as a Dictionary key every tick, and
        // checkCollision dereferences kingdom.asset - a null kingdom would exception-storm.
        private void ApplyProjectile(ProjectileMsg m)
        {
            if (string.IsNullOrEmpty(m.AssetId)) return;
            try
            {
                BaseSimObject init = m.InitiatorId != 0 ? (BaseSimObject)Resolve(m.InitiatorId) : null;
                if (init == null && m.InitiatorId != 0)
                    try { init = World.world.buildings.get(m.InitiatorId); } catch { }
                BaseSimObject targ = m.TargetId != 0 ? (BaseSimObject)Resolve(m.TargetId) : null;
                if (targ == null && m.TargetId != 0)
                    try { targ = World.world.buildings.get(m.TargetId); } catch { }
                Kingdom k = init != null ? init.kingdom : null;
                if (k == null && targ != null) k = targ.kingdom;
                if (k == null) k = AnyKingdom();
                if (k == null) { if (_projErr < 4) { _projErr++; ModLog.Warn("[PROJ] follower: no kingdom to own projectile - skipped"); } return; }
                _applyingProjectile = true;
                World.world.projectiles.spawn(init, targ, m.AssetId,
                    new Vector3(m.LX, m.LY, 0f), new Vector3(m.TX, m.TY, 0f), m.TargetZ, m.StartZ, null, k);
                if (_projApplyLog < 12) { _projApplyLog++; ModLog.Info($"[PROJ] follower: spawned '{m.AssetId}' ({m.LX:F0},{m.LY:F0})->({m.TX:F0},{m.TY:F0}) init={(init != null)} targ={(targ != null)}"); }
            }
            catch (Exception e) { if (_projErr < 4) { _projErr++; ModLog.Warn("[PROJ] ApplyProjectile failed: " + (e.InnerException ?? e).Message); } }
            finally { _applyingProjectile = false; }
        }

        private static Kingdom AnyKingdom()
        {
            try
            {
                if (World.world?.kingdoms is System.Collections.IEnumerable ks)
                    foreach (var k in ks) if (k is Kingdom kk) return kk;
            }
            catch { }
            return null;
        }

        private readonly Dictionary<string, object> _animActionCache = new Dictionary<string, object>();
        private readonly Dictionary<string, object> _taskCache = new Dictionary<string, object>();
        private int _animLog, _animErr, _slashLog, _slashErr;
        // Follower: apply the king's action-visual state. All pure render data - the follower's
        // AI is suppressed, so nothing here executes logic:
        //  • ai.task  → the real shared task asset; hasTask()+force_hand_tool makes the
        //    hand-tool overlay (pickaxe/axe/hammer…) render = the visible job animation.
        //  • ai.action → a cached synthetic action carrying force_animation(_id) (e.g. burrow).
        //  • Jump > 0  → set timer_jump_animation (one hop; decays in updateParallelChecks).
        private void ApplyAnimBatch(List<AnimMsg> list)
        {
            if (list == null || _actorAiField == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                var a = Resolve(m.Id);
                if (a == null) continue;
                try
                {
                    var ai = _actorAiField.GetValue(a);
                    if (ai == null) continue;

                    // task (hand tool + unit-window text)
                    if (_aiTaskField != null)
                    {
                        if (string.IsNullOrEmpty(m.TaskId)) _aiTaskField.SetValue(ai, null);
                        else if (a.isAlive())
                        {
                            if (!_taskCache.TryGetValue(m.TaskId, out var task))
                            {
                                task = _taskLibGet?.Invoke(_taskLibrary, new object[] { m.TaskId });
                                _taskCache[m.TaskId] = task;   // cache nulls too (unknown ids)
                            }
                            if (task != null) _aiTaskField.SetValue(ai, task);
                        }
                    }

                    // forced animation (rare, e.g. burrow)
                    if (_aiActionField != null)
                    {
                        if (string.IsNullOrEmpty(m.AnimId)) _aiActionField.SetValue(ai, null);
                        else if (a.isAlive() && HasAnimSprite(a, m.AnimId))
                        {
                            if (!_animActionCache.TryGetValue(m.AnimId, out var act))
                            {
                                act = Activator.CreateInstance(AccessTools.TypeByName("ai.behaviours.BehaviourActionActor"));
                                _forceAnimField?.SetValue(act, true);
                                _forceAnimIdField?.SetValue(act, m.AnimId);
                                _animActionCache[m.AnimId] = act;
                            }
                            _aiActionField.SetValue(ai, act);
                        }
                    }

                    // lunge/hop
                    if (m.Jump > 0f && a.isAlive()) _jumpTimerField?.SetValue(a, m.Jump);

                    if (_animLog < 12) { _animLog++; ModLog.Info($"[ANIM] follower: actor {m.Id} task='{m.TaskId}' anim='{m.AnimId}' jump={m.Jump:F2}"); }
                }
                catch (Exception e) { if (_animErr < 4) { _animErr++; ModLog.Warn("[ANIM] apply failed: " + (e.InnerException ?? e).Message); } }
            }
        }

        // Only force animations this actor's sprite container actually has - // calculateMainSprite indexes sprites[id] directly (KeyNotFound = render storm).
        private bool HasAnimSprite(Actor a, string animId)
        {
            try
            {
                var cont = _animContainerField?.GetValue(a);
                if (cont == null)
                {
                    try { _checkAnimContainer?.Invoke(a, null); } catch { }
                    cont = _animContainerField?.GetValue(a);
                }
                var sprites = cont != null ? _animSpritesField?.GetValue(cont) as System.Collections.IDictionary : null;
                return sprites != null && sprites.Contains(animId);
            }
            catch { return false; }
        }

        // Follower: replay a slash arc (attack swing/punch/kick/talk) on the mirrored actor,
        // plus the flip+tilt toward the target so the attacker visibly faces its victim.
        private void ApplySlashBatch(List<SlashMsg> list)
        {
            if (list == null) return;
            _applyingSlash = true;
            try
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var m = list[i];
                    var a = Resolve(m.Id);
                    if (a == null || !a.isAlive()) continue;
                    try
                    {
                        var towards = new Vector2(m.TX, m.TY);
                        a.spawnSlash(towards, string.IsNullOrEmpty(m.SlashType) ? null : m.SlashType,
                            m.DistMod, m.TargetZ, m.StartZ, m.HasAngle ? (float?)m.Angle : null);
                        try { _punchAnim?.Invoke(a, new object[] { (Vector3)towards, true, false, 40f }); } catch { }
                        if (_slashLog < 12) { _slashLog++; ModLog.Info($"[SLASH] follower: actor {m.Id} '{m.SlashType}' -> ({m.TX:F0},{m.TY:F0})"); }
                    }
                    catch (Exception e) { if (_slashErr < 4) { _slashErr++; ModLog.Warn("[SLASH] apply failed: " + (e.InnerException ?? e).Message); } }
                }
            }
            finally { _applyingSlash = false; }
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
                    // Re-apply the live profession: setData only updates data.profession, not the
                    // _profession field the renderer reads - so crowns/leader hats went stale on
                    // followers. Only when changed (setProfession clears cached graphics), and with
                    // pCancelBeh: false (no behavior side effects on the mirrored actor).
                    try
                    {
                        if (_professionField != null && _setProfession != null)
                        {
                            var cur = _professionField.GetValue(a);
                            if (!Equals(cur, data.profession))
                                _setProfession.Invoke(a, new object[] { data.profession, false });
                        }
                    }
                    catch (Exception pe) { if (_resyncErr < 5) { _resyncErr++; ModLog.Warn("ApplyActorResync profession: " + (pe.InnerException ?? pe).Message); } }
                    // Authoritative stats from the king (the follower can't derive them - // its genome/subspecies is incomplete). Apply BEFORE vitals so maxHealth is right.
                    try { ApplyStats(a, JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, float>>(m.StatsJson)); } catch { }
                    try { a.setHealth(data.health, false); } catch { }
                    try { a.setNutrition(data.nutrition, false); } catch { }
                    try { a.setStamina(data.stamina, false); } catch { }
                    try { a.setMana(data.mana, false); } catch { }
                    try { a.setHappiness(data.happiness, false); } catch { }
                    // City membership: setData sets data.cityID but does NOT add the actor to the
                    // city's `units` list (that needs joinCity). Without this the follower's city
                    // has 0 members -> population/families/leader-resident all show 0. Join the
                    // actor to its city so City.getPopulationPeople() counts it.
                    try
                    {
                        long cid = data.cityID;
                        var targetCity = cid > 0 ? World.world.cities.get(cid) : null;
                        if (_cityJoinLog < 20)
                        {
                            _cityJoinLog++;
                            ModLog.Info($"[FOLLOWER] resync actor id={a.id} data.cityID={cid} cityResolved={(targetCity != null)} alreadyIn={(a.city != null ? "yes" : "no")}");
                        }
                        if (a.city != targetCity)
                        {
                            if (targetCity != null) a.joinCity(targetCity);
                            else if (a.city != null) a.joinCity(null);
                        }
                    }
                    catch (Exception ce) { if (_resyncErr < 5) { _resyncErr++; ModLog.Warn("ApplyActorResync joinCity: " + ce.Message); } }
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
                KingPaused = IsFollower && m.Paused;   // drives the "God King paused" indicator
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

            // Each subsystem is isolated: an exception in one must never abort the whole
            // pump (NetPump.Update runs the REST drain after us - a repeating throw here
            // starved it once and "wedged" the king).
            try { PumpWorldChange(dt); } catch (Exception e) { if (_pumpErr < 5) { _pumpErr++; ModLog.Warn("PumpWorldChange: " + e.Message); } }
            try { PumpSnapshotTransfers(); } catch (Exception e) { if (_pumpErr < 5) { _pumpErr++; ModLog.Warn("PumpSnapshotTransfers: " + e.Message); } }
            try { RemoteMagnet.Update(); } catch (Exception e) { if (_pumpErr < 5) { _pumpErr++; ModLog.Warn("RemoteMagnet: " + e.Message); } }

            // Sync ONLY the explicit pause (Config.paused = the pause button). The game's
            // EFFECTIVE pause is `Config.paused || isWindowActive || ads` (decompiled) - so
            // opening a dialog/menu pauses the King via the SEPARATE isWindowActive term WITHOUT
            // setting Config.paused. Reading Config.paused therefore naturally excludes
            // dialog-pauses, so followers keep playing while the God King is in a menu.
            bool paused = SafePaused();
            string speed = SafeSpeedId();
            // Broadcast on pause OR speed change - the follower runs a passive sim, so it
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

            // Flush this frame's slash effects (attack swings etc.).
            if (_pendingSlashes.Count > 0)
            {
                Send(MirrorKind.Slash, MirrorCodec.WriteSlashBatch(_pendingSlashes));
                _pendingSlashes.Clear();
            }

            // Low-rate authoritative clock correction (single scalar - not position polling)
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
            if (_shortPollTimer >= 0.1f) { _shortPollTimer = 0f; SendAnimPoll(); }   // forced-animation diffs
            _longPollTimer += dt;
            if (_longPollTimer >= 0.5f) { _longPollTimer = 0f; SendLongPoll(); }
            _extraLongPollTimer += dt;
            if (_extraLongPollTimer >= 1.0f) { _extraLongPollTimer = 0f; SendActorResync(); }   // age/stats are continuous; structural is event-based
        }

        // short_poll (100ms): action-visual diff scan. Followers suppress AI, so the king
        // streams what each actor is visibly DOING:
        //  • task id  - drives the hand-tool overlay (pickaxe/axe/hammer/basket…), which IS
        //    the visible mine/chop/build/farm animation, plus the unit-window task text.
        //  • forced animation id - rare (e.g. "burrow"), but it's the one true sprite override.
        //  • jump - timer_jump_animation edge (combat lunge / task bounce); one-shot value,
        //    decays naturally on the follower (updateParallelChecks runs in the passive sim).
        // Only CHANGES are sent, so steady state costs nothing.
        private struct AnimState { public string Anim; public string Task; public bool Jumping; }
        private readonly Dictionary<long, AnimState> _lastAnim = new Dictionary<long, AnimState>();
        private readonly HashSet<long> _animSeen = new HashSet<long>();
        private readonly List<AnimMsg> _animOut = new List<AnimMsg>(64);
        private readonly List<long> _animPrune = new List<long>();
        private void SendAnimPoll()
        {
            if (_actorAiField == null) return;
            try
            {
                var units = World.world?.units?.getSimpleList();
                if (units == null) return;
                _animOut.Clear(); _animSeen.Clear();
                for (int i = 0; i < units.Count; i++)
                {
                    var a = units[i];
                    if (a == null || !a.isAlive()) continue;
                    string anim = "", task = "";
                    float jump = 0f;
                    try
                    {
                        var aiSys = _actorAiField.GetValue(a);
                        if (aiSys != null)
                        {
                            var act = _aiActionField?.GetValue(aiSys);
                            if (act != null && _forceAnimField != null && (bool)_forceAnimField.GetValue(act))
                                anim = _forceAnimIdField.GetValue(act) as string ?? "";
                            if (_aiTaskField?.GetValue(aiSys) is BehaviourElementAI t) task = t.id ?? "";
                        }
                        if (_jumpTimerField != null) jump = (float)_jumpTimerField.GetValue(a);
                    }
                    catch { }
                    bool jumping = jump > 0f;
                    _animSeen.Add(a.id);
                    _lastAnim.TryGetValue(a.id, out var prev);
                    bool jumpEdge = jumping && !prev.Jumping;   // only the rising edge is sent
                    if (anim != (prev.Anim ?? "") || task != (prev.Task ?? "") || jumping != prev.Jumping)
                    {
                        _lastAnim[a.id] = new AnimState { Anim = anim, Task = task, Jumping = jumping };
                        _animOut.Add(new AnimMsg { Id = a.id, AnimId = anim, TaskId = task, Jump = jumpEdge ? jump : 0f });
                    }
                }
                // prune dead/removed actors so the diff dict doesn't grow forever
                if (_lastAnim.Count > _animSeen.Count)
                {
                    _animPrune.Clear();
                    foreach (var id in _lastAnim.Keys) if (!_animSeen.Contains(id)) _animPrune.Add(id);
                    for (int i = 0; i < _animPrune.Count; i++) _lastAnim.Remove(_animPrune[i]);
                }
                if (_animOut.Count > 0) Send(MirrorKind.Anim, MirrorCodec.WriteAnimBatch(_animOut));
            }
            catch (Exception e) { ModLog.Warn("SendAnimPoll failed: " + e.Message); }
        }

        // extra_long_poll (1000ms): force-override actor state on followers, so traits/
        // statuses/vitals that drifted (or whose events were missed) get corrected and
        // derived stats (max health, speed, damage) recompute correctly.
        //
        // BUDGETED round-robin: serializing EVERY actor to JSON every second saturated
        // the channel on army-heavy worlds (hundreds of units -> megabytes/sec), and
        // followers fell minutes behind, which read as permanent desync. Now at most
        // ResyncBatchSize actors go out per tick, rotating through the population - the
        // safety net stays complete (full coverage every units/batch seconds) but the
        // bandwidth is capped and independent of world size.
        private int _resyncCursor;
        private const int ResyncBatchSize = 60;
        private void SendActorResync()
        {
            try
            {
                var units = World.world?.units?.getSimpleList();
                if (units == null || units.Count == 0) return;
                int batch = Math.Min(ResyncBatchSize, units.Count);
                var list = new List<ActorStateMsg>(batch);
                for (int n = 0; n < batch; n++)
                {
                    var a = units[(_resyncCursor + n) % units.Count];
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
                _resyncCursor = (_resyncCursor + batch) % units.Count;
                if (list.Count > 0) Send(MirrorKind.ActorResync, MirrorCodec.WriteActorResync(list));
            }
            catch (Exception e) { ModLog.Warn("SendActorResync failed: " + e.Message); }
        }

        // long_poll (500ms): age - derived from world-time + age_overgrowth on the king; we
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
                SendWorldAge();   // world-age state (era_manager isn't advanced on followers)
            }
            catch (Exception e) { ModLog.Warn("SendLongPoll failed: " + e.Message); }
        }

        // Stream the authoritative world-age state (MapStats). Followers don't advance the
        // era_manager (passive sim), so the King pushes age id/slot/progress/slots each long_poll.
        private void SendWorldAge()
        {
            try
            {
                var ms = _mapStatsField?.GetValue(World.world) as MapStats;
                if (ms == null) return;
                Send(MirrorKind.WorldAge, MirrorCodec.Write(new WorldAgeMsg
                {
                    AgeId = ms.world_age_id, SlotIndex = ms.world_age_slot_index, Progress = ms.current_age_progress,
                    Duration = ms.current_world_ages_duration, SpeedMult = ms.world_ages_speed_multiplier,
                    Paused = ms.is_world_ages_paused, Slots = ms.world_ages_slots
                }));
            }
            catch (Exception e) { ModLog.Warn("SendWorldAge failed: " + e.Message); }
        }

        private int _ageLog;
        private void ApplyWorldAge(WorldAgeMsg m)
        {
            try
            {
                var ms = _mapStatsField?.GetValue(World.world) as MapStats;
                if (ms == null) return;
                ms.world_age_id = m.AgeId;
                ms.world_age_slot_index = m.SlotIndex;
                ms.current_age_progress = m.Progress;
                ms.current_world_ages_duration = m.Duration;
                ms.world_ages_speed_multiplier = m.SpeedMult;
                ms.is_world_ages_paused = m.Paused;
                if (m.Slots != null && m.Slots.Length > 0) ms.world_ages_slots = m.Slots;
                // Refresh the live era_manager so getCurrentAge()/effects reflect the synced
                // world_age_id (loadAge() reads map_stats.world_age_id we just set).
                var era = _eraField?.GetValue(World.world);
                if (era != null) _eraLoadAge?.Invoke(era, null);
                if (_ageLog < 6) { _ageLog++; ModLog.Info($"[FOLLOWER] world age <- '{m.AgeId}' slot={m.SlotIndex} prog={m.Progress:F2}"); }
            }
            catch (Exception e) { ModLog.Warn("ApplyWorldAge failed: " + e.Message); }
        }

        // Re-broadcast every live object of a manager (cities/kingdoms) via OnKingObject, which
        // save()s + serializes current state. Followers apply as an in-place update.
        // NOTE: CityManager/KingdomManager : CoreSystemManager - they have NO getSimpleList
        // (that's on the ActorManager/BuildingManager hierarchy). They expose a public `list`
        // field (List<TObject>) and are IEnumerable. Enumerate via `list`.
        private void RefreshStructural(object mgr)
        {
            if (mgr == null) return;
            try
            {
                // Enumerate the manager itself (CoreSystemManager.GetEnumerator -> _hashset), NOT
                // its `list` field - `list` is rebuilt only by checkLists() in the game sim.
                var listObj = mgr as System.Collections.IEnumerable;
                if (listObj == null) { var lf = AccessTools.Field(mgr.GetType(), "list"); listObj = lf?.GetValue(mgr) as System.Collections.IEnumerable; }
                if (listObj == null) { ModLog.Warn($"RefreshStructural: not enumerable {mgr.GetType().Name}"); return; }
                var snapshot = new List<object>();
                foreach (var o in listObj) if (o != null) snapshot.Add(o);
                foreach (var o in snapshot) OnKingObject(o);
            }
            catch (Exception e) { ModLog.Warn($"RefreshStructural({mgr.GetType().Name}) failed: " + e.Message); }
        }

        // ── Snapshots (chunked, channel 1) ──────────────────────────────────
        /// <summary>King: broadcast a full world snapshot to every follower (full resync).</summary>
        public void ForceSnapshotToAll()
        {
            if (!Active || !IsKing) return;
            StartSnapshotTransfer("");
        }

        /// <summary>King: serialize the world once and queue it as a chunked transfer.
        /// Chunks are pumped a few per frame on their own channel, so even a huge world
        /// can't head-of-line-block the live mirror stream.</summary>
        private void StartSnapshotTransfer(string targetId4)
        {
            if (!Active || !IsKing) return;
            var data = WorldSync.Save();
            if (data.Length == 0) return;
            // A newer transfer to the same audience supersedes an unfinished one.
            _outgoingSnapshots.RemoveAll(t => t.TargetId4 == targetId4);
            int count = (data.Length + NetProtocol.SnapshotChunkSize - 1) / NetProtocol.SnapshotChunkSize;
            _outgoingSnapshots.Add(new OutgoingSnapshot
            {
                TargetId4 = targetId4, TransferId = _nextTransferId++, Epoch = _epoch,
                Data = data, Count = count
            });
            ModLog.Info($"Snapshot transfer queued: {data.Length} bytes / {count} chunks -> {(targetId4.Length == 0 ? "ALL" : targetId4)} (epoch {_epoch}).");
        }

        /// <summary>King: submit a couple of chunks per frame (LiteNetLib paces the wire;
        /// this bounds queue growth and lets a superseding transfer cancel cleanly).</summary>
        private void PumpSnapshotTransfers()
        {
            for (int t = _outgoingSnapshots.Count - 1; t >= 0; t--)
            {
                var tr = _outgoingSnapshots[t];
                for (int n = 0; n < 2 && tr.NextIndex < tr.Count; n++)
                {
                    int off = tr.NextIndex * NetProtocol.SnapshotChunkSize;
                    int len = Math.Min(NetProtocol.SnapshotChunkSize, tr.Data.Length - off);
                    var piece = new byte[len];
                    Buffer.BlockCopy(tr.Data, off, piece, 0, len);
                    NetClient.Instance.SendSnapshotChunk(new SnapshotChunk
                    {
                        TargetId4 = tr.TargetId4, TransferId = tr.TransferId, Epoch = tr.Epoch,
                        Index = tr.NextIndex, Count = tr.Count, TotalBytes = tr.Data.Length, Data = piece
                    });
                    tr.NextIndex++;
                }
                if (tr.NextIndex >= tr.Count)
                {
                    ModLog.Info($"Snapshot transfer submitted: {tr.Count} chunks -> {(tr.TargetId4.Length == 0 ? "ALL" : tr.TargetId4)}.");
                    _outgoingSnapshots.RemoveAt(t);
                }
            }
        }

        private void OnSnapshotRequested(SnapshotRequest req)
        {
            if (!IsKing) return;
            StartSnapshotTransfer(req.ForId4);
            // Make the joiner adopt our current game speed/pause immediately (so their
            // passive sim replays movement at the right rate). Re-broadcast next Pump.
            _lastSpeedId = "\0"; _lastPaused = !SafePaused();
        }

        private void OnSnapshotChunk(SnapshotChunk c)
        {
            if (IsKing) return;
            if (c.TransferId != _rxTransferId)
            {
                // New (or superseding) transfer: start a fresh assembly buffer.
                if (c.TotalBytes <= 0 || c.Count <= 0) return;
                _rxTransferId = c.TransferId;
                _rxSnapshot = new byte[c.TotalBytes];
                _rxChunksGot = 0; _rxChunkCount = c.Count; _rxEpoch = c.Epoch;
            }
            if (_rxSnapshot == null) return;
            long off = (long)c.Index * NetProtocol.SnapshotChunkSize;
            if (c.Data == null || off + c.Data.Length > _rxSnapshot.Length) return;   // corrupt chunk
            Buffer.BlockCopy(c.Data, 0, _rxSnapshot, (int)off, c.Data.Length);
            _rxChunksGot++;
            // Progress bar only for LARGE transfers (big map on join / world change);
            // small resyncs finish in well under a second and surface no UI at all.
            bool big = _rxChunkCount > ProgressBarMinChunks;
            if (big)
            {
                float mb = _rxSnapshot.Length / 1048576f;
                UI.PingHud.SetTransfer((float)_rxChunksGot / _rxChunkCount,
                    _rxChunksGot < _rxChunkCount
                        ? $"Receiving world  {(int)(100f * _rxChunksGot / _rxChunkCount)}%  ({mb:F1} MB)"
                        : "Loading world...");
            }
            if (_rxChunksGot < _rxChunkCount) return;

            var data = _rxSnapshot;
            _rxSnapshot = null; _rxTransferId = -1;
            WorldSync.Load(data);
            _kingToLocal.Clear();   // post-load actors carry king ids; new spawns repopulate the map
            _appliedEpoch = _rxEpoch;
            if (big) UI.PingHud.ClearTransfer();
            ModLog.Info($"World snapshot applied ({data.Length} bytes, epoch {_appliedEpoch}).");
        }

        /// <summary>Hook (MapBox.clearWorld postfix): the local player loaded/generated a
        /// different world. Only meaningful on the king - after the load settles, it bumps
        /// the epoch and re-broadcasts the world so followers actually change worlds too.</summary>
        public void OnWorldCleared()
        {
            if (!Active || !IsKing) return;
            _worldChangePending = true;
            _worldChangeSettle = 0f;
        }

        private void PumpWorldChange(float dt)
        {
            if (!_worldChangePending) return;
            bool loading = false;
            try { loading = SmoothLoader.isLoading(); } catch { }
            if (loading) { _worldChangeSettle = 0f; return; }
            // Wait for a short stable window so we don't snapshot a half-built world.
            _worldChangeSettle += dt;
            if (_worldChangeSettle < 1.0f) return;
            _worldChangePending = false;
            _epoch++; if (_epoch == 0) _epoch = 1;   // byte wrap; 0 means "no world"
            Reset();                                  // stale id maps refer to the old world
            ModLog.Info($"King changed world -> epoch {_epoch}; broadcasting fresh snapshot.");
            ForceSnapshotToAll();
        }

        // ── Helpers ─────────────────────────────────────────────────────────
        private static bool SafePaused() { try { return Config.paused; } catch { return false; } }
        private static bool SafeWindowActive() { try { return ScrollWindow.isWindowActive(); } catch { return false; } }

        /// <summary>True on a follower while the God King has the game genuinely paused (synced).
        /// Drives the on-screen "God King paused" indicator. Not set by the King's menu/window pauses.</summary>
        public static bool KingPaused { get; private set; }

        private void Send(MirrorKind kind, byte[] payload, DeliveryMethod method = DeliveryMethod.ReliableOrdered) =>
            NetClient.Instance.SendMirror(kind, payload, CurrentEpoch, method);
    }
}
