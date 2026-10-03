using System;
using System.Windows.Input;

namespace CouchTV
{
    internal sealed class HotkeySpec
    {
        public uint Modifiers;
        public uint Vk;
        public bool IsWinTap;   // a tap of the Windows key on its own
        public bool IsHold;     // the key held down for a moment (e.g. Back on remotes without a Home button)
    }

    /// <summary>Parses key names from couchtv.ini, e.g. "BrowserHome", "Win", "Hold:BrowserBack", "Ctrl+Alt+H", "F12".</summary>
    internal static class Hotkeys
    {
        public static HotkeySpec Parse(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return null;
            text = text.Trim();

            if (text.StartsWith("Hold:", StringComparison.OrdinalIgnoreCase))
            {
                uint vk = ParseKey(text.Substring(5).Trim());
                return vk == 0 ? null : new HotkeySpec { IsHold = true, Vk = vk };
            }

            string[] parts = text.Split('+');
            var spec = new HotkeySpec();
            if (parts.Length == 1 && parts[0].Trim().Equals("Win", StringComparison.OrdinalIgnoreCase))
            {
                spec.IsWinTap = true;
                return spec;
            }

            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].Trim().ToLowerInvariant())
                {
                    case "ctrl": case "control": spec.Modifiers |= Native.MOD_CONTROL; break;
                    case "alt": spec.Modifiers |= Native.MOD_ALT; break;
                    case "shift": spec.Modifiers |= Native.MOD_SHIFT; break;
                    case "win": spec.Modifiers |= Native.MOD_WIN; break;
                    default: return null;
                }
            }
            spec.Vk = ParseKey(parts[parts.Length - 1].Trim());
            return spec.Vk == 0 ? null : spec;
        }

        static uint ParseKey(string name)
        {
            if (name.Length == 1 && char.IsDigit(name[0])) name = "D" + name;   // "1" means the 1 key, not Key value 1
            Key key;
            if (name.Length == 0 || IsNumeric(name) || !Enum.TryParse(name, true, out key)) return 0;
            return (uint)KeyInterop.VirtualKeyFromKey(key);
        }

        static bool IsNumeric(string s)
        {
            foreach (char c in s) if (!char.IsDigit(c)) return false;
            return true;
        }
    }
}
