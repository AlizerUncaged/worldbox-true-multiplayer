using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows;

namespace WorldboxMP.Installer
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // GitHub only speaks TLS 1.2+; .NET Framework does not always default to it.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            // Headless mode for scripts:  --install [--game <folder>] [--log <file>]
            // Exit code 0 = installed, 1 = failed, 2 = WorldBox not found.
            var args = e.Args;
            if (args.Contains("--install", StringComparer.OrdinalIgnoreCase))
            {
                string Arg(string name) { int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
                string game = Arg("--game") ?? GameLocator.Find();
                string logPath = Arg("--log");
                void Log(string line) { if (logPath != null) File.AppendAllText(logPath, line + Environment.NewLine); }
                int code;
                if (!GameLocator.IsGameFolder(game)) { Log("WorldBox not found. Pass --game <folder with worldbox.exe>."); code = 2; }
                else
                {
                    try { new ModInstaller(game, Log, (p, s) => { }).Run(); Log("OK"); code = 0; }
                    catch (Exception ex) { Log("Error: " + ex.Message); code = 1; }
                }
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Shutdown(code);
                return;
            }
            base.OnStartup(e);
        }

        private void App_Startup(object sender, StartupEventArgs e)
        {
            if (ShutdownMode == ShutdownMode.OnExplicitShutdown) return;
            new MainWindow().Show();
        }
    }
}
