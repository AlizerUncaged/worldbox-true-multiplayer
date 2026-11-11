using System;
using System.Linq;
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
        private class ObjReg { public byte Kind; public Type ObjType; public Type DataType; public Func<object> Mgr; public FieldInfo DataField; public MethodInfo PrepareForSave; public MethodInfo GetData; public MethodInfo Save; public MethodInfo ReloadInstance; public MethodInfo LoadData; public MethodInfo IdGetter; }

        /// <summary>Per-kind structural counters (sent on the king / applied + failed on followers).
        /// Read by the dev bridge to prove every meta system actually crosses the wire.</summary>
        public static readonly int[] ObjSentByKind = new int[32], ObjAppliedByKind = new int[32], ObjFailedByKind = new int[32];
        private static readonly List<ObjReg> _objReg = new List<ObjReg>();
        internal bool ApplyingStatus => _applyingStatus;
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
            _awaitTransferId = -1; _postSnapshotBuffer.Clear();
            _sentObjHash.Clear(); _sentOvergrowth.Clear(); _sentStatsHash.Clear(); _sentActorJson.Clear(); _lastFullAt.Clear(); _sentVitals.Clear(); _dirtyObjects.Clear(); _freshActors.Clear(); _pendingSpawns.Clear(); _lastWorldAgeHash = 0;
            _snapshotLoading = false; _loadingTransferId = -1; _deferredSnapshot = null;
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

        // Spawns are sent one frame late: the creation hooks fire before the game finishes
        // setting the actor up (births get their subspecies/city/traits right after), and a
        // record sent that early could not even be loaded on followers (sub=-1, no asset).
        private readonly List<Actor> _pendingSpawns = new List<Actor>();
        private int _pendingSpawnFrame;

        public void OnKingSpawned(Actor actor, string assetId)
        {
            if (!Active || !IsKing || ApplyingSpawn || actor == null) return;
            _pendingSpawns.Add(actor);
            _pendingSpawnFrame = UnityEngine.Time.frameCount;
        }

        private void FlushPendingSpawns()
        {
            if (_pendingSpawns.Count == 0 || UnityEngine.Time.frameCount == _pendingSpawnFrame) return;
            var batch = _pendingSpawns.ToArray();
            _pendingSpawns.Clear();
            foreach (var a in batch)
                if (a != null && a.isAlive()) BroadcastSpawn(a);   // died in its first frame: nothing to send
        }

        /// <summary>Send one actor's full creation record (also used to answer ActorRequest).</summary>
        private void BroadcastSpawn(Actor actor)
        {
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
                _freshActors.Add(actor); _freshFrame = UnityEngine.Time.frameCount;
                if (_sentActorJson.ContainsKey(actor.id)) { _sentActorJson.Remove(actor.id); _lastFullAt.Remove(actor.id); }
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
            void Reg(byte kind, Type obj, Type data, Func<object> mgr)
            {
                if (kind == ActorRemoveKind || _objReg.Exists(x => x.Kind == kind))
                    throw new InvalidOperationException($"structural kind {kind} for {obj.Name} collides with another kind");
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
                    // In-place update for an object the follower already has (MetaObject.loadData:
                    // re-reads name/colors/stats and, for trait holders, the trait list).
                    LoadData = AccessTools.Method(obj, "loadData", new[] { data }),
                    IdGetter = AccessTools.PropertyGetter(data, "id") });
            }
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
            // Clans, families and armies were never mirrored: followers showed no clan/family/
            // army anywhere and actors' links to them stayed empty. Kinds start at 20: 10 is
            // ActorRemoveKind on the ObjectRemove channel (a clan at 10 deleted actors!).
            void RegByName(byte kind, Type obj, string dataName, Func<object> mgr)
            {
                var dt = AccessTools.TypeByName(dataName);
                if (dt != null) Reg(kind, obj, dt, mgr); else ModLog.Warn("[sync] no data type " + dataName);
            }
            RegByName(20, typeof(Clan), "ClanData", () => World.world.clans);
            RegByName(21, typeof(Family), "FamilyData", () => World.world.families);
            RegByName(22, typeof(Army), "ArmyData", () => World.world.armies);
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
        // Last-sent fingerprint per structural object (kind, id). Change-only sends compare
        // against it; it is wiped every FullRefreshSeconds so a missed apply still heals.
        private readonly Dictionary<long, int> _sentObjHash = new Dictionary<long, int>();
        private static long ObjKey(byte kind, long id) => ((long)kind << 56) ^ id;

        // Objects touched by a hooked event this frame (zones, culture/language/religion
        // setters, renames). Flushed once per frame, so a city that gains ten zones in one
        // frame goes out once.
        private readonly HashSet<object> _dirtyObjects = new HashSet<object>();

        /// <summary>Event hook entry: this structural object changed; send it this frame.</summary>
        public void MarkDirty(object obj)
        {
            if (!Active || !IsKing || _applyingObject || obj == null) return;
            _dirtyObjects.Add(obj);
        }

        private void FlushDirtyObjects()
        {
            if (_dirtyObjects.Count == 0) return;
            var batch = new List<object>(_dirtyObjects);
            _dirtyObjects.Clear();
            foreach (var o in batch) OnKingObject(o, onlyIfChanged: true);
        }

        public void OnKingObject(object obj) => OnKingObject(obj, onlyIfChanged: false);

        public void OnKingObject(object obj, bool onlyIfChanged)
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
                        long okey = ObjKey(r.Kind, IdOf(obj));
                        int ohash = json.GetHashCode();
                        if (onlyIfChanged && _sentObjHash.TryGetValue(okey, out var prevHash) && prevHash == ohash) return;
                        _sentObjHash[okey] = ohash;
                        Send(MirrorKind.Object, MirrorCodec.Write(new ObjectMsg { Kind = r.Kind, Json = json }));
                        DbgObjSent++;
                        if (r.Kind < ObjSentByKind.Length) ObjSentByKind[r.Kind]++;
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
                      : target is City ? MemberKind.City
                      : target is Clan ? MemberKind.Clan
                      : target is Family ? MemberKind.Family
                      : target is Army ? MemberKind.Army : (byte)0;
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
        // ── Snapshot cut-over (follower) ────────────────────────────────────
        // Set by a SnapshotMark meant for us: the live messages that follow it describe changes
        // made after that snapshot was taken, so hold them until the snapshot has loaded.
        private int _awaitTransferId = -1;
        private readonly List<MirrorMessage> _postSnapshotBuffer = new List<MirrorMessage>();
        private const int MaxPostSnapshotBuffer = 50000;
        private bool _replaying;
        // SaveManager.loadData runs on the game's SmoothLoader over many frames. Until that
        // queue drains the world is half-built, so live messages wait (and the epoch only
        // flips to the new world once it is really there).
        private bool _snapshotLoading;
        private byte _loadingEpoch;
        private int _loadingTransferId = -1;

        private byte[] _deferredSnapshot;
        private float _noWorldTimer;
        private float _loadStartedAt;
        private const float SnapshotLoadTimeout = 120f;

        /// <summary>The local game can take a world load right now: it has finished booting
        /// (a world was built at least once) and its loader is idle.</summary>
        private static bool LocalWorldReady
        {
            get
            {
                bool loading = true;
                try { loading = SmoothLoader.isLoading(); } catch { loading = false; }
                bool booted = true;
                try { booted = Config.game_loaded; } catch { }
                return booted && GameHooks.WorldBuiltOnce && !loading;
            }
        }

        private void PumpSnapshotLoad()
        {
            if (!_snapshotLoading) return;
            if (UnityEngine.Time.unscaledTime - _loadStartedAt > SnapshotLoadTimeout)
            {
                // Never leave a follower stuck "loading" (it would drop every live message and
                // the owner would drop its intents): start over with a fresh snapshot.
                ModLog.Warn("[sync] world load did not finish; requesting a fresh snapshot");
                _snapshotLoading = false; _deferredSnapshot = null; _awaitTransferId = -1; _postSnapshotBuffer.Clear();
                NetClient.Instance?.RequestResync();
                return;
            }
            if (_deferredSnapshot != null)
            {
                if (!LocalWorldReady) return;   // still booting: try again next frame
                var data = _deferredSnapshot;
                _deferredSnapshot = null;
                ModLog.Info($"Loading world snapshot ({data.Length} bytes, epoch {_loadingEpoch})...");
                WorldSync.Load(data);
                return;   // the load runs over the next frames
            }
            bool stillLoading = true;
            try { stillLoading = SmoothLoader.isLoading(); } catch { stillLoading = false; }
            if (stillLoading) return;
            _snapshotLoading = false;
            _appliedEpoch = _loadingEpoch;
            _kingToLocal.Clear();   // post-load actors carry king ids; new spawns repopulate the map
            UI.PingHud.ClearTransfer();
            ModLog.Info($"World snapshot loaded (epoch {_appliedEpoch}).");
            ReplayPostSnapshot(_loadingTransferId);
        }

        private void OnSnapshotMark(MirrorMessage msg)
        {
            if (IsKing) return;
            try
            {
                var r = new LiteNetLib.Utils.NetDataReader(msg.Payload);
                int transferId = r.GetInt();
                string target = r.GetString();
                if (target.Length > 0 && target != NetClient.Instance.Id4) return;   // someone else's join
                // A newer snapshot already contains everything buffered for an older one.
                _awaitTransferId = transferId;
                _postSnapshotBuffer.Clear();
                ModLog.Info($"[sync] snapshot mark {transferId}: buffering live changes until it loads");
            }
            catch (Exception e) { ModLog.Warn("SnapshotMark: " + e.Message); }
        }

        private void ReplayPostSnapshot(int transferId)
        {
            // A newer snapshot is already on its way: keep holding for that one.
            if (_awaitTransferId >= 0 && _awaitTransferId != transferId) return;
            _awaitTransferId = -1;
            var batch = _postSnapshotBuffer.ToArray();
            _postSnapshotBuffer.Clear();
            var kinds = new Dictionary<MirrorKind, int>();
            foreach (var m in batch) { var k = (MirrorKind)m.Kind; kinds[k] = kinds.TryGetValue(k, out var n) ? n + 1 : 1; }
            var parts = new List<string>();
            foreach (var kv in kinds) parts.Add(kv.Key + "=" + kv.Value);
            ModLog.Info($"[sync] snapshot {transferId} loaded: replaying {batch.Length} live changes made during the transfer ({string.Join(", ", parts)})");
            _replaying = true;
            try { foreach (var m in batch) OnMirror(m); }
            finally { _replaying = false; }
        }

        private void OnMirror(MirrorMessage msg)
        {
            if ((MirrorKind)msg.Kind == MirrorKind.SnapshotMark) { OnSnapshotMark(msg); return; }
            if ((_awaitTransferId >= 0 || _snapshotLoading) && !_replaying && !IsKing)
            {
                // Copy: LiteNetLib's packet processor hands every MirrorMessage through ONE reused
                // instance, so storing `msg` itself would leave N references to the last packet.
                if (_postSnapshotBuffer.Count < MaxPostSnapshotBuffer)
                    _postSnapshotBuffer.Add(new MirrorMessage { Kind = msg.Kind, SenderId4 = msg.SenderId4, Epoch = msg.Epoch, Payload = msg.Payload });
                else if (_postSnapshotBuffer.Count == MaxPostSnapshotBuffer)
                {
                    // Pathological: a transfer that never finishes. Stop holding; ask for a fresh one.
                    ModLog.Warn("[sync] post-snapshot buffer full; requesting a resync");
                    _awaitTransferId = -1; _postSnapshotBuffer.Clear();
                    NetClient.Instance?.RequestResync();
                }
                return;
            }
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
                case MirrorKind.ActorRoster:
                    if (IsFollower) ApplyActorRoster(msg.Payload);
                    break;
                case MirrorKind.ActorRequest:
                    if (IsKing) AnswerActorRequest(msg.Payload);
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
                    // Third-party channel (WbmpApi). Owner-authoritative routing: the server
                    // delivers a follower's message to the owner ONLY (an intent: the plugin
                    // validates it and Sends the result), and the owner's messages to every
                    // follower. No automatic relay: that used to re-broadcast raw follower
                    // requests to everyone flagged as if the owner had sent them.
                    var pm = MirrorCodec.ReadPlugin(msg.Payload);
                    Api.WbmpApi.DispatchInternal(pm, !IsKing);   // a non-owner only ever receives from the owner
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
                // Already here (a roster re-send, or a duplicate): keep it. Its state is kept
                // current by the record stream; reloading would reset it.
                if (World.world.units.get(actorData.id) != null) return;
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

        // Status adds for units not spawned here yet. The king sends a new unit's spawn one
        // frame late (so its record is settled) but statuses it gets at birth (migrants'
        // handsome_migrant...) right away, so they arrived first and were dropped.
        private struct PendingStatus { public StatusMsg Msg; public float Until; }
        private readonly List<PendingStatus> _pendingStatuses = new List<PendingStatus>();

        private void RetryPendingStatuses()
        {
            if (_pendingStatuses.Count == 0) return;
            float now = UnityEngine.Time.unscaledTime;
            for (int i = _pendingStatuses.Count - 1; i >= 0; i--)
            {
                var p = _pendingStatuses[i];
                if (Resolve(p.Msg.Id) != null) { _pendingStatuses.RemoveAt(i); ApplyStatus(p.Msg); }
                else if (now > p.Until) _pendingStatuses.RemoveAt(i);
            }
        }

        private void ApplyStatus(StatusMsg m)
        {
            var a = Resolve(m.Id);
            if (a == null)
            {
                if (m.Add) _pendingStatuses.Add(new PendingStatus { Msg = m, Until = UnityEngine.Time.unscaledTime + 5f });
                else _pendingStatuses.RemoveAll(p => p.Msg.Id == m.Id && p.Msg.StatusId == m.StatusId);
                return;
            }
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
                            // Adopt the king's data in place. Cities/kingdoms have dedicated updaters;
                            // cultures/religions/languages/clans/families/armies use loadData. Buildings
                            // are never re-sent, so they keep the plain "already have it" skip.
                            var update = r.ReloadInstance ?? (r.Kind != 9 ? r.LoadData : null);
                            if (update != null) { update.Invoke(existing, new[] { data }); DbgObjRecv++; }
                            LinkObjectRefs(r.Kind, existing);
                            if (r.Kind < ObjAppliedByKind.Length) ObjAppliedByKind[r.Kind]++;
                            return;   // already have it - never re-loadObject (throws on duplicate id)
                        }
                        var load = AccessTools.Method(mgr.GetType(), "loadObject", new[] { r.DataType });
                        var created = load?.Invoke(mgr, new[] { data });
                        LinkObjectRefs(r.Kind, created);
                        DbgObjRecv++;
                        if (r.Kind < ObjAppliedByKind.Length) ObjAppliedByKind[r.Kind]++;
                        RetryPendingMembers();   // memberships that arrived before this object
                        return;
                    }
            }
            catch (Exception e)
            {
                if (m.Kind < ObjFailedByKind.Length) ObjFailedByKind[m.Kind]++;
                if (_applyObjFail < 20)
                {
                    _applyObjFail++;
                    var ie = e.InnerException ?? e;
                    string asset = ""; try { var mm = System.Text.RegularExpressions.Regex.Match(m.Json ?? "", @"""asset_id""\s*:\s*""([^""]*)"""); if (mm.Success) asset = " asset=" + mm.Groups[1].Value; } catch { }
                    ModLog.Warn($"ApplyObject(kind={m.Kind}{asset}) failed: {ie.GetType().Name}: {ie.Message}" + (_applyObjFail <= 3 ? "\n" + ie.StackTrace : ""));
                }
            }
            finally { _applyingObject = false; }
        }
        private int _applyObjFail;

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
                    // setData swaps the data but leaves the live refs: re-link them from the ids.
                    var kd2 = _kingdomDataF?.GetValue(k) as KingdomData;
                    if (kd2 != null) LinkMeta(k.culture, kd2.id_culture, World.world.cultures, c => k.setCulture(c),
                                              k.language, kd2.id_language, World.world.languages, l => k.setLanguage(l),
                                              k.religion, kd2.id_religion, World.world.religions, r => k.setReligion(r));
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
                    // loadCity re-links these too, but only while the target already exists on
                    // this follower - so a city that arrived before its culture stayed blank.
                    var cd2 = _cityDataF?.GetValue(c) as CityData;
                    if (cd2 != null) LinkMeta(c.culture, cd2.id_culture, World.world.cultures, x => c.setCulture(x),
                                              c.language, cd2.id_language, World.world.languages, x => c.setLanguage(x),
                                              c.religion, cd2.id_religion, World.world.religions, x => c.setReligion(x));
                }
            }
            catch (Exception e) { if (_linkErr < 4) { _linkErr++; ModLog.Warn($"LinkObjectRefs(kind={kind}): " + (e.InnerException ?? e).Message); } }
        }

        /// <summary>Point an owner's live culture/language/religion at the objects its data names.</summary>
        private static void LinkMeta(Culture curC, long wantC, CultureManager cultures, Action<Culture> setC,
                                     Language curL, long wantL, LanguageManager languages, Action<Language> setL,
                                     Religion curR, long wantR, ReligionManager religions, Action<Religion> setR)
        {
            if (wantC > 0 && (curC == null || curC.id != wantC)) { var x = cultures.get(wantC); if (x != null) setC(x); }
            if (wantL > 0 && (curL == null || curL.id != wantL)) { var x = languages.get(wantL); if (x != null) setL(x); }
            if (wantR > 0 && (curR == null || curR.id != wantR)) { var x = religions.get(wantR); if (x != null) setR(x); }
        }

        // ── Follower self-repair ────────────────────────────────────────────
        // Membership messages can arrive before the culture/clan/... they point at (create
        // events race, or the object only shows up on the next periodic re-send). Those used
        // to be dropped for good. Keep them briefly and retry whenever a new object lands.
        private struct PendingMember { public MemberMsg Msg; public float Until; }
        private readonly List<PendingMember> _pendingMembers = new List<PendingMember>();

        private void RetryPendingMembers()
        {
            if (_pendingMembers.Count == 0) return;
            var batch = _pendingMembers.ToArray();
            _pendingMembers.Clear();
            float now = UnityEngine.Time.unscaledTime;
            foreach (var p in batch)
                if (p.Until > now && !TryApplyMember(p.Msg)) _pendingMembers.Add(p);
        }

        /// <summary>Every second on followers: every actor's live culture/religion/language/clan/
        /// family/army/kingdom must match the ids in its own data (spawn snapshots and membership
        /// messages write those ids even when the target wasn't there yet).</summary>
        private float _relinkTimer;
        // ── Dangling links ─────────────────────────────────────────────────
        // An actor can keep pointing at a meta object (family, clan, army...) that was removed:
        // the object survives but its data is wiped, and the game's save code (getID) then
        // throws - which made a promoted follower unable to save, so nobody got a world.
        private static readonly string[] _actorLinkFields = { "family", "clan", "army", "culture", "religion", "language", "city", "kingdom" };
        private static FieldInfo[] _actorLinkInfos;
        private static readonly Dictionary<Type, FieldInfo> _objDataField = new Dictionary<Type, FieldInfo>();

        private static bool IsDead(object meta)
        {
            if (meta == null) return false;
            var t = meta.GetType();
            if (!_objDataField.TryGetValue(t, out var f)) _objDataField[t] = f = AccessTools.Field(t, "data");
            return f != null && f.GetValue(meta) == null;
        }

        /// <summary>Clear every actor link to a removed meta object. Returns how many were cleared.</summary>
        internal int ClearDanglingLinks()
        {
            var units = World.world?.units?.getSimpleList();
            if (units == null) return 0;
            if (_actorLinkInfos == null)
            {
                var list = new List<FieldInfo>();
                foreach (var n in _actorLinkFields) { var fi = AccessTools.Field(typeof(Actor), n); if (fi != null) list.Add(fi); }
                _actorLinkInfos = list.ToArray();
            }
            int cleared = 0;
            foreach (var a in units)
            {
                if (a == null) continue;
                foreach (var fi in _actorLinkInfos)
                {
                    try
                    {
                        var v = fi.GetValue(a);
                        if (v != null && IsDead(v)) { fi.SetValue(a, null); cleared++; }
                    }
                    catch { }
                }
            }
            if (cleared > 0 && _danglingLog < 10) { _danglingLog++; ModLog.Info($"[sync] cleared {cleared} link(s) to removed objects"); }
            return cleared;
        }
        private int _danglingLog;

        private void RelinkActorsFromData(float dt)
        {
            _relinkTimer += dt;
            if (_relinkTimer < 1f) return;
            _relinkTimer = 0f;
            RetryPendingMembers();
            ClearDanglingLinks();
            var w = World.world; var units = w?.units?.getSimpleList();
            if (units == null) return;
            _applyingMember = true;
            try
            {
                foreach (var a in units)
                {
                    if (a == null || !a.isAlive()) continue;
                    var d = _actorDataField?.GetValue(a) as ActorData;
                    if (d == null) continue;
                    try
                    {
                        if (d.culture > 0 && (a.culture == null || a.culture.id != d.culture)) { var x = w.cultures.get(d.culture); if (x != null) a.setCulture(x); }
                        if (d.religion > 0 && (a.religion == null || a.religion.id != d.religion)) { var x = w.religions.get(d.religion); if (x != null) a.setReligion(x); }
                        if (d.language > 0 && (a.language == null || a.language.id != d.language)) { var x = w.languages.get(d.language); if (x != null) a.joinLanguage(x); }
                        if (d.clan > 0 && (a.clan == null || a.clan.id != d.clan)) { var x = w.clans.get(d.clan); if (x != null) a.setClan(x); }
                        if (d.family > 0 && (a.family == null || a.family.id != d.family)) { var x = w.families.get(d.family); if (x != null) a.setFamily(x); }
                        if (d.army > 0 && (a.army == null || a.army.id != d.army)) { var x = w.armies.get(d.army); if (x != null) a.setArmy(x); }
                        // Kingdom too: when a city changes kingdom (rebellion, conquest), a member
                        // the city switch missed kept its old kingdom here for good.
                        if (d.civ_kingdom_id > 0 && (a.kingdom == null || a.kingdom.id != d.civ_kingdom_id)) { var x = w.kingdoms.get(d.civ_kingdom_id); if (x != null && !x.wild) a.setKingdom(x); }
                    }
                    catch (Exception e) { if (_relinkErr < 5) { _relinkErr++; ModLog.Warn("relink actor: " + e.Message); } }
                }
            }
            finally { _applyingMember = false; }
        }
        private int _relinkErr;

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
                        if (obj is Building b)
                        {
                            // The game's own teardown, not a bare removeObject: that skipped the
                            // zone, tiles, the asset's building list, occupied_buildings and the
                            // job batches, leaving dead references the full simulation trips on.
                            DetachBuilding(b);
                            if (_buildingDestroy == null) _buildingDestroy = AccessTools.Method(typeof(BuildingManager), "destroyObject", new[] { typeof(Building) });
                            if (_buildingDestroy != null) { _buildingDestroy.Invoke(mgr, new object[] { b }); return; }
                        }
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
            if (!TryApplyMember(m))
                _pendingMembers.Add(new PendingMember { Msg = m, Until = UnityEngine.Time.unscaledTime + 30f });
        }

        /// <summary>False when the actor or target doesn't exist here (yet).</summary>
        private bool TryApplyMember(MemberMsg m)
        {
            var a = Resolve(m.ActorId); if (a == null) return false;
            var w = World.world;
            _applyingMember = true;
            try
            {
                switch (m.Kind)
                {
                    case MemberKind.Kingdom: { var x = w.kingdoms.get(m.TargetId); if (x == null) return false; a.joinKingdom(x); break; }
                    case MemberKind.Religion: { var x = w.religions.get(m.TargetId); if (x == null) return false; a.setReligion(x); break; }
                    case MemberKind.Culture: { var x = w.cultures.get(m.TargetId); if (x == null) return false; a.setCulture(x); break; }
                    case MemberKind.Language: { var x = w.languages.get(m.TargetId); if (x == null) return false; a.joinLanguage(x); break; }
                    case MemberKind.City: { var x = w.cities.get(m.TargetId); if (x == null) return false; a.joinCity(x); break; }
                    case MemberKind.Clan: { var x = w.clans.get(m.TargetId); if (x == null) return false; a.setClan(x); break; }
                    case MemberKind.Family: { var x = w.families.get(m.TargetId); if (x == null) return false; a.setFamily(x); break; }
                    case MemberKind.Army: { var x = w.armies.get(m.TargetId); if (x == null) return false; a.setArmy(x); break; }
                }
                return true;
            }
            catch (Exception e) { ModLog.Warn("ApplyMember failed: " + e.Message); return true; }
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
                    switch (kind)
                    {
                        case PollKind.Age:
                            var data = _actorDataField?.GetValue(a) as ActorData;
                            if (data != null)
                            {
                                int delta = m.V - a.getAge();      // king age - follower age
                                if (delta != 0) data.age_overgrowth += delta;
                            }
                            break;
                        case PollKind.Overgrowth:
                            var dog = _actorDataField?.GetValue(a) as ActorData;
                            if (dog != null) dog.age_overgrowth = m.V;
                            break;
                        case PollKind.Health: a.setHealth(m.V, false); break;
                        case PollKind.Nutrition: a.setNutrition(m.V, false); break;
                        case PollKind.Happiness: a.setHappiness(m.V, false); break;
                        case PollKind.Stamina: a.setStamina(m.V, false); break;
                        case PollKind.Mana: a.setMana(m.V, false); break;
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
        // Patch merge settings. Two traps in the game's own settings: (1) Json.NET's default
        // merge APPENDS to existing lists (doubled every trait on a saved_traits patch), and
        // (2) the game uses DefaultValueHandling.IgnoreAndPopulate, which resets every field
        // NOT in the patch to its [DefaultValue] (health 100, happiness 0, city -1...).
        private static JsonSerializerSettings _patchSettings;
        private static JsonSerializerSettings PatchSettings
        {
            get
            {
                if (_patchSettings != null) return _patchSettings;
                var src = JsonHelper.read_settings;
                _patchSettings = new JsonSerializerSettings
                {
                    ObjectCreationHandling = ObjectCreationHandling.Replace,
                    NullValueHandling = src.NullValueHandling,
                    DefaultValueHandling = DefaultValueHandling.Include,   // never touch fields absent from the patch
                    MissingMemberHandling = src.MissingMemberHandling,
                    ContractResolver = src.ContractResolver,
                    Converters = src.Converters,
                };
                return _patchSettings;
            }
        }

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
                    ActorData data;
                    bool traitsChanged = true;
                    if (m.Mode == ActorStateMode.Patch)
                    {
                        // Merge only the changed fields into this actor's own data.
                        data = _actorDataField?.GetValue(a) as ActorData;
                        if (data == null) continue;
                        traitsChanged = false;
                        if (!string.IsNullOrEmpty(m.Json))
                        {
                            JsonConvert.PopulateObject(m.Json, data, PatchSettings);
                            traitsChanged = m.Json.Contains("\"saved_traits\"");
                        }
                        if (!string.IsNullOrEmpty(m.StatsJson))
                            try { ApplyStats(a, JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, float>>(m.StatsJson)); } catch { }
                        if (string.IsNullOrEmpty(m.Json)) continue;   // stats-only
                    }
                    else
                    {
                        data = JsonConvert.DeserializeObject<ActorData>(m.Json, JsonHelper.read_settings);
                        if (data == null) continue;
                        a.setData(data);
                    }
                    if (traitsChanged) { try { TraitTools.loadTraits(a, data.saved_traits); } catch { } }   // force trait list to match king
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
                    if (m.Mode == ActorStateMode.Full)
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
        // ── Leaving the follower role ───────────────────────────────────────
        // Followers create every mirrored object under the king's id, so their own id
        // counters (map_stats.id_unit, id_building...) never move. Once this client runs the
        // full simulation again (promoted to owner, or left the room) the first new building
        // or unit reused a taken id: the add threw halfway, left a building without an asset
        // in its zone, and the city update then threw every tick - the world froze.
        private bool _wasFollower;

        /// <summary>Called every frame (Pump and the simulation hook): when this client just
        /// stopped being a follower, make its world safe to simulate on its own.</summary>
        internal void CheckLeftFollower()
        {
            bool follower = Active && IsFollower;
            if (_wasFollower && !follower)
            {
                try { RaiseIdCounters(); } catch (Exception e) { ModLog.Warn("RaiseIdCounters: " + e.Message); }
                try { PurgeStaleZoneBuildings(); } catch (Exception e) { ModLog.Warn("PurgeStaleZoneBuildings: " + e.Message); }
                try { ResetActorAi(); } catch (Exception e) { ModLog.Warn("ResetActorAi: " + e.Message); }
                try { PurgeDestroyQueue(World.world?.buildings); PurgeDestroyQueue(World.world?.units); } catch (Exception e) { ModLog.Warn("PurgeDestroyQueue: " + e.Message); }
                try { PurgeStaleSet(World.world?.buildings, "occupied_buildings"); } catch (Exception e) { ModLog.Warn("PurgeStaleSet: " + e.Message); }
                try { ClearVisualProjectiles(); } catch (Exception e) { ModLog.Warn("ClearVisualProjectiles: " + e.Message); }
                try { ClearDanglingLinks(); } catch { }
            }
            _wasFollower = follower;
        }

        private static MethodInfo _buildingClearTiles, _buildingDestroy;

        /// <summary>The steps of the game's removeBuildingFinal that removeObject skips: free the
        /// tiles and leave the zone. Without them the destroyed building stayed in its zone's
        /// set; followers' guards skip it, but once promoted the city update threw on it every
        /// tick and the world froze.</summary>
        private static void DetachBuilding(Building b)
        {
            try
            {
                if (_buildingClearTiles == null) _buildingClearTiles = AccessTools.Method(typeof(Building), "clearTiles");
                _buildingClearTiles?.Invoke(b, null);
            }
            catch { }
            try { b.current_tile?.zone?.removeBuildingMain(b); } catch { }
        }

        /// <summary>Drop zone entries whose building no longer exists (safety net for buildings
        /// removed before DetachBuilding existed, or by any path that skipped it).</summary>
        internal static int PurgeStaleZoneBuildings()
        {
            var w = World.world;
            var calc = w != null ? AccessTools.Field(typeof(MapBox), "zone_calculator")?.GetValue(w) as ZoneCalculator : null;
            var dict = w?.buildings != null ? AccessTools.Field(w.buildings.GetType(), "dict")?.GetValue(w.buildings) as System.Collections.IDictionary : null;
            if (calc == null || dict == null) return 0;
            int purged = 0;
            var stale = new List<Building>();
            foreach (var zone in calc.zones)
            {
                if (zone == null) continue;
                stale.Clear();
                foreach (var b in zone.buildings_all)
                {
                    bool ok;
                    try { ok = b != null && dict.Contains(b.id) && ReferenceEquals(dict[b.id], b); } catch { ok = false; }
                    if (!ok) stale.Add(b);
                }
                foreach (var b in stale) { zone.buildings_all.Remove(b); purged++; }
            }
            if (purged > 0) ModLog.Info($"[sync] removed {purged} destroyed building(s) still listed in zones");
            return purged;
        }

        /// <summary>Followers never run unit AI; the task/action state their units carry comes
        /// from the king's world and doesn't match locally. On the first full simulation tick
        /// every unit's AI threw (action index out of range), aborting the actor update, so the
        /// promoted world stood still. Start every unit's AI fresh.</summary>
        private static void ResetActorAi()
        {
            var units = World.world?.units?.getSimpleList();
            if (units == null) return;
            int reset = 0;
            foreach (var a in units)
            {
                if (a == null || !a.isAlive()) continue;
                try { a.cancelAllBeh(); reset++; } catch { }
            }
            ModLog.Info($"[sync] reset AI of {reset} unit(s) for the full simulation");
        }

        /// <summary>Drop objects from a manager's destroy queue that it no longer holds. Followers
        /// never run that queue (the passive sim skips it), and objects the king removed were
        /// disposed while still queued; the first full tick then destroyed them again and threw
        /// (null asset) every frame.</summary>
        private static void PurgeDestroyQueue(object mgr) => PurgeStaleSet(mgr, "_to_destroy_objects");

        /// <summary>Remove objects the manager no longer holds from one of its object sets.</summary>
        private static void PurgeStaleSet(object mgr, string field)
        {
            if (mgr == null) return;
            var t = mgr.GetType();
            var queue = AccessTools.Field(t, field)?.GetValue(mgr);
            var dict = AccessTools.Field(t, "dict")?.GetValue(mgr) as System.Collections.IDictionary;
            if (!(queue is System.Collections.IEnumerable items) || dict == null) return;
            var remove = queue.GetType().GetMethod("Remove");
            var stale = new List<object>();
            foreach (var o in items)
            {
                bool ok;
                try { ok = o is NanoObject n && dict.Contains(n.id) && ReferenceEquals(dict[n.id], o); } catch { ok = false; }
                if (!ok) stale.Add(o);
            }
            foreach (var o in stale) remove?.Invoke(queue, new[] { o });
            if (stale.Count > 0) ModLog.Info($"[sync] dropped {stale.Count} already removed object(s) from {t.Name}.{field}");
        }

        /// <summary>Projectiles in flight on a follower are visual copies (no owning kingdom);
        /// the full simulation's hit check threw on each until it landed. Drop them.</summary>
        private static void ClearVisualProjectiles()
        {
            var pm = World.world?.projectiles;
            if (pm == null) return;
            var all = new List<Projectile>();
            foreach (var p in pm) all.Add(p);
            var rem = AccessTools.Method(typeof(ProjectileManager), "removeObject", new[] { typeof(Projectile) });
            foreach (var p in all) { try { rem?.Invoke(pm, new object[] { p }); } catch { } }
        }

        private static List<FieldInfo> _managerFields;

        /// <summary>Move every map_stats id counter past the highest id its manager holds.</summary>
        internal static int RaiseIdCounters()
        {
            // map_stats is public only in the publicized reference assembly: read it by reflection.
            var w = World.world;
            var stats = w != null ? AccessTools.Field(typeof(MapBox), "map_stats")?.GetValue(w) : null;
            if (stats == null) return 0;
            if (_managerFields == null)
            {
                _managerFields = new List<FieldInfo>();
                foreach (var f in typeof(MapBox).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (typeof(BaseSystemManager).IsAssignableFrom(f.FieldType)) _managerFields.Add(f);
            }
            int raised = 0;
            var seen = new HashSet<object>();
            foreach (var f in _managerFields)
            {
                try
                {
                    var mgr = f.GetValue(w);
                    if (mgr == null || !seen.Add(mgr)) continue;
                    var type = AccessTools.Field(mgr.GetType(), "type_id")?.GetValue(mgr) as string;
                    if (string.IsNullOrEmpty(type)) continue;
                    var counter = AccessTools.Field(typeof(MapStats), type == "statuses" ? "id_status" : "id_" + type);
                    if (counter == null || counter.FieldType != typeof(long)) continue;
                    if (!(AccessTools.Field(mgr.GetType(), "dict")?.GetValue(mgr) is System.Collections.IDictionary dict)) continue;
                    long max = 0;
                    foreach (var k in dict.Keys) if (k is long id && id > max) max = id;
                    long cur = (long)counter.GetValue(stats);
                    if (cur <= max) { counter.SetValue(stats, max + 1); raised++; ModLog.Info($"[sync] id counter {type}: {cur} -> {max + 1}"); }
                }
                catch { }
            }
            return raised;
        }

        public void Pump(float dt)
        {
            CheckLeftFollower();
            if (Active && IsFollower)
            {
                // In a room but no world yet and nothing arriving: ask the owner again.
                if (_appliedEpoch == 0 && !_snapshotLoading && _rxSnapshot == null)
                {
                    _noWorldTimer += dt;
                    if (_noWorldTimer >= 15f)
                    {
                        _noWorldTimer = 0f;
                        ModLog.Warn("[sync] still no world from the owner; requesting a snapshot");
                        NetClient.Instance?.RequestResync();
                    }
                }
                else _noWorldTimer = 0f;
                try { PumpSnapshotLoad(); } catch (Exception e) { if (_pumpErr < 5) { _pumpErr++; ModLog.Warn("PumpSnapshotLoad: " + e.Message); } }
                if (_snapshotLoading) return;
                try { RetryPendingStatuses(); } catch (Exception e) { if (_pumpErr < 5) { _pumpErr++; ModLog.Warn("RetryPendingStatuses: " + e.Message); } }
                try { RelinkActorsFromData(dt); } catch (Exception e) { if (_pumpErr < 5) { _pumpErr++; ModLog.Warn("RelinkActorsFromData: " + e.Message); } }
                return;
            }
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

            // Structural objects touched by hooked events this frame.
            try { FlushPendingSpawns(); } catch (Exception e) { if (_pumpErr < 5) { _pumpErr++; ModLog.Warn("FlushPendingSpawns: " + e.Message); } }
            try { FlushFreshActors(); } catch (Exception e) { if (_pumpErr < 5) { _pumpErr++; ModLog.Warn("FlushFreshActors: " + e.Message); } }
            try { FlushDirtyObjects(); } catch (Exception e) { if (_pumpErr < 5) { _pumpErr++; ModLog.Warn("FlushDirtyObjects: " + e.Message); } }
            TickFullRefresh(dt);

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
            if (_extraLongPollTimer >= 1.0f) { _extraLongPollTimer = 0f; SendVitals(); SendActorResync(); }   // age/stats are continuous; structural is event-based
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
        private readonly Dictionary<long, int> _sentStatsHash = new Dictionary<long, int>();
        public static long ResyncFull, ResyncStatsOnly;   // full records vs patches (dev bridge /traffic)
        private readonly Dictionary<long, Newtonsoft.Json.Linq.JObject> _sentActorJson = new Dictionary<long, Newtonsoft.Json.Linq.JObject>();
        private static readonly string[] _volatileFields = { "x", "y", "health", "nutrition", "happiness", "stamina", "mana" };
        // Fields excluded from the full-record fingerprint: position (Move events) and vitals
        // (their own change-only scalar stream, SendVitals). What's left changes rarely.
        private static readonly System.Text.RegularExpressions.Regex _posFields =
            new System.Text.RegularExpressions.Regex(@"""(x|y|health|nutrition|happiness|stamina|mana)"":-?\d+(\.\d+)?,?", System.Text.RegularExpressions.RegexOptions.Compiled);

        // Last vitals sent per actor: health, nutrition, happiness, stamina, mana.
        private readonly Dictionary<long, int[]> _sentVitals = new Dictionary<long, int[]>();

        /// <summary>Every second: stream only the vitals that changed since last sent.
        /// Replaces shipping ~1 KB of full actor data whenever nutrition ticked down by one.</summary>
        private void SendVitals()
        {
            var units = World.world?.units?.getSimpleList();
            if (units == null) return;
            List<ScalarMsg> hp = null, nu = null, ha = null, st = null, ma = null;
            for (int i = 0; i < units.Count; i++)
            {
                var a = units[i];
                if (a == null || !a.isAlive()) continue;
                var d = _actorDataField?.GetValue(a) as ActorData;
                if (d == null) continue;
                if (!_sentVitals.TryGetValue(a.id, out var v)) { v = new[] { int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue }; _sentVitals[a.id] = v; }
                if (v[0] != d.health) { v[0] = d.health; (hp ?? (hp = new List<ScalarMsg>())).Add(new ScalarMsg { Id = a.id, V = d.health }); }
                if (v[1] != d.nutrition) { v[1] = d.nutrition; (nu ?? (nu = new List<ScalarMsg>())).Add(new ScalarMsg { Id = a.id, V = d.nutrition }); }
                if (v[2] != d.happiness) { v[2] = d.happiness; (ha ?? (ha = new List<ScalarMsg>())).Add(new ScalarMsg { Id = a.id, V = d.happiness }); }
                if (v[3] != d.stamina) { v[3] = d.stamina; (st ?? (st = new List<ScalarMsg>())).Add(new ScalarMsg { Id = a.id, V = d.stamina }); }
                if (v[4] != d.mana) { v[4] = d.mana; (ma ?? (ma = new List<ScalarMsg>())).Add(new ScalarMsg { Id = a.id, V = d.mana }); }
            }
            if (hp != null) Send(MirrorKind.LongPoll, MirrorCodec.WriteScalarBatch(PollKind.Health, hp));
            if (nu != null) Send(MirrorKind.LongPoll, MirrorCodec.WriteScalarBatch(PollKind.Nutrition, nu));
            if (ha != null) Send(MirrorKind.LongPoll, MirrorCodec.WriteScalarBatch(PollKind.Happiness, ha));
            if (st != null) Send(MirrorKind.LongPoll, MirrorCodec.WriteScalarBatch(PollKind.Stamina, st));
            if (ma != null) Send(MirrorKind.LongPoll, MirrorCodec.WriteScalarBatch(PollKind.Mana, ma));
        }
        private const int ResyncBatchSize = 60;
        /// <summary>Build this actor's record message: a full record when there is no baseline
        /// (or a field reset to default), a field patch when something changed, nothing when
        /// unchanged. Shared by the rotation and by the immediate post-spawn send.</summary>
        private bool TryBuildActorState(Actor a, out ActorStateMsg msg)
        {
            msg = default;
            try { a.prepareForSave(); } catch { }
            var data = _actorDataField?.GetValue(a) as ActorData;
            if (data == null) return false;
            var json = JsonConvert.SerializeObject(data, JsonHelper.read_settings);
            var statsJson = JsonConvert.SerializeObject(ReadStats(a));   // king's computed stats
            // Field-level diff against what this follower set last received. Position and
            // vitals are excluded (Move events / SendVitals). Unchanged -> nothing; some
            // fields changed -> a patch with just those; a field reset to its default
            // (dropped from the JSON) or no baseline yet -> the full record.
            int sh = statsJson.GetHashCode();
            bool statsChanged = !_sentStatsHash.TryGetValue(a.id, out var prevS) || prevS != sh;
            var cur = Newtonsoft.Json.Linq.JObject.Parse(json);
            foreach (var f in _volatileFields) cur.Remove(f);
            byte mode = ActorStateMode.Full;
            string payload = json;
            // Rolling safety net: each actor's full record goes out again at most every
            // ActorFullRefreshPeriod, spread across the rotation (bounded rate, no bursts).
            float now = UnityEngine.Time.unscaledTime;
            bool refreshDue = !_lastFullAt.TryGetValue(a.id, out var lastFull) || now - lastFull >= _actorFullPeriod;
            if (!refreshDue && _sentActorJson.TryGetValue(a.id, out var prev))
            {
                var patch = new Newtonsoft.Json.Linq.JObject();
                bool removed = false;
                foreach (var prop in cur.Properties())
                    if (!Newtonsoft.Json.Linq.JToken.DeepEquals(prop.Value, prev[prop.Name])) patch[prop.Name] = prop.Value;
                foreach (var prop in prev.Properties())
                    if (cur[prop.Name] == null) { removed = true; break; }
                if (!removed)
                {
                    if (patch.Count == 0 && !statsChanged) return false;   // nothing changed since last sent
                    mode = ActorStateMode.Patch;
                    payload = patch.Count == 0 ? "" : patch.ToString(Newtonsoft.Json.Formatting.None);
                }
            }
            _sentActorJson[a.id] = cur;
            _sentStatsHash[a.id] = sh;
                if (mode == ActorStateMode.Full) _lastFullAt[a.id] = now;
            if (mode == ActorStateMode.Full) ResyncFull++; else ResyncStatsOnly++;
            msg = (new ActorStateMsg { Id = a.id, Mode = mode, Json = payload,
                StatsJson = (mode == ActorStateMode.Full || statsChanged) ? statsJson : "" });
            return true;
        }

        // Actors spawned this frame: their creation message carries creation-time data (city,
        // kingdom and traits are assigned right after), so send the settled full record on the
        // next frame instead of waiting up to a whole rotation for it.
        private readonly List<Actor> _freshActors = new List<Actor>();
        private readonly Dictionary<long, float> _lastFullAt = new Dictionary<long, float>();
        // Seconds between an actor's safety-net full records: 30 s, stretched for big worlds so
        // the total stays around 10 full records per second.
        private float _actorFullPeriod = 30f;
        private int _freshFrame;

        private void FlushFreshActors()
        {
            if (_freshActors.Count == 0 || UnityEngine.Time.frameCount == _freshFrame) return;
            var list = new List<ActorStateMsg>(_freshActors.Count);
            foreach (var a in _freshActors)
                if (a != null && a.isAlive() && TryBuildActorState(a, out var m)) list.Add(m);
            _freshActors.Clear();
            if (list.Count > 0) Send(MirrorKind.ActorResync, MirrorCodec.WriteActorResync(list));
        }

        private void SendActorResync()
        {
            try
            {
                var units = World.world?.units?.getSimpleList();
                if (units == null || units.Count == 0) return;
                int batch = Math.Min(ResyncBatchSize, units.Count);
                _actorFullPeriod = Math.Max(30f, units.Count / 10f);
                var list = new List<ActorStateMsg>(batch);
                for (int n = 0; n < batch; n++)
                {
                    var a = units[(_resyncCursor + n) % units.Count];
                    if (a == null || !a.isAlive()) continue;
                    if (TryBuildActorState(a, out var m)) list.Add(m);
                }
                _resyncCursor = (_resyncCursor + batch) % units.Count;
                if (list.Count > 0) Send(MirrorKind.ActorResync, MirrorCodec.WriteActorResync(list));
            }
            catch (Exception e) { ModLog.Warn("SendActorResync failed: " + e.Message); }
        }

        // long_poll (500 ms): change detection for state the game mutates in place without a
        // hookable method. Ages: followers compute them from the synced clock; only an
        // age_overgrowth change is sent. Structural sweeps send only objects that changed.
        private int _metaPollTick;
        private float _fullRefreshTimer;
        private const float FullRefreshSeconds = 30f;
        // Last age_overgrowth sent per actor. Age itself = years since created_time (synced
        // world clock + spawn data, so followers compute it) + age_overgrowth; only the
        // overgrowth part ever needs to cross the wire, and it changes rarely.
        private readonly Dictionary<long, int> _sentOvergrowth = new Dictionary<long, int>();

        /// <summary>Every FullRefreshSeconds: forget what was sent, so the next sweeps re-send
        /// everything once. The safety net for a dropped or failed apply on a follower.</summary>
        private void TickFullRefresh(float dt)
        {
            _fullRefreshTimer += dt;
            if (_fullRefreshTimer < FullRefreshSeconds) return;
            ResetChangeBaselines(includeActorRecords: false);   // actor records refresh on a rolling basis
            SendActorRoster();
        }

        /// <param name="includeActorRecords">True on a snapshot: whoever loads it must get
        /// every record again. The periodic safety net leaves actor records to the rolling
        /// refresh in TryBuildActorState, so a big world never sends them all in one burst.</param>
        private void SendActorRoster()
        {
            var units = World.world?.units?.getSimpleList();
            if (units == null) return;
            var w = new LiteNetLib.Utils.NetDataWriter();
            var ids = new List<long>(units.Count);
            foreach (var a in units) if (a != null && a.isAlive()) ids.Add(a.id);
            w.Put(ids.Count);
            foreach (var id in ids) w.Put(id);
            Send(MirrorKind.ActorRoster, w.CopyData());
        }

        private void ApplyActorRoster(byte[] payload)
        {
            var r = new LiteNetLib.Utils.NetDataReader(payload);
            int n = r.GetInt();
            var kingIds = new HashSet<long>();
            for (int i = 0; i < n; i++) kingIds.Add(r.GetLong());
            var units = World.world?.units?.getSimpleList();
            if (units == null) return;
            var mine = new HashSet<long>();
            int removed = 0;
            foreach (var a in units.ToArray())
            {
                if (a == null || !a.isAlive()) continue;
                mine.Add(a.id);
                if (!kingIds.Contains(a.id))
                {
                    ApplyObjectRemove(new ObjectRemoveMsg { Kind = ActorRemoveKind, Id = a.id });
                    removed++;
                }
            }
            var missing = new List<long>();
            foreach (var id in kingIds) if (!mine.Contains(id)) missing.Add(id);
            if (missing.Count > 0)
            {
                var w = new LiteNetLib.Utils.NetDataWriter();
                w.Put(missing.Count);
                foreach (var id in missing) w.Put(id);
                Send(MirrorKind.ActorRequest, w.CopyData());
            }
            if (removed > 0 || missing.Count > 0)
                ModLog.Info($"[sync] roster: removed {removed} stray actor(s), requested {missing.Count} missing");
        }

        private void AnswerActorRequest(byte[] payload)
        {
            var r = new LiteNetLib.Utils.NetDataReader(payload);
            int n = Math.Min(r.GetInt(), 2000);
            for (int i = 0; i < n; i++)
            {
                var a = World.world?.units?.get(r.GetLong());
                if (a != null && a.isAlive()) BroadcastSpawn(a);
            }
        }

        private void ResetChangeBaselines(bool includeActorRecords = true)
        {
            _fullRefreshTimer = 0f;
            _sentObjHash.Clear();
            _sentOvergrowth.Clear();
            _sentVitals.Clear();
            _lastWorldAgeHash = 0;
            if (includeActorRecords) { _sentStatsHash.Clear(); _sentActorJson.Clear(); _lastFullAt.Clear(); }
        }

        private void SendLongPoll()
        {
            try
            {
                var units = World.world?.units?.getSimpleList();
                if (units == null) return;
                List<ScalarMsg> list = null;
                for (int i = 0; i < units.Count; i++)
                {
                    var a = units[i];
                    if (a == null || !a.isAlive()) continue;
                    var d = _actorDataField?.GetValue(a) as ActorData;
                    if (d == null) continue;
                    if (_sentOvergrowth.TryGetValue(a.id, out var og) && og == d.age_overgrowth) continue;
                    _sentOvergrowth[a.id] = d.age_overgrowth;
                    (list ?? (list = new List<ScalarMsg>())).Add(new ScalarMsg { Id = a.id, V = d.age_overgrowth });
                }
                if (list != null)
                    Send(MirrorKind.LongPoll, MirrorCodec.WriteScalarBatch(PollKind.Overgrowth, list));

                // Structural change detection (events cover zones, culture/language/religion and
                // renames immediately via MarkDirty). The sweeps below send only objects whose
                // serialized state differs from what was last sent: population, stats and other
                // values the game mutates in place with no method to hook.
                _metaPollTick++;
                if (_metaPollTick % 4 == 0)   // 2 s
                {
                    RefreshStructural(World.world?.cities);
                    RefreshStructural(World.world?.kingdoms);
                }
                if (_metaPollTick % 10 == 5)  // 5 s
                {
                    RefreshStructural(World.world?.cultures);
                    RefreshStructural(World.world?.religions);
                    RefreshStructural(World.world?.languages);
                    RefreshStructural(World.world?.clans);
                    RefreshStructural(World.world?.families);
                    RefreshStructural(World.world?.armies);
                    RefreshStructural(World.world?.alliances);
                    RefreshStructural(World.world?.wars);
                    RefreshStructural(World.world?.plots);
                }
                SendWorldAge();   // world-age state (era_manager isn't advanced on followers)
            }
            catch (Exception e) { ModLog.Warn("SendLongPoll failed: " + e.Message); }
        }

        // Stream the authoritative world-age state (MapStats). Followers don't advance the
        // era_manager (passive sim), so the King pushes age id/slot/progress/slots each long_poll.
        private int _lastWorldAgeHash;
        private void SendWorldAge()
        {
            try
            {
                var ms = _mapStatsField?.GetValue(World.world) as MapStats;
                if (ms == null) return;
                // Progress moves continuously; quantize it so a slowly advancing era isn't a
                // message every 500 ms, while any id/slot/pause/speed change goes out at once.
                int h = (ms.world_age_id ?? "").GetHashCode() ^ (ms.world_age_slot_index * 397) ^ (ms.is_world_ages_paused ? 1 : 2)
                        ^ ((int)(ms.current_age_progress * 50f) * 7919) ^ ms.world_ages_speed_multiplier.GetHashCode() ^ ms.current_world_ages_duration.GetHashCode();
                if (h == _lastWorldAgeHash) return;
                _lastWorldAgeHash = h;
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
                foreach (var o in snapshot) OnKingObject(o, onlyIfChanged: true);
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
            ClearDanglingLinks();   // a dangling link makes the game's save throw
            var data = WorldSync.Save();
            if (data.Length == 0) { ModLog.Warn("[sync] snapshot not sent: the world could not be saved (see error above)"); return; }
            // A newer transfer to the same audience supersedes an unfinished one.
            _outgoingSnapshots.RemoveAll(t => t.TargetId4 == targetId4);
            int count = (data.Length + NetProtocol.SnapshotChunkSize - 1) / NetProtocol.SnapshotChunkSize;
            int transferId = _nextTransferId++;
            // Whoever loads this snapshot starts from ITS state, not from what was sent before.
            // Forget every change baseline so the following sweeps send full state again.
            ResetChangeBaselines();
            // Cut-over marker on the reliable mirror stream, right after serializing: the
            // receiver holds every later live change until this snapshot has loaded.
            var mark = new LiteNetLib.Utils.NetDataWriter();
            mark.Put(transferId); mark.Put(targetId4 ?? "");
            Send(MirrorKind.SnapshotMark, mark.CopyData());
            SendLiveStatuses();
            _outgoingSnapshots.Add(new OutgoingSnapshot
            {
                TargetId4 = targetId4, TransferId = transferId, Epoch = _epoch,
                Data = data, Count = count
            });
            ModLog.Info($"Snapshot transfer queued: {data.Length} bytes / {count} chunks -> {(targetId4.Length == 0 ? "ALL" : targetId4)} (epoch {_epoch}).");
        }

        /// <summary>King: every unit's running statuses with their remaining time. The save
        /// carries no statuses, so a joiner only saw those added after it joined (sleeping,
        /// burning, pregnant...). Sent right after the cut-over marker, so the joiner applies
        /// them on top of the loaded world; followers already in the room just get their
        /// timers corrected.</summary>
        private void SendLiveStatuses()
        {
            var units = World.world?.units?.getSimpleList();
            if (units == null) return;
            int sent = 0;
            foreach (var a in units)
            {
                if (a == null || !a.isAlive()) continue;
                try
                {
                    foreach (var kv in a.getStatusesDict())
                    {
                        var st = kv.Value;
                        if (st == null || st.is_finished) continue;
                        float left = (float)st.getRemainingTime();
                        if (left <= 0f) continue;
                        Send(MirrorKind.Status, MirrorCodec.Write(new StatusMsg { Id = a.id, StatusId = kv.Key, Timer = left, Add = true }));
                        sent++;
                    }
                }
                catch { }
            }
            if (sent > 0) ModLog.Info($"[sync] sent {sent} running status(es) after the snapshot mark");
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
            int loadedId = _rxTransferId;
            _rxSnapshot = null; _rxTransferId = -1;
            // Hold live traffic from here until the (multi-frame) load has finished. If this
            // game is still booting, park the snapshot and load it once the local world is up.
            _snapshotLoading = true; _loadingEpoch = _rxEpoch; _loadingTransferId = loadedId;
            _loadStartedAt = UnityEngine.Time.unscaledTime;
            _deferredSnapshot = data;
            ModLog.Info($"World snapshot received ({data.Length} bytes, epoch {_rxEpoch}).");
            PumpSnapshotLoad();
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

        /// <summary>Bytes / messages sent per MirrorKind since start (dev bridge: /traffic).</summary>
        public static readonly long[] TxBytesByKind = new long[64], TxCountByKind = new long[64];

        private void Send(MirrorKind kind, byte[] payload, DeliveryMethod method = DeliveryMethod.ReliableOrdered)
        {
            int k = (int)kind;
            if (k >= 0 && k < TxBytesByKind.Length) { TxBytesByKind[k] += payload?.Length ?? 0; TxCountByKind[k]++; }
            NetClient.Instance.SendMirror(kind, payload, CurrentEpoch, method);
        }
    }
}
