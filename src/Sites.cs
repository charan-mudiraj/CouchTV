using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CouchTV
{
    /// <summary>
    /// CouchTV's touches for websites (the sites folder in the source): a small browser extension, built into
    /// CouchTV.exe and written out to %LOCALAPPDATA%\CouchTV\sites, that the browser for a web tile loads
    /// (--load-extension) when it runs on its normal profile (YouTube in Brave). On one of CouchTV's own profiles
    /// (Netflix in Edge, which no longer takes --load-extension), the same scripts go in through DevTools instead
    /// (PageScripts.cs). On YouTube and Netflix the remote's arrows move a highlight from video to video and OK opens
    /// one; it also sets YouTube's videos per row and tidies Netflix's home page. Its settings.json is rewritten whenever
    /// one of those settings changes, and the open page reads it again within a couple of seconds.
    /// </summary>
    internal static class Sites
    {
        const string Prefix = "CouchTV.sites.";

        public static string Folder { get { return Path.Combine(Config.DataDir, "sites"); } }

        /// <summary>The settings as the pages read them (settings.json), as last written.</summary>
        public static string CurrentSettings { get; private set; }

        /// <summary>The extension, written out (only files that changed) with today's settings; false if it couldn't be.</summary>
        public static bool Prepare(Config cfg)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                Assembly assembly = Assembly.GetExecutingAssembly();
                foreach (string name in assembly.GetManifestResourceNames())
                {
                    if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                    using (Stream stream = assembly.GetManifestResourceStream(name))
                    using (var memory = new MemoryStream())
                    {
                        stream.CopyTo(memory);
                        WriteIfChanged(Path.Combine(Folder, name.Substring(Prefix.Length)), memory.ToArray());
                    }
                }
                WriteSettings(cfg);
                return File.Exists(Path.Combine(Folder, "manifest.json"));
            }
            catch (Exception ex)
            {
                Log.Error("Writing the websites' touches to " + Folder, ex);
                return false;
            }
        }

        /// <summary>settings.json, for the pages: after a setting changes, they pick it up by themselves.</summary>
        public static void WriteSettings(Config cfg)
        {
            try
            {
                string json = SettingsJson(cfg);
                bool changed = CurrentSettings != null && CurrentSettings != json;
                CurrentSettings = json;
                if (changed) PageScripts.SettingsChanged(json);   // pages put in through DevTools
                if (!Directory.Exists(Folder)) return;   // written with the rest when a site's next opened
                WriteIfChanged(Path.Combine(Folder, "settings.json"), Encoding.UTF8.GetBytes(json));
            }
            catch (Exception ex) { Log.Error("Writing the websites' settings", ex); }
        }

        internal static string SettingsJson(Config cfg)
        {
            return "{\"keys\": " + (cfg.SiteArrows ? "true" : "false") +
                   ", \"youtubeRow\": " + cfg.YouTubeRow +
                   ", \"netflixTidy\": " + (cfg.NetflixTidy ? "true" : "false") +
                   ", \"netflixPick\": " + (cfg.NetflixPickProfile && cfg.NetflixProfile.Length > 0 ? "true" : "false") +
                   ", \"netflixProfile\": " + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(cfg.NetflixProfile) +
                   ", \"hotstarAds\": " + (cfg.HotstarAds ? "true" : "false") + "}\n";
        }

        /// <summary>Whether JioHotstar's ads are kept off its films and shows (HotstarAds), as last written.</summary>
        public static bool BlockHotstarAds
        {
            get { string s = CurrentSettings; return s == null || s.Contains("\"hotstarAds\": true"); }
        }

        /// <summary>
        /// JioHotstar's ad servers, for Edge to refuse on its pages (PageScripts): from uBlock Origin's and EasyList
        /// India's filters for the site.
        /// </summary>
        public static readonly string[] HotstarAdServers =
        {
            "*://hesads.akamaized.net/*", "*://videoads.hotstar.com/*", "*://*.hotstar.com/*preroll*", "*://*.hotstar.com/*midroll*",
            "*://*.videoplaza.tv/proxy/distributor/*",
        };

        /// <summary>
        /// The browser's command line for it: on one of CouchTV's own profiles (userDataDir), a DevTools port for
        /// PageScripts.cs; on the browser's normal profile (null), the extension (nothing if it isn't written out).
        /// </summary>
        public static IEnumerable<string> BrowserArgs(string userDataDir)
        {
            if (userDataDir != null)
            {
                yield return "--remote-debugging-port=0";   // a free port, on this PC only
                yield break;
            }
            if (!File.Exists(Path.Combine(Folder, "manifest.json"))) yield break;
            yield return "--load-extension=\"" + Folder + "\"";
            // Chrome and Edge have begun ignoring --load-extension; this switch turns it back on in some (Edge 155 on
            // the laptop, not on the TV). Brave takes it either way.
            yield return "--disable-features=DisableLoadExtensionCommandLineSwitch";
        }

        // Browsers (exe and profile) seen to be running with the extension as it now is, this run of CouchTV.
        static readonly Dictionary<string, DateTime> Fresh = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        static readonly Regex DataDir = new Regex("--user-data-dir=(?:\"([^\"]+)\"|(\\S+))", RegexOptions.IgnoreCase);

        /// <summary>When the extension's own files (not its settings) last changed.</summary>
        static DateTime Changed()
        {
            DateTime last = DateTime.MinValue;
            foreach (string file in Directory.GetFiles(Folder))
                if (!file.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase)) last = Max(last, File.GetLastWriteTimeUtc(file));
            return last;
        }

        static DateTime Max(DateTime a, DateTime b) { return a > b ? a : b; }

        /// <summary>A browser left running that would take a new window and ignore what's asked of it.</summary>
        internal sealed class StaleBrowser
        {
            public int Pid;
            public string What;
        }

        /// <summary>
        /// Before a web tile opens: a browser already running on the same profile would take the new window and keep
        /// what it started with: no extension or DevTools port (started before CouchTV asked for them, or by hand), or
        /// an older extension. Edge keeps running in the background after its window closes, and a new tile's window
        /// joins it at once. Quick (one look at the running programs, once a run unless the extension changes); null or
        /// empty if there's none. Close() then closes them, which takes a moment.
        /// userDataDir: the profile's folder, or null for the browser's own (default) one.
        /// </summary>
        public static List<StaleBrowser> Stale(string exe, string userDataDir)
        {
            string key = exe + "|" + (userDataDir ?? "");
            var stale = new List<StaleBrowser>();
            try
            {
                DateTime changed = Changed();
                DateTime seen;
                if (Fresh.TryGetValue(key, out seen) && seen >= changed) return stale;
                string name = Path.GetFileName(exe);
                using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine, CreationDate FROM Win32_Process WHERE Name = '" + name.Replace("'", "") + "'"))
                {
                    foreach (ManagementBaseObject p in searcher.Get())
                    {
                        string command = (p["CommandLine"] as string) ?? "";
                        if (command.Contains("--type=")) continue;   // a helper (a tab, the GPU), not the browser itself
                        Match dir = DataDir.Match(command);
                        string its = dir.Success ? (dir.Groups[1].Success ? dir.Groups[1].Value : dir.Groups[2].Value) : null;
                        if (!SameFolder(its, userDataDir)) continue;
                        DateTime started = ManagementDateTimeConverter.ToDateTime((string)p["CreationDate"]).ToUniversalTime();
                        string what;
                        if (userDataDir != null)
                        {
                            // CouchTV's own profile: it needs its DevTools port open (the scripts come from CouchTV, never stale).
                            if (command.Contains("--remote-debugging-port")) continue;
                            what = name + " (running since " + started.ToLocalTime().ToString("HH:mm") + ", without its DevTools port)";
                        }
                        else
                        {
                            if (command.Contains("--load-extension=") && started > changed) continue;   // already has it, as it is
                            what = name + " (running since " + started.ToLocalTime().ToString("HH:mm") + ", " +
                                   (command.Contains("--load-extension=") ? "an older extension" : "no extension") + ")";
                        }
                        stale.Add(new StaleBrowser { Pid = Convert.ToInt32(p["ProcessId"]), What = what });
                    }
                }
                if (stale.Count == 0) Fresh[key] = changed;
            }
            catch (Exception ex) { Log.Error("Websites: checking for a browser running from before", ex); }
            return stale;
        }

        /// <summary>
        /// Closes them, each once it has no windows of its own open (waiting a moment for the app window just closed to
        /// go); one with windows open (Brave playing music behind) is left alone, and gets the touches once it closes.
        /// Slow: call off the UI thread.
        /// </summary>
        public static void Close(List<StaleBrowser> stale, string exe, string userDataDir)
        {
            bool left = false;
            foreach (StaleBrowser browser in stale)
            {
                try
                {
                    if (!WindowsGone(browser.Pid))
                    {
                        Log.Info("Websites: " + browser.What + " has windows open, so it keeps that until it's closed");
                        left = true;
                        continue;
                    }
                    Log.Info("Websites: closing " + browser.What + ", left running in the background, so the new window gets the touches");
                    using (Process process = Process.GetProcessById(browser.Pid))
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                    Thread.Sleep(300);   // its helpers and its hold on the profile go with it
                }
                catch (ArgumentException) { /* gone already */ }
                catch (Exception ex) { Log.Error("Websites: closing " + browser.What, ex); left = true; }
            }
            if (!left) lock (Fresh) Fresh[exe + "|" + (userDataDir ?? "")] = Changed();
        }

        static bool SameFolder(string a, string b)
        {
            if (a == null || b == null) return a == null && b == null;
            return string.Equals(a.Trim().TrimEnd('\\'), b.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True once the process has no windows showing (waiting up to 2.5 s for ones being closed).</summary>
        static bool WindowsGone(int pid)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                bool any = false;
                Native.EnumWindows((hwnd, l) =>
                {
                    uint owner;
                    Native.GetWindowThreadProcessId(hwnd, out owner);
                    if (owner == pid && Native.IsWindowVisible(hwnd) && Native.GetWindowTextLength(hwnd) > 0) any = true;
                    return !any;
                }, IntPtr.Zero);
                if (!any) return true;
                if (clock.ElapsedMilliseconds > 2500) return false;
                Thread.Sleep(150);
            }
        }

        // ------------------------------------------------------------------ the scripts, for DevTools

        static readonly Dictionary<string, string> Texts = new Dictionary<string, string>();

        static string Text(string file)
        {
            lock (Texts)
            {
                string text;
                if (Texts.TryGetValue(file, out text)) return text;
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Prefix + file))
                using (var reader = new StreamReader(stream, Encoding.UTF8)) text = reader.ReadToEnd();
                return Texts[file] = text;
            }
        }

        /// <summary>
        /// The scripts as one, for a page's own world beside its scripts: tv.js and the site's (by the page's address, as
        /// manifest.json matches them), once the page is parsed, with the settings to start with.
        /// </summary>
        public static string Bundle(string settings)
        {
            return "(function () {\n" +
                   "if (window.__couchtvBundle) return;\n" +
                   "window.__couchtvBundle = true;\n" +
                   "var site = /(^|\\.)netflix\\.com$/.test(location.hostname) ? 'netflix'\n" +
                   "  : location.hostname === 'www.youtube.com' && location.pathname.indexOf('/tv') !== 0 ? 'youtube' : null;\n" +
                   "if (!site) return;\n" +
                   "function go() {\n" +
                   "window.__couchtvSettings = " + (settings ?? "null").Trim() + ";\n" +
                   Text("tv.js") + "\n" +
                   "if (site === 'netflix') {\n" + Text("netflix.js") + "\n}\n" +
                   "if (site === 'youtube') {\n" + Text("youtube.js") + "\n}\n" +
                   "}\n" +
                   "if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', go); else go();\n" +
                   "})();\n";
        }

        /// <summary>youtube-page.js, for the page's own world (it reaches YouTube's grid), on YouTube only.</summary>
        public static string PageBundle()
        {
            return "if (location.hostname === 'www.youtube.com' && location.pathname.indexOf('/tv') !== 0 && !window.__couchtvPage) {\n" +
                   "window.__couchtvPage = true;\n" + Text("youtube-page.js") + "\n}\n" +
                   (BlockHotstarAds ? "if (/(^|\\.)hotstar\\.com$/.test(location.hostname) && !window.__couchtvAds) {\n" +
                                      "window.__couchtvAds = true;\n" + Text("hotstar-page.js") + "\n}\n" : "");
        }

        static void WriteIfChanged(string path, byte[] bytes)
        {
            if (File.Exists(path))
            {
                byte[] old = File.ReadAllBytes(path);
                if (old.Length == bytes.Length && Same(old, bytes)) return;
            }
            File.WriteAllBytes(path, bytes);
        }

        static bool Same(byte[] a, byte[] b)
        {
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
