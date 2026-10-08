using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
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
using Microsoft.Win32;

namespace CouchTV
{
    internal sealed class HomeOptions
    {
        public bool Preview;          // offscreen render for screenshots: no window, timers or hotkeys
        public bool Windowed;         // small corner window, for smoke tests
        public int ExitAfterSeconds;  // close by itself, for smoke tests
        public string StartupMessage; // shown once the home screen is up (e.g. after an update)
    }

    /// <summary>One tile on the home screen plus the visuals that change with focus.</summary>
    internal sealed class TileView
    {
        public Tile Tile;
        public bool IsSystem, Focused, Styled;
        public FrameworkElement Root;
        public ScaleTransform Scale;
        public Border Card, Ring;       // app tiles
        public Ellipse Disc, Badge;     // settings & power tiles
        public TextBlock Glyph, Caption;
    }

    /// <summary>The full-screen TV home screen. Designed on a 1920x1080 canvas and scaled to any screen.</summary>
    internal sealed partial class HomeWindow : Window
    {
        public const string Caption = "CouchTV Home Screen";

        const double DesignW = 1920, DesignH = 1080, Side = 112;
        const double TileW = 300, TileH = 170, TileGap = 40, RingPad = 8;
        const double SysW = 150, SysGap = 8, DiscSize = 104;
        const int VolumeUpId = 101, VolumeDownId = 102, MuteId = 103;

        Config _cfg;
        readonly HomeOptions _opt;
        IntPtr _hwnd;
        bool _allowClose;

        Grid _layers;
        Canvas _stage;
        StackPanel _appsRow, _sysRow;
        readonly TranslateTransform[] _shift = { new TranslateTransform(), new TranslateTransform() };
        readonly double[] _scroll = new double[2];
        TextBlock _greeting, _time, _ampm, _date, _net, _focusTitle, _focusHint, _launchText, _toastText, _confirmTitle, _confirmText;
        Ellipse _netDot;
        FrameworkElement _focusInfo, _launch, _confirm, _toast, _empty;
        Border _confirmYes, _confirmNo;
        readonly RotateTransform _spin = new RotateTransform();

        readonly List<TileView> _apps = new List<TileView>(), _sys = new List<TileView>();
        int _row;
        readonly int[] _col = new int[2];

        DispatcherTimer _timer;
        int _ticks;
        DateTime _toastUntil, _launchUntil, _lastMouse = DateTime.Now;
        bool _toastShown;
        DateTime? _sleepAt;
        bool _sleepWarned, _pointer = true;
        Point _keyMouse;
        Action _confirmAction;
        int _confirmIndex;

        readonly List<int> _homeIds = new List<int>();
        readonly List<uint> _holdKeys = new List<uint>();
        bool _volumeKeys, _winTapHome, _winDown, _winCombo;
        uint _heldVk;
        DispatcherTimer _holdTimer;
        IntPtr _hook;
        Native.LowLevelKeyboardProc _hookProc;
        OsdWindow _osd;

