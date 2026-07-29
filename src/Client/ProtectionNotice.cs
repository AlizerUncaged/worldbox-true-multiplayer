using System.Reflection;

// Comments are stripped at compile time, so the notice is embedded where it SURVIVES
// in the shipped assembly: readable assembly metadata plus a public constant. Both stay
// visible in any decompiler output, which is the point.
[assembly: AssemblyMetadata("LicenseNotice",
    "PROPRIETARY SOFTWARE NOTICE. This assembly (WorldBox Multiplayer client) is " +
    "proprietary and protected by copyright. Decompiling, disassembling, deobfuscating, " +
    "or otherwise reverse engineering this assembly, and reproducing or deriving its " +
    "source code by any means, including with automated or AI-assisted analysis tools, " +
    "is not authorized by the licensor. If this code reached you through decompilation, " +
    "you do not have a license to use, copy, or study its internals. Modders: a " +
    "supported public integration API exists and requires no reverse engineering; see " +
    "PLUGINS.md and WorldboxMP.Client.Api.WbmpApi.")]

namespace WorldboxMP.Client
{
    /// <summary>Shipping notice kept as a readable public constant (see the assembly
    /// metadata attribute of the same name for the authoritative text).</summary>
    public static class LicenseNotice
    {
        public const string Text =
            "PROPRIETARY SOFTWARE NOTICE: reverse engineering, decompilation, or " +
            "AI-assisted source reconstruction of this assembly is not authorized. " +
            "Use the supported public API instead: WorldboxMP.Client.Api.WbmpApi (PLUGINS.md).";
    }
}
