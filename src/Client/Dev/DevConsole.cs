#if DEBUG
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace WorldboxMP.Client.Dev
{
    /// <summary>
    /// DEBUG-only: allocates a real Windows console for this WorldBox instance and
    /// pipes ModLog + all Unity logs/exceptions to it, so multiplayer behaviour can
    /// be debugged live. Compiled out in Release.
    /// </summary>
    internal static class DevConsole
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AllocConsole();
        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();

        private static bool _started;

        public static void Start()
        {
            if (_started) return;
            _started = true;
            try
            {
                if (GetConsoleWindow() == IntPtr.Zero) AllocConsole();
                var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                Console.SetOut(stdout);
                try { Console.Title = "WorldBox Multiplayer — debug"; } catch { }
                Console.WriteLine("================ WorldBox Multiplayer debug console ================");
                Application.logMessageReceivedThreaded += OnUnityLog;
            }
            catch (Exception e) { Debug.LogWarning("[WBMP] DevConsole init failed: " + e); }
        }

        /// <summary>Update the console title (e.g. with the logged-in account).</summary>
        public static void SetTitle(string title)
        {
            try { Console.Title = "WorldBox Multiplayer — " + title; } catch { }
        }

        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            try
            {
                string ts = DateTime.Now.ToString("HH:mm:ss");
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                    Console.WriteLine($"[{ts}] [{type}] {condition}\n{stackTrace}");
                else
                    Console.WriteLine($"[{ts}] {condition}");
            }
            catch { }
        }
    }
}
#endif
