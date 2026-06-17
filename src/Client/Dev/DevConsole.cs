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
    /// pipes ModLog + all Unity logs/exceptions to it AND to a log file (so a crash
    /// that closes the console still leaves a readable log). Compiled out in Release.
    /// </summary>
    internal static class DevConsole
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AllocConsole();
        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();

        private static bool _started;
        private static StreamWriter _file;
        public static string LogPath { get; private set; }

        public static void Start()
        {
            if (_started) return;
            _started = true;

            // 1) Log file FIRST — this survives a crash even if the console window closes.
            try
            {
                LogPath = Path.Combine(Application.persistentDataPath, "wbmp-client.log");
                _file = new StreamWriter(LogPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
                _file.WriteLine($"==== WorldBox Multiplayer client log — {DateTime.Now} ====");
                // Capture EVERYTHING (incl. uncaught exceptions) to the file.
                Application.logMessageReceivedThreaded += OnUnityLog;
            }
            catch (Exception e) { Debug.LogWarning("[WBMP] log file init failed: " + e); }

            // 2) Console window (best-effort; if it fails we still have the file).
            try
            {
                if (GetConsoleWindow() == IntPtr.Zero) AllocConsole();
                var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                Console.SetOut(stdout);
                try { Console.Title = "WorldBox Multiplayer — debug"; } catch { }
                Console.WriteLine("================ WorldBox Multiplayer debug console ================");
                Console.WriteLine("Log file: " + LogPath);
            }
            catch (Exception e) { Debug.LogWarning("[WBMP] DevConsole window init failed: " + e); }
        }

        public static void SetTitle(string title)
        {
            try { Console.Title = "WorldBox Multiplayer — " + title; } catch { }
        }

        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss");
            bool err = type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
            string line = err ? $"[{ts}] [{type}] {condition}\n{stackTrace}" : $"[{ts}] {condition}";
            try { Console.WriteLine(line); } catch { }
            try { _file?.WriteLine(line); } catch { }
        }
    }
}
#endif
