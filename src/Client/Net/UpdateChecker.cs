using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace WorldboxMP.Client.Net
{
    /// <summary>
    /// Startup version check against the server's REST api
    /// (GET {ApiBase}/api/v1/version -> {"latest":"...","minimum":"..."}).
    /// Runs on a background thread; results land in static strings that the HUD
    /// reads from the main thread. Date-based versions (yyyy.MM.dd.HHmm) compare
    /// lexicographically, so "is newer" is an ordinal string compare.
    /// Uses HttpWebRequest (always present in the game's runtime; System.Net.Http
    /// is not guaranteed to ship with the game).
    /// </summary>
    public static class UpdateChecker
    {
        /// <summary>Latest version string when the server advertises something newer
        /// than this build; null otherwise. Read by PingHud.</summary>
        public static volatile string UpdateAvailable;

        public static void Start()
        {
            // Old Mono profiles default to pre-TLS1.2 protocols; enable explicitly.
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            new Thread(Check) { IsBackground = true, Name = "wbmp-update-check" }.Start();
        }

        private static void Check()
        {
            string url = ServerConfig.ApiBase + "/api/v1/version";
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 8000;
                req.ReadWriteTimeout = 8000;
                using var resp = (HttpWebResponse)req.GetResponse();
                using var reader = new StreamReader(resp.GetResponseStream());
                string json = reader.ReadToEnd();

                var m = Regex.Match(json, "\"latest\"\\s*:\\s*\"([^\"]*)\"");
                if (!m.Success || m.Groups[1].Value.Length == 0)
                {
                    ModLog.Info("[update] server advertises no version yet (" + url + ").");
                    return;
                }
                string latest = m.Groups[1].Value;
                if (string.CompareOrdinal(latest, ModInfo.Version) > 0)
                {
                    UpdateAvailable = latest;
                    ModLog.Info($"[update] UPDATE AVAILABLE: {latest} (this build: {ModInfo.Version}).");
                }
                else
                {
                    ModLog.Info($"[update] up to date ({ModInfo.Version}; server latest: {latest}).");
                }
            }
            catch (Exception e)
            {
                // Never noisy: no server-side api yet / offline is a normal situation.
                ModLog.Info("[update] check skipped (" + e.Message + ").");
            }
        }
    }
}
