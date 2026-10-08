using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CouchTV
{
    /// <summary>
    /// Keys for AI search, kept in %LOCALAPPDATA%\CouchTV\keys.ini: on this PC only, never in the install folder or
    /// the repo. The easy way to add one is to paste it into the phone remote's search box (see README).
    /// </summary>
    internal sealed class ApiKeys
    {
        public const string GeminiName = "GeminiKey", TmdbName = "TmdbKey";

        public string Gemini, Tmdb;

        public static string FilePath { get { return Path.Combine(Config.DataDir, "keys.ini"); } }

        public static ApiKeys Load()
        {
            var keys = new ApiKeys();
            try
            {
                if (!File.Exists(FilePath)) return keys;
                foreach (KeyValuePair<string, string> pair in Read())
                {
                    if (pair.Key.Equals(GeminiName, StringComparison.OrdinalIgnoreCase)) keys.Gemini = pair.Value;
                    else if (pair.Key.Equals(TmdbName, StringComparison.OrdinalIgnoreCase)) keys.Tmdb = pair.Value;
                }
            }
            catch (Exception ex) { Log.Error("Reading " + FilePath, ex); }
            return keys;
        }

        /// <summary>Sets one key, keeping the others.</summary>
        public static void Save(string name, string value)
        {
            var lines = new List<string> { "; Keys for CouchTV's AI search. Keep this file private.", "[Keys]" };
            bool done = false;
            if (File.Exists(FilePath))
            {
                foreach (KeyValuePair<string, string> pair in Read())
                {
                    if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        lines.Add(name + " = " + value);
                        done = true;
                    }
                    else lines.Add(pair.Key + " = " + pair.Value);
                }
            }
            if (!done) lines.Add(name + " = " + value);
            Directory.CreateDirectory(Config.DataDir);
            File.WriteAllLines(FilePath, lines.ToArray(), new UTF8Encoding(false));
        }

        static IEnumerable<KeyValuePair<string, string>> Read()
        {
            foreach (string raw in File.ReadAllLines(FilePath))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.Length == 0 || line[0] == ';' || line[0] == '[' || eq <= 0) continue;
                string value = line.Substring(eq + 1).Trim();
                if (value.Length > 0) yield return new KeyValuePair<string, string>(line.Substring(0, eq).Trim(), value);
            }
        }

        /// <summary>
        /// Google AI Studio keys: the classic AIza followed by 35 letters, digits, - and _, or the newer kind that
        /// starts with AQ. and runs to about 53 characters.
        /// </summary>
        public static bool LooksLikeGemini(string text)
        {
            if (text == null) return false;
            string t = text.Trim();
            return Regex.IsMatch(t, @"^AIza[0-9A-Za-z_\-]{30,45}$") || Regex.IsMatch(t, @"^AQ\.[0-9A-Za-z_\-\.]{30,90}$");
        }

        /// <summary>TMDB's "API key" is 32 hex digits; its longer "read access token" also works.</summary>
        public static bool LooksLikeTmdb(string text)
        {
            if (text == null) return false;
            string t = text.Trim();
            return Regex.IsMatch(t, "^[0-9a-f]{32}$") || Regex.IsMatch(t, @"^eyJ[\w\-]+\.[\w\-]+\.[\w\-]+$");
        }
    }
}
