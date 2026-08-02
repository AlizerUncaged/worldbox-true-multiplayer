using UnityEngine;

namespace WorldboxMP.Client.Net
{
    /// <summary>Server endpoint. Precedence: an endpoint the user explicitly saved in the
    /// settings dialog (PlayerPrefs) > compile-time default (DEBUG: local dev server,
    /// Release: the baked official instance - see BuildConfig/EndpointConfig).
    /// No config files at runtime - by design.</summary>
    public static class ServerConfig
    {
        private const string HostKey = "wbmp_host";
        private const string PortKey = "wbmp_port";

#if DEBUG
        private static readonly string DefaultHost = "127.0.0.1";   // dev: local server
        private static readonly int DefaultPort = Shared.Protocol.NetProtocol.DefaultPort;   // 40441
        /// <summary>REST origin for the api (version checks etc.). Dev: the local server.</summary>
        public static string ApiBase => "http://127.0.0.1:" + Shared.Protocol.NetProtocol.DefaultPort;
#else
        private static readonly string DefaultHost = EndpointConfig.Host;   // baked official instance
        private static readonly int DefaultPort = EndpointConfig.Port;      // scheme-derived shared port
        /// <summary>REST origin for the api (version checks etc.).</summary>
        public static string ApiBase => EndpointConfig.ApiBase;
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
