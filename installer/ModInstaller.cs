using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace WorldboxMP.Installer
{
    /// <summary>
    /// Downloads and installs the latest NeoModLoader and the latest WorldBox Multiplayer
    /// release from GitHub. Set WBMP_INSTALLER_MOD_ZIP to a local zip to install that instead
    /// of downloading the mod (used for testing builds before they are released).
    /// </summary>
    internal sealed class ModInstaller
    {
        private const string NmlReleases = "https://api.github.com/repos/WorldBoxOpenMods/ModLoader/releases/latest";
        private const string ModReleases = "https://api.github.com/repos/AlizerUncaged/worldbox-multiplayer/releases";
        private const string ModFolderName = "WorldBoxMultiplayer";

        private readonly string _game;
        private readonly Action<string> _log;
        private readonly Action<double, string> _progress;

        public ModInstaller(string gameDir, Action<string> log, Action<double, string> progress)
        {
            _game = gameDir; _log = log; _progress = progress;
        }

        public void Run()
        {
            using (var http = new HttpClient())
            {
                // GitHub requires a User-Agent. A plain product name only: no personal data.
                http.DefaultRequestHeaders.UserAgent.ParseAdd("WorldBoxMultiplayer-Installer");
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                http.Timeout = TimeSpan.FromMinutes(5);

                InstallNml(http);
                InstallMod(http);
            }
        }

        // ── NeoModLoader ────────────────────────────────────────────────────
        private void InstallNml(HttpClient http)
        {
            _progress(5, "NeoModLoader");
            _log("Looking up the latest NeoModLoader...");
            var release = GetJson<Release>(http, NmlReleases);
            var asset = release.Find(a => a.name.Equals("NeoModLoader.dll", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("The NeoModLoader release has no NeoModLoader.dll.");

            string mods = Path.Combine(_game, "worldbox_Data", "StreamingAssets", "mods");
            Directory.CreateDirectory(mods);
            _log($"Downloading NeoModLoader ({release.tag_name})...");
            byte[] dll = Download(http, asset.browser_download_url, 10, 40, "NeoModLoader");
            File.WriteAllBytes(Path.Combine(mods, "NeoModLoader.dll"), dll);
            _log("NeoModLoader installed: " + Path.Combine(mods, "NeoModLoader.dll"));

            // NML's own instructions: the older NCMS loader should not run alongside it.
            string ncms = Path.Combine(mods, "NCMS_memload.dll");
            if (File.Exists(ncms))
            {
                File.Move(ncms, ncms + ".disabled");
                _log("Disabled NCMS_memload.dll (it conflicts with NeoModLoader).");
            }
        }

        // ── WorldBox Multiplayer ────────────────────────────────────────────
        private void InstallMod(HttpClient http)
        {
            byte[] zip;
            string local = Environment.GetEnvironmentVariable("WBMP_INSTALLER_MOD_ZIP");
            if (!string.IsNullOrWhiteSpace(local) && File.Exists(local))
            {
                _log("Using local mod package: " + local);
                zip = File.ReadAllBytes(local);
            }
            else
            {
                _progress(45, "Multiplayer mod");
                _log("Looking up the latest WorldBox Multiplayer release...");
                var releases = GetJson<Release[]>(http, ModReleases);
                Release latest = null;
                foreach (var r in releases) if (!r.draft) { latest = r; break; }   // newest first; betas count
                if (latest == null) throw new InvalidOperationException("No WorldBox Multiplayer release has been published yet.");
                var asset = latest.Find(a => a.name.StartsWith("WorldBoxMultiplayer-", StringComparison.OrdinalIgnoreCase)
                                             && a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                                             && a.name.IndexOf("server", StringComparison.OrdinalIgnoreCase) < 0)
                            ?? throw new InvalidOperationException($"Release {latest.tag_name} has no mod package.");
                _log($"Downloading WorldBox Multiplayer {latest.tag_name}...");
                zip = Download(http, asset.browser_download_url, 50, 85, "Multiplayer mod");
            }

            _progress(90, "Installing");
            string target = Path.Combine(_game, "Mods", ModFolderName);
            // Replace the whole folder: a stale DLL from an older version must not linger.
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(target);
            using (var archive = new ZipArchive(new MemoryStream(zip)))
            {
                string prefix = CommonRoot(archive);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;   // directory entry
                    string rel = entry.FullName.Substring(prefix.Length).Replace('/', Path.DirectorySeparatorChar);
                    string dest = Path.GetFullPath(Path.Combine(target, rel));
                    if (!dest.StartsWith(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Unsafe path in package: " + entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    entry.ExtractToFile(dest, true);
                }
            }
            if (!File.Exists(Path.Combine(target, "mod.json")))
                throw new InvalidDataException("The downloaded package doesn't look like the mod (no mod.json).");
            _log("WorldBox Multiplayer installed: " + target);
        }

        /// <summary>Packages may wrap everything in one top-level folder; strip it.</summary>
        private static string CommonRoot(ZipArchive archive)
        {
            string root = null;
            foreach (var e in archive.Entries)
            {
                int slash = e.FullName.IndexOf('/');
                if (slash < 0) return "";
                string top = e.FullName.Substring(0, slash + 1);
                if (root == null) root = top;
                else if (root != top) return "";
            }
            return root ?? "";
        }

        private byte[] Download(HttpClient http, string url, double from, double to, string step)
        {
            using (var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).Result)
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                using (var src = resp.Content.ReadAsStreamAsync().Result)
                using (var dst = new MemoryStream())
                {
                    var buf = new byte[81920];
                    int n; long got = 0;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, n);
                        got += n;
                        if (total > 0) _progress(from + (to - from) * got / total, step);
                    }
                    return dst.ToArray();
                }
            }
        }

        private static T GetJson<T>(HttpClient http, string url)
        {
            string json = http.GetStringAsync(url).Result;
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (T)ser.ReadObject(ms);
        }

        [DataContract]
        private sealed class Release
        {
            [DataMember] public string tag_name = "";
            [DataMember] public bool draft = false;
            [DataMember] public Asset[] assets = new Asset[0];
            public Asset Find(Predicate<Asset> match) => Array.Find(assets ?? new Asset[0], match);
        }

        [DataContract]
        private sealed class Asset
        {
            [DataMember] public string name = "";
            [DataMember] public string browser_download_url = "";
        }
    }
}
