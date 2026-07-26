using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using WorldboxMP.Shared.Protocol;   // MemberKind, KingdomPropKind

namespace WorldboxMP.Client.Mirror
{
    /// <summary>
    /// Harmony patches that drive mirroring:
    ///  • PlayerControl.clickedFinal  - capture/defer local god-power actions.
    ///  • ActorManager.spawnNewUnit   - king broadcasts authoritative spawns.
    ///  • MapAction.terraformTile     - king broadcasts terrain/biome/block changes.
    ///  • Actor.b6_updateAI           - suppress follower AI *decisions* only.
    ///
    /// We deliberately do NOT suppress movement/animation/render on followers - the
    /// game keeps interpolating each actor toward its <c>next_step_position</c> (which
    /// we feed from the king's stream), so creatures move smoothly and render at full
    /// detail instead of as their zoomed-out sprites.
    /// </summary>
    public static class GameHooks
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                var clickedFinal = AccessTools.Method(typeof(PlayerControl), "clickedFinal",
                    new[] { typeof(Vector2Int), typeof(GodPower), typeof(bool) });
                if (clickedFinal != null)
                    harmony.Patch(clickedFinal, prefix: H(nameof(ClickedFinal_Prefix)), postfix: H(nameof(ClickedFinal_Postfix)));

                // Drops / disasters (meteors, rain, loot…). Guarded so god-power drops
                // (which replay via clickedFinal on followers) don't double.
                var dropSpawn = AccessTools.Method(typeof(DropManager), "spawn",
                    new[] { typeof(WorldTile), typeof(string), typeof(float), typeof(float), typeof(long) });
                if (dropSpawn != null) harmony.Patch(dropSpawn, postfix: H(nameof(DropSpawn_Postfix)));

                // createNewUnit catches player/miracle spawns. BUT village BIRTHS use a SEPARATE
                // method, createBabyActorFromData(ActorData, WorldTile, City) - not createNewUnit.
                // Without hooking it, born citizens never broadcast -> followers have no city
                // members -> city population stays 0. Hook both.
                var spawn = AccessTools.Method(typeof(ActorManager), "createNewUnit");
                if (spawn != null)
                    harmony.Patch(spawn, postfix: H(nameof(CreateNewUnit_Postfix)));
                var babySpawn = AccessTools.Method(typeof(ActorManager), "createBabyActorFromData");
                if (babySpawn != null)
                    harmony.Patch(babySpawn, postfix: H(nameof(BabySpawn_Postfix)));

                var addTrait = AccessTools.Method(typeof(Actor), "addTrait", new[] { typeof(string), typeof(bool) });
                if (addTrait != null) harmony.Patch(addTrait, postfix: H(nameof(AddTrait_Postfix)));
                var removeTrait = AccessTools.Method(typeof(Actor), "removeTrait", new[] { typeof(string) });
                if (removeTrait != null) harmony.Patch(removeTrait, postfix: H(nameof(RemoveTrait_Postfix)));

                var terraform = AccessTools.Method(typeof(MapAction), "terraformTile");
                if (terraform != null)
                    harmony.Patch(terraform, prefix: H(nameof(Terraform_Prefix)), postfix: H(nameof(Terraform_Postfix)));

                // Death/damage are KING-AUTHORITATIVE. The follower's passive sim still runs
                // environmental damage (drowning/starvation/fire -> getHit) and can locally
                // kill+destroy a mirrored actor the king still has - which is exactly how
                // village populations drifted (king "Roin 7" vs follower "Roin 2"). Block the
                // local paths: deaths only ever arrive via ApplyDeath (setAlive), damage via
                // Health/Resync. ApplyingRemote (replayed god powers) stays allowed so their
                // impact feedback still plays.
                var die = AccessTools.Method(typeof(Actor), "die");
                if (die != null) harmony.Patch(die, prefix: H(nameof(SuppressFollowerDie_Prefix)), postfix: H(nameof(Die_Postfix)));

                // Drops fall + animate on followers (drop_manager.update in the passive sim), but
                // their landing EXPLOSION (action_landed = terraform/fire/effect/shake) must NOT run
                // - that's king-authoritative and double-running it leaves a frozen explosion sprite
                // + endless shake + divergent terrain. Prefix Drop.land() on followers to skip the
                // explosion yet still landDrop() (remove the drop) so it disappears cleanly. The
                // crater/fire/deaths arrive via the king's synced terraform/fire/health.
                var dropLand = AccessTools.Method(typeof(Drop), "land");
                if (dropLand != null) harmony.Patch(dropLand, prefix: H(nameof(DropLand_Prefix)));
                _dropLandedField = AccessTools.Field(typeof(Drop), "_landed");
                _landDropMethod = AccessTools.Method(AccessTools.TypeByName("DropManager"), "landDrop");

                var addFire = AccessTools.Method(typeof(WorldBehaviourActionFire), "addFire");
                if (addFire != null) harmony.Patch(addFire, prefix: H(nameof(Fire_Prefix)), postfix: H(nameof(AddFire_Postfix)));
                var removeFire = AccessTools.Method(typeof(WorldBehaviourActionFire), "removeFire");
                if (removeFire != null) harmony.Patch(removeFire, postfix: H(nameof(RemoveFire_Postfix)));

                // moveTo is the per-tile step commit - THE event for movement mirroring.
                var moveTo = AccessTools.Method(typeof(Actor), "moveTo", new[] { typeof(WorldTile) });
                if (moveTo != null) harmony.Patch(moveTo, postfix: H(nameof(MoveTo_Postfix)));

                var setLaw = AccessTools.Method(typeof(WorldLaws), "setAgeEnabled", new[] { typeof(string), typeof(bool) });
                if (setLaw != null) harmony.Patch(setLaw, postfix: H(nameof(SetWorldLaw_Postfix)));

                // New-year boundary (fires once per in-game year) → sync world time.
                var objAge = AccessTools.Method(typeof(MapBox), "updateObjectAge");
                if (objAge != null) harmony.Patch(objAge, postfix: H(nameof(UpdateObjectAge_Postfix)));

