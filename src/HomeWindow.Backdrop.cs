using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace CouchTV
{
    /// <summary>A home screen background: a dark base with two glows of colour (the phone remote has the same, by name).</summary>
    internal sealed class Look
    {
        public string Key, Name;
        public Color Base, Glow, Glow2;
    }

    // The home screen's background (Background in couchtv.ini, also in Settings and on the phone's Backgrounds page):
    // one of the looks, or a photo (sent from the phone, kept as wallpaper.jpg, or any picture set as Wallpaper).
    internal sealed partial class HomeWindow
    {
        public const string PhotoLook = "photo";

        internal static readonly Look[] Looks =
        {
            new Look { Key = "midnight", Name = "Midnight", Base = Theme.Hex("#0B0E14"), Glow = Theme.Hex("#B31E3A72"), Glow2 = Theme.Hex("#80472066") },
            new Look { Key = "aurora", Name = "Aurora", Base = Theme.Hex("#060C14"), Glow = Theme.Hex("#A00FA896"), Glow2 = Theme.Hex("#996A3CE0") },
            new Look { Key = "sunset", Name = "Sunset", Base = Theme.Hex("#120A0E"), Glow = Theme.Hex("#B3D95B26"), Glow2 = Theme.Hex("#8CB0306E") },
            new Look { Key = "ocean", Name = "Ocean", Base = Theme.Hex("#05101A"), Glow = Theme.Hex("#B30E62A8"), Glow2 = Theme.Hex("#8C0FA3C2") },
            new Look { Key = "forest", Name = "Forest", Base = Theme.Hex("#07110C"), Glow = Theme.Hex("#A01E7A48"), Glow2 = Theme.Hex("#8C5E7A1E") },
            new Look { Key = "rose", Name = "Rose", Base = Theme.Hex("#13090F"), Glow = Theme.Hex("#B3C2366E"), Glow2 = Theme.Hex("#8C6E3A9A") },
            new Look { Key = "graphite", Name = "Graphite", Base = Theme.Hex("#111317"), Glow = Theme.Hex("#30FFFFFF"), Glow2 = Theme.Hex("#18FFFFFF") },
        };

        /// <summary>Where a photo sent from the phone is kept.</summary>
        static string PhotoPath { get { return System.IO.Path.Combine(Config.DataDir, "wallpaper.jpg"); } }

        Grid _backdrop;

        bool HasPhoto { get { return !string.IsNullOrEmpty(_cfg.Wallpaper) && File.Exists(_cfg.Wallpaper); } }

        /// <summary>The background, as couchtv.ini says: the look's base and glows, or the photo under a veil.</summary>
        void FillBackdrop()
        {
            if (_backdrop == null) return;
            _backdrop.Children.Clear();
            BitmapSource photo = _cfg.Background == PhotoLook && HasPhoto ? LoadPhoto(_cfg.Wallpaper) : null;
            if (photo != null)
            {
                _backdrop.Children.Add(new Rectangle { Fill = Theme.Brush(Theme.Base) });
                _backdrop.Children.Add(new Image { Source = photo, Stretch = Stretch.UniformToFill });
                // Darker at the top left (the clock) and towards the bottom (the rows), so the words stay easy to read.
                _backdrop.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Theme.Alpha(Theme.Base, 0xD8), Theme.Alpha(Theme.Base, 0x88), 90) });
                _backdrop.Children.Add(new Rectangle
                {
                    Fill = new LinearGradientBrush(Theme.Alpha(Theme.Base, 0x70), Theme.Alpha(Theme.Base, 0x00), new Point(0, 0), new Point(0.7, 0.6)),
                });
                return;
            }
            Look look = Array.Find(Looks, l => l.Key == _cfg.Background) ?? Looks[0];
            _backdrop.Children.Add(new Rectangle { Fill = Theme.Brush(look.Base) });
            _backdrop.Children.Add(Glow(new Point(0.06, -0.05), 0.85, 1.05, look.Glow));
            _backdrop.Children.Add(Glow(new Point(1.0, 1.08), 0.7, 0.85, look.Glow2));
        }

        /// <summary>A picture from a file, read whole (so a new one under the same name shows, and the file isn't held).</summary>
        static BitmapSource LoadPhoto(string path)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = new MemoryStream(File.ReadAllBytes(path));
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 1920;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                Log.Error("Background " + path, ex);
                return null;
            }
        }

        /// <summary>The Background setting: the looks, and the photo once there is one.</summary>
        TvSetting BackgroundSetting()
        {
            var setting = new TvSetting
            {
                Key = "background", Label = "Background", Note = "Behind the home screen. A photo comes from the phone remote's Backgrounds page",
                Get = () => _cfg.Background == PhotoLook && !HasPhoto ? Looks[0].Key : _cfg.Background,
                Set = v =>
                {
                    _cfg.Background = v;
                    FillBackdrop();
                    SaveSetting("Background", v);
                },
            };
            BackgroundChoices(setting);
            return setting;
        }

        void BackgroundChoices(TvSetting setting)
        {
            int count = Looks.Length + (HasPhoto ? 1 : 0);
            setting.Values = new string[count];
            setting.Names = new string[count];
            for (int i = 0; i < Looks.Length; i++)
            {
                setting.Values[i] = Looks[i].Key;
                setting.Names[i] = Looks[i].Name;
            }
            if (HasPhoto)
            {
                setting.Values[count - 1] = PhotoLook;
                setting.Names[count - 1] = "Your photo";
            }
        }

        /// <summary>A photo from a phone (a JPEG it made the screen's shape): kept, and the background from now on.</summary>
        void PhotoFromPhone(byte[] jpeg)
        {
            try
            {
                using (var stream = new MemoryStream(jpeg))
                {
                    BitmapFrame frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                    if (frame.PixelWidth < 64 || frame.PixelHeight < 64) throw new Exception("too small (" + frame.PixelWidth + "x" + frame.PixelHeight + ")");
                }
                Directory.CreateDirectory(Config.DataDir);
                File.WriteAllBytes(PhotoPath, jpeg);
            }
            catch (Exception ex)
            {
                Log.Info("A photo from a phone for the background: " + ex.Message);
                Osd.ShowMessage('', "That photo couldn't be used", 3);
                return;
            }
            Log.Info("A photo from a phone is the background now (" + jpeg.Length / 1024 + " KB)");
            _cfg.Wallpaper = PhotoPath;
            _cfg.Background = PhotoLook;
            TvSetting setting = TvSettings().Find(s => s.Key == "background");
            if (setting != null) BackgroundChoices(setting);
            FillBackdrop();
            SaveSetting("Wallpaper", PhotoPath);
            SaveSetting("Background", PhotoLook);
            if (IsActive && !Modal) Toast("The photo from your phone is the background now");
        }
    }
}
