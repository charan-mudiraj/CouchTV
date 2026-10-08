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
using System.Windows.Threading;

namespace CouchTV
{
    /// <summary>
    /// Search: say or type what to watch, then pick the app to search in. The words come from the phone remote's voice
    /// search (as text over infrared, see IrText) or from a keyboard. Each app opens its own search results.
    /// </summary>
    internal sealed partial class HomeWindow
    {
        sealed class SearchTarget
        {
            public Tile Tile;
            public FrameworkElement Root;
            public Border Ring;
            public ScaleTransform Scale;
        }

        const double TargetW = 260, TargetH = 146, TargetGap = 34;
        static readonly Color Listening = Theme.Hex("#3D7BFF");

        readonly IrText _irText = new IrText();
        FrameworkElement _search;
        TextBlock _searchQuery, _searchCaret, _searchStatus, _searchGlyph;
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

        bool SearchOpen { get { return _search != null && _search.Visibility == Visibility.Visible; } }

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
            var words = new StackPanel { Margin = new Thickness(40, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(queryLine);
            words.Children.Add(_searchStatus);
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(_searchMic);
            top.Children.Add(words);

            TextBlock label = SectionLabel("Search in");
            label.Margin = new Thickness(0, 120, 0, 24);
            _targetRow = new StackPanel { Orientation = Orientation.Horizontal, RenderTransform = _targetShift };
            var targetHost = new Canvas { Width = DesignW - 2 * Side + 2 * RingPad, Height = TargetH + 2 * RingPad, ClipToBounds = false, Margin = new Thickness(-RingPad, 0, 0, 0) };
            targetHost.Children.Add(_targetRow);
            var targets = new StackPanel();
            targets.Children.Add(label);
            targets.Children.Add(targetHost);
            _searchTargets = targets;

            var column = new StackPanel { Margin = new Thickness(Side, 170, Side, 0) };
            column.Children.Add(top);
            column.Children.Add(targets);

            var hints = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(Side, 0, 0, 60) };
            hints.Children.Add(KeyHint("OK", "Search"));
            hints.Children.Add(KeyHint("← →", "Choose the app"));
            hints.Children.Add(KeyHint("Back", "Close"));
            hints.Children.Add(KeyHint("Delete", "Remove a letter"));

            // The home screen is hidden while search is open (see OpenSearch), so the background glow shows through.
            var overlay = new Grid { Background = Brushes.Transparent, Visibility = Visibility.Collapsed };
            overlay.Children.Add(column);
            overlay.Children.Add(hints);
            return overlay;
        }

        SearchTarget MakeTarget(Tile t)
        {
            var target = new SearchTarget { Tile = t };
            var face = new Grid();
            if (t.ImagePath != null && File.Exists(t.ImagePath))
                face.Children.Add(new Image { Source = LoadBitmap(t.ImagePath, 520), Stretch = Stretch.Uniform, Margin = new Thickness(22) });
            else
                face.Children.Add(TileLabel(t));
            var card = new Border
            {
                Width = TargetW, Height = TargetH, CornerRadius = new CornerRadius(16), Background = TileBrush(t), Child = face,
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
                Width = TargetW + 2 * RingPad, Height = TargetH + 2 * RingPad, Margin = new Thickness(0, 0, TargetGap - 2 * RingPad, 0),
                Background = Brushes.Transparent, RenderTransformOrigin = new Point(0.5, 0.5), Cursor = Cursors.Hand,
            };
            host.Children.Add(card);
            host.Children.Add(target.Ring);
            target.Scale = new ScaleTransform(1, 1);
            host.RenderTransform = target.Scale;
            host.MouseEnter += (s, e) =>
            {
                if (!_pointer) return;
                _targetIndex = _targets.IndexOf(target);
                RefreshTargets(true);
            };
            host.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                _targetIndex = _targets.IndexOf(target);
                OpenTarget();
            };
            target.Root = host;
            return target;
        }

        void BuildTargets()
        {
            _targets.Clear();
            _targetRow.Children.Clear();
            foreach (Tile t in _cfg.AppTiles)
            {
                if (Launcher.SearchTemplate(t) == null) continue;
                SearchTarget target = MakeTarget(t);
                _targets.Add(target);
                _targetRow.Children.Add(target.Root);
            }
            _targetIndex = Math.Max(0, Math.Min(_targetIndex, _targets.Count - 1));
            RefreshTargets(false);
        }

