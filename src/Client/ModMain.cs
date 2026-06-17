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
        public const string ServerHost = "127.0.0.1";
        public const int ServerPort = NetProtocol.DefaultPort; // 40441

        protected override void OnModLoad()
        {
            ModLog.Info($"Loading WorldBox Multiplayer v{GetDeclaration().Version}...");

            // The mod's folder on disk (used to find optional icon PNGs). NML loads
            // compiled mods from memory, so Assembly.Location is empty — use the
            // ModDeclare folder instead.
            try { IconFactory.ModFolder = GetDeclaration()?.FolderPath; }
            catch { IconFactory.ModFolder = null; }

            var harmony = new Harmony("alizer.worldboxmp");
            harmony.PatchAll(GetType().Assembly);

            // We reload the world at runtime (join snapshot / resync). WorldBox's
            // window preloader isn't reentrant and crashes with a duplicate-key
            // ("achievements") if a reload starts mid-preload. Disable preloading —
            // windows then instantiate on demand, which is harmless.
            try { Config.preload_windows = false; } catch { }

            // Networking + UI.
            var net = new NetClient();
            MultiplayerWindow.Ensure();
            NetPump.Spawn();
            UI.PlayerCursors.Spawn();

            // Mirroring (god powers / spawns / world sync / cursors).
            Mirror.MirrorManager.Init(harmony);

            string host = Net.ServerConfig.Host;
            int port = Net.ServerConfig.Port;
            net.Connect(host, port);
            ModLog.Info($"Connecting to {host}:{port} (F9 toggles the manager).");

#if DEBUG
            Dev.DevAutoLogin.TryStart();
#endif
        }
    }
}
