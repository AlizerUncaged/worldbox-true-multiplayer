using UnityEngine;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Client.Net
{
    /// <summary>Server endpoint. Precedence: an endpoint the user explicitly saved in the
    /// settings dialog (PlayerPrefs) > .env file > compile-time default (see EnvConfig).</summary>
    public static class ServerConfig
    {
        private const string HostKey = "wbmp_host";
        private const string PortKey = "wbmp_port";

        public static string Host
        {
            get => PlayerPrefs.HasKey(HostKey) ? PlayerPrefs.GetString(HostKey) : EnvConfig.Host;
            set { PlayerPrefs.SetString(HostKey, value); PlayerPrefs.Save(); }
        }

        public static int Port
        {
            get => PlayerPrefs.HasKey(PortKey) ? PlayerPrefs.GetInt(PortKey) : EnvConfig.Port;
            set { PlayerPrefs.SetInt(PortKey, value); PlayerPrefs.Save(); }
        }
    }
}
