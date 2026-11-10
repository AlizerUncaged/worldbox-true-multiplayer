using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace WorldboxMP.Client
{
    /// <summary>
    /// Loads the mod's own dependencies (LiteNetLib, WorldboxMP.Shared) from the mod's
    /// <c>lib/</c> folder. They can't sit next to the mod DLL: NeoModLoader loads every
    /// top-level DLL of a compiled mod as a mod candidate and logs an error for each one
    /// without a mod component, which also pops the game's console at every launch.
    /// </summary>
    internal static class LibResolver
    {
        private static string _libDir;
        private static bool _installed;
        private static readonly Dictionary<string, Assembly> _loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        public static void Install(string modFolder)
        {
            if (_installed || string.IsNullOrEmpty(modFolder)) return;
            _installed = true;
            _libDir = Path.Combine(modFolder, "lib");
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            lock (_loaded)
            {
                if (_loaded.TryGetValue(name, out var cached)) return cached;
                // Already in the domain (same name)? Reuse it rather than loading a second copy.
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase)) return _loaded[name] = a;
                string path = Path.Combine(_libDir ?? "", name + ".dll");
                if (!File.Exists(path)) return null;
                var asm = Assembly.Load(File.ReadAllBytes(path));
                _loaded[name] = asm;
                return asm;
            }
        }
    }
}
