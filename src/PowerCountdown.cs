using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace CouchTV
{
    /// <summary>
    /// "Turning off the TV": big, in the middle of the screen and over whatever's playing, while Power on the phone
    /// counts down (POWERSOON), so whoever's watching sees it coming. The screen dims, a glowing ring runs down round
    /// the seconds and each second pops in; cancelled on the phone, the ring fills back up green with a tick and it says
    /// the TV stays on. Click-through and never focused, like the OSD, so the video underneath keeps playing.
    /// </summary>
    internal sealed class PowerCountdownWindow : Window
    {
        const double Ring = 300, Stroke = 18;
        static readonly Color Red = Theme.Bad, Green = Theme.Good;
        // The ring's length in stroke widths, which is how WPF measures dashes.
        static readonly double Length = Math.PI * (Ring - Stroke) / Stroke;

        readonly double _scale = Math.Max(0.6, SystemParameters.PrimaryScreenHeight / 1080.0);
        readonly Rectangle _dim;
        readonly Border _card;
        readonly ScaleTransform _cardScale = new ScaleTransform(1, 1), _numberScale = new ScaleTransform(1, 1);
        readonly Ellipse _arc;
        readonly DropShadowEffect _glow;
        readonly TextBlock _number, _title, _note;
        readonly DispatcherTimer _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        readonly DispatcherTimer _hide = new DispatcherTimer();
        DateTime _endsAt;
        int _shown;

        public PowerCountdownWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            ResizeMode = ResizeMode.NoResize;
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;

            _dim = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), Opacity = 0 };

            var track = new Ellipse { Width = Ring, Height = Ring, Stroke = new SolidColorBrush(Color.FromArgb(0x2E, 255, 255, 255)), StrokeThickness = Stroke };
            _glow = new DropShadowEffect { Color = Red, BlurRadius = 34, ShadowDepth = 0, Opacity = 0.9 };
            _arc = new Ellipse
            {
                Width = Ring, Height = Ring, Stroke = new SolidColorBrush(Red), StrokeThickness = Stroke,
                StrokeDashArray = new DoubleCollection { Length, Length }, StrokeDashCap = PenLineCap.Round,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(-90),   // from 12 o'clock
                Effect = _glow,
            };
            _number = Theme.Text("", 140, Theme.Ink, FontWeights.Light, Theme.Display);
            _number.HorizontalAlignment = HorizontalAlignment.Center;
            _number.VerticalAlignment = VerticalAlignment.Center;
            _number.RenderTransformOrigin = new Point(0.5, 0.5);
            _number.RenderTransform = _numberScale;
            var dial = new Grid { Width = Ring, Height = Ring, HorizontalAlignment = HorizontalAlignment.Center };
            dial.Children.Add(track);
            dial.Children.Add(_arc);
            dial.Children.Add(_number);

            _title = Theme.Text("", 44, Theme.Ink, FontWeights.SemiBold);
            _title.HorizontalAlignment = HorizontalAlignment.Center;
            _title.Margin = new Thickness(0, 40, 0, 0);
            _note = Theme.Text("", 26, Theme.Muted, FontWeights.Normal);
            _note.HorizontalAlignment = HorizontalAlignment.Center;
            _note.Margin = new Thickness(0, 10, 0, 0);
            var column = new StackPanel();
            column.Children.Add(dial);
            column.Children.Add(_title);
            column.Children.Add(_note);

            _card = new Border
            {
                CornerRadius = new CornerRadius(40), Padding = new Thickness(96, 72, 96, 60), Child = column, MinWidth = 640,
                Background = Theme.Brush(Theme.Alpha(Theme.Panel, 0xF4)), BorderBrush = Theme.Brush(Color.FromArgb(0x26, 255, 255, 255)),
                BorderThickness = new Thickness(1.5), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _cardScale, Opacity = 0,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 80, ShadowDepth = 18, Direction = 270, Opacity = 0.7 },
            };
            var middle = new Border
            {
                Child = _card, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                LayoutTransform = new ScaleTransform(_scale, _scale),
            };
            var root = new Grid();
            root.Children.Add(_dim);
            root.Children.Add(middle);
            Content = root;

            _tick.Tick += (s, e) => Tick();
            _hide.Tick += (s, e) => FadeOut();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, exStyle | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW);
        }

        /// <summary>Counts down from seconds: the TV turns off at the end (the phone sends Power then).</summary>
        public void Start(int seconds)
        {
            _hide.Stop();
            _endsAt = DateTime.Now.AddSeconds(seconds);
            _shown = -1;
            Paint(Red);
            _title.Text = "Turning off the TV";
            _note.Text = "Cancel on the phone";
            _arc.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, Length, TimeSpan.FromSeconds(seconds)));
            if (!IsVisible) Show();
            Topmost = false;
            Topmost = true;   // re-asserted, so it's above a full-screen browser
            _dim.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
            _card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
            Bounce(_cardScale, 0.82, 520, 0.45);
            _tick.Start();
            Tick();
        }

        /// <summary>Cancelled on the phone: green, a tick, and gone.</summary>
        public void Cancel()
        {
            if (!IsVisible) return;
            _tick.Stop();
            Paint(Green);
            _arc.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            ShowSymbol('');   // a tick
            _title.Text = "Cancelled";
            _note.Text = "The TV stays on";
            HideIn(1.8);
        }

        void Tick()
        {
            int left = (int)Math.Ceiling((_endsAt - DateTime.Now).TotalSeconds);
            if (left <= 0)
            {
                _tick.Stop();
                ShowSymbol('');   // power
                _title.Text = "Turning off…";
                _note.Text = "";
                HideIn(6);   // gone with the picture, normally; this is in case the TV stays on after all
                return;
            }
            if (left == _shown) return;
            _shown = left;
            _number.FontFamily = Theme.Display;
            _number.FontSize = 140;
            _number.Text = left.ToString();
            Bounce(_numberScale, 1.45, 420, 0.7);
            _number.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(260)));
        }

        void ShowSymbol(char glyph)
        {
            _number.FontFamily = Theme.Icons;
            _number.FontSize = 112;
            _number.Text = glyph.ToString();
            Bounce(_numberScale, 0.5, 480, 0.6);
        }

        void Paint(Color color)
        {
            _arc.Stroke = new SolidColorBrush(color);
            _glow.Color = color;
        }

        static void Bounce(ScaleTransform scale, double from, int ms, double amplitude)
        {
            var animation = new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = amplitude } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }

        void HideIn(double seconds)
        {
            _hide.Stop();
            _hide.Interval = TimeSpan.FromSeconds(seconds);
            _hide.Start();
        }

        void FadeOut()
        {
            _hide.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
            fade.Completed += (s, e) => { if (_card.Opacity == 0) Hide(); };
            _dim.BeginAnimation(OpacityProperty, fade);
            _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
        }
    }
}
