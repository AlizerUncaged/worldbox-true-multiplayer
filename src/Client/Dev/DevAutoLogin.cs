#if DEBUG
using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using WorldboxMP.Client.Net;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Client.Dev
{
    /// <summary>
    /// DEBUG-ONLY developer convenience: when a <c>.dev</c> file exists at the game
    /// root, automatically log in (creating the account if needed) as
    /// <c>testp{N}</c> / <c>testp{N}</c>, where N is this WorldBox instance's launch
    /// rank among running WorldBox processes (1st launched = testp1, 2nd = testp2…).
    ///
    /// Compiled out entirely in Release builds.
    /// </summary>
    internal static class DevAutoLogin
    {
        private static bool _started;
        private static bool _loginTried;

        /// <summary>The dev account name (testpN), or null when .dev isn't present. Used to prefill the login form.</summary>
        public static string User { get; private set; }

        public static void TryStart()
        {
            if (_started) return;
            _started = true;

            if (!DevFileExists()) { ModLog.Info("[dev] no .dev file - auto-login disabled."); return; }

            int n = InstanceIndex();
            User = "testp" + n;
            DevConsole.SetTitle(User);
            ModLog.Info($"[dev] .dev detected - auto-login as {User} (instance #{n}).");

            var net = NetClient.Instance;
            net.DisableTokenLogin = true;   // instances share PlayerPrefs; each must stay its own testpN
            net.OnStateChanged += s =>
            {
                if (s == ConnState.Disconnected) _loginTried = false;   // re-login on the next (re)connect
                else if (s == ConnState.Connected) AttemptLogin();
            };
            net.OnAuth += OnAuth;
            if (net.State == ConnState.Connected) AttemptLogin();
        }

        private static void AttemptLogin()
        {
            if (_loginTried || NetClient.Instance.LoggedIn || string.IsNullOrEmpty(User)) return;
            _loginTried = true;
            NetClient.Instance.Login(User, User);
        }

        private static void OnAuth(AuthResponse r)
        {
            if (r.Ok) { ModLog.Info($"[dev] logged in as {User}."); return; }
            // Login failed (likely the account doesn't exist yet) → register it once.
            if (_loginTried)
            {
                _loginTried = false; // so a subsequent register-failure won't loop into login
                ModLog.Info($"[dev] {User} not found, registering...");
                NetClient.Instance.Register(User, User);
            }
        }

        /// <summary>True when the .dev file contains <paramref name="flag"/> on its own line.</summary>
        public static bool DevFlag(string flag)
        {
            try
            {
                string root = Directory.GetParent(Application.dataPath)?.FullName;
                string path = root != null ? Path.Combine(root, ".dev") : null;
                if (path == null || !File.Exists(path)) return false;
                foreach (var line in File.ReadAllLines(path))
                    if (string.Equals(line.Trim(), flag, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        private static bool DevFileExists()
        {
            try
            {
                string root = Directory.GetParent(Application.dataPath)?.FullName;
                return root != null && File.Exists(Path.Combine(root, ".dev"));
            }
            catch { return false; }
        }

        /// <summary>This instance's rank (1-based) ordered by process start time.</summary>
        private static int InstanceIndex()
        {
            try
            {
                var me = Process.GetCurrentProcess();
                DateTime myStart = me.StartTime;
                int rank = 1;
                foreach (var p in Process.GetProcessesByName(me.ProcessName))
                {
                    try { if (p.Id != me.Id && p.StartTime < myStart) rank++; }
                    catch { /* access denied on some processes - ignore */ }
                }
                return rank;
            }
            catch { return 1; }
        }
    }
}
#endif
