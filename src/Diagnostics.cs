using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CouchTV
{
    /// <summary>Command-line checks used while building and installing: screenshots and a self-test report.</summary>
    internal static class Diagnostics
    {
        /// <summary>CouchTV.exe --screenshot out.png [--size 1366x768] [--focus row,col] [--state toast,launch,confirm,osd,update,sleep] [--config file]</summary>
        public static int Screenshot(string[] args, string path)
        {
            int width = 1920, height = 1080, row = 0, col = 0;
            string size = Program.Arg(args, "--size");
            if (size != null)
            {
                string[] parts = size.Split('x');
                width = int.Parse(parts[0]);
                height = int.Parse(parts[1]);
            }
            string focus = Program.Arg(args, "--focus");
            if (focus != null)
            {
                string[] parts = focus.Split(',');
                row = int.Parse(parts[0]);
                col = int.Parse(parts[1]);
            }
            string configPath = Program.Arg(args, "--config");
            Config cfg = configPath != null ? Config.LoadFile(configPath) : Config.Load();

            var window = new HomeWindow(cfg, new HomeOptions { Preview = true });
            FrameworkElement root = window.PreparePreview(row, col, Program.Arg(args, "--state") ?? "");
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream file = File.Create(path)) png.Save(file);
            return 0;
        }

        /// <summary>
        /// CouchTV.exe --updatetest report.txt [--installed sha] [--prepare]: checks GitHub as if the given commit were
        /// installed, and with --prepare also downloads, builds and start-checks the update (without installing it).
        /// </summary>
        public static int UpdateTest(string[] args, string path)
        {
            var report = new StringBuilder();
            Config cfg = Config.Load();
            string installed = Program.Arg(args, "--installed");
            int code = 0;
            try
            {
                report.AppendLine("Repo: " + cfg.UpdateRepo + " (" + cfg.UpdateBranch + "), installed: " + (installed ?? "unknown"));
                AppUpdateInfo update = AppUpdate.Check(cfg.UpdateRepo, cfg.UpdateBranch, installed);
                if (update == null) report.AppendLine("Result: up to date");
                else report.AppendLine("Result: update " + update.Sha + " (" + update.NewCommits + " new) “" + update.Title + "” " + update.Date.ToString("u"));
                AppUpdateInfo again = AppUpdate.Check(cfg.UpdateRepo, cfg.UpdateBranch, installed);   // exercises the not-modified path
                report.AppendLine("Second check agrees: " + ((again == null) == (update == null)));
                if (Program.Has(args, "--prepare") && update != null)
                {
                    PreparedUpdate prepared = AppUpdate.Prepare(cfg.UpdateRepo, update, step => report.AppendLine("  step: " + step));
                    report.AppendLine("Prepared: source " + prepared.Source + ", build " + prepared.Build);
                }
            }
            catch (Exception ex)
            {
                report.AppendLine("FAILED: " + ex.Message);
                code = 1;
            }
            File.WriteAllText(path, report.ToString());
            return code;
        }

        /// <summary>CouchTV.exe --remotetest report.txt: checks the remote-button logic without a receiver.</summary>
        public static int RemoteTest(string path)
        {
            var report = new StringBuilder();
            int failures = 0;
            Action<string, bool> check = (name, ok) =>
            {
                report.AppendLine((ok ? "PASS  " : "FAIL  ") + name);
                if (!ok) failures++;
            };

            IrSignal press = IrLink.ParseLine("IR,NEC 00CE 0001,N");
            IrSignal held = IrLink.ParseLine("IR,Samsung 0707 0002,R");
            check("parse a press", press != null && press.Code == "NEC 00CE 0001" && !press.IsRepeat);
            check("parse a repeat", held != null && held.Code == "Samsung 0707 0002" && held.IsRepeat);
            check("parse a universal-decoder code", IrLink.ParseLine("IR,PulseDistance 1A2B3C4D/48,N").Code == "PulseDistance 1A2B3C4D/48");
            check("ignore other lines", IrLink.ParseLine("COUCHIR 1 0") == null && IrLink.ParseLine("OK") == null);

            RemoteMap map = RemoteMap.FromText(Config.DefaultRemoteText());
            check("phone remote: up", map.ActionFor("NEC 00CE 0001") == "up");
            check("hand-typed code with extra spaces and lower case", map.ActionFor("nec  00ce   0001") == "up");
            check("open a tile", map.ActionFor("NEC 00CE 0040") == "open:Netflix");
            check("unknown button", map.ActionFor("Sony 0001 0015") == null);
            check("one power button", map.CodesFor("power").Count == 1);
            // Must match hashCode() in the receiver firmware (FNV-1a of the upper-cased code text).
            report.AppendLine("      wake hash of 'NEC 00CE 0020' = " + IrLink.Hash("NEC 00CE 0020").ToString("X8"));

            string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "couchtv-remote-test.ini");
            File.WriteAllText(file, "; my comment\r\n[Buttons]\r\nNEC 00CE 0001 = up\r\nNEC 00CE 0002 = down\r\n");
            RemoteMap saved = RemoteMap.LoadFile(file);
            saved.Save(new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Samsung 0707 0060", "up"),
                new KeyValuePair<string, string>("NEC 00CE 0002", "volup"),
            });
            RemoteMap reread = RemoteMap.LoadFile(file);
            string text = File.ReadAllText(file);
            check("save: new remote added", reread.ActionFor("Samsung 0707 0060") == "up");
            check("save: other buttons kept", reread.ActionFor("NEC 00CE 0001") == "up");
            check("save: reassigned button replaced, not duplicated", reread.ActionFor("NEC 00CE 0002") == "volup" && text.IndexOf("= down") < 0);
            check("save: comments kept", text.Contains("; my comment"));
            File.Delete(file);

            check("phone voice search button works with an old remote.ini", RemoteMap.FromText("[Buttons]\nNEC 00CE 0001 = up\n").ActionFor("NEC 00CE 0051") == "voicesearch");
            check("remote.ini can remap the voice search button", RemoteMap.FromText("[Buttons]\nNEC 00CE 0051 = home\n").ActionFor("NEC 00CE 0051") == "home");

            // ----- text over infrared (voice search), as the receiver reports it
            check("CRC-16/CCITT-FALSE of \"123456789\" is 29B1", IrText.Crc16(Encoding.ASCII.GetBytes("123456789")) == 0x29B1);
            bool packOk = true;
            for (int b = 0; b < 256; b++)
            {
                if (b == 0xC0 || b == 0xC1) continue;   // never in UTF-8, and not used as markers
                byte packed = IrText.Pack((byte)b);
                if (packed == 0x00 || packed == 0x31 || IrText.Unpack(packed) != b) packOk = false;
            }
            check("packed byte is never 00 or 31, and unpacks back", packOk);

            string[] samples =
            {
                "panchayat season 3", "1", "11", "Mirzapur 2", "पंचायत", "Ωμέγα", "🎬 movies", "a",
                "the family man season 2 hindi dubbed full episodes watch online free 1080p", "  spaces  ",
                "aΩ", "a" + char.ConvertFromUtf32(0x40000),   // CE and F1 as the second byte of a frame
            };
            foreach (string sample in samples)
            {
                List<string> codes = TextAsReceived(sample);
                List<string> frames = codes.GetRange(1, codes.Count - 1);   // after the start button
                bool looksLikeButton = frames.Exists(c => c.Contains(" 00CE ")) || frames.Exists(c => !c.StartsWith("NEC"));
                check("text \"" + sample + "\" never looks like a phone button", !looksLikeButton);
                check("text \"" + sample + "\" arrives intact (" + (codes.Count - 1) + " frames)", Decode(codes) == sample.Trim());
            }

            List<string> broken = TextAsReceived("panchayat");
            broken[3] = broken[3].Substring(0, broken[3].Length - 2) + "77";   // one garbled byte
            check("a garbled frame is caught", Decode(broken) == "FAILED");
            List<string> missing = TextAsReceived("panchayat");
            missing.RemoveAt(2);
            check("a missing frame is caught", Decode(missing) == "FAILED");
            var idle = new IrText();
            check("xxCE codes from other remotes are buttons when no text is coming", idle.Handle("NEC 12CE 0005", DateTime.Now) == IrTextResult.NotText);
            check("the phone's buttons are never text", idle.Handle("NEC 00CE 0001", DateTime.Now) == IrTextResult.NotText);
            var stalled = new IrText();
            DateTime t0 = DateTime.Now;
            stalled.Handle("NEC 00CE 0052", t0);
            stalled.Handle(TextAsReceived("abc")[1], t0);
            check("text that stops arriving times out", !stalled.TimedOut(t0.AddMilliseconds(300)) && stalled.TimedOut(t0.AddMilliseconds(900)));

            // ----- where each app searches
            Config defaults = Config.Parse(Config.DefaultText(), "built-in defaults");
            Tile netflix = defaults.AppTiles.Find(x => x.Name == "Netflix");
            Tile youtube = defaults.AppTiles.Find(x => x.Name == "YouTube");
            Tile web = defaults.AppTiles.Find(x => x.Name == "Web");
            check("Netflix search address", Launcher.SearchUrl(netflix, " panchayat season 3 ") == "https://www.netflix.com/search?q=panchayat%20season%203");
            check("YouTube search address", Launcher.SearchUrl(youtube, "पंचायत").StartsWith("https://www.youtube.com/results?search_query=%E0%A4%AA"));
            check("Web searches Google", Launcher.SearchUrl(web, "a&b") == "https://www.google.com/search?q=a%26b");
            check("Prime Video and JioHotstar can search", defaults.AppTiles.FindAll(x => Launcher.SearchTemplate(x) != null).Count >= 5);
            check("Search = off hides an app", Launcher.SearchTemplate(new Tile { Kind = TileKind.Web, Url = "https://www.netflix.com", Search = "off" }) == null);
            check("Search = <address> sets it", Launcher.SearchUrl(new Tile { Kind = TileKind.Web, Url = "https://x.example", Search = "https://x.example/find?w={q}" }, "a b") == "https://x.example/find?w=a%20b");
            check("apps without search are left out", Launcher.SearchTemplate(new Tile { Kind = TileKind.App, Exe = "vlc.exe" }) == null);

            // ----- AI search, without the network: canned answers from Gemini and TMDB
            string gemini = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"thinking...\",\"thought\":true},{\"text\":" +
                "\"{\\\"intent\\\":\\\"watch\\\",\\\"title\\\":\\\"Panchayat\\\",\\\"kind\\\":\\\"tv\\\",\\\"year\\\":2020,\\\"season\\\":3," +
                "\\\"search_text\\\":\\\"Panchayat\\\",\\\"apps\\\":[\\\"Prime Video\\\"],\\\"action\\\":\\\"none\\\",\\\"value\\\":0}\"}]},\"finishReason\":\"STOP\"}]}";
            SearchIntent understood = SmartSearch.ParseIntent(SmartSearch.AnswerText(gemini));
            check("AI answer: thought parts skipped, fields read", understood.Intent == "watch" && understood.Title == "Panchayat" && understood.Kind == "tv"
                && understood.Year == 2020 && understood.Season == 3 && understood.Apps.Count == 1 && understood.Apps[0] == "Prime Video");

            string tmdbSearch = "{\"results\":[{\"media_type\":\"movie\",\"id\":1,\"title\":\"Panchayat\",\"release_date\":\"2015-05-01\"}," +
                "{\"media_type\":\"person\",\"id\":2,\"name\":\"Panchayat\"},{\"media_type\":\"tv\",\"id\":3,\"name\":\"Panchayat\",\"first_air_date\":\"2020-04-03\"}]}";
            Dictionary<string, object> match = SmartSearch.BestMatch(tmdbSearch, understood);
            check("TMDB: the 2020 series wins over a 2015 film of the same name", match != null && Convert.ToInt32(match["id"]) == 3);

            string providers = "{\"results\":{\"US\":{\"flatrate\":[{\"provider_name\":\"Netflix\"}]},\"IN\":{" +
                "\"flatrate\":[{\"provider_name\":\"Amazon Prime Video\"}],\"ads\":[{\"provider_name\":\"JioHotstar\"}],\"rent\":[{\"provider_name\":\"YouTube\"}]}}}";
            List<KeyValuePair<Tile, string>> where = SmartSearch.ProvidersToApps(providers, defaults.AppTiles);
            check("TMDB: India's providers map to tiles, streaming first, rentals left out",
                where.Count == 2 && where[0].Key.Name == "Prime Video" && where[0].Value == "Streams here" && where[1].Key.Name == "JioHotstar");
            check("TMDB: rent or buy only when nothing streams",
                SmartSearch.ProvidersToApps("{\"results\":{\"IN\":{\"rent\":[{\"provider_name\":\"YouTube\"}]}}}", defaults.AppTiles).Count == 1);

            var noKeys = new ApiKeys();
            check("\"open netflix\" opens it", SmartSearch.OpenCommand("open netflix", defaults.AppTiles) == netflix);
            check("\"youtube kholo\" opens it", SmartSearch.OpenCommand("youtube kholo", defaults.AppTiles) == youtube);
            check("\"open season\" isn't an app", SmartSearch.OpenCommand("open season", defaults.AppTiles) == null);
            SearchPlan direct = SmartSearch.Plan("Netflix", defaults, noKeys);
            check("one app named: one option, no AI needed", direct.Options.Count == 1 && direct.Options[0].Kind == SearchKind.OpenApp);
            SearchPlan offline = SmartSearch.Plan("arijit singh songs", defaults, noKeys);
            check("no key: songs still go to YouTube", offline.Options.Count == 1 && offline.Options[0].Tile == youtube && offline.Note != null);
            SearchPlan onApp = SmartSearch.Plan("kota factory on netflix", defaults, noKeys);
            check("no key: \"<title> on netflix\" searches Netflix for the title", onApp.Options.Count == 1 && onApp.Options[0].Tile == netflix && onApp.Options[0].Query == "kota factory");
            check("no key: anything else falls back to the app row", SmartSearch.Plan("panchayat", defaults, noKeys).Options.Count == 0);

            var plan = new SearchPlan();
            var sleepIntent = new SearchIntent { Intent = "action", Action = "sleep_timer", Value = 30 };
            SmartSearch.Build(plan, sleepIntent, "aadhe ghante baad band", defaults, noKeys);
            check("action: sleep timer 30 minutes", plan.Options.Count == 1 && plan.Options[0].Action == "sleeptimer" && plan.Options[0].Value == 30);
            var guess = new SearchIntent { Intent = "watch", Title = "Panchayat", SearchText = "Panchayat" };
            guess.Apps.Add("Prime Video");
            guess.Apps.Add("Netflix");
            plan = new SearchPlan();
            SmartSearch.Build(plan, guess, "panchayat", defaults, noKeys);
            check("no TMDB key: the AI's guesses become the options", plan.Options.Count == 2 && plan.Options[0].Tile.Name == "Prime Video" && plan.Options[0].Query == "Panchayat");
            Config subscribed = Config.Parse(System.Text.RegularExpressions.Regex.Replace(Config.DefaultText(), @"(?m)^Subscriptions =.*$", "Subscriptions = Netflix"), "test");
            plan = new SearchPlan();
            SmartSearch.Build(plan, guess, "panchayat", subscribed, noKeys);
            check("Subscriptions = Netflix drops apps you don't pay for", plan.Options.Count == 1 && plan.Options[0].Tile.Name == "Netflix");

            // Made-up keys, built here so GitHub's secret scanning doesn't mistake the source for a real one.
            string oldStyle = "AI" + "za" + new string('x', 35), newStyle = "AQ" + "." + new string('x', 25) + "_" + new string('y', 24);
            check("a pasted Gemini key is recognised, old and new formats", ApiKeys.LooksLikeGemini(oldStyle) && ApiKeys.LooksLikeGemini(newStyle)
                && !ApiKeys.LooksLikeGemini("aiza panchayat") && !ApiKeys.LooksLikeGemini("AQ. panchayat season 3"));
            check("a pasted TMDB key is recognised", ApiKeys.LooksLikeTmdb(new string('a', 16) + new string('0', 16)) && !ApiKeys.LooksLikeTmdb("panchayat season 3"));

            report.AppendLine(failures == 0 ? "All passed" : failures + " failed");
            File.WriteAllText(path, report.ToString());
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// CouchTV.exe --searchtest report.txt "query" ["query" ...]: asks the real AI search (with this PC's keys.ini)
        /// and reports what it understood and what it would offer.
        /// </summary>
        public static int SearchTest(string[] args, string path)
        {
            var report = new StringBuilder();
            Config cfg = Config.Load();
            ApiKeys keys = ApiKeys.Load();
            report.AppendLine("Gemini key: " + (string.IsNullOrEmpty(keys.Gemini) ? "missing" : "set") + ", TMDB key: " + (string.IsNullOrEmpty(keys.Tmdb) ? "missing" : "set") +
                              ", model: " + cfg.AiModel + ", subscriptions: " + (cfg.Subscriptions.Count == 0 ? "none" : string.Join(", ", cfg.Subscriptions)));
            int start = Array.IndexOf(args, "--searchtest") + 2;
            for (int i = start; i < args.Length; i++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                SearchPlan plan = SmartSearch.Plan(args[i], cfg, keys);
                report.AppendLine();
                report.AppendLine("\"" + args[i] + "\"  (" + watch.ElapsedMilliseconds + " ms, " + plan.Source + (plan.UsedTmdb ? " + TMDB" : "") + ")");
                if (plan.Raw != null) report.AppendLine("  AI:      " + plan.Raw.Replace("\n", " "));
                report.AppendLine("  Summary: " + plan.Summary);
                report.AppendLine("  Options: " + SmartSearch.Describe(plan) + (plan.Options.Count == 1 ? "  -> opens by itself" : plan.Options.Count > 1 ? "  -> you choose" : ""));
                if (plan.Note != null) report.AppendLine("  Note:    " + plan.Note);
            }
            File.WriteAllText(path, report.ToString(), Encoding.UTF8);
            return 0;
        }

        /// <summary>
        /// The receiver's report for each frame the phone sends for a text: the start button, then the frames sent
        /// 108 ms apart. Mirrors decodeNEC() in IRremote 4.x and describe() in CouchIR.ino: an 8-bit address when
        /// byte 1 is ~byte 0, an 8-bit command when byte 3 is ~byte 2 (else Onkyo), and NEC2 for a frame that
        /// follows the previous one within 70 ms.
        /// </summary>
        static List<string> TextAsReceived(string text)
        {
            var codes = new List<string> { IrText.StartCode };
            foreach (byte[] f in IrText.Encode(text))
            {
                int address = f[0] == (byte)~f[1] ? f[0] : f[0] | (f[1] << 8);
                bool nec = f[2] == (byte)~f[3];
                int command = nec ? f[2] : f[2] | (f[3] << 8);
                string protocol = nec ? "NEC2" : "Onkyo";   // every frame after the start button comes quickly
                codes.Add(string.Format("{0} {1:X4} {2:X4}", protocol, address, command));
            }
            return codes;
        }

        static string Decode(List<string> codes)
        {
            var text = new IrText();
            DateTime now = DateTime.Now;
            foreach (string code in codes)
            {
                IrTextResult result = text.Handle(code, now);
                if (result == IrTextResult.Done) return text.Text;
                if (result == IrTextResult.Failed || result == IrTextResult.NotText) return "FAILED";
            }
            return "FAILED";
        }

        /// <summary>CouchTV.exe --selftest report.txt: what the launcher sees on this PC, without changing anything.</summary>
        public static int SelfTest(string path)
        {
            var report = new StringBuilder();
            Config cfg = Config.Load();
            report.AppendLine("Config: " + cfg.Source);
            foreach (string warning in cfg.Warnings) report.AppendLine("  warning: " + warning);
            report.AppendLine("Mode now: " + (Shell.ExplorerRunning ? "desktop (Explorer running)" : "TV (no Explorer)"));
            report.AppendLine("Windows update waiting for restart: " + Shell.UpdateWaiting());

            float level;
            bool muted;
            report.AppendLine("Audio: " + (Audio.Change(0, false, out level, out muted)
                ? Math.Round(level * 100) + "%" + (muted ? " (muted)" : "")
                : "no output device"));

            foreach (string browser in new[] { "brave", "edge", "chrome" })
                report.AppendLine("Browser " + browser + ": " + (Launcher.FindBrowser(cfg, browser) ?? "not found"));

            foreach (string name in cfg.HomeKeys)
            {
                HotkeySpec spec = Hotkeys.Parse(name);
                report.AppendLine("Home key '" + name + "': " + (spec == null ? "NOT RECOGNISED"
                    : spec.IsWinTap ? "tap of the Windows key"
                    : spec.IsHold ? "hold vk 0x" + spec.Vk.ToString("X2") + " for 0.7 s"
                    : "vk 0x" + spec.Vk.ToString("X2") + ", modifiers " + spec.Modifiers));
            }

            var tiles = new List<Tile>(cfg.AppTiles);
            tiles.AddRange(cfg.SystemTiles);
            foreach (Tile t in tiles)
                report.AppendLine(string.Format("[{0}] {1,-16} {2,-8} {3}", t.IsSystem ? "system" : "apps  ", t.Label, t.Kind, Launcher.Describe(cfg, t)));

            report.AppendLine("Installed: " + (AppUpdate.IsInstalled ? AppUpdate.InstalledLabel : "not installed (no version.txt), so no update checks") +
                              "; updates from " + cfg.UpdateRepo + " (" + cfg.UpdateBranch + ")" + (cfg.Updates ? "" : ", turned off"));
            try
            {
                AppUpdateInfo update = AppUpdate.Check(cfg.UpdateRepo, cfg.UpdateBranch, AppUpdate.InstalledSha);
                report.AppendLine("Update: " + (update == null ? "none, up to date" : update.ShortSha + " “" + update.Title + "”"));
            }
            catch (Exception ex) { report.AppendLine("Update check failed: " + ex.Message); }
            string receiver = IrLink.Probe(cfg.RemotePort);
            report.AppendLine("IR receiver: " + (receiver ?? "not found (unplugged, or CouchTV is running and using it)"));
            RemoteMap remote = RemoteMap.Load();
            report.AppendLine("Remote buttons in remote.ini: " + remote.Count + ", of which wake the PC: " + remote.CodesFor("power").Count);
            ApiKeys keys = ApiKeys.Load();
            report.AppendLine("AI search: " + (cfg.SmartSearch ? "on" : "off") + ", Gemini key " + (string.IsNullOrEmpty(keys.Gemini) ? "missing" : "set") +
                              ", TMDB key " + (string.IsNullOrEmpty(keys.Tmdb) ? "missing" : "set") + " (" + ApiKeys.FilePath + "), model " + cfg.AiModel);

            var owners = new List<string>();
            Shell.FindAppWindows(IntPtr.Zero, owners);
            report.AppendLine("Home in TV mode would close windows from: " + (owners.Count == 0 ? "(none)" : string.Join(", ", owners)));
            File.WriteAllText(path, report.ToString());
            return 0;
        }
    }
}
