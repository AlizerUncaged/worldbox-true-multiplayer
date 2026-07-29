using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace WorldboxMP.Client.Net
{
    /// <summary>
    /// Optional .env config for the client. Looked up (first hit wins) in:
    ///   1) the mod folder (Mods/WorldBoxMultiplayer/.env)
    ///   2) the game root (next to worldbox.exe)
    /// Lines are KEY=VALUE ('#' starts a comment). Recognized keys: HOST, PORT.
    /// Without a .env, the compile-time default applies: 127.0.0.1 in DEBUG builds
    /// (local dev), the public server in Release.
    /// </summary>
    public static class EnvConfig
    {
#if DEBUG
        private const string DefaultHost = "127.0.0.1";        // dev: local server
        private const int DefaultPort = Shared.Protocol.NetProtocol.DefaultPort;   // 40441
#else
        private const string DefaultHost = "old-server.example.invalid";   // release: public server
        private const int DefaultPort = 9191;                  // public server port (run_server.sh)
#endif

        private static bool _loaded;
        private static string _host;
        private static int _port;

        public static string Host { get { Load(); return _host; } }
        public static int Port { get { Load(); return _port; } }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            _host = DefaultHost;
            _port = DefaultPort;

            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in CandidatePaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        vars[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                    ModLog.Info($"[env] loaded {path}");
                    break;   // first .env found wins
                }
                catch (Exception e) { ModLog.Warn("[env] read failed: " + e.Message); }
            }

            if (vars.TryGetValue("HOST", out var h) && !string.IsNullOrWhiteSpace(h)) _host = h.Trim();
            if (vars.TryGetValue("PORT", out var p) && int.TryParse(p, out var port) && port > 0) _port = port;
        }

        private static IEnumerable<string> CandidatePaths()
        {
            string modFolder = null;
            try { modFolder = UI.IconFactory.ModFolder; } catch { }
            if (!string.IsNullOrEmpty(modFolder)) yield return Path.Combine(modFolder, ".env");
            string root = null;
            try { root = Directory.GetParent(Application.dataPath)?.FullName; } catch { }
            if (!string.IsNullOrEmpty(root)) yield return Path.Combine(root, ".env");
        }
    }
}
