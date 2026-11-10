using System;
using HarmonyLib;
using NeoModLoader.api;
using WorldboxMP.Client.Net;
using WorldboxMP.Client.UI;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Client
{
    /// <summary>
    /// Entry point for the WorldBox Multiplayer client mod. Sets up the network
    /// client, the manager window, the toolbar tab, and the Harmony hooks used by
    /// the mirroring layer.
    /// </summary>
    public class ModMain : BasicMod<ModMain>
    {
        protected override void OnModLoad()
        {
            // Dependencies live in <mod>/lib (see LibResolver). Install the resolver BEFORE
            // anything that references LiteNetLib/Shared is compiled: Load() is a separate,
            // never-inlined method, so its body is only JIT-compiled after this line ran.
            string folder = null;
            try { folder = GetDeclaration()?.FolderPath; } catch { }
            LibResolver.Install(folder);
            Load();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void Load()
        {
#if DEBUG
            // Separate debug console window: opt-in only (a line "console" in the .dev file).
            if (Dev.DevAutoLogin.DevFlag("console")) Dev.DevConsole.Start();
#endif
            try { ModInfo.Version = GetDeclaration()?.Version ?? ModInfo.Version; } catch { }
            ModLog.Info($"Loading WorldBox Multiplayer {ModInfo.Display}...");

            // The mod's folder on disk (used to find optional icon PNGs). NML loads
            // compiled mods from memory, so Assembly.Location is empty - use the
            // ModDeclare folder instead.
            try { IconFactory.ModFolder = GetDeclaration()?.FolderPath; }
            catch { IconFactory.ModFolder = null; }

            // Each step is isolated so a failure in one (e.g. a Harmony hook) can't
            // take down the whole mod - it logs and the rest still loads.
            var net = new NetClient();
            Step("UI", () => { MultiplayerWindow.Ensure(); NetPump.Spawn(); UI.PlayerCursors.Spawn(); UI.PingHud.Spawn(); UI.Model3D.Spawn(); });
            Step("mirroring", () =>
            {
                var harmony = new Harmony("alizer.worldboxmp");
                Mirror.MirrorManager.Init(harmony);
            });

            string host = Net.ServerConfig.Host;
            int port = Net.ServerConfig.Port;
            Step("connect", () => net.Connect(host, port));
            ModLog.Info($"Connecting to {host}:{port} (F9 toggles the manager).");
            Step("update-check", Net.UpdateChecker.Start);

#if DEBUG
            Step("dev-autologin", Dev.DevAutoLogin.TryStart);
#endif
        }

        /// <summary>Run an init step in isolation so one failure logs instead of killing the whole mod load.</summary>
        private static void Step(string name, Action action)
        {
            try { action(); }
            catch (Exception e) { ModLog.Error($"Init step '{name}' failed: " + e); }
        }
    }
}
