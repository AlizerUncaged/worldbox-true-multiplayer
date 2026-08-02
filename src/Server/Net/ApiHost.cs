using System;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Server.Net
{
    /// <summary>
    /// Minimal REST api (/api/v1/**) on the SAME port number as the game: LiteNetLib
    /// owns the UDP side, Kestrel owns the TCP side - no conflict, no iptables.
    /// HTTPS when a certificate is provided (WBMP_CERT_PFX [+ WBMP_CERT_PASS]),
    /// plain HTTP otherwise (local/dev). The client hits /api/v1/version at startup.
    /// </summary>
    public static class ApiHost
    {
        /// <summary>The version advertised to clients. Set from appsettings
        /// ("LatestVersion") or env WBMP_LATEST_VERSION; empty = endpoint reports none.</summary>
        public static string LatestVersion = "";

        /// <summary>Oldest client version this server accepts (defaults to LatestVersion).</summary>
        public static string MinimumVersion = "";

        public static WebApplication Start(int port)
        {
            string pfx = Environment.GetEnvironmentVariable("WBMP_CERT_PFX");
            string pass = Environment.GetEnvironmentVariable("WBMP_CERT_PASS");

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();   // keep the game server console clean
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.ListenAnyIP(port, o =>
                {
                    if (!string.IsNullOrEmpty(pfx))
                        o.UseHttps(new X509Certificate2(pfx, pass));
                });
            });

            var app = builder.Build();

            app.MapGet("/api/v1/version", () => Microsoft.AspNetCore.Http.Results.Json(new
            {
                latest = LatestVersion,
                minimum = string.IsNullOrEmpty(MinimumVersion) ? LatestVersion : MinimumVersion,
                protocol = NetProtocol.ConnectKey,
            }));

            app.MapGet("/api/v1/health", () => Microsoft.AspNetCore.Http.Results.Json(new
            {
                ok = true,
                protocol = NetProtocol.ConnectKey,
            }));

            app.RunAsync();
            Console.WriteLine($"REST api listening on TCP {port} ({(string.IsNullOrEmpty(pfx) ? "http" : "https")}) - /api/v1/version, /api/v1/health.");
            return app;
        }
    }
}
