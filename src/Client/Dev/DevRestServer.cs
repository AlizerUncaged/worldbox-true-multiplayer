#if DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using WorldboxMP.Client.Net;
using WorldboxMP.Shared.Protocol;
using WorldboxMP.Client.Mirror;

namespace WorldboxMP.Client.Dev
{
    /// <summary>
    /// DEBUG-ONLY HTTP control bridge so a developer can drive automated test loops:
    /// spawn units, create/join rooms, and read back authoritative state to compare king vs
    /// follower - without manually clicking the game. Each instance binds the first free port
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
                catch { /* port taken - try next */ }
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

        // Called on the MAIN thread from NetPump.Update - runs queued jobs against Unity/game state.
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
                case "console":
                    // The game's own log overlay pops up on any error (two instances fight over
                    // stats.s3db) and covers the UI in screenshots.
                    try { foreach (var c in Resources.FindObjectsOfTypeAll<global::WorldBoxConsole.Console>()) c.Hide(); }
                    catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
                    return Ok("console hidden");
                case "scroll":
                    try
                    {
                        var w = WorldboxMP.Client.UI.MultiplayerWindow.Instance;
                        ScrollRect sr0 = null;
                        for (var t = w != null ? w.transform : null; t != null && sr0 == null; t = t.parent) sr0 = t.GetComponent<ScrollRect>();
                        if (sr0 == null && w != null) sr0 = w.GetComponentInChildren<ScrollRect>(true);
                        if (sr0 == null) return "{\"error\":\"no ScrollRect\"}";
                        sr0.verticalNormalizedPosition = float.Parse(Get(q, "pos", "0"), CultureInfo.InvariantCulture);
                        return Ok("scrolled");
                    }
                    catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
                case "scrollprobe":
                    // Would a mouse-wheel event at the multiplayer window's center reach its ScrollRect?
                    try
                    {
                        var win = WorldboxMP.Client.UI.MultiplayerWindow.Instance;
                        if (win == null) return "{\"error\":\"no window\"}";
                        ScrollRect sr = null;
                        for (var t = win.transform; t != null && sr == null; t = t.parent) sr = t.GetComponent<ScrollRect>();
                        if (sr == null) sr = win.GetComponentInChildren<ScrollRect>(true);
                        if (sr == null) return "{\"error\":\"no ScrollRect\"}";
                        var vpRt = sr.viewport != null ? sr.viewport : (RectTransform)sr.transform;
                        var corners = new Vector3[4]; vpRt.GetWorldCorners(corners);
                        var cam = vpRt.GetComponentInParent<Canvas>()?.worldCamera;
                        string outp = "[";
                        foreach (float fy in new[] { 0.2f, 0.5f, 0.8f })
                        {
                            var world = Vector3.Lerp(corners[0], corners[2], 0.5f); world.y = Mathf.Lerp(corners[0].y, corners[2].y, fy);
                            var screen = RectTransformUtility.WorldToScreenPoint(cam, world);
                            var ped = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current) { position = screen };
                            var hits = new List<UnityEngine.EventSystems.RaycastResult>();
                            UnityEngine.EventSystems.EventSystem.current.RaycastAll(ped, hits);
                            var top = hits.Count > 0 ? hits[0].gameObject : null;
                            var handler = top != null ? UnityEngine.EventSystems.ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.IScrollHandler>(top) : null;
                            if (outp.Length > 1) outp += ",";
                            outp += $"{{\"y\":{fy},\"top\":\"{Esc(top != null ? top.name : "none")}\",\"scrollHandler\":\"{Esc(handler != null ? handler.name : "none")}\",\"isOurScroll\":{(handler == sr.gameObject ? "true" : "false")}}}";
                        }
                        float overflow = sr.content != null ? sr.content.rect.height - vpRt.rect.height : 0f;
                        return "{\"probes\":" + outp + "],\"overflow\":" + overflow.ToString("F0", CultureInfo.InvariantCulture) + ",\"pos\":" + sr.verticalNormalizedPosition.ToString("F2", CultureInfo.InvariantCulture) + "}";
                    }
                    catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
                case "cursors": return WorldboxMP.Client.UI.PlayerCursors.Instance?.DebugJson() ?? "{\"error\":\"no cursors\"}";
                case "meta": return Meta();
                case "record":
                    // Promo clip frames from this game's own renderer: ?dir=&seconds=&fps=, or ?status=1.
                    if (Get(q, "status", "0") == "1") return Ok(DevRecorder.Status);
                    return Ok(DevRecorder.Start(Get(q, "dir", Path.Combine(Path.GetTempPath(), "wbmp-rec")),
                        float.Parse(Get(q, "seconds", "10"), CultureInfo.InvariantCulture), GetI(q, "fps", 30)));
                case "resolution":
                    // Windowed size for recordings (e.g. 1600x900 for a 16:9 video).
                    try { Screen.SetResolution(GetI(q, "w", 1600), GetI(q, "h", 900), FullScreenMode.Windowed); return Ok("resolution " + GetI(q, "w", 1600) + "x" + GetI(q, "h", 900)); }
                    catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
                case "cursorsend": WorldboxMP.Client.UI.PlayerCursors.SuppressLocalSend = Get(q, "on", "1") == "0"; return Ok("local cursor send " + Get(q, "on", "1"));
                case "camera":
                    // Frame the view for screenshots: ?city=<id> or ?x=&y=, plus ?zoom= (orthographic size).
                    try
                    {
                        float zoom = float.Parse(Get(q, "zoom", "40"), CultureInfo.InvariantCulture);
                        Vector3 pos;
                        long cid = long.Parse(Get(q, "city", "0"));
                        if (cid > 0)
                        {
                            var city = World.world.cities.get(cid);
                            var t = city?.getTile();
                            if (t == null) return "{\"error\":\"no such city\"}";
                            pos = new Vector3(t.pos.x, t.pos.y, 0f);
                        }
                        else pos = new Vector3(GetI(q, "x", MapBox.width / 2), GetI(q, "y", MapBox.height / 2), 0f);
                        // MoveCamera.instance is public only in the publicized reference assembly;
                        // at runtime it is not, so touch it by reflection (a direct access breaks
                        // this whole method at JIT time).
                        var mcType = AccessTools.TypeByName("MoveCamera");
                        var mc = AccessTools.Field(mcType, "instance")?.GetValue(null);
                        var cam = Camera.main;
                        if (cam != null) cam.transform.position = new Vector3(pos.x, pos.y, cam.transform.position.z);
                        if (mc != null) AccessTools.Method(mcType, "forceZoom", new[] { typeof(float) })?.Invoke(mc, new object[] { zoom });
                        return "{\"ok\":\"camera\",\"x\":" + pos.x + ",\"y\":" + pos.y + "}";
                    }
                    catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
                case "hud":
                    // Hide/show the mod's own overlays (version + ping badge) for clean screenshots.
                    try
                    {
                        bool show = Get(q, "show", "1") != "0";
                        foreach (var go in Resources.FindObjectsOfTypeAll<Canvas>())
                            if (go != null && go.gameObject.name == "WBMP_PingCanvas") go.gameObject.SetActive(show);
                        return Ok("hud " + (show ? "shown" : "hidden"));
                    }
                    catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
                case "plugin":
                    // SDK test: register the dev channel once, then send on it.
                    if (!_pluginReg)
                    {
                        _pluginReg = true;
                        WorldboxMP.Client.Api.WbmpApi.RegisterChannel("wbmp.devtest", (data, fromKing) =>
                        { lock (_pluginLog) _pluginLog.Add(System.Text.Encoding.UTF8.GetString(data) + (fromKing ? "|fromOwner" : "|fromFollower")); });
                    }
                    if (Get(q, "msg", "") != "") WorldboxMP.Client.Api.WbmpApi.Send("wbmp.devtest", System.Text.Encoding.UTF8.GetBytes(Get(q, "msg", "")));
                    lock (_pluginLog) return "[\"" + string.Join("\",\"", _pluginLog.Select(Esc)) + "\"]";
                case "actorjson":
                    try
                    {
                        long want = long.Parse(Get(q, "id", "0"));
                        foreach (var u in World.world.units.getSimpleList())
                            if (u != null && u.id == want)
                            {
                                try { u.prepareForSave(); } catch { }
                                var d = AccessTools.Field(typeof(Actor), "data")?.GetValue(u);
                                return Newtonsoft.Json.JsonConvert.SerializeObject(d, JsonHelper.read_settings);
                            }
                        return "{\"error\":\"no actor\"}";
                    }
                    catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
                case "traffic":
                    {
                        var sbt = new StringBuilder("{");
                        bool ft = true;
                        for (int k = 0; k < MirrorManager.TxBytesByKind.Length; k++)
                        {
                            if (MirrorManager.TxCountByKind[k] == 0) continue;
                            if (!ft) sbt.Append(","); ft = false;
                            sbt.Append("\"" + (MirrorKind)k + "\":[" + MirrorManager.TxCountByKind[k] + "," + MirrorManager.TxBytesByKind[k] + "]");
                        }
                        if (!ft) sbt.Append(",");
                        sbt.Append("\"_resyncFull\":[" + MirrorManager.ResyncFull + ",0],\"_resyncPatch\":[" + MirrorManager.ResyncStatsOnly + ",0]");
                        return sbt.Append("}").ToString();
                    }
                case "actors":
                    {
                        var sba = new StringBuilder("[");
                        try
                        {
                            bool f1 = true;
                            foreach (var u in World.world.units.getSimpleList())
                            {
                                if (u == null) continue;
                                if (!f1) sba.Append(","); f1 = false;
                                string extra = "";
                                if (Get(q, "deep", "0") == "1")
                                {
                                    try
                                    {
                                        var ad = AccessTools.Field(typeof(Actor), "data")?.GetValue(u) as ActorData;
                                        var tr = ad?.saved_traits != null ? string.Join("+", ad.saved_traits.OrderBy(t => t)) : "";
                                        extra = $"|hp={ad?.health}|nu={ad?.nutrition}|ha={ad?.happiness}|age={u.getAge()}|og={ad?.age_overgrowth}|dcity={ad?.cityID}|city={u.city?.id}|cul={u.culture?.id}|clan={u.clan?.id}|kd={u.kingdom?.id}|tr={tr}|obj={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(u)}|dobj={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ad)}";
                                    }
                                    catch { }
                                }
                                sba.Append("\"" + u.id + ":" + Esc(u.asset?.id ?? "?") + (u.isAlive() ? "" : ":dead") + Esc(extra) + "\"");
                            }
                        }
                        catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
                        return sba.Append("]").ToString();
                    }
                case "logout": net?.Logout(); return Ok("logged out");
                case "leave": net?.LeaveRoom(); return Ok("left");
                case "rooms":
                    {
                        var sbr = new StringBuilder("[");
                        if (net != null)
                            for (int i = 0; i < net.RoomList.Count; i++)
                            {
                                var r = net.RoomList[i];
                                if (i > 0) sbr.Append(",");
                                sbr.Append($"{{\"id\":\"{Esc(r.ShareableId)}\",\"name\":\"{Esc(r.Name)}\",\"count\":{r.PlayerCount},\"max\":{r.MaxPlayers},\"king\":\"{Esc(r.KingName)}\",\"lobby\":{(r.IsLobby ? "true" : "false")},\"locked\":{(r.HasPassword ? "true" : "false")}}}");
                            }
                        return sbr.Append("]").ToString();
                    }
                case "shot":
                    // Full-frame screenshot of THIS instance (written at end of frame).
                    try
                    {
                        string shotPath = Get(q, "path", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wbmp-shot.png"));
                        UnityEngine.ScreenCapture.CaptureScreenshot(shotPath);
                        return Ok("shot " + shotPath);
                    }
                    catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
                case "btn":
                    // Press a button in the multiplayer window by GameObject name (what a click would do).
                    try
                    {
                        string want = Get(q, "name", "");
                        foreach (var b in Resources.FindObjectsOfTypeAll<Button>())
                            if (b != null && b.name == want && b.gameObject.activeInHierarchy)
                            {
                                if (!b.interactable) return "{\"error\":\"not interactable\"}";
                                b.onClick.Invoke();
                                return Ok("clicked " + want);
                            }
                        return "{\"error\":\"no active button " + Esc(want) + "\"}";
                    }
                    catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
                case "type":
                    // Set an input field's text by GameObject name (InputField or its NameInput root).
                    try
                    {
                        string want = Get(q, "name", ""), val = Get(q, "text", "");
                        foreach (var f in Resources.FindObjectsOfTypeAll<InputField>())
                            if (f != null && f.gameObject.activeInHierarchy && (f.name == want || (f.transform.parent != null && f.transform.parent.name == want)))
                            { f.text = val; return Ok("typed " + want); }
                        return "{\"error\":\"no active field " + Esc(want) + "\"}";
                    }
                    catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
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
                case "relink":
                    // Diagnostic: per kingdom/city dump the saved ruler id vs the live link,
                    // then force the loader's link pass (Kingdom.load2 / City.loadLeader).
                    try
                    {
                        var sbr = new StringBuilder("{\"kingdoms\":[");
                        var load2 = AccessTools.Method(typeof(Kingdom), "load2");
                        var kdataF = AccessTools.Field(typeof(Kingdom), "data");
                        int n0 = 0;
                        if (World.world?.kingdoms is IEnumerable kss)
                            foreach (var k in kss)
                            {
                                if (!(k is Kingdom kk)) continue;
                                long kid = -1;
                                try
                                {
                                    var kd = kdataF?.GetValue(kk);
                                    var f = kd != null ? AccessTools.Field(kd.GetType(), "kingID") : null;
                                    if (f != null) kid = Convert.ToInt64(f.GetValue(kd));
                                    else { var pr = kd != null ? AccessTools.PropertyGetter(kd.GetType(), "kingID") : null; if (pr != null) kid = Convert.ToInt64(pr.Invoke(kd, null)); }
                                }
                                catch { }
                                bool before = false; try { before = kk.king != null; } catch { }
                                string err = "";
                                try { load2?.Invoke(kk, null); } catch (Exception le) { err = (le.InnerException ?? le).Message; }
                                bool after = false; try { after = kk.king != null; } catch { }
                                bool actorExists = false; try { actorExists = kid > 0 && World.world.units.get(kid) != null; } catch { }
                                if (n0++ > 0) sbr.Append(",");
                                sbr.Append($"{{\"id\":{kk.id},\"dataKingID\":{kid},\"actorExists\":{(actorExists ? "true" : "false")},\"before\":{(before ? "true" : "false")},\"after\":{(after ? "true" : "false")},\"err\":\"{Esc(err)}\"}}");
                            }
                        sbr.Append("],\"cities\":[");
                        n0 = 0;
                        var cdataF = AccessTools.Field(typeof(City), "data");
                        if (World.world?.cities is IEnumerable css)
                            foreach (var c in css)
                            {
                                if (!(c is City cy)) continue;
                                long lid = -1;
                                try
                                {
                                    var cd = cdataF?.GetValue(cy);
                                    var pr = cd != null ? AccessTools.PropertyGetter(cd.GetType(), "leaderID") : null;
                                    if (pr != null) lid = Convert.ToInt64(pr.Invoke(cd, null));
                                    else { var f = cd != null ? AccessTools.Field(cd.GetType(), "leaderID") : null; if (f != null) lid = Convert.ToInt64(f.GetValue(cd)); }
                                }
                                catch { }
                                bool before = false; try { before = cy.leader != null; } catch { }
                                string err = "";
                                try { cy.loadLeader(); } catch (Exception le) { err = (le.InnerException ?? le).Message; }
                                bool after = false; try { after = cy.leader != null; } catch { }
                                if (n0++ > 0) sbr.Append(",");
                                sbr.Append($"{{\"id\":{cy.id},\"dataLeaderID\":{lid},\"before\":{(before ? "true" : "false")},\"after\":{(after ? "true" : "false")},\"err\":\"{Esc(err)}\"}}");
                            }
                        sbr.Append("]}");
                        return sbr.ToString();
                    }
                    catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
                case "uiprobe":
                    // Diagnose input-field focusability: state flags, what a raycast at the
                    // field's center actually hits, and whether the EventSystem accepts AND
                    // keeps the selection (call twice: second call shows if focus stuck).
                    try
                    {
                        var sbp = new StringBuilder("{\"fields\":[");
                        int fi = 0;
                        foreach (var inp in Resources.FindObjectsOfTypeAll<InputField>())
                        {
                            if (inp == null || !inp.gameObject.scene.IsValid()) continue;
                            // The named GameObject is the cloned NameInputElement ROOT; the
                            // InputField component sits on an inner child - match ancestors.
                            string nm = null;
                            var walk = inp.transform;
                            for (int up = 0; up < 5 && walk != null && nm == null; up++, walk = walk.parent)
                            {
                                string wn = walk.gameObject.name;
                                if (wn == "User" || wn == "Pass" || wn == "RName" || wn == "RPass" || wn == "JId" || wn == "JPass" || wn == "Host" || wn == "Port") nm = wn;
                            }
                            if (nm == null) continue;
                            bool cgOk = true;
                            foreach (var cg in inp.GetComponentsInParent<CanvasGroup>(true))
                                if (!cg.interactable || !cg.blocksRaycasts) cgOk = false;
                            string top = "";
                            try
                            {
                                var rt2 = (RectTransform)inp.transform;
                                var corners = new Vector3[4]; rt2.GetWorldCorners(corners);
                                var center = (corners[0] + corners[2]) / 2f;
                                var cv = inp.GetComponentInParent<Canvas>();
                                Vector2 screen = RectTransformUtility.WorldToScreenPoint(
                                    cv != null && cv.renderMode != RenderMode.ScreenSpaceOverlay ? cv.worldCamera : null, center);
                                var ped = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current) { position = screen };
                                var hits = new List<UnityEngine.EventSystems.RaycastResult>();
                                UnityEngine.EventSystems.EventSystem.current.RaycastAll(ped, hits);
                                for (int h = 0; h < Math.Min(4, hits.Count); h++)
                                {
                                    var hg = hits[h].gameObject;
                                    var hcv = hg.GetComponentInParent<Canvas>();
                                    string hpath = hg.name;
                                    for (var pw = hg.transform.parent; pw != null && hpath.Length < 90; pw = pw.parent) hpath = pw.gameObject.name + "/" + hpath;
                                    top += (h > 0 ? " >> " : "") + $"{hg.name}{{cv={(hcv != null ? hcv.name + "#" + hcv.sortingOrder : "none")} d={hits[h].depth} path={hpath}}}";
                                }
                            }
                            catch (Exception rex) { top = "rayerr:" + rex.Message; }
                            bool wasFocused = inp.isFocused;
                            string sel;
                            try
                            {
                                UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(inp.gameObject);
                                inp.ActivateInputField();
                                sel = UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject != null
                                    ? UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject.name : "null";
                            }
                            catch (Exception fex) { sel = "err:" + fex.Message; }
                            // Geometry + raycastability of everything under the field ROOT (the
                            // named ancestor): the visible box may not be where the InputField is.
                            string tree = "";
                            try
                            {
                                Transform rootT = inp.transform;
                                for (var w2 = inp.transform; w2 != null; w2 = w2.parent)
                                    if (w2.gameObject.name == nm) { rootT = w2; break; }
                                var cvv = inp.GetComponentInParent<Canvas>();
                                var cam = cvv != null && cvv.renderMode != RenderMode.ScreenSpaceOverlay ? cvv.worldCamera : null;
                                foreach (var g in rootT.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                                {
                                    var grt = g.rectTransform;
                                    var c4 = new Vector3[4]; grt.GetWorldCorners(c4);
                                    Vector2 mn = RectTransformUtility.WorldToScreenPoint(cam, c4[0]);
                                    Vector2 mx = RectTransformUtility.WorldToScreenPoint(cam, c4[2]);
                                    tree += $"{g.gameObject.name}[{g.GetType().Name} rt={(g.raycastTarget ? 1 : 0)} en={(g.isActiveAndEnabled ? 1 : 0)} x={mn.x:F0}-{mx.x:F0} y={mn.y:F0}-{mx.y:F0}]; ";
                                }
                                var irt = (RectTransform)inp.transform;
                                var ic4 = new Vector3[4]; irt.GetWorldCorners(ic4);
                                Vector2 imn = RectTransformUtility.WorldToScreenPoint(cam, ic4[0]);
                                Vector2 imx = RectTransformUtility.WorldToScreenPoint(cam, ic4[2]);
                                tree += $"| inputRect x={imn.x:F0}-{imx.x:F0} y={imn.y:F0}-{imx.y:F0} scale={inp.transform.lossyScale.x:F2}";
                            }
                            catch (Exception tex) { tree = "treeerr:" + tex.Message; }
                            if (fi++ > 0) sbp.Append(",");
                            sbp.Append($"{{\"name\":\"{Esc(nm)}\",\"activeInHierarchy\":{(inp.isActiveAndEnabled ? "true" : "false")},\"interactable\":{(inp.interactable ? "true" : "false")},\"cgOk\":{(cgOk ? "true" : "false")},\"wasFocusedBefore\":{(wasFocused ? "true" : "false")},\"topHits\":\"{Esc(top)}\",\"selectedNow\":\"{Esc(sel)}\",\"tree\":\"{Esc(tree)}\"}}");
                        }
                        sbp.Append("]}");
                        return sbp.ToString();
                    }
                    catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
                case "uiclick":
                    // Simulate a player's mouse click at the named field's center through the
                    // full EventSystem pipeline (raycast -> handler walk -> down/up/click) and
                    // report focus. InputField focus lands a frame later, so call twice:
                    // focusedBefore=true on the second call proves the first click stuck.
                    try
                    {
                        string want = Get(q, "name", "User");
                        InputField target = null;
                        foreach (var cand in Resources.FindObjectsOfTypeAll<InputField>())
                        {
                            if (cand == null || !cand.gameObject.scene.IsValid() || !cand.isActiveAndEnabled) continue;
                            for (var t = cand.transform; t != null && target == null; t = t.parent)
                                if (t.gameObject.name == want) target = cand;
                            if (target != null) break;
                        }
                        if (target == null) return "{\"error\":\"field not found or inactive\"}";
                        bool before = target.isFocused;
                        var rt3 = (RectTransform)target.transform;
                        var cs = new Vector3[4]; rt3.GetWorldCorners(cs);
                        var cvx = target.GetComponentInParent<Canvas>();
                        Vector2 sp = RectTransformUtility.WorldToScreenPoint(
                            cvx != null && cvx.renderMode != RenderMode.ScreenSpaceOverlay ? cvx.worldCamera : null, (cs[0] + cs[2]) / 2f);
                        var es = UnityEngine.EventSystems.EventSystem.current;
                        var pd = new UnityEngine.EventSystems.PointerEventData(es) { position = sp, button = UnityEngine.EventSystems.PointerEventData.InputButton.Left, clickCount = 1 };
                        var hits2 = new List<UnityEngine.EventSystems.RaycastResult>();
                        es.RaycastAll(pd, hits2);
                        if (hits2.Count == 0) return "{\"error\":\"no raycast hit at field center\"}";
                        pd.pointerCurrentRaycast = pd.pointerPressRaycast = hits2[0];
                        var pressGo = UnityEngine.EventSystems.ExecuteEvents.ExecuteHierarchy(hits2[0].gameObject, pd, UnityEngine.EventSystems.ExecuteEvents.pointerDownHandler);
                        var clickGo = UnityEngine.EventSystems.ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.IPointerClickHandler>(hits2[0].gameObject);
                        pd.pointerPress = pressGo;
                        UnityEngine.EventSystems.ExecuteEvents.ExecuteHierarchy(hits2[0].gameObject, pd, UnityEngine.EventSystems.ExecuteEvents.pointerUpHandler);
                        if (clickGo != null) UnityEngine.EventSystems.ExecuteEvents.Execute(clickGo, pd, UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                        return $"{{\"field\":\"{Esc(want)}\",\"topHit\":\"{Esc(hits2[0].gameObject.name)}\",\"pressHandler\":\"{Esc(pressGo != null ? pressGo.name : "null")}\",\"clickHandler\":\"{Esc(clickGo != null ? clickGo.name : "null")}\",\"focusedBefore\":{(before ? "true" : "false")},\"focusedAfter\":{(target.isFocused ? "true" : "false")}}}";
                    }
                    catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
                case "closewin":
                    // A dialog pauses the local sim (effective pause = Config.paused || isWindowActive),
                    // which silently freezes AI/tasks/combat on this instance - close everything.
                    try { ScrollWindow.hideAllEvent(pWithAnimation: false); } catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
                    return Ok("windows closed");
                case "model3d": try { var inst = WorldboxMP.Client.UI.Model3D.Instance; if (inst == null) return "{\"error\":\"Model3D.Instance null (Spawn didn't run)\"}"; if (Get(q, "act", "show") == "hide") inst.Teardown(); else inst.Activate(); } catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; } return Ok("model3d " + Get(q, "act", "show"));
                case "mpwindow": try { if (Get(q, "act", "open") == "open") WorldboxMP.Client.UI.MultiplayerWindow.Open(); else WorldboxMP.Client.UI.MultiplayerWindow.Prewarm(); } catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; } return Ok("mpwindow " + Get(q, "act", "open"));
                case "resync":
                    // Same logic as the window's Resync button, role-aware.
                    try
                    {
                        if (net != null && net.IsKing) { WorldboxMP.Client.Mirror.MirrorManager.Instance?.ForceSnapshotToAll(); return Ok("king broadcast"); }
                        net?.RequestResync(); return Ok("resync requested");
                    }
                    catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
                case "newworld":
                    // Generate a fresh world (exercises the king world-change auto-resync).
                    try { World.world.generateNewMap(); return Ok("generating new world"); }
                    catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
                case "transferbar":
                    // Visual QA for the transfer progress bar: pct 0..100 shows, -1 hides.
                    try
                    {
                        int pct = GetI(q, "pct", 50);
                        if (pct < 0) WorldboxMP.Client.UI.PingHud.ClearTransfer();
                        else WorldboxMP.Client.UI.PingHud.SetTransfer(pct / 100f, $"Receiving world  {pct}%  (12.3 MB)");
                        return Ok("transferbar " + pct);
                    }
                    catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
                case "magnetdrag":
                    // Follower: fake one magnet drag sample at (x,y) - drives RemoteMagnet
                    // on the king when called repeatedly, and drop happens on silence.
                    try
                    {
                        var mtile = World.world.GetTile(GetI(q, "x", 128), GetI(q, "y", 128));
                        WorldboxMP.Client.Mirror.MirrorManager.Instance?.OnFollowerMagnet(mtile);
                        return Ok("magnet drag sent");
                    }
                    catch (Exception e) { return "{\"error\":\"" + Esc(e.Message) + "\"}"; }
                default: return "{\"error\":\"unknown endpoint\"}";
            }
        }

        /// <summary>Meta-object linkage for king/follower comparison: each city/kingdom's live
        /// culture/religion/language refs vs the ids in its data, plus every culture/religion/language/clan.</summary>
        private static bool _pluginReg;
        private static readonly List<string> _pluginLog = new List<string>();

        private static string Meta()
        {
            var sb = new StringBuilder("{");
            string R(object o)
            {
                if (o == null) return "null";
                try
                {
                    var id = AccessTools.Property(o.GetType(), "id")?.GetValue(o);
                    object nm = AccessTools.Property(o.GetType(), "name")?.GetValue(o);
                    if (nm == null) nm = AccessTools.Method(o.GetType(), "getName", Type.EmptyTypes)?.Invoke(o, null);
                    return id + ":" + Esc(Convert.ToString(nm));
                }
                catch { return "?"; }
            }
            string D(object data, string prop) { try { return Convert.ToString(AccessTools.Property(data.GetType(), prop)?.GetValue(data)); } catch { return "?"; } }
            void Owners(string label, System.Collections.IEnumerable list)
            {
                sb.Append("\"" + label + "\":[");
                bool first = true;
                foreach (var o in list)
                {
                    if (o == null) continue;
                    var data = AccessTools.Field(o.GetType(), "data")?.GetValue(o);
                    if (!first) sb.Append(","); first = false;
                    sb.Append("\"" + R(o) + " | culture " + R(AccessTools.Field(o.GetType(), "culture")?.GetValue(o)) + " (data " + D(data, "id_culture") + ")"
                        + " | religion " + R(AccessTools.Field(o.GetType(), "religion")?.GetValue(o)) + " (data " + D(data, "id_religion") + ")"
                        + " | language " + R(AccessTools.Field(o.GetType(), "language")?.GetValue(o)) + " (data " + D(data, "id_language") + ")\"");
                }
                sb.Append("],");
            }
            void Plain(string label, System.Collections.IEnumerable list)
            {
                sb.Append("\"" + label + "\":[");
                bool first = true;
                foreach (var o in list) { if (o == null) continue; if (!first) sb.Append(","); first = false; sb.Append("\"" + R(o) + "\""); }
                sb.Append("],");
            }
            try
            {
                var w = World.world;
                Owners("cities", w.cities); Owners("kingdoms", w.kingdoms);
                Plain("cultures", w.cultures); Plain("religions", w.religions); Plain("languages", w.languages); Plain("clans", w.clans);
                Plain("families", w.families); Plain("armies", w.armies);
            }
            catch (Exception e) { sb.Append("\"error\":\"" + Esc(e.Message) + "\","); }
            sb.Append("\"sent\":\"" + string.Join(",", MirrorManager.ObjSentByKind) + "\",");
            sb.Append("\"applied\":\"" + string.Join(",", MirrorManager.ObjAppliedByKind) + "\",");
            sb.Append("\"failed\":\"" + string.Join(",", MirrorManager.ObjFailedByKind) + "\",");
            return sb.Append("\"ok\":true}").ToString();
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
            sb.Append($"\"version\":\"{Esc(ModInfo.Display)}\",");
            sb.Append($"\"epoch\":{(mm != null ? mm.WorldEpoch : 0)},\"transfer\":\"{Esc(WorldboxMP.Client.UI.PingHud.TransferStatus ?? "")}\",");
            sb.Append($"\"update\":\"{Esc(WorldboxMP.Client.Net.UpdateChecker.UpdateAvailable ?? "")}\",");
            sb.Append($"\"role\":\"{role}\",\"connState\":\"{net?.State}\",\"loggedIn\":{(net != null && net.LoggedIn ? "true" : "false")},");
            sb.Append($"\"roomId\":\"{Esc(net?.RoomId ?? "")}\",\"inRoom\":{(net != null && net.InRoom ? "true" : "false")},\"isKing\":{(net != null && net.IsKing ? "true" : "false")},");
            // roster as THIS client sees it (verifies the roster push reaches non-kings too)
            sb.Append("\"roster\":[");
            try
            {
                var ros = net?.Roster;
                if (ros != null)
                    for (int i = 0; i < ros.Count; i++)
                    { if (i > 0) sb.Append(","); sb.Append($"\"{Esc(ros[i].Username)}:{ros[i].Role}\""); }
            }
            catch { }
            sb.Append("],");
            sb.Append($"\"rttMs\":{net?.TransportRttMs ?? -1},\"bytesRx\":{net?.BytesReceived ?? 0},\"bytesTx\":{net?.BytesSent ?? 0},");
            int actors = -1; try { actors = World.world.units.getSimpleList().Count; } catch { }
            bool paused = false; string speed = "?";
            try { paused = Config.paused; } catch { }
            try { speed = Config.time_scale_asset != null ? Config.time_scale_asset.id : "?"; } catch { }
            // MapBox.map_stats is internal at runtime (publicized-DLL gotcha) - reach it by reflection.
            string age = "?"; try { var ms = AccessTools.Field(typeof(MapBox), "map_stats")?.GetValue(World.world) as MapStats; if (ms != null) age = ms.world_age_id; } catch { }
            // era darkness (age_moon overlay ramp): compare king vs follower to verify era visuals
            float nightMod = -1f;
            try
            {
                var era = AccessTools.Field(typeof(MapBox), "era_manager")?.GetValue(World.world);
                var gm = era != null ? AccessTools.Method(era.GetType(), "getNightMod") : null;
                if (gm != null) nightMod = (float)gm.Invoke(era, null);
            }
            catch { }
            bool winActive = false; try { winActive = ScrollWindow.isWindowActive(); } catch { }
            int sapient = 0, mobs = 0;
            try { var l = World.world.units.getSimpleList(); for (int i = 0; i < l.Count; i++) { var a = l[i]; if (a == null || !a.isAlive()) continue; if (a.isSapient()) sapient++; else mobs++; } } catch { }
            sb.Append($"\"actors\":{actors},\"sapient\":{sapient},\"mobs\":{mobs},\"paused\":{(paused ? "true" : "false")},\"speed\":\"{Esc(speed)}\",\"winActive\":{(winActive ? "true" : "false")},\"kingPaused\":{(MirrorManager.KingPaused ? "true" : "false")},\"age\":\"{Esc(age)}\",\"nightMod\":{nightMod.ToString("F2", CultureInfo.InvariantCulture)},");
            // projectiles in flight + action-visual histograms (data-level verify of the
            // Anim/Slash/Projectile sync: compare these between king and follower).
            int projCount = -1;
            try { projCount = (AccessTools.Field(typeof(ProjectileManager), "list")?.GetValue(World.world.projectiles) as ICollection)?.Count ?? -1; } catch { }
            var animCounts = new Dictionary<string, int>();
            var taskCounts = new Dictionary<string, int>();
            int jumping = 0;
            try
            {
                var aiF = AccessTools.Field(typeof(Actor), "ai");
                var actF = AccessTools.Field(typeof(AiSystemActor), "action");
                var taskF = AccessTools.Field(typeof(AiSystemActor), "task");
                var jumpF = AccessTools.Field(typeof(Actor), "timer_jump_animation");
                var behType = AccessTools.TypeByName("ai.behaviours.BehaviourActionActor");
                var faF = AccessTools.Field(behType, "force_animation");
                var faIdF = AccessTools.Field(behType, "force_animation_id");
                var l = World.world.units.getSimpleList();
                for (int i = 0; i < l.Count; i++)
                {
                    var a = l[i]; if (a == null || !a.isAlive()) continue;
                    var aiSys = aiF?.GetValue(a);
                    if (aiSys == null) continue;
                    try { if ((float)jumpF.GetValue(a) > 0f) jumping++; } catch { }
                    try
                    {
                        if (taskF?.GetValue(aiSys) is BehaviourElementAI t && !string.IsNullOrEmpty(t.id))
                        { taskCounts.TryGetValue(t.id, out var tn); taskCounts[t.id] = tn + 1; }
                    }
                    catch { }
                    var act = actF?.GetValue(aiSys);
                    if (act == null || !(bool)faF.GetValue(act)) continue;
                    var aid = faIdF.GetValue(act) as string; if (string.IsNullOrEmpty(aid)) continue;
                    animCounts.TryGetValue(aid, out var n0); animCounts[aid] = n0 + 1;
                }
            }
            catch { }
            // king/leader link diagnostics (crown + leader-face map icons draw only when
            // kingdom.king / city.leader are LIVE references, not just ids in data).
            int kingsLinked = 0, kingsTotal = 0, leadersLinked = 0, citiesTotal = 0;
            try
            {
                if (World.world?.kingdoms is IEnumerable ks)
                    foreach (var k in ks)
                    {
                        if (!(k is Kingdom kk)) continue;
                        kingsTotal++;
                        try { if (kk.king != null && kk.king.isAlive()) kingsLinked++; } catch { }
                    }
                if (World.world?.cities is IEnumerable cs)
                    foreach (var c in cs)
                    {
                        if (!(c is City cityObj)) continue;
                        citiesTotal++;
                        try { if (cityObj.leader != null && cityObj.leader.isAlive()) leadersLinked++; } catch { }
                    }
            }
            catch { }
            sb.Append($"\"kingsLinked\":\"{kingsLinked}/{kingsTotal}\",\"leadersLinked\":\"{leadersLinked}/{citiesTotal}\",");
            // burning tiles (verify fire sync: compare king vs follower).
            // HashSetWorldTile : HashSet<WorldTile> implements only the GENERIC ICollection<T>.
            int fires = -1;
            try { fires = (AccessTools.Field(typeof(WorldBehaviourActionFire), "_tiles")?.GetValue(null) as System.Collections.Generic.ICollection<WorldTile>)?.Count ?? -1; } catch { }
            sb.Append($"\"fires\":{fires},");
            sb.Append($"\"projectiles\":{projCount},\"jumping\":{jumping},\"anims\":{{");
            int ac = 0;
            foreach (var kv in animCounts) { if (ac++ > 0) sb.Append(","); sb.Append($"\"{Esc(kv.Key)}\":{kv.Value}"); }
            sb.Append("},\"tasks\":{");
            ac = 0;
            foreach (var kv in taskCounts) { if (ac++ > 0) sb.Append(","); sb.Append($"\"{Esc(kv.Key)}\":{kv.Value}"); }
            sb.Append("},");
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
