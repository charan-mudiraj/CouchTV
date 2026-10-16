using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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
    /// On the TV it's only shown while it's in use: while a phone has its trackpad open (and not a moment after it
    /// closes), and for a few seconds after anything else moves it (the trackpad by infrared, an air mouse, a real
    /// mouse); otherwise it's hidden everywhere, so it doesn't sit over a film or the home screen's tiles. Hidden means
    /// a blank pointer in place of all of Windows' pointers: every pixel see-through, and its mask saying so too (with
    /// a mask of zeros Windows paints an all-see-through pointer as a black square).
    /// With no mouse plugged in, Windows draws no pointer at all, whatever moves it: CouchTV turns on Mouse Keys then
    /// (Windows' setting for moving it with the number pad), which counts as a mouse, until it closes.
    /// </summary>
    internal static class Pointer
    {
        public const int SmallestSize = 32, LargestSize = 128, DefaultSize = 64;
        public static readonly string[] Styles = { "arrow", "dot", "ring" };
        public static readonly string[] ColorNames = { "white", "yellow", "blue", "green", "pink" };
        static readonly string[] ColorValues = { "#FFFFFF", "#FFD43B", "#4D8BFF", "#3DDC84", "#FF5FA2" };
        const string Outline = "#0B0E14", LinkBlue = "#3D7BFF";
        const string Version = "2";   // bump when the drawing changes, so cached cursor files are redrawn
        public const int ShowForMs = 5000;   // moved by something other than the phone's trackpad page: shown this long

        internal enum Kind { Normal, Link, Text }

        static string _style = "arrow", _color = "white";
        static int _size = DefaultSize;
        static volatile bool _applied, _hidden, _trackpad;
        static string _dir;
        static int _lastUse = Environment.TickCount, _updateQueued, _lastFlags = CURSOR_SHOWING;
        static readonly object Lock = new object();
        static bool _mouseKeysOurs;           // CouchTV turned Mouse Keys on (and turns it back off when it closes)
        static MOUSEKEYS _mouseKeysBefore;

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
                _dir = Path.Combine(Config.DataDir, "pointers");
                Directory.CreateDirectory(_dir);
                foreach (Kind kind in new[] { Kind.Normal, Kind.Link, Kind.Text }) Prepare(kind);   // drawn here: WPF needs the UI thread
                if (!File.Exists(BlankPath)) File.WriteAllBytes(BlankPath, CursorFile(new byte[32 * 32 * 4], 32, 0, 0));
                lock (Lock)
                {
                    if (_hidden) Blank();
                    else
                    {
                        Show();
                        MakeVisible();
                    }
                    _applied = true;
                }
                Log.Info("Pointer: " + Describe());
            }
            catch (Exception ex) { Log.Error("Pointer", ex); }
        }

        /// <summary>Windows' own pointers again, as set in its Mouse settings.</summary>
        public static void Restore()
        {
            lock (Lock)
            {
                if (_mouseKeysOurs) PutBackMouseKeys();
                if (!_applied) return;
                SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
                _applied = false;
                _hidden = false;
            }
        }

        static string PathFor(Kind kind)
        {
            return Path.Combine(_dir, "v" + Version + "-" + _style + "-" + _size + "-" + _color + "-" + kind.ToString().ToLowerInvariant() + ".cur");
        }

        static string BlankPath { get { return Path.Combine(_dir, "v" + Version + "-blank.cur"); } }

        static void Prepare(Kind kind)
        {
            string path = PathFor(kind);
            if (File.Exists(path)) return;
            int hotX, hotY;
            byte[] pixels = Render(_style, _size, _color, kind, out hotX, out hotY);
            File.WriteAllBytes(path, CursorFile(pixels, _size, hotX, hotY));
        }

        /// <summary>CouchTV's pointer as Windows' arrow, link hand and text cursor, and Windows' own for the rest. Under Lock.</summary>
        static void Show()
        {
            if (_hidden) SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);   // the resize arrows, the hourglass... back
            Replace(PathFor(Kind.Normal), _size, OCR_NORMAL);
            Replace(PathFor(Kind.Normal), _size, OCR_APPSTARTING);   // "working in the background": the same, so it doesn't shrink
            Replace(PathFor(Kind.Link), _size, OCR_HAND);
            Replace(PathFor(Kind.Text), _size, OCR_IBEAM);
            _hidden = false;
        }

        /// <summary>No pointer at all, whatever an app asks for. Under Lock.</summary>
        static void Blank()
        {
            foreach (uint id in AllPointers) Replace(BlankPath, 32, id);
            _hidden = true;
        }

        static void Replace(string path, int size, uint id)
        {
            // Loaded at its own size: Windows would otherwise shrink it to the standard 32 pixels.
            IntPtr cursor = LoadImage(IntPtr.Zero, path, IMAGE_CURSOR, size, size, LR_LOADFROMFILE);
            if (cursor == IntPtr.Zero) throw new Exception("couldn't load " + path + " (error " + Marshal.GetLastWin32Error() + ")");
            if (!SetSystemCursor(cursor, id))   // takes the cursor over, and frees it, when it works
            {
                DestroyCursor(cursor);
                throw new Exception("SetSystemCursor failed (error " + Marshal.GetLastWin32Error() + ")");
            }
        }

        // ---------------------------------------------------------------- shown only while in use

        /// <summary>For the self-test: hidden now, and as if the last move was long ago.</summary>
        internal static bool Hidden { get { return _hidden; } }
        internal static void ForgetUse() { _lastUse = unchecked(Environment.TickCount - ShowForMs - 1); }

        /// <summary>A phone opened (true) or closed (false) its trackpad: shown while it's open, hidden the moment it closes. UI thread.</summary>
        public static void Trackpad(bool open)
        {
            _trackpad = open;
            if (!open) ForgetUse();
            Update();
        }

        /// <summary>Something other than the phone's trackpad page moved or clicked it. Any thread, and quick (WM_INPUT calls it).</summary>
        public static void Used()
        {
            _lastUse = Environment.TickCount;
            if (_applied && _hidden && Interlocked.Exchange(ref _updateQueued, 1) == 0)
            {
                ThreadPool.QueueUserWorkItem(state =>
                {
                    _updateQueued = 0;
                    Update();
                });
            }
        }

        /// <summary>Shows or hides the pointer as it's wanted now; the home screen calls it every second. Any thread.</summary>
        public static void Update()
        {
            if (!_applied) return;
            bool want = _trackpad || unchecked(Environment.TickCount - _lastUse) < ShowForMs;
            lock (Lock)
            {
                if (!_applied || want == !_hidden) return;
                try
                {
                    if (!want)
                    {
                        Blank();
                        return;
                    }
                    Show();
                    MakeVisible();
                }
                catch (Exception ex) { Log.Error("Pointer", ex); }
            }
        }

        // ---------------------------------------------------------------- Windows drawing it at all

        /// <summary>
        /// Makes sure Windows draws the pointer. After a touch (the screen mirror's) it stops until the mouse moves: a
        /// pixel there and back. With no mouse plugged in (a TV PC run from the phone) it draws none at all, whatever
        /// moves it: Mouse Keys counts as a mouse, so it's turned on. Under Lock.
        /// </summary>
        static void MakeVisible()
        {
            int flags = CursorFlags();
            if (flags >= 0 && (flags & CURSOR_SUPPRESSED) != 0)
            {
                Input.MoveBy(1, 0);
                Input.MoveBy(-1, 0);
                flags = CursorFlags();
            }
            if (flags == 0 && !_mouseKeysOurs && TurnOnMouseKeys())
            {
                Input.MoveBy(1, 0);
                Input.MoveBy(-1, 0);
                flags = CursorFlags();
                Log.Info("Pointer: Windows drew none (no mouse plugged in), so Mouse Keys is on: " + (flags == CURSOR_SHOWING ? "drawn now" : "still not drawn"));
            }
            if (flags >= 0 && flags != _lastFlags && flags != CURSOR_SHOWING)
                Log.Info("Pointer: Windows isn't drawing it (" + (flags == 0 ? "hidden" : "suppressed after a touch") + ")");
            if (flags >= 0) _lastFlags = flags;
        }

        /// <summary>Windows' CURSOR_* flags for the pointer now (0: not drawn, 1: drawn, 2: held back after a touch), or -1.</summary>
        static int CursorFlags()
        {
            var info = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
            return GetCursorInfo(ref info) ? info.flags : -1;
        }

        /// <summary>Mouse Keys on, for this sign-in only, without its taskbar icon; false if it already was, or Windows refused.</summary>
        static bool TurnOnMouseKeys()
        {
            var keys = new MOUSEKEYS { cbSize = Marshal.SizeOf(typeof(MOUSEKEYS)) };
            if (!SystemParametersInfo(SPI_GETMOUSEKEYS, (uint)keys.cbSize, ref keys, 0) || (keys.dwFlags & MKF_MOUSEKEYSON) != 0) return false;
            _mouseKeysBefore = keys;
            keys.dwFlags = (keys.dwFlags | MKF_AVAILABLE | MKF_MOUSEKEYSON) & ~MKF_INDICATOR;
            if (keys.iMaxSpeed < 10) keys.iMaxSpeed = 80;   // never set: Windows' defaults
            if (keys.iTimeToMaxSpeed < 1000) keys.iTimeToMaxSpeed = 3000;
            if (!SystemParametersInfo(SPI_SETMOUSEKEYS, (uint)keys.cbSize, ref keys, 0))
            {
                Log.Info("Pointer: turning Mouse Keys on failed (error " + Marshal.GetLastWin32Error() + ")");
                return false;
            }
            _mouseKeysOurs = true;
            return true;
        }

        static void PutBackMouseKeys()
        {
            MOUSEKEYS keys = _mouseKeysBefore;
            SystemParametersInfo(SPI_SETMOUSEKEYS, (uint)keys.cbSize, ref keys, 0);
            _mouseKeysOurs = false;
        }

        /// <summary>For the self-test: Mouse Keys is on now; and turning it on as with no mouse (Restore turns it back off).</summary>
        internal static bool MouseKeysOn
        {
            get
            {
                var keys = new MOUSEKEYS { cbSize = Marshal.SizeOf(typeof(MOUSEKEYS)) };
                return SystemParametersInfo(SPI_GETMOUSEKEYS, (uint)keys.cbSize, ref keys, 0) && (keys.dwFlags & MKF_MOUSEKEYSON) != 0;
            }
        }
        internal static bool TestMouseKeys() { lock (Lock) return TurnOnMouseKeys(); }

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

        /// <summary>
        /// A .cur file holding one 32-bit image with transparency (the format Windows' own cursors use), and a mask
        /// marking its see-through pixels: Windows goes by the mask when every pixel's alpha is 0.
        /// </summary>
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
            var mask = new byte[maskStride * size];                        // 1: leave the screen as it is
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    if (pixels[(y * size + x) * 4 + 3] == 0) mask[(size - 1 - y) * maskStride + x / 8] |= (byte)(0x80 >> (x % 8));
            w.Write(mask);
            w.Flush();
            return stream.ToArray();
        }

        // ---------------------------------------------------------------- Windows

        const uint OCR_NORMAL = 32512, OCR_IBEAM = 32513, OCR_HAND = 32649, OCR_APPSTARTING = 32650;
        const uint IMAGE_CURSOR = 2, LR_LOADFROMFILE = 0x10, SPI_SETCURSORS = 0x57;
        const int CURSOR_SHOWING = 1, CURSOR_SUPPRESSED = 2;
        const uint SPI_GETMOUSEKEYS = 0x36, SPI_SETMOUSEKEYS = 0x37, MKF_MOUSEKEYSON = 0x1, MKF_AVAILABLE = 0x2, MKF_INDICATOR = 0x20;
        // Every pointer Windows has: the arrow, text, hourglass, cross, up arrow, the four resize arrows, move, no, hand,
        // working in the background, help, and the pen and location ones.
        static readonly uint[] AllPointers =
            { 32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651, 32671, 32672 };

        [StructLayout(LayoutKind.Sequential)]
        struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public int x, y; }

        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO info);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint flags);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetSystemCursor(IntPtr cursor, uint id);
        [DllImport("user32.dll")] static extern bool DestroyCursor(IntPtr cursor);
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint param, ref MOUSEKEYS value, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEKEYS { public int cbSize; public uint dwFlags; public int iMaxSpeed, iTimeToMaxSpeed, iCtrlSpeed, dwReserved1, dwReserved2; }

        /// <summary>
        /// For the self-test: a cursor file drawn on white as Windows draws a pointer; true when it leaves it all white.
        /// [zeroMask] blanks its mask first (cursor files from before Version 2).
        /// </summary>
        internal static bool DrawsNothing(byte[] file, int size, bool zeroMask)
        {
            if (zeroMask)
            {
                file = (byte[])file.Clone();
                int maskBytes = (size + 31) / 32 * 4 * size;
                Array.Clear(file, file.Length - maskBytes, maskBytes);
            }
            string path = Path.Combine(Path.GetTempPath(), "couchtv-pointer-test.cur");
            File.WriteAllBytes(path, file);
            IntPtr cursor = LoadImage(IntPtr.Zero, path, IMAGE_CURSOR, size, size, LR_LOADFROMFILE);
            if (cursor == IntPtr.Zero) return false;
            try
            {
                using (var bitmap = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                using (var g = System.Drawing.Graphics.FromImage(bitmap))
                {
                    g.Clear(System.Drawing.Color.White);
                    IntPtr dc = g.GetHdc();
                    try { DrawIconEx(dc, 0, 0, cursor, size, size, 0, IntPtr.Zero, DI_NORMAL); }
                    finally { g.ReleaseHdc(dc); }
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                        {
                            System.Drawing.Color c = bitmap.GetPixel(x, y);
                            if (c.R != 255 || c.G != 255 || c.B != 255) return false;
                        }
                }
                return true;
            }
            finally
            {
                DestroyCursor(cursor);
                try { File.Delete(path); } catch (IOException) { }
            }
        }

        const uint DI_NORMAL = 3;
        [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);

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
