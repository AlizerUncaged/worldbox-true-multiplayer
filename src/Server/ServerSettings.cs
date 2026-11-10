using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using WorldboxMP.Server.Admin;
using WorldboxMP.Server.Rooms;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Server
{
    /// <summary>
    /// All server settings, bound with the standard .NET configuration stack. Sources, later
    /// ones win:
    /// <list type="number">
    /// <item><c>appsettings.json</c> next to the server (committed defaults, documents every key)</item>
    /// <item><c>appsettings.Local.json</c> next to the server or in the working directory
    ///   (git-ignored: your own overrides and secrets)</item>
    /// <item>the file named by the <c>WBMP_SETTINGS</c> environment variable (deployments,
    ///   e.g. /etc/worldbox-multiplayer/appsettings.json)</item>
    /// <item>environment variables with the <c>WBMP_</c> prefix, <c>__</c> for nesting
    ///   (e.g. <c>WBMP_Admin__Password</c>, <c>WBMP_Port</c>)</item>
    /// </list>
    /// </summary>
    public sealed class ServerSettings
    {
        /// <summary>Shared port: UDP for the game, TCP for the REST api.</summary>
        public int Port { get; set; } = NetProtocol.DefaultPort;

        /// <summary>Advertised at /api/v1/version for the in-game update notice.</summary>
        public string LatestVersion { get; set; } = "";
        public string MinimumVersion { get; set; } = "";

        public DatabaseSettings Database { get; set; } = new DatabaseSettings();
        public RoomSettings Rooms { get; set; } = new RoomSettings();
        public AdminOptions Admin { get; set; } = new AdminOptions();
        public HttpsSettings Https { get; set; } = new HttpsSettings();

        public sealed class DatabaseSettings
        {
            /// <summary>"sqlite" (default, zero setup) or "mongodb".</summary>
            public string Provider { get; set; } = "sqlite";
            public string ConnectionString { get; set; } = "Data Source=worldbox-multiplayer-local.db";
        }

        public sealed class HttpsSettings
        {
            /// <summary>PFX certificate; when set, the REST api and admin panel use HTTPS.</summary>
            public string CertificatePath { get; set; } = "";
            public string CertificatePassword { get; set; } = "";
            public bool Enabled => !string.IsNullOrWhiteSpace(CertificatePath);
        }

        public static ServerSettings Load()
        {
            var b = new ConfigurationBuilder();
            AddJson(b, Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
            AddJson(b, Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json"));
            if (!SamePath(Environment.CurrentDirectory, AppContext.BaseDirectory))
                AddJson(b, Path.Combine(Environment.CurrentDirectory, "appsettings.Local.json"));
            string extra = Environment.GetEnvironmentVariable("WBMP_SETTINGS");
            if (!string.IsNullOrWhiteSpace(extra))
            {
                if (File.Exists(extra)) AddJson(b, extra);
                else Console.WriteLine($"WARN: WBMP_SETTINGS points to a missing file: {extra}");
            }
            b.AddEnvironmentVariables("WBMP_");

            var s = new ServerSettings();
            b.Build().Bind(s);
            if (s.Port <= 0 || s.Port > 65535) s.Port = NetProtocol.DefaultPort;
            return s;
        }

        private static void AddJson(ConfigurationBuilder b, string path)
        {
            if (!File.Exists(path)) return;
            b.AddJsonFile(path, optional: true, reloadOnChange: false);
            Console.WriteLine("Settings: " + path);
        }

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }
}
