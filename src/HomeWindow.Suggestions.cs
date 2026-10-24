using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CouchTV
{
    /// <summary>
    /// The suggestion rows under the apps: popular films and series, each on the app it streams on, what's popular on
    /// each app you pay for, and India's most-played songs, on YouTube (see Suggestions). OK plays it in that app (a
    /// series opens on its page). The phone remote gets the same list for its first page, and plays from it with PLAY.
    /// </summary>
    internal sealed partial class HomeWindow
    {
        // Big, for a sofa 7 or 8 feet from the TV: one row at a time under the hero, about six posters across.
        const double PosterW = 236, PosterH = 354, CoverSize = 300, CardGap = 28, SuggestionTextH = 0, ChannelW = 480, ChannelH = 270;

        List<Suggestion> _suggestions = new List<Suggestion>();
        DateTime _suggestFetched = DateTime.MinValue, _suggestTried = DateTime.MinValue;   // UTC
        bool _suggestFetching;
        List<string> _phoneSuggestions = new List<string> { "SUGGESTIONS 0" };   // what phones are told (read from the network thread)

        List<Suggestion> _live = new List<Suggestion>();   // the Live TV rows' channels (LiveTv)
        DateTime _liveFetched = DateTime.MinValue, _liveTried = DateTime.MinValue;   // UTC
        bool _liveFetching;

        /// <summary>The suggestions the settings ask for, whose app is on the TV: the live channels first.</summary>
        List<Suggestion> VisibleSuggestions()
        {
            var shown = _live.FindAll(s => _cfg.LiveTvOn && _cfg.LiveTvLanguages.Contains(s.Row.Substring(5)) && FindTile(s.App) != null);
            shown.AddRange(_suggestions.FindAll(s => (s.Kind == SuggestionKind.Movie ? _cfg.SuggestMovies : _cfg.SuggestSongs) && FindTile(s.App) != null));
            return shown;
        }

        /// <summary>A row of suggestions under the apps: its heading, and its tiles on a strip that slides sideways.</summary>
        sealed class ContentRow
        {
            public string Key;            // Suggestion.Row: "live:TEL", "films", "series", "app:nfx", "songs"
            public double W, H;           // a tile's card: a poster, a square song cover, or a channel's wide card
            public FrameworkElement Heading, Host;
            public StackPanel Strip;
            public readonly List<TileView> Tiles = new List<TileView>();
            public readonly TranslateTransform Shift = new TranslateTransform();
        }

        readonly List<ContentRow> _content = new List<ContentRow>();

        /// <summary>The rows afresh from the list, in its order (films, series, a row per app, songs).</summary>
        void BuildSuggestionRows()
        {
            foreach (ContentRow old in _content)
            {
                _stage.Children.Remove(old.Heading);
                _stage.Children.Remove(old.Host);
            }
            _content.Clear();
            List<Suggestion> shown = VisibleSuggestions();
            foreach (Suggestion s in shown)
            {
                if (!InTab(s.Row)) continue;   // the chosen tab's rows only (the phones get All's, PhoneLines)
                string key = s.Row ?? (s.Kind == SuggestionKind.Movie ? "films" : "songs");
                ContentRow row = _content.Find(r => r.Key == key);
                if (row == null)
                {
                    if (_content.Count == MostRows - 2) continue;
                    row = NewContentRow(key, CardWidth(s.Kind), CardHeight(s.Kind));
                    _content.Add(row);
                }
                TileView v = MakeSuggestionTile(s, FindTile(s.App));
                row.Tiles.Add(v);
                row.Strip.Children.Add(v.Root);
            }

            List<string> lines = Suggestions.PhoneLines(shown, _cfg, FindTile);
            if (string.Join("\n", lines) == string.Join("\n", _phoneSuggestions)) return;
            _phoneSuggestions = lines;
            if (_wifi != null) _wifi.SendToPhones(lines);
        }

        static double CardWidth(SuggestionKind kind) { return kind == SuggestionKind.Movie ? PosterW : kind == SuggestionKind.Song ? CoverSize : ChannelW; }

        static double CardHeight(SuggestionKind kind) { return kind == SuggestionKind.Movie ? PosterH : kind == SuggestionKind.Song ? CoverSize : ChannelH; }

        ContentRow NewContentRow(string key, double w, double h)
        {
            var row = new ContentRow { Key = key, W = w, H = h };
            row.Heading = RowHeading(Suggestions.RowTitle(key, _cfg.AppTiles), Suggestions.RowNote(key));
            row.Strip = new StackPanel { Orientation = Orientation.Horizontal, RenderTransform = row.Shift };
            row.Host = new Canvas { Width = DesignW - 2 * Side + 2 * RingPad, Height = h + 2 * RingPad, Children = { row.Strip } };
            Place(row.Heading, Side, 0);
            Place(row.Host, Side - RingPad, 0);
            return row;
        }

        /// <summary>
        /// A poster (or a song's cover) with the app it plays in on it, or a channel's logo with LIVE and how far the
        /// programme on now has got; its title and details below.
        /// </summary>
        TileView MakeSuggestionTile(Suggestion s, Tile app)
        {
            bool channel = s.Kind == SuggestionKind.Channel;
            double w = CardWidth(s.Kind), h = CardHeight(s.Kind);
            var v = new TileView { Tile = app, Suggestion = s };

            var face = new Grid { Clip = new RectangleGeometry(new Rect(0, 0, w, h), 14, 14) };
            if (channel) face.Background = new LinearGradientBrush(Color.FromRgb(0x2A, 0x31, 0x40), Color.FromRgb(0x16, 0x1A, 0x23), 90);
            TextBlock stand = Theme.Text(s.Title, 26, Theme.Muted, FontWeights.SemiBold);   // until the picture's there, or if it never comes
            stand.TextWrapping = TextWrapping.Wrap;
            stand.TextAlignment = TextAlignment.Center;
            stand.VerticalAlignment = VerticalAlignment.Center;
            stand.Margin = new Thickness(18);
            if (!channel || s.ImageFile == null) face.Children.Add(stand);   // logos have see-through parts: not behind one
            // A channel's logo sits whole in the middle of its card; posters and covers fill theirs.
            var picture = channel ? new Image { Stretch = Stretch.Uniform, Margin = new Thickness(46, 30, 46, 30) } : new Image { Stretch = Stretch.UniformToFill };
            face.Children.Add(picture);
            LoadPicture(picture, s.ImageFile, (int)(1.25 * w));   // a little over its size: many rows of them now
            // No app on posters (what to watch matters, not where: the hero says); a live channel says it's live.
            if (channel) face.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8), Background = Theme.Brush(Color.FromRgb(0xE5, 0x48, 0x4D)),
                Padding = new Thickness(10, 3, 10, 5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10),
                Child = Theme.Text("● LIVE", 15, Colors.White, FontWeights.Bold),
            });
            if (channel && s.End > s.Start)
            {
                // How far the programme on now has got, along the bottom.
                double done = Math.Max(0, Math.Min(1, (Watchlist.Now - s.Start) / (double)(s.End - s.Start)));
                face.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Height = 6, Width = w * done, Fill = Theme.Brush(Color.FromRgb(0xE5, 0x48, 0x4D)),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                });
            }
            v.Card = new Border
            {
                Width = w, Height = h, CornerRadius = new CornerRadius(14), Background = Theme.Brush(Theme.Chip), Child = face,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            v.Ring = new Border
            {
                CornerRadius = new CornerRadius(14 + RingPad), BorderBrush = Brushes.White, BorderThickness = new Thickness(4),
                Opacity = 0, IsHitTestVisible = false,
            };
            var host = new Grid { Width = w + 2 * RingPad, Height = h + 2 * RingPad, RenderTransformOrigin = new Point(0.5, 1) };
            host.Children.Add(v.Card);
            host.Children.Add(v.Ring);
            v.Scale = new ScaleTransform(1, 1);
            host.RenderTransform = v.Scale;

            TextBlock title = Theme.Text(s.Title, 22, Theme.Ink, FontWeights.SemiBold);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.Margin = new Thickness(RingPad, 14, RingPad, 0);
            TextBlock details = Theme.Text(s.Subtitle ?? "", 18, Theme.Muted, FontWeights.Normal);
            details.TextTrimming = TextTrimming.CharacterEllipsis;
            details.Margin = new Thickness(RingPad, 2, RingPad, 0);

            var item = new StackPanel
            {
                Width = w + 2 * RingPad, Margin = new Thickness(0, 0, CardGap - 2 * RingPad, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            };
            item.Children.Add(host);   // its name and details are in the hero, while it has the highlight
            v.Root = item;
            HookMouse(v);
            return v;
        }

        /// <summary>Shows a downloaded picture, decoded off the UI thread (at once for screenshots).</summary>
        void LoadPicture(Image image, string file, int width) { LoadPicture(image, file, width, null); }

        /// <summary>The same, then [loaded] once it's there.</summary>
        void LoadPicture(Image image, string file, int width, Action loaded)
        {
            if (string.IsNullOrEmpty(file)) return;
            string path = Path.Combine(Suggestions.Folder, file);
            if (_opt.Preview)
            {
                if (File.Exists(path)) image.Source = LoadBitmap(path, width);
                if (image.Source != null && loaded != null) loaded();
                return;
            }
            ThreadPool.QueueUserWorkItem(state =>
            {
                if (!File.Exists(path)) return;
                BitmapImage bitmap = LoadBitmap(path, width);   // frozen, so the UI thread can use it
                if (bitmap != null) Dispatcher.BeginInvoke(new Action(() =>
                {
                    image.Source = bitmap;
                    if (loaded != null) loaded();
                }));
            });
        }

        // ================================================================ playing one

        /// <summary>
        /// From the phone remote's first page: "PLAY &lt;id&gt;", and what it is (Suggestions.FromPhone), for one the phone
        /// found while the TV was off, or one that's left the TV's list since.
        /// </summary>
        void PlaySuggestionById(string request)
        {
            string[] p = request.Split(' ');
            Suggestion s = VisibleSuggestions().Find(x => x.Id == p[0]) ?? Suggestions.FromPhone(p, _cfg.AppTiles);
            if (s != null) PlaySuggestion(s);
            else Log.Info("Suggestion from the phone: can't play \"" + (request.Length > 200 ? request.Substring(0, 200) + "…" : request) + "\"");
        }

        void PlaySuggestion(Suggestion s)
        {
            Tile app = FindTile(s.App);
            if (app == null)
            {
                Toast("There's no " + s.App + " tile any more");
                return;
            }
            if (s.PlayUrl != null)
            {
                PlayIn(app, s);
                return;
            }
            // A song whose video wasn't found beforehand: look for it now.
            ShowLaunch(app);
            ThreadPool.QueueUserWorkItem(state =>
            {
                string url = null;
                try { url = Suggestions.FindVideo(s.Query); }
                catch (Exception ex) { Log.Info("Suggestion " + s.Title + ": " + ex.Message); }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (url != null)
                    {
                        s.PlayUrl = url;
                        PlayIn(app, s);
                        return;
                    }
                    string search = Launcher.SearchUrl(app, s.Query);   // YouTube couldn't be read: its search, then
                    if (search != null) OpenFromAnywhere(app.WithUrl(search));
                    else
                    {
                        HideLaunch();
                        Toast("Couldn't find " + s.Title + " on " + app.Label);
                    }
                }));
            });
        }

        void PlayIn(Tile app, Suggestion s)
        {
            Log.Info("Suggestion: " + s.Title + " on " + app.Name + ": " + s.PlayUrl);
            Tile play = app.WithUrl(s.PlayUrl);
            play.Args = ((play.Args ?? "") + " --autoplay-policy=no-user-gesture-required").Trim();
            OpenFromAnywhere(play);
            if (s.Kind == SuggestionKind.Song) FullScreenWhenPlaying(play);   // YouTube plays inside its page; F fills the screen
        }

        // ================================================================ keeping the list fresh

        /// <summary>
        /// The Live TV rows' channels and what's on now (on a background thread), every 10 minutes; after a failure,
        /// a minute later. Only with a YuppTV tile, and languages chosen (LiveTv).
        /// </summary>
        void RefreshLiveIfStale()
        {
            if (_opt.Preview || _liveFetching || !_cfg.LiveTvOn || _cfg.LiveTvLanguages.Count == 0) return;
            Tile yupp = SmartSearch.FindHost(_cfg.AppTiles, "yupptv.com");
            if (yupp == null) return;
            DateTime now = DateTime.UtcNow;
            if (now - _liveFetched < LiveTv.FreshFor || now - _liveTried < TimeSpan.FromMinutes(1)) return;
            _liveFetching = true;
            _liveTried = now;
            var languages = new List<string>(_cfg.LiveTvLanguages);
            bool clock24 = _cfg.Clock24;
            ThreadPool.QueueUserWorkItem(state =>
            {
                List<Suggestion> list = null;
                var notes = new StringBuilder();
                try { list = LiveTv.Fetch(languages, yupp, clock24, notes); }
                catch (Exception ex) { Log.Info("Live TV: couldn't read YuppTV's guide: " + ex.Message); }
                if (list != null && (notes.Length > 0 || _live.Count == 0))
                    Log.Info("Live TV: " + list.Count + " channels" + (LiveTv.HaveSession ? " (yours)" : " (YuppTV's free ones)") + ". " + notes.ToString().Trim());
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _liveFetching = false;
                    if (list == null) return;
                    _liveFetched = DateTime.UtcNow;
                    _live = list;
                    RebuildSuggestions();
                }));
            });
        }

        /// <summary>Live TV switched on or off (Settings): the rows go at once, or come once fetched.</summary>
        void LiveChoiceChanged()
        {
            SaveSetting("LiveTv", _cfg.LiveTvOn ? "on" : "off");
            RebuildSuggestions();
            _liveFetched = _liveTried = DateTime.MinValue;
            RefreshLiveIfStale();
        }

        /// <summary>Fetches the list again (on a background thread) once it's a few hours old; after a failure, 15 minutes later.</summary>
        void RefreshSuggestionsIfStale()
        {
            if (_opt.Preview || _suggestFetching || (!_cfg.SuggestMovies && !_cfg.SuggestSongs)) return;
            DateTime now = DateTime.UtcNow;
            if (now - _suggestFetched < Suggestions.FreshFor || now - _suggestTried < TimeSpan.FromMinutes(15)) return;
            _suggestFetching = true;
            _suggestTried = now;
            Config cfg = _cfg;
            List<Suggestion> previous = _suggestions;
            ThreadPool.QueueUserWorkItem(state =>
            {
                List<Suggestion> list = null;
                var notes = new StringBuilder();
                try { list = Suggestions.Fetch(cfg, previous, notes); }
                catch (Exception ex) { Log.Info("Suggestions: couldn't fetch them: " + ex.Message); }
                if (list != null)
                {
                    var rows = new List<string>();
                    foreach (Suggestion x in list) if (!rows.Contains(x.Row)) rows.Add(x.Row);
                    Log.Info("Suggestions: " + string.Join(", ", rows.ConvertAll(r => r + " " + list.FindAll(x => x.Row == r).Count)) + ". " + notes.ToString().Trim());
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _suggestFetching = false;
                    if (list == null) return;
                    _suggestFetched = DateTime.UtcNow;
                    _suggestions = list;
                    RebuildSuggestions();
                }));
            });
        }

        /// <summary>After waking up, once the network is back.</summary>
        void RefreshSuggestionsSoon(int seconds)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                RefreshSuggestionsIfStale();
            };
            timer.Start();
        }

        /// <summary>couchtv.ini was reloaded: a kind of suggestion just switched on has nothing yet, so fetch now.</summary>
        void SuggestionsSettingsChanged()
        {
            bool missing = (_cfg.SuggestMovies && !_suggestions.Exists(s => s.Kind == SuggestionKind.Movie)) ||
                           (_cfg.SuggestSongs && !_suggestions.Exists(s => s.Kind == SuggestionKind.Song));
            if (!missing) return;
            _suggestFetched = _suggestTried = DateTime.MinValue;
            RefreshSuggestionsIfStale();
        }

        /// <summary>New suggestions: the rows again, keeping the highlight where it was.</summary>
        void RebuildSuggestions()
        {
            KeepPlace(() =>
            {
                BuildSuggestionRows();
                LayoutRows();
            });
            RefreshFocus(false);
        }
    }
}
