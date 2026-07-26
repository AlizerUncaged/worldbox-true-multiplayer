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
        // Config precedence: env vars WBMP_MONGO / WBMP_PORT > appsettings.json > built-in defaults.
        // Credentials (the Mongo connection string) live in appsettings.json next to the exe
        // (copied from src/Server on build). The password is %-encoded in the URI (?! -> %3F!);
        // the db name (worldboxmp) is supplied in the connection string.
        private const string DefaultMongo = "mongodb://127.0.0.1:27017/worldboxmp";

        private sealed class AppSettings
        {
            public string Mongo { get; set; }
            public int Port { get; set; }
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
            string mongo = Environment.GetEnvironmentVariable("WBMP_MONGO")
                           ?? (string.IsNullOrWhiteSpace(settings.Mongo) ? DefaultMongo : settings.Mongo);
            int port = int.TryParse(Environment.GetEnvironmentVariable("WBMP_PORT"), out var p) ? p
                       : (settings.Port > 0 ? settings.Port : NetProtocol.DefaultPort);

            Console.WriteLine("=== WorldBox Multiplayer Server ===");

            Database db;
            try
            {
                Console.WriteLine("Connecting to MongoDB (timeout 12s)...");
                db = new Database(mongo);
                db.Ping();
                Console.WriteLine("MongoDB connected.");
                db.EnsureIndexes();   // non-fatal
            }
            catch (Exception ex)
            {
                Console.WriteLine("\nERROR: could not connect to MongoDB.\n" + ex);

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
                    Console.WriteLine("\nLikely the Atlas cluster is paused, or the connection string/credentials are wrong.");
                }
                Console.WriteLine("Override with env WBMP_MONGO=<connection string> and restart.");
                Pause();   // do not exit silently - wait so the error is readable
                return;
            }

            var server = new GameServer(db, port);
            server.Start();

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
