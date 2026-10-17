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
    /// (--load-extension). On YouTube and Netflix the remote's arrows move a highlight from video to video and OK opens
    /// one; it also sets YouTube's videos per row and tidies Netflix's home page. Its settings.json is rewritten whenever
    /// one of those settings changes, and the open page reads it again within a couple of seconds.
    /// </summary>
    internal static class Sites
    {
        const string Prefix = "CouchTV.sites.";

        public static string Folder { get { return Path.Combine(Config.DataDir, "sites"); } }

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
                if (!Directory.Exists(Folder)) return;   // written with the rest when a site's next opened
                WriteIfChanged(Path.Combine(Folder, "settings.json"), Encoding.UTF8.GetBytes(SettingsJson(cfg)));
            }
            catch (Exception ex) { Log.Error("Writing the websites' settings", ex); }
        }

        internal static string SettingsJson(Config cfg)
        {
            return "{\"keys\": " + (cfg.SiteArrows ? "true" : "false") +
                   ", \"youtubeRow\": " + cfg.YouTubeRow +
                   ", \"netflixTidy\": " + (cfg.NetflixTidy ? "true" : "false") +
                   ", \"netflixPick\": " + (cfg.NetflixPickProfile && cfg.NetflixProfile.Length > 0 ? "true" : "false") +
                   ", \"netflixProfile\": " + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(cfg.NetflixProfile) + "}\n";
        }

        /// <summary>The browser's command line for loading it (nothing if it isn't written out).</summary>
        public static IEnumerable<string> BrowserArgs()
        {
            if (!File.Exists(Path.Combine(Folder, "manifest.json"))) yield break;
            yield return "--load-extension=\"" + Folder + "\"";
            // Edge, like Chrome, has begun ignoring --load-extension (seen on Edge 155 once a profile's been used); this
            // switch still turns it back on. Brave takes it either way.
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

        /// <summary>
        /// Before a web tile opens: a browser already running on the same profile would take the new window and keep
        /// what it started with: no extension (started before CouchTV loaded it, or by hand), or an older one. Edge keeps
        /// running in the background after its window closes, and a new tile's window joins it at once. So one like that
        /// is closed first, if it has no windows of its own open (it waits a moment for the app window just closed to
        /// go); one with windows open (Brave playing music behind) is left alone, and gets the extension once it closes.
        /// userDataDir: the profile's folder, or null for the browser's own (default) one.
        /// </summary>
        public static void Freshen(string exe, string userDataDir)
        {
            string key = exe + "|" + (userDataDir ?? "");
            try
            {
                DateTime changed = Changed();
                DateTime seen;
                if (Fresh.TryGetValue(key, out seen) && seen >= changed) return;
                string name = Path.GetFileName(exe);
                bool stale = false;
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
                        int pid = Convert.ToInt32(p["ProcessId"]);
                        if (command.Contains("--load-extension=") && started > changed) continue;   // already has it, as it is
                        string what = name + " (running since " + started.ToLocalTime().ToString("HH:mm") + ", " +
                                      (command.Contains("--load-extension=") ? "an older extension" : "no extension") + ")";
                        if (!WindowsGone(pid))
                        {
                            Log.Info("Websites: " + what + " has windows open, so it keeps that until it's closed");
                            stale = true;
                            continue;
                        }
                        Log.Info("Websites: closing " + what + ", left running in the background, so the new window gets the extension");
                        using (Process process = Process.GetProcessById(pid))
                        {
                            process.Kill();
                            process.WaitForExit(5000);
                        }
                        Thread.Sleep(300);   // its helpers and its hold on the profile go with it
                    }
                }
                if (!stale) Fresh[key] = changed;
            }
            catch (Exception ex) { Log.Error("Websites: checking for a browser running from before", ex); }
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
