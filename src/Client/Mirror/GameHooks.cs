using System;
using HarmonyLib;
using UnityEngine;

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
                    harmony.Patch(clickedFinal, prefix: H(nameof(ClickedFinal_Prefix)));

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

                var ai = AccessTools.Method(typeof(Actor), "b6_updateAI");
                if (ai != null) harmony.Patch(ai, prefix: H(nameof(SuppressAi_Prefix)));

                ModLog.Info("Mirror game hooks applied.");
            }
            catch (Exception e) { ModLog.Error("Failed to apply game hooks: " + e); }
        }

        private static HarmonyMethod H(string name) => new HarmonyMethod(typeof(GameHooks), name);

        private static bool ClickedFinal_Prefix(Vector2Int pPos, GodPower pPower)
        {
            var m = MirrorManager.Instance;
            if (m == null || !m.Active) return true;
            if (m.ApplyingRemote) return true;
            return m.OnLocalGodPower(pPos, pPower);
        }

        private static void CreateNewUnit_Postfix(Actor __result, string pStatsID)
        {
            MirrorManager.Instance?.OnKingSpawned(__result, pStatsID);
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
    }
}
