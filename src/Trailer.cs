using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CouchTV
{
    /// <summary>
    /// The home screen hero's trailers: the highlighted title's YouTube trailer, in a WebView2 (Edge's engine, part of
    /// Windows 10 and 11) laid over the hero's picture, as streaming apps do (home/trailer.html). WebView2's own files
    /// ride inside CouchTV.exe, since updates only replace the exe, and are unpacked to %LOCALAPPDATA%\CouchTV\webview2
    /// when first needed. Without the WebView2 runtime there are no trailers, just the pictures.
    /// </summary>
    internal sealed class TrailerPlayer
    {
        static readonly object UnpackLock = new object();
        static bool _resolving, _unpacked;

        // YouTube's ads in its embedded player: refused.
        static readonly string[] AdFilters =
        {
            "*://*.doubleclick.net/*", "*://*.googlesyndication.com/*", "*://*.googleadservices.com/*",
            "*://www.youtube.com/api/stats/ads*", "*://www.youtube.com/pagead/*", "*://*.youtube.com/get_midroll_*",
        };

        static string Folder { get { return Path.Combine(Config.DataDir, "webview2"); } }

        /// <summary>The self-test: the page plays with sound as asked, but WebView2 itself is muted (nothing comes out).</summary>
        internal static bool Silent;

        /// <summary>At start-up, before anything uses WebView2: its assemblies come from the copies unpacked to Folder.</summary>
        public static void Prepare()
        {
            if (_resolving) return;
            _resolving = true;
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string name = new AssemblyName(e.Name).Name;
                if (name != "Microsoft.Web.WebView2.Core" && name != "Microsoft.Web.WebView2.Wpf") return null;
                try
                {
                    Unpack();
                    return Assembly.LoadFrom(Path.Combine(Folder, name + ".dll"));
                }
                catch (Exception ex)
                {
                    Log.Info("Trailers: WebView2's " + name + ": " + ex.Message);
                    return null;
                }
            };
        }

        /// <summary>WebView2's files, out of CouchTV.exe into Folder (only those that changed).</summary>
        static void Unpack()
        {
            lock (UnpackLock)
            {
                if (_unpacked) return;
                Directory.CreateDirectory(Folder);
                Assembly me = Assembly.GetExecutingAssembly();
                string loader = Environment.Is64BitProcess ? "WebView2Loader.x64.dll" : "WebView2Loader.x86.dll";
                foreach (string file in new[] { "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.Wpf.dll", loader })
                {
                    using (Stream input = me.GetManifestResourceStream("CouchTV.lib." + file))
                    {
                        if (input == null) throw new FileNotFoundException(file + " isn't inside CouchTV.exe");
                        var bytes = new MemoryStream();
                        input.CopyTo(bytes);
                        string to = Path.Combine(Folder, file == loader ? "WebView2Loader.dll" : file);
                        try
                        {
                            if (!File.Exists(to) || !Same(File.ReadAllBytes(to), bytes.ToArray())) File.WriteAllBytes(to, bytes.ToArray());
                        }
                        catch (IOException ex) { Log.Info("Trailers: couldn't refresh " + to + " (" + ex.Message + "); using the one there"); }
                    }
                }
                _unpacked = true;
            }
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ---------------------------------------------------------------- the player

        readonly FrameworkElement _view;   // a WebView2 (kept as its base type, so this class loads without WebView2)
        CoreWebView2 _core;
        bool _pageReady;                   // YouTube's player can be asked (the page said "ready")
        readonly System.Collections.Generic.List<object> _frames = new System.Collections.Generic.List<object>();   // YouTube's frames (CoreWebView2Frame)
        string _pending;                   // a play asked for before then

        /// <summary>
        /// What the page says: "playing", "ended", "error N", "stopped" or "state N"; or "failed why" if WebView2
        /// didn't start. On the UI thread.
        /// </summary>
        public event Action<string> Said;

        /// <summary>Why Create gave no player, in a few words.</summary>
        public static string Problem { get; private set; }

        /// <summary>YouTube's player has loaded in the page.</summary>
        public bool PageReady { get { return _pageReady; } }

        /// <summary>The view, for the hero (hidden until a trailer plays).</summary>
        public FrameworkElement View { get { return _view; } }

        /// <summary>WebView2 has started (the page may still be loading: a play then waits for it).</summary>
        public bool Ready { get { return _core != null; } }

        TrailerPlayer(FrameworkElement view) { _view = view; }

        /// <summary>A player, whose view goes into the hero; null if WebView2 can't be used here.</summary>
        public static TrailerPlayer Create(Color background)
        {
            try { return CreateNow(background); }
            catch (Exception ex)
            {
                Log.Info("Trailers: no WebView2 here (" + ex.Message + "); the hero shows pictures only");
                Problem = "WebView2 couldn't be loaded (" + ex.Message + ")";
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]   // WebView2's types load in here, inside Create's try
        static TrailerPlayer CreateNow(Color background)
        {
            Unpack();
            CoreWebView2Environment.SetLoaderDllFolderPath(Folder);
            var view = new WebView2
            {
                Visibility = Visibility.Hidden, Focusable = false, IsHitTestVisible = false,
                DefaultBackgroundColor = System.Drawing.Color.Transparent,   // see-through: the home screen shows beneath
            };
            var player = new TrailerPlayer(view);
            // Once the window's running (WebView2 won't start before the event loop has).
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => player.Start(view, background)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return player;
        }

        async void Start(WebView2 view, Color background)
        {
            try
            {
                // Plays without a click; and stays out of Windows' media controls, so a trailer isn't "what's playing".
                var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required --disable-features=HardwareMediaKeyHandling");
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Config.DataDir, "trailers"), options);
                await view.EnsureCoreWebView2Async(environment);
                CoreWebView2 core = view.CoreWebView2;
                if (Silent) core.IsMuted = true;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                foreach (string ad in AdFilters) core.AddWebResourceRequestedFilter(ad, CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (s, e) => e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                // YouTube's own player, in its frame: none of its buttons, titles or overlays over the trailer.
                core.FrameCreated += (s, e) =>
                {
                    CoreWebView2Frame frame = e.Frame;
                    _frames.Add(frame);
                    frame.NavigationCompleted += (fs, fe) => frame.ExecuteScriptAsync(HideYouTubeChrome);
                };
                core.WebMessageReceived += (s, e) =>
                {
                    string said = e.TryGetWebMessageAsString();
                    if (said == "ready")
                    {
                        _pageReady = true;
                        if (_pending != null) _core.ExecuteScriptAsync(_pending);
                        _pending = null;
                        return;
                    }
                    if (Said != null) Said(said);
                };
                // The page from a folder of its own, at an https address: YouTube's player wants a proper page to sit in.
                string page = Path.Combine(Folder, "page");
                Directory.CreateDirectory(page);
                File.WriteAllText(Path.Combine(page, "trailer.html"), Html(background), new UTF8Encoding(false));
                core.SetVirtualHostNameToFolderMapping("couchtv.home", page, CoreWebView2HostResourceAccessKind.Allow);
                core.Navigate("https://couchtv.home/trailer.html");
                _core = core;
                Log.Info("Trailers: ready (WebView2 " + environment.BrowserVersionString + ")");
            }
            catch (Exception ex)
            {
                Log.Info("Trailers: WebView2 didn't start (" + ex.Message + "); the hero shows pictures only");
                if (Said != null) Said("failed " + (ex is WebView2RuntimeNotFoundException ? "WebView2 isn't installed on this PC" : "WebView2 didn't start (" + ex.Message + ")"));
            }
        }

        const string HideYouTubeChrome =
            "(function(){if(!/youtube/.test(location.hostname))return;var s=document.createElement('style');s.textContent=" +
            "'.ytp-chrome-top,.ytp-chrome-bottom,.ytp-gradient-top,.ytp-gradient-bottom,.ytp-pause-overlay,.ytp-large-play-button," +
            ".ytp-bezel,.ytp-bezel-text,.ytp-watermark,.ytp-ce-element,.ytp-cards-teaser,.ytp-paid-content-overlay,.ytp-spinner," +
            ".ytp-show-cards-title,.ytp-impression-link,.ytp-youtube-button,.iv-branding,.ytp-overlay,.ytp-player-content," +
            ".ytp-cued-thumbnail-overlay,.ytp-touch-mode .ytp-chrome-controls," +
            // its touch-style player (YouTube serves it here: WebView2 says the PC can be touched)
            "[class*=\"player-controls\"],[class*=\"ytmCued\"],ytm-watch-player-controls,ytm-custom-control,.ytm-autonav-bar,.fullscreen-icon" +
            "{display:none!important}';" +
            "(document.head||document.documentElement).appendChild(s);})();";

        static string Html(Color background)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CouchTV.home.trailer.html"))
            using (var reader = new StreamReader(s, Encoding.UTF8))
                return reader.ReadToEnd().Replace("{{BASE}}", string.Format("#{0:X2}{1:X2}{2:X2}", background.R, background.G, background.B));
        }

        /// <summary>Plays a YouTube video (its 11-character id) from the start, fading in over the home screen's picture.</summary>
        public void Play(string video, bool muted)
        {
            if (_core == null || video == null || !Regex.IsMatch(video, @"^[\w-]{11}$")) return;
            string script = "play('" + video + "', " + (muted ? "true" : "false") + ")";
            if (_pageReady) _core.ExecuteScriptAsync(script);
            else _pending = script;
        }

        /// <summary>For the self-test: what's at the middle of YouTube's player, and whether the hiding took.</summary>
        internal void Probe(Action<string> said)
        {
            foreach (object f in _frames)
            {
                var frame = (CoreWebView2Frame)f;
                frame.ExecuteScriptAsync("(function(){var e=document.elementFromPoint(innerWidth/2,innerHeight/2);var a=[];for(var n=e;n&&a.length<6;n=n.parentElement)a.push(n.tagName+'.'+(n.className&&n.className.baseVal!==undefined?n.className.baseVal:n.className));" +
                    "return location.host+' | styles '+document.querySelectorAll('style').length+' | '+a.join(' < ');})()")
                    .ContinueWith(t => { if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion) said(t.Result); });
            }
        }

        public void Stop()
        {
            _pending = null;
            if (_core != null && _pageReady) _core.ExecuteScriptAsync("stop()");
        }
    }
}
