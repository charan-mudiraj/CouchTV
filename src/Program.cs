using System;
using System.Threading;
using System.Windows;

namespace CouchTV
{
    internal static class Program
    {
        static Mutex _instance;

        [STAThread]
        static int Main(string[] args)
        {
            Log.Init();
            try
            {
                string shot = Arg(args, "--screenshot");
                if (shot != null) return Diagnostics.Screenshot(args, shot);
                string updateTest = Arg(args, "--updatetest");
                if (updateTest != null) return Diagnostics.UpdateTest(args, updateTest);
                string remoteTest = Arg(args, "--remotetest");
                if (remoteTest != null) return Diagnostics.RemoteTest(remoteTest);
                string report = Arg(args, "--selftest");
                if (report != null) return Diagnostics.SelfTest(report);

                bool created;
                _instance = new Mutex(true, @"Local\CouchTV.Home", out created);
                if (!created)
                {
                    // Started by hand while running: bring it forward. Started by the sign-in fallback: nothing to do.
                    if (!Has(args, "--autostart")) Shell.ActivateWindow(HomeWindow.Caption);
                    return 0;
                }

                Shell.EnsureShellSetting();
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    Log.Error("Crash", e.ExceptionObject as Exception);
                    Shell.Rescue();
                };

                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += (s, e) =>
                {
                    Log.Error("Unhandled", e.Exception);
                    e.Handled = true;
                };

                var options = new HomeOptions
                {
                    Windowed = Has(args, "--windowed"),
                    StartupMessage = HomeWindow.StartupMessageFor(Has(args, "--updated"), Has(args, "--update-failed")),
                };
                string exitAfter = Arg(args, "--exit-after");
                if (exitAfter != null) int.TryParse(exitAfter, out options.ExitAfterSeconds);

                var home = new HomeWindow(Config.Load(), options);
                app.SessionEnding += (s, e) => home.AllowClose();
                Log.Info("Started in " + (Shell.ExplorerRunning ? "desktop" : "TV") + " mode");
                int code = app.Run(home);
                Log.Info("Exited");
                return code;
            }
            catch (Exception ex)
            {
                Log.Error("Startup", ex);
                Shell.Rescue();
                return 1;
            }
        }

        internal static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        internal static bool Has(string[] args, string name) { return Array.IndexOf(args, name) >= 0; }
    }
}
