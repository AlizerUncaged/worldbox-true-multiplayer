#if DEBUG
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using WorldboxMP.Client.Mirror;

namespace WorldboxMP.Client.Dev
{
    /// <summary>
    /// Logs the factors that drive actor movement speed on BOTH king and follower, so we
    /// can diff them to find why follower movement is slow. Per-frame visual speed =
    /// ticks * _current_combined_movement_speed * pElapsed; _current_combined_movement_speed
    /// = base * SimGlobals.m.unit_speed_multiplier. Logs every ~2s to wbmp-client-&lt;pid&gt;.log.
    /// </summary>
    internal static class MoveDiag
    {
        private static float _t;
        private static Vector2 _lastPos;
        private static long _lastId = -1;
        private static FieldInfo _cmsField;
        private static FieldInfo _simMField;
        private static FieldInfo _unitMultField;

        public static void Tick(float dt)
        {
            _t += dt;
            if (_t < 2f) return;
            _t = 0f;
            try
            {
                var mm = MirrorManager.Instance;
                string role = mm == null ? "?" : (mm.IsKing ? "KING" : (mm.IsFollower ? "FOLLOWER" : "idle"));

                string speed = "?"; int ticks = -1;
                try { speed = Config.time_scale_asset.id; ticks = Config.time_scale_asset.ticks; } catch { }

                float mult = -1f;
                try
                {
                    _simMField = _simMField ?? AccessTools.Field(typeof(SimGlobals), "m");
                    var m = _simMField?.GetValue(null);
                    if (m != null)
                    {
                        _unitMultField = _unitMultField ?? AccessTools.Field(m.GetType(), "unit_speed_multiplier");
                        if (_unitMultField != null) mult = (float)_unitMultField.GetValue(m);
                    }
                }
                catch { }

                string actorInfo = "actors=0";
                var w = World.world;
                if (w != null && w.units != null)
                {
                    var list = w.units.getSimpleList();
                    Actor a = null;
                    for (int i = 0; i < list.Count; i++) if (list[i] != null && list[i].isAlive()) { a = list[i]; break; }
                    if (a != null)
                    {
                        float cms = -1f;
                        try { _cmsField = _cmsField ?? AccessTools.Field(typeof(Actor), "_current_combined_movement_speed"); cms = (float)_cmsField.GetValue(a); } catch { }
                        Vector2 pos = a.current_position;
                        float visSpeed = (_lastId == a.id) ? Vector2.Distance(pos, _lastPos) / 2f : -1f; // tiles/sec
                        _lastPos = pos; _lastId = a.id;
                        actorInfo = $"actors={list.Count} sample id={a.id} cms={cms:F2} visSpeed={visSpeed:F2}t/s moving={a.is_moving} pos=({pos.x:F1},{pos.y:F1})";
                    }
                }

                // Move/object flow over this interval (then reset).
                string flow = $"movesSent={MirrorManager.DbgMovesSent} recv={MirrorManager.DbgMovesRecv} applied={MirrorManager.DbgMovesApplied} " +
                              $"nullResolve={MirrorManager.DbgMovesNullResolve} noTile={MirrorManager.DbgMovesNoTile} dead={MirrorManager.DbgMovesDead} ex={MirrorManager.DbgMovesEx} " +
                              $"objSent={MirrorManager.DbgObjSent} objRecv={MirrorManager.DbgObjRecv}" +
                              (string.IsNullOrEmpty(MirrorManager.DbgLastMoveEx) ? "" : $" lastMoveEx={MirrorManager.DbgLastMoveEx}");
                MirrorManager.DbgMovesSent = MirrorManager.DbgMovesRecv = MirrorManager.DbgMovesApplied = 0;
                MirrorManager.DbgMovesNullResolve = MirrorManager.DbgMovesNoTile = MirrorManager.DbgMovesDead = MirrorManager.DbgMovesEx = 0;
                MirrorManager.DbgObjSent = MirrorManager.DbgObjRecv = 0; MirrorManager.DbgLastMoveEx = "";

                ModLog.Info($"[MOVEDIAG] role={role} speed={speed} ticks={ticks} unitMult={mult:F2} fixedDt={Time.fixedDeltaTime:F4} {actorInfo} | {flow}");
            }
            catch (Exception e) { ModLog.Warn("[MOVEDIAG] " + e.Message); }
        }
    }
}
#endif