                // Actor rename.
                var setName = AccessTools.Method(typeof(NanoObject), "setName", new[] { typeof(string), typeof(bool) });
                if (setName != null) harmony.Patch(setName, postfix: H(nameof(SetName_Postfix)));

                // Combat / health (see the death/damage authority note above).
                var getHit = AccessTools.Method(typeof(Actor), "getHit");
                if (getHit != null) harmony.Patch(getHit, prefix: H(nameof(SuppressFollowerGetHit_Prefix)), postfix: H(nameof(GetHit_Postfix)));

                // Actor REMOVAL (corpse cleanup). With local deaths blocked, follower corpses
                // would linger forever (and dead members inflate city population counts) -
                // so mirror the king's actual actor destruction and remove the same actor.
                var actorDestroy = AccessTools.Method(typeof(ActorManager), "destroyObject", new[] { typeof(Actor) });
                if (actorDestroy != null) harmony.Patch(actorDestroy, postfix: H(nameof(ActorDestroy_Postfix)));
                else ModLog.Warn("hook MISS: ActorManager.destroyObject");

                // Status effects.
                var addStatus = AccessTools.Method(typeof(Actor), "addStatusEffect", new[] { typeof(StatusAsset), typeof(float), typeof(bool) });
                if (addStatus != null) harmony.Patch(addStatus, postfix: H(nameof(AddStatus_Postfix)));
                var finishStatus = AccessTools.Method(typeof(BaseSimObject), "finishStatusEffect", new[] { typeof(string) });
                if (finishStatus != null) harmony.Patch(finishStatus, postfix: H(nameof(FinishStatus_Postfix)));

                // Equipment.
                var setItem = AccessTools.Method(typeof(ActorEquipment), "setItem", new[] { typeof(Item), typeof(Actor) });
                if (setItem != null) harmony.Patch(setItem, postfix: H(nameof(SetItem_Postfix)));

                // Structural object creation (kingdom/city/war/alliance/plot/religion/culture/language/building).
                PatchCreate(harmony, typeof(KingdomManager), "makeNewCivKingdom", typeof(Kingdom));
                PatchCreate(harmony, typeof(CityManager), "newCity", typeof(City));
                PatchCreate(harmony, typeof(WarManager), "newWar", typeof(War));
                PatchCreate(harmony, typeof(AllianceManager), "newAlliance", typeof(Alliance));
                PatchCreate(harmony, typeof(PlotManager), "newPlot", typeof(Plot));
                PatchCreate(harmony, typeof(ReligionManager), "newReligion", typeof(Religion));
                PatchCreate(harmony, typeof(CultureManager), "newCulture", typeof(Culture));
                PatchCreate(harmony, typeof(LanguageManager), "newLanguage", typeof(Language));
                // Buildings: hook the FUNNEL overload addBuilding(BuildingAsset, WorldTile, …)
                // - addBuilding(string,…) calls it, and civs/placement may call it directly,
                // so this catches every addBuilding-based creation (e.g. village huts). NOTE:
                // flora (trees/bushes/rocks) is created via addTree/addFlora, NOT addBuilding,
                // so it is not covered here (those grow autonomously and aren't player-relevant).
                foreach (var mi in typeof(BuildingManager).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (mi.Name != "addBuilding") continue;
                    var ps = mi.GetParameters();
                    if (ps.Length > 0 && ps[0].ParameterType == typeof(BuildingAsset) && typeof(Building).IsAssignableFrom(mi.ReturnType))
                    { harmony.Patch(mi, postfix: H(nameof(StructuralCreate_Postfix))); break; }
                }

                // Structural object destruction (removeObject) - one postfix for all.
                PatchRemove(harmony, typeof(KingdomManager), typeof(Kingdom));
                PatchRemove(harmony, typeof(CityManager), typeof(City));
                PatchRemove(harmony, typeof(WarManager), typeof(War));
                PatchRemove(harmony, typeof(AllianceManager), typeof(Alliance));
                PatchRemove(harmony, typeof(PlotManager), typeof(Plot));
                PatchRemove(harmony, typeof(ReligionManager), typeof(Religion));
                PatchRemove(harmony, typeof(CultureManager), typeof(Culture));
                PatchRemove(harmony, typeof(LanguageManager), typeof(Language));
                PatchRemove(harmony, typeof(BuildingManager), typeof(Building));
                // Bombs/disasters/fire destroy buildings via Building.startDestroyBuilding(), NOT
                // BuildingManager.removeObject - so those destructions never reached followers
                // (e.g. a volcano bombed away on the king persisted on the follower). Hook it and
                // broadcast a removal so the follower removes the same building.
                var destroyBuilding = AccessTools.Method(typeof(Building), "startDestroyBuilding");
                if (destroyBuilding != null) harmony.Patch(destroyBuilding, postfix: H(nameof(BuildingDestroy_Postfix)));

                // Actor membership/conversion (kingdom/religion/culture/language/city).
                PatchMember(harmony, "joinKingdom", typeof(Kingdom));
                PatchMember(harmony, "setReligion", typeof(Religion));
                PatchMember(harmony, "setCulture", typeof(Culture));
                PatchMember(harmony, "joinLanguage", typeof(Language));
                PatchMember(harmony, "joinCity", typeof(City));

                // City ownership change (capture / rebellion).
                var capture = AccessTools.Method(typeof(City), "joinAnotherKingdom", new[] { typeof(Kingdom), typeof(bool), typeof(bool) });
                if (capture != null) harmony.Patch(capture, postfix: H(nameof(Capture_Postfix)));

                // Kingdom properties.
                var setKing = AccessTools.Method(typeof(Kingdom), "setKing", new[] { typeof(Actor), typeof(bool) });
                if (setKing != null) harmony.Patch(setKing, postfix: H(nameof(SetKing_Postfix)));
                var setCapital = AccessTools.Method(typeof(Kingdom), "setCapital", new[] { typeof(City) });
                if (setCapital != null) harmony.Patch(setCapital, postfix: H(nameof(SetCapital_Postfix)));
                var setColor = AccessTools.Method(typeof(Kingdom), "setColorID", new[] { typeof(int) });
                if (setColor != null) harmony.Patch(setColor, postfix: H(nameof(SetColor_Postfix)));

