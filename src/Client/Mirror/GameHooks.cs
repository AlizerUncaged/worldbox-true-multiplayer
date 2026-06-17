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
    ///  • PlayerControl.clickedFinal  — capture/defer local god-power actions.
    ///  • ActorManager.spawnNewUnit   — king broadcasts authoritative spawns.
    ///  • MapAction.terraformTile     — king broadcasts terrain/biome/block changes.
    ///  • Actor.b6_updateAI           — suppress follower AI *decisions* only.
    ///
    /// We deliberately do NOT suppress movement/animation/render on followers — the
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

                // createNewUnit is the core spawn funnel — catches player spawns AND
                // births/reproduction/mutations (which all route through it).
                var spawn = AccessTools.Method(typeof(ActorManager), "createNewUnit");
                if (spawn != null)
                    harmony.Patch(spawn, postfix: H(nameof(CreateNewUnit_Postfix)));

                var addTrait = AccessTools.Method(typeof(Actor), "addTrait", new[] { typeof(string), typeof(bool) });
                if (addTrait != null) harmony.Patch(addTrait, postfix: H(nameof(AddTrait_Postfix)));
                var removeTrait = AccessTools.Method(typeof(Actor), "removeTrait", new[] { typeof(string) });
                if (removeTrait != null) harmony.Patch(removeTrait, postfix: H(nameof(RemoveTrait_Postfix)));

                var terraform = AccessTools.Method(typeof(MapAction), "terraformTile");
                if (terraform != null)
                    harmony.Patch(terraform, postfix: H(nameof(Terraform_Postfix)));

                var die = AccessTools.Method(typeof(Actor), "die");
                if (die != null) harmony.Patch(die, postfix: H(nameof(Die_Postfix)));

                var addFire = AccessTools.Method(typeof(WorldBehaviourActionFire), "addFire");
                if (addFire != null) harmony.Patch(addFire, postfix: H(nameof(AddFire_Postfix)));
                var removeFire = AccessTools.Method(typeof(WorldBehaviourActionFire), "removeFire");
                if (removeFire != null) harmony.Patch(removeFire, postfix: H(nameof(RemoveFire_Postfix)));

                // moveTo is the per-tile step commit — THE event for movement mirroring.
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

                // Combat / health.
                var getHit = AccessTools.Method(typeof(Actor), "getHit");
                if (getHit != null) harmony.Patch(getHit, postfix: H(nameof(GetHit_Postfix)));

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
                PatchCreate(harmony, typeof(BuildingManager), "addBuilding", typeof(Building));

                // Structural object destruction (removeObject) — one postfix for all.
                PatchRemove(harmony, typeof(KingdomManager), typeof(Kingdom));
                PatchRemove(harmony, typeof(CityManager), typeof(City));
                PatchRemove(harmony, typeof(WarManager), typeof(War));
                PatchRemove(harmony, typeof(AllianceManager), typeof(Alliance));
                PatchRemove(harmony, typeof(PlotManager), typeof(Plot));
                PatchRemove(harmony, typeof(ReligionManager), typeof(Religion));
                PatchRemove(harmony, typeof(CultureManager), typeof(Culture));
                PatchRemove(harmony, typeof(LanguageManager), typeof(Language));
                PatchRemove(harmony, typeof(BuildingManager), typeof(Building));

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

                // Defensive: on followers, the mirrored world can briefly hold a building
                // with a null current_tile (e.g. created via loadObject before its tile is
                // linked). BatchBuildings.updateVisibility then NREs every sim tick inside
                // a parallel job — an exception storm that stalls the whole update loop
                // (kills input/zoom/spawn). Swallow it on followers so the client stays
                // responsive; the king is authoritative and must NOT mask real bugs.
                var bbVis = AccessTools.Method(typeof(BatchBuildings), "updateVisibility");
                if (bbVis != null) harmony.Patch(bbVis, finalizer: H(nameof(BatchBuildingsVisibility_Finalizer)));

                // The mirrored world can contain buildings that aren't fully initialized
                // on a follower (null asset/batch/current_tile). The follower's own sim
                // then NREs on them in several places. Guard each crash site on followers
                // (skip the op for that one building) so the sim loop survives. Building.asset
                // is internal at runtime -> reflection.
                _buildingAssetField = AccessTools.Field(typeof(Building), "asset");
                var shake = AccessTools.Method(typeof(Building), "updateShake", new[] { typeof(float) });
                if (shake != null) harmony.Patch(shake, prefix: H(nameof(BuildingUpdateShake_Prefix)));
                var housing = AccessTools.Method(typeof(Building), "hasHousingLogic");
                if (housing != null) harmony.Patch(housing, prefix: H(nameof(BuildingHasHousing_Prefix)));

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

        private static void StructuralCreate_Postfix(object __result)
        {
            MirrorManager.Instance?.OnKingObject(__result);
        }

        private static void StructuralRemove_Postfix(object[] __args)
        {
            if (__args != null && __args.Length > 0) MirrorManager.Instance?.OnKingObjectRemove(__args[0]);
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

        private static void Terraform_Postfix(WorldTile pTile, TileType pNewTypeMain, TopTileType pTopType)
        {
            MirrorManager.Instance?.OnKingTerraform(pTile, pNewTypeMain, pTopType);
        }

        private static void Die_Postfix(Actor __instance)
        {
            MirrorManager.Instance?.OnKingDeath(__instance);
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

        private static FieldInfo _buildingAssetField;

        // Skip building shake on followers when the building has no batch (uninitialized
        // mirrored building) — the shake code dereferences batch.rnd / batch.c_shake.
        private static bool BuildingUpdateShake_Prefix(Building __instance)
        {
            var m = MirrorManager.Instance;
            return !(m != null && m.IsFollower && __instance != null && __instance.batch == null);
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
