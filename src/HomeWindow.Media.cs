using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace CouchTV
{
    // What's playing (NowPlaying, from Windows' media controls). Music, or anything on YouTube, goes on behind the home
    // screen after Home instead of closing, and behind other apps opened meanwhile, with a card at the top of the home
    // screen to see and work it (Up from the apps), and over other apps the mini player at the top right (MiniPlayer);
    // phones show it as a notification with its buttons (MEDIA lines). A film closes on Home as before.
    internal sealed partial class HomeWindow
    {
        const double CardW = 760, CardH = 184, CardTop = 92, ArtBox = 140;

        // Window titles that mean a film or a show: those need the screen, so Home closes them even while they play.
        static readonly string[] FilmApps = { "Netflix", "Prime Video", "Hotstar", "SonyLIV", "ZEE5", "Disney+", "JioCinema", "Apple TV" };

        // Names to show for where it plays, found in its window's title (YouTube Music before YouTube).
        static readonly string[] KnownApps =
        {
            "YouTube Music", "YouTube", "Spotify", "JioSaavn", "Gaana", "Wynk", "SoundCloud", "Apple Music", "Amazon Music",
            "Netflix", "Prime Video", "JioHotstar", "Hotstar", "SonyLIV", "ZEE5", "VLC", "Kodi",
        };

        IntPtr _bgWindow;              // the app left playing behind the home screen, else zero
        Tile _bgTile;                  // the tile it was opened from, when known: opening that tile brings it back
        DateTime _cardQuietUntil;      // just after Home closed the apps: what they played is on its way out
        bool _mediaFocused;            // the card has the remote's focus (Up from the apps)
        volatile string _mediaApp = "";   // where it plays ("YouTube"), for the phones' lines (read on the network thread)
        string _artTold = "", _artShown = "", _titleShown;
        bool _cardShown;
        MiniPlayer _mini;              // over other apps while the music plays behind them
        DispatcherTimer _playPauseHold;

        FrameworkElement _card, _cardRing, _cardText, _cardBarHost;
        Grid _cardHints;
        readonly ScaleTransform _cardScale = new ScaleTransform(1, 1), _cardBar = new ScaleTransform(0, 1);
        readonly TranslateTransform _cardSlide = new TranslateTransform();
        Image _cardArt, _cardGlow;
        TextBlock _cardStatus, _cardTitle, _cardArtist, _cardAt, _cardLength, _cardPlay, _cardPlaceholder;
        Ellipse _cardDot;

        void StartMedia()
        {
            NowPlaying.Changed += MediaChanged;
            NowPlaying.Start(Dispatcher);
            Activated += (s, e) => UpdateMini();
            Deactivated += (s, e) => UpdateMini();
            if (_wifi == null) return;
            _wifi.CurrentMedia = () => MediaLines(NowPlaying.Current, _mediaApp, true);
            _wifi.Media += (what, ms) =>
            {
                if (what == "stop") StopBackground();
                else if (what == "next") MediaNext();
                else NowPlaying.Command(what, ms);
            };
            StartQueue();
        }

        void MediaChanged()
        {
            MediaNow now = NowPlaying.Current;
            if (_bgWindow != IntPtr.Zero && !Native.IsWindow(_bgWindow)) ForgetBackground();
            string title;
            IntPtr window = now == null ? IntPtr.Zero : MediaWindow(now, out title);
            _mediaApp = now == null ? "" : AppName(now, window);
            UpdateMediaCard(!_opt.Preview);
            UpdateMini();
            QueueMediaChanged();
            if (_wifi == null) return;
            bool art = now != null && now.ArtId != _artTold;
            if (now != null) _artTold = now.ArtId;
            _wifi.SendToPhones(MediaLines(now, _mediaApp, art));
        }

        /// <summary>
        /// For phones: "MEDIA none", or MEDIA &lt;playing|paused&gt; &lt;position ms&gt; &lt;length ms, 0 unknown&gt; &lt;flags&gt; &lt;picture
        /// id&gt; &lt;app&gt; &lt;title&gt; &lt;artist&gt; (texts in base64, "-" for none; flags: P play/pause, N next, B previous, S seek),
        /// after MEDIAART &lt;id&gt; &lt;JPEG, base64&gt; when the picture's new to them ("MEDIAART -": none). From any thread.
        /// </summary>
        internal static List<string> MediaLines(MediaNow now, string app, bool art)
        {
            var lines = new List<string>();
            if (now == null)
            {
                lines.Add("MEDIA none");
                return lines;
            }
            if (art) lines.Add("MEDIAART " + now.ArtId + (now.Art != null ? " " + Convert.ToBase64String(now.Art) : ""));
            string flags = (now.CanPlayPause ? "P" : "") + (now.CanNext ? "N" : "") + (now.CanPrevious || now.CanSeek ? "B" : "") + (now.CanSeek ? "S" : "");
            lines.Add("MEDIA " + (now.Playing ? "playing" : "paused") + " " + now.PositionAt(DateTime.UtcNow) + " " + now.DurationMs + " " +
                      (flags.Length > 0 ? flags : "-") + " " + now.ArtId + " " + MediaB64(app) + " " + MediaB64(now.Title) + " " +
                      MediaB64(now.Artist.Length > 0 ? now.Artist : now.Album));
            return lines;
        }

        static string MediaB64(string text) { return string.IsNullOrEmpty(text) ? "-" : Convert.ToBase64String(Encoding.UTF8.GetBytes(text)); }

        // ================================================================ playing on behind the home screen

        bool BackgroundAlive { get { return _bgWindow != IntPtr.Zero && Native.IsWindow(_bgWindow); } }

        void ForgetBackground()
        {
            _bgWindow = IntPtr.Zero;
            _bgTile = null;
            NowPlaying.Prefer = null;
            if (_mini != null) _mini.HideNow();
        }

        /// <summary>
        /// Home, or another app opening: the window to leave open behind, or zero to close everything as before. Music,
        /// or anything on YouTube, that's playing goes on, and so does one already left behind (unless it's the app in
        /// front, paused); a film, or anything paused, closes.
        /// </summary>
        IntPtr KeepPlaying()
        {
            if (_bgWindow != IntPtr.Zero && !Native.IsWindow(_bgWindow)) ForgetBackground();
            if (BackgroundAlive && Native.GetForegroundWindow() != _bgWindow) return _bgWindow;
            MediaNow now = NowPlaying.Current;
            if (now == null || !now.Playing) return IntPtr.Zero;
            string title;
            IntPtr window = MediaWindow(now, out title);
            if (window == IntPtr.Zero || IsFilm(title)) return IntPtr.Zero;
            _bgWindow = window;
            _bgTile = TileFor(title);
            NowPlaying.Prefer = now.Source;
            Log.Info("Home: \"" + now.Title + "\" plays on behind the home screen (" + Shell.WindowProcess(window) + ")");
            return window;
        }

        static bool IsFilm(string title)
        {
            if (title.IndexOf("YouTube", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            foreach (string app in FilmApps)
                if (title.IndexOf(app, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>
        /// The app window it plays in: one whose title has the track's title in it (YouTube's do), else the one window
        /// of the program Windows says plays it (Brave, Edge...) that's a music app or YouTube, else zero.
        /// </summary>
        IntPtr MediaWindow(MediaNow now, out string title)
        {
            title = "";
            IntPtr best = IntPtr.Zero;
            int bestScore = 0, ofProgram = 0;
            foreach (IntPtr hwnd in Shell.FindAppWindows(_hwnd, null))
            {
                string t = Shell.WindowTitle(hwnd), process = Shell.WindowProcess(hwnd) ?? "";
                bool program = process.Length > 0 && now.Source.IndexOf(process, StringComparison.OrdinalIgnoreCase) >= 0;
                if (program) ofProgram++;
                int score = (now.Title.Length >= 2 && t.IndexOf(now.Title, StringComparison.OrdinalIgnoreCase) >= 0 ? 4 : 0) +
                            (program ? 2 : 0) + (program && KnownApp(t) != null ? 1 : 0);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = hwnd;
                    title = t;
                }
            }
            if (bestScore >= 3 || (bestScore == 2 && ofProgram == 1)) return best;
            title = "";
            return IntPtr.Zero;
        }

        static string KnownApp(string title)
        {
            foreach (string app in KnownApps)
                if (title.IndexOf(app, StringComparison.OrdinalIgnoreCase) >= 0) return app;
            return null;
        }

        /// <summary>Where it plays, to show: a known app in its window's title, else the program ("Brave", "Edge").</summary>
        static string AppName(MediaNow now, IntPtr window)
        {
            string known = window != IntPtr.Zero ? KnownApp(Shell.WindowTitle(window)) : null;
            if (known != null) return known == "Hotstar" ? "JioHotstar" : known;
            string source = now.Source;
            int bang = source.IndexOf('!');
            if (bang >= 0) source = source.Substring(bang + 1);   // a Store app: "SpotifyAB.SpotifyMusic_...!Spotify"
            if (source.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) source = source.Substring(0, source.Length - 4);
            if (source.Equals("MSEdge", StringComparison.OrdinalIgnoreCase)) return "Edge";
            return source.Length > 24 ? source.Substring(0, 24) : source;
        }

        /// <summary>The app tile whose name is in the window's title ("YouTube" in "Song - YouTube"), else null.</summary>
        Tile TileFor(string title)
        {
            Tile best = null;
            foreach (Tile t in _cfg.AppTiles)
                if (!string.IsNullOrEmpty(t.Label) && title.IndexOf(t.Label, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (best == null || t.Label.Length > best.Label.Length)) best = t;
            return best;
        }

        /// <summary>The tile that plays behind, opened as it is (not a search or a song in it): bring that back instead.</summary>
        bool IsBackgroundTile(Tile t)
        {
            return BackgroundAlive && _bgTile != null && t.Name == _bgTile.Name && t.Url == _bgTile.Url;
        }

        /// <summary>Back to the app that plays behind (or, with none left behind, the window it plays in).</summary>
        void OpenBackground()
        {
            IntPtr window = BackgroundAlive ? _bgWindow : IntPtr.Zero;
            if (window == IntPtr.Zero && NowPlaying.Current != null)
            {
                string title;
                window = MediaWindow(NowPlaying.Current, out title);
            }
            if (window == IntPtr.Zero)
            {
                Toast("Couldn't find the app it's playing in");
                return;
            }
            _mediaFocused = false;
            RefreshFocus(false);
            Shell.BringBack(window);
        }

        // ================================================================ the mini player, over other apps

        bool MiniShown { get { return _mini != null && _mini.IsVisible; } }

        /// <summary>Up while music plays behind an app other than the home screen (or the app it plays in).</summary>
        void UpdateMini()
        {
            if (_opt.Preview || _opt.Windowed) return;
            IntPtr front = Native.GetForegroundWindow();
            if (front == IntPtr.Zero) return;   // between two windows
            MediaNow now = NowPlaying.Current;
            if (now == null || !BackgroundAlive || front == _hwnd || front == _bgWindow || Shell.ExplorerRunning)
            {
                if (_mini != null) _mini.HideNow();
                return;
            }
            if (_mini == null)
            {
                _mini = new MiniPlayer();
                _mini.Pressed += what =>
                {
                    if (what == "open")
                    {
                        _mini.Fold(true);
                        OpenBackground();
                    }
                    else if (what == "stop") StopBackground();
                    else if (what == "next") MediaNext();
                    else NowPlaying.Command(what);
                };
            }
            _mini.Show(now, _mediaApp);
        }

        /// <summary>
        /// Play/Pause with the mini player up over an app: a press is still Space for the app in front (a film pauses);
        /// held, it opens the mini player, whose arrows, OK and Back then work it.
        /// </summary>
        void HoldPlayPause()
        {
            if (_playPauseHold == null)
            {
                _playPauseHold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                _playPauseHold.Tick += (s, e) =>
                {
                    DateTime now = DateTime.Now;
                    bool held = _irAction != null && _irAction.Equals("playpause", StringComparison.OrdinalIgnoreCase) && (now - _irLast).TotalMilliseconds < 240;
                    if (!held)
                    {
                        _playPauseHold.Stop();
                        Input.Key(0x20);
                        return;
                    }
                    if ((now - _irFirst).TotalMilliseconds < 600) return;
                    _playPauseHold.Stop();
                    _irHoldDone = true;
                    if (MiniShown) _mini.Open();
                };
            }
            _playPauseHold.Start();
        }

        /// <summary>The card's Back (or a phone's stop): closes the app left playing behind; else just pauses.</summary>
        void StopBackground()
        {
            if (BackgroundAlive)
            {
                Log.Info("Stopped what played behind the home screen");
                Native.PostMessage(_bgWindow, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                ForgetBackground();
                _cardQuietUntil = DateTime.UtcNow.AddSeconds(3);
                _mediaFocused = false;
                UpdateMediaCard(true);
                RefreshFocus(true);
                LaterUpdateCard(3.2);
            }
            else NowPlaying.Command("pause");
        }

        /// <summary>Home closed the apps: whatever they played takes a moment to go, so no card for it meanwhile.</summary>
        void QuietCardAfterHome()
        {
            _cardQuietUntil = DateTime.UtcNow.AddSeconds(3);
            UpdateMediaCard(false);
            LaterUpdateCard(3.2);
        }

        void LaterUpdateCard(double seconds)
        {
            var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            later.Tick += (s, e) =>
            {
                later.Stop();
                UpdateMediaCard(true);
            };
            later.Start();
        }

        // ================================================================ the card

        bool CardWanted
        {
            get { return NowPlaying.Current != null && (BackgroundAlive || DateTime.UtcNow >= _cardQuietUntil); }
        }

        void BuildMediaCard()
        {
            var card = new Grid { Width = CardW, Height = CardH };
            // The picture, blurred, behind a dark veil: the card takes on the song's colours.
            var back = new Grid { Clip = new RectangleGeometry(new Rect(0, 0, CardW, CardH), 30, 30), CacheMode = new BitmapCache() };
            back.Children.Add(new Rectangle { Fill = Theme.Brush(Theme.Panel) });
            _cardGlow = new Image { Stretch = Stretch.UniformToFill, Margin = new Thickness(-80), Opacity = 0.6, Effect = new BlurEffect { Radius = 80 } };
            back.Children.Add(_cardGlow);
            back.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Theme.Alpha(Theme.Base, 0xC8), Theme.Alpha(Theme.Base, 0x70), 0) });
            card.Children.Add(back);
            card.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(30), BorderThickness = new Thickness(1.5), BorderBrush = Theme.Brush(Color.FromArgb(0x30, 255, 255, 255)),
            });

            var content = new Grid { Margin = new Thickness(22) };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var art = new Grid { Width = ArtBox, Height = ArtBox, Clip = new RectangleGeometry(new Rect(0, 0, ArtBox, ArtBox), 20, 20) };
            art.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Theme.Hex("#2E3850"), Theme.Hex("#1A2030"), 45) });
            _cardPlaceholder = Theme.Text("", 60, Theme.Muted, FontWeights.Normal, Theme.Icons);
            _cardPlaceholder.HorizontalAlignment = HorizontalAlignment.Center;
            _cardPlaceholder.VerticalAlignment = VerticalAlignment.Center;
            art.Children.Add(_cardPlaceholder);
            _cardArt = new Image { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(_cardArt, BitmapScalingMode.HighQuality);
            art.Children.Add(_cardArt);
            content.Children.Add(art);

            var text = new StackPanel { Margin = new Thickness(26, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center };
            var status = new StackPanel { Orientation = Orientation.Horizontal };
            _cardDot = new Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            _cardStatus = Theme.Text("", 19, Theme.Muted, FontWeights.SemiBold);
            status.Children.Add(_cardDot);
            status.Children.Add(_cardStatus);
            text.Children.Add(status);
            _cardTitle = Theme.Text("", 30, Theme.Ink, FontWeights.SemiBold);
            _cardTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            _cardTitle.Margin = new Thickness(0, 4, 0, 0);
            text.Children.Add(_cardTitle);
            _cardArtist = Theme.Text("", 22, Theme.Muted, FontWeights.Normal);
            _cardArtist.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(_cardArtist);
            // The timeline: where it's got to, the bar, how long it is.
            var barHost = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            barHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            barHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            barHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _cardAt = Theme.Text("", 17, Theme.Muted, FontWeights.Normal);
            _cardAt.MinWidth = 44;
            _cardLength = Theme.Text("", 17, Theme.Faint, FontWeights.Normal);
            _cardLength.MinWidth = 44;
            _cardLength.TextAlignment = TextAlignment.Right;
            var bar = new Grid { Height = 6, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            bar.Children.Add(new Rectangle { RadiusX = 3, RadiusY = 3, Fill = Theme.Brush(Color.FromArgb(0x38, 255, 255, 255)) });
            bar.Children.Add(new Rectangle { RadiusX = 3, RadiusY = 3, Fill = Theme.Brush(Theme.Ink), RenderTransform = _cardBar });
            Grid.SetColumn(bar, 1);
            Grid.SetColumn(_cardLength, 2);
            barHost.Children.Add(_cardAt);
            barHost.Children.Add(bar);
            barHost.Children.Add(_cardLength);
            _cardBarHost = barHost;
            text.Children.Add(barHost);
            Grid.SetColumn(text, 1);
            content.Children.Add(text);
            _cardText = text;

            var play = new Grid { Width = 68, Height = 68, VerticalAlignment = VerticalAlignment.Center };
            play.Children.Add(new Ellipse { Fill = Theme.Brush(Color.FromArgb(0x24, 255, 255, 255)) });
            _cardPlay = Theme.Text("", 28, Theme.Ink, FontWeights.Normal, Theme.Icons);
            _cardPlay.HorizontalAlignment = HorizontalAlignment.Center;
            _cardPlay.VerticalAlignment = VerticalAlignment.Center;
            play.Children.Add(_cardPlay);
            Grid.SetColumn(play, 2);
            content.Children.Add(play);
            card.Children.Add(content);

            var host = new Grid { Width = CardW, Height = CardH, RenderTransformOrigin = new Point(0.5, 0.5), Opacity = 0, Visibility = Visibility.Collapsed };
            var transforms = new TransformGroup();
            transforms.Children.Add(_cardScale);
            transforms.Children.Add(_cardSlide);
            host.RenderTransform = transforms;
            var ring = new Border
            {
                CornerRadius = new CornerRadius(38), BorderThickness = new Thickness(4), BorderBrush = Theme.Brush(Colors.White),
                Margin = new Thickness(-9), Opacity = 0,
            };
            host.Children.Add(ring);
            host.Children.Add(card);
            host.MouseLeftButtonUp += (s, e) =>
            {
                if (!_pointer || Modal) return;   // OK from an air mouse: OnClick
                e.Handled = true;
                OpenBackground();
            };
            _cardRing = ring;
            _card = host;
            Place(host, (DesignW - CardW) / 2, CardTop);

            _cardHints = new Grid { Width = 1200, Opacity = 0, IsHitTestVisible = false };
            Place(_cardHints, DesignW / 2 - 600, CardTop + CardH + 20);
            _titleShown = null;
            _artShown = "";
            _cardShown = false;
        }

        /// <summary>Shows, hides or brings the card up to date with what's playing.</summary>
        void UpdateMediaCard(bool animate)
        {
            if (_card == null) return;
            MediaNow now = NowPlaying.Current;
            bool shown = CardWanted;
            if (shown != _cardShown)
            {
                _cardShown = shown;
                _card.IsHitTestVisible = shown;
                if (shown)
                {
                    _card.Visibility = Visibility.Visible;
                    if (animate)
                    {
                        _cardSlide.Y = -24;
                        _card.Opacity = 0;
                    }
                    Theme.AnimateTo(_card, OpacityProperty, 1, animate);
                    Theme.AnimateTo(_cardSlide, TranslateTransform.YProperty, 0, animate);
                }
                else
                {
                    if (_mediaFocused)
                    {
                        _mediaFocused = false;
                        RefreshFocus(animate);
                    }
                    Theme.AnimateTo(_card, OpacityProperty, 0, animate);
                    Theme.AnimateTo(_cardSlide, TranslateTransform.YProperty, -24, animate);
                    if (!animate) _card.Visibility = Visibility.Collapsed;
                }
                // Toasts go to the bottom while the card has the top.
                var toast = (FrameworkElement)_toast;
                toast.VerticalAlignment = shown ? VerticalAlignment.Bottom : VerticalAlignment.Top;
                toast.Margin = shown ? new Thickness(0, 0, 0, 70) : new Thickness(0, 74, 0, 0);
                StyleMediaCard(animate);
            }
            if (!shown || now == null) return;

            string app = _mediaApp.Length > 0 ? _mediaApp : "this TV";
            _cardStatus.Text = (now.Playing ? "Playing" : "Paused") + " · " + app;
            _cardDot.Fill = Theme.Brush(now.Playing ? Theme.Good : Theme.Faint);
            _cardPlay.Text = now.Playing ? "" : "";   // what Play/Pause does now
            string artist = now.Artist.Length > 0 ? now.Artist : now.Album;
            if (now.Title != _titleShown)
            {
                _titleShown = now.Title;
                _cardTitle.Text = now.Title.Length > 0 ? now.Title : artist;
                _cardArtist.Text = now.Title.Length > 0 ? artist : "";
                if (animate) _cardText.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(350)));
            }
            else
            {
                _cardTitle.Text = now.Title.Length > 0 ? now.Title : artist;
                _cardArtist.Text = now.Title.Length > 0 ? artist : "";
            }
            if (now.ArtId != _artShown)
            {
                _artShown = now.ArtId;
                BitmapImage picture = ArtImage(now.Art);
                _cardArt.Source = picture;
                _cardGlow.Source = picture;
                _cardPlaceholder.Visibility = picture == null ? Visibility.Visible : Visibility.Collapsed;
                if (animate && picture != null) _cardArt.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
            }
            _cardBarHost.Visibility = now.DurationMs > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_mediaFocused) StyleMediaCard(animate);   // its hints say Play or Pause
            TickMediaCard(false);
        }

        static BitmapImage ArtImage(byte[] jpeg)
        {
            if (jpeg == null) return null;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(jpeg);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Every second: the timeline moves on, smoothly, to where it will be at the next tick.</summary>
        void TickMediaCard(bool tick)
        {
            if (_card == null) return;
            if (tick && _bgWindow != IntPtr.Zero && !Native.IsWindow(_bgWindow))
            {
                ForgetBackground();
                UpdateMediaCard(true);
            }
            if (tick)
            {
                UpdateMini();
                TickQueue();
            }
            MediaNow now = NowPlaying.Current;
            if (!_cardShown || now == null || now.DurationMs <= 0) return;
            DateTime utc = DateTime.UtcNow;
            long at = now.PositionAt(utc);
            _cardAt.Text = Clock(at);
            _cardLength.Text = Clock(now.DurationMs);
            double from = Math.Min(1, (double)at / now.DurationMs);
            if (!now.Playing || _opt.Preview)
            {
                _cardBar.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _cardBar.ScaleX = from;
                return;
            }
            double to = Math.Min(1, (double)now.PositionAt(utc.AddSeconds(1.05)) / now.DurationMs);
            _cardBar.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(from, to, TimeSpan.FromSeconds(1.05)));
        }

        static string Clock(long ms)
        {
            long s = Math.Max(0, ms / 1000);
            return s >= 3600 ? string.Format("{0}:{1:00}:{2:00}", s / 3600, s / 60 % 60, s % 60) : string.Format("{0}:{1:00}", s / 60, s % 60);
        }

        /// <summary>The card with the remote's focus (ring, a little bigger, the keys to use) or without (how to get there).</summary>
        void StyleMediaCard(bool animate)
        {
            if (_card == null) return;
            bool on = _mediaFocused && _cardShown;
            Theme.AnimateTo(_cardRing, OpacityProperty, on ? 1 : 0, animate);
            Theme.AnimateTo(_cardScale, ScaleTransform.ScaleXProperty, on ? 1.04 : 1, animate);
            Theme.AnimateTo(_cardScale, ScaleTransform.ScaleYProperty, on ? 1.04 : 1, animate);
            _card.Effect = on ? new DropShadowEffect { BlurRadius = 48, ShadowDepth = 12, Direction = 270, Opacity = 0.6, Color = Colors.Black } : null;
            var keys = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            MediaNow now = NowPlaying.Current;
            if (on)
            {
                keys.Children.Add(CardHint("OK", "Open"));
                if (now == null || now.CanPlayPause) keys.Children.Add(CardHint("", now != null && now.Playing ? "Pause" : "Play", true));
                if (now == null || now.CanNext || now.CanPrevious || now.CanSeek) keys.Children.Add(CardHint("← →", "Previous · Next"));
                keys.Children.Add(CardHint("Back", BackgroundAlive ? "Stop" : "Pause"));
            }
            else keys.Children.Add(CardHint("↑", "Play, pause or skip"));
            _cardHints.Children.Clear();
            _cardHints.Children.Add(keys);
            Theme.AnimateTo(_cardHints, OpacityProperty, _cardShown ? (on ? 1 : 0.75) : 0, animate);
        }

        static UIElement CardHint(string key, string action, bool icons = false)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 16, 0) };
            TextBlock label = Theme.Text(key, icons ? 15 : 18, Theme.Muted, FontWeights.SemiBold, icons ? Theme.Icons : null);
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.5), Padding = new Thickness(9, icons ? 5 : 1, 9, icons ? 5 : 3),
                BorderBrush = Theme.Brush(Theme.Faint), VerticalAlignment = VerticalAlignment.Center, Child = label,
            });
            TextBlock text = Theme.Text(action, 20, Theme.Faint, FontWeights.Normal);
            text.Margin = new Thickness(10, 0, 0, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text);
            return row;
        }

        /// <summary>Up from the top row reaches the card, when there is one.</summary>
        bool FocusMediaCard()
        {
            if (_mediaFocused || !_cardShown || Modal) return false;
            _mediaFocused = true;
            RefreshFocus(true);
            return true;
        }

        /// <summary>A key while the card has the focus; false for keys it leaves to the home screen.</summary>
        bool MediaCardKey(Key key)
        {
            MediaNow now = NowPlaying.Current;
            switch (key)
            {
                case Key.Up: return true;
                case Key.Down:
                    _mediaFocused = false;
                    RefreshFocus(true);
                    return true;
                case Key.Left:
                    if (now != null && (now.CanPrevious || now.CanSeek)) NowPlaying.Command("previous");
                    return true;
                case Key.Right:
                    if (now != null && (now.CanNext || _upNext.Count > 0)) MediaNext();
                    return true;
                case Key.Enter:
                case Key.Space:
                    OpenBackground();
                    return true;
                case Key.Escape:
                case Key.BrowserBack:
                case Key.Back:
                    StopBackground();
                    return true;
                default:
                    return false;
            }
        }
    }
}
