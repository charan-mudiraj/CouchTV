using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CouchTV
{
    /// <summary>
    /// The TV's brightness, from the phone's slider (BRIGHTNESS in ir-receiver/PROTOCOL.md), 0 to 100. A screen that
    /// takes DDC/CI commands over its cable (most monitors, a few TVs) has its own backlight turned down; any other gets
    /// a black veil over everything, click-through and left out of screenshots and the screen mirror. 100 is the screen
    /// as it is. The veil's level is kept between runs (a screen keeps its own).
    /// </summary>
    internal static class Brightness
    {
        const double MostVeil = 0.85;   // at 0, the veil lets this much less light through

        static volatile int _level = 100;
        static volatile bool _screen;   // the screen's own brightness (DDC/CI), else the veil
        static IntPtr _monitor;         // the physical monitor, while it takes DDC/CI
        static uint _min, _max;
        static int _wanted = -1;        // the latest level for the screen, for the worker
        static readonly object _lock = new object();
        static readonly AutoResetEvent _poke = new AutoResetEvent(false);
        static Thread _worker;
        static DimWindow _veil;
        static Dispatcher _ui;
        static DispatcherTimer _save;
        static bool _test;               // the self-test: the veil only, nothing kept

        /// <summary>The brightness now, 0 to 100; from any thread.</summary>
        public static int Level { get { return _level; } }

        /// <summary>"screen" when the screen's own brightness changes, "veil" when CouchTV dims the picture.</summary>
        public static string How { get { return _screen ? "screen" : "veil"; } }

        /// <summary>
        /// At start, on the UI thread: puts back the veil as it was, and finds out (on a thread of its own: it can take a
        /// second) whether the screen takes DDC/CI.
        /// </summary>
        public static void Start(Dispatcher ui)
        {
            _ui = ui;
            int saved = Saved();
            if (saved < 100)
            {
                _level = saved;
                ShowVeil(saved);
                Log.Info("Brightness: the picture dimmed to " + saved + "%, as it was");
            }
            ThreadPool.QueueUserWorkItem(s => Probe());
        }

        /// <summary>The phone's slider moved: the screen's brightness, or the veil, follows. UI thread.</summary>
        public static void Set(int level)
        {
            level = Math.Max(0, Math.Min(100, level));
            _level = level;
            if (_screen)
            {
                lock (_lock) _wanted = level;
                if (_worker == null)
                {
                    _worker = new Thread(Work) { IsBackground = true, Name = "Brightness" };
                    _worker.Start();
                }
                _poke.Set();
                return;
            }
            ShowVeil(level);
            SaveSoon();
        }

        /// <summary>For the self-test (--brightnesstest): the veil only (no asking the screen), and nothing kept.</summary>
        internal static void StartForTest(Dispatcher ui)
        {
            _ui = ui;
            _test = true;
        }

        /// <summary>For the self-test: the veil's window, once it's been needed.</summary>
        internal static Window Veil { get { return _veil; } }

        static void Probe()
        {
            try
            {
                IntPtr display = MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
                uint count;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(display, out count) || count == 0)
                {
                    Log.Info("Brightness: no physical screen to ask; the phone's slider dims the picture");
                    return;
                }
                var monitors = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(display, count, monitors)) return;
                uint min, current, max;
                if (GetMonitorBrightness(monitors[0].hPhysicalMonitor, out min, out current, out max) && max > min)
                {
                    _monitor = monitors[0].hPhysicalMonitor;
                    _min = min;
                    _max = max;
                    int level = (int)Math.Round((Math.Max(min, Math.Min(max, current)) - min) * 100.0 / (max - min));
                    _ui.BeginInvoke(new Action(() =>
                    {
                        _screen = true;
                        _level = level;
                        HideVeil();
                        Save(100);
                    }));
                    Log.Info("Brightness: the screen (" + monitors[0].szPhysicalMonitorDescription + ") takes DDC/CI, at " + level + "%");
                }
                else
                {
                    DestroyPhysicalMonitors(count, monitors);
                    Log.Info("Brightness: the screen doesn't take DDC/CI; the phone's slider dims the picture");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Brightness", ex);
            }
        }

        /// <summary>Sets the screen's own brightness, the latest level only, one at a time (each takes about 50 ms).</summary>
        static void Work()
        {
            while (true)
            {
                _poke.WaitOne();
                int level;
                lock (_lock)
                {
                    level = _wanted;
                    _wanted = -1;
                }
                if (level < 0 || !_screen) continue;
                uint value = (uint)Math.Round(_min + (_max - _min) * level / 100.0);
                if (SetMonitorBrightness(_monitor, value)) continue;
                Log.Info("Brightness: the screen stopped taking DDC/CI; dimming the picture instead");
                _screen = false;
                _ui.BeginInvoke(new Action(() =>
                {
                    ShowVeil(_level);
                    SaveSoon();
                }));
            }
        }

        static void ShowVeil(int level)
        {
            if (_veil == null) _veil = new DimWindow();
            _veil.Dim((100 - level) / 100.0 * MostVeil);
        }

        static void HideVeil()
        {
            if (_veil != null) _veil.Dim(0);
        }

        // The veil's level, kept in the registry a moment after the slider stops.
        const string Key = @"Software\CouchTV";

        static void SaveSoon()
        {
            if (_test) return;
            if (_save == null)
            {
                _save = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                _save.Tick += (s, e) =>
                {
                    _save.Stop();
                    Save(_level);
                };
            }
            _save.Stop();
            _save.Start();
        }

        static void Save(int level)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue("Brightness", level, RegistryValueKind.DWord);
            }
            catch (Exception ex) { Log.Error("Saving the brightness", ex); }
        }

        static int Saved()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    object value = k != null ? k.GetValue("Brightness") : null;
                    if (value is int) return Math.Max(0, Math.Min(100, (int)value));
                }
            }
            catch (Exception) { }
            return 100;
        }

        const uint MONITOR_DEFAULTTOPRIMARY = 1;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
        }

        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT point, uint flags);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint current, out uint max);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool SetMonitorBrightness(IntPtr monitor, uint brightness);

        /// <summary>
        /// The veil: black over the whole screen, as see-through as the brightness says. Click-through and never
        /// focused, kept above full-screen video, and left out of screenshots and the screen mirror (Windows 10 2004
        /// and newer), so a phone mirroring the TV still sees the picture as it is.
        /// </summary>
        sealed class DimWindow : Window
        {
            const uint WDA_EXCLUDEFROMCAPTURE = 0x11, SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
            static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

            readonly DispatcherTimer _onTop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            double _target;

            public DimWindow()
            {
                WindowStyle = WindowStyle.None;
                AllowsTransparency = true;
                Background = Brushes.Black;
                Opacity = 0;
                Topmost = true;
                ShowInTaskbar = false;
                ShowActivated = false;
                Focusable = false;
                ResizeMode = ResizeMode.NoResize;
                _onTop.Tick += (s, e) => KeepOnTop();
            }

            protected override void OnSourceInitialized(EventArgs e)
            {
                base.OnSourceInitialized(e);
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int exStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, exStyle | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW);
                try { SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE); }
                catch (Exception) { }   // older Windows: the mirror shows the veil too
            }

            /// <summary>Eases to this much black (0: gone).</summary>
            public void Dim(double opacity)
            {
                _target = opacity;
                if (opacity <= 0.001)
                {
                    _onTop.Stop();
                    if (!IsVisible) return;
                    var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
                    fade.Completed += (s, e) => { if (_target <= 0.001) Hide(); };
                    BeginAnimation(OpacityProperty, fade);
                    return;
                }
                Left = 0;
                Top = 0;
                Width = SystemParameters.PrimaryScreenWidth;
                Height = SystemParameters.PrimaryScreenHeight;
                if (!IsVisible) Show();
                KeepOnTop();
                BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase() });
                _onTop.Start();
            }

            void KeepOnTop()
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }

            [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
            [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        }
    }
}
