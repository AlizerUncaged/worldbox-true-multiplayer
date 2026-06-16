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

            // Harmony (mirroring patches register here as the feature lands).
            new Harmony("alizer.worldboxmp").PatchAll(GetType().Assembly);

            // Networking + UI.
            var net = new NetClient();
            MultiplayerWindow.Ensure();
            NetPump.Spawn();

            net.Connect(ServerHost, ServerPort);
            ModLog.Info($"Connecting to {ServerHost}:{ServerPort} (F9 toggles the manager).");
        }
    }
}
