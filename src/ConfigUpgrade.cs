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
        public const int Latest = 1;

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
            return Set(text, "CouchTV", "ConfigVersion", Latest.ToString(), true);
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
