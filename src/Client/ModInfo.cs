using System.Linq;
using System.Reflection;

namespace WorldboxMP.Client
{
    /// <summary>
    /// Release identity shown to players (net HUD) and exposed to plugins.
    /// Channel comes from the csproj (ReleaseChannel property, baked in as assembly
    /// metadata - the same value stamps the bin/Publish folder name); the version
    /// comes from mod.json via NML's declaration, set by ModMain at load.
    /// </summary>
    public static class ModInfo
    {
        /// <summary>Release channel, e.g. "beta". Single-sourced from the csproj.</summary>
        public static readonly string Channel = ReadChannel();

        /// <summary>Mod version from mod.json (set at load; fallback until then).</summary>
        public static string Version { get; internal set; } = "0.0.0";

        /// <summary>What players see, e.g. "beta-0.1.0".</summary>
        public static string Display => Channel + "-" + Version;

        private static string ReadChannel()
        {
            try
            {
                var meta = Assembly.GetExecutingAssembly()
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => a.Key == "ReleaseChannel");
                if (meta != null && !string.IsNullOrWhiteSpace(meta.Value)) return meta.Value;
            }
            catch { }
            return "beta";
        }
    }
}
