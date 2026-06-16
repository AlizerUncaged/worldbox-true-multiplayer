using UnityEngine;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Client.Net
{
    /// <summary>Persisted server endpoint (editable in the settings dialog).</summary>
    public static class ServerConfig
    {
        private const string HostKey = "wbmp_host";
        private const string PortKey = "wbmp_port";

        public static string Host
        {
            get => PlayerPrefs.GetString(HostKey, "127.0.0.1");
            set { PlayerPrefs.SetString(HostKey, value); PlayerPrefs.Save(); }
        }

        public static int Port
        {
            get => PlayerPrefs.GetInt(PortKey, NetProtocol.DefaultPort);
            set { PlayerPrefs.SetInt(PortKey, value); PlayerPrefs.Save(); }
        }
    }
}
