using System;
using HarmonyLib;
using UnityEngine;

namespace WorldboxMP.Client.Mirror
{
    /// <summary>
    /// Harmony patches that drive mirroring:
    ///  • PlayerControl.clickedFinal  — capture/defer local god-power actions.
    ///  • ActorManager.spawnNewUnit   — king broadcasts authoritative spawns.
    ///  • Actor.b6_updateAI / u10_checkSmoothMovement — suppress follower simulation.
    /// All are patched manually because clickedFinal is internal.
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

                var spawn = AccessTools.Method(typeof(ActorManager), "spawnNewUnit");
                if (spawn != null)
                    harmony.Patch(spawn, postfix: H(nameof(SpawnNewUnit_Postfix)));

                var ai = AccessTools.Method(typeof(Actor), "b6_updateAI");
                if (ai != null) harmony.Patch(ai, prefix: H(nameof(SuppressOnFollower_Prefix)));

                var move = AccessTools.Method(typeof(Actor), "u10_checkSmoothMovement");
                if (move != null) harmony.Patch(move, prefix: H(nameof(SuppressOnFollower_Prefix)));

                ModLog.Info("Mirror game hooks applied.");
            }
            catch (Exception e) { ModLog.Error("Failed to apply game hooks: " + e); }
        }

        private static HarmonyMethod H(string name) => new HarmonyMethod(typeof(GameHooks), name);

        // Returns false to skip the original method.
        private static bool ClickedFinal_Prefix(Vector2Int pPos, GodPower pPower)
        {
            var m = MirrorManager.Instance;
            if (m == null || !m.Active) return true;     // singleplayer → normal
            if (m.ApplyingRemote) return true;           // applying a remote action → run it
            return m.OnLocalGodPower(pPos, pPower);      // send; king applies, follower defers
        }

        private static void SpawnNewUnit_Postfix(Actor __result, string pActorAssetID)
        {
            MirrorManager.Instance?.OnKingSpawned(__result, pActorAssetID);
        }

        // Suppress local AI + movement on followers (the king is authoritative).
        private static bool SuppressOnFollower_Prefix()
        {
            var m = MirrorManager.Instance;
            return m == null || !m.IsFollower;           // false on follower → skip simulation
        }
    }
}
