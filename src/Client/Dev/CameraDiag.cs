using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace WorldboxMP.Client.Dev
{
    /// <summary>
    /// Diagnostic for the follower zoom/LOD bug. WorldBox switches the WHOLE map to
    /// low-res "bit-scale" rendering when <c>camera.orthographicSize &gt;
    /// QualityChanger.getZoomRateBoundLow()</c> (see MoveCamera / QualityChanger in
    /// Assembly-CSharp). So a follower stuck on simple/blocky sprites is simply stuck
    /// zoomed-out. This logs the exact camera state on every scroll-wheel input and on
    /// F10, so we can see whether scroll input reaches the follower at all and how the
    /// zoom values respond — instead of guessing. Lands in wbmp-client.log via ModLog.
    /// All game members are internal at runtime, so we go through reflection.
    /// </summary>
    internal static class CameraDiag
    {
        private static bool _init;
        private static FieldInfo _fiInstance;     // MoveCamera.instance (static)
        private static FieldInfo _fiMainCam;      // MoveCamera.main_camera
        private static FieldInfo _fiTargetZoom;   // MoveCamera._target_zoom
        private static FieldInfo _fiMaxZoom;      // MoveCamera.orthographic_size_max
        private static MethodInfo _miResetZoom;   // MoveCamera.resetZoom()
        private static FieldInfo _fiQuality;      // World.quality_changer
        private static MethodInfo _miIsLowRes;    // QualityChanger.isLowRes()
        private static MethodInfo _miBoundLow;    // QualityChanger.getZoomRateBoundLow()

        private static void Init()
        {
            if (_init) return;
            _init = true;
            try
            {
                var mc = AccessTools.TypeByName("MoveCamera");
                _fiInstance = AccessTools.Field(mc, "instance");
                _fiMainCam = AccessTools.Field(mc, "main_camera");
                _fiTargetZoom = AccessTools.Field(mc, "_target_zoom");
                _fiMaxZoom = AccessTools.Field(mc, "orthographic_size_max");
                _miResetZoom = AccessTools.Method(mc, "resetZoom");

                var world = AccessTools.TypeByName("World");
                _fiQuality = AccessTools.Field(world, "quality_changer");
                var qc = AccessTools.TypeByName("QualityChanger");
                _miIsLowRes = AccessTools.Method(qc, "isLowRes");
                _miBoundLow = AccessTools.Method(qc, "getZoomRateBoundLow");
            }
            catch (Exception e) { ModLog.Warn("[CAMDIAG] reflection init failed: " + e); }
        }

        /// <summary>Dump the current camera/zoom/LOD/selection state to the log.</summary>
        public static void Dump(string reason)
        {
            Init();
            try
            {
                object mcInst = _fiInstance?.GetValue(null);
                var cam = mcInst != null ? _fiMainCam?.GetValue(mcInst) as Camera : null;
                float ortho = cam != null ? cam.orthographicSize : -1f;
                float target = mcInst != null && _fiTargetZoom != null ? (float)_fiTargetZoom.GetValue(mcInst) : -1f;
                float max = mcInst != null && _fiMaxZoom != null ? (float)_fiMaxZoom.GetValue(mcInst) : -1f;

                object qc = _fiQuality?.GetValue(World.world);
                bool lowRes = qc != null && _miIsLowRes != null && (bool)_miIsLowRes.Invoke(qc, null);
                float boundLow = qc != null && _miBoundLow != null ? (float)_miBoundLow.Invoke(qc, null) : -1f;

                bool sel = false, focus = false;
                try { sel = World.world.player_control.isSelectionHappens(); } catch { }
                try { focus = (bool)(AccessTools.Method(AccessTools.TypeByName("MoveCamera"), "hasFocusUnit")?.Invoke(null, null) ?? false); } catch { }

                var mm = Mirror.MirrorManager.Instance;
                string role = mm == null ? "?" : (mm.IsKing ? "KING" : (mm.IsFollower ? "FOLLOWER" : "idle"));

                ModLog.Info(
                    $"[CAMDIAG] {reason} | role={role} ortho={ortho:F1} target={target:F1} max={max:F1} " +
                    $"lowResBound={boundLow:F1} lowRes={lowRes} mapW={MapBox.width} mapH={MapBox.height} " +
                    $"selection={sel} focusUnit={focus} screen={Screen.width}x{Screen.height} focused={Application.isFocused}");
            }
            catch (Exception e) { ModLog.Warn("[CAMDIAG] dump failed: " + e); }
        }

        /// <summary>Recompute the camera zoom bounds for the current map (internal resetZoom).</summary>
        public static void ResetZoom()
        {
            Init();
            try
            {
                object mcInst = _fiInstance?.GetValue(null);
                if (mcInst != null) _miResetZoom?.Invoke(mcInst, null);
                ModLog.Info("[CAMDIAG] resetZoom() invoked");
            }
            catch (Exception e) { ModLog.Warn("[CAMDIAG] resetZoom failed: " + e); }
        }
    }
}
