using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace CouchTV
{
    /// <summary>
    /// The home screen, content first: along the top, tabs (All, then each app: moving along them shows that app's films
    /// and series, OK opens the app itself); under them the hero, the highlighted title's backdrop, then its trailer
    /// with sound, as streaming apps do (TrailerPlayer), with its name, year, rating, genres and description; and at the
    /// bottom one row of big posters at a time, sized for a sofa 7 or 8 feet from the TV.
    /// </summary>
    internal sealed partial class HomeWindow
    {
        const double StripTop = 34, TabH = 58;
        const double HeroTop = 118, HeroMediaLeft = 690, HeroBottom = 636, HeroTextW = 560;
        const double RowsTop = 650, RowHeadH = 50;
        const double TrailerDelay = 1.6;   // seconds on a title before its trailer starts

        static readonly Tile AllTab = new Tile { Name = "All", Label = "All", Kind = TileKind.Action, Action = "all" };

        int _tabIndex;
        string _tabName = "All";
        TextBlock _heroTitle, _heroMeta, _heroGenres, _heroText, _heroNote, _tabEmpty;
        Image _heroPicture, _heroPictureOld;
        double _heroPictureTo = 1;   // the picture's opacity: a backdrop's full, a poster standing in for one dimmed
        TrailerPlayer _trailer;
        string _heroKey;
        Suggestion _heroShown;
        bool _trailerShown, _trailerDue, _trailerRolling;   // shown (its picture, then the video); due to start; the video playing
        DispatcherTimer _trailerTimer;
        DateTime _trailerAsked;          // when the trailer showing was asked for
        string _trailerState;            // YouTube's player's last state for it, if it said one (not playing yet)
        string _trailerTrouble;          // why trailers aren't playing, for Settings (null: they are, or none tried yet)
        string _trailerWaitLogged;       // the title whose trailer's wait went in the log

        const string TrailersNote = "The highlighted film's or series' trailer, at the top of the home screen";

        /// <summary>The tab chosen: All, or an app.</summary>
        Tile SelectedTab { get { return _tabIndex >= 0 && _tabIndex < _apps.Count ? _apps[_tabIndex].Tile : AllTab; } }

        // ================================================================ building

        void BuildHero()
        {
            // The picture: fading into the screen on its left and bottom edges, as the trailer's page does too.
            double w = DesignW - HeroMediaLeft, h = HeroBottom - HeroTop;
            var pictures = new Grid { Width = w, Height = h, ClipToBounds = true, IsHitTestVisible = false };
            _heroPictureOld = new Image { Stretch = Stretch.UniformToFill, Opacity = 0 };
            _heroPicture = new Image { Stretch = Stretch.UniformToFill, Opacity = 0 };
            pictures.Children.Add(_heroPictureOld);
            pictures.Children.Add(_heroPicture);
            pictures.OpacityMask = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
                GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.Black, 0.3) },
            };
            var fade = new Grid { Width = w, Height = h, IsHitTestVisible = false, Children = { pictures } };
            fade.OpacityMask = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                GradientStops = { new GradientStop(Colors.Black, 0.74), new GradientStop(Colors.Transparent, 1) },
            };
            Place(fade, HeroMediaLeft, HeroTop);

            // The words, on the left: never under the trailer (a WebView2 is a window of its own, drawn over all else).
            var text = new StackPanel { Width = HeroTextW, IsHitTestVisible = false };
            _heroTitle = Theme.Text("", 58, Theme.Ink, FontWeights.SemiBold, Theme.Display);
            _heroTitle.TextWrapping = TextWrapping.Wrap;
            _heroTitle.MaxHeight = 150;
            _heroTitle.TextTrimming = TextTrimming.WordEllipsis;
            _heroMeta = Theme.Text("", 26, Theme.Muted, FontWeights.Normal);
            _heroMeta.Margin = new Thickness(0, 14, 0, 0);
            _heroGenres = Theme.Text("", 24, Theme.Faint, FontWeights.Normal);
            _heroGenres.Margin = new Thickness(0, 6, 0, 0);
            _heroText = Theme.Text("", 25, Theme.Alpha(Theme.Ink, 0xD8), FontWeights.Normal);
            _heroText.TextWrapping = TextWrapping.Wrap;
            _heroText.TextTrimming = TextTrimming.WordEllipsis;
            _heroText.MaxHeight = 4 * 34;
            _heroText.LineHeight = 34;
            _heroText.Margin = new Thickness(0, 20, 0, 0);
            _heroNote = Theme.Text("", 22, Theme.Faint, FontWeights.SemiBold);
            _heroNote.Margin = new Thickness(0, 18, 0, 0);
            text.Children.Add(_heroTitle);
            text.Children.Add(_heroMeta);
            text.Children.Add(_heroGenres);
            text.Children.Add(_heroText);
            text.Children.Add(_heroNote);
            Place(text, Side, HeroTop + 36);

            // A tab with nothing listed (an app JustWatch doesn't know): said where the posters would be.
            _tabEmpty = Theme.Text("", 28, Theme.Muted, FontWeights.Normal);
            _tabEmpty.Visibility = Visibility.Collapsed;
            Place(_tabEmpty, Side, RowsTop + 70);

            if (_opt.Preview) return;
            _trailer = TrailerPlayer.Create(Theme.Base);
            if (_trailer != null)
            {
                _trailer.View.Width = w;
                _trailer.View.Height = h;
                Place(_trailer.View, HeroMediaLeft, HeroTop);
                _trailer.Said += TrailerSaid;
            }
            else Dispatcher.BeginInvoke(new Action(() => TrailerTrouble(TrailerPlayer.Problem)));
            _trailerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(TrailerDelay) };
            _trailerTimer.Tick += (s, e) =>
            {
                _trailerTimer.Stop();
                _trailerDue = true;
                TrailerTick();
            };
        }

        /// <summary>A tab: All, or an app, as a pill with its name.</summary>
        TileView MakeTabTile(Tile t)
        {
            var v = new TileView { Tile = t, IsTab = true };
            v.Label = Theme.Text(t.Label, 23, Theme.Muted, FontWeights.SemiBold);
            v.Label.VerticalAlignment = VerticalAlignment.Center;
            v.Card = new Border
            {
                Height = TabH, CornerRadius = new CornerRadius(TabH / 2), Padding = new Thickness(20, 0, 20, 2), Child = v.Label,
                Background = Brushes.Transparent,
            };
            v.Ring = new Border
            {
                CornerRadius = new CornerRadius(TabH / 2 + RingPad), BorderBrush = Brushes.White, BorderThickness = new Thickness(3),
                Opacity = 0, IsHitTestVisible = false,
            };
            var host = new Grid
            {
                Margin = new Thickness(0, 0, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, RenderTransformOrigin = new Point(0.5, 0.5),
            };
            v.Card.Margin = new Thickness(RingPad);
            host.Children.Add(v.Card);
            host.Children.Add(v.Ring);
            v.Scale = new ScaleTransform(1, 1);
            host.RenderTransform = v.Scale;
            v.Root = host;
            HookMouse(v);
            return v;
        }

        /// <summary>
        /// The tabs as they are: the one with the highlight white; the chosen one (whose films show below) a soft
        /// pill while the highlight's elsewhere; the rest just their names.
        /// </summary>
        void StyleTabs(bool animate)
        {
            for (int i = 0; i < _apps.Count; i++)
            {
                TileView v = _apps[i];
                bool focused = !_mediaFocused && _row == RowApps && _col[RowApps] == i, chosen = i == _tabIndex;
                v.Card.Background = focused ? Brushes.White : chosen ? Theme.Brush(Color.FromArgb(0x33, 255, 255, 255)) : Brushes.Transparent;
                v.Label.Foreground = Theme.Brush(focused ? Theme.Base : chosen ? Theme.Ink : Theme.Muted);
                Theme.AnimateTo(v.Scale, ScaleTransform.ScaleXProperty, focused ? 1.08 : 1, animate);
                Theme.AnimateTo(v.Scale, ScaleTransform.ScaleYProperty, focused ? 1.08 : 1, animate);
                v.Focused = focused;
                v.Styled = true;
            }
        }

        /// <summary>The highlight moved along the tabs: that tab's rows below, at once.</summary>
        void TabChosen()
        {
            int col = _col[RowApps];
            if (col == _tabIndex || col < 0 || col >= _apps.Count) return;
            _tabIndex = col;
            _tabName = _apps[col].Tile.Name;
            KeepPlace(() =>
            {
                BuildSuggestionRows();
                LayoutRows();
            });
        }

        /// <summary>Whether a row of suggestions belongs to the chosen tab: All has the popular ones, an app its own.</summary>
        bool InTab(string row)
        {
            Tile tab = SelectedTab;
            if (row == null) return tab == AllTab;
            if (tab == AllTab) return !row.StartsWith("app:");
            if (row == "songs") return SmartSearch.FindHost(new List<Tile> { tab }, "youtube.com") != null;
            string key = Suggestions.AppKey(tab);
            return key != null && row.StartsWith("app:" + key + ":");
        }

        /// <summary>The first row of posters (under the tabs), or -1 when the chosen tab has none.</summary>
        int FirstContentRow()
        {
            for (int r = RowApps + 1; r < RowSystem; r++) if (Row(r).Count > 0) return r;
            return -1;
        }

        // ================================================================ the hero

        /// <summary>The title the hero is about: the highlighted poster's, or from the tabs the first of the tab's rows.</summary>
        void UpdateHero(bool animate)
        {
            Suggestion s = null;
            Tile tab = null;
            TileView f = Focused;
            if (_row == RowApps)
            {
                tab = SelectedTab;
                int first = FirstContentRow();
                if (first > 0) s = Row(first)[0].Suggestion;
            }
            else if (_row != RowSystem && f != null) s = f.Suggestion;
            string key = (s != null ? s.Id : "-") + "|" + (tab != null ? "tab:" + tab.Name : _row == RowSystem ? "system" : "row");
            if (key == _heroKey) return;
            _heroKey = key;
            bool changedTitle = s != _heroShown;
            _heroShown = s;

            if (s != null)
            {
                _heroTitle.Text = s.Title;
                var meta = new List<string>();
                if (!string.IsNullOrEmpty(s.Subtitle)) meta.Add(s.Subtitle);
                if (!string.IsNullOrEmpty(s.Score)) meta.Add("★ " + s.Score);
                _heroMeta.Text = string.Join("  ·  ", meta);
                _heroGenres.Text = s.Genres ?? "";
                _heroText.Text = s.Kind == SuggestionKind.Song ? "" : s.Description ?? "";
                Tile app = FindTile(s.App);
                _heroNote.Text = tab != null && tab != AllTab ? "OK opens " + tab.Label
                    : tab == AllTab ? "↓ to choose" : app != null ? (s.Kind == SuggestionKind.Song ? "Plays on " : "On ") + app.Label : "";
            }
            else if (tab != null)
            {
                _heroTitle.Text = tab.Label;
                _heroMeta.Text = tab == AllTab ? "" : HintFor(tab);
                _heroGenres.Text = "";
                _heroText.Text = tab == AllTab ? "Nothing to show yet: the lists come once the TV's online." : "Its films and shows aren't listed here.";
                _heroNote.Text = tab == AllTab ? "" : "OK opens " + tab.Label;
            }
            else
            {
                _heroTitle.Text = _row == RowSystem ? "Settings & power" : "";
                _heroMeta.Text = _heroGenres.Text = _heroText.Text = _heroNote.Text = "";
            }
            _tabEmpty.Text = tab != null && tab != AllTab && s == null ? "Press OK on " + tab.Label + " to open it." : "";
            _tabEmpty.Visibility = _tabEmpty.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (!changedTitle) return;
            ShowHeroPicture(s, animate);
            StopTrailer();
            _trailerDue = false;
            if (_trailerTimer != null)
            {
                _trailerTimer.Stop();
                if (s != null && s.Trailer != null) _trailerTimer.Start();
            }
        }

        /// <summary>The title's backdrop (or its poster, blurred, without one), fading in over the last.</summary>
        void ShowHeroPicture(Suggestion s, bool animate)
        {
            // (Set without animating first: a finished animation would otherwise keep holding its value.)
            _heroPictureOld.Source = _heroPicture.Source;
            _heroPictureOld.Effect = _heroPicture.Effect;
            Theme.AnimateTo(_heroPictureOld, OpacityProperty, _heroPicture.Opacity, false);
            Theme.AnimateTo(_heroPictureOld, OpacityProperty, 0, animate);
            _heroPicture.Source = null;
            Theme.AnimateTo(_heroPicture, OpacityProperty, 0, false);
            if (s == null) return;
            bool wide = s.BackdropFile != null;
            _heroPictureTo = wide ? 1 : 0.55;
            _heroPicture.Effect = wide ? null : new BlurEffect { Radius = 40 };
            LoadPicture(_heroPicture, wide ? s.BackdropFile : s.ImageFile, wide ? 1440 : 300, () =>
            {
                if (_heroShown == s && !_trailerRolling) Theme.AnimateTo(_heroPicture, OpacityProperty, _heroPictureTo, animate);
            });
        }

        /// <summary>
        /// The backdrop beneath the trailer: fading out as the video fades in (as long as the page's own fade), so the
        /// video's faded edges show the screen behind rather than the picture; back when it stops.
        /// </summary>
        void ShowBackdrop(bool shown)
        {
            double to = shown && _heroPicture.Source != null ? _heroPictureTo : 0;
            _heroPicture.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(shown ? 170 : 800)));
        }

        // ================================================================ the trailer

        /// <summary>
        /// Whether a trailer may play now: the TV's Settings allow it, the home screen's in front with nothing over it,
        /// nothing's playing or paused in the background (its card is where the trailer would be), and the highlight is on
        /// a title that has one.
        /// </summary>
        bool TrailerAllowed
        {
            get
            {
                return _trailer != null && _trailer.Ready && _cfg.Trailers != "off" && IsActive && !Modal && WindowState != WindowState.Minimized &&
                       _launch.Visibility != Visibility.Visible && !MediaBehind && _heroShown != null && _heroShown.Trailer != null &&
                       _row != RowSystem && !_mediaFocused;
            }
        }

        /// <summary>Something playing, or paused with its card on the home screen.</summary>
        bool MediaBehind
        {
            get
            {
                if (_opt.IgnoreMedia) return false;
                MediaNow now = NowPlaying.Current;
                return (now != null && now.Playing) || _cardShown;
            }
        }

        /// <summary>Every second, after a key, and when the wait's over: starts the trailer, or stops it when it mayn't play.</summary>
        void TrailerTick()
        {
            if (_trailer == null) return;
            bool allowed = TrailerAllowed;
            if (_trailerShown && !allowed) StopTrailer();
            else if (_trailerDue && allowed)
            {
                // Shown at once (the page is see-through: the backdrop stays until the video fades in over it).
                _trailerDue = false;
                _trailer.Play(_heroShown.Trailer, _cfg.Trailers == "muted");
                _trailerShown = true;
                _trailerAsked = DateTime.UtcNow;
                _trailerState = null;
                _trailer.View.Visibility = Visibility.Visible;
            }
            else if (_trailerShown && !_trailerRolling && (DateTime.UtcNow - _trailerAsked).TotalSeconds > 20)
            {
                // Asked for, and nothing: the page or YouTube's player is stuck.
                string title = _heroShown != null ? _heroShown.Title : "?";
                StopTrailer();
                TrailerTrouble(!_trailer.PageReady ? "YouTube's player didn't load (can the TV reach youtube.com?)"
                    : "YouTube's player didn't start " + title + "'s trailer (" + StateName(_trailerState) + ")");
            }
            else if (_trailerDue && _heroShown != null && _trailerWaitLogged != _heroShown.Id)
            {
                _trailerWaitLogged = _heroShown.Id;
                Log.Info("Trailers: " + _heroShown.Title + "'s waits: " + TrailerWhyNot());
            }
        }

        static string StateName(string state)
        {
            switch (state)
            {
                case null: return "it said nothing";
                case "-1": return "it stayed unstarted";
                case "2": return "it stayed paused";
                case "3": return "it kept loading";
                case "5": return "it stayed cued";
                default: return "state " + state;
            }
        }

        /// <summary>Why trailers aren't playing (null: they are): under Trailers in Settings, on the TV and the phones, and in the log.</summary>
        void TrailerTrouble(string why)
        {
            if (why == _trailerTrouble) return;
            _trailerTrouble = why;
            if (why != null) Log.Info("Trailers: not playing: " + why);
            TvSetting setting = TvSettings().Find(t => t.Key == "trailers");
            if (setting != null) setting.Note = TrailersNoteNow();
            SettingsChanged();
        }

        string TrailersNoteNow() { return TrailersNote + (_trailerTrouble != null ? ". Not playing: " + _trailerTrouble : ""); }

        void TrailerSaid(string said)
        {
            if (said == "playing" && TrailerAllowed)
            {
                _trailerRolling = true;
                ShowBackdrop(false);
                TrailerTrouble(null);
            }
            else if (said == "playing") StopTrailer();   // too late: things changed while it loaded
            else if (said.StartsWith("state ")) _trailerState = said.Substring(6);
            else if (said.StartsWith("failed ")) TrailerTrouble(said.Substring(7));
            else if (said == "ended" || said.StartsWith("error") || said == "stopped")
            {
                if (_trailerRolling) ShowBackdrop(true);   // the same backdrop, back beneath
                _trailerShown = _trailerRolling = false;
                _trailer.View.Visibility = Visibility.Hidden;
                if (said.StartsWith("error"))
                {
                    string code = said.Substring(5).Trim();
                    TrailerTrouble("YouTube wouldn't play " + (_heroShown != null ? _heroShown.Title : "?") + "'s trailer (" +
                        (code == "101" || code == "150" ? "its owner allows it only on YouTube" : code == "100" ? "it's gone" : "error " + code) + ")");
                }
            }
        }

        // For the self-test (Diagnostics.TrailerTest).
        internal bool TrailerPlaying { get { return _trailerRolling; } }
        internal string HeroTitle { get { return _heroShown != null ? _heroShown.Title : null; } }
        internal void ProbeTrailer(Action<string> said) { if (_trailer != null) _trailer.Probe(said); }
        internal string TrailerTroubleNow { get { return _trailerTrouble; } }

        /// <summary>For the self-test: the highlight on the first poster that has a trailer.</summary>
        internal bool FocusTrailer()
        {
            for (int r = RowApps + 1; r < RowSystem; r++)
                for (int c = 0; c < Row(r).Count; c++)
                {
                    Suggestion s = Row(r)[c].Suggestion;
                    if (s == null || s.Trailer == null) continue;
                    _row = r;
                    _col[r] = c;
                    RefreshFocus(true);
                    return true;
                }
            return false;
        }

        /// <summary>What's keeping the trailer from playing, for the self-test's report.</summary>
        internal string TrailerWhyNot()
        {
            var why = new List<string>();
            if (_trailer == null) why.Add("no player");
            else if (!_trailer.Ready) why.Add("player not ready");
            if (_cfg.Trailers == "off") why.Add("off in settings");
            if (!IsActive) why.Add("window not in front");
            if (Modal) why.Add("a screen open");
            if (_launch.Visibility == Visibility.Visible) why.Add("opening an app");
            MediaNow now = NowPlaying.Current;
            if (MediaBehind) why.Add(now != null && now.Playing ? "something playing (" + now.Source + ")" : "what's paused has its card there");
            if (_heroShown == null) why.Add("no title");
            else if (_heroShown.Trailer == null) why.Add(_heroShown.Title + " has no trailer");
            if (_row == RowSystem) why.Add("on settings & power");
            if (_mediaFocused) why.Add("on what's playing");
            return why.Count == 0 ? "nothing (due " + _trailerDue + ")" : string.Join(", ", why);
        }

        void StopTrailer()
        {
            if (_trailer == null) return;
            if (_trailerShown || _trailer.View.Visibility == Visibility.Visible) _trailer.View.Visibility = Visibility.Hidden;
            if (_trailerRolling) ShowBackdrop(true);
            _trailerShown = _trailerRolling = false;
            _trailer.Stop();
        }
    }
}
