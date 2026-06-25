#if DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using WorldboxMP.Client.Net;
using WorldboxMP.Shared.Protocol;
using WorldboxMP.Client.Mirror;

namespace WorldboxMP.Client.Dev
{
    /// <summary>
    /// DEBUG-ONLY HTTP control bridge so the dev (Claude) can drive automated test loops:
    /// spawn units, create/join rooms, and read back authoritative state to compare king vs
    /// follower — without manually clicking the game. Each instance binds the first free port
    /// from 8750 and logs it. Unity APIs only run on the main thread, so every request is
    /// marshaled into NetPump.Update via a queue. Compiled out entirely in Release.
    ///
    /// Endpoints (GET): /state /spawn?asset&x&y&count /login?user&pass /createroom?name&pass
    ///                  /joinroom?id&pass /connect /speed?n /power?id&x&y
    /// </summary>
    internal static class DevRestServer
    {
        private static HttpListener _listener;
        private static Thread _thread;
        private static volatile bool _running;
        public static int Port { get; private set; }

        private class Job { public string Path; public Dictionary<string, string> Q; public string Result; public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false); }
        private static readonly Queue<Job> _jobs = new Queue<Job>();

        public static void Start()
        {
            if (_running) return;
            for (int p = 8750; p <= 8770; p++)
            {
                try
                {
                    var l = new HttpListener();
                    l.Prefixes.Add($"http://localhost:{p}/");
                    l.Start();
                    _listener = l; Port = p; break;
                }
                catch { /* port taken — try next */ }
            }
            if (_listener == null) { ModLog.Warn("[REST] no free port 8750-8770"); return; }
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "WBMP-REST" };
            _thread.Start();
            ModLog.Info($"[REST] control server on http://localhost:{Port}/  (DEBUG)");
        }

        private static void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; }
                try
                {
                    var job = new Job { Path = ctx.Request.Url.AbsolutePath.Trim('/').ToLowerInvariant(), Q = ParseQuery(ctx.Request.Url.Query) };
                    lock (_jobs) _jobs.Enqueue(job);
                    if (!job.Done.Wait(8000)) job.Result = "{\"error\":\"timeout (main thread busy?)\"}";
                    var bytes = Encoding.UTF8.GetBytes(job.Result ?? "{}");
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    ctx.Response.OutputStream.Close();
                }
                catch { try { ctx.Response.Abort(); } catch { } }
            }
        }

        // Called on the MAIN thread from NetPump.Update — runs queued jobs against Unity/game state.
        public static void Drain()
        {
            if (!_running) return;
            for (int i = 0; i < 16; i++)
            {
                Job job = null;
                lock (_jobs) { if (_jobs.Count > 0) job = _jobs.Dequeue(); }
                if (job == null) break;
                try { job.Result = Handle(job.Path, job.Q); }
                catch (Exception e) { job.Result = "{\"error\":\"" + Esc(e.Message) + "\"}"; }
                finally { job.Done.Set(); }
            }
        }

        private static string Handle(string path, Dictionary<string, string> q)
        {
            var net = NetClient.Instance;
            switch (path)
            {
                case "state": return State();
                case "connect": net?.Connect(); return Ok("connecting");
                case "login": net?.Login(Get(q, "user", "testp1"), Get(q, "pass", Get(q, "user", "testp1"))); return Ok("login sent");
                case "createroom": net?.CreateRoom(Get(q, "name", "TEST"), Get(q, "pass", ""), RoomVisibility.Unlisted); return Ok("createroom sent");
                case "joinroom": net?.JoinRoom(Get(q, "id", "").ToUpperInvariant(), Get(q, "pass", "")); return Ok("joinroom sent");
                case "spawn": return Spawn(q);
                case "speed": return Speed(q);
                case "pause": try { Config.paused = GetI(q, "on", 1) != 0; } catch { } return Ok("paused " + Get(q, "on", "1"));
                case "age":
                    try
                    {
                        var ms = AccessTools.Field(typeof(MapBox), "map_stats")?.GetValue(World.world) as MapStats;
                        if (ms != null) ms.world_age_id = Get(q, "id", "age_spawn");
                        var era = AccessTools.Field(typeof(MapBox), "era_manager")?.GetValue(World.world);
                        AccessTools.Method(era?.GetType(), "loadAge")?.Invoke(era, null);
                    }
                    catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
                    return Ok("age id " + Get(q, "id", "age_spawn"));
                case "power": return Power(q);
                case "model3d": try { var inst = WorldboxMP.Client.UI.Model3D.Instance; if (inst == null) return "{\"error\":\"Model3D.Instance null (Spawn didn't run)\"}"; if (Get(q, "act", "show") == "hide") inst.Teardown(); else inst.Activate(); } catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; } return Ok("model3d " + Get(q, "act", "show"));
                case "mpwindow": try { if (Get(q, "act", "open") == "open") WorldboxMP.Client.UI.MultiplayerWindow.Open(); else WorldboxMP.Client.UI.MultiplayerWindow.Prewarm(); } catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; } return Ok("mpwindow " + Get(q, "act", "open"));
                default: return "{\"error\":\"unknown endpoint\"}";
            }
        }

        private static string Spawn(Dictionary<string, string> q)
        {
            string asset = Get(q, "asset", "human");
            int x = GetI(q, "x", MapBox.width / 2), y = GetI(q, "y", MapBox.height / 2), count = Math.Max(1, GetI(q, "count", 1));
            var tile = World.world?.GetTile(x, y);
            if (tile == null) return "{\"error\":\"null tile\"}";
            var ids = new List<long>();
            for (int i = 0; i < count; i++) { var a = World.world.units.createNewUnit(asset, tile, pAdultAge: true); if (a != null) ids.Add(a.id); }
            return "{\"spawned\":[" + string.Join(",", ids.ConvertAll(v => v.ToString())) + "],\"at\":[" + x + "," + y + "]}";
        }

        private static string Speed(Dictionary<string, string> q)
        {
            int n = GetI(q, "n", 1);
            // Game speed ids are "x1".."x8" via Config.setWorldSpeed(string,bool).
            try { if (n <= 0) Config.paused = true; else { Config.paused = false; Config.setWorldSpeed("x" + n, false); } } catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
            return Ok("speed x" + n);
        }

        private static string Power(Dictionary<string, string> q)
        {
            string id = Get(q, "id", ""); int x = GetI(q, "x", 0), y = GetI(q, "y", 0);
            var power = AssetManager.powers.get(id);
            if (power == null) return "{\"error\":\"no power " + Esc(id) + "\"}";
            try { AccessTools.Method(typeof(PlayerControl), "clickedFinal", new[] { typeof(Vector2Int), typeof(GodPower), typeof(bool) })?.Invoke(World.world.player_control, new object[] { new Vector2Int(x, y), power, false }); }
            catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
            return Ok("power " + id);
        }

        private static string State()
        {
            var mm = MirrorManager.Instance; var net = NetClient.Instance;
            string role = mm == null ? "idle" : (mm.IsKing ? "king" : (mm.IsFollower ? "follower" : "idle"));
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append($"\"role\":\"{role}\",\"connState\":\"{net?.State}\",\"loggedIn\":{(net != null && net.LoggedIn ? "true" : "false")},");
            sb.Append($"\"roomId\":\"{Esc(net?.RoomId ?? "")}\",\"inRoom\":{(net != null && net.InRoom ? "true" : "false")},\"isKing\":{(net != null && net.IsKing ? "true" : "false")},");
            int actors = -1; try { actors = World.world.units.getSimpleList().Count; } catch { }
            bool paused = false; string speed = "?";
            try { paused = Config.paused; } catch { }
            try { speed = Config.time_scale_asset != null ? Config.time_scale_asset.id : "?"; } catch { }
            // MapBox.map_stats is internal at runtime (publicized-DLL gotcha) — reach it by reflection.
            string age = "?"; try { var ms = AccessTools.Field(typeof(MapBox), "map_stats")?.GetValue(World.world) as MapStats; if (ms != null) age = ms.world_age_id; } catch { }
            bool winActive = false; try { winActive = ScrollWindow.isWindowActive(); } catch { }
            int sapient = 0, mobs = 0;
            try { var l = World.world.units.getSimpleList(); for (int i = 0; i < l.Count; i++) { var a = l[i]; if (a == null || !a.isAlive()) continue; if (a.isSapient()) sapient++; else mobs++; } } catch { }
            sb.Append($"\"actors\":{actors},\"sapient\":{sapient},\"mobs\":{mobs},\"paused\":{(paused ? "true" : "false")},\"speed\":\"{Esc(speed)}\",\"winActive\":{(winActive ? "true" : "false")},\"kingPaused\":{(MirrorManager.KingPaused ? "true" : "false")},\"age\":\"{Esc(age)}\",");
            // cities (enumerate _hashset, not stale list)
            sb.Append("\"cities\":[");
            int cc = 0;
            try
            {
                if (World.world?.cities is IEnumerable cities)
                    foreach (var c in cities)
                    {
                        if (c == null) continue;
                        long id = -1, lid = -1; string name = "?"; int pop = -1, zones = -1;
                        try { id = Convert.ToInt64(AccessTools.PropertyGetter(c.GetType(), "id")?.Invoke(c, null) ?? -1L); } catch { }
                        try { name = (string)AccessTools.PropertyGetter(c.GetType(), "name")?.Invoke(c, null) ?? "?"; } catch { }
                        try { pop = Convert.ToInt32(AccessTools.Method(c.GetType(), "getPopulationPeople")?.Invoke(c, null) ?? -1); } catch { }
                        try { zones = (AccessTools.Field(c.GetType(), "zones")?.GetValue(c) as ICollection)?.Count ?? -1; } catch { }
                        try { var d = AccessTools.Field(c.GetType(), "data")?.GetValue(c); lid = d != null ? Convert.ToInt64(AccessTools.PropertyGetter(d.GetType(), "leaderID")?.Invoke(d, null) ?? -1L) : -1; } catch { }
                        if (cc++ > 0) sb.Append(",");
                        sb.Append($"{{\"id\":{id},\"name\":\"{Esc(name)}\",\"pop\":{pop},\"zones\":{zones},\"leaderID\":{lid}}}");
                    }
            }
            catch { }
            sb.Append("],");
            // sample alive actor speed/health
            try
            {
                var list = World.world.units.getSimpleList();
                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[i]; if (a == null || !a.isAlive()) continue;
                    float spd = -1, cms = -1; try { var so = AccessTools.Field(typeof(Actor), "stats")?.GetValue(a); var idx = so?.GetType().GetMethod("get_Item", new[] { typeof(string) }); if (idx != null) spd = (float)idx.Invoke(so, new object[] { "speed" }); } catch { }
                    try { cms = (float)AccessTools.Field(typeof(Actor), "_current_combined_movement_speed").GetValue(a); } catch { }
                    sb.Append($"\"sample\":{{\"id\":{a.id},\"asset\":\"{Esc(a.asset?.id ?? "")}\",\"speed\":{spd.ToString("F2", CultureInfo.InvariantCulture)},\"cms\":{cms.ToString("F2", CultureInfo.InvariantCulture)},\"hp\":{a.getHealth()},\"maxhp\":{a.getMaxHealth()},\"moving\":{(a.is_moving ? "true" : "false")}}}");
                    break;
                }
            }
            catch { }
            if (!sb.ToString().Contains("\"sample\"")) sb.Append("\"sample\":null");
            sb.Append("}");
            return sb.ToString();
        }

        private static string Ok(string m) => "{\"ok\":\"" + Esc(m) + "\"}";
        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        private static string Get(Dictionary<string, string> q, string k, string def) => q != null && q.TryGetValue(k, out var v) ? v : def;
        private static int GetI(Dictionary<string, string> q, string k, int def) => int.TryParse(Get(q, k, ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : def;

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var d = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(query)) return d;
            foreach (var pair in query.TrimStart('?').Split('&'))
            {
                if (pair.Length == 0) continue;
                var kv = pair.Split('=');
                d[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            }
            return d;
        }
    }
}
#endif
