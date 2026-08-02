using System;
using System.Threading;
using Pastel;
using WorldboxMP.Server.Data;
using WorldboxMP.Server.Net;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Server
{
    internal static class Program
    {
        // Config precedence: env vars > appsettings.json > built-in defaults.
        //   WBMP_CONNECTION_STRING / WBMP_DATABASE_BRAND / WBMP_PORT / WBMP_LATEST_VERSION
        //   (legacy WBMP_MONGO still accepted and implies DatabaseBrand=mongodb)
        // Default brand is sqlite so a fresh checkout runs with ZERO external services:
        // one db file appears next to the exe. The official instance flips to mongodb
        // in its own appsettings/env.
        private const string DefaultSqlite = "Data Source=worldbox-multiplayer-local.db";

        private sealed class AppSettings
        {
            public string ConnectionString { get; set; }
            public string DatabaseBrand { get; set; }
            public int Port { get; set; }
            public string LatestVersion { get; set; }
            public string MinimumVersion { get; set; }
            public string Mongo { get; set; }   // legacy field (pre-brand); implies mongodb
        }

        private static AppSettings LoadAppSettings()
        {
            try
            {
                string path = System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                if (!System.IO.File.Exists(path)) path = "appsettings.json";
                if (!System.IO.File.Exists(path)) return new AppSettings();
                var json = System.IO.File.ReadAllText(path);
                return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch (Exception e)
            {
                Console.WriteLine("WARN: could not read appsettings.json (" + e.Message + ") - using defaults.");
                return new AppSettings();
            }
        }

        private static void Main(string[] args)
        {
            ConsoleExtensions.Enable();   // enable ANSI colour on Windows consoles
            Console.Title = "WorldBox Multiplayer Server";

            // Any background-thread crash → show it and wait, never vanish.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Console.WriteLine("\nUNHANDLED EXCEPTION:\n" + e.ExceptionObject);
                Pause();
            };

            try
            {
                Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine("\nFATAL ERROR:\n" + ex);
                Pause();
            }
        }

        private static void Run()
        {
            var settings = LoadAppSettings();

            // Legacy fallbacks first so new-style values always win.
            string legacyMongo = Environment.GetEnvironmentVariable("WBMP_MONGO") ?? settings.Mongo;

            string brand = (Environment.GetEnvironmentVariable("WBMP_DATABASE_BRAND")
                            ?? settings.DatabaseBrand
                            ?? (string.IsNullOrWhiteSpace(legacyMongo) ? "sqlite" : "mongodb")).Trim().ToLowerInvariant();
            string conn = Environment.GetEnvironmentVariable("WBMP_CONNECTION_STRING")
                          ?? (string.IsNullOrWhiteSpace(settings.ConnectionString) ? null : settings.ConnectionString)
                          ?? legacyMongo
                          ?? DefaultSqlite;
            int port = int.TryParse(Environment.GetEnvironmentVariable("WBMP_PORT"), out var p) ? p
                       : (settings.Port > 0 ? settings.Port : NetProtocol.DefaultPort);

            ApiHost.LatestVersion = Environment.GetEnvironmentVariable("WBMP_LATEST_VERSION") ?? settings.LatestVersion ?? "";
            ApiHost.MinimumVersion = Environment.GetEnvironmentVariable("WBMP_MIN_VERSION") ?? settings.MinimumVersion ?? "";

            Console.WriteLine("=== WorldBox Multiplayer Server ===");

            IPlayerStore db = brand switch
            {
                "sqlite" => new SqlitePlayerStore(conn),
                "mongodb" => new MongoPlayerStore(conn),
                _ => null,
            };
            if (db == null)
            {
                Console.WriteLine($"ERROR: unknown DatabaseBrand '{brand}' - use \"sqlite\" or \"mongodb\".");
                Pause();
                return;
            }

            try
            {
                Console.WriteLine($"Opening database: {db.Describe} ...");
                db.Init();
                Console.WriteLine("Database ready.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("\nERROR: could not open the database.\n" + ex);
                if (brand == "mongodb")
                {
                    bool timedOut = ex is TimeoutException
                        || ex.Message.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
                        || ex.Message.IndexOf("selecting a server", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (timedOut)
                    {
                        Console.WriteLine();
                        Console.WriteLine("============================================================".Pastel(System.Drawing.Color.Gold));
                        Console.WriteLine(" CONNECTION TIMED OUT - whitelist your IP in MongoDB Atlas! ".Pastel(System.Drawing.Color.Gold));
                        Console.WriteLine(" Atlas → Network Access → Add IP Address → 'Add Current IP'  ".Pastel(System.Drawing.Color.Gold));
                        Console.WriteLine(" (or 0.0.0.0/0 to allow all). Atlas drops your IP regularly. ".Pastel(System.Drawing.Color.Gold));
                        Console.WriteLine("============================================================".Pastel(System.Drawing.Color.Gold));
                    }
                    else
                    {
                        Console.WriteLine("\nLikely the cluster is paused, or the connection string/credentials are wrong.");
                    }
                    Console.WriteLine("Tip: \"DatabaseBrand\": \"sqlite\" needs no external database at all.");
                }
                Console.WriteLine("Override with env WBMP_CONNECTION_STRING / WBMP_DATABASE_BRAND and restart.");
                Pause();   // do not exit silently - wait so the error is readable
                return;
            }

            var server = new GameServer(db, port);
            server.Start();

            // REST api on the same port number, TCP side (version checks, health).
            try { ApiHost.Start(port); }
            catch (Exception e) { Console.WriteLine("WARN: REST api failed to start (" + e.Message + ") - game server continues without it."); }

            using var stop = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };

            // Network poll loop (~60 Hz). Single-threaded: all handlers run here.
            while (!stop.IsSet)
            {
                server.PollEvents();
                Thread.Sleep(15);
            }

            Console.WriteLine("Shutting down...");
            server.Stop();
        }

        /// <summary>Keep the console open until a key is pressed (so errors are readable).</summary>
        private static void Pause()
        {
            Console.WriteLine("\nPress any key to continue...");
            try { Console.ReadKey(true); }
            catch { try { Console.ReadLine(); } catch { } }
        }
    }
}
