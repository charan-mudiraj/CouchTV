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
        const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;
        const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4;
        const uint MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_WHEEL = 0x800;

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

        public static void Click(bool right)
        {
            Send(MouseEvent(right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN, 0, 0, 0),
                 MouseEvent(right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP, 0, 0, 0));
        }

        public static void Wheel(int notches) { Send(MouseEvent(MOUSEEVENTF_WHEEL, 0, 0, notches * 120)); }

        static INPUT KeyEvent(uint vk, bool up)
        {
            var input = new INPUT { type = INPUT_KEYBOARD };
            input.u.ki.wVk = (ushort)vk;
            input.u.ki.wScan = (ushort)MapVirtualKey(vk, 0);
            input.u.ki.dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
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

        // Arrows, Page Up/Down, Home/End, Insert/Delete, the menu key and the browser/media keys are "extended" keys.
        static bool IsExtended(uint vk)
        {
            return (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || vk == 0x5B || vk == 0x5C || vk == 0x5D
                || (vk >= 0xA6 && vk <= 0xB7);
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
