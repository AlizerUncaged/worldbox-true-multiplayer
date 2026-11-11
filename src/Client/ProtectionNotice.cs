using System.Reflection;

// The license travels with the shipped assembly: readable assembly metadata plus a public
// constant. The class is skip-listed in obfuscar.xml so it stays readable verbatim.
[assembly: AssemblyMetadata("LicenseNotice",
    "WorldBox Multiplayer client. Open source under the MIT License, (c) WorldBox Multiplayer contributors. " +
    "Source, license and docs: https://github.com/AlizerUncaged/worldbox-true-multiplayer. " +
    "Modders: integrate through the public API, WorldboxMP.Client.Api.WbmpApi (PLUGINS.md).")]

namespace WorldboxMP.Client
{
    /// <summary>License notice kept as a readable public constant in the shipped assembly.</summary>
    public static class LicenseNotice
    {
        public const string Text =
            "WorldBox Multiplayer is open source under the MIT License: " +
            "https://github.com/AlizerUncaged/worldbox-true-multiplayer. " +
            "For mod compatibility use the public API: WorldboxMP.Client.Api.WbmpApi (PLUGINS.md).";
    }
}