                // War end.
                var endWar = AccessTools.Method(typeof(WarManager), "endWar");
                if (endWar != null) harmony.Patch(endWar, postfix: H(nameof(EndWar_Postfix)));

                var ai = AccessTools.Method(typeof(Actor), "b6_updateAI");
                if (ai != null) harmony.Patch(ai, prefix: H(nameof(SuppressAi_Prefix)));

                // Projectiles (arrows/spells/boulders in flight). The king broadcasts each
                // ProjectileManager.spawn; followers re-spawn it for the visual (their
                // projectiles.update already runs in the passive sim below). On followers a
                // projectile is VISUAL-ONLY: checkHitOnNearbyUnits is replaced (no local damage/
                // deflect - hits arrive via the king's Health/Death sync) and its landing
                // world-actions are already neutralized (Terraform_Prefix/Fire_Prefix).
                var projSpawn = AccessTools.Method(typeof(ProjectileManager), "spawn");
                if (projSpawn != null) harmony.Patch(projSpawn, postfix: H(nameof(ProjectileSpawn_Postfix)));
                var projHit = AccessTools.Method(typeof(Projectile), "checkHitOnNearbyUnits");
                if (projHit != null) harmony.Patch(projHit, prefix: H(nameof(ProjectileCheckHit_Prefix)));
                // targetReached runs impact_actions/world_actions with a possibly-unresolvable
                // initiator on the follower - swallow any NRE there so one bad projectile can't
                // stall the whole projectiles.update for that frame.
                var projReached = AccessTools.Method(typeof(Projectile), "targetReached");
                if (projReached != null) harmony.Patch(projReached, finalizer: H(nameof(ProjectileTargetReached_Finalizer)));

                // Weather: cloud spawns come from the "clouds" world behaviour, which only the
                // king runs. Hook the effects funnel and mirror fx_cloud spawns (asset + pos);
                // the follower's stack_effects update (added to the passive sim below) drifts
                // and rains them like the king's does.
                var fxSpawn = AccessTools.Method(typeof(EffectsLibrary), "spawn",
                    new[] { typeof(string), typeof(WorldTile), typeof(string), typeof(string), typeof(float), typeof(float), typeof(float), typeof(Actor) });
                if (fxSpawn != null) harmony.Patch(fxSpawn, postfix: H(nameof(EffectSpawn_Postfix)));
                else ModLog.Warn("hook MISS: EffectsLibrary.spawn");
                _stackEffectsField = AccessTools.Field(typeof(MapBox), "stack_effects");
                _stackEffectsUpdate = _stackEffectsField != null ? AccessTools.Method(_stackEffectsField.FieldType, "update", new[] { typeof(float) }) : null;

                // World-log announcements (top-left "X was murdered by Y" feed). All of them
                // funnel through WorldLogMessageExtensions.add; followers never generate any
                // (suppressed sim), so the king mirrors each message DTO.
                var wlAdd = AccessTools.Method(AccessTools.TypeByName("WorldLogMessageExtensions"), "add");
                if (wlAdd != null) harmony.Patch(wlAdd, postfix: H(nameof(WorldLogAdd_Postfix)));
                else ModLog.Warn("hook MISS: WorldLogMessageExtensions.add");

                // Slash effects - the swing/punch/kick/talk arcs. spawnSlash is the single funnel
                // (spawnSlashPunch/Kick/… and weapon slashes all route through it). Followers
                // never produce them (AI suppressed) so the king mirrors each one (batched/frame).
                var slash = AccessTools.Method(typeof(Actor), "spawnSlash",
                    new[] { typeof(Vector2), typeof(string), typeof(float), typeof(float), typeof(float), typeof(float?) });
                if (slash != null) harmony.Patch(slash, postfix: H(nameof(SpawnSlash_Postfix)));
                else ModLog.Warn("hook MISS: Actor.spawnSlash");

                // Followers must NOT derive their own stats (their subspecies genome comes
                // through incomplete, so updateStats yields stats["health"]≈0 → maxHealth=1,
                // clamping synced health down). The king is authoritative for stats: it sends
                // the computed BaseStats and the follower adopts them. Suppress local recompute.
                var updStats = AccessTools.Method(typeof(Actor), "updateStats");
                if (updStats != null) harmony.Patch(updStats, prefix: H(nameof(SuppressUpdateStats_Prefix)));

                // Defensive: on followers, the mirrored world can briefly hold a building
                // with a null current_tile (e.g. created via loadObject before its tile is
                // linked). BatchBuildings.updateVisibility then NREs every sim tick inside
                // a parallel job - an exception storm that stalls the whole update loop
                // (kills input/zoom/spawn). Swallow it on followers so the client stays
                // responsive; the king is authoritative and must NOT mask real bugs.
                var bbVis = AccessTools.Method(typeof(BatchBuildings), "updateVisibility");
                if (bbVis != null) harmony.Patch(bbVis, finalizer: H(nameof(BatchBuildingsVisibility_Finalizer)));

                // ActorManager.fillVisibleObjects (render path, runs every frame in
                // MapBox.calculateVisibleObjects) NREs if a mirrored actor has a null asset or
                // null current_tile (seen with elves). Swallow on followers so render survives.
                var fillVis = AccessTools.Method(typeof(ActorManager), "fillVisibleObjects");
                if (fillVis != null) harmony.Patch(fillVis, finalizer: H(nameof(FillVisibleObjects_Finalizer)));

