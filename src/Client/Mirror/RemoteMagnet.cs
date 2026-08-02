using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Client.Mirror
{
    /// <summary>
    /// King-side replay of a follower's magnet drag. The game's own Magnet reads the
    /// LOCAL mouse (both position and button state) inside its state machine, so it
    /// cannot be driven remotely - this replicates its pickup/carry/drop behavior with
    /// the same actor APIs, one session per dragging player. A session ends (units
    /// drop) after a short silence, so no explicit release message is needed and a
    /// disconnecting dragger can never leave units floating.
    ///
    /// Several Actor members the native Magnet touches are INTERNAL (is_in_magnet,
    /// dirty_current_tile, spawnOn, findCurrentTile, addStatusEffect) - direct IL
    /// references throw FieldAccessException when the method JITs (uncatchable from
    /// inside the method), so those go through cached reflection like the rest of the
    /// mod's game-internal access.
    /// </summary>
    internal static class RemoteMagnet
    {
        private const float DropAfterSilenceSeconds = 0.45f;
        private const int PickupRadius = 3;          // tiles, roughly the default magnet brush
        private const int MaxUnitsPerSession = 80;   // sanity cap per dragging player

        private static readonly FieldInfo _isInMagnet = AccessTools.Field(typeof(Actor), "is_in_magnet");
        private static readonly FieldInfo _dirtyTile = AccessTools.Field(typeof(Actor), "dirty_current_tile");
        private static readonly MethodInfo _spawnOn = AccessTools.Method(typeof(Actor), "spawnOn", new[] { typeof(WorldTile), typeof(float) });
        private static readonly MethodInfo _findTile = AccessTools.Method(typeof(Actor), "findCurrentTile", new[] { typeof(bool) });
        private static readonly MethodInfo _addStatus = AccessTools.Method(typeof(Actor), "addStatusEffect", new[] { typeof(string), typeof(float), typeof(bool) });

        private class Session
        {
            public readonly List<Actor> Units = new List<Actor>();
            public readonly HashSet<Actor> Seen = new HashSet<Actor>();
            public Vector2 Target;
            public float LastMsgTime;
            public float Angle;
        }

        private static readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>();

        /// <summary>A MagnetDrag mirror arrived from a follower: extend/refresh their session.</summary>
        public static void OnDrag(string senderId4, GodPowerMsg m)
        {
            if (string.IsNullOrEmpty(senderId4) || World.world == null) return;
            var tile = World.world.GetTile(m.X, m.Y);
            if (tile == null) return;
            if (!_sessions.TryGetValue(senderId4, out var s))
            {
                _sessions[senderId4] = s = new Session();
                ModLog.Info($"[MAGNET] remote drag session started by {senderId4} at ({m.X},{m.Y}).");
            }
            s.Target = new Vector2(tile.pos.x + 0.5f, tile.pos.y + 0.5f);
            s.LastMsgTime = Time.unscaledTime;
            int before = s.Units.Count;
            Pickup(s, tile);
            if (s.Units.Count != before)
                ModLog.Info($"[MAGNET] {senderId4} now carrying {s.Units.Count} units.");
        }

        // Same pickup rules as Magnet.pickupUnits, over a fixed circular radius.
        private static void Pickup(Session s, WorldTile center)
        {
            if (s.Units.Count >= MaxUnitsPerSession) return;
            for (int dx = -PickupRadius; dx <= PickupRadius; dx++)
                for (int dy = -PickupRadius; dy <= PickupRadius; dy++)
                {
                    if (dx * dx + dy * dy > PickupRadius * PickupRadius) continue;
                    var tile = World.world.GetTile(center.pos.x + dx, center.pos.y + dy);
                    if (tile == null || !tile.hasUnits()) continue;
                    tile.doUnits(a =>
                    {
                        if (s.Units.Count >= MaxUnitsPerSession || a == null || !a.isAlive()
                            || !a.asset.can_be_moved_by_powers || a.isInsideSomething() || !s.Seen.Add(a))
                            return;
                        try
                        {
                            a.cancelAllBeh();
                            _isInMagnet?.SetValue(a, true);
                            s.Units.Add(a);   // last, so a failure never leaves a phantom entry
                        }
                        catch { s.Seen.Remove(a); }
                    });
                }
        }

        /// <summary>Pumped every frame on the king: carry held units, drop silent sessions.</summary>
        public static void Update()
        {
            if (_sessions.Count == 0) return;
            float now = Time.unscaledTime;
            List<string> done = null;
            foreach (var kv in _sessions)
            {
                if (now - kv.Value.LastMsgTime > DropAfterSilenceSeconds)
                {
                    Drop(kv.Value);
                    (done ?? (done = new List<string>())).Add(kv.Key);
                }
                else Carry(kv.Value);
            }
            if (done != null) foreach (var k in done) _sessions.Remove(k);
        }

        // Same carry placement idea as Magnet.updatePickedUnits: a slowly rotating ring
        // around the drag target (the followers see it through the position stream).
        private static void Carry(Session s)
        {
            int n = s.Units.Count;
            if (n == 0) return;
            s.Angle += 1.5f * Time.deltaTime;
            float ring = 0.5f + 0.06f * n;
            for (int i = 0; i < n; i++)
            {
                var a = s.Units[i];
                if (a == null || !a.isAlive()) continue;
                try
                {
                    _findTile?.Invoke(a, new object[] { true });
                    float px = s.Target.x + Mathf.Cos(s.Angle + i) * (ring * i / n);
                    float py = s.Target.y + Mathf.Sin(s.Angle + i) * (ring * i / n);
                    a.current_position = new Vector2(px, py - a.position_height);
                    a.callbacks_magnet_update?.Invoke(a);
                }
                catch { }
            }
        }

        // Mirrors Magnet.dropPickedUnits without the mouse-velocity throw.
        private static void Drop(Session s)
        {
            if (s.Units.Count > 0)
                ModLog.Info($"[MAGNET] dropping {s.Units.Count} units at ({s.Target.x:F1},{s.Target.y:F1}).");
            for (int i = 0; i < s.Units.Count; i++)
            {
                var a = s.Units[i];
                if (a == null || !a.isAlive()) continue;
                try
                {
                    a.current_position.y -= a.position_height;
                    _isInMagnet?.SetValue(a, false);
                    _dirtyTile?.SetValue(a, true);
                    _findTile?.Invoke(a, new object[] { true });
                    _spawnOn?.Invoke(a, new object[] { a.current_tile, a.getActorAsset().default_height });
                    a.makeStunned(1f);
                    _addStatus?.Invoke(a, new object[] { "magnetized", 0f, true });
                    a.addForce(0f, 0f, 0.1f, pCheckLandCancelAllActions: true);
                    a.addActionWaitAfterLand(0.5f);
                }
                catch { }
            }
            s.Units.Clear();
            s.Seen.Clear();
        }

        /// <summary>Drop everything (room left / reset / world change).</summary>
        public static void Clear()
        {
            foreach (var kv in _sessions) Drop(kv.Value);
            _sessions.Clear();
        }
    }
}
