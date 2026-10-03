using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CouchTV
{
    /// <summary>The volume / message bubble. Shared by the OSD window and screenshot previews.</summary>
    internal sealed class OsdPanel : Border
    {
        const double TrackWidth = 300;
        readonly TextBlock _glyph, _text;
        readonly Border _track, _fill;

        public OsdPanel()
        {
            CornerRadius = new CornerRadius(22);
            Background = Theme.Brush(Theme.Alpha(Theme.Panel, 0xF0));
            BorderBrush = Theme.Brush(Color.FromArgb(0x26, 255, 255, 255));
            BorderThickness = new Thickness(1);
            Padding = new Thickness(26, 18, 32, 20);

            _glyph = new TextBlock { FontFamily = Theme.Icons, FontSize = 32, Foreground = Brushes.White, Width = 44, VerticalAlignment = VerticalAlignment.Center };
            _text = Theme.Text("", 22, Theme.Ink, FontWeights.SemiBold);
            _fill = new Border { CornerRadius = new CornerRadius(4), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
            _track = new Border
            {
                Width = TrackWidth, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 12, 0, 0),
                Background = Theme.Brush(Color.FromArgb(0x40, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Left,
                Child = _fill,
            };

            var column = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            column.Children.Add(_text);
            column.Children.Add(_track);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_glyph);
            row.Children.Add(column);
            Child = row;
        }

        public void SetVolume(float level, bool muted)
        {
            // Segoe icons: Mute, Volume0..Volume3
            _glyph.Text = muted ? "" : level < 0.01f ? "" : level < 0.34f ? "" : level < 0.67f ? "" : "";
            _text.Text = muted ? "Muted" : "Volume " + Math.Round(level * 100);
            _track.Visibility = Visibility.Visible;
            _track.MinWidth = TrackWidth;
            _fill.Width = muted ? 0 : TrackWidth * level;
        }

        public void SetMessage(char glyph, string text)
        {
            _glyph.Text = glyph.ToString();
            _text.Text = text;
            _track.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// A click-through, never-focused, always-on-top window, so the volume bar and sleep warning show on top of
    /// full-screen video without interrupting it.
    /// </summary>
    internal sealed class OsdWindow : Window
    {
        readonly OsdPanel _panel = new OsdPanel();
        readonly DispatcherTimer _hide = new DispatcherTimer();
        readonly double _scale = Math.Max(0.6, SystemParameters.PrimaryScreenHeight / 1080.0);

        public OsdWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Content = new Border { Child = _panel, LayoutTransform = new ScaleTransform(_scale, _scale) };
            _hide.Tick += (s, e) => { _hide.Stop(); Hide(); };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, exStyle | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW);
        }

        public void ShowVolume(float level, bool muted)
        {
            _panel.SetVolume(level, muted);
            Pop(1.6);
        }

        public void ShowMessage(char glyph, string text, double seconds)
        {
            _panel.SetMessage(glyph, text);
            Pop(seconds);
        }

        void Pop(double seconds)
        {
            var content = (FrameworkElement)Content;
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double margin = 40 * _scale;
            Left = SystemParameters.PrimaryScreenWidth - content.DesiredSize.Width - margin;
            Top = margin;
            if (!IsVisible) Show();
            Topmost = false;
            Topmost = true;   // re-assert so it stays above a full-screen browser
            _hide.Stop();
            _hide.Interval = TimeSpan.FromSeconds(seconds);
            _hide.Start();
        }
    }
}