                // The mirrored world can contain buildings that aren't fully initialized
                // on a follower (null asset/batch/current_tile). The follower's own sim
                // then NREs on them in several places. Guard each crash site on followers
                // (skip the op for that one building) so the sim loop survives. Building.asset
                // is internal at runtime -> reflection.
                // PASSIVE FOLLOWER: replace the follower's autonomous world simulation with
                // a minimal subset (movement + animation + rendering). The king is the only
                // authority; followers must not run cleanup/cities/kingdoms/wars/etc. (which
                // crash on the partially-mirrored state and diverge). Keeps updateActors
                // (movement interp) + updateBuildings (render, guarded) + updateMapLayers.
                _mbUpdateBuildings = AccessTools.Method(typeof(MapBox), "updateBuildings", new[] { typeof(float) });
                // MapBox.updateBuildings is a DEBUG-gated no-op (only runs buildings.update when
                // DebugOption.SystemUpdateBuildings is set). The REAL building batch update - which
                // schedules the updateVisibility job that sets each building's is_visible - lives
                // in the part of updateSimulation we skip. Without it, high-res render
                // (calculateVisibleBuildings -> fillVisibleObjects, filtered by is_visible) finds
                // NO buildings -> they only appear in the low-res bit-scale chunk render. So call
                // BuildingManager.update directly on the follower.
                _buildingsUpdate = AccessTools.Method(typeof(BuildingManager), "update", new[] { typeof(float) });
                // BuildingZonesSystem.update() is the ONLY thing that fills each zone's
                // buildings_render_list - the list the HIGH-RES render iterates
                // (BuildingManager.fillVisibleObjects copies zone.buildings_render_list). It runs
                // only in the skipped part of updateSimulation. addBuildingMain already marks the
                // zone + global dirty on load, so calling update() here rebuilds the render list
                // and buildings finally appear when zoomed in.
                _buildingZonesUpdate = AccessTools.Method(AccessTools.TypeByName("BuildingZonesSystem"), "update");
                // City.updateCityStatus() recomputes population/adults/children/families/housing
                // from the city's member units. It's driven by City.update() (via CityManager.update)
                // which the passive sim skips, so on followers city stats stay 0. Call it per city.
                _cityStatusUpdate = AccessTools.Method(typeof(City), "updateCityStatus");
                _mbUpdateMapLayers = AccessTools.Method(typeof(MapBox), "updateMapLayers", new[] { typeof(float) });
                _animUpdateTime    = AccessTools.Method(typeof(AnimationHelper), "updateTime", new[] { typeof(float), typeof(float) });
                _enemiesClear      = AccessTools.Method(typeof(EnemiesFinder), "clear");
                // Drops (nukes/bombs/meteors) + projectiles: their fall/flight physics live in
                // these managers' update(); without them a synced nuke freezes in mid-air on
                // the follower while only the terrain result syncs.
                _dropMgrField = AccessTools.Field(typeof(MapBox), "drop_manager");
                _dropUpdate   = _dropMgrField != null ? AccessTools.Method(_dropMgrField.FieldType, "update", new[] { typeof(float) }) : null;
                _projMgrField = AccessTools.Field(typeof(MapBox), "projectiles");
                _projUpdate   = _projMgrField != null ? AccessTools.Method(_projMgrField.FieldType, "update", new[] { typeof(float) }) : null;
                // Full actor pipeline (the order updateSimulation uses) so movement isn't
                // starved: refresh container → meta prep → visibility → batch update
                // (movement/render) → destroy (death cleanup). ActorManager = units.
                _unitsCheckContainer = AccessTools.Method(typeof(ActorManager), "checkContainer");
                _unitsPrepMeta       = AccessTools.Method(typeof(ActorManager), "prepareForMetaChecks");
                _unitsCalcVisible    = AccessTools.Method(typeof(ActorManager), "calculateVisibleActors");
                _unitsUpdate         = AccessTools.Method(typeof(ActorManager), "update", new[] { typeof(float) });
                _unitsCheckDestroy   = AccessTools.Method(typeof(ActorManager), "checkObjectsToDestroy");
                var sim = AccessTools.Method(typeof(MapBox), "updateSimulation", new[] { typeof(float) });
                if (sim != null && _unitsUpdate != null) harmony.Patch(sim, prefix: H(nameof(PassiveSim_Prefix)));

                _buildingAssetField = AccessTools.Field(typeof(Building), "asset");
                _buildingComponentsField = AccessTools.Field(typeof(Building), "components_list");
                var shake = AccessTools.Method(typeof(Building), "updateShake", new[] { typeof(float) });
                if (shake != null) harmony.Patch(shake, prefix: H(nameof(BuildingUpdateShake_Prefix)));
                var housing = AccessTools.Method(typeof(Building), "hasHousingLogic");
                if (housing != null) harmony.Patch(housing, prefix: H(nameof(BuildingHasHousing_Prefix)));
                var comps = AccessTools.Method(typeof(Building), "updateComponents", new[] { typeof(float) });
                if (comps != null) harmony.Patch(comps, prefix: H(nameof(BuildingUpdateComponents_Prefix)));
                var ustats = AccessTools.Method(typeof(Building), "updateStats");
                if (ustats != null) harmony.Patch(ustats, prefix: H(nameof(BuildingUpdateStats_Prefix)));

                // ROOT FIX: the king is authoritative for buildings. The follower must NOT
                // autonomously construct buildings (its sim was producing uninitialized
                // shells that crash). addBuilding(...) is the construction path; loadObject
                // (snapshot + king's structural sync) is the valid load path and does NOT
                // route through addBuilding, so suppressing addBuilding on followers leaves
                // synced/snapshot buildings intact. Skip only outside world-load.
                foreach (var mi in typeof(BuildingManager).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (mi.Name != "addBuilding") continue;
                    var ps = mi.GetParameters();
                    if (ps.Length > 0 && ps[0].ParameterType == typeof(BuildingAsset))
                    { harmony.Patch(mi, prefix: H(nameof(SuppressFollowerBuild_Prefix))); break; }
                }

                // Our runtime world reloads (join snapshot / resync) can re-enter the
                // window preloader and call instantiateWindow twice for the same id,
                // which throws a duplicate-key ArgumentException and aborts the load.
                // Guard it: skip the call if that window is already being preloaded.
                _preloadOps = AccessTools.Field(typeof(WindowPreloader), "_windows_preloading_operations");
                var instantiate = AccessTools.Method(typeof(WindowPreloader), "instantiateWindow");
                if (instantiate != null && _preloadOps != null)
                    harmony.Patch(instantiate, prefix: H(nameof(InstantiateWindow_Prefix)));

