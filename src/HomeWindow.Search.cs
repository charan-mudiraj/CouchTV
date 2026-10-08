using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
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
    /// Search: say or type what you want. The words come from the phone remote's voice search (as text over infrared,
    /// see IrText) or from a keyboard. AI search (SmartSearch) works out what they mean: with one clear answer it
    /// opens it by itself after a short countdown; with several it puts them first and you choose. Every other app
    /// stays in the row, so you can always pick one yourself.
    /// </summary>
    internal sealed partial class HomeWindow
    {
        sealed class SearchTarget
        {
            public SearchOption Option;
            public FrameworkElement Root;
            public Border Ring;
            public ScaleTransform Scale;
        }

        const double TargetW = 260, TargetH = 146, TargetGap = 34;
        const int CountdownSeconds = 2;
        static readonly Color Listening = Theme.Hex("#3D7BFF");

        readonly IrText _irText = new IrText();
        FrameworkElement _search;
        TextBlock _searchQuery, _searchCaret, _searchStatus, _searchNote, _searchGlyph, _searchCredit, _targetLabel;
        Border _searchMic;
        FrameworkElement _searchTargets;
        readonly ScaleTransform _searchPulse = new ScaleTransform(1, 1);
        readonly TranslateTransform _targetShift = new TranslateTransform();
        StackPanel _targetRow;
        readonly List<SearchTarget> _targets = new List<SearchTarget>();
        int _targetIndex;
        double _targetScroll;
        string _query = "", _searchError;
        bool _listening, _receiving, _ignoreText;
        DateTime _listenUntil;
        IntPtr _searchReturnTo;
        float _duckedFrom = -1;
        DispatcherTimer _searchTimer;

        // AI search
        ApiKeys _keys;
        SearchPlan _plan;
        string _planFor;
        int _planSeq, _countdownLeft;
        bool _thinking, _targetChosen;
        DispatcherTimer _countdown;
        readonly Dictionary<string, SearchPlan> _planCache = new Dictionary<string, SearchPlan>();

        bool SearchOpen { get { return _search != null && _search.Visibility == Visibility.Visible; } }
        bool CountingDown { get { return _countdownLeft > 0; } }

        // ================================================================ layout

        FrameworkElement BuildSearch()
        {
            _searchGlyph = new TextBlock
            {
                FontFamily = Theme.Icons, FontSize = 42, Foreground = Theme.Brush(Theme.Ink),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            _searchMic = new Border
            {
                Width = 104, Height = 104, CornerRadius = new CornerRadius(52), Child = _searchGlyph,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _searchPulse, VerticalAlignment = VerticalAlignment.Center,
            };

            _searchQuery = Theme.Text("", 66, Theme.Ink, FontWeights.SemiBold, Theme.Display);
            _searchQuery.TextTrimming = TextTrimming.CharacterEllipsis;
            _searchQuery.MaxWidth = 1460;
            _searchCaret = Theme.Text("|", 66, Theme.Faint, FontWeights.Light, Theme.Display);
            _searchCaret.Margin = new Thickness(4, 0, 0, 0);
            var queryLine = new StackPanel { Orientation = Orientation.Horizontal };
            queryLine.Children.Add(_searchQuery);
            queryLine.Children.Add(_searchCaret);
            _searchStatus = Theme.Text("", 28, Theme.Muted, FontWeights.Normal);
            _searchStatus.Margin = new Thickness(0, 6, 0, 0);
            _searchNote = Theme.Text("", 22, Theme.Faint, FontWeights.Normal);
            _searchNote.Margin = new Thickness(0, 6, 0, 0);
            var words = new StackPanel { Margin = new Thickness(40, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(queryLine);
            words.Children.Add(_searchStatus);
            words.Children.Add(_searchNote);
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(_searchMic);
            top.Children.Add(words);

            _targetLabel = SectionLabel("Search in");
            _targetLabel.Margin = new Thickness(0, 100, 0, 24);
            _targetRow = new StackPanel { Orientation = Orientation.Horizontal, RenderTransform = _targetShift };
            var targetHost = new Canvas { Width = DesignW - 2 * Side + 2 * RingPad, Height = TargetH + 2 * RingPad + 50, Margin = new Thickness(-RingPad, 0, 0, 0) };
            targetHost.Children.Add(_targetRow);
            var targets = new StackPanel();
            targets.Children.Add(_targetLabel);
            targets.Children.Add(targetHost);
            _searchTargets = targets;

            var column = new StackPanel { Margin = new Thickness(Side, 170, Side, 0) };
            column.Children.Add(top);
            column.Children.Add(targets);

            var hints = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(Side, 0, 0, 60) };
            hints.Children.Add(KeyHint("OK", "Go"));
            hints.Children.Add(KeyHint("← →", "Choose"));
            hints.Children.Add(KeyHint("Back", "Close"));
            hints.Children.Add(KeyHint("Delete", "Remove a letter"));

            _searchCredit = Theme.Text("Where-to-watch data from JustWatch, via TMDB", 20, Theme.Faint, FontWeights.Normal);
            _searchCredit.HorizontalAlignment = HorizontalAlignment.Right;
            _searchCredit.VerticalAlignment = VerticalAlignment.Bottom;
            _searchCredit.Margin = new Thickness(0, 0, Side, 66);

            // The home screen is hidden while search is open (see OpenSearch), so the background glow shows through.
            var overlay = new Grid { Background = Brushes.Transparent, Visibility = Visibility.Collapsed };
            overlay.Children.Add(column);
            overlay.Children.Add(hints);
            overlay.Children.Add(_searchCredit);
            return overlay;
        }

        SearchTarget MakeTarget(SearchOption option, bool suggested)
        {
            var target = new SearchTarget { Option = option };
            Tile look = option.Kind == SearchKind.Action
                ? new Tile { Label = option.Label, Glyph = option.Glyph, Background = Theme.Hex("#262D3A"), Foreground = Theme.Ink, Weight = FontWeights.SemiBold }
                : option.Tile;
            var face = new Grid();
            if (option.Kind == SearchKind.Action)
            {
                // A TV command: a big icon over its words, e.g. the timer over "Sleep in 30 minutes".
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 16, 0) };
                stack.Children.Add(new TextBlock
                {
                    Text = option.Glyph.ToString(), FontFamily = Theme.Icons, FontSize = 46, Foreground = Theme.Brush(Theme.Ink),
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                TextBlock words = Theme.Text(option.Label, 26, Theme.Ink, FontWeights.SemiBold);
                words.TextAlignment = TextAlignment.Center;
                words.TextWrapping = TextWrapping.Wrap;
                words.Margin = new Thickness(0, 10, 0, 0);
                stack.Children.Add(words);
                face.Children.Add(stack);
            }
            else if (look.ImagePath != null && File.Exists(look.ImagePath))
                face.Children.Add(new Image { Source = LoadBitmap(look.ImagePath, 520), Stretch = Stretch.Uniform, Margin = new Thickness(22) });
            else
                face.Children.Add(TileLabel(look));
            var card = new Border
            {
                Width = TargetW, Height = TargetH, CornerRadius = new CornerRadius(16), Background = TileBrush(look), Child = face,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.5, Color = Colors.Black },
            };
            target.Ring = new Border
            {
                CornerRadius = new CornerRadius(16 + RingPad), BorderBrush = Brushes.White, BorderThickness = new Thickness(4),
                Opacity = 0, IsHitTestVisible = false,
            };
            var host = new Grid
            {
                Width = TargetW + 2 * RingPad, Height = TargetH + 2 * RingPad,
                Background = Brushes.Transparent, RenderTransformOrigin = new Point(0.5, 0.5),
            };
            host.Children.Add(card);
            host.Children.Add(target.Ring);
            target.Scale = new ScaleTransform(1, 1);
            host.RenderTransform = target.Scale;

            TextBlock caption = Theme.Text(suggested ? option.Caption ?? "" : "", 22, Theme.Good, FontWeights.SemiBold);
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            caption.Margin = new Thickness(0, 10, 0, 0);
            var item = new StackPanel { Margin = new Thickness(0, 0, TargetGap - 2 * RingPad, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            item.Children.Add(host);
            item.Children.Add(caption);
            item.MouseEnter += (s, e) =>
            {
                if (!_pointer) return;
                _targetIndex = _targets.IndexOf(target);
                _targetChosen = true;
                CancelCountdown();
                RefreshTargets(true);
                UpdateSearch();
            };
            item.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                _targetIndex = _targets.IndexOf(target);
                OpenTarget();
            };
            target.Root = item;
            return target;
        }

        /// <summary>
        /// The plan's options first (with their captions), then every other app that can search. A plan that only
        /// opens an app or runs a TV command ("sleep in 30 minutes") shows just that.
        /// </summary>
        void BuildTargets(SearchPlan plan)
        {
            _targets.Clear();
            _targetRow.Children.Clear();
            var used = new List<Tile>();
            if (plan != null)
            {
                foreach (SearchOption option in plan.Options)
                {
                    AddTarget(MakeTarget(option, true));
                    if (option.Tile != null) used.Add(option.Tile);
                }
            }
            bool isSearch = plan == null || plan.Options.Count == 0 || plan.Options.Exists(o => o.Kind == SearchKind.AppSearch);
            foreach (Tile t in _cfg.AppTiles)
            {
                if (!isSearch || used.Contains(t) || Launcher.SearchTemplate(t) == null) continue;
                AddTarget(MakeTarget(new SearchOption { Kind = SearchKind.AppSearch, Tile = t }, false));
            }
            _targetIndex = plan != null ? 0 : Math.Max(0, Math.Min(_targetIndex, _targets.Count - 1));
            _targetScroll = 0;
            RefreshTargets(false);
        }

        void AddTarget(SearchTarget target)
        {
            _targets.Add(target);
            _targetRow.Children.Add(target.Root);
        }

        void RefreshTargets(bool animate)
        {
            animate &= !_opt.Preview;
            int suggested = _plan == null ? 0 : _plan.Options.Count;
            for (int i = 0; i < _targets.Count; i++)
            {
                SearchTarget target = _targets[i];
                bool on = i == _targetIndex;
                Theme.AnimateTo(target.Scale, ScaleTransform.ScaleXProperty, on ? 1.08 : 1, animate);
                Theme.AnimateTo(target.Scale, ScaleTransform.ScaleYProperty, on ? 1.08 : 1, animate);
                Theme.AnimateTo(target.Ring, OpacityProperty, on ? 1 : 0, animate);
                // Suggestions stand out; the rest of the apps stay there, quieter, for picking yourself.
                Theme.AnimateTo(target.Root, OpacityProperty, on ? 1 : suggested > 0 && i >= suggested ? 0.45 : 0.75, animate);
                Panel.SetZIndex(target.Root, on ? 1 : 0);
            }
            // Slide the row so the chosen one is always on screen.
            double pitch = TargetW + TargetGap, item = TargetW + 2 * RingPad, view = DesignW - 2 * Side + 2 * RingPad;
            double x = _targetIndex * pitch, total = Math.Max(0, (_targets.Count - 1) * pitch + item);
            if (x < _targetScroll) _targetScroll = x;
            else if (x + item > _targetScroll + view) _targetScroll = x + item - view;
            _targetScroll = Math.Max(0, Math.Min(_targetScroll, Math.Max(0, total - view)));
            Theme.AnimateTo(_targetShift, TranslateTransform.XProperty, -_targetScroll, animate);
        }

        void UpdateSearch()
        {
            bool empty = _query.Length == 0;
            _searchQuery.Text = empty ? (_listening ? "Listening…" : "Search") : _query;
            _searchQuery.Foreground = Theme.Brush(empty ? Theme.Faint : Theme.Ink);
            _searchCaret.Visibility = _listening || _receiving || _thinking || _plan != null ? Visibility.Collapsed : Visibility.Visible;

            int suggestions = _plan == null ? 0 : _plan.Options.Count;
            string status, note = "";
            Color color = Theme.Muted;
            if (_receiving) status = "Getting the words from your phone…";
            else if (_listening) status = _duckedFrom >= 0 ? "Speak to your phone. The TV is turned down while it listens." : "Speak to your phone.";
            else if (_searchError != null)
            {
                status = _searchError;
                color = Theme.Warn;
            }
            else if (_thinking) status = "Working out what you mean…";
            else if (CountingDown)
            {
                status = Lead() + (_targets[0].Option.Kind == SearchKind.Action
                    ? "Doing that in " + _countdownLeft + "…  Press Back to cancel."
                    : "Opening " + TargetName(_targets[0]) + " in " + _countdownLeft + "…  Press Back to choose something else.");
                color = Theme.Good;
            }
            else if (suggestions >= 2) status = Lead() + (_plan.Options[0].Kind == SearchKind.AppSearch && _plan.Options[0].Caption == "Streams here" ? "Choose where to watch, then press OK." : "Choose one, then press OK.");
            else if (suggestions == 1) status = Lead() + "Press OK to open " + TargetName(_targets[0]) + ".";
            else if (_targets.Count == 0) status = "None of your apps can search. Add Search = … to an app in couchtv.ini.";
            else if (empty) status = "Tap the mic on the phone remote and say what you want, or type it.";
            else status = "Choose where to search, then press OK.";
            if (_plan != null && suggestions == 0 && !string.IsNullOrEmpty(_plan.Note)) note = _plan.Note;
            _searchStatus.Text = status;
            _searchStatus.Foreground = Theme.Brush(color);
            _searchNote.Text = note;
            _searchNote.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _searchCredit.Visibility = _plan != null && _plan.UsedTmdb ? Visibility.Visible : Visibility.Collapsed;
            _targetLabel.Text = suggestions == 0 ? "Search in" : _targets.Count > suggestions ? "Suggested, then your other apps" : "Suggested";

            bool live = _listening || _receiving;
            _searchMic.Background = Theme.Brush(live ? Listening : Theme.Chip);
            _searchGlyph.Text = live ? "" : "";   // microphone : magnifier
            _searchTargets.Opacity = empty || _thinking ? 0.35 : 1;
        }

        /// <summary>"Panchayat · TV show · 2020.  " before the plan's status, when the AI found something.</summary>
        string Lead()
        {
            return _plan == null || string.IsNullOrEmpty(_plan.Summary) || _plan.Summary == _query ? "" : _plan.Summary + ".  ";
        }

        static string TargetName(SearchTarget target)
        {
            SearchOption o = target.Option;
            return o.Kind == SearchKind.Action ? o.Label.ToLowerInvariant() : o.Tile.Label;
        }

        // ================================================================ opening and closing

        /// <summary>Opens search over whatever is on screen. listening: the phone remote just started voice search.</summary>
        void OpenSearch(bool listening)
        {
            if (!SearchOpen)
            {
                if (!_opt.Preview)
                {
                    IntPtr front = Native.GetForegroundWindow();
                    _searchReturnTo = front != _hwnd ? front : IntPtr.Zero;
                    BringHome();
                }
                _query = "";
                _searchError = null;
                ClearPlan();
                _stage.Visibility = Visibility.Hidden;
                _search.Visibility = Visibility.Visible;
            }
            if (listening) StartListening();
            UpdateSearch();
            StartSearchTimer();
        }

        /// <summary>Hides search. With returnToApp, goes back to the app that was playing when search opened.</summary>
        void CloseSearch(bool returnToApp)
        {
            if (!SearchOpen) return;
            _search.Visibility = Visibility.Collapsed;
            _stage.Visibility = Visibility.Visible;
            StopListening();
            CancelCountdown();
            _planSeq++;                                              // ignore a plan still on its way
            _thinking = false;
            if (_receiving || _irText.Active) _ignoreText = true;   // closed while words were still arriving
            _receiving = false;
            _searchError = null;
            IntPtr back = _searchReturnTo;
            _searchReturnTo = IntPtr.Zero;
            if (returnToApp && back != IntPtr.Zero && Native.IsWindow(back)) Shell.ForceForeground(back);
        }

        void OpenTarget()
        {
            CancelCountdown();
            if (_targets.Count == 0) return;
            SearchOption o = _targets[_targetIndex].Option;
            if (o.Kind == SearchKind.Action)
            {
                CloseSearch(o.Action != "home" && o.Action != "sleep");
                RunSearchAction(o);
                return;
            }
            if (o.Kind == SearchKind.OpenApp)
            {
                CloseSearch(false);
                OpenFromAnywhere(o.Tile);
                return;
            }
            string words = o.Query ?? (_plan != null && _planFor == _query ? _plan.SearchText : null) ?? _query;
            if (words.Trim().Length == 0)
            {
                _searchError = "Say or type something to search for first.";
                UpdateSearch();
                return;
            }
            string url = Launcher.SearchUrl(o.Tile, words);
            if (url == null) return;
            Log.Info("Search in " + o.Tile.Name + ": " + words);
            CloseSearch(false);
            OpenFromAnywhere(o.Tile.WithUrl(url));
        }

        void RunSearchAction(SearchOption o)
        {
            switch (o.Action)
            {
                case "sleep": Shell.Sleep(); break;
                case "home": GoHome(); break;
                case "mute": ChangeVolume(0, true); break;
                case "volume":
                    Audio.SetLevel(o.Value / 100f);
                    ChangeVolume(0, false);   // shows the volume bar
                    break;
                case "sleeptimer":
                    _sleepAt = DateTime.Now.AddMinutes(o.Value);
                    _sleepWarned = false;
                    UpdateSleepCaption();
                    Osd.ShowMessage('', "The TV will go to sleep in " + o.Value + " minutes", 4);
                    break;
            }
        }

        // ================================================================ AI search

        /// <summary>Asks SmartSearch about the words (on a background thread) and shows its answer.</summary>
        void StartPlanning()
        {
            string words = _query.Trim();
            if (words.Length == 0) return;
            if (ApiKeys.LooksLikeGemini(words) || ApiKeys.LooksLikeTmdb(words))
            {
                OfferToSaveKey(words);
                return;
            }
            CancelCountdown();
            int seq = ++_planSeq;
            _thinking = true;
            _searchError = null;
            UpdateSearch();

            SearchPlan cached;
            if (_planCache.TryGetValue(words.ToLowerInvariant(), out cached))
            {
                ApplyPlan(cached, seq, words);
                return;
            }
            if (_keys == null) _keys = ApiKeys.Load();
            Config cfg = _cfg;
            ApiKeys keys = _keys;
            ThreadPool.QueueUserWorkItem(state =>
            {
                SearchPlan plan;
                try { plan = SmartSearch.Plan(words, cfg, keys); }
                catch (Exception ex)
                {
                    Log.Error("Search", ex);
                    plan = new SearchPlan { Note = "Search went wrong: " + ex.Message };
                }
                Dispatcher.BeginInvoke(new Action(() => ApplyPlan(plan, seq, words)));
            });
        }

        void ApplyPlan(SearchPlan plan, int seq, string words)
        {
            if (seq != _planSeq || !SearchOpen || _query.Trim() != words) return;   // the words changed meanwhile
            if (plan.Source == "Gemini")
            {
                if (_planCache.Count > 50) _planCache.Clear();
                _planCache[words.ToLowerInvariant()] = plan;
            }
            _thinking = false;
            _plan = plan;
            _planFor = _query;
            _targetChosen = false;
            BuildTargets(plan);
            if (plan.Options.Count == 1) StartCountdown();   // one clear answer: open it by itself
            UpdateSearch();
        }

        void ClearPlan()
        {
            CancelCountdown();
            _planSeq++;
            _thinking = false;
            _targetChosen = false;
            bool had = _plan != null;
            _plan = null;
            _planFor = null;
            if (had || _targets.Count == 0) BuildTargets(null);
        }

        void StartCountdown()
        {
            _countdownLeft = CountdownSeconds;
            if (_opt.Preview) return;
            if (_countdown == null)
            {
                _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _countdown.Tick += (s, e) =>
                {
                    if (!SearchOpen || !CountingDown)
                    {
                        CancelCountdown();
                        return;
                    }
                    _countdownLeft--;
                    if (_countdownLeft > 0)
                    {
                        UpdateSearch();
                        return;
                    }
                    _countdown.Stop();
                    _targetIndex = 0;
                    OpenTarget();
                };
            }
            _countdown.Stop();
            _countdown.Start();
        }

        void CancelCountdown()
        {
            _countdownLeft = 0;
            if (_countdown != null) _countdown.Stop();
        }

        /// <summary>A Gemini or TMDB key pasted into the phone's search box: offer to save it for AI search.</summary>
        void OfferToSaveKey(string key)
        {
            bool gemini = ApiKeys.LooksLikeGemini(key);
            string name = gemini ? ApiKeys.GeminiName : ApiKeys.TmdbName;
            CloseSearch(false);
            Confirm(gemini ? "Save this Gemini key?" : "Save this TMDB key?",
                (gemini ? "AI search will use it to understand what you ask for." : "Search will use it to check which app has a show or film.") +
                " It's kept only on this PC, in " + ApiKeys.FilePath + ".",
                "Save", () =>
                {
                    try
                    {
                        ApiKeys.Save(name, key.Trim());
                        _keys = null;
                        _planCache.Clear();
                        Toast(gemini ? "AI search is on. Try it with the mic on the phone." : "Saved. Search now checks where shows and films stream.");
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Saving a key", ex);
                        Toast("Couldn't save the key: " + ex.Message);
                    }
                });
        }

        // ================================================================ voice search from the phone

        void StartListening()
        {
            _listening = true;
            _listenUntil = DateTime.Now.AddSeconds(20);
            _query = "";
            _searchError = null;
            ClearPlan();
            Duck();
            if (_opt.Preview) return;
            var pulse = new DoubleAnimation(1, 1.12, TimeSpan.FromSeconds(0.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            _searchPulse.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            _searchPulse.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }

        /// <summary>The phone stopped listening without words: close search if nothing was typed meanwhile.</summary>
        void CancelVoiceSearch()
        {
            if (!SearchOpen) return;
            if (_query.Length == 0) CloseSearch(true);
            else StopListening();
        }

        void StopListening()
        {
            if (!_listening) return;
            _listening = false;
            _searchPulse.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _searchPulse.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            Unduck();
        }

        /// <summary>Turns the TV down while the phone listens, so it hears you rather than the show.</summary>
        void Duck()
        {
            if (_opt.Preview || _duckedFrom >= 0) return;
            float level;
            bool muted;
            if (!Audio.Change(0, false, out level, out muted) || muted || level <= 0.1f) return;
            _duckedFrom = level;
            Audio.SetLevel(Math.Max(0.06f, level * 0.25f));
        }

        void Unduck()
        {
            if (_duckedFrom < 0) return;
            Audio.SetLevel(_duckedFrom);
            _duckedFrom = -1;
        }

        void OnIrText(IrTextResult result)
        {
            switch (result)
            {
                case IrTextResult.Started:
                    _ignoreText = false;
                    _receiving = true;
                    if (!SearchOpen) OpenSearch(false);
                    UpdateSearch();
                    StartSearchTimer();
                    break;
                case IrTextResult.Done:
                    _receiving = false;
                    if (_ignoreText)
                    {
                        _ignoreText = false;
                        return;
                    }
                    if (!SearchOpen) OpenSearch(false);
                    StopListening();
                    _query = _irText.Text;
                    _searchError = null;
                    StartPlanning();
                    if (SearchOpen) UpdateSearch();
                    break;
                case IrTextResult.Failed:
                    TextFailed();
                    break;
            }
        }

        void TextFailed()
        {
            _receiving = false;
            if (_ignoreText)
            {
                _ignoreText = false;
                return;
            }
            StopListening();
            _searchError = "Some of the words didn't arrive. Point the phone at the TV and try again.";
            if (SearchOpen) UpdateSearch();
        }

        /// <summary>Watches for words that stop arriving part way, and for a phone that never sends any.</summary>
        void StartSearchTimer()
        {
            if (_opt.Preview) return;
            if (_searchTimer == null)
            {
                _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _searchTimer.Tick += (s, e) =>
                {
                    DateTime now = DateTime.Now;
                    if (_irText.TimedOut(now)) TextFailed();
                    if (_listening && !_receiving && now > _listenUntil)
                    {
                        StopListening();
                        if (SearchOpen) UpdateSearch();
                    }
                    if (!SearchOpen && !_irText.Active && !_listening) _searchTimer.Stop();
                };
            }
            _searchTimer.Start();
        }

        // ================================================================ keys

        /// <summary>Keys while search is open. Letters arrive as text (OnSearchText); false lets them through.</summary>
        bool SearchKey(Key key)
        {
            if (CountingDown && (key == Key.Escape || key == Key.BrowserBack || key == Key.Back))
            {
                CancelCountdown();   // stay, and choose something else
                UpdateSearch();
                return true;
            }
            switch (key)
            {
                case Key.Left: MoveTarget(-1); return true;
                case Key.Right: MoveTarget(1); return true;
                case Key.Up: case Key.Down: return true;
                case Key.Enter:
                    // Typed words: the first OK works out what they mean, unless you already picked an app.
                    if (!_targetChosen && !_thinking && _query.Trim().Length > 0 && (_plan == null || _planFor != _query)) StartPlanning();
                    else if (!_thinking) OpenTarget();
                    return true;
                case Key.Escape: case Key.BrowserBack: CloseSearch(true); return true;
                case Key.Back:
                    if (_query.Length > 0)
                    {
                        int cut = _query.Length >= 2 && char.IsLowSurrogate(_query[_query.Length - 1]) ? 2 : 1;
                        EditQuery(_query.Substring(0, _query.Length - cut));
                    }
                    return true;
                case Key.Delete:
                    EditQuery("");
                    return true;
                default:
                    return false;
            }
        }

        void MoveTarget(int step)
        {
            CancelCountdown();
            int next = _targetIndex + step;
            if (next >= 0 && next < _targets.Count)
            {
                _targetIndex = next;
                _targetChosen = true;
                RefreshTargets(true);
            }
            UpdateSearch();
        }

        void EditQuery(string text)
        {
            StopListening();
            _query = text;
            _searchError = null;
            ClearPlan();
            UpdateSearch();
        }

        void OnSearchText(object sender, TextCompositionEventArgs e)
        {
            if (!SearchOpen || string.IsNullOrEmpty(e.Text)) return;
            e.Handled = true;
            var text = new StringBuilder(_query);
            foreach (char c in e.Text)
                if (!char.IsControl(c)) text.Append(c);
            if (text.Length == _query.Length || text.Length > 80) return;
            EditQuery(text.ToString());
        }

        // ================================================================ screenshots

        void PreviewPlan(string words, SearchPlan plan)
        {
            OpenSearch(false);
            _query = words;
            ApplyPlan(plan, _planSeq, words);
        }
    }
}
