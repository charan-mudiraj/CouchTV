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
    /// The suggestion rows under the apps: popular films, each on the app it streams on, and India's most-played songs,
    /// on YouTube (see Suggestions). OK plays it in that app. The phone remote gets the same list for its first page,
    /// and plays from it with PLAY.
    /// </summary>
    internal sealed partial class HomeWindow
    {
        const double PosterW = 200, PosterH = 300, CoverSize = 236, CardGap = 34, SuggestionTextH = 76;

        List<Suggestion> _suggestions = new List<Suggestion>();
        DateTime _suggestFetched = DateTime.MinValue, _suggestTried = DateTime.MinValue;   // UTC
        bool _suggestFetching;
        List<string> _phoneSuggestions = new List<string> { "SUGGESTIONS 0" };   // what phones are told (read from the network thread)

        /// <summary>The suggestions the settings ask for, whose app is on the TV.</summary>
        List<Suggestion> VisibleSuggestions()
        {
            return _suggestions.FindAll(s => (s.Kind == SuggestionKind.Movie ? _cfg.SuggestMovies : _cfg.SuggestSongs) && FindTile(s.App) != null);
        }

        void BuildSuggestionRows()
        {
            _movies.Clear();
            _songs.Clear();
            _movieRow.Children.Clear();
            _songRow.Children.Clear();
            List<Suggestion> shown = VisibleSuggestions();
            foreach (Suggestion s in shown)
            {
                TileView v = MakeSuggestionTile(s, FindTile(s.App));
                if (s.Kind == SuggestionKind.Movie)
                {
                    _movies.Add(v);
                    _movieRow.Children.Add(v.Root);
                }
                else
                {
                    _songs.Add(v);
                    _songRow.Children.Add(v.Root);
                }
            }

            List<string> lines = Suggestions.PhoneLines(shown, FindTile);
            if (string.Join("\n", lines) == string.Join("\n", _phoneSuggestions)) return;
            _phoneSuggestions = lines;
            if (_wifi != null) _wifi.SendToPhones(lines);
        }

        /// <summary>A poster (or a song's cover) with the app it plays in on it, and its title and details below.</summary>
        TileView MakeSuggestionTile(Suggestion s, Tile app)
        {
            bool movie = s.Kind == SuggestionKind.Movie;
            double w = movie ? PosterW : CoverSize, h = movie ? PosterH : CoverSize;
            var v = new TileView { Tile = app, Suggestion = s };

            var face = new Grid { Clip = new RectangleGeometry(new Rect(0, 0, w, h), 14, 14) };
            TextBlock stand = Theme.Text(s.Title, 26, Theme.Muted, FontWeights.SemiBold);   // until the picture's there, or if it never comes
            stand.TextWrapping = TextWrapping.Wrap;
            stand.TextAlignment = TextAlignment.Center;
            stand.VerticalAlignment = VerticalAlignment.Center;
            stand.Margin = new Thickness(18);
            face.Children.Add(stand);
            var picture = new Image { Stretch = Stretch.UniformToFill };
            face.Children.Add(picture);
            LoadPicture(picture, s.ImageFile, (int)(2 * w));
            face.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8), Background = Theme.Brush(app.Background), Padding = new Thickness(10, 3, 10, 5),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10),
                Child = Theme.Text("▶ " + app.Label, 16, app.Foreground, FontWeights.Bold),
            });
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
            var host = new Grid { Width = w + 2 * RingPad, Height = h + 2 * RingPad, RenderTransformOrigin = new Point(0.5, 0.5) };
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
            item.Children.Add(host);
            item.Children.Add(title);
            item.Children.Add(details);
            v.Root = item;
            HookMouse(v);
            return v;
        }

        /// <summary>Shows a downloaded picture, decoded off the UI thread (at once for screenshots).</summary>
        void LoadPicture(Image image, string file, int width)
        {
            if (string.IsNullOrEmpty(file)) return;
            string path = Path.Combine(Suggestions.Folder, file);
            if (_opt.Preview)
            {
                if (File.Exists(path)) image.Source = LoadBitmap(path, width);
                return;
            }
            ThreadPool.QueueUserWorkItem(state =>
            {
                if (!File.Exists(path)) return;
                BitmapImage bitmap = LoadBitmap(path, width);   // frozen, so the UI thread can use it
                if (bitmap != null) Dispatcher.BeginInvoke(new Action(() => image.Source = bitmap));
            });
        }

        // ================================================================ playing one

        /// <summary>From the phone remote's first page: "PLAY &lt;id&gt;".</summary>
        void PlaySuggestionById(string id)
        {
            Suggestion s = VisibleSuggestions().Find(x => x.Id == id);
            if (s != null) PlaySuggestion(s);
            else Log.Info("Suggestion " + id + " isn't on the list any more");
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
                    int movies = list.FindAll(x => x.Kind == SuggestionKind.Movie).Count;
                    Log.Info("Suggestions: " + movies + " films, " + (list.Count - movies) + " songs. " + notes.ToString().Trim());
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
            BuildSuggestionRows();
            LayoutRows();
            for (int r = 0; r < RowCount; r++) _col[r] = Math.Max(0, Math.Min(_col[r], Row(r).Count - 1));
            if (Row(_row).Count == 0) _row = FirstRow();
            RefreshFocus(false);
        }
    }
}