                int patched = 0;
                foreach (var _ in harmony.GetPatchedMethods()) patched++;
                ModLog.Info($"Mirror game hooks applied: {patched} methods patched (watch for any 'hook MISS' / 'PatchCreate: no' warnings above).");
            }
            catch (Exception e) { ModLog.Error("Failed to apply game hooks: " + e); }
        }

        private static HarmonyMethod H(string name) => new HarmonyMethod(typeof(GameHooks), name);

        /// <summary>Patch the creation method whose return type IS the created object (skips void overloads).</summary>
        private static void PatchCreate(Harmony harmony, Type managerType, string method, Type objType)
        {
            try
            {
                MethodInfo found = null;
                foreach (var mi in managerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                    if (mi.Name == method && objType.IsAssignableFrom(mi.ReturnType)) { found = mi; break; }
                if (found != null) harmony.Patch(found, postfix: H(nameof(StructuralCreate_Postfix)));
                else ModLog.Warn($"PatchCreate: no {managerType.Name}.{method} returning {objType.Name}");
            }
            catch (Exception e) { ModLog.Warn($"PatchCreate {managerType.Name}.{method} failed: " + e.Message); }
        }

        private static void PatchRemove(Harmony harmony, Type managerType, Type objType)
        {
            try
            {
                var mi = AccessTools.Method(managerType, "removeObject", new[] { objType });
                if (mi != null) harmony.Patch(mi, postfix: H(nameof(StructuralRemove_Postfix)));
            }
            catch (Exception e) { ModLog.Warn($"PatchRemove {managerType.Name} failed: " + e.Message); }
        }

        private static void PatchMember(Harmony harmony, string method, Type targetType)
        {
            try
            {
                var mi = AccessTools.Method(typeof(Actor), method, new[] { targetType });
                if (mi != null) harmony.Patch(mi, postfix: H(nameof(Member_Postfix)));
            }
            catch (Exception e) { ModLog.Warn($"PatchMember {method} failed: " + e.Message); }
        }

        private static FieldInfo _preloadOps;

        // Return false to skip instantiateWindow when this window id is already queued
        // (prevents the duplicate-key crash on a reload that re-enters the preloader).
        private static bool InstantiateWindow_Prefix(string pWindowID)
        {
            try
            {
                if (_preloadOps?.GetValue(null) is IDictionary ops && ops.Contains(pWindowID))
                    return false;
            }
            catch { }
            return true;
        }

        private static bool ClickedFinal_Prefix(Vector2Int pPos, GodPower pPower)
        {
            var m = MirrorManager.Instance;
            if (m != null) m.InGodPower = true;   // suppress god-power-driven drop broadcasts
            if (m == null || !m.Active) return true;
            if (m.ApplyingRemote) return true;
            return m.OnLocalGodPower(pPos, pPower);
        }

        private static void ClickedFinal_Postfix()
        {
            if (MirrorManager.Instance != null) MirrorManager.Instance.InGodPower = false;
        }

        private static void DropSpawn_Postfix(object[] __args)
        {
            if (__args != null && __args.Length >= 2 && __args[0] is WorldTile t && __args[1] is string id)
                MirrorManager.Instance?.OnKingDisaster(t, id);
        }

        private static void CreateNewUnit_Postfix(Actor __result, string pStatsID)
        {
            MirrorManager.Instance?.OnKingSpawned(__result, pStatsID);
        }

        // Village births: createBabyActorFromData(ActorData, WorldTile, City). Broadcast the baby
        // like any spawn so followers get city citizens (otherwise city population stays 0).
        private static void BabySpawn_Postfix(Actor __result)
        {
            if (__result == null) return;
            string assetId = null;
            try { assetId = __result.asset?.id; } catch { }
            MirrorManager.Instance?.OnKingSpawned(__result, assetId);
        }

        private static void MoveTo_Postfix(Actor __instance, WorldTile pTileTarget)
        {
            MirrorManager.Instance?.OnKingMove(__instance, pTileTarget);
        }

        private static void SetWorldLaw_Postfix(string pID, bool pValue)
        {
            MirrorManager.Instance?.OnKingLaw(pID, pValue);
        }

        private static void UpdateObjectAge_Postfix()
        {
            MirrorManager.Instance?.OnKingYear();
        }

        private static void SetName_Postfix(NanoObject __instance, string pName)
        {
            if (__instance is Actor a) MirrorManager.Instance?.OnKingRename(a, pName);
        }

        private static void GetHit_Postfix(Actor __instance)
        {
            MirrorManager.Instance?.OnKingHealth(__instance);
        }

        private static void AddStatus_Postfix(Actor __instance, StatusAsset pStatusAsset, float pOverrideTimer)
        {
            if (pStatusAsset != null) MirrorManager.Instance?.OnKingStatus(__instance, pStatusAsset.id, pOverrideTimer, true);
        }

        private static void FinishStatus_Postfix(BaseSimObject __instance, string pID)
        {
            if (__instance is Actor a) MirrorManager.Instance?.OnKingStatus(a, pID, 0f, false);
        }

        private static void SetItem_Postfix(Item pItem, Actor pActor)
        {
            MirrorManager.Instance?.OnKingEquip(pActor, pItem);
        }

        private static int _structCreateLogged;
        private static void StructuralCreate_Postfix(object __result)
        {
            if (_structCreateLogged < 12) { _structCreateLogged++; ModLog.Info($"[KING] structural create hook fired: {(__result == null ? "null" : __result.GetType().Name)}"); }
            MirrorManager.Instance?.OnKingObject(__result);
        }

        private static void StructuralRemove_Postfix(object[] __args)
        {
            if (__args != null && __args.Length > 0) MirrorManager.Instance?.OnKingObjectRemove(__args[0]);
        }

        // A building began destruction (bomb/disaster/fire). Broadcast it as an object removal so
        // followers remove the same building (the king's removeObject hook alone misses this path).
        private static void BuildingDestroy_Postfix(Building __instance)
        {
            if (__instance != null) MirrorManager.Instance?.OnKingObjectRemove(__instance);
        }

        private static void Member_Postfix(Actor __instance, object[] __args)
        {
            if (__args != null && __args.Length > 0) MirrorManager.Instance?.OnKingMember(__instance, __args[0]);
        }

        private static void Capture_Postfix(City __instance, object[] __args)
        {
            if (__args == null || __args.Length < 3) return;
            MirrorManager.Instance?.OnKingCapture(__instance, __args[0] as Kingdom, (bool)__args[1], (bool)__args[2]);
        }

        private static void SetKing_Postfix(Kingdom __instance, object[] __args)
        {
            if (__args != null && __args.Length > 0) MirrorManager.Instance?.OnKingdomProp(__instance, KingdomPropKind.King, __args[0]);
        }

        private static void SetCapital_Postfix(Kingdom __instance, object[] __args)
        {
            if (__args != null && __args.Length > 0) MirrorManager.Instance?.OnKingdomProp(__instance, KingdomPropKind.Capital, __args[0]);
        }

        private static void SetColor_Postfix(Kingdom __instance, object[] __args)
        {
            if (__args != null && __args.Length > 0) MirrorManager.Instance?.OnKingdomColor(__instance, System.Convert.ToInt64(__args[0]));
        }

        private static void EndWar_Postfix(object[] __args)
        {
            if (__args != null && __args.Length > 0) MirrorManager.Instance?.OnKingWarEnd(__args[0], __args.Length > 1 ? __args[1] : null);
        }

        private static void AddTrait_Postfix(Actor __instance, string pTraitID)
        {
            MirrorManager.Instance?.OnKingTrait(__instance, pTraitID, true);
        }

        private static void RemoveTrait_Postfix(Actor __instance, string pTraitID)
        {
            MirrorManager.Instance?.OnKingTrait(__instance, pTraitID, false);
        }

        // Terrain is king-authoritative: block autonomous terraform on followers (e.g. a
        // drop/nuke explosion's crater) so it can't diverge - only the king's synced terraform
        // (ApplyingRemote) applies. The visual fall/boom still plays; the crater comes from sync.
        private static bool Terraform_Prefix()
        {
            var m = MirrorManager.Instance;
            return !(m != null && m.IsFollower && !m.ApplyingRemote);
        }

        private static void Terraform_Postfix(WorldTile pTile, TileType pNewTypeMain, TopTileType pTopType)
        {
            MirrorManager.Instance?.OnKingTerraform(pTile, pNewTypeMain, pTopType);
        }

        private static void Die_Postfix(Actor __instance)
        {
            MirrorManager.Instance?.OnKingDeath(__instance);
        }

        // King: an actor was finally destroyed (corpse decayed / removed) - mirror it.
        private static void ActorDestroy_Postfix(Actor pActor)
        {
            MirrorManager.Instance?.OnKingActorDestroy(pActor);
        }

        // Follower: a falling drop reached the ground. Skip its explosion (action_landed) - that
        // is king-authoritative - but still remove the drop (landDrop) so it doesn't freeze in
        // place. Returns false to skip the original land(). King/idle run land() normally.
        private static bool DropLand_Prefix(Drop __instance)
        {
            var m = MirrorManager.Instance;
            if (m == null || !m.IsFollower) return true;
            try
            {
                var dm = _dropMgrField?.GetValue(World.world);
                if (dm != null && _landDropMethod != null) _landDropMethod.Invoke(dm, new object[] { __instance });
                _dropLandedField?.SetValue(__instance, true);
            }
            catch (Exception e) { if (_dropLandErr < 3) { _dropLandErr++; ModLog.Warn("[FOLLOWER] DropLand_Prefix: " + e.Message); } }
            return false;
        }
        private static int _dropLandErr;

        // Fire is king-authoritative: block autonomous fire on followers (e.g. a drop/bomb
        // explosion igniting tiles) so re-running the drop sim for the falling VISUAL can't
        // diverge the world. Only the king's synced fire (ApplyingRemote) applies.
        private static bool Fire_Prefix()
        {
            var m = MirrorManager.Instance;
            return !(m != null && m.IsFollower && !m.ApplyingRemote);
        }

        private static void AddFire_Postfix(WorldTile pTile)
        {
            MirrorManager.Instance?.OnKingFire(pTile, true);
        }

        private static void RemoveFire_Postfix(WorldTile pTile)
        {
            MirrorManager.Instance?.OnKingFire(pTile, false);
        }

        // Suppress autonomous AI decisions on followers (king is authoritative).
        private static bool SuppressAi_Prefix()
        {
            var m = MirrorManager.Instance;
            return m == null || !m.IsFollower;
        }

        // Followers never run the real die(): deaths arrive exclusively via ApplyDeath's
        // setAlive(false) (corpse transition). Blocking die() entirely prevents the local
        // sim (drowning/starvation) from destroying actors the king still has.
        private static bool SuppressFollowerDie_Prefix()
        {
            var m = MirrorManager.Instance;
            return m == null || !m.IsFollower;
        }

        // Followers take no local damage; health flows from the king (Health + Resync).
        // Replayed god powers (ApplyingRemote) keep their impact feedback.
        private static bool SuppressFollowerGetHit_Prefix()
        {
            var m = MirrorManager.Instance;
            return m == null || !m.IsFollower || m.ApplyingRemote;
        }

        // King: a world-log announcement was posted - mirror the DTO to followers.
        private static void WorldLogAdd_Postfix(WorldLogMessage pMessage)
        {
            MirrorManager.Instance?.OnKingAnnounce(pMessage);
        }

        private static FieldInfo _stackEffectsField;
        private static MethodInfo _stackEffectsUpdate;

        // King: an effect spawned through the generic funnel - mirror CLOUDS only (weather).
        // pParam1 is the CloudAsset id; the spawned effect's transform holds the position
        // (randomized inside the cloud's spawn_action, so we read the RESULT, not the args).
        private static void EffectSpawn_Postfix(BaseEffect __result, string pID, string pParam1)
        {
            if (__result == null || pID != "fx_cloud") return;
            var pos = __result.transform.position;
            MirrorManager.Instance?.OnKingCloud(pParam1, pos.x, pos.y);
        }

        // King: a slash effect played (attack swing/punch/kick/talk/yell) - queue it for
        // this frame's batch so followers render the same arc on the mirrored actor.
        private static void SpawnSlash_Postfix(Actor __instance, Vector2 pTowardsPosition,
            string pSlashType, float pDistMod, float pTargetZ, float pStartZ, float? pAngle)
        {
            MirrorManager.Instance?.OnKingSlash(__instance, pTowardsPosition, pSlashType, pDistMod, pTargetZ, pStartZ, pAngle);
        }

        // King: a projectile launched - broadcast it so followers show the flight.
        private static void ProjectileSpawn_Postfix(Projectile __result, BaseSimObject pInitiator,
            BaseSimObject pTargetObject, string pAssetID, Vector3 pLaunchPosition, Vector3 pTargetPosition,
            float pTargetZ, float pStartPosZ)
        {
            if (__result != null)
                MirrorManager.Instance?.OnKingProjectile(pInitiator, pTargetObject, pAssetID,
                    pLaunchPosition, pTargetPosition, pTargetZ, pStartPosZ);
        }

        // Follower: projectiles are visual-only - never compute local hits/deflects/blocks
        // (that would double damage or even locally kill a mirrored actor; real hits arrive
        // via the king's Health/Death sync). Airborne → keep flying; on ground → land (the
        // Miss path plays fx + targetReached, whose world actions are prefix-blocked).
        private static bool ProjectileCheckHit_Prefix(Projectile __instance, ref AttackDataResult __result)
        {
            var m = MirrorManager.Instance;
            if (m == null || !m.IsFollower) return true;
            __result = __instance.getCurrentHeight() <= 0f ? AttackDataResult.Miss : AttackDataResult.Continue;
            return false;
        }

        private static int _projReachedErr;
        private static Exception ProjectileTargetReached_Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            var m = MirrorManager.Instance;
            if (m != null && m.IsFollower)
            {
                if (_projReachedErr < 3) { _projReachedErr++; ModLog.Warn("[FOLLOWER] projectile targetReached swallowed: " + (__exception.InnerException ?? __exception).Message); }
                return null;
            }
            return __exception;
        }

        // Skip local stat recompute on followers - stats come authoritatively from the king
        // (the follower's genome/subspecies is incomplete and would compute maxHealth=1).
        private static bool SuppressUpdateStats_Prefix()
        {
            var m = MirrorManager.Instance;
            return m == null || !m.IsFollower;
        }

        private static FieldInfo _buildingAssetField;
        private static FieldInfo _buildingComponentsField;
        private static int _suppressedBuilds;

        private static MethodInfo _mbUpdateBuildings, _mbUpdateMapLayers, _animUpdateTime, _enemiesClear, _buildingsUpdate, _buildingZonesUpdate, _cityStatusUpdate;
        private static MethodInfo _unitsCheckContainer, _unitsPrepMeta, _unitsCalcVisible, _unitsUpdate, _unitsCheckDestroy;
        private static FieldInfo _dropMgrField, _projMgrField, _dropLandedField;
        private static MethodInfo _dropUpdate, _projUpdate, _landDropMethod;
        private static bool _passiveLogged;

        // Follower runs ONLY the visual + actor subset of the simulation at the king's
        // tick rate; the king drives all autonomous world changes. Skips
        // cities/kingdoms/wars/cultures/drops/etc. Each subcall is isolated so one bad
        // entity (e.g. a not-yet-synced null kingdom) can't stall the frame.
        internal static float LastSimElapsed;       // real updateSimulation arg (king + follower)
        internal static int SimCallsThisInterval;   // how many times updateSimulation ran
        private static bool PassiveSim_Prefix(MapBox __instance, float pElapsed)
        {
            LastSimElapsed = pElapsed; SimCallsThisInterval++;
            var m = MirrorManager.Instance;
            if (m == null || !m.IsFollower) return true; // king/idle: full simulation

            float dt = Time.fixedDeltaTime;
            Safe(_enemiesClear, null, null);
            Safe(_animUpdateTime, null, new object[] { pElapsed, dt });
            Safe(_mbUpdateMapLayers, __instance, new object[] { pElapsed });   // water/lava/anim layers
            var units = World.world != null ? World.world.units : null;
            if (units != null)
            {
                Safe(_unitsCheckContainer, units, null);                       // refresh actor batch
                Safe(_unitsPrepMeta, units, null);                            // build alive/wild/civ lists
                Safe(_unitsCalcVisible, units, null);                         // visibility (needed by update)
                Safe(_unitsUpdate, units, new object[] { pElapsed });          // movement interp + render + animation
                Safe(_unitsCheckDestroy, units, null);                        // finalize corpses (death)
            }
            // Real building batch update (sets is_visible via the updateVisibility job) so
            // buildings render in HIGH-RES, not just the low-res chunk texture. updateBuildings
            // itself is a debug-gated no-op, so call BuildingManager.update directly. Guarded
            // crash sites (updateVisibility/components/shake/stats) protect malformed buildings.
            try { var b = World.world?.buildings; if (b != null) Safe(_buildingsUpdate, b, new object[] { pElapsed }); } catch { }
            // Rebuild each dirty zone's buildings_render_list so high-res render finds buildings.
            try { _buildingZonesUpdate?.Invoke(null, null); } catch { }
            // Keep each meta-manager's `list` cache fresh. The game sim calls checkLists() (rebuilds
            // list from _hashset when _dirty_list); the passive sim skips it, so `list` stays stale/
            // empty on followers - and MANY systems iterate `list`: nameplate/banner rendering (city
            // + kingdom names on the map), stats, etc. checkLists() is cheap (early-returns unless
            // dirty). Without this, synced cities/kingdoms exist (get/_hashset) but have no map name.
            var w = World.world;
            if (w != null)
            {
                RefreshList(w.cities); RefreshList(w.kingdoms); RefreshList(w.cultures);
                RefreshList(w.languages); RefreshList(w.religions); RefreshList(w.clans);
                RefreshList(w.families); RefreshList(w.plots); RefreshList(w.alliances); RefreshList(w.wars);
            }
            // Recompute city status (population/families/housing) from member units on the follower.
            // Enumerate the manager itself (CoreSystemManager.GetEnumerator -> _hashset), NOT `list`.
            try
            {
                if (w?.cities is System.Collections.IEnumerable cities)
                    foreach (var c in cities) if (c != null) Safe(_cityStatusUpdate, c, null);
            }
            catch { }
            // Drops (nukes/bombs/meteors) + projectiles: run their managers' update so they FALL
            // and animate on the follower (otherwise they hang frozen mid-air). Their explosions'
            // world-changing side-effects are now neutralized: terraform (Terraform_Prefix) and
            // fire (Fire_Prefix) are blocked on followers, and damage is king-authoritative via the
            // resync. So only the visual fall + boom plays; the crater/fire/deaths come from sync.
            try { var dm = _dropMgrField?.GetValue(__instance); if (dm != null) _dropUpdate?.Invoke(dm, new object[] { pElapsed }); } catch { }
            try { var pm = _projMgrField?.GetValue(__instance); if (pm != null) _projUpdate?.Invoke(pm, new object[] { pElapsed }); } catch { }
            // Effects (clouds, explosion visuals, tile fx, hearts...): stack_effects.update
            // lives in the skipped updateSimulation, so without this the mirrored clouds and
            // every synced effect would freeze in place on followers.
            try { var se = _stackEffectsField?.GetValue(__instance); if (se != null) _stackEffectsUpdate?.Invoke(se, new object[] { pElapsed }); } catch { }

            if (!_passiveLogged) { _passiveLogged = true; ModLog.Info("[FOLLOWER] passive simulation engaged (actors+render at king's tick rate)."); }
            return false; // skip the full autonomous simulation
        }

        private static int _passiveErrs;
        private static void Safe(MethodInfo mi, object inst, object[] args)
        {
            if (mi == null) return;
            try { mi.Invoke(inst, args); }
            catch (Exception e) { if (_passiveErrs < 6) { _passiveErrs++; ModLog.Warn($"[FOLLOWER] passive {mi.Name} skipped: " + (e.InnerException ?? e).Message); } }
        }

        private static readonly System.Collections.Generic.Dictionary<Type, MethodInfo> _checkListsCache = new System.Collections.Generic.Dictionary<Type, MethodInfo>();
        private static readonly System.Collections.Generic.Dictionary<Type, MethodInfo> _beginChecksUnitsCache = new System.Collections.Generic.Dictionary<Type, MethodInfo>();
        // Rebuild a CoreSystemManager's caches that the passive sim would otherwise miss:
        //  • checkLists()       -> the lazy `list` cache from _hashset (list-based render: names).
        //  • beginChecksUnits() -> member `base.units` lists (clearAllUnitLists+updateDirtyUnits+
        //    finishDirtyUnits), gated by _dirty_units. Without it a city's a.city links exist but
        //    base.units is empty -> getPopulationPeople()=0 -> 0 population/families/residents.
        // Both early-return unless their dirty flag is set, so calling every frame is cheap.
        private static void RefreshList(object mgr)
        {
            if (mgr == null) return;
            var t = mgr.GetType();
            try
            {
                if (!_checkListsCache.TryGetValue(t, out var mi)) { mi = AccessTools.Method(t, "checkLists"); _checkListsCache[t] = mi; }
                mi?.Invoke(mgr, null);
            }
            catch { }
            try
            {
                if (!_beginChecksUnitsCache.TryGetValue(t, out var bu)) { bu = AccessTools.Method(t, "beginChecksUnits"); _beginChecksUnitsCache[t] = bu; }
                bu?.Invoke(mgr, null);
            }
            catch { }
        }

        // Stop the follower from autonomously constructing buildings (king is authoritative).
        // Allowed during world load (snapshot) so the load pipeline isn't broken.
        private static bool SuppressFollowerBuild_Prefix(ref Building __result)
        {
            var m = MirrorManager.Instance;
            if (m != null && m.IsFollower && !SmoothLoader.isLoading())
            {
                __result = null;
                if (_suppressedBuilds < 3) { _suppressedBuilds++; ModLog.Info("[FOLLOWER] suppressed autonomous addBuilding (king-authoritative)."); }
                return false;
            }
            return true;
        }

        // Skip building shake on followers when the building has no batch (uninitialized
        // mirrored building) - the shake code dereferences batch.rnd / batch.c_shake.
        private static bool BuildingUpdateShake_Prefix(Building __instance)
        {
            var m = MirrorManager.Instance;
            return !(m != null && m.IsFollower && __instance != null && __instance.batch == null);
        }

        // Skip component updates on followers for uninitialized shells (null components_list).
        private static bool BuildingUpdateComponents_Prefix(Building __instance)
        {
            var m = MirrorManager.Instance;
            return !(m != null && m.IsFollower && __instance != null && _buildingComponentsField?.GetValue(__instance) == null);
        }

        // Skip stats update on followers when asset is null (shell / mid-destruction building).
        private static bool BuildingUpdateStats_Prefix(Building __instance)
        {
            var m = MirrorManager.Instance;
            return !(m != null && m.IsFollower && __instance != null && _buildingAssetField?.GetValue(__instance) == null);
        }

        // Treat an asset-less mirrored building as "no housing" on followers instead of
        // NREing in asset.canBeOccupied() (hit from BuildingManager.destroyObject).
        private static bool BuildingHasHousing_Prefix(Building __instance, ref bool __result)
        {
            var m = MirrorManager.Instance;
            if (m != null && m.IsFollower && __instance != null && _buildingAssetField?.GetValue(__instance) == null)
            {
                __result = false;
                return false;
            }
            return true;
        }

        private static int _fillVisErr;
        private static Exception FillVisibleObjects_Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            var m = MirrorManager.Instance;
            if (m != null && m.IsFollower)
            {
                if (_fillVisErr < 3) { _fillVisErr++; ModLog.Warn("[FOLLOWER] fillVisibleObjects swallowed (null asset/current_tile actor?): " + __exception.Message); }
                return null;
            }
            return __exception;
        }

        private static int _bbErrLogged;
        // Finalizer: swallow building-visibility NREs ONLY on followers, and log the
        // first few so we can still find the root cause (null current_tile building).
        private static Exception BatchBuildingsVisibility_Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            var m = MirrorManager.Instance;
            if (m != null && m.IsFollower)
            {
                if (_bbErrLogged < 3)
                {
                    _bbErrLogged++;
                    ModLog.Warn("[FOLLOWER] BatchBuildings.updateVisibility swallowed (null-tile building?): " + __exception.Message);
                }
                return null; // suppress
            }
            return __exception; // king: surface it
        }
    }
}
