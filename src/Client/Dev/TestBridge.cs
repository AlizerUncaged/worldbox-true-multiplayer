#if DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using WorldboxMP.Client.Mirror;

namespace WorldboxMP.Client.Dev
{
    /// <summary>
    /// File-driven remote control so a developer can drive automated sync tests without
    /// manually clicking the game. Both instances poll a SHARED command file every second;
    /// each line is "&lt;id&gt;|&lt;role&gt;|&lt;cmd&gt;|&lt;args...&gt;" (role = king|follower|all). New ids
    /// (by full-line content) run once per instance. State is written to a per-role file the dev
    /// reads back to compare king vs follower programmatically.
    ///
    ///   cmd file : %LocalLow%/mkarpenko/WorldBox/wbmp-test-cmd.txt   (dev writes)
    ///   state    : %LocalLow%/mkarpenko/WorldBox/wbmp-test-state-&lt;role&gt;.txt (mod writes)
    ///
    /// Commands: dump | spawn|&lt;asset&gt;|&lt;x&gt;|&lt;y&gt;[|&lt;count&gt;] | speed|&lt;0-5&gt; | law|&lt;id&gt;|&lt;on&gt; | clearcmd
    /// </summary>
    internal static class TestBridge
    {
        private static float _t;
        private static string _cmdPath, _dir;
        private static readonly HashSet<string> _done = new HashSet<string>();
        private static bool _pathsResolved;

        private static string Role()
        {
            var mm = MirrorManager.Instance;
            return mm == null ? "idle" : (mm.IsKing ? "king" : (mm.IsFollower ? "follower" : "idle"));
        }

