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
        private static string _user;

        public static void TryStart()
        {
            if (_started) return;
            _started = true;

            if (!DevFileExists()) { ModLog.Info("[dev] no .dev file — auto-login disabled."); return; }

            int n = InstanceIndex();
            _user = "testp" + n;
            ModLog.Info($"[dev] .dev detected — auto-login as {_user} (instance #{n}).");

            var net = NetClient.Instance;
            net.OnStateChanged += s => { if (s == ConnState.Connected) AttemptLogin(); };
            net.OnAuth += OnAuth;
            if (net.State == ConnState.Connected) AttemptLogin();
        }

        private static void AttemptLogin()
        {
            if (_loginTried || NetClient.Instance.LoggedIn) return;
            _loginTried = true;
            NetClient.Instance.Login(_user, _user);
        }

        private static void OnAuth(AuthResponse r)
        {
            if (r.Ok) { ModLog.Info($"[dev] logged in as {_user}."); return; }
            // Login failed (likely the account doesn't exist yet) → register it once.
            if (_loginTried)
            {
                _loginTried = false; // so a subsequent register-failure won't loop into login
                ModLog.Info($"[dev] {_user} not found, registering...");
                NetClient.Instance.Register(_user, _user);
            }
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
                    catch { /* access denied on some processes — ignore */ }
                }
                return rank;
            }
            catch { return 1; }
        }
    }
}
#endif
