using UnityEngine;

namespace WorldboxMP.Client.Net
{
    /// <summary>Server endpoint. Precedence: an endpoint the user explicitly saved in the
    /// settings dialog (PlayerPrefs) > compile-time default (DEBUG: local dev server,
    /// Release: the public server). No config files - by design.</summary>
    public static class ServerConfig
    {
        private const string HostKey = "wbmp_host";
        private const string PortKey = "wbmp_port";

#if DEBUG
        private const string DefaultHost = "127.0.0.1";        // dev: local server
        private const int DefaultPort = Shared.Protocol.NetProtocol.DefaultPort;   // 40441
#else
        private const string DefaultHost = "old-server.example.invalid";   // release: public server
        private const int DefaultPort = 9191;                  // public server port (run_server.sh)
#endif

        public static string Host
        {
            get => PlayerPrefs.HasKey(HostKey) ? PlayerPrefs.GetString(HostKey) : DefaultHost;
            set { PlayerPrefs.SetString(HostKey, value); PlayerPrefs.Save(); }
        }

        public static int Port
        {
            get => PlayerPrefs.HasKey(PortKey) ? PlayerPrefs.GetInt(PortKey) : DefaultPort;
            set { PlayerPrefs.SetInt(PortKey, value); PlayerPrefs.Save(); }
        }

        /// <summary>Drop any endpoint saved from the settings dialog and fall back to the
        /// build's default. Lets dev tooling undo a stale localhost override.</summary>
        public static void ClearSavedOverride()
        {
            PlayerPrefs.DeleteKey(HostKey);
            PlayerPrefs.DeleteKey(PortKey);
            PlayerPrefs.Save();
        }
    }
}
