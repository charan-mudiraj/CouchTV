using System;
using System.Collections.Generic;
using System.Text;

namespace CouchTV
{
    /// <summary>
    /// One-time changes to the installed couchtv.ini. Updates never replace that file (it holds your own settings), so
    /// when a new version changes a default, it's applied here once, recorded as ConfigVersion in [CouchTV], and never
    /// again: if you change it back afterwards, your choice stays.
    /// </summary>
    internal static class ConfigUpgrade
    {
        public const int Latest = 4;

        /// <summary>The file's text with any pending changes applied; changes lists what was done.</summary>
        public static string Apply(string text, List<string> changes)
        {
            int version;
            string current = Get(text, "CouchTV", "ConfigVersion");
            if (current == null || !int.TryParse(current, out version)) version = 0;
            if (version >= Latest) return text;

            if (version < 1)
            {
                // October 2026: VLC hidden for now. Netflix is the subscription, so search tries it first for shows and
                // films, then Prime Video and JioHotstar (they have some free titles), and keeps YouTube for music and videos.
                if (Get(text, "VLC", "Type") != null)
                {
                    text = Set(text, "VLC", "Enabled", "false", false);
                    changes.Add("VLC tile hidden");
                }
                text = Set(text, "CouchTV", "Subscriptions", "Netflix", true);
                changes.Add("Subscriptions = Netflix");
            }
            if (version < 2)
            {
                // October 2026: the Bluetooth tile opens CouchTV's own Bluetooth screen (worked with the remote, and
                // headphones play along with the TV speakers) instead of Windows' pairing wizard.
                string exe = Get(text, "Bluetooth", "Exe");
                if (exe != null && exe.IndexOf("DevicePairingWizard", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    text = Set(text, "Bluetooth", "Type", "action", false);
                    text = Set(text, "Bluetooth", "Action", "bluetooth", false);
                    changes.Add("Bluetooth tile opens CouchTV's Bluetooth screen");
                }
            }
            if (version < 3)
            {
                // October 2026: CouchTV's own Settings screen, first in Settings & power.
                if (!text.Contains("Action = settings"))
                {
                    text = InsertSectionBefore(text, "Bluetooth", new[]
                    {
                        "; CouchTV's own settings, worked with the remote (the phone's Settings page has them too).",
                        "[Settings]", "Row = system", "Type = action", "Action = settings", "Glyph = E713", "",
                    });
                    changes.Add("Settings tile added");
                }
            }
            if (version < 4)
            {
                // October 2026: ACT's bundle. ZEE5, Sun NXT, ETV Win and YuppTV join Netflix, Prime Video and JioHotstar
                // as yours, as tiles before Web, and YuppTV's live channels in Telugu, Hindi and English go under the apps.
                if (Get(text, "ZEE5", "Type") != null)
                {
                    // It was an optional tile, hidden down after Web: shown now, with the other apps.
                    if (text.IndexOf("[ZEE5]", StringComparison.Ordinal) > text.IndexOf("[Web]", StringComparison.Ordinal)) text = MoveSectionBefore(text, "ZEE5", "Web");
                    text = Set(text, "ZEE5", "Enabled", "true", false);
                }
                else text = InsertSectionBefore(text, "Web", BundleTile("ZEE5", "https://www.zee5.com", "ZEE5", "#8230C6", "#4B1580", "#FFFFFF"));
                if (Get(text, "Sun NXT", "Type") == null)
                    text = InsertSectionBefore(text, "Web", BundleTile("Sun NXT", "https://www.sunnxt.com", "SUN NXT", "#262626", "#000000", "#FF9A1F"));
                if (Get(text, "ETV Win", "Type") == null)
                    text = InsertSectionBefore(text, "Web", BundleTile("ETV Win", "https://www.etvwin.com", "ETV Win", "#0E2A6B", "#06122E", "#FFC21A"));
                if (Get(text, "YuppTV", "Type") == null)
                    text = InsertSectionBefore(text, "Web", BundleTile("YuppTV", "https://www.yupptv.com", "YuppTV", "#2B2B2B", "#0D0D0D", "#FFD400"));
                text = Set(text, "CouchTV", "Subscriptions", "Netflix, Prime Video, JioHotstar, ZEE5, Sun NXT, ETV Win, YuppTV", true);
                text = Set(text, "CouchTV", "LiveTv", "TEL, HIN, ENG", true);
                changes.Add("ZEE5, Sun NXT, ETV Win and YuppTV tiles, all of ACT's bundle as subscriptions, Live TV in Telugu, Hindi and English");
            }
            return Set(text, "CouchTV", "ConfigVersion", Latest.ToString(), true);
        }

        /// <summary>A tile of the bundle, opening in Edge (the paid services' best-supported Windows browser).</summary>
        static string[] BundleTile(string name, string url, string label, string background, string background2, string foreground)
        {
            return new[]
            {
                "[" + name + "]", "Type = web", "Url = " + url, "Browser = edge", "Text = " + label, "Weight = Black",
                "Background = " + background, "Background2 = " + background2, "Foreground = " + foreground, "",
            };
        }

        /// <summary>[section] moved to just before [before]: its own lines, not the blank lines and comments leading to the next.</summary>
        internal static string MoveSectionBefore(string text, string section, string before)
        {
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> lines = Lines(text);
            int start, end;
            if (!FindSection(lines, section, out start, out end)) return text;
            int last = end;
            while (last > start + 1 && (lines[last - 1].TrimStart().StartsWith(";") || lines[last - 1].Trim().Length == 0)) last--;
            List<string> block = lines.GetRange(start, last - start);
            block.Add("");
            lines.RemoveRange(start, last - start);
            return InsertSectionBefore(string.Join(newline, lines), before, block.ToArray());
        }

        /// <summary>New lines just before [section] (and the comments over it), or at the end without one.</summary>
        internal static string InsertSectionBefore(string text, string section, string[] block)
        {
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> lines = Lines(text);
            int start, end;
            int at = lines.Count;
            if (FindSection(lines, section, out start, out end))
            {
                at = start;
                while (at > 0 && lines[at - 1].TrimStart().StartsWith(";")) at--;
            }
            else if (lines.Count > 0 && lines[lines.Count - 1].Length > 0) lines.Add("");
            lines.InsertRange(at, block);
            return string.Join(newline, lines);
        }

        /// <summary>A key's value inside [section], or null.</summary>
        internal static string Get(string text, string section, string key)
        {
            List<string> lines = Lines(text);
            int start, end;
            if (!FindSection(lines, section, out start, out end)) return null;
            for (int i = start + 1; i < end; i++)
            {
                string name, value;
                if (KeyLine(lines[i], out name, out value) && name.Equals(key, StringComparison.OrdinalIgnoreCase)) return value;
            }
            return null;
        }

        /// <summary>
        /// Sets key = value inside [section]: replaces the key's line, or adds one after the section's last setting
        /// (not after the comments that introduce the next section). addSection: create the section if it's missing.
        /// </summary>
        internal static string Set(string text, string section, string key, string value, bool addSection)
        {
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> lines = Lines(text);
            string line = key + " = " + value;
            int start, end;
            if (!FindSection(lines, section, out start, out end))
            {
                if (!addSection) return text;
                if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
                lines.Add("");
                lines.Add("[" + section + "]");
                lines.Add(line);
                lines.Add("");
                return string.Join(newline, lines);
            }
            int lastSetting = start;
            for (int i = start + 1; i < end; i++)
            {
                string name, ignored;
                if (!KeyLine(lines[i], out name, out ignored)) continue;
                if (name.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = line;
                    return string.Join(newline, lines);
                }
                lastSetting = i;
            }
            lines.Insert(lastSetting + 1, line);
            return string.Join(newline, lines);
        }

        static List<string> Lines(string text)
        {
            var lines = new List<string>();
            foreach (string raw in text.Split('\n')) lines.Add(raw.TrimEnd('\r'));
            return lines;
        }

        static bool FindSection(List<string> lines, string section, out int start, out int end)
        {
            start = -1;
            end = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (!(t.StartsWith("[") && t.EndsWith("]"))) continue;
                if (start >= 0)
                {
                    end = i;
                    break;
                }
                if (t.Substring(1, t.Length - 2).Trim().Equals(section, StringComparison.OrdinalIgnoreCase)) start = i;
            }
            return start >= 0;
        }

        static bool KeyLine(string line, out string name, out string value)
        {
            name = value = null;
            string t = line.Trim();
            int eq = t.IndexOf('=');
            if (t.Length == 0 || t[0] == ';' || t[0] == '#' || eq <= 0) return false;
            name = t.Substring(0, eq).Trim();
            value = t.Substring(eq + 1).Trim();
            return true;
        }
    }
}