        public static void Tick(float dt)
        {
            _t += dt;
            if (_t < 1f) return;
            _t = 0f;
            try
            {
                if (!_pathsResolved)
                {
                    _dir = Application.persistentDataPath;
                    _cmdPath = Path.Combine(_dir, "wbmp-test-cmd.txt");
                    _pathsResolved = true;
                    ModLog.Info("[TESTBRIDGE] watching " + _cmdPath);
                }
                if (!File.Exists(_cmdPath)) return;
                string[] lines;
                try { lines = File.ReadAllLines(_cmdPath); } catch { return; } // may be mid-write
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || _done.Contains(line)) continue;
                    _done.Add(line);
                    Execute(line);
                }
            }
            catch (Exception e) { ModLog.Warn("[TESTBRIDGE] " + e.Message); }
        }

        private static void Execute(string line)
        {
            // <id>|<role>|<cmd>|<args...>
            var p = line.Split('|');
            if (p.Length < 3) return;
            string targetRole = p[1].Trim().ToLowerInvariant();
            string role = Role();
            if (targetRole != "all" && targetRole != role) return;
            string cmd = p[2].Trim().ToLowerInvariant();
            ModLog.Info($"[TESTBRIDGE] ({role}) exec: {line}");
            try
            {
                switch (cmd)
                {
                    case "dump": DumpState(); break;
                    case "spawn": DoSpawn(p); break;
                    case "speed": DoSpeed(p); break;
                    case "law": DoLaw(p); break;
                }
            }
            catch (Exception e) { ModLog.Warn($"[TESTBRIDGE] cmd '{cmd}' failed: " + e.Message); }
        }

        private static int I(string s, int def = 0) => int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : def;

        private static void DoSpawn(string[] p)
        {
            // spawn|<asset>|<x>|<y>[|<count>]
            string asset = p.Length > 3 ? p[3].Trim() : "human";
            int x = p.Length > 4 ? I(p[4]) : MapBox.width / 2;
            int y = p.Length > 5 ? I(p[5]) : MapBox.height / 2;
            int count = p.Length > 6 ? Math.Max(1, I(p[6], 1)) : 1;
            var tile = World.world?.GetTile(x, y);
            if (tile == null) { ModLog.Warn($"[TESTBRIDGE] spawn: tile ({x},{y}) null"); return; }
            for (int i = 0; i < count; i++)
            {
                var a = World.world.units.createNewUnit(asset, tile, pAdultAge: true);
                ModLog.Info($"[TESTBRIDGE] spawned {asset} id={(a != null ? a.id.ToString() : "null")} at ({x},{y})");
            }
        }

        private static void DoSpeed(string[] p)
        {
            int s = p.Length > 3 ? I(p[3], 1) : 1;
            // Map 0..5 to the game's time-scale assets if available; fall back to Config.paused.
            try
            {
                if (s <= 0) { Config.paused = true; return; }
                Config.paused = false;
                var setSpeed = AccessTools.Method(typeof(World), "setWorldSpeed", new[] { typeof(int) })
                               ?? AccessTools.Method(typeof(MapBox), "setWorldSpeed", new[] { typeof(int) });
                setSpeed?.Invoke(World.world, new object[] { s });
            }
            catch (Exception e) { ModLog.Warn("[TESTBRIDGE] speed: " + e.Message); }
        }

        private static void DoLaw(string[] p)
        {
            // law|<fieldName>|<on>  e.g. law|world_law_aging|1  (best-effort via WorldLawLibrary)
            if (p.Length < 5) return;
            string lawField = p[3].Trim();
            bool on = I(p[4], 1) != 0;
            try
            {
                var f = AccessTools.Field(typeof(WorldLawLibrary), lawField);
                var law = f?.GetValue(null);
                var setEnabled = law != null ? AccessTools.Method(law.GetType(), "setEnabled", new[] { typeof(bool) }) : null;
                setEnabled?.Invoke(law, new object[] { on });
                ModLog.Info($"[TESTBRIDGE] law {lawField}={on}");
            }
            catch (Exception e) { ModLog.Warn("[TESTBRIDGE] law: " + e.Message); }
        }

        private static void DumpState()
        {
            var sb = new StringBuilder();
            string role = Role();
            sb.AppendLine($"role={role} time={Time.frameCount}");
            var w = World.world;
            int actors = -1;
            try { actors = w.units.getSimpleList().Count; } catch { }
            sb.AppendLine($"actors={actors}");

            // Cities (enumerate _hashset via IEnumerable, not the stale list).
            int cityCount = 0;
            try
            {
                if (w.cities is IEnumerable cities)
                {
                    foreach (var c in cities)
                    {
                        if (c == null) continue;
                        cityCount++;
                        long id = -1; string name = "?"; int pop = -1, zones = -1; long leaderId = -1;
                        try { id = Convert.ToInt64(AccessTools.PropertyGetter(c.GetType(), "id")?.Invoke(c, null) ?? -1L); } catch { }
                        try { name = (string)AccessTools.PropertyGetter(c.GetType(), "name")?.Invoke(c, null) ?? "?"; } catch { }
                        try { pop = Convert.ToInt32(AccessTools.Method(c.GetType(), "getPopulationPeople")?.Invoke(c, null) ?? -1); } catch { }
                        try { zones = (AccessTools.Field(c.GetType(), "zones")?.GetValue(c) as ICollection)?.Count ?? -1; } catch { }
                        try { var d = AccessTools.Field(c.GetType(), "data")?.GetValue(c); leaderId = d != null ? Convert.ToInt64(AccessTools.PropertyGetter(d.GetType(), "leaderID")?.Invoke(d, null) ?? -1L) : -1; } catch { }
                        sb.AppendLine($"city id={id} name='{name}' pop={pop} zones={zones} leaderID={leaderId}");
                    }
                }
            }
            catch (Exception e) { sb.AppendLine("cities ERR " + e.Message); }
            sb.AppendLine($"cityCount={cityCount}");

            // A sample alive actor's movement stats (to compare speed king vs follower).
            try
            {
                var list = w.units.getSimpleList();
                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[i];
                    if (a == null || !a.isAlive()) continue;
                    float spd = -1, cms = -1; bool sub = false;
                    try { var sf = AccessTools.Field(typeof(Actor), "stats"); var so = sf?.GetValue(a); var idx = so?.GetType().GetMethod("get_Item", new[] { typeof(string) }); if (idx != null) spd = (float)idx.Invoke(so, new object[] { "speed" }); } catch { }
                    try { cms = (float)AccessTools.Field(typeof(Actor), "_current_combined_movement_speed").GetValue(a); } catch { }
                    try { sub = a.hasSubspecies(); } catch { }
                    sb.AppendLine($"sampleActor id={a.id} asset={a.asset?.id} speed={spd:F2} cms={cms:F2} hasSub={sub} health={a.getHealth()}/{a.getMaxHealth()} moving={a.is_moving}");
                    break;
                }
            }
            catch (Exception e) { sb.AppendLine("sampleActor ERR " + e.Message); }

            try
            {
                var path = Path.Combine(_dir, $"wbmp-test-state-{role}.txt");
                File.WriteAllText(path, sb.ToString());
                ModLog.Info($"[TESTBRIDGE] dumped state -> {path}");
            }
            catch (Exception e) { ModLog.Warn("[TESTBRIDGE] dump write: " + e.Message); }
        }
    }
}
#endif
