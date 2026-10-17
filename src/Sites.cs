using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

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
