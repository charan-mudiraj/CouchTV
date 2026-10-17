using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace CouchTV
{
    /// <summary>
    /// Music left playing behind other apps: a round button at the top right of the screen, over whatever app is in
    /// front, with the song's picture and its timeline as a ring round it. It opens into a small player (the title,
    /// previous, play/pause, next, back to its app, stop) for a few seconds when it first shows and when the song
    /// changes, and stays open while it's used: a click on the button, or Play/Pause held on the remote (whose arrows,
    /// OK and Back then work it: HomeWindow.Media.cs). Never takes the focus from the app in front.
    /// </summary>
    internal sealed class MiniPlayer : Window
    {
        const double W = 640, H = 150, Bubble = 96, PanelH = 132;
        static readonly string[] Names = { "previous", "toggle", "next", "open", "stop" };

        /// <summary>A button: previous, toggle, next, open (back to its app) or stop.</summary>
        public Action<string> Pressed;

        /// <summary>Opened to be used (a click, or the remote): it takes the remote's arrows, OK and Back meanwhile.</summary>
        public bool Opened { get; private set; }

        readonly double _scale = Math.Max(0.6, SystemParameters.PrimaryScreenHeight / 1080.0);
        readonly ScaleTransform _panelScale = new ScaleTransform(0, 1);
        readonly Border _panel;
        readonly Grid _bubble;
        readonly ImageBrush _art = new ImageBrush { Stretch = Stretch.UniformToFill };
        readonly Ellipse _artDisc, _pausedVeil;
        readonly TextBlock _placeholder, _pausedGlyph, _status, _time, _title, _focusName;
        readonly Path _ring;
        readonly Border[] _buttons = new Border[5];
        readonly TextBlock[] _glyphs = new TextBlock[5];
        readonly DispatcherTimer _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        MediaNow _now;
        string _app = "", _titleShown, _artShown;
        bool _expanded;
        int _focus = 1;
        DateTime _peekUntil, _usedAt, _changedAt;

        public MiniPlayer()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            ResizeMode = ResizeMode.NoResize;
            Title = "CouchTV mini player";
            Width = W * _scale;
            Height = H * _scale;

            var stage = new Canvas { Width = W, Height = H };

            // The player: a pill that slides out leftwards from under the round button.
            _panel = new Border
            {
                Width = W - 10 - Bubble / 2, Height = PanelH, CornerRadius = new CornerRadius(PanelH / 2),
                Background = Theme.Brush(Theme.Panel), BorderThickness = new Thickness(1.5),
                BorderBrush = Theme.Brush(Color.FromArgb(0x30, 255, 255, 255)),
                RenderTransform = _panelScale, RenderTransformOrigin = new Point(1, 0.5), Opacity = 0,
                Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.55, Color = Colors.Black },
            };
            var content = new StackPanel { Margin = new Thickness(36, 14, Bubble / 2 + 26, 0) };
            var top = new Grid();
            _status = Theme.Text("", 16, Theme.Muted, FontWeights.SemiBold);
            _time = Theme.Text("", 16, Theme.Faint, FontWeights.Normal);
            _time.HorizontalAlignment = HorizontalAlignment.Right;
            top.Children.Add(_status);
            top.Children.Add(_time);
            content.Children.Add(top);
            _title = Theme.Text("", 24, Theme.Ink, FontWeights.SemiBold);
            _title.TextTrimming = TextTrimming.CharacterEllipsis;
            _title.Margin = new Thickness(0, 2, 0, 8);
            content.Children.Add(_title);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            string[] glyphs = { "", "", "", "", "" };
            for (int i = 0; i < 5; i++)
            {
                int index = i;
                _glyphs[i] = Theme.Text(glyphs[i], i == 1 ? 20 : 17, Theme.Ink, FontWeights.Normal, Theme.Icons);
                _glyphs[i].HorizontalAlignment = HorizontalAlignment.Center;
                _glyphs[i].VerticalAlignment = VerticalAlignment.Center;
                _buttons[i] = new Border
                {
                    Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Child = _glyphs[i], Cursor = Cursors.Hand,
                    Margin = new Thickness(0, 0, i == 2 ? 26 : 10, 0), Background = Theme.Brush(Color.FromArgb(0x1C, 255, 255, 255)),
                };
                _buttons[i].MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    Press(index);
                };
                row.Children.Add(_buttons[i]);
            }
            _focusName = Theme.Text("", 17, Theme.Muted, FontWeights.Normal);
            _focusName.VerticalAlignment = VerticalAlignment.Center;
            _focusName.Margin = new Thickness(4, 0, 0, 0);
            row.Children.Add(_focusName);
            content.Children.Add(row);
            _panel.Child = content;
            Canvas.SetLeft(_panel, 0);
            Canvas.SetTop(_panel, (H - PanelH) / 2);
            stage.Children.Add(_panel);

            // The round button: the song's picture with its timeline as a ring round it.
            _bubble = new Grid { Width = Bubble, Height = Bubble, Cursor = Cursors.Hand };
            _bubble.Children.Add(new Ellipse
            {
                Fill = Theme.Brush(Theme.Panel),
                Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.6, Color = Colors.Black },
            });
            _placeholder = Theme.Text("", 34, Theme.Muted, FontWeights.Normal, Theme.Icons);
            _placeholder.HorizontalAlignment = HorizontalAlignment.Center;
            _placeholder.VerticalAlignment = VerticalAlignment.Center;
            _bubble.Children.Add(_placeholder);
            _artDisc = new Ellipse { Margin = new Thickness(9), Fill = _art };
            _bubble.Children.Add(_artDisc);
            _pausedVeil = new Ellipse { Margin = new Thickness(9), Fill = Theme.Brush(Color.FromArgb(0x99, 0, 0, 0)) };
            _bubble.Children.Add(_pausedVeil);
            _pausedGlyph = Theme.Text("", 28, Theme.Ink, FontWeights.Normal, Theme.Icons);
            _pausedGlyph.HorizontalAlignment = HorizontalAlignment.Center;
            _pausedGlyph.VerticalAlignment = VerticalAlignment.Center;
            _bubble.Children.Add(_pausedGlyph);
            _bubble.Children.Add(new Ellipse { Margin = new Thickness(2.5), Stroke = Theme.Brush(Color.FromArgb(0x40, 255, 255, 255)), StrokeThickness = 4 });
            _ring = new Path { Stroke = Theme.Brush(Theme.Ink), StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            _bubble.Children.Add(_ring);
            _bubble.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (Opened) Fold(true);
                else Open();
            };
            Canvas.SetLeft(_bubble, W - 10 - Bubble);
            Canvas.SetTop(_bubble, (H - Bubble) / 2);
            stage.Children.Add(_bubble);

            Content = new Border { Child = stage, LayoutTransform = new ScaleTransform(_scale, _scale) };
            MouseEnter += (s, e) => Used();
            _clock.Tick += (s, e) => Tick();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            // Clickable, but never the focus: the app in front keeps the keyboard.
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, exStyle | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        }

        /// <summary>What plays now, and where ("YouTube"); shows the button if it was hidden (opening for a moment).</summary>
        public void Show(MediaNow now, string app)
        {
            _now = now;
            _app = app ?? "";
            Refresh();
            if (IsVisible) return;
            double margin = 30 * _scale;
            Left = SystemParameters.PrimaryScreenWidth - Width - margin + 10 * _scale;
            Top = margin - (H - Bubble) / 2 * _scale;
            Opacity = 1;
            base.Show();
            KeepOnTop();
            Peek(5);
            _clock.Start();
        }

        public void HideNow()
        {
            if (!IsVisible) return;
            Opened = false;
            Expand(false, false);
            _clock.Stop();
            Hide();
        }

        /// <summary>Opened to be used: stays open while it is, and takes the remote's arrows, OK and Back.</summary>
        public void Open()
        {
            Opened = true;
            _focus = 1;
            Used();
            Expand(true, true);
            StyleButtons();
        }

        /// <summary>Back to just the button (now, or once a peek that's still due to show is over).</summary>
        public void Fold(bool now)
        {
            Opened = false;
            if (now) _peekUntil = DateTime.MinValue;
            StyleButtons();
            if (now || DateTime.Now >= _peekUntil) Expand(false, true);
        }

        /// <summary>A remote key while it's opened: true if it took it.</summary>
        public bool Key(string action)
        {
            if (!Opened) return false;
            Used();
            switch (action)
            {
                case "left":
                    _focus = Math.Max(0, _focus - 1);
                    StyleButtons();
                    return true;
                case "right":
                    _focus = Math.Min(4, _focus + 1);
                    StyleButtons();
                    return true;
                case "ok":
                    Press(_focus);
                    return true;
                case "playpause":
                    Press(1);
                    return true;
                case "back": case "escape": case "up": case "down":
                    Fold(true);
                    return true;
                default:
                    return false;
            }
        }

        void Press(int index)
        {
            Used();
            if (Pressed != null) Pressed(Names[index]);
        }

        void Used()
        {
            _usedAt = DateTime.Now;
            if (Opacity < 1) BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        }

        /// <summary>Open for a few seconds without taking any keys: here's what's playing.</summary>
        void Peek(double seconds)
        {
            _peekUntil = DateTime.Now.AddSeconds(seconds);
            Used();
            Expand(true, true);
        }

        void Expand(bool open, bool animate)
        {
            if (open == _expanded && animate) return;
            _expanded = open;
            _panel.IsHitTestVisible = open;
            if (!animate)
            {
                _panelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _panel.BeginAnimation(OpacityProperty, null);
                _panelScale.ScaleX = _panel.Opacity = open ? 1 : 0;
                return;
            }
            var ease = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };
            var time = TimeSpan.FromMilliseconds(animate ? (open ? 260 : 200) : 0);
            _panelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(open ? 1 : 0, time) { EasingFunction = ease });
            _panel.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 1 : 0, time));
            _panel.IsHitTestVisible = open;
        }

        void Tick()
        {
            if (!IsVisible) return;
            DateTime now = DateTime.Now;
            if (_expanded && !Opened && now >= _peekUntil) Expand(false, true);
            if (Opened && (now - _usedAt).TotalSeconds > 10) Fold(true);
            // Left alone, the button fades a little, to sit quietly over a film.
            if (!_expanded && Opacity >= 1 && (now - _usedAt).TotalSeconds > 6 && !IsMouseOver)
                BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, TimeSpan.FromMilliseconds(600)));
            if ((now - _changedAt).TotalSeconds > 2) KeepOnTop();
            Timeline();
        }

        /// <summary>A full-screen app can put itself above: back on top (without taking the focus).</summary>
        void KeepOnTop()
        {
            _changedAt = DateTime.Now;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        void Refresh()
        {
            MediaNow m = _now;
            if (m == null) return;
            _status.Text = (m.Playing ? "Playing" : "Paused") + (_app.Length > 0 ? " · " + _app : "");
            string title = m.Title.Length > 0 ? m.Title : m.Artist;
            if (m.Artist.Length > 0 && m.Title.Length > 0) title += " · " + m.Artist;
            _title.Text = title;
            if (_titleShown != null && _titleShown != m.Title && IsVisible) Peek(4);   // another song: say so
            _titleShown = m.Title;
            if (m.ArtId != _artShown)
            {
                _artShown = m.ArtId;
                BitmapSource picture = Picture(m.Art);
                _art.ImageSource = picture;
                _artDisc.Visibility = picture != null ? Visibility.Visible : Visibility.Collapsed;
                _placeholder.Visibility = picture != null ? Visibility.Collapsed : Visibility.Visible;
            }
            _pausedVeil.Visibility = _pausedGlyph.Visibility = m.Playing ? Visibility.Collapsed : Visibility.Visible;
            _glyphs[1].Text = m.Playing ? "" : "";
            StyleButtons();
            Timeline();
        }

        void StyleButtons()
        {
            MediaNow m = _now;
            bool[] works = { m == null || m.CanPrevious || m.CanSeek, m == null || m.CanPlayPause, m == null || m.CanNext, true, true };
            string[] names = { "Previous", m != null && m.Playing ? "Pause" : "Play", "Next", "Open " + (_app.Length > 0 ? _app : "it"), "Stop" };
            for (int i = 0; i < 5; i++)
            {
                bool focused = Opened && i == _focus;
                _buttons[i].Background = Theme.Brush(focused ? Colors.White : Color.FromArgb(0x1C, 255, 255, 255));
                _glyphs[i].Foreground = Theme.Brush(focused ? Theme.Base : Theme.Ink);
                _buttons[i].Opacity = works[i] ? 1 : 0.35;
            }
            _focusName.Text = Opened ? names[_focus] : "";
        }

        /// <summary>The ring round the button, and "1:23 / 4:05".</summary>
        void Timeline()
        {
            MediaNow m = _now;
            if (m == null || m.DurationMs <= 0)
            {
                _ring.Data = null;
                _time.Text = m != null && m.DurationMs <= 0 ? "Live" : "";
                return;
            }
            long at = m.PositionAt(DateTime.UtcNow);
            _time.Text = Clock(at) + " / " + Clock(m.DurationMs);
            double fraction = Math.Max(0, Math.Min(0.9999, (double)at / m.DurationMs));
            double r = Bubble / 2 - 4.5, c = Bubble / 2, angle = fraction * 2 * Math.PI;
            var figure = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
            figure.Segments.Add(new ArcSegment(new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle)), new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            _ring.Data = fraction > 0.002 ? geometry : null;
        }

        static string Clock(long ms)
        {
            long s = Math.Max(0, ms / 1000);
            return s >= 3600 ? string.Format("{0}:{1:00}:{2:00}", s / 3600, s / 60 % 60, s % 60) : string.Format("{0}:{1:00}", s / 60, s % 60);
        }

        static BitmapSource Picture(byte[] jpeg)
        {
            if (jpeg == null) return null;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new System.IO.MemoryStream(jpeg);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception) { return null; }
        }

        /// <summary>For screenshots: the player drawn open (focus on a button) or as just the button.</summary>
        internal FrameworkElement Preview(MediaNow now, string app, bool open)
        {
            _now = now;
            _app = app;
            Refresh();
            Opened = open;
            Expand(open, false);
            StyleButtons();
            var root = (Border)Content;
            Content = null;
            root.LayoutTransform = Transform.Identity;   // drawn at its design size
            return root;
        }
    }
}
