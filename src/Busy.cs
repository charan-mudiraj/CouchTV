using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace CouchTV
{
    /// <summary>
    /// "Hands off for a moment": a banner at the top of the screen while CouchTV works something on the TV by itself
    /// (closing a browser to start it afresh, putting the next song on in YouTube's page, making a song full screen) or a
    /// laptop's test tool presses its keys, so you know to wait rather than press buttons into the middle of it. Over
    /// everything, never takes the focus or a click, and left out of screen captures (the test tool's, the phone's
    /// mirror). Callable from any thread: each job has a key, and the banner shows the latest job's words until every
    /// job's done (or its time's up, in case one never says).
    /// </summary>
    internal static class Busy
    {
        sealed class Job
        {
            public string Text;
            public DateTime Until;
            public long Order;
        }

        static readonly Dictionary<string, Job> Jobs = new Dictionary<string, Job>();
        static long _order;
        static BusyWindow _window;
        static DispatcherTimer _timer;

        /// <summary>A job starts (or goes on): its words on the banner for at most this long.</summary>
        public static void Show(string key, string text, double seconds)
        {
            Run(() =>
            {
                Jobs[key] = new Job { Text = text, Until = DateTime.UtcNow.AddSeconds(seconds), Order = ++_order };
                Refresh();
            });
        }

        /// <summary>A job's done.</summary>
        public static void Hide(string key)
        {
            Run(() =>
            {
                if (Jobs.Remove(key)) Refresh();
            });
        }

        static void Run(Action action)
        {
            Application app = Application.Current;
            if (app == null) return;   // tests and tools: nothing on screen
            if (app.Dispatcher.CheckAccess()) action();
            else app.Dispatcher.BeginInvoke(action);
        }

        static void Refresh()
        {
            DateTime now = DateTime.UtcNow;
            var done = new List<string>();
            Job latest = null;
            foreach (var pair in Jobs)
            {
                if (pair.Value.Until <= now) done.Add(pair.Key);
                else if (latest == null || pair.Value.Order > latest.Order) latest = pair.Value;
            }
            foreach (string key in done) Jobs.Remove(key);
            if (latest == null)
            {
                if (_window != null) _window.Fade();
                if (_timer != null) _timer.Stop();
                return;
            }
            if (_window == null) _window = new BusyWindow();
            _window.Say(latest.Text);
            if (_timer == null)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _timer.Tick += (s, e) => Refresh();
            }
            _timer.Start();
        }

        /// <summary>The banner as it looks, for screenshots of the home screen.</summary>
        internal static FrameworkElement Preview(string text) { return BusyWindow.Banner(text, null); }
    }

    internal sealed class BusyWindow : Window
    {
        readonly TextBlock _text = Theme.Text("", 32, Theme.Ink, FontWeights.SemiBold);
        readonly double _scale = Math.Max(0.6, SystemParameters.PrimaryScreenHeight / 1080.0);
        bool _fading;

        public BusyWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Content = new Border { Child = Banner(null, _text), LayoutTransform = new ScaleTransform(_scale, _scale) };
        }

        /// <summary>The pill: a turning ring, what's going on, and to wait.</summary>
        internal static FrameworkElement Banner(string text, TextBlock words)
        {
            var arc = new Ellipse
            {
                Width = 46, Height = 46, StrokeThickness = 5, Stroke = Theme.Brush(Theme.Hex("#FF3EA6FF")),
                StrokeDashArray = new DoubleCollection { 5.5, 3 }, StrokeDashCap = PenLineCap.Round,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(),
                VerticalAlignment = VerticalAlignment.Center,
            };
            arc.Loaded += (s, e) => ((RotateTransform)arc.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
            words = words ?? Theme.Text(text ?? "", 32, Theme.Ink, FontWeights.SemiBold);
            var hint = Theme.Text("Hands off the remote for a moment", 23, Theme.Muted, FontWeights.Normal);
            hint.Margin = new Thickness(0, 2, 0, 0);
            var column = new StackPanel { Margin = new Thickness(22, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            column.Children.Add(words);
            column.Children.Add(hint);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(arc);
            row.Children.Add(column);
            return new Border
            {
                CornerRadius = new CornerRadius(34), Padding = new Thickness(30, 18, 40, 20),
                Background = Theme.Brush(Theme.Alpha(Theme.Panel, 0xF2)),
                BorderBrush = Theme.Brush(Theme.Hex("#CC3EA6FF")), BorderThickness = new Thickness(2.5),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Opacity = 0.55, Color = Colors.Black },
                Child = row,
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, exStyle | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW);
            // Seen on the TV, not in screen captures (the test tool's screenshots, the phone's mirror). Windows 10 2004 on.
            try { SetWindowDisplayAffinity(hwnd, 0x11); } catch (Exception) { }
        }

        public void Say(string text)
        {
            _text.Text = text;
            var content = (FrameworkElement)Content;
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Left = (SystemParameters.PrimaryScreenWidth - content.DesiredSize.Width) / 2;
            Top = 28 * _scale;
            _fading = false;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            if (!IsVisible) Show();
            Topmost = false;
            Topmost = true;   // above a full-screen browser
        }

        public void Fade()
        {
            if (!IsVisible || _fading) return;
            _fading = true;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (s, e) => { if (_fading) Hide(); };
            BeginAnimation(OpacityProperty, fade);
        }

        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    }
}
