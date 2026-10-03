using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CouchTV
{
    /// <summary>Colours, fonts and small UI helpers shared by the home screen and the on-screen display.</summary>
    internal static class Theme
    {
        public static readonly Color Ink = Hex("#F4F6FA");
        public static readonly Color Muted = Hex("#A3ACBC");
        public static readonly Color Faint = Hex("#6B7486");
        public static readonly Color Base = Hex("#0B0E14");
        public static readonly Color Chip = Hex("#1C222D");
        public static readonly Color Panel = Hex("#161B24");
        public static readonly Color Good = Hex("#3DDC84");
        public static readonly Color Warn = Hex("#FFB547");
        public static readonly Color Bad = Hex("#FF5C61");

        public static readonly FontFamily Display = new FontFamily("Segoe UI Variable Display, Segoe UI");
        public static readonly FontFamily Body = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        static readonly IEasingFunction Ease = CreateEase();

        public static Color Hex(string value) { return (Color)ColorConverter.ConvertFromString(value); }

        public static Color Alpha(Color c, byte alpha) { return Color.FromArgb(alpha, c.R, c.G, c.B); }

        public static SolidColorBrush Brush(Color c)
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }

        public static TextBlock Text(string text, double size, Color color, FontWeight weight, FontFamily family = null)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                Foreground = Brush(color),
                FontWeight = weight,
                FontFamily = family ?? Body,
            };
        }

        /// <summary>Eases a double property to a value, or jumps straight there when animate is false.</summary>
        public static void AnimateTo(IAnimatable target, DependencyProperty property, double to, bool animate)
        {
            if (!animate)
            {
                target.BeginAnimation(property, null);
                ((DependencyObject)target).SetValue(property, to);
                return;
            }
            var animation = new DoubleAnimation(to, new Duration(System.TimeSpan.FromMilliseconds(170))) { EasingFunction = Ease };
            target.BeginAnimation(property, animation);
        }

        static IEasingFunction CreateEase()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ease.Freeze();
            return ease;
        }
    }
}
