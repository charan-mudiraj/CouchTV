using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;

namespace CouchTV
{
    internal enum TileKind { Web, Browser, App, Uri, Action }

    /// <summary>One entry from couchtv.ini.</summary>
    internal sealed class Tile
    {
        public string Name, Label, Hint;
        public bool IsSystem;
        public TileKind Kind = TileKind.Web;
        public string Url, Browser, Profile, UserAgent, Args, Exe, Action, Scale;
        public string Search;          // search address with {q}, or "off"; null = work it out from Url
        public string Text, ImagePath;
        public char Glyph;
        public FontWeight Weight = FontWeights.Bold;
        public Color Background = Theme.Chip, Background2, Foreground = Theme.Ink;
        public bool HasBackground2;

        /// <summary>The same tile, opening a different address (e.g. its search results).</summary>
        public Tile WithUrl(string url)
        {
            var copy = (Tile)MemberwiseClone();
            copy.Url = url;
            return copy;
        }
    }

    /// <summary>Settings and tiles, read from couchtv.ini next to the exe.</summary>
    internal sealed class Config
    {
        public const string FileName = "couchtv.ini";
        const string ResourceName = "CouchTV.couchtv.ini";

        public readonly List<Tile> AppTiles = new List<Tile>();
        public readonly List<Tile> SystemTiles = new List<Tile>();
        public readonly List<string> HomeKeys = new List<string>();
        public readonly Dictionary<string, string> BrowserPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Warnings = new List<string>();
        public string Wallpaper;
        public string RemotePort;   // optional, e.g. COM5; normally the receiver is found by itself
        public bool Updates = true;
        public string UpdateRepo = "charan-mudiraj/CouchTV", UpdateBranch = "main";
        public bool SmartSearch = true;                       // AI search, when a Gemini key is set
        public string AiModel = "gemini-flash-lite-latest";
        public readonly List<string> Subscriptions = new List<string>();   // tile names you pay for; empty = don't filter
        public string Source;

        public static string AppDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }

