using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace CouchTV
{
    internal sealed class LaunchResult
    {
        public bool Ok;
        public string Message;   // shown as a toast; set on failure, or as a note on success

        public static LaunchResult Success(string note) { return new LaunchResult { Ok = true, Message = note }; }
        public static LaunchResult Fail(string message) { return new LaunchResult { Ok = false, Message = message }; }
    }

    /// <summary>Starts whatever a tile points at.</summary>
    internal static class Launcher
    {
        public static LaunchResult Open(Config cfg, Tile t)
        {
            try
            {
                switch (t.Kind)
                {
                    case TileKind.Web:
                    case TileKind.Browser:
                        return OpenInBrowser(cfg, t);

                    case TileKind.App:
                        if (!File.Exists(t.Exe)) return LaunchResult.Fail(t.Label + " isn't installed");
                        Start(t.Exe, t.Args ?? "");
                        return LaunchResult.Success(null);

                    case TileKind.Uri:
                        Log.Info("Open: " + t.Url);
                        using (Process.Start(new ProcessStartInfo(t.Url) { UseShellExecute = true })) { }
                        return LaunchResult.Success(null);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Opening " + t.Name, ex);
                return LaunchResult.Fail("Couldn't open " + t.Label);
            }
            return LaunchResult.Fail("Nothing to open for " + t.Label);
        }

        /// <summary>What a tile will run, for the self-test report.</summary>
        public static string Describe(Config cfg, Tile t)
        {
            switch (t.Kind)
            {
                case TileKind.Web:
                case TileKind.Browser:
                    string exe = FindBrowser(cfg, t.Browser);
                    return exe == null ? "(" + t.Browser + " not found)" : "\"" + exe + "\" " + BrowserArgs(t, t.Browser);
                case TileKind.App: return "\"" + t.Exe + "\" " + t.Args + (File.Exists(t.Exe) ? "" : "   (missing)");
                case TileKind.Uri: return t.Url;
                default: return "action: " + t.Action;
            }
        }

        /// <summary>
        /// Where a tile searches, with {q} for the words, or null if it can't. couchtv.ini can set Search = ... (or
        /// off) per tile; the well-known sites work without it.
        /// </summary>
        public static string SearchTemplate(Tile t)
        {
            if (t.Search != null)
            {
                string value = t.Search.Trim();
                return value.Equals("off", StringComparison.OrdinalIgnoreCase) || value.IndexOf("{q}", StringComparison.OrdinalIgnoreCase) < 0 ? null : value;
            }
            if (t.Kind == TileKind.Browser) return "https://www.google.com/search?q={q}";
            Uri uri;
            if (t.Kind != TileKind.Web || !Uri.TryCreate(t.Url, UriKind.Absolute, out uri)) return null;
            string host = uri.Host.ToLowerInvariant();
            if (host.EndsWith("netflix.com")) return "https://www.netflix.com/search?q={q}";
            if (host.EndsWith("youtube.com")) return uri.AbsolutePath.StartsWith("/tv") ? null : "https://www.youtube.com/results?search_query={q}";
            if (host.EndsWith("primevideo.com")) return "https://www.primevideo.com/search/ref=atv_nb_sug?ie=UTF8&phrase={q}";
            if (host.EndsWith("hotstar.com")) return "https://www.hotstar.com/in/explore?search_query={q}";
            if (host.EndsWith("spotify.com")) return "https://open.spotify.com/search/{q}";
            return null;
        }

        public static string SearchUrl(Tile t, string query)
        {
            string template = SearchTemplate(t);
            if (template == null) return null;
            int at = template.IndexOf("{q}", StringComparison.OrdinalIgnoreCase);
            return template.Substring(0, at) + Uri.EscapeDataString(query.Trim()) + template.Substring(at + 3);
        }

        /// <summary>The process name a tile runs as (e.g. "msedge"), or null if it isn't a program.</summary>
        public static string ProcessNameFor(Config cfg, Tile t)
        {
            string exe = t.Kind == TileKind.Web || t.Kind == TileKind.Browser ? FindBrowser(cfg, t.Browser)
                : t.Kind == TileKind.App ? t.Exe : null;
            return exe == null ? null : Path.GetFileNameWithoutExtension(exe);
        }

        static LaunchResult OpenInBrowser(Config cfg, Tile t)
        {
            string browser = t.Browser;
            string exe = FindBrowser(cfg, browser);
            string note = null;
            if (exe == null && browser != "edge")
            {
                exe = FindBrowser(cfg, "edge");
                if (exe != null)
                {
                    note = Title(browser) + " isn't installed, so this opened in Edge";
                    browser = "edge";
                }
            }
            if (exe == null) return LaunchResult.Fail(Title(t.Browser) + " isn't installed");
            Start(exe, BrowserArgs(t, browser));
            return LaunchResult.Success(note);
        }

        /// <summary>
        /// Command line for Chromium-based browsers. Web tiles open as a full-screen app window (no tabs or
        /// address bar). We avoid --kiosk because Edge's kiosk mode wipes cookies on exit, which would log
        /// you out of Netflix every time.
        /// </summary>
        public static string BrowserArgs(Tile t, string browser)
        {
            var args = new List<string>();
            if (!string.Equals(t.Profile, "default", StringComparison.OrdinalIgnoreCase))
            {
                string profile = string.IsNullOrEmpty(t.Profile) || t.Profile.Equals("tv", StringComparison.OrdinalIgnoreCase) ? browser : t.Profile;
                args.Add("--user-data-dir=" + Quote(Path.Combine(Config.DataDir, "profiles", profile)));
            }
            if (t.Kind == TileKind.Web)
            {
                args.Add("--app=" + Quote(t.Url));
                args.Add("--start-fullscreen");
            }
            else
            {
                args.Add("--start-maximized");
            }
            args.Add("--no-first-run");
            args.Add("--no-default-browser-check");
            args.Add("--hide-crash-restore-bubble");
            if (!string.IsNullOrEmpty(t.UserAgent)) args.Add("--user-agent=" + Quote(t.UserAgent));
            if (!string.IsNullOrEmpty(t.Scale)) args.Add("--force-device-scale-factor=" + t.Scale);
            if (!string.IsNullOrEmpty(t.Args)) args.Add(t.Args);
            if (t.Kind == TileKind.Browser && !string.IsNullOrEmpty(t.Url)) args.Add(Quote(t.Url));
            return string.Join(" ", args);
        }

        public static string FindBrowser(Config cfg, string key)
        {
            string configured;
            if (cfg.BrowserPaths.TryGetValue(key, out configured) && File.Exists(configured)) return configured;

            string exe;
            string[] candidates;
            switch (key)
            {
                case "brave":
                    exe = "brave.exe";
                    candidates = new[]
                    {
                        @"%ProgramFiles%\BraveSoftware\Brave-Browser\Application\brave.exe",
                        @"%ProgramFiles(x86)%\BraveSoftware\Brave-Browser\Application\brave.exe",
                        @"%LocalAppData%\BraveSoftware\Brave-Browser\Application\brave.exe",
                    };
                    break;
                case "edge":
                    exe = "msedge.exe";
                    candidates = new[]
                    {
                        @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe",
                        @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe",
                    };
                    break;
                case "chrome":
                    exe = "chrome.exe";
                    candidates = new[]
                    {
                        @"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
                        @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe",
                        @"%LocalAppData%\Google\Chrome\Application\chrome.exe",
                    };
                    break;
                default:
                    return null;
            }

            string registered = FromAppPaths(exe);
            if (registered != null) return registered;
            foreach (string candidate in candidates)
            {
                string path = Environment.ExpandEnvironmentVariables(candidate);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        static string FromAppPaths(string exe)
        {
            foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using (RegistryKey key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe))
                {
                    if (key == null) continue;
                    string path = key.GetValue(null) as string;
                    if (string.IsNullOrEmpty(path)) continue;
                    path = path.Trim().Trim('"');
                    if (File.Exists(path)) return path;
                }
            }
            return null;
        }

        static void Start(string exe, string args)
        {
            Log.Info("Start: \"" + exe + "\" " + args);
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            using (Process.Start(psi)) { }
        }

        static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }

        static string Title(string browser)
        {
            return string.IsNullOrEmpty(browser) ? "The browser" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(browser);
        }
    }
}
