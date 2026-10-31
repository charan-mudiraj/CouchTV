using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CouchTV
{
    /// <summary>
    /// The phones' screens (PhoneShare), over everything on the TV: full screen, on top, and never taking the keyboard
    /// from the app in front (Back and Home on the remote close it: HomeWindow.Share.cs). Its page (home/share.html)
    /// is served by PhoneShare on this PC only, in a WebView2 of its own; without WebView2 it says so instead.
    /// </summary>
    internal sealed class ShareWindow : Window
    {
        readonly string _url;
        readonly Grid _grid = new Grid();
        readonly TextBlock _note;
        FrameworkElement _view;   // the WebView2 (as its base type, so this class loads without it)

        /// <summary>Its WebView2's own process, once it's started (0 before): the phones' sound comes from one of its helpers.</summary>
        internal volatile int BrowserProcess;

        /// <summary>[windowed]: a small window, for the self-test, instead of the whole screen.</summary>
        public ShareWindow(string url, bool windowed)
        {
            _url = url;
            Title = "CouchTV phone screens";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Background = Brushes.Black;
            if (windowed)
            {
                Left = 40;
                Top = 40;
                Width = 960;
                Height = 540;
            }
            else
            {
                Left = 0;
                Top = 0;
                Width = SystemParameters.PrimaryScreenWidth;
                Height = SystemParameters.PrimaryScreenHeight;
            }
            _note = Theme.Text("Showing a phone…", 30, Theme.Muted, FontWeights.Normal);
            _note.HorizontalAlignment = HorizontalAlignment.Center;
            _note.VerticalAlignment = VerticalAlignment.Center;
            _grid.Children.Add(_note);
            Content = _grid;
            SourceInitialized += (s, e) =>
            {
                // Over the app in front without taking its keyboard, and not in the Alt+Tab list.
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int exStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, exStyle | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            };
            Loaded += (s, e) => ShowPage();
            Closed += (s, e) =>
            {
                IDisposable view = _view as IDisposable;
                if (view != null) view.Dispose();
            };
        }

        void ShowPage()
        {
            try { ShowPageNow(); }
            catch (Exception ex)
            {
                _note.Text = "Phones can't be shown on this TV: it needs Microsoft Edge WebView2";
                Log.Info("Phone screens: no WebView2 (" + ex.Message + ")");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]   // WebView2's types load in here, inside ShowPage's try
        void ShowPageNow()
        {
            TrailerPlayer.UseWebView2();
            var view = new WebView2 { Focusable = false, IsHitTestVisible = false, DefaultBackgroundColor = System.Drawing.Color.Black };
            _view = view;
            _grid.Children.Add(view);
            Start(view);
        }

        async void Start(WebView2 view)
        {
            try
            {
                // Sound without a click first; and out of Windows' media controls, so a phone isn't "what's playing".
                var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required --disable-features=HardwareMediaKeyHandling");
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Config.DataDir, "phonescreens"), options);
                await view.EnsureCoreWebView2Async(environment);
                CoreWebView2 core = view.CoreWebView2;
                BrowserProcess = (int)core.BrowserProcessId;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Navigate(_url);
                _note.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                _note.Text = "Phones can't be shown on this TV just now";
                Log.Info("Phone screens: WebView2 didn't start (" + ex.Message + ")");
            }
        }
    }
}
