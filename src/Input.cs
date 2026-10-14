using System;
using System.Runtime.InteropServices;

namespace CouchTV
{
    /// <summary>Types keys and moves the mouse for remote buttons, into whatever app is in front (Netflix, YouTube...).</summary>
    internal static class Input
    {
        /// <summary>Marks CouchTV's own key presses so its keyboard hook can tell them from real ones.</summary>
        public static readonly IntPtr Marker = (IntPtr)0x43545631;   // "CTV1"

        const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;
        const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4;
        const uint MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_WHEEL = 0x800;
        const uint MOUSEEVENTF_HWHEEL = 0x1000, MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
        const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

        public static void Key(uint vk) { Combo(0, vk); }

        /// <summary>Presses modifiers (MOD_* flags), taps the key, releases the modifiers.</summary>
        public static void Combo(uint modifiers, uint vk)
        {
            var events = new System.Collections.Generic.List<INPUT>();
            uint[] mods = { Native.MOD_CONTROL, 0x11, Native.MOD_ALT, 0x12, Native.MOD_SHIFT, 0x10, Native.MOD_WIN, 0x5B };
            for (int i = 0; i < mods.Length; i += 2)
                if ((modifiers & mods[i]) != 0) events.Add(KeyEvent(mods[i + 1], false));
            events.Add(KeyEvent(vk, false));
            events.Add(KeyEvent(vk, true));
            for (int i = mods.Length - 2; i >= 0; i -= 2)
                if ((modifiers & mods[i]) != 0) events.Add(KeyEvent(mods[i + 1], true));
            Send(events.ToArray());
        }

        public static void MouseMove(int dx, int dy) { Send(MouseEvent(MOUSEEVENTF_MOVE, dx, dy, 0)); }

        // ---------------------------------------------------------------- the phone's keyboards

        /// <summary>
        /// A key down or up from the phone's keyboard page, as a real keyboard's (not marked as CouchTV's own, so a tap
        /// of Win is Home in TV mode, just as on a real keyboard). Held, it's sent down again and again: Windows'
        /// own key repeat only comes from real keyboards.
        /// </summary>
        public static void KeyChange(uint vk, bool down) { Send(KeyEvent(vk, !down, false)); }

        /// <summary>
        /// What was typed on the phone's own keyboard into the field in front: backspaces, then the text, as characters
        /// (any language, emoji too), whatever the PC's keyboard layout. A line break is Enter.
        /// </summary>
        public static void Edit(int backspaces, string text)
        {
            var events = new System.Collections.Generic.List<INPUT>();
            for (int i = 0; i < backspaces; i++)
            {
                events.Add(KeyEvent(0x08, false));
                events.Add(KeyEvent(0x08, true));
            }
            foreach (char c in text)
            {
                if (c == '\r') continue;
                if (c == '\n')
                {
                    events.Add(KeyEvent(0x0D, false));
                    events.Add(KeyEvent(0x0D, true));
                    continue;
                }
                events.Add(CharEvent(c, false));
                events.Add(CharEvent(c, true));
            }
            if (events.Count > 0) Send(events.ToArray());
        }

        public static void Click(bool right)
        {
            Send(MouseEvent(right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN, 0, 0, 0),
                 MouseEvent(right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP, 0, 0, 0));
        }

        public static void Wheel(int notches) { Send(MouseEvent(MOUSEEVENTF_WHEEL, 0, 0, notches * 120)); }

        // ---------------------------------------------------------------- the phone's trackpad

        /// <summary>
        /// Moves the pointer by exactly (dx, dy) pixels, skipping Windows' mouse acceleration: the phone's trackpad
        /// has its own. Sent as an absolute position, as Windows' normalised 0–65535 across all screens.
        /// </summary>
        public static void MoveBy(int dx, int dy)
        {
            POINT p;
            if (!GetCursorPos(out p)) return;
            int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int width = GetSystemMetrics(SM_CXVIRTUALSCREEN), height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (width < 2 || height < 2) return;
            int x = Math.Max(0, Math.Min(width - 1, p.X + dx - left));
            int y = Math.Max(0, Math.Min(height - 1, p.Y + dy - top));
            // Windows turns n back into the pixel n * size / 65536, rounded down: the smallest n that lands on x.
            int nx = (int)Math.Min(65535, ((long)x * 65536 + width - 1) / width);
            int ny = (int)Math.Min(65535, ((long)y * 65536 + height - 1) / height);
            Send(MouseEvent(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny, 0));
        }

