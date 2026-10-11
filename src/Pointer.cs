using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace CouchTV
{
    /// <summary>
    /// The mouse pointer on the TV. CouchTV draws its own, big and easy to see from the couch (a dark outline and a
    /// soft shadow keep it visible on any picture), in place of Windows' arrow, link hand and text cursor, and puts
    /// Windows' own back when it closes. Over a link it turns a highlight colour, so you can see what's clickable.
    ///   Styles: arrow, dot, ring. Sizes: 32 to 128 pixels (Windows' own is 32). Colours: white, yellow, blue, green, pink.
    /// Set in couchtv.ini (PointerStyle, PointerSize, PointerColor) or from the phone remote's trackpad page.
    /// </summary>
    internal static class Pointer
    {
        public const int SmallestSize = 32, LargestSize = 128, DefaultSize = 64;
        public static readonly string[] Styles = { "arrow", "dot", "ring" };
        public static readonly string[] ColorNames = { "white", "yellow", "blue", "green", "pink" };
        static readonly string[] ColorValues = { "#FFFFFF", "#FFD43B", "#4D8BFF", "#3DDC84", "#FF5FA2" };
        const string Outline = "#0B0E14", LinkBlue = "#3D7BFF";
        const string Version = "1";   // bump when the drawing changes, so cached cursor files are redrawn

        internal enum Kind { Normal, Link, Text }

        static string _style = "arrow", _color = "white";
        static int _size = DefaultSize;
        static bool _applied;

        /// <summary>The current pointer, as the phone remote is told it: "arrow 64 white".</summary>
        public static string Describe() { return _style + " " + _size + " " + _color; }

        /// <summary>Unknown styles and colours become the defaults; the size is kept within 32 to 128.</summary>
        public static void Normalise(ref string style, ref int size, ref string color)
        {
            style = Array.IndexOf(Styles, (style ?? "").ToLowerInvariant()) >= 0 ? style.ToLowerInvariant() : "arrow";
            color = Array.IndexOf(ColorNames, (color ?? "").ToLowerInvariant()) >= 0 ? color.ToLowerInvariant() : "white";
            size = Math.Max(SmallestSize, Math.Min(LargestSize, size));
        }

        /// <summary>Draws the pointer and makes it Windows' arrow, link hand and text cursor (until Restore). UI thread.</summary>
        public static void Apply(string style, int size, string color)
        {
            Normalise(ref style, ref size, ref color);
            _style = style;
            _size = size;
            _color = color;
            try
            {
                string dir = Path.Combine(Config.DataDir, "pointers");
                Directory.CreateDirectory(dir);
                Replace(dir, Kind.Normal, OCR_NORMAL);
                Replace(dir, Kind.Normal, OCR_APPSTARTING);   // "working in the background": the same, so it doesn't shrink
                Replace(dir, Kind.Link, OCR_HAND);
                Replace(dir, Kind.Text, OCR_IBEAM);
                _applied = true;
                Log.Info("Pointer: " + Describe());
            }
            catch (Exception ex) { Log.Error("Pointer", ex); }
        }

        /// <summary>Windows' own pointers again, as set in its Mouse settings.</summary>
        public static void Restore()
        {
            if (!_applied) return;
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
            _applied = false;
        }

        static void Replace(string dir, Kind kind, uint id)
        {
            string path = Path.Combine(dir, "v" + Version + "-" + _style + "-" + _size + "-" + _color + "-" + kind.ToString().ToLowerInvariant() + ".cur");
            if (!File.Exists(path))
            {
                int hotX, hotY;
                byte[] pixels = Render(_style, _size, _color, kind, out hotX, out hotY);
                File.WriteAllBytes(path, CursorFile(pixels, _size, hotX, hotY));
            }
            // Loaded at its own size: Windows would otherwise shrink it to the standard 32 pixels.
            IntPtr cursor = LoadImage(IntPtr.Zero, path, IMAGE_CURSOR, _size, _size, LR_LOADFROMFILE);
            if (cursor == IntPtr.Zero) throw new Exception("couldn't load " + path + " (error " + Marshal.GetLastWin32Error() + ")");
            if (!SetSystemCursor(cursor, id))   // takes the cursor over, and frees it, when it works
            {
                DestroyCursor(cursor);
                throw new Exception("SetSystemCursor failed (error " + Marshal.GetLastWin32Error() + ")");
            }
        }

        // ---------------------------------------------------------------- drawing

        /// <summary>The pointer as size x size straight (not premultiplied) BGRA pixels, top row first, and its hotspot.</summary>
        internal static byte[] Render(string style, int size, string color, Kind kind, out int hotX, out int hotY)
        {
            Normalise(ref style, ref size, ref color);
            Color fill = Hex(ColorValues[Array.IndexOf(ColorNames, color)]);
            Color link = color == "blue" ? Hex(ColorValues[1]) : Hex(LinkBlue);   // blue on a blue pointer wouldn't show
            Point hot;

            var main = new DrawingVisual();
            using (DrawingContext dc = main.RenderOpen()) hot = Draw(dc, style, kind, size, fill, link, Hex(Outline));
            var shadow = new DrawingVisual();
            using (DrawingContext dc = shadow.RenderOpen()) Draw(dc, style, kind, size, Colors.Black, Colors.Black, Colors.Black);
            shadow.Opacity = 0.55;
            shadow.Offset = new Vector(size / 40.0, size / 26.0);
            shadow.Effect = new BlurEffect { Radius = size / 16.0 };
            var root = new ContainerVisual();
            root.Children.Add(shadow);
            root.Children.Add(main);

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var pixels = new byte[size * size * 4];
            bitmap.CopyPixels(pixels, size * 4, 0);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int a = pixels[i + 3];
                if (a == 0 || a == 255) continue;
                for (int c = 0; c < 3; c++) pixels[i + c] = (byte)Math.Min(255, pixels[i + c] * 255 / a);
            }
            hotX = Math.Max(0, Math.Min(size - 1, (int)Math.Round(hot.X)));
            hotY = Math.Max(0, Math.Min(size - 1, (int)Math.Round(hot.Y)));
            return pixels;
        }

        /// <summary>Draws one pointer at size s; returns its hotspot (the pixel that points).</summary>
        static Point Draw(DrawingContext dc, string style, Kind kind, double s, Color fill, Color link, Color outline)
        {
            double c = s / 2;
            if (kind == Kind.Text)
            {
                // An I-beam, the same for every style.
                var beam = new StreamGeometry();
                using (StreamGeometryContext g = beam.Open())
                {
                    double top = s * 0.14, bottom = s * 0.86, serif = s * 0.12;
                    Line(g, c, top, c, bottom);
                    Line(g, c - serif, top, c + serif, top);
                    Line(g, c - serif, bottom, c + serif, bottom);
                }
                Stroke(dc, beam, outline, s * 0.115);
                Stroke(dc, beam, fill, s * 0.055);
                return new Point(c, c);
            }

            Color colour = kind == Kind.Link ? link : fill;
            switch (style)
            {
                case "dot":
                    if (kind == Kind.Link)
                    {
                        Ring(dc, c, s * 0.36, s * 0.07, colour, outline, s * 0.045);
                        Disc(dc, c, s * 0.15, colour, outline, s * 0.05);
                    }
                    else Disc(dc, c, s * 0.2, colour, outline, s * 0.055);
                    return new Point(c, c);

                case "ring":
                    if (kind == Kind.Link) dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(90, colour.R, colour.G, colour.B)), null, new Point(c, c), s * 0.3, s * 0.3);
                    Ring(dc, c, s * 0.3, s * 0.075, colour, outline, s * 0.045);
                    Disc(dc, c, s * 0.05, colour, outline, s * 0.03);
                    return new Point(c, c);

                default:   // arrow: the classic shape, drawn on a 32-pixel grid and scaled up
                    double k = s / 32;
                    var arrow = new StreamGeometry();
                    using (StreamGeometryContext g = arrow.Open())
                    {
                        g.BeginFigure(new Point(4 * k, 3 * k), true, true);
                        g.PolyLineTo(new[]
                        {
                            new Point(4 * k, 25.5 * k), new Point(9.3 * k, 20.6 * k), new Point(13 * k, 29.2 * k),
                            new Point(17 * k, 27.5 * k), new Point(13.4 * k, 19.2 * k), new Point(20.5 * k, 19.2 * k),
                        }, true, true);
                    }
                    var pen = new Pen(new SolidColorBrush(outline), 1.8 * k) { LineJoin = PenLineJoin.Round };
                    dc.DrawGeometry(new SolidColorBrush(colour), pen, arrow);
                    return new Point(4 * k, 3 * k);
            }
        }

        static void Line(StreamGeometryContext g, double x1, double y1, double x2, double y2)
        {
            g.BeginFigure(new Point(x1, y1), false, false);
            g.LineTo(new Point(x2, y2), true, false);
        }

        static void Stroke(DrawingContext dc, Geometry geometry, Color color, double width)
        {
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
        }

        static void Disc(DrawingContext dc, double c, double radius, Color color, Color outline, double outlineWidth)
        {
            dc.DrawEllipse(new SolidColorBrush(color), new Pen(new SolidColorBrush(outline), outlineWidth), new Point(c, c), radius, radius);
        }

        static void Ring(DrawingContext dc, double c, double radius, double width, Color color, Color outline, double outlineWidth)
        {
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(outline), width + 2 * outlineWidth), new Point(c, c), radius, radius);
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(color), width), new Point(c, c), radius, radius);
        }

        static Color Hex(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }

        /// <summary>A .cur file holding one 32-bit image with transparency (the format Windows' own cursors use).</summary>
        internal static byte[] CursorFile(byte[] pixels, int size, int hotX, int hotY)
        {
            int maskStride = (size + 31) / 32 * 4;
            int imageBytes = 40 + size * size * 4 + maskStride * size;
            var stream = new MemoryStream();
            var w = new BinaryWriter(stream);
            w.Write((short)0); w.Write((short)2); w.Write((short)1);      // a cursor file, one image
            w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)hotX); w.Write((short)hotY);
            w.Write(imageBytes); w.Write(6 + 16);
            w.Write(40); w.Write(size); w.Write(size * 2);                 // the bitmap: twice as tall, for its mask
            w.Write((short)1); w.Write((short)32); w.Write(0);
            w.Write(size * size * 4 + maskStride * size);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = size - 1; y >= 0; y--) w.Write(pixels, y * size * 4, size * 4);   // bottom row first
            w.Write(new byte[maskStride * size]);                          // the mask: unused, transparency does it
            w.Flush();
            return stream.ToArray();
        }

        // ---------------------------------------------------------------- Windows

        const uint OCR_NORMAL = 32512, OCR_IBEAM = 32513, OCR_HAND = 32649, OCR_APPSTARTING = 32650;
        const uint IMAGE_CURSOR = 2, LR_LOADFROMFILE = 0x10, SPI_SETCURSORS = 0x57;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint flags);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetSystemCursor(IntPtr cursor, uint id);
        [DllImport("user32.dll")] static extern bool DestroyCursor(IntPtr cursor);
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);

        // For the self-test: how big Windows' arrow is right now.
        internal static int CurrentArrowSize()
        {
            IntPtr arrow = LoadCursor(IntPtr.Zero, (IntPtr)OCR_NORMAL);
            ICONINFO info;
            if (arrow == IntPtr.Zero || !GetIconInfo(arrow, out info)) return 0;
            try
            {
                BITMAP bm;
                IntPtr bitmap = info.hbmColor != IntPtr.Zero ? info.hbmColor : info.hbmMask;
                if (GetObject(bitmap, Marshal.SizeOf(typeof(BITMAP)), out bm) == 0) return 0;
                return bm.bmWidth;
            }
            finally
            {
                if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
                if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

        [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr obj, int size, out BITMAP bitmap);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    }
}
