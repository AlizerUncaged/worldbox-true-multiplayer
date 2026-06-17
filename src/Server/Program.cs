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
        // Defaults; override with env vars WBMP_MONGO / WBMP_PORT.
        // Atlas SRV cluster; db name (wmulti) is supplied by Database when the
        // connection string omits it.
        private const string DefaultMongo =
            "mongodb://localhost:27017/wbmp";

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
            string mongo = Environment.GetEnvironmentVariable("WBMP_MONGO") ?? DefaultMongo;
            int port = int.TryParse(Environment.GetEnvironmentVariable("WBMP_PORT"), out var p) ? p : NetProtocol.DefaultPort;

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
                Console.WriteLine("\nLikely the Atlas cluster is paused/unreachable, or the connection string/credentials are wrong.");
                Console.WriteLine("Override with env WBMP_MONGO=<connection string> and restart.");
                Pause();   // do not exit silently — wait so the error is readable
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
