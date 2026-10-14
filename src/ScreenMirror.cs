using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace CouchTV
{
    /// <summary>
    /// The TV's screen on a phone, for choosing what to watch from across the room ("Screen mirror" in
    /// ir-receiver/PROTOCOL.md). The screen is captured small (GDI, scaled with HALFTONE, about 1280 pixels wide),
    /// split into 64-pixel tiles, and only tiles that changed are sent, as JPEG strips: a highlight moving costs a few
    /// KB, and a still screen nothing but a heartbeat each second. Frames are paced to a bandwidth cap the phone asks
    /// for (about 4 Mbps; less while it's playing the TV's sound too), so the sound keeps its share of the Wi-Fi, and
    /// sent encrypted on the phone's own paired connection. It looks 15 times a second while things change or someone
    /// touches or presses something, and 5 times a second when the screen's been still for a while. Copy-protected
    /// video (Netflix, Prime Video…) shows as black, as in any screen capture; their menus don't.
    /// </summary>
    internal static class ScreenMirror
    {
        public const int TileSize = 64, MostWidth = 1600;
        const int FrameMs = 66, IdleFrameMs = 200, BusyFor = 2000, HeartbeatMs = 1000;
        static long _poked = Environment.TickCount;

        /// <summary>A touch, button or key from a phone: look at full speed for a while, as the screen's about to change.</summary>
        public static void Poke() { _poked = Environment.TickCount; }
        const byte Start = 1, Tile = 2, End = 3, Cursor = 4, Heartbeat = 5;
        static readonly ImageCodecInfo JpegCodec = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.MimeType == "image/jpeg");

        /// <summary>Streams until stop() says so or the phone goes (a write fails). width: the most the phone wants.</summary>
        public static void Stream(Stream stream, byte[] enc, byte[] mac, int width, int kbps, Func<bool> stop)
        {
            width = Math.Max(320, Math.Min(MostWidth, width));
            double bytesPerMs = Math.Max(64, Math.Min(20000, kbps)) / 8.0;
            using (var writer = new FrameWriter(stream, enc, mac))
            {
                ScreenGrab grab = null;
                int[] previous = null;
                var clock = Stopwatch.StartNew();
                long allowedAt = 0, lastSent = 0, lastSizeCheck = 0, lastChange = 0;
                int cursorX = -1, cursorY = -1;
                bool cursorShown = false;
                try
                {
                    while (!stop())
                    {
                        long now = clock.ElapsedMilliseconds;
                        if (grab == null || now - lastSizeCheck > 2000)
                        {
                            lastSizeCheck = now;
                            if (grab == null || grab.ScreenChanged())
                            {
                                if (grab != null) grab.Dispose();
                                grab = new ScreenGrab(width);
                                previous = null;
                                lastSent = now;
                                writer.Send(Message(Start, U16(grab.Width), U16(grab.Height)));
                            }
                        }
                        if (now < allowedAt)
                        {
                            Thread.Sleep((int)Math.Min(30, allowedAt - now));
                            continue;
                        }
                        if (!grab.Grab())
                        {
                            Thread.Sleep(250);   // a secure desktop (UAC), say
                            continue;
                        }
                        int bytes = SendChanges(grab, previous, writer);
                        if (previous == null) previous = new int[grab.Pixels.Length];
                        if (bytes > 0) Array.Copy(grab.Pixels, previous, previous.Length);

                        int x, y;
                        bool shown = grab.CursorAt(out x, out y);
                        if (shown != cursorShown || (shown && (x != cursorX || y != cursorY)))
                        {
                            cursorShown = shown;
                            cursorX = x;
                            cursorY = y;
                            bytes += writer.Send(Message(Cursor, U16(Math.Max(0, x)), U16(Math.Max(0, y)), new[] { (byte)(shown ? 1 : 0) }));
                        }
                        if (bytes > 0) lastChange = now;
                        if (bytes == 0 && now - lastSent >= HeartbeatMs) bytes = writer.Send(new[] { Heartbeat });
                        if (bytes > 0) lastSent = now;
                        // The next look waits a frame (longer when all's been still), or as long as these bytes take at
                        // the cap, whichever is longer.
                        bool busy = now - lastChange < BusyFor || unchecked(Environment.TickCount - (int)_poked) < BusyFor;
                        allowedAt = now + Math.Max(busy ? FrameMs : IdleFrameMs, (long)(bytes / bytesPerMs));
                    }
                }
                finally { if (grab != null) grab.Dispose(); }
            }
        }

        /// <summary>Sends the tiles that changed since previous (all of them when it's null), as strips along each row; the bytes sent.</summary>
        static int SendChanges(ScreenGrab grab, int[] previous, FrameWriter writer)
        {
            int w = grab.Width, h = grab.Height, sent = 0;
            int[] now = grab.Pixels;
            for (int ty = 0; ty < h; ty += TileSize)
            {
                int th = Math.Min(TileSize, h - ty);
                int runStart = -1;
                for (int tx = 0; tx <= w; tx += TileSize)
                {
                    bool changed = tx < w && (previous == null || Differs(now, previous, w, tx, ty, Math.Min(TileSize, w - tx), th));
                    if (changed && runStart < 0) runStart = tx;
                    if (!changed && runStart >= 0)
                    {
                        int rw = Math.Min(tx, w) - runStart;
                        byte[] jpeg = Jpeg(now, w, runStart, ty, rw, th);
                        sent += writer.Send(Message(Tile, U16(runStart), U16(ty), U16(rw), U16(th), jpeg));
                        runStart = -1;
                    }
                }
            }
            if (sent > 0) sent += writer.Send(new[] { End });
            return sent;
        }

        static bool Differs(int[] a, int[] b, int stride, int x, int y, int w, int h)
        {
            for (int row = y; row < y + h; row++)
            {
                int i = row * stride + x, end = i + w;
                for (; i < end; i++) if (a[i] != b[i]) return true;
            }
            return false;
        }

        static byte[] Jpeg(int[] pixels, int stride, int x, int y, int w, int h)
        {
            using (var bitmap = new Bitmap(w, h, PixelFormat.Format32bppRgb))
            {
                BitmapData data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    for (int row = 0; row < h; row++)
                        Marshal.Copy(pixels, (y + row) * stride + x, new IntPtr(data.Scan0.ToInt64() + (long)row * data.Stride), w);
                }
                finally { bitmap.UnlockBits(data); }
                using (var output = new MemoryStream())
                using (var parameters = new EncoderParameters(1))
                {
                    parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 72L);   // text stays readable
                    bitmap.Save(output, JpegCodec, parameters);
                    return output.ToArray();
                }
            }
        }

        static byte[] U16(int value) { return new[] { (byte)(value >> 8), (byte)value }; }

        static byte[] Message(byte type, params byte[][] parts)
        {
            int length = 1;
            foreach (byte[] p in parts) length += p.Length;
            var message = new byte[length];
            message[0] = type;
            int at = 1;
            foreach (byte[] p in parts)
            {
                Buffer.BlockCopy(p, 0, message, at, p.Length);
                at += p.Length;
            }
            return message;
        }
    }

    /// <summary>
    /// Binary messages, encrypted: each is its length (4 bytes, big-endian), the AES-CTR ciphertext, and a 16-byte
    /// HMAC tag over the message number and ciphertext, as for lines (Channel). Numbers count from 0 and aren't sent.
    /// </summary>
    internal sealed class FrameWriter : IDisposable
    {
        readonly Stream _stream;
        readonly Ctr _cipher;
        readonly HMACSHA256 _mac;
        ulong _number;

        public FrameWriter(Stream stream, byte[] enc, byte[] mac)
        {
            _stream = stream;
            _cipher = new Ctr(enc);
            _mac = new HMACSHA256(mac);
        }

        /// <summary>The bytes sealed, for the self-test's known answers.</summary>
        public byte[] Seal(byte[] payload)
        {
            int length = payload.Length + Secure.TagBytes;
            var sealedBytes = new byte[4 + length];
            sealedBytes[0] = (byte)(length >> 24);
            sealedBytes[1] = (byte)(length >> 16);
            sealedBytes[2] = (byte)(length >> 8);
            sealedBytes[3] = (byte)length;
            Buffer.BlockCopy(payload, 0, sealedBytes, 4, payload.Length);
            _cipher.Apply(_number, sealedBytes, 4, payload.Length);
            var counter = new byte[8];
            Ctr.BigEndian(_number, counter, 0);
            _mac.Initialize();
            _mac.TransformBlock(counter, 0, 8, null, 0);
            _mac.TransformFinalBlock(sealedBytes, 4, payload.Length);
            Buffer.BlockCopy(_mac.Hash, 0, sealedBytes, 4 + payload.Length, Secure.TagBytes);
            _number++;
            return sealedBytes;
        }

        public int Send(byte[] payload)
        {
            byte[] sealedBytes = Seal(payload);
            _stream.Write(sealedBytes, 0, sealedBytes.Length);
            return sealedBytes.Length;
        }

        public void Dispose()
        {
            _cipher.Dispose();
            _mac.Dispose();
        }
    }

    /// <summary>The primary screen, scaled down to at most a width (GDI StretchBlt, HALFTONE), as 32-bit pixels.</summary>
    internal sealed class ScreenGrab : IDisposable
    {
        public readonly int Width, Height, ScreenWidth, ScreenHeight;
        public readonly int[] Pixels;
        IntPtr _memory, _bitmap, _old, _bits;

        public ScreenGrab(int most)
        {
            ScreenWidth = Math.Max(1, GetSystemMetrics(0));
            ScreenHeight = Math.Max(1, GetSystemMetrics(1));
            Width = Math.Min(most, ScreenWidth) & ~1;
            Height = (int)((long)Width * ScreenHeight / ScreenWidth) & ~1;
            Pixels = new int[Width * Height];
            IntPtr screen = GetDC(IntPtr.Zero);
            try
            {
                _memory = CreateCompatibleDC(screen);
                var header = new BITMAPINFOHEADER { biSize = 40, biWidth = Width, biHeight = -Height, biPlanes = 1, biBitCount = 32 };
                _bitmap = CreateDIBSection(screen, ref header, 0, out _bits, IntPtr.Zero, 0);
            }
            finally { ReleaseDC(IntPtr.Zero, screen); }
            if (_bitmap == IntPtr.Zero) throw new Exception("couldn't make a bitmap to capture into");
            _old = SelectObject(_memory, _bitmap);
            SetStretchBltMode(_memory, 4);   // HALFTONE: averages, so small text stays legible
            SetBrushOrgEx(_memory, 0, 0, IntPtr.Zero);
        }

        public bool ScreenChanged() { return GetSystemMetrics(0) != ScreenWidth || GetSystemMetrics(1) != ScreenHeight; }

        public bool Grab()
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            try
            {
                if (!StretchBlt(_memory, 0, 0, Width, Height, screen, 0, 0, ScreenWidth, ScreenHeight, 0x00CC0020)) return false;   // SRCCOPY
            }
            finally { ReleaseDC(IntPtr.Zero, screen); }
            GdiFlush();
            Marshal.Copy(_bits, Pixels, 0, Pixels.Length);
            return true;
        }

        /// <summary>The mouse pointer's place on the small picture; false when it's hidden (touching hides it).</summary>
        public bool CursorAt(out int x, out int y)
        {
            var info = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
            x = y = -1;
            if (!GetCursorInfo(ref info) || (info.flags & 1) == 0) return false;
            x = (int)((long)info.ptScreenPos.X * Width / ScreenWidth);
            y = (int)((long)info.ptScreenPos.Y * Height / ScreenHeight);
            return x >= 0 && y >= 0 && x < Width && y < Height;
        }

        public void Dispose()
        {
            if (_memory != IntPtr.Zero)
            {
                SelectObject(_memory, _old);
                DeleteDC(_memory);
                _memory = IntPtr.Zero;
            }
            if (_bitmap != IntPtr.Zero)
            {
                DeleteObject(_bitmap);
                _bitmap = IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct CURSORINFO
        {
            public int cbSize, flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO info);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER header, int usage, out IntPtr bits, IntPtr section, int offset);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);
        [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dest, int x, int y, int w, int h, IntPtr source, int sx, int sy, int sw, int sh, int rop);
        [DllImport("gdi32.dll")] static extern bool GdiFlush();
    }

    /// <summary>
    /// Touches on the phone's mirror, played on the TV as real touches (Windows' touch injection), so apps and
    /// browsers take them as from a touchscreen: tap to click, drag to scroll, pinch to zoom, hold to right-click. Up
    /// to ten fingers. Held fingers are repeated every 100 ms (Windows lets go of a touch that goes quiet). Where touch
    /// can't be injected, one finger works as the mouse.
    /// </summary>
    internal static class TouchInjector
    {
        const int MostContacts = 10;
        const uint FlagInRange = 0x2, FlagInContact = 0x4, FlagDown = 0x10000, FlagUpdate = 0x20000, FlagUp = 0x40000;

        sealed class Contact
        {
            public int Slot, X, Y;
        }

        static readonly object Lock = new object();
        static readonly Dictionary<string, Contact> Contacts = new Dictionary<string, Contact>();
        static Timer _repeat;
        static bool? _ready;
        static bool _mouseDown;

        /// <summary>A finger (owner: the phone's connection; id: its finger) down, moved or up, at x, y from 0 to 65535 across the screen.</summary>
        public static void Touch(string owner, int id, char kind, int nx, int ny)
        {
            int x = (int)((long)nx * (GetSystemMetrics(0) - 1) / 65535), y = (int)((long)ny * (GetSystemMetrics(1) - 1) / 65535);
            lock (Lock)
            {
                if (_ready == null)
                {
                    _ready = InitializeTouchInjection(MostContacts, 1);   // TOUCH_FEEDBACK_DEFAULT: the TV shows where you touch
                    if (_ready == false) Log.Info("Screen mirror: touch can't be injected here (" + Marshal.GetLastWin32Error() + "): one finger works as the mouse");
                }
                string key = owner + "/" + id;
                Contact contact;
                Contacts.TryGetValue(key, out contact);
                if (_ready == false)
                {
                    // The mouse instead: the first finger moves it, pressing the left button while it's down.
                    if (kind == 'D')
                    {
                        if (Contacts.Count > 0) return;
                        Contacts[key] = new Contact { Slot = 0, X = x, Y = y };
                        Input.MoveTo(x, y);
                        Input.Button(false, true);
                        _mouseDown = true;
                    }
                    else if (contact != null)
                    {
                        Input.MoveTo(x, y);
                        if (kind == 'U')
                        {
                            Input.Button(false, false);
                            _mouseDown = false;
                            Contacts.Remove(key);
                        }
                    }
                    return;
                }
                if (kind == 'D')
                {
                    if (contact != null) return;
                    int slot = FreeSlot();
                    if (slot < 0) return;
                    contact = new Contact { Slot = slot, X = x, Y = y };
                    Contacts[key] = contact;
                    Inject(contact, FlagDown | FlagInRange | FlagInContact);
                }
                else if (contact != null && kind == 'M')
                {
                    contact.X = x;
                    contact.Y = y;
                    Inject(contact, FlagUpdate | FlagInRange | FlagInContact);
                }
                else if (contact != null && kind == 'U')
                {
                    contact.X = x;
                    contact.Y = y;
                    Inject(contact, FlagUp);
                    Contacts.Remove(key);
                }
                if (Contacts.Count > 0 && _repeat == null) _repeat = new Timer(state => Repeat(), null, 100, 100);
                if (Contacts.Count == 0 && _repeat != null)
                {
                    _repeat.Dispose();
                    _repeat = null;
                }
            }
        }

        /// <summary>Lifts every finger of a phone that went away.</summary>
        public static void LiftAll(string owner)
        {
            lock (Lock)
            {
                foreach (string key in new List<string>(Contacts.Keys))
                {
                    if (!key.StartsWith(owner + "/")) continue;
                    Contact c = Contacts[key];
                    if (_ready == true) Inject(c, FlagUp);
                    else if (_mouseDown)
                    {
                        Input.Button(false, false);
                        _mouseDown = false;
                    }
                    Contacts.Remove(key);
                }
            }
        }

        static int FreeSlot()
        {
            for (int slot = 0; slot < MostContacts; slot++)
            {
                bool used = false;
                foreach (Contact c in Contacts.Values) if (c.Slot == slot) used = true;
                if (!used) return slot;
            }
            return -1;
        }

        static void Repeat()
        {
            lock (Lock)
            {
                if (_ready != true || Contacts.Count == 0) return;
                Inject(null, 0);
            }
        }

        /// <summary>One frame of every finger down: changed with its flags, the rest held. Under Lock.</summary>
        static void Inject(Contact changed, uint flags)
        {
            var frame = new List<POINTER_TOUCH_INFO>();
            foreach (Contact c in Contacts.Values)
                frame.Add(Info(c, c == changed ? flags : FlagUpdate | FlagInRange | FlagInContact));
            if (changed != null && !Contacts.ContainsValue(changed)) frame.Add(Info(changed, flags));
            if (frame.Count > 0 && !InjectTouchInput((uint)frame.Count, frame.ToArray()))
                Log.Info("Screen mirror: a touch wasn't taken (" + Marshal.GetLastWin32Error() + ")");
        }

        static POINTER_TOUCH_INFO Info(Contact c, uint flags)
        {
            var info = new POINTER_TOUCH_INFO();
            info.pointerInfo.pointerType = 2;   // PT_TOUCH
            info.pointerInfo.pointerId = (uint)c.Slot;
            info.pointerInfo.pointerFlags = flags;
            info.pointerInfo.ptPixelLocation = new POINT { X = c.X, Y = c.Y };
            info.touchMask = 0x7;   // contact area, orientation, pressure
            info.rcContact = new RECT { Left = c.X - 4, Top = c.Y - 4, Right = c.X + 4, Bottom = c.Y + 4 };
            info.orientation = 90;
            info.pressure = 32000;
            return info;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct POINTER_INFO
        {
            public int pointerType;
            public uint pointerId, frameId, pointerFlags;
            public IntPtr sourceDevice, hwndTarget;
            public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
            public uint dwTime, historyCount;
            public int inputData;
            public uint dwKeyStates;
            public ulong performanceCount;
            public int buttonChangeType;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINTER_TOUCH_INFO
        {
            public POINTER_INFO pointerInfo;
            public uint touchFlags, touchMask;
            public RECT rcContact, rcContactRaw;
            public uint orientation, pressure;
        }

        [DllImport("user32.dll", SetLastError = true)] static extern bool InitializeTouchInjection(uint maxCount, uint feedbackMode);
        [DllImport("user32.dll", SetLastError = true)] static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    }
}