        public static string DataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CouchTV"); }
        }

        public static Config Load()
        {
            string path = Path.Combine(AppDir, FileName);
            try
            {
                // First run: write the defaults out so there is a file to edit.
                if (!File.Exists(path)) File.WriteAllText(path, DefaultText());
                return Parse(File.ReadAllText(path), path);
            }
            catch (Exception ex)
            {
                Log.Error("Reading " + path, ex);
                Config fallback = Parse(DefaultText(), "built-in defaults");
                fallback.Warnings.Add("Couldn't read " + path + " - using the defaults");
                return fallback;
            }
        }

        public static Config LoadFile(string path)
        {
            return Parse(File.ReadAllText(path), path);
        }

        public static string DefaultText()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd();
        }

        public static string DefaultRemoteText() { return RemoteMap.DefaultText(); }

        public static Config Parse(string text, string source)
        {
            var cfg = new Config { Source = source };
            foreach (KeyValuePair<string, Dictionary<string, string>> section in ReadIni(text))
            {
                if (section.Key.Equals("CouchTV", StringComparison.OrdinalIgnoreCase))
                {
                    cfg.ReadGlobals(section.Value);
                    continue;
                }
                Tile tile = cfg.ReadTile(section.Key, section.Value);
                if (tile != null) (tile.IsSystem ? cfg.SystemTiles : cfg.AppTiles).Add(tile);
            }

            // Whatever the file says, keep a way out to the normal Windows desktop.
            if (!cfg.SystemTiles.Exists(t => t.Kind == TileKind.Action && t.Action == "desktop"))
            {
                cfg.SystemTiles.Add(new Tile
                {
                    Name = "Windows desktop", Label = "Windows desktop", IsSystem = true,
                    Kind = TileKind.Action, Action = "desktop", Glyph = '',
                });
            }
            if (cfg.HomeKeys.Count == 0) cfg.HomeKeys.AddRange(new[] { "BrowserHome", "Win", "Hold:BrowserBack", "Ctrl+Alt+H" });
            return cfg;
        }

        static List<KeyValuePair<string, Dictionary<string, string>>> ReadIni(string text)
        {
            var sections = new List<KeyValuePair<string, Dictionary<string, string>>>();
            Dictionary<string, string> current = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line.EndsWith("]"))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections.Add(new KeyValuePair<string, Dictionary<string, string>>(line.Substring(1, line.Length - 2).Trim(), current));
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq > 0 && current != null) current[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return sections;
        }

        void ReadGlobals(Dictionary<string, string> v)
        {
            string keys = Get(v, "HomeKeys", null);
            if (keys != null)
            {
                foreach (string k in keys.Split(','))
                    if (k.Trim().Length > 0) HomeKeys.Add(k.Trim());
            }
            Wallpaper = ResolvePath(Get(v, "Wallpaper", null));
            RemotePort = Get(v, "RemotePort", null);
            Updates = GetBool(v, "Updates", true);
            UpdateRepo = Get(v, "UpdateRepo", UpdateRepo);
            UpdateBranch = Get(v, "UpdateBranch", UpdateBranch);
            SmartSearch = GetBool(v, "SmartSearch", true);
            AiModel = Get(v, "AiModel", AiModel);
            string subscriptions = Get(v, "Subscriptions", null);
            if (subscriptions != null)
            {
                foreach (string s in subscriptions.Split(','))
                    if (s.Trim().Length > 0 && !s.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) Subscriptions.Add(s.Trim());
            }
            foreach (string browser in new[] { "Brave", "Edge", "Chrome" })
            {
                string path = Get(v, browser + "Path", null);
                if (path != null) BrowserPaths[browser] = Environment.ExpandEnvironmentVariables(path);
            }
        }

        Tile ReadTile(string name, Dictionary<string, string> v)
        {
            if (!GetBool(v, "Enabled", true)) return null;

            var t = new Tile { Name = name, Label = Get(v, "Label", name), Hint = Get(v, "Hint", null) };
            t.IsSystem = Get(v, "Row", "apps").Equals("system", StringComparison.OrdinalIgnoreCase);

            string type = Get(v, "Type", "web").ToLowerInvariant();
            switch (type)
            {
                case "web": t.Kind = TileKind.Web; break;
                case "browser": t.Kind = TileKind.Browser; break;
                case "app": t.Kind = TileKind.App; break;
                case "uri": t.Kind = TileKind.Uri; break;
                case "action": t.Kind = TileKind.Action; break;
                default:
                    Warnings.Add("[" + name + "] has an unknown Type '" + type + "'");
                    return null;
            }

            t.Url = Get(v, "Url", null);
            t.Browser = Get(v, "Browser", "brave").ToLowerInvariant();
            t.Profile = Get(v, "Profile", "tv");
            t.UserAgent = Get(v, "UserAgent", null);
            t.Args = Get(v, "Args", null);
            t.Scale = Get(v, "Scale", null);
            t.Search = Get(v, "Search", null);
            t.Action = Get(v, "Action", "").ToLowerInvariant();
            t.Text = Get(v, "Text", null);
            t.ImagePath = ResolvePath(Get(v, "Image", null));
            t.Glyph = ParseGlyph(name, Get(v, "Glyph", null));
            t.Weight = ParseWeight(Get(v, "Weight", null), t.Weight);
            t.Background = ParseColor(name, Get(v, "Background", null), t.Background);
            t.Foreground = ParseColor(name, Get(v, "Foreground", null), t.Foreground);
            string background2 = Get(v, "Background2", null);
            if (background2 != null)
            {
                t.Background2 = ParseColor(name, background2, t.Background);
                t.HasBackground2 = true;
            }

            string exe = Get(v, "Exe", null);
            if (exe != null) t.Exe = Environment.ExpandEnvironmentVariables(exe);

            if ((t.Kind == TileKind.Web || t.Kind == TileKind.Uri) && string.IsNullOrEmpty(t.Url))
            {
                Warnings.Add("[" + name + "] needs a Url");
                return null;
            }
            if (t.Kind == TileKind.App)
            {
                if (string.IsNullOrEmpty(t.Exe)) { Warnings.Add("[" + name + "] needs an Exe"); return null; }
                if (GetBool(v, "HideIfMissing", false) && !File.Exists(t.Exe)) return null;
            }
            return t;
        }

        static string Get(Dictionary<string, string> v, string key, string fallback)
        {
            string value;
            return v.TryGetValue(key, out value) && value.Length > 0 ? value : fallback;
        }

        static bool GetBool(Dictionary<string, string> v, string key, bool fallback)
        {
            string value = Get(v, key, null);
            if (value == null) return fallback;
            value = value.ToLowerInvariant();
            return value == "true" || value == "yes" || value == "1" || value == "on";
        }

        static string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            path = Environment.ExpandEnvironmentVariables(path);
            return Path.IsPathRooted(path) ? path : Path.Combine(AppDir, path);
        }

        char ParseGlyph(string tile, string value)
        {
            if (string.IsNullOrEmpty(value)) return '\0';
            string hex = value.Trim();
            if (hex.StartsWith("U+", StringComparison.OrdinalIgnoreCase) || hex.StartsWith("\\u", StringComparison.OrdinalIgnoreCase)) hex = hex.Substring(2);
            int code;
            if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code) && code > 0 && code <= 0xFFFF) return (char)code;
            Warnings.Add("[" + tile + "] Glyph should be a hex code like E768");
            return '\0';
        }

        Color ParseColor(string tile, string value, Color fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            try { return (Color)ColorConverter.ConvertFromString(value); }
            catch (Exception)
            {
                Warnings.Add("[" + tile + "] has an invalid colour '" + value + "'");
                return fallback;
            }
        }

        static FontWeight ParseWeight(string value, FontWeight fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            try { return (FontWeight)new FontWeightConverter().ConvertFromString(value); }
            catch (Exception) { return fallback; }
        }
    }
}
