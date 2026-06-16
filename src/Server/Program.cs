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
            string mongo = Environment.GetEnvironmentVariable("WBMP_MONGO") ?? DefaultMongo;
            int port = int.TryParse(Environment.GetEnvironmentVariable("WBMP_PORT"), out var p) ? p : NetProtocol.DefaultPort;

            Console.WriteLine("=== WorldBox Multiplayer Server ===");

            Database db;
            try
            {
                Console.WriteLine("Connecting to MongoDB...");
                db = new Database(mongo);
                db.Ping();
                Console.WriteLine("MongoDB connected.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FATAL: could not connect to MongoDB: {ex.Message}");
                Console.WriteLine("Set WBMP_MONGO to a reachable connection string and retry.");
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
    }
}