        public HomeWindow(Config cfg, HomeOptions options)
        {
            _cfg = cfg;
            _opt = options ?? new HomeOptions();
            Title = Caption;
            Background = Theme.Brush(Theme.Base);
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            UseLayoutRounding = true;

            Content = BuildRoot();
            BuildTiles();
            _row = _apps.Count > 0 ? 0 : 1;
            RefreshFocus(false);
            UpdateClock();
            UpdateNetwork();
            if (_opt.Preview) return;

            FitToScreen();
            if (_opt.Windowed) ShowActivated = false;
            PreviewKeyDown += OnKey;
            PreviewTextInput += OnSearchText;
            MouseMove += OnMouseMove;
            PreviewMouseLeftButtonUp += OnClick;
            PreviewMouseRightButtonUp += OnRightClick;
            Loaded += (s, e) =>
            {
                if (!_opt.Windowed)
                {
                    Activate();
                    Shell.ForceForeground(_hwnd);
                }
                Keyboard.Focus(this);
                if (_opt.StartupMessage != null) Toast(_opt.StartupMessage);
            };
            Activated += (s, e) => { UpdateNetwork(); Keyboard.Focus(this); };
            Deactivated += (s, e) => HideLaunch();      // the app is in front now
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.PowerModeChanged += OnPowerChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTick;
            _timer.Start();
            StartRemote();
            StartUpdateChecks();

            if (_opt.ExitAfterSeconds > 0)
            {
                var exit = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_opt.ExitAfterSeconds) };
                exit.Tick += (s, e) => { exit.Stop(); _allowClose = true; Close(); };
                exit.Start();
            }
        }

        /// <summary>Lets the window close (Windows is signing out or shutting down).</summary>
        public void AllowClose() { _allowClose = true; }

        // ================================================================ layout

        Grid BuildRoot()
        {
            var root = new Grid { Background = Theme.Brush(Theme.Base), ClipToBounds = true };
            root.Children.Add(Glow(new Point(0.06, -0.05), 0.85, 1.05, Theme.Hex("#B31E3A72")));
            root.Children.Add(Glow(new Point(1.0, 1.08), 0.7, 0.85, Theme.Hex("#80472066")));
            if (!string.IsNullOrEmpty(_cfg.Wallpaper) && File.Exists(_cfg.Wallpaper))
            {
                root.Children.Add(new Image { Source = LoadBitmap(_cfg.Wallpaper, 1920), Stretch = Stretch.UniformToFill });
                root.Children.Add(new Rectangle
                {
                    Fill = new LinearGradientBrush(Theme.Alpha(Theme.Base, 0xF0), Theme.Alpha(Theme.Base, 0xA0), 90),
                });
            }

            _stage = new Canvas { Width = DesignW, Height = DesignH };
            _layers = new Grid { Width = DesignW, Height = DesignH };
            _layers.Children.Add(_stage);
            _layers.Children.Add(_toast = BuildToast());
            _layers.Children.Add(_launch = BuildLaunchOverlay());
            _layers.Children.Add(_confirm = BuildConfirm());
            _layers.Children.Add(_setup = BuildSetup());
            _layers.Children.Add(_search = BuildSearch());
            root.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = _layers });

            BuildHeader();
            BuildRows();
            return root;
        }

        static Rectangle Glow(Point center, double rx, double ry, Color color)
        {
            var brush = new RadialGradientBrush
            {
                Center = center, GradientOrigin = center, RadiusX = rx, RadiusY = ry,
                GradientStops = { new GradientStop(color, 0), new GradientStop(Theme.Alpha(color, 0), 1) },
            };
            return new Rectangle { Fill = brush, IsHitTestVisible = false };
        }

        void BuildHeader()
        {
            _greeting = Theme.Text("", 30, Theme.Muted, FontWeights.Normal);
            Place(_greeting, Side, 72);

            var clock = new StackPanel { Orientation = Orientation.Horizontal };
            _time = Theme.Text("", 128, Theme.Ink, FontWeights.Light, Theme.Display);
            _ampm = Theme.Text("", 38, Theme.Muted, FontWeights.Normal, Theme.Display);
            _ampm.VerticalAlignment = VerticalAlignment.Bottom;
            _ampm.Margin = new Thickness(14, 0, 0, 30);
            clock.Children.Add(_time);
            clock.Children.Add(_ampm);
            Place(clock, Side - 6, 104);

            var right = new StackPanel();
            _date = Theme.Text("", 30, Theme.Hex("#D6DBE4"), FontWeights.Normal);
            _date.HorizontalAlignment = HorizontalAlignment.Right;
            var pill = new Border
            {
                CornerRadius = new CornerRadius(22), Padding = new Thickness(18, 8, 20, 9), Margin = new Thickness(0, 18, 0, 0),
                Background = Theme.Brush(Color.FromArgb(0x1F, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Right,
            };
            var pillRow = new StackPanel { Orientation = Orientation.Horizontal };
            _netDot = new Ellipse { Width = 12, Height = 12, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            _net = Theme.Text("", 22, Theme.Ink, FontWeights.Normal);
            pillRow.Children.Add(_netDot);
            pillRow.Children.Add(_net);
            pill.Child = pillRow;
            pill.Margin = new Thickness(0);
            var pills = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            pills.Children.Add(_updatePill = BuildUpdatePill());
            pills.Children.Add(pill);
            right.Children.Add(_date);
            right.Children.Add(pills);
            Canvas.SetRight(right, Side);
            Canvas.SetTop(right, 86);
            _stage.Children.Add(right);
        }

        void BuildRows()
        {
            Place(SectionLabel("Apps"), Side, 352);
            var appsHost = new Canvas { Width = DesignW - 2 * Side + 2 * RingPad, Height = TileH + 2 * RingPad };
            _appsRow = new StackPanel { Orientation = Orientation.Horizontal, RenderTransform = _shift[0] };
            appsHost.Children.Add(_appsRow);
            Place(appsHost, Side - RingPad, 400);

            _empty = Theme.Text("No apps yet. Add some to couchtv.ini, then press F5.", 28, Theme.Muted, FontWeights.Normal);
            Place(_empty, Side, 470);

            var info = new StackPanel();
            _focusTitle = Theme.Text("", 34, Theme.Ink, FontWeights.SemiBold);
            _focusHint = Theme.Text("", 22, Theme.Muted, FontWeights.Normal);
            _focusHint.Margin = new Thickness(0, 4, 0, 0);
            info.Children.Add(_focusTitle);
            info.Children.Add(_focusHint);
            _focusInfo = info;
            Place(info, Side, 618);

            Place(SectionLabel("Settings & power"), Side, 734);
            var sysHost = new Canvas { Width = DesignW - 2 * Side + (SysW - DiscSize), Height = 180 };
            _sysRow = new StackPanel { Orientation = Orientation.Horizontal, RenderTransform = _shift[1] };
            sysHost.Children.Add(_sysRow);
            Place(sysHost, Side - (SysW - DiscSize) / 2, 782);

            var hints = new StackPanel { Orientation = Orientation.Horizontal };
            hints.Children.Add(KeyHint("OK", "Open"));
            hints.Children.Add(KeyHint("Home", "Come back here (or hold Back)"));
            hints.Children.Add(KeyHint("← ↑ → ↓", "Move"));
            Place(hints, Side, 996);
        }

        static TextBlock SectionLabel(string text) { return Theme.Text(text, 26, Theme.Muted, FontWeights.SemiBold); }

        static UIElement KeyHint(string key, string action)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 44, 0) };
            row.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.5), Padding = new Thickness(10, 2, 10, 4),
                BorderBrush = Theme.Brush(Theme.Faint), VerticalAlignment = VerticalAlignment.Center,
                Child = Theme.Text(key, 19, Theme.Muted, FontWeights.SemiBold),
            });
            TextBlock label = Theme.Text(action, 22, Theme.Faint, FontWeights.Normal);
            label.Margin = new Thickness(12, 0, 0, 0);
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
            return row;
        }

        FrameworkElement BuildToast()
        {
            _toastText = Theme.Text("", 24, Theme.Ink, FontWeights.Normal);
            return new Border
            {
                CornerRadius = new CornerRadius(18), Padding = new Thickness(28, 14, 28, 16), Margin = new Thickness(0, 74, 0, 0),
                Background = Theme.Brush(Theme.Alpha(Theme.Panel, 0xF2)), BorderBrush = Theme.Brush(Color.FromArgb(0x26, 255, 255, 255)),
                BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                MaxWidth = 1000,
                Child = _toastText, Opacity = 0, IsHitTestVisible = false,
            };
        }

        FrameworkElement BuildLaunchOverlay()
        {
            var overlay = new Grid { Background = Theme.Brush(Color.FromArgb(0xE6, 8, 10, 14)), Visibility = Visibility.Collapsed };
            var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            column.Children.Add(new System.Windows.Shapes.Path
            {
                Width = 72, Height = 72, Stroke = Brushes.White, StrokeThickness = 6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M 36,6 A 30,30 0 1 1 6,36"),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _spin, HorizontalAlignment = HorizontalAlignment.Center,
            });
            _launchText = Theme.Text("", 40, Theme.Ink, FontWeights.SemiBold);
            _launchText.Margin = new Thickness(0, 36, 0, 0);
            _launchText.HorizontalAlignment = HorizontalAlignment.Center;
            column.Children.Add(_launchText);
            TextBlock hint = _launchHint = Theme.Text(LaunchHintText, 24, Theme.Muted, FontWeights.Normal);
            hint.Margin = new Thickness(0, 14, 0, 0);
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            column.Children.Add(hint);
            overlay.Children.Add(column);
            return overlay;
        }

        FrameworkElement BuildConfirm()
        {
            var overlay = new Grid { Background = Theme.Brush(Color.FromArgb(0xD9, 8, 10, 14)), Visibility = Visibility.Collapsed };
            var column = new StackPanel();
            _confirmTitle = Theme.Text("", 44, Theme.Ink, FontWeights.SemiBold, Theme.Display);
            _confirmText = Theme.Text("", 26, Theme.Muted, FontWeights.Normal);
            _confirmText.TextWrapping = TextWrapping.Wrap;
            _confirmText.Margin = new Thickness(0, 14, 0, 40);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            _confirmYes = PillButton(0);
            _confirmNo = PillButton(1);
            buttons.Children.Add(_confirmYes);
            buttons.Children.Add(_confirmNo);
            column.Children.Add(_confirmTitle);
            column.Children.Add(_confirmText);
            column.Children.Add(buttons);
            overlay.Children.Add(new Border
            {
                Width = 780, CornerRadius = new CornerRadius(28), Padding = new Thickness(56, 48, 56, 52),
                Background = Theme.Brush(Theme.Panel), BorderBrush = Theme.Brush(Color.FromArgb(0x22, 255, 255, 255)),
                BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Child = column,
            });
            return overlay;
        }

        Border PillButton(int index)
        {
            var button = new Border
            {
                CornerRadius = new CornerRadius(32), Padding = new Thickness(44, 14, 44, 17), Margin = new Thickness(0, 0, 20, 0),
                Child = Theme.Text("", 26, Theme.Ink, FontWeights.SemiBold), Cursor = Cursors.Hand,
            };
            button.MouseEnter += (s, e) => { if (_pointer) { _confirmIndex = index; RefreshConfirm(); } };
            button.MouseLeftButtonUp += (s, e) => { e.Handled = true; ChooseConfirm(index); };
            return button;
        }

        void Place(UIElement element, double x, double y)
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            _stage.Children.Add(element);
        }

        // ================================================================ tiles

        void BuildTiles()
        {
            _apps.Clear();
            _sys.Clear();
            _appsRow.Children.Clear();
            _sysRow.Children.Clear();
            foreach (Tile t in _cfg.AppTiles)
            {
                TileView v = MakeAppTile(t);
                _apps.Add(v);
                _appsRow.Children.Add(v.Root);
            }
            if (_appUpdate != null)
            {
                TileView update = MakeSystemTile(UpdateTile);
                update.Badge.Fill = Theme.Brush(Theme.Good);
                update.Badge.Visibility = Visibility.Visible;
                _sys.Add(update);
                _sysRow.Children.Add(update.Root);
            }
            foreach (Tile t in _cfg.SystemTiles)
            {
                TileView v = MakeSystemTile(t);
                _sys.Add(v);
                _sysRow.Children.Add(v.Root);
            }
            for (int r = 0; r < 2; r++) _col[r] = Math.Max(0, Math.Min(_col[r], Row(r).Count - 1));
            if (Row(_row).Count == 0) _row = 1 - _row;
            _empty.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateBadges();
            UpdateSleepCaption();
        }

        TileView MakeAppTile(Tile t)
        {
            var v = new TileView { Tile = t };
            var face = new Grid();
            if (t.ImagePath != null && File.Exists(t.ImagePath))
                face.Children.Add(new Image { Source = LoadBitmap(t.ImagePath, 600), Stretch = Stretch.Uniform, Margin = new Thickness(26) });
            else
                face.Children.Add(TileLabel(t));
            face.Children.Add(new Border     // soft top sheen for depth
            {
                CornerRadius = new CornerRadius(18), IsHitTestVisible = false,
                Background = new LinearGradientBrush(Color.FromArgb(0x22, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90),
            });
            v.Card = new Border
            {
                Width = TileW, Height = TileH, CornerRadius = new CornerRadius(18), Background = TileBrush(t), Child = face,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            v.Ring = new Border
            {
                CornerRadius = new CornerRadius(18 + RingPad), BorderBrush = Brushes.White, BorderThickness = new Thickness(4),
                Opacity = 0, IsHitTestVisible = false,
            };
            var host = new Grid
            {
                Width = TileW + 2 * RingPad, Height = TileH + 2 * RingPad, Margin = new Thickness(0, 0, TileGap - 2 * RingPad, 0),
                Background = Brushes.Transparent, RenderTransformOrigin = new Point(0.5, 0.5), Cursor = Cursors.Hand,
            };
            host.Children.Add(v.Card);
            host.Children.Add(v.Ring);
            v.Scale = new ScaleTransform(1, 1);
            host.RenderTransform = v.Scale;
            v.Root = host;
            HookMouse(v);
            return v;
        }

        static UIElement TileLabel(Tile t)
        {
            Brush foreground = Theme.Brush(t.Foreground);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (t.Glyph != '\0')
            {
                row.Children.Add(new TextBlock
                {
                    Text = t.Glyph.ToString(), FontFamily = Theme.Icons, FontSize = 46, Foreground = foreground,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 14, 0),
                });
            }
            row.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(t.Text) ? t.Label : t.Text, FontFamily = Theme.Display, FontSize = 50,
                FontWeight = t.Weight, Foreground = foreground, VerticalAlignment = VerticalAlignment.Center,
            });
            // Long names shrink to fit; short ones keep their size.
            return new Viewbox
            {
                Child = row, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(26, 18, 26, 18),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        }

        static Brush TileBrush(Tile t)
        {
            if (!t.HasBackground2) return Theme.Brush(t.Background);
            var brush = new LinearGradientBrush(t.Background, t.Background2, new Point(0, 0), new Point(1, 1));
            brush.Freeze();
            return brush;
        }

        TileView MakeSystemTile(Tile t)
        {
            var v = new TileView { Tile = t, IsSystem = true };
            v.Disc = new Ellipse { Fill = Theme.Brush(Theme.Chip), Stroke = Theme.Brush(Color.FromArgb(0x1A, 255, 255, 255)), StrokeThickness = 1.5 };
            v.Glyph = new TextBlock
            {
                Text = (t.Glyph == '\0' ? '' : t.Glyph).ToString(), FontFamily = Theme.Icons, FontSize = 38,
                Foreground = Theme.Brush(Theme.Ink), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            v.Badge = new Ellipse
            {
                Width = 22, Height = 22, Stroke = Theme.Brush(Theme.Base), StrokeThickness = 4, Fill = Theme.Brush(Theme.Warn),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 2, 0), Visibility = Visibility.Collapsed,
            };
            var disc = new Grid { Width = DiscSize, Height = DiscSize, HorizontalAlignment = HorizontalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5) };
            disc.Children.Add(v.Disc);
            disc.Children.Add(v.Glyph);
            disc.Children.Add(v.Badge);
            v.Scale = new ScaleTransform(1, 1);
            disc.RenderTransform = v.Scale;

            v.Caption = Theme.Text(t.Label, 22, Theme.Muted, FontWeights.Normal);
            v.Caption.TextAlignment = TextAlignment.Center;
            v.Caption.TextWrapping = TextWrapping.Wrap;
            v.Caption.HorizontalAlignment = HorizontalAlignment.Center;
            v.Caption.MaxWidth = SysW - 8;
            v.Caption.Margin = new Thickness(0, 16, 0, 0);

            var item = new StackPanel { Width = SysW, Margin = new Thickness(0, 0, SysGap, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            item.Children.Add(disc);
            item.Children.Add(v.Caption);
            v.Root = item;
            HookMouse(v);
            return v;
        }

        void HookMouse(TileView v)
        {
            v.Root.MouseEnter += (s, e) => { if (_pointer && !Modal) FocusTile(v); };
            v.Root.MouseLeftButtonUp += (s, e) =>
            {
                if (Modal) return;
                e.Handled = true;
                FocusTile(v);
                Open(v.Tile);
            };
        }

        bool Modal { get { return _confirm.Visibility == Visibility.Visible || SetupOpen || SearchOpen; } }

        List<TileView> Row(int r) { return r == 0 ? _apps : _sys; }

        TileView Focused
        {
            get
            {
                List<TileView> row = Row(_row);
                return row.Count == 0 ? null : row[Math.Min(_col[_row], row.Count - 1)];
            }
        }

        // ================================================================ focus & navigation

        void Move(int dRow, int dCol)
        {
            if (dRow != 0)
            {
                int next = _row + dRow;
                if (next < 0 || next > 1 || Row(next).Count == 0) return;
                _row = next;
            }
            else
            {
                int next = _col[_row] + dCol;
                if (next < 0 || next >= Row(_row).Count) return;
                _col[_row] = next;
            }
            RefreshFocus(true);
        }

        void FocusTile(TileView v)
        {
            int r = v.IsSystem ? 1 : 0;
            int c = Row(r).IndexOf(v);
            if (c < 0 || (_row == r && _col[r] == c)) return;
            _row = r;
            _col[r] = c;
            RefreshFocus(true);
        }

        void RefreshFocus(bool animate)
        {
            for (int r = 0; r < 2; r++)
            {
                List<TileView> row = Row(r);
                for (int c = 0; c < row.Count; c++) ApplyFocusStyle(row[c], r == _row && c == _col[r], animate);
                ScrollRow(r, animate);
            }
            TileView f = Focused;
            bool showInfo = f != null && !f.IsSystem;
            if (showInfo)
            {
                _focusTitle.Text = f.Tile.Label;
                _focusHint.Text = HintFor(f.Tile);
            }
            Theme.AnimateTo(_focusInfo, OpacityProperty, showInfo ? 1 : 0, animate);
        }

        void ApplyFocusStyle(TileView v, bool on, bool animate)
        {
            if (v.Styled && v.Focused == on) return;
            v.Styled = true;
            v.Focused = on;
            double scale = on ? 1.1 : 1.0;
            Theme.AnimateTo(v.Scale, ScaleTransform.ScaleXProperty, scale, animate);
            Theme.AnimateTo(v.Scale, ScaleTransform.ScaleYProperty, scale, animate);
            Panel.SetZIndex(v.Root, on ? 1 : 0);
            if (v.IsSystem)
            {
                v.Disc.Fill = Theme.Brush(on ? Colors.White : Theme.Chip);
                v.Glyph.Foreground = Theme.Brush(on ? Theme.Base : Theme.Ink);
                v.Caption.Foreground = Theme.Brush(on ? Theme.Ink : Theme.Muted);
                v.Caption.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
            else
            {
                Theme.AnimateTo(v.Ring, OpacityProperty, on ? 1 : 0, animate);
                Theme.AnimateTo(v.Root, OpacityProperty, on ? 1 : 0.8, animate);
                v.Card.Effect = on ? new DropShadowEffect { BlurRadius = 48, ShadowDepth = 12, Direction = 270, Opacity = 0.65, Color = Colors.Black } : null;
            }
        }

        /// <summary>Slides a row sideways so the focused tile is always fully on screen.</summary>
        void ScrollRow(int r, bool animate)
        {
            List<TileView> row = Row(r);
            if (row.Count == 0) return;
            double pitch = r == 0 ? TileW + TileGap : SysW + SysGap;
            double item = r == 0 ? TileW + 2 * RingPad : SysW;
            double view = DesignW - 2 * Side + (r == 0 ? 2 * RingPad : SysW - DiscSize);
            double total = (row.Count - 1) * pitch + item;
            double x = _col[r] * pitch;
            double offset = _scroll[r];
            if (x < offset) offset = x;
            else if (x + item > offset + view) offset = x + item - view;
            offset = Math.Max(0, Math.Min(offset, Math.Max(0, total - view)));
            _scroll[r] = offset;
            Theme.AnimateTo(_shift[r], TranslateTransform.XProperty, -offset, animate);
        }

        static string HintFor(Tile t)
        {
            if (!string.IsNullOrEmpty(t.Hint)) return t.Hint;
            switch (t.Kind)
            {
                case TileKind.Web: return t.Browser == "brave" ? "Opens in Brave · ads blocked" : "Opens in " + Cap(t.Browser);
                case TileKind.Browser: return "Opens the full " + Cap(t.Browser) + " browser";
                case TileKind.App: return "Opens " + System.IO.Path.GetFileNameWithoutExtension(t.Exe);
                default: return "";
            }
        }

        static string Cap(string s) { return string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1); }

        // ================================================================ input

        void OnKey(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.F4 && (Keyboard.Modifiers & ModifierKeys.Alt) != 0) return;   // let Alt+F4 reach OnClosing
            UsingKeys();
            if (_updating)
            {
                e.Handled = true;
                return;
            }

            if (SetupOpen)
            {
                SetupKey(key);
                e.Handled = true;
                return;
            }
            if (SearchOpen)
            {
                if (SearchKey(key)) e.Handled = true;
                return;
            }
            if (Modal)
            {
                switch (key)
                {
                    case Key.Left: case Key.Right: case Key.Up: case Key.Down:
                        _confirmIndex = 1 - _confirmIndex;
                        RefreshConfirm();
                        break;
                    case Key.Enter: case Key.Space: ChooseConfirm(_confirmIndex); break;
                    case Key.Escape: case Key.BrowserBack: case Key.Back: ChooseConfirm(1); break;
                }
                e.Handled = true;
                return;
            }

            switch (key)
            {
                case Key.Left: Move(0, -1); break;
                case Key.Right: Move(0, 1); break;
                case Key.Up: Move(-1, 0); break;
                case Key.Down: Move(1, 0); break;
                case Key.Enter:
                case Key.Space:
                    TileView f = Focused;
                    if (f != null) Open(f.Tile);
                    break;
                case Key.Home:
                    _row = _apps.Count > 0 ? 0 : 1;
                    _col[_row] = 0;
                    RefreshFocus(true);
                    break;
                case Key.F2: OpenRemoteSetup(); break;
                case Key.F3:
                case Key.BrowserSearch:
                    OpenSearch(false);
                    break;
                case Key.F5: Reload(); break;
                case Key.F6: CheckForUpdates(true); break;
                case Key.Escape:
                case Key.BrowserBack:
                case Key.Back:
                    HideLaunch();
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        void UsingKeys()
        {
            _pointer = false;
            _keyMouse = Mouse.GetPosition(this);
            if (IsActive) Mouse.OverrideCursor = Cursors.None;
        }

        /// <summary>
        /// Many remotes send a mouse click for OK while in air-mouse mode. After the arrow keys were used the
        /// pointer is hidden and wherever it rests is meaningless, so a click means OK on the highlighted tile.
        /// </summary>
        void OnClick(object sender, MouseButtonEventArgs e)
        {
            if (_pointer || SetupOpen) return;
            e.Handled = true;
            if (SearchOpen)
            {
                SearchKey(Key.Enter);
                return;
            }
            if (Modal)
            {
                ChooseConfirm(_confirmIndex);
                return;
            }
            TileView f = Focused;
            if (f != null) Open(f.Tile);
        }

        /// <summary>The same remotes send a right-click for Back.</summary>
        void OnRightClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (SetupOpen) return;
            if (SearchOpen) CloseSearch(true);
            else if (Modal) ChooseConfirm(1);
            else HideLaunch();
        }

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            // Air mice jitter, so ignore small movements after the arrow keys were used.
            Point p = e.GetPosition(this);
            if (!_pointer)
            {
                if ((p - _keyMouse).Length < 24) return;
                _pointer = true;
            }
            _lastMouse = DateTime.Now;
            if (Mouse.OverrideCursor != null) Mouse.OverrideCursor = null;
        }

        // ================================================================ actions

        void Open(Tile t)
        {
            if (t.Kind == TileKind.Action)
            {
                RunAction(t);
                return;
            }
            if (t.Kind == TileKind.Uri && t.Url.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) && !Shell.ExplorerRunning)
            {
                OpenSettingsWithDesktop(t);
                return;
            }
            LaunchResult result = Launcher.Open(_cfg, t);
            if (result.Message != null) Toast(result.Message);
            if (result.Ok) ShowLaunch(t);
        }

        /// <summary>Windows' Settings app only runs while the desktop (Explorer) is running, so start that first.</summary>
        void OpenSettingsWithDesktop(Tile t)
        {
            ShowLaunch(t);
            Shell.StartDesktop();
            DateTime started = DateTime.Now;
            var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            wait.Tick += (s, e) =>
            {
                bool ready = Shell.ExplorerRunning;
                if (!ready && (DateTime.Now - started).TotalSeconds < 20) return;
                wait.Stop();
                UpdateVolumeKeys();
                LaunchResult result = ready ? Launcher.Open(_cfg, t) : LaunchResult.Fail("Windows Settings couldn't start. Try the Windows desktop button.");
                if (result.Ok) return;
                HideLaunch();
                Toast(result.Message);
            };
            wait.Start();
        }

        void RunAction(Tile t)
        {
            switch (t.Action)
            {
                case "sleep":
                    Shell.Sleep();
                    break;
                case "sleeptimer":
                    CycleSleepTimer();
                    break;
                case "restart":
                    Confirm("Restart the TV?", "It will come back to this screen in about a minute.", "Restart", () => Shell.Shutdown(true));
                    break;
                case "shutdown":
                    Confirm("Turn off the TV?", "To turn it on again, press the power button on the PC. Sleep (the moon) is faster: it wakes up in a couple of seconds.", "Turn off", () => Shell.Shutdown(false));
                    break;
                case "desktop":
                    GoDesktop();
                    break;
                case "reload":
                    Reload();
                    break;
                case "remote":
                    OpenRemoteSetup();
                    break;
                case "search":
                    OpenSearch(false);
                    break;
                case "update":
                    AskToUpdate();
                    break;
                default:
                    Toast("Unknown action '" + t.Action + "' in couchtv.ini");
                    break;
            }
        }

        /// <summary>The Home key: close whatever app is open and show the home screen.</summary>
        void GoHome()
        {
            bool alreadyHome = IsActive && WindowState != WindowState.Minimized;
            int closed = Shell.ExplorerRunning ? 0 : Shell.CloseAppWindows(_hwnd);
            BringHome();
            if (alreadyHome && closed == 0)
            {
                _row = _apps.Count > 0 ? 0 : 1;
                _col[_row] = 0;
                RefreshFocus(true);
            }
        }

        /// <summary>Shows the home screen in front, closing any dialog that was open on it.</summary>
        void BringHome()
        {
            HideLaunch();
            _confirm.Visibility = Visibility.Collapsed;
            if (SetupOpen) FinishSetup(false);
            if (SearchOpen) CloseSearch(false);
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Activate();
            Shell.ForceForeground(_hwnd);
            Topmost = true;
            Topmost = false;
            Keyboard.Focus(this);
        }

        void GoDesktop()
        {
            if (Shell.ExplorerRunning)
            {
                WindowState = WindowState.Minimized;
                return;
            }
            Toast("Opening the Windows desktop… Press Home to come back.");
            Shell.StartDesktop();
            DateTime started = DateTime.Now;
            var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            wait.Tick += (s, e) =>
            {
                if (!Shell.ExplorerRunning && (DateTime.Now - started).TotalSeconds < 20) return;
                wait.Stop();
                UpdateVolumeKeys();
                WindowState = WindowState.Minimized;
            };
            wait.Start();
        }

        void Reload()
        {
            _cfg = Config.Load();
            _keys = null;            // keys.ini may have been edited too
            _planCache.Clear();
            if (_ir != null)
            {
                _remote = RemoteMap.Load();
                SyncWakeButtons();
            }
            Content = BuildRoot();
            BuildTiles();
            RefreshFocus(false);
            UpdateClock();
            UpdateNetwork();
            if (_hwnd != IntPtr.Zero)
            {
                UnregisterKeys();
                RegisterKeys();
            }
            Toast(_cfg.Warnings.Count > 0 ? _cfg.Warnings[0] : "Settings reloaded");
        }

        string CycleSleepTimer()
        {
            int[] steps = { 30, 60, 90, 120 };
            int left = _sleepAt.HasValue ? (int)Math.Ceiling((_sleepAt.Value - DateTime.Now).TotalMinutes) : 0;
            int next = 0;
            foreach (int step in steps)
            {
                if (step > left + 1) { next = step; break; }
            }
            string message;
            if (next == 0)
            {
                _sleepAt = null;
                message = "Sleep timer off";
            }
            else
            {
                _sleepAt = DateTime.Now.AddMinutes(next);
                _sleepWarned = false;
                message = "The TV will go to sleep in " + next + " minutes. Press again for longer.";
            }
            Toast(message);
            UpdateSleepCaption();
            return message;
        }

        void TickSleepTimer(DateTime now)
        {
            if (!_sleepAt.HasValue) return;
            TimeSpan left = _sleepAt.Value - now;
            if (left <= TimeSpan.Zero)
            {
                _sleepAt = null;
                UpdateSleepCaption();
                Log.Info("Sleep timer");
                Shell.Sleep();
                return;
            }
            if (!_sleepWarned && left.TotalSeconds <= 60)
            {
                _sleepWarned = true;
                Osd.ShowMessage('', "Going to sleep in 1 minute", 8);
            }
            if (now.Second == 0) UpdateSleepCaption();
        }

        void UpdateSleepCaption()
        {
            foreach (TileView v in _sys)
            {
                if (v.Tile.Action != "sleeptimer") continue;
                if (_sleepAt.HasValue)
                {
                    int minutes = Math.Max(1, (int)Math.Ceiling((_sleepAt.Value - DateTime.Now).TotalMinutes));
                    v.Caption.Text = "Sleep in " + minutes + " min";
                    v.Badge.Fill = Theme.Brush(Theme.Good);
                    v.Badge.Visibility = Visibility.Visible;
                }
                else
                {
                    v.Caption.Text = v.Tile.Label;
                    v.Badge.Visibility = Visibility.Collapsed;
                }
            }
        }

        void UpdateBadges() { ShowUpdateBadge(Shell.UpdateWaiting()); }

        void ShowUpdateBadge(bool waiting)
        {
            foreach (TileView v in _sys)
            {
                if (v.Tile.Action != "restart") continue;
                v.Badge.Fill = Theme.Brush(Theme.Warn);
                v.Badge.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
                v.Caption.Text = waiting ? "Restart to update" : v.Tile.Label;
            }
        }

        OsdWindow Osd { get { return _osd ?? (_osd = new OsdWindow()); } }

        void ChangeVolume(float delta, bool toggleMute)
        {
            float level;
            bool muted;
            if (Audio.Change(delta, toggleMute, out level, out muted)) Osd.ShowVolume(level, muted);
        }

        // ================================================================ overlays

        void ShowLaunch(Tile t)
        {
            _launchText.Text = "Opening " + t.Label + "…";
            _launchHint.Text = LaunchHintText;
            _launch.Visibility = Visibility.Visible;
            _launchUntil = DateTime.Now.AddSeconds(25);
            if (!_opt.Preview)
                _spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        void HideLaunch()
        {
            if (_updating || _launch.Visibility != Visibility.Visible) return;
            _launch.Visibility = Visibility.Collapsed;
            _spin.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        void Toast(string message)
        {
            _toastText.Text = message;
            _toastUntil = DateTime.Now.AddSeconds(5);
            _toastShown = true;
            Theme.AnimateTo(_toast, OpacityProperty, 1, !_opt.Preview);
        }

        void Confirm(string title, string text, string yes, Action action)
        {
            _confirmTitle.Text = title;
            _confirmText.Text = text;
            ((TextBlock)_confirmYes.Child).Text = yes;
            ((TextBlock)_confirmNo.Child).Text = "Cancel";
            _confirmAction = action;
            _confirmIndex = 0;
            RefreshConfirm();
            _confirm.Visibility = Visibility.Visible;
        }

        void RefreshConfirm()
        {
            foreach (Border b in new[] { _confirmYes, _confirmNo })
            {
                bool on = (b == _confirmYes) == (_confirmIndex == 0);
                b.Background = Theme.Brush(on ? Colors.White : Theme.Hex("#262D3A"));
                ((TextBlock)b.Child).Foreground = Theme.Brush(on ? Theme.Base : Theme.Ink);
            }
        }

        void ChooseConfirm(int index)
        {
            _confirm.Visibility = Visibility.Collapsed;
            Action action = _confirmAction;
            _confirmAction = null;
            if (index == 0 && action != null) action();
        }

        // ================================================================ clock, network, timers

        void OnTick(object sender, EventArgs e)
        {
            _ticks++;
            DateTime now = DateTime.Now;
            UpdateClock();
            if (_ticks % 5 == 0)
            {
                UpdateNetwork();
                UpdateVolumeKeys();
            }
            if (_ticks % 600 == 3) UpdateBadges();
            if (_launch.Visibility == Visibility.Visible && now > _launchUntil) HideLaunch();
            if (_toastShown && now > _toastUntil)
            {
                _toastShown = false;
                Theme.AnimateTo(_toast, OpacityProperty, 0, true);
            }
            TickSleepTimer(now);
            if (_pointer && IsActive && Mouse.OverrideCursor == null && (now - _lastMouse).TotalSeconds > 4) Mouse.OverrideCursor = Cursors.None;
        }

        void UpdateClock()
        {
            DateTime now = DateTime.Now;
            CultureInfo culture = CultureInfo.CurrentCulture;
            bool h24 = culture.DateTimeFormat.ShortTimePattern.Contains("H");
            SetText(_time, now.ToString(h24 ? "HH:mm" : "h:mm", culture));
            SetText(_ampm, h24 ? "" : now.ToString("tt", culture).ToUpperInvariant());
            SetText(_date, now.ToString("dddd, d MMMM", culture));
            int h = now.Hour;
            SetText(_greeting, h >= 5 && h < 12 ? "Good morning" : h >= 12 && h < 17 ? "Good afternoon" : "Good evening");
        }

        static void SetText(TextBlock block, string text) { if (block.Text != text) block.Text = text; }

        void UpdateNetwork()
        {
            bool linked = false;
            try { linked = NetworkInterface.GetIsNetworkAvailable(); } catch (Exception) { }
            bool? internet = linked ? InternetConnected() : false;
            string text = internet == false ? (linked ? "No internet" : "Offline") : "Online";
            Color dot = internet == false ? (linked ? Theme.Warn : Theme.Bad) : Theme.Good;
            SetText(_net, text);
            _netDot.Fill = Theme.Brush(dot);
        }

        /// <summary>Asks Windows' Network List Manager whether there is a real internet connection.</summary>
        static bool? InternetConnected()
        {
            object manager = null;
            try
            {
                Type type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
                manager = Activator.CreateInstance(type);
                return (bool)type.InvokeMember("IsConnectedToInternet", BindingFlags.GetProperty, null, manager, null);
            }
            catch (Exception) { return null; }
            finally { if (manager != null) Marshal.ReleaseComObject(manager); }
        }

        void OnNetworkChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(UpdateNetwork));
        }

        void OnDisplayChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(FitToScreen));
        }

        void OnPowerChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                CheckForUpdatesSoon(45);
                UpdateClock();
                UpdateNetwork();
            }));
        }

        void FitToScreen()
        {
            WindowState = WindowState.Normal;
            if (_opt.Windowed)
            {
                Width = 960;
                Height = 540;
                Left = SystemParameters.WorkArea.Right - Width - 20;
                Top = SystemParameters.WorkArea.Bottom - Height - 20;
                return;
            }
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
        }

        static BitmapImage LoadBitmap(string path, int decodeWidth)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = decodeWidth;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                Log.Error("Image " + path, ex);
                return null;
            }
        }

        // ================================================================ global keys

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
            RegisterKeys();
        }

        void RegisterKeys()
        {
            int id = 1;
            _winTapHome = false;
            foreach (string name in _cfg.HomeKeys)
            {
                HotkeySpec spec = Hotkeys.Parse(name);
                if (spec == null)
                {
                    Log.Info("HomeKeys: unknown key name '" + name + "'");
                    continue;
                }
                if (spec.IsWinTap)
                {
                    _winTapHome = true;
                    continue;
                }
                if (spec.IsHold)
                {
                    _holdKeys.Add(spec.Vk);
                    continue;
                }
                if (Native.RegisterHotKey(_hwnd, id, spec.Modifiers | Native.MOD_NOREPEAT, spec.Vk)) _homeIds.Add(id);
                else Log.Info("HomeKeys: '" + name + "' is already taken by another program");
                id++;
            }
            if (_winTapHome || _holdKeys.Count > 0)
            {
                _hookProc = KeyboardHook;
                _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _hookProc, Native.GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero) Log.Info("Keyboard hook failed (error " + Marshal.GetLastWin32Error() + ")");
            }
            UpdateVolumeKeys();
        }

        void UnregisterKeys()
        {
            foreach (int id in _homeIds) Native.UnregisterHotKey(_hwnd, id);
            _homeIds.Clear();
            _holdKeys.Clear();
            SetVolumeKeys(false);
            if (_hook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        /// <summary>Explorer handles the volume keys on the desktop; in TV mode there is no Explorer, so CouchTV does.</summary>
        void UpdateVolumeKeys() { SetVolumeKeys(!Shell.ExplorerRunning); }

        void SetVolumeKeys(bool on)
        {
            if (_hwnd == IntPtr.Zero || on == _volumeKeys) return;
            _volumeKeys = on;
            if (on)
            {
                Native.RegisterHotKey(_hwnd, VolumeUpId, 0, Native.VK_VOLUME_UP);
                Native.RegisterHotKey(_hwnd, VolumeDownId, 0, Native.VK_VOLUME_DOWN);
                Native.RegisterHotKey(_hwnd, MuteId, Native.MOD_NOREPEAT, Native.VK_VOLUME_MUTE);
            }
            else
            {
                Native.UnregisterHotKey(_hwnd, VolumeUpId);
                Native.UnregisterHotKey(_hwnd, VolumeDownId);
                Native.UnregisterHotKey(_hwnd, MuteId);
            }
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != Native.WM_HOTKEY) return IntPtr.Zero;
            int id = wParam.ToInt32();
            switch (id)
            {
                case VolumeUpId: ChangeVolume(0.02f, false); break;
                case VolumeDownId: ChangeVolume(-0.02f, false); break;
                case MuteId: ChangeVolume(0, true); break;
                default: if (_homeIds.Contains(id)) GoHome(); break;
            }
            handled = true;
            return IntPtr.Zero;
        }

        /// <summary>
        /// Watches for two Home gestures that RegisterHotKey can't express: a tap of the Windows key on its own
        /// (TV mode only), and holding a key such as Back, for remotes whose only Home/Back button sends Back.
        /// Runs for every key press on the PC, so it only records state and defers the work.
        /// </summary>
        IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int msg = wParam.ToInt32();
                uint vk = (uint)Marshal.ReadInt32(lParam);
                if (Marshal.ReadIntPtr(lParam, 16) == Input.Marker) return Native.CallNextHookEx(_hook, code, wParam, lParam);   // our own remote keys
                bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
                if (_holdKeys.Contains(vk))
                {
                    if (down && _heldVk != vk)
                    {
                        _heldVk = vk;   // key repeats while held are ignored until it is released
                        Dispatcher.BeginInvoke(new Action(StartHoldTimer));
                    }
                    else if (up && _heldVk == vk)
                    {
                        _heldVk = 0;
                        Dispatcher.BeginInvoke(new Action(StopHoldTimer));
                    }
                }
                if (_winTapHome && (vk == Native.VK_LWIN || vk == Native.VK_RWIN))
                {
                    if (down && !_winDown)
                    {
                        _winDown = true;
                        _winCombo = false;
                    }
                    else if (up)
                    {
                        if (_winDown && !_winCombo) Dispatcher.BeginInvoke(new Action(OnWinTap));
                        _winDown = false;
                    }
                }
                else if (down && _winDown)
                {
                    _winCombo = true;
                }
            }
            return Native.CallNextHookEx(_hook, code, wParam, lParam);
        }

        void OnWinTap() { if (!Shell.ExplorerRunning) GoHome(); }

        void StartHoldTimer()
        {
            if (_holdTimer == null)
            {
                _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                _holdTimer.Tick += (s, e) =>
                {
                    _holdTimer.Stop();
                    GoHome();
                };
            }
            _holdTimer.Stop();
            _holdTimer.Start();
        }

        void StopHoldTimer()
        {
            if (_holdTimer != null) _holdTimer.Stop();
        }

        // ================================================================ lifetime

        protected override void OnClosing(CancelEventArgs e)
        {
            // In TV mode there is nothing behind this window, so don't let a stray Alt+F4 leave a black screen.
            if (!_allowClose && !_opt.Windowed && !Shell.ExplorerRunning) e.Cancel = true;
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_timer != null) _timer.Stop();
            UnregisterKeys();
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            SystemEvents.PowerModeChanged -= OnPowerChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
            if (_osd != null) _osd.Close();
            if (_ir != null) _ir.Stop();
            Mouse.OverrideCursor = null;
            base.OnClosed(e);
        }

        // ================================================================ screenshots

        /// <summary>Sets up a state for an offscreen screenshot and hands back the visual tree to render.</summary>
        public FrameworkElement PreparePreview(int row, int col, string state)
        {
            if (Row(row).Count == 0) row = 1 - row;
            _row = row;
            _col[row] = Math.Max(0, Math.Min(col, Row(row).Count - 1));
            RefreshFocus(false);
            if (state.Contains("update")) ShowUpdateBadge(true);
            if (state.Contains("sleep"))
            {
                _sleepAt = DateTime.Now.AddMinutes(30);
                UpdateSleepCaption();
            }
            if (state.Contains("toast")) Toast("Brave isn't installed, so this opened in Edge");
            if (state.Contains("launch") && _apps.Count > 0) ShowLaunch(_apps[0].Tile);
            if (state.Contains("confirm")) RunAction(new Tile { Kind = TileKind.Action, Action = "shutdown" });
            if (state.Contains("appupdate"))
            {
                SetAppUpdate(new AppUpdateInfo { Sha = "9f1c2ab0000000000000000000000000000000000", Message = "Add a sleep timer button to the phone remote", Date = DateTime.Now.AddMinutes(-12), NewCommits = 3 });
                RefreshFocus(false);
            }
            if (state.Contains("updating"))
            {
                _updating = true;
                ShowUpdateProgress("Building the new version");
            }
            if (state.Contains("askupdate"))
            {
                SetAppUpdate(new AppUpdateInfo { Sha = "9f1c2ab0000000000000000000000000000000000", Message = "Add a sleep timer button to the phone remote", Date = DateTime.Now.AddMinutes(-12), NewCommits = 3 });
                AskToUpdate();
            }
            if (state.Contains("setup"))
            {
                OpenRemoteSetup();
                SetupCapture(new IrSignal { Code = "Samsung 0707 0060" });
            }
            if (state.Contains("listening")) OpenSearch(true);
            else if (state.Contains("searchplan") || state.Contains("searchauto") || state.Contains("searchaction"))
            {
                var plan = new SearchPlan { Source = "Gemini", SearchText = "Panchayat" };
                Tile prime = FindTile("Prime Video"), youtube = FindTile("YouTube");
                string words = "panchayat ka season 3 lagao";
                if (state.Contains("searchaction"))
                {
                    words = "aadhe ghante baad TV band kar do";
                    plan.Summary = "Sleep in 30 minutes";
                    plan.Options.Add(SmartSearch.ActionOption("sleep_timer", 30));
                }
                else
                {
                    plan.Summary = "Panchayat · TV show · 2020 · season 3";
                    plan.UsedTmdb = true;
                    if (prime != null) plan.Options.Add(new SearchOption { Kind = SearchKind.AppSearch, Tile = prime, Query = "Panchayat", Caption = "Streams here" });
                    if (state.Contains("searchplan") && youtube != null)
                        plan.Options.Add(new SearchOption { Kind = SearchKind.AppSearch, Tile = youtube, Query = "Panchayat", Caption = "Free with ads" });
                }
                PreviewPlan(words, plan);
            }
            else if (state.Contains("search"))
            {
                OpenSearch(false);
                _query = state.Contains("searchtyping") ? "panch" : "panchayat season 3";
                _targetIndex = state.Contains("searchtyping") ? 0 : 2;
                RefreshTargets(false);
                UpdateSearch();
            }
            if (state.Contains("osd"))
            {
                var panel = new OsdPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 40, 40, 0) };
                panel.SetVolume(0.46f, false);
                _layers.Children.Add(panel);
            }
            var root = (FrameworkElement)Content;
            Content = null;
            return root;
        }
    }
}