        /// <summary>Puts the pointer on a screen pixel (the screen mirror's touches, where touch can't be injected).</summary>
        public static void MoveTo(int x, int y)
        {
            int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int width = GetSystemMetrics(SM_CXVIRTUALSCREEN), height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (width < 2 || height < 2) return;
            x = Math.Max(0, Math.Min(width - 1, x - left));
            y = Math.Max(0, Math.Min(height - 1, y - top));
            int nx = (int)Math.Min(65535, ((long)x * 65536 + width - 1) / width);
            int ny = (int)Math.Min(65535, ((long)y * 65536 + height - 1) / height);
            Send(MouseEvent(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny, 0));
        }

        /// <summary>A mouse button down or up (a click is both; holding it between drags).</summary>
        public static void Button(bool right, bool down)
        {
            uint flags = right ? (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP) : (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP);
            Send(MouseEvent(flags, 0, 0, 0));
        }

        /// <summary>Scrolls by delta (120 is one notch of a mouse wheel; less scrolls smoothly in browsers): up/right when positive.</summary>
        public static void WheelBy(int delta, bool horizontal)
        {
            Send(MouseEvent(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, 0, 0, delta));
        }

        /// <summary>Where the pointer is, in screen pixels.</summary>
        public static bool CursorPosition(out int x, out int y)
        {
            POINT p;
            bool ok = GetCursorPos(out p);
            x = p.X;
            y = p.Y;
            return ok;
        }

        /// <summary>True while an app (a game, say) has locked the pointer to part of the screen.</summary>
        public static bool PointerLocked()
        {
            RECT clip;
            return GetClipCursor(out clip)
                && (clip.Right - clip.Left < GetSystemMetrics(SM_CXVIRTUALSCREEN) || clip.Bottom - clip.Top < GetSystemMetrics(SM_CYVIRTUALSCREEN));
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] static extern bool GetClipCursor(out RECT rect);

        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

        static INPUT KeyEvent(uint vk, bool up, bool ours = true)
        {
            var input = new INPUT { type = INPUT_KEYBOARD };
            input.u.ki.wVk = (ushort)vk;
            input.u.ki.wScan = (ushort)MapVirtualKey(vk, 0);
            input.u.ki.dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
            input.u.ki.dwExtraInfo = ours ? Marker : IntPtr.Zero;
            return input;
        }

        /// <summary>One UTF-16 unit as a character (an emoji is two), not a key: lands as typed text in any app.</summary>
        static INPUT CharEvent(char c, bool up)
        {
            var input = new INPUT { type = INPUT_KEYBOARD };
            input.u.ki.wScan = c;
            input.u.ki.dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0);
            input.u.ki.dwExtraInfo = Marker;
            return input;
        }

        static INPUT MouseEvent(uint flags, int dx, int dy, int data)
        {
            var input = new INPUT { type = INPUT_MOUSE };
            input.u.mi.dx = dx;
            input.u.mi.dy = dy;
            input.u.mi.mouseData = data;
            input.u.mi.dwFlags = flags;
            input.u.mi.dwExtraInfo = Marker;
            return input;
        }

        // Arrows, Page Up/Down, Home/End, Insert/Delete, Print Screen, Win, the menu key, the right Ctrl and Alt, and
        // the browser/media keys are "extended" keys.
        static bool IsExtended(uint vk)
        {
            return (vk >= 0x21 && vk <= 0x28) || vk == 0x2C || vk == 0x2D || vk == 0x2E || vk == 0x5B || vk == 0x5C || vk == 0x5D
                || vk == 0xA3 || vk == 0xA5 || (vk >= 0xA6 && vk <= 0xB7);
        }

        static void Send(params INPUT[] inputs)
        {
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT))) != inputs.Length)
                Log.Info("SendInput was blocked (error " + Marshal.GetLastWin32Error() + ")");
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx, dy, mouseData;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    }
}
