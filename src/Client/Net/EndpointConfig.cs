using System;

namespace WorldboxMP.Client.Net
{
    /// <summary>
    /// Parses the baked <see cref="BuildConfig.MainServerSource"/> origin into the
    /// pieces the client needs. ONE port number serves everything on the official
    /// instance: the REST api on TCP (https) and the LiteNetLib game traffic on UDP.
    /// Scheme decides the default (https=443, http=80); an explicit :port wins.
    /// </summary>
    public static class EndpointConfig
    {
        /// <summary>Game/api host, e.g. "multiplayer-worlbox.alizer.xyz" (LiteNetLib
        /// resolves hostnames, so DNS changes need no client rebuild).</summary>
        public static readonly string Host;

        /// <summary>The shared port number (UDP for the game, TCP for the api).</summary>
        public static readonly int Port;

        /// <summary>Origin for REST calls, no trailing slash, e.g.
        /// "https://multiplayer-worlbox.alizer.xyz".</summary>
        public static readonly string ApiBase;

        static EndpointConfig()
        {
            string src = BuildConfig.MainServerSource.TrimEnd('/');
            try
            {
                var uri = new Uri(src);
                Host = uri.Host;
                Port = uri.IsDefaultPort ? (uri.Scheme == "http" ? 80 : 443) : uri.Port;
                ApiBase = src;
            }
            catch (Exception e)
            {
                // A malformed baked value should never brick the client - fall back to
                // something inspectable and let the settings dialog rescue the user.
                ModLog.Error("EndpointConfig: cannot parse MAIN_SERVER_SOURCE '" + src + "': " + e.Message);
                Host = "127.0.0.1";
                Port = Shared.Protocol.NetProtocol.DefaultPort;
                ApiBase = "http://127.0.0.1:" + Port;
            }
        }
    }
}
