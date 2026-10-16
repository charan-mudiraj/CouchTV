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
            // WPF takes touch through Windows' pointer messages (as browsers do), not its older tablet stack, which
            // never sees touches injected by the screen mirror: so a tap on a home screen tile opens it. Must be set
            // before WPF starts (.NET 4.7 and later).
            AppContext.SetSwitch("Switch.System.Windows.Input.Stylus.EnablePointerSupport", true);
            Log.Init();
            try
            {
                string shot = Arg(args, "--screenshot");
                if (shot != null) return Diagnostics.Screenshot(args, shot);
                string updateTest = Arg(args, "--updatetest");
                if (updateTest != null) return Diagnostics.UpdateTest(args, updateTest);
                string remoteTest = Arg(args, "--remotetest");
                if (remoteTest != null) return Diagnostics.RemoteTest(remoteTest);
                string audioTest = Arg(args, "--audiotest");
                if (audioTest != null) return Diagnostics.AudioTest(audioTest);
                string browserTouch = Arg(args, "--browsertouchtest");
                if (browserTouch != null && Has(args, "--wpf"))
                {
                    // As in CouchTV itself: a WPF app running, the taps from a background thread.
                    int result = 1;
                    var testApp = new Application();
                    var window = new Window { Width = 220, Height = 120, Left = 1500, Top = 20, Title = "CouchTV WPF", ShowActivated = false };
                    window.ContentRendered += (s, e) => new System.Threading.Thread(() =>
                    {
                        result = Diagnostics.BrowserTouchTest(browserTouch, int.Parse(Arg(args, "--hold") ?? "60"), Arg(args, "--browser"));
                        testApp.Dispatcher.BeginInvoke(new Action(testApp.Shutdown));
                    }) { IsBackground = true }.Start();
                    testApp.Run(window);
                    return result;
                }
                if (browserTouch != null) return Diagnostics.BrowserTouchTest(browserTouch, int.Parse(Arg(args, "--hold") ?? "60"), Arg(args, "--browser"), Has(args, "--drag"));
                string dragTest = Arg(args, "--dragtest");
                if (dragTest != null) return Diagnostics.DragTest(dragTest);
                string touchTest = Arg(args, "--touchtest");
                if (touchTest != null) return Diagnostics.TouchTest(touchTest, Has(args, "--mouse"));
                string mirrorTest = Arg(args, "--mirrortest");
                if (mirrorTest != null) return Diagnostics.MirrorTest(mirrorTest);
                string bluetoothTest = Arg(args, "--bluetoothtest");
                if (bluetoothTest != null) return Diagnostics.BluetoothTest(bluetoothTest);
                string typingTest = Arg(args, "--typingtest");
                if (typingTest != null) return Diagnostics.TypingTest(typingTest);
                if (Has(args, "--typingtarget")) return Diagnostics.TypingTarget();
                string vectors = Arg(args, "--securityvectors");
                if (vectors != null) return Diagnostics.SecurityVectors(vectors);
                string pointerTest = Arg(args, "--pointertest");
                if (pointerTest != null) return Diagnostics.PointerTest(pointerTest);
                string suggestTest = Arg(args, "--suggesttest");
                if (suggestTest != null) return Diagnostics.SuggestTest(suggestTest);
                string searchTest = Arg(args, "--searchtest");
                if (searchTest != null) return Diagnostics.SearchTest(args, searchTest);
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
                    try { Pointer.Restore(); } catch (Exception) { }
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
