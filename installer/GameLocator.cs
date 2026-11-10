using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WorldboxMP.Installer
{
    /// <summary>Finds the WorldBox install through Steam: the Steam folder from the registry,
    /// then every library listed in steamapps/libraryfolders.vdf.</summary>
    internal static class GameLocator
    {
        public const string SteamAppId = "1206560";

        public static bool IsGameFolder(string dir) =>
            !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "worldbox.exe"));

        public static string Find()
        {
            foreach (var lib in SteamLibraries())
            {
                // The manifest names the real install folder; fall back to the usual one.
                string manifest = Path.Combine(lib, "steamapps", "appmanifest_" + SteamAppId + ".acf");
                string folder = "worldbox";
                try
                {
                    if (File.Exists(manifest))
                    {
                        var m = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
                        if (m.Success) folder = m.Groups[1].Value;
                    }
                }
                catch { }
                string dir = Path.Combine(lib, "steamapps", "common", folder);
                if (IsGameFolder(dir)) return dir;
            }
            return null;
        }

        private static IEnumerable<string> SteamLibraries()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var steam in SteamRoots())
            {
                if (seen.Add(steam)) yield return steam;
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                string text = null;
                try { if (File.Exists(vdf)) text = File.ReadAllText(vdf); } catch { }
                if (text == null) continue;
                foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                {
                    string lib = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (seen.Add(lib)) yield return lib;
                }
            }
        }

        private static IEnumerable<string> SteamRoots()
        {
            var roots = new List<string>();
            void Add(string p) { if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) roots.Add(p.Replace('/', '\\')); }
            try { Add(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string); } catch { }
            try { Add(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string); } catch { }
            try { Add(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string); } catch { }
            Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            return roots;
        }
    }
}