        void RefreshTargets(bool animate)
        {
            animate &= !_opt.Preview;
            for (int i = 0; i < _targets.Count; i++)
            {
                SearchTarget target = _targets[i];
                bool on = i == _targetIndex;
                Theme.AnimateTo(target.Scale, ScaleTransform.ScaleXProperty, on ? 1.08 : 1, animate);
                Theme.AnimateTo(target.Scale, ScaleTransform.ScaleYProperty, on ? 1.08 : 1, animate);
                Theme.AnimateTo(target.Ring, OpacityProperty, on ? 1 : 0, animate);
                Theme.AnimateTo(target.Root, OpacityProperty, on ? 1 : 0.7, animate);
                Panel.SetZIndex(target.Root, on ? 1 : 0);
            }
            // Slide the row so the chosen app is always on screen.
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
            _searchCaret.Visibility = _listening || _receiving ? Visibility.Collapsed : Visibility.Visible;

            string status;
            Color color = Theme.Muted;
            if (_receiving) status = "Getting the words from your phone…";
            else if (_listening) status = _duckedFrom >= 0 ? "Speak to your phone. The TV is turned down while it listens." : "Speak to your phone.";
            else if (_searchError != null)
            {
                status = _searchError;
                color = Theme.Warn;
            }
            else if (_targets.Count == 0) status = "None of your apps can search. Add Search = … to an app in couchtv.ini.";
            else if (empty) status = "Tap the mic on the phone remote and say what to watch, or type it.";
            else status = "Choose where to search, then press OK.";
            _searchStatus.Text = status;
            _searchStatus.Foreground = Theme.Brush(color);

            bool live = _listening || _receiving;
            _searchMic.Background = Theme.Brush(live ? Listening : Theme.Chip);
            _searchGlyph.Text = live ? "" : "";   // microphone : magnifier
            _searchTargets.Opacity = empty ? 0.35 : 1;
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
                BuildTargets();
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
            if (_receiving || _irText.Active) _ignoreText = true;   // closed while words were still arriving
            _receiving = false;
            _searchError = null;
            IntPtr back = _searchReturnTo;
            _searchReturnTo = IntPtr.Zero;
            if (returnToApp && back != IntPtr.Zero && Native.IsWindow(back)) Shell.ForceForeground(back);
        }

        void OpenTarget()
        {
            if (_targets.Count == 0) return;
            if (_query.Trim().Length == 0)
            {
                _searchError = "Say or type something to search for first.";
                UpdateSearch();
                return;
            }
            Tile t = _targets[_targetIndex].Tile;
            string url = Launcher.SearchUrl(t, _query);
            if (url == null) return;
            Log.Info("Search in " + t.Name + ": " + _query);
            CloseSearch(false);
            OpenFromAnywhere(t.WithUrl(url));
        }

        // ================================================================ voice search from the phone

        void StartListening()
        {
            _listening = true;
            _listenUntil = DateTime.Now.AddSeconds(20);
            _query = "";
            _searchError = null;
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
                    UpdateSearch();
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
            switch (key)
            {
                case Key.Left: MoveTarget(-1); return true;
                case Key.Right: MoveTarget(1); return true;
                case Key.Up: case Key.Down: return true;
                case Key.Enter: OpenTarget(); return true;
                case Key.Escape: case Key.BrowserBack: CloseSearch(true); return true;
                case Key.Back:
                    if (_query.Length > 0)
                    {
                        int cut = _query.Length >= 2 && char.IsLowSurrogate(_query[_query.Length - 1]) ? 2 : 1;
                        _query = _query.Substring(0, _query.Length - cut);
                        _searchError = null;
                        UpdateSearch();
                    }
                    return true;
                case Key.Delete:
                    _query = "";
                    UpdateSearch();
                    return true;
                default:
                    return false;
            }
        }

        void MoveTarget(int step)
        {
            int next = _targetIndex + step;
            if (next < 0 || next >= _targets.Count) return;
            _targetIndex = next;
            RefreshTargets(true);
        }

        void OnSearchText(object sender, TextCompositionEventArgs e)
        {
            if (!SearchOpen || string.IsNullOrEmpty(e.Text)) return;
            e.Handled = true;
            var text = new StringBuilder(_query);
            foreach (char c in e.Text)
                if (!char.IsControl(c)) text.Append(c);
            if (text.Length == _query.Length || text.Length > 80) return;
            StopListening();
            _query = text.ToString();
            _searchError = null;
            UpdateSearch();
        }
    }
}
