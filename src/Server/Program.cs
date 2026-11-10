using System;
using System.Threading;
using Pastel;
using WorldboxMP.Server.Admin;
using WorldboxMP.Server.Data;
using WorldboxMP.Server.Net;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Server
{
    internal static class Program
    {
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
            var settings = ServerSettings.Load();
            string brand = (settings.Database.Provider ?? "sqlite").Trim().ToLowerInvariant();
            string conn = settings.Database.ConnectionString;
            int port = settings.Port;
            ApiHost.LatestVersion = settings.LatestVersion ?? "";
            ApiHost.MinimumVersion = settings.MinimumVersion ?? "";

            Console.WriteLine("=== WorldBox Multiplayer Server ===");

            IPlayerStore db = brand switch
            {
                "sqlite" => new SqlitePlayerStore(conn),
                "mongodb" => new MongoPlayerStore(conn),
                _ => null,
            };
            if (db == null)
            {
                Console.WriteLine($"ERROR: unknown Database:Provider '{brand}' - use \"sqlite\" or \"mongodb\".");
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
                    Console.WriteLine("Tip: Database:Provider \"sqlite\" needs no external database at all.");
                }
                Console.WriteLine("Fix Database:ConnectionString / Database:Provider (appsettings.Local.json or WBMP_Database__...) and restart.");
                Pause();   // do not exit silently - wait so the error is readable
                return;
            }

            var admin = settings.Admin;
            bool adminReady = admin.Enabled && AdminHost.Seed(db, admin);   // before the auth worker owns the db

            var server = new GameServer(db, port, settings.Rooms);
            server.Start();

            if (adminReady)
            {
                try { AdminHost.Start(server, admin, settings.Https); }
                catch (Exception e) { Console.WriteLine("WARN: admin panel failed to start (" + e.Message + ")."); }
            }
            else if (!admin.Enabled) Console.WriteLine("Admin panel off (Admin:Enabled = true turns it on).");

            // REST api on the same port number, TCP side (version checks, health).
            try { ApiHost.Start(port, settings.Https); }
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
