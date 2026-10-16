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
        /// <summary>
        /// CouchTV.exe --screenshot out.png [--size 1366x768] [--focus row,col] [--state toast,launch,confirm,osd,update,sleep,bluetooth,btdevice,btadd] [--config file]
        /// Rows for --focus: 0 apps, 1 films, 2 songs, 3 settings &amp; power (the suggestions are the saved ones).
        /// </summary>
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
            Config unsubscribed = Config.Parse(System.Text.RegularExpressions.Regex.Replace(Config.DefaultText(), @"(?m)^Subscriptions =.*$", "Subscriptions ="), "test");
            plan = new SearchPlan();
            SmartSearch.Build(plan, guess, "panchayat", unsubscribed, noKeys);
            check("no TMDB key: the AI's guesses become the options", plan.Options.Count == 2 && plan.Options[0].Tile.Name == "Prime Video" && plan.Options[0].Query == "Panchayat");
            plan = new SearchPlan();
            SmartSearch.Build(plan, guess, "panchayat", defaults, noKeys);
            check("Subscriptions = Netflix (the default): Netflix alone when it has it", plan.Options.Count == 1 && plan.Options[0].Tile.Name == "Netflix");

            // ----- shows and films: your subscriptions first, then other apps that have it, then a search of yours
            Tile prime = defaults.AppTiles.Find(x => x.Name == "Prime Video"), hotstar = defaults.AppTiles.Find(x => x.Name == "JioHotstar");
            Func<Config, KeyValuePair<Tile, string>[], string> watch = (config, found) =>
            {
                var p = new SearchPlan();
                SmartSearch.AddWatchOptions(p, new List<KeyValuePair<Tile, string>>(found), "Some Show", config);
                return string.Join(", ", p.Options.ConvertAll(o => o.Tile.Name + "/" + o.Caption).ToArray());
            };
            check("on Netflix and Prime: Netflix only", watch(defaults, new[] { Pair(prime, "Streams here"), Pair(netflix, "Streams here") }) == "Netflix/Streams here");
            check("not on Netflix: Prime and JioHotstar, saying which needs a plan",
                watch(defaults, new[] { Pair(prime, "Streams here"), Pair(hotstar, "Free with ads") }) == "Prime Video/Needs a subscription, JioHotstar/Free with ads");
            check("only rentable on YouTube: search Netflix instead", watch(defaults, new[] { Pair(youtube, "Rent or buy") }) == "Netflix/Search Netflix");
            check("nothing known: search Netflix", watch(defaults, new KeyValuePair<Tile, string>[0]) == "Netflix/Search Netflix");
            check("no subscriptions and nothing known: the plain app row", watch(unsubscribed, new KeyValuePair<Tile, string>[0]) == "");

            // ----- one-time settings changes on an installed couchtv.ini from before them
            string oldIni = System.Text.RegularExpressions.Regex.Replace(Config.DefaultText(), @"(?m)^(Subscriptions = Netflix|ConfigVersion = 2|Enabled = false\r?\n(?=Type = app\r?\nExe = %ProgramFiles%\\VideoLAN))\r?\n?", "");
            // ...and with the Bluetooth tile as it was: Windows' pairing wizard.
            oldIni = oldIni.Replace("Type = action\r\nAction = bluetooth", "Type = app\r\nExe = %SystemRoot%\\System32\\DevicePairingWizard.exe")
                           .Replace("Type = action\nAction = bluetooth", "Type = app\nExe = %SystemRoot%\\System32\\DevicePairingWizard.exe");
            var changes = new List<string>();
            string upgraded = ConfigUpgrade.Apply(oldIni, changes);
            Config after = Config.Parse(upgraded, "test");
            check("upgrade: test file really is the old kind", ConfigUpgrade.Get(oldIni, "CouchTV", "ConfigVersion") == null && ConfigUpgrade.Get(oldIni, "VLC", "Enabled") == null);
            check("upgrade: VLC hidden, Netflix subscribed, version recorded", ConfigUpgrade.Get(upgraded, "VLC", "Enabled") == "false"
                && after.Subscriptions.Count == 1 && after.Subscriptions[0] == "Netflix" && ConfigUpgrade.Get(upgraded, "CouchTV", "ConfigVersion") == ConfigUpgrade.Latest.ToString());
            Tile bluetoothTile = after.SystemTiles.Find(t => t.Name == "Bluetooth");
            check("upgrade: the Bluetooth tile opens CouchTV's Bluetooth screen, not Windows' wizard",
                ConfigUpgrade.Get(oldIni, "Bluetooth", "Exe") != null && bluetoothTile != null && bluetoothTile.Kind == TileKind.Action && bluetoothTile.Action == "bluetooth");
            check("upgrade: the new line goes with the other settings, before the tiles' comments",
                upgraded.IndexOf("Subscriptions = Netflix") < upgraded.IndexOf(";  Tiles: each"));
            check("upgrade: runs once (changing it back afterwards sticks)", ConfigUpgrade.Apply(ConfigUpgrade.Set(upgraded, "VLC", "Enabled", "true", false), new List<string>()).Contains("Enabled = true"));
            check("upgrade: a new install's file is left alone", ConfigUpgrade.Apply(Config.DefaultText(), new List<string>()) == Config.DefaultText());
            check("upgrade: everything else in the file is kept", upgraded.Contains("[Netflix]") && upgraded.Contains("HomeKeys = ") && after.AppTiles.Count == defaults.AppTiles.Count);

            SearchIntent play = SmartSearch.ParseIntent("{\"intent\":\"video\",\"title\":\"\",\"kind\":\"unknown\",\"year\":0,\"season\":0,\"episode\":0," +
                "\"search_text\":\"MrBeast\",\"apps\":[\"YouTube\"],\"action\":\"none\",\"value\":0,\"play\":true,\"newest\":true}");
            check("AI answer: play and newest read", play.Play && play.Newest && play.SearchText == "MrBeast");
            check("rules: \"gaane chalao\" plays", SmartSearch.Rules("arijit ke gaane chalao", defaults.AppTiles, false).Play
                && !SmartSearch.Rules("cooking recipe videos", defaults.AppTiles, false).Play);
            string page = "<script>var ytInitialData = {\"contents\":[{\"adSlotRenderer\":{}},{\"videoRenderer\":{\"videoId\":\"Ab3_dEfGh-1\"," +
                "\"thumbnail\":{\"thumbnails\":[{\"url\":\"x\"}]},\"title\":{\"runs\":[{\"text\":\"Kesariya \\u0026 Gerua \\\"Live\\\"\"}]}}}]};</script>";
            SmartSearch.YouTubeVideo first = SmartSearch.ParseYouTube(page);
            check("YouTube: the first ordinary result's id and title", first != null && first.Id == "Ab3_dEfGh-1" && first.Title == "Kesariya & Gerua \"Live\"");
            check("YouTube: a page without results gives nothing", SmartSearch.ParseYouTube("<html>consent</html>") == null);

            SecurityTest(check);
            WifiRemoteTest(check);
            PointerTest(check);

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
        /// CouchTV.exe --suggesttest report.txt: checks how titles and links are tidied, then fetches the suggestions as
        /// the home screen does (and saves them, pictures and all), and lists them with what the phone would be told.
        /// </summary>
        public static int SuggestTest(string path)
        {
            var report = new StringBuilder();
            int failures = 0;
            Action<string, string, string> check = (what, got, expected) =>
            {
                bool ok = got == expected;
                if (!ok) failures++;
                report.AppendLine((ok ? "ok    " : "FAIL  ") + what + ": " + got + (ok ? "" : "   (expected " + expected + ")"));
            };
            check("song title", Suggestions.SongTitle("Radhimaa (feat. Sai Smriti) [From \"Think Indie\"]"), "Radhimaa");
            check("song title", Suggestions.SongTitle("One Sun One Moon (From \"Jailer 2\")"), "One Sun One Moon");
            check("song title", Suggestions.SongTitle("Arz Kiya Hai  Coke Studio Bharat"), "Arz Kiya Hai Coke Studio Bharat");
            check("song title", Suggestions.SongTitle("KALYANI (Remix)"), "KALYANI (Remix)");
            check("lead artist", Suggestions.LeadArtist("Karan Aujla & MXRCI"), "Karan Aujla");
            check("lead artist", Suggestions.LeadArtist("Shashwat Sachdev, Arijit Singh, Irshad Kamil & Armaan Khan"), "Shashwat Sachdev");
            check("Netflix plays", Suggestions.PlayableUrl("https://www.netflix.com/title/82034837"), "https://www.netflix.com/watch/82034837");
            check("Netflix plays", Suggestions.PlayableUrl("https://www.netflix.com/in/title/82034837?s=a"), "https://www.netflix.com/watch/82034837");
            check("JioHotstar plays", Suggestions.PlayableUrl("https://www.hotstar.com/in/movies/bethlehem-kudumba-unit/1271710275?search_query=Bethlehem+Kudumba+Unit"),
                  "https://www.hotstar.com/in/movies/bethlehem-kudumba-unit/1271710275/watch");
            check("ZEE5 as it is", Suggestions.PlayableUrl("https://www.zee5.com/movies/details/nude/0-0-70944"), "https://www.zee5.com/movies/details/nude/0-0-70944");

            Config cfg = Config.Load();
            Func<string, string> b64 = text => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            Func<string, string> fromPhone = request =>
            {
                Suggestion s = Suggestions.FromPhone(request.Split(' '), cfg.AppTiles);
                return s == null ? "refused" : s.App + " " + (s.PlayUrl ?? "(look for: " + s.Query + ")");
            };
            check("phone's film plays", fromPhone("mtm1 M nfx " + b64("https://www.netflix.com/title/82034837") + " " + b64("Vishwanath & Sons") + " -"),
                  "Netflix https://www.netflix.com/watch/82034837");
            check("phone's song is looked for", fromPhone("s1 S youtube - " + b64("Hawayein") + " " + b64("Hawayein Pritam")), "YouTube (look for: Hawayein Pritam)");
            check("phone's song with its video", fromPhone("s1 S youtube " + b64("https://www.youtube.com/watch?v=cYOB941gyXI&list=RDcYOB941gyXI") + " " + b64("Hawayein") + " -"),
                  "YouTube https://www.youtube.com/watch?v=cYOB941gyXI&list=RDcYOB941gyXI");
            check("not another site", fromPhone("mtm1 M nfx " + b64("https://netflix.com.example.org/title/1") + " " + b64("x") + " -"), "refused");
            check("not plain http", fromPhone("mtm1 M nfx " + b64("http://www.netflix.com/title/1") + " " + b64("x") + " -"), "refused");
            check("YouTube: videos only", fromPhone("s1 S youtube " + b64("https://www.youtube.com/redirect?q=x") + " " + b64("x") + " -"), "refused");
            check("not an app the TV hasn't", fromPhone("mtm1 M xyz " + b64("https://www.netflix.com/title/1") + " " + b64("x") + " -"), "refused");
            check("a film needs its link", fromPhone("mtm1 M nfx - " + b64("x") + " " + b64("x")), "refused");
            DateTime fetched;
            List<Suggestion> previous = Suggestions.Load(out fetched);
            report.AppendLine();
            report.AppendLine("Saved list: " + previous.Count + (fetched == DateTime.MinValue ? "" : ", fetched " + fetched.ToLocalTime()));
            report.AppendLine("Settings: films " + (cfg.SuggestMovies ? "on" : "off") + ", songs " + (cfg.SuggestSongs ? "on" : "off") +
                              ", subscriptions: " + (cfg.Subscriptions.Count == 0 ? "none" : string.Join(", ", cfg.Subscriptions)));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var notes = new StringBuilder();
            try
            {
                List<Suggestion> list = Suggestions.Fetch(cfg, previous, notes);
                report.AppendLine("Fetched in " + watch.ElapsedMilliseconds + " ms. " + notes.ToString().Trim());
                foreach (Suggestion s in list)
                    report.AppendLine((s.Kind == SuggestionKind.Movie ? "Film  " : "Song  ") + s.Title + "  |  " + s.Subtitle + "  |  " + s.App + "  |  " +
                                      (s.PlayUrl ?? "(looked for when chosen: " + s.Query + ")") + "  |  " + (s.ImageFile ?? "no picture"));
                report.AppendLine();
                report.AppendLine("To the phone:");
                foreach (string line in Suggestions.PhoneLines(list, cfg, name => cfg.AppTiles.Find(t => t.Name == name)))
                    report.AppendLine("  " + (line.Length > 150 ? line.Substring(0, 150) + "…" : line));
                if (list.Count == 0) failures++;
            }
            catch (Exception ex)
            {
                report.AppendLine("FAIL  fetching: " + ex.Message);
                failures++;
            }
            report.Insert(0, (failures == 0 ? "All good" : failures + " failed") + "\r\n\r\n");
            File.WriteAllText(path, report.ToString(), Encoding.UTF8);
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

        static KeyValuePair<Tile, string> Pair(Tile t, string caption) { return new KeyValuePair<Tile, string>(t, caption); }

        /// <summary>
        /// CouchTV.exe --pointertest folder: draws every pointer into folder\pointers.png to look at, checks the
        /// cursor files, then puts a big one in place for a moment and Windows' own back (the pointer flickers).
        /// </summary>
        public static int PointerTest(string folder)
        {
            var report = new StringBuilder();
            bool ok = true;
            Action<string, bool> check = (name, pass) => { report.AppendLine((pass ? "PASS  " : "FAIL  ") + name); ok &= pass; };
            Directory.CreateDirectory(folder);

            // A contact sheet: each style's pointer, link and text cursor, in white, then the other colours, each on a
            // dark and a light background; then the arrow at every size.
            const int cell = 80;
            var kinds = new[] { Pointer.Kind.Normal, Pointer.Kind.Link, Pointer.Kind.Text };
            var sheet = new DrawingVisual();
            int width = cell * 2 * (kinds.Length + Pointer.ColorNames.Length - 1), height = cell * (Pointer.Styles.Length + 2);
            using (DrawingContext dc = sheet.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
                for (int row = 0; row < Pointer.Styles.Length; row++)
                {
                    int col = 0;
                    foreach (Pointer.Kind kind in kinds) DrawCell(dc, Pointer.Styles[row], 64, "white", kind, col++, row, cell, check);
                    for (int c = 1; c < Pointer.ColorNames.Length; c++) DrawCell(dc, Pointer.Styles[row], 64, Pointer.ColorNames[c], Pointer.Kind.Normal, col++, row, cell, check);
                }
                int[] sizes = { 32, 48, 64, 96, 128 };
                for (int i = 0; i < sizes.Length; i++)
                {
                    int hx, hy;
                    byte[] px = Pointer.Render("arrow", sizes[i], "white", Pointer.Kind.Normal, out hx, out hy);
                    var image = BitmapSource.Create(sizes[i], sizes[i], 96, 96, PixelFormats.Bgra32, null, px, sizes[i] * 4);
                    double x = 10 + i * 150, y = cell * Pointer.Styles.Length + 10;
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x14)), null, new Rect(x, y, 140, 140));
                    dc.DrawImage(image, new Rect(x, y, sizes[i], sizes[i]));
                }
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(sheet);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(folder, "pointers.png"))) png.Save(file);

            int hotX, hotY;
            byte[] pixels = Pointer.Render("arrow", 96, "yellow", Pointer.Kind.Normal, out hotX, out hotY);
            byte[] cur = Pointer.CursorFile(pixels, 96, hotX, hotY);
            check("a 96-pixel arrow is 96 x 96, pointing from its tip", pixels.Length == 96 * 96 * 4 && hotX == 12 && hotY == 9);
            check("its .cur file has the cursor header and hotspot", cur.Length == 6 + 16 + 40 + 96 * 96 * 4 + 12 * 96
                && BitConverter.ToInt16(cur, 2) == 2 && BitConverter.ToInt16(cur, 10) == hotX && BitConverter.ToInt16(cur, 12) == hotY);

            int before = Pointer.CurrentArrowSize();
            Pointer.Apply("arrow", 96, "yellow");
            int during = Pointer.CurrentArrowSize();

            // Shown only while in use: hidden (a blank pointer) a few seconds after the last move, shown at once while a
            // phone's trackpad page is open or when something else moves it.
            Pointer.ForgetUse();
            Pointer.Update();
            check("not in use: hidden (" + Pointer.CurrentArrowSize() + " pixels, blank)", Pointer.Hidden && Pointer.CurrentArrowSize() == 32);
            Pointer.Trackpad(true);
            check("a phone's trackpad page open: shown (" + Pointer.CurrentArrowSize() + " pixels)", !Pointer.Hidden && Pointer.CurrentArrowSize() == 96);
            Pointer.ForgetUse();
            Pointer.Update();
            check("...and stays shown while it's open, however long", !Pointer.Hidden);
            Pointer.Trackpad(false);
            check("the page left: still shown for a few seconds", !Pointer.Hidden);
            Pointer.ForgetUse();
            Pointer.Update();
            check("...then hidden", Pointer.Hidden);
            Pointer.Used();
            for (int i = 0; i < 20 && Pointer.Hidden; i++) System.Threading.Thread.Sleep(50);
            check("a mouse moving shows it again", !Pointer.Hidden && Pointer.CurrentArrowSize() == 96);

            // With no mouse plugged in Windows draws no pointer: Mouse Keys on (it counts as one) until CouchTV closes.
            bool keysWere = Pointer.MouseKeysOn, keysTurned = Pointer.TestMouseKeys(), keysDuring = Pointer.MouseKeysOn;
            Pointer.Restore();
            bool keysAfter = Pointer.MouseKeysOn;
            check("no mouse: Mouse Keys turned on so Windows draws it (" + keysDuring + "), then back off (" + keysAfter + ")",
                keysWere ? keysDuring && keysAfter : keysTurned && keysDuring && !keysAfter);
            int after = Pointer.CurrentArrowSize();
            check("Windows' arrow becomes the 96-pixel pointer (was " + before + "), then goes back (" + after + ")",
                during == 96 && after == before);
            report.AppendLine(ok ? "All passed" : "FAILED");
            File.WriteAllText(Path.Combine(folder, "pointertest.txt"), report.ToString());
            return ok ? 0 : 1;
        }

        static void DrawCell(DrawingContext dc, string style, int size, string color, Pointer.Kind kind, int col, int row, int cell, Action<string, bool> check)
        {
            int hx, hy;
            byte[] px = Pointer.Render(style, size, color, kind, out hx, out hy);
            bool visible = false;
            for (int i = 3; i < px.Length && !visible; i += 4) visible = px[i] > 200;
            if (!visible) check(style + " " + color + " " + kind + " draws something", false);
            var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, px, size * 4);
            double x = col * cell * 2, y = row * cell;
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x14)), null, new Rect(x, y, cell, cell));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE4, 0xE6, 0xEA)), null, new Rect(x + cell, y, cell, cell));
            dc.DrawImage(image, new Rect(x + (cell - size) / 2, y + (cell - size) / 2, size, size));
            dc.DrawImage(image, new Rect(x + cell + (cell - size) / 2, y + (cell - size) / 2, size, size));
            dc.DrawEllipse(Brushes.Red, null, new Point(x + (cell - size) / 2 + hx, y + (cell - size) / 2 + hy), 1.5, 1.5);   // the hotspot
        }

        /// <summary>
        /// CouchTV.exe --audiotest report.txt: plays a quiet tone for 2 seconds and listens like a paired phone would,
        /// over this PC's loopback, to check the sound arrives: packet format, encryption, timing and that it isn't
        /// silence; and that requests without the phone's key, or replayed, get nothing.
        /// </summary>
        public static int AudioTest(string path)
        {
            var report = new StringBuilder();
            var share = new AudioShare(47792, System.Net.IPAddress.Loopback);
            var session = new RemoteSession
            {
                Id = "0123456789abcdef", Phone = System.Net.IPAddress.Loopback, Name = "test",
                AudioEnc = Secure.Random(32), AudioMac = Secure.Random(32),
            };
            share.Sessions = id => id == session.Id ? session : null;
            share.Start();
            var packets = new List<byte[]>();
            var times = new List<long>();
            int strangers = -1, replayed = -1, badTags = 0;
            long number = 0;
            Func<string, byte[]> ask = verb =>
            {
                string n = (++number).ToString();
                byte[] tag = Secure.Take(Secure.Hmac(session.AudioMac, Secure.Utf8(verb + " " + session.Id + " " + n)), 0, Secure.TagBytes);
                return Encoding.ASCII.GetBytes(verb + " 3 " + session.Id + " " + n + " " + Secure.Hex(tag));
            };
            try
            {
                using (var phone = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
                using (var tone = new System.Media.SoundPlayer(Tone(440, 1.0, 0.03)))
                {
                    phone.Client.ReceiveTimeout = 200;
                    var server = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 47792);
                    Func<int, int> drain = wait =>
                    {
                        int count = 0;
                        var until = System.Diagnostics.Stopwatch.StartNew();
                        while (until.ElapsedMilliseconds < wait)
                        {
                            var from = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                            try { phone.Receive(ref from); count++; }
                            catch (System.Net.Sockets.SocketException) { }
                        }
                        return count;
                    };
                    // Strangers: an old app's requests, and one with a made-up tag.
                    foreach (string message in new[] { "LISTEN 2", "LISTEN", "LISTEN 3 " + session.Id + " 99 " + Secure.Hex(Secure.Random(16)) })
                    {
                        byte[] bytes = Encoding.ASCII.GetBytes(message);
                        phone.Send(bytes, bytes.Length, server);
                    }
                    strangers = drain(500);

                    byte[] firstListen = ask("LISTEN");
                    phone.Send(firstListen, firstListen.Length, server);
                    System.Threading.Thread.Sleep(300);
                    tone.Play();
                    phone.Client.ReceiveTimeout = 1000;
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    long lastAsk = 0;
                    while (clock.ElapsedMilliseconds < 3000)
                    {
                        if (clock.ElapsedMilliseconds - lastAsk >= 1000)   // keep listening
                        {
                            lastAsk = clock.ElapsedMilliseconds;
                            byte[] listen = ask("LISTEN");
                            phone.Send(listen, listen.Length, server);
                        }
                        var from = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                        try { packets.Add(phone.Receive(ref from)); times.Add(clock.ElapsedMilliseconds); }
                        catch (System.Net.Sockets.SocketException) { }
                    }
                    byte[] stop = ask("STOP");
                    phone.Send(stop, stop.Length, server);
                    tone.Stop();
                    phone.Client.ReceiveTimeout = 200;
                    drain(300);
                    phone.Send(firstListen, firstListen.Length, server);   // replayed: must not start it again
                    replayed = drain(600);
                }
            }
            catch (Exception ex) { report.AppendLine("FAILED: " + ex.Message); }
            finally { share.Stop(); }

            // Each packet: check its tag, then decrypt it, as the phone does.
            var opened = new List<byte[]>();
            using (var cipher = new Ctr(session.AudioEnc))
            using (var mac = new System.Security.Cryptography.HMACSHA256(session.AudioMac))
                foreach (byte[] sealedPacket in packets)
                {
                    int body = sealedPacket.Length - Secure.TagBytes;
                    if (body <= 16 || !Secure.Same(Secure.Take(mac.ComputeHash(sealedPacket, 0, body), 0, Secure.TagBytes), Secure.Take(sealedPacket, body, Secure.TagBytes)))
                    {
                        badTags++;
                        continue;
                    }
                    byte[] p = Secure.Take(sealedPacket, 0, body);
                    cipher.Apply(BitConverter.ToUInt32(p, 4), p, 16, body - 16);
                    opened.Add(p);
                }

            int loud = 0, peak = 0, rate = 0, gaps = 0, repairs = 0, badRepairs = 0;
            uint last = 0;
            byte[] previous = null;
            foreach (byte[] p in opened)
            {
                if (p.Length < 16 || Encoding.ASCII.GetString(p, 0, 4) != "CTA3") continue;
                uint seq = BitConverter.ToUInt32(p, 4);
                if (last != 0 && seq != last + 1) gaps++;
                last = seq;
                // Each packet carries its chunk, then a copy of the one before: check it matches what came before.
                int half = (p.Length - 16) / 2;
                if ((p[13] & 1) != 0 && previous != null)
                {
                    repairs++;
                    for (int i = 0; i < half; i++) if (p[16 + half + i] != previous[16 + i]) { badRepairs++; break; }
                }
                previous = p;
                rate = (int)BitConverter.ToUInt32(p, 8);
                int max = 0;
                for (int i = 16; i + 1 < 16 + half; i += 2) max = Math.Max(max, Math.Abs((int)BitConverter.ToInt16(p, i)));
                peak = Math.Max(peak, max);
                if (max > 100) loud++;
            }
            double ms = packets.Count > 1 ? (times[times.Count - 1] - times[0]) / (double)(packets.Count - 1) : 0;
            long longest = 0;
            for (int i = 1; i < times.Count; i++) longest = Math.Max(longest, times[i] - times[i - 1]);
            report.AppendLine("Packets: " + packets.Count + ", " + (packets.Count > 0 ? packets[0].Length : 0) + " bytes each, every " + ms.ToString("0.0") + " ms on average");
            report.AppendLine("Sample rate: " + rate + " Hz, gaps in the sequence: " + gaps + ", repair copies: " + repairs + " (" + badRepairs + " wrong)");
            report.AppendLine("With the tone: " + loud + " packets, peak " + peak + " of 32767; silent: " + (packets.Count - loud));
            report.AppendLine("Longest wait between packets: " + longest + " ms (silence keeps coming after the tone ends)");
            report.AppendLine("Packets whose tag didn't check: " + badTags + "; packets for strangers' requests: " + strangers + ", for a replayed one: " + replayed);
            bool ok = packets.Count > 450 && loud > 100 && packets.Count - loud > 100 && gaps == 0 && longest < 60 && repairs > 400 && badRepairs == 0
                      && packets.Count > 0 && packets[0].Length == 16 + 2 * 720 + Secure.TagBytes && badTags == 0 && strangers == 0 && replayed == 0;
            report.AppendLine(ok ? "PASS" : "FAIL");
            File.WriteAllText(path, report.ToString());
            return ok ? 0 : 1;
        }

        /// <summary>A sine tone as an in-memory WAV file.</summary>
        static MemoryStream Tone(int hz, double seconds, double volume)
        {
            const int rate = 44100;
            int samples = (int)(rate * seconds);
            var wav = new MemoryStream();
            var w = new BinaryWriter(wav);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + samples * 2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(samples * 2);
            for (int i = 0; i < samples; i++) w.Write((short)(Math.Sin(2 * Math.PI * hz * i / rate) * volume * 32767));
            w.Flush();
            wav.Position = 0;
            return wav;
        }

        /// <summary>The trackpad's pointer moves land on exactly the pixel asked for (nudges it, then puts it back).</summary>
        static void PointerTest(Action<string, bool> check)
        {
            if (Input.PointerLocked())
            {
                check("trackpad: pointer precision not checked: an app (a game?) has locked the pointer", true);
                return;
            }
            int x0, y0;
            if (!Input.CursorPosition(out x0, out y0)) { check("trackpad: can't read the pointer's position", false); return; }
            int sx = x0 > 100 ? -1 : 1, sy = y0 > 100 ? -1 : 1;   // away from the screen's edge
            bool exact = true;
            string path = x0 + "," + y0;
            for (int i = 1; i <= 5 && exact; i++)
            {
                Input.MoveBy(sx, sy);
                System.Threading.Thread.Sleep(15);
                int x, y;
                exact = Input.CursorPosition(out x, out y) && x == x0 + i * sx && y == y0 + i * sy;
                path += " > " + x + "," + y;
            }
            Input.MoveBy(-5 * sx, -5 * sy);
            System.Threading.Thread.Sleep(15);
            int xb, yb;
            bool back = Input.CursorPosition(out xb, out yb) && xb == x0 && yb == y0;
            check("trackpad: each 1-pixel nudge moves the pointer exactly 1 pixel, and back" + (exact && back ? "" : " (" + path + " > " + xb + "," + yb + ")"), exact && back);
        }

        /// <summary>The cryptography against published answers (RFC 4231, RFC 5869, NIST SP 800-38A, RFC 5903).</summary>
        static void SecurityTest(Action<string, bool> check)
        {
            Func<string, byte[]> hex = s => Secure.FromHex(s.Replace(" ", ""));
            byte[] key = new byte[20];
            for (int i = 0; i < key.Length; i++) key[i] = 0x0b;
            check("security: HMAC-SHA256 (RFC 4231 case 1)", Secure.Hex(Secure.Hmac(key, Encoding.ASCII.GetBytes("Hi There"))) ==
                "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7");

            byte[] ikm = new byte[22];
            for (int i = 0; i < ikm.Length; i++) ikm[i] = 0x0b;
            check("security: HKDF-SHA256 (RFC 5869 case 1)", Secure.Hex(Secure.Hkdf(ikm, hex("000102030405060708090a0b0c"), hex("f0f1f2f3f4f5f6f7f8f9"), 42)) ==
                "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865");

            byte[] block = hex("6bc1bee22e409f96e93d7e117393172a ae2d8a571e03ac9c9eb76fac45af8e51");
            using (var ctr = new Ctr(hex("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4")))
                ctr.Apply(0xf0f1f2f3f4f5f6f7, block, 0, block.Length, 0xf8f9fafbfcfdfeff);
            check("security: AES-256-CTR (NIST SP 800-38A F.5.5)", Secure.Hex(block) == "601ec313775789a5b7a7f504bbf3d228f443e3ca4d62b59aca84e990cacaf5c5");

            try
            {
                using (var i = new EcdhKey(hex(Rfc5903I), hex("04" + Rfc5903Gi)))
                {
                    check("security: ECDH P-256 public key (RFC 5903)", Secure.Hex(i.PublicPoint) == "04" + Rfc5903Gi.Replace(" ", "").ToLowerInvariant());
                    check("security: ECDH P-256 shared secret (RFC 5903), hashed", Secure.Same(i.Agree(hex("04" + Rfc5903Gr)), Secure.Sha256(hex(Rfc5903Gir))));
                }
                using (var a = new EcdhKey())
                using (var b = new EcdhKey())
                    check("security: two fresh keys agree", Secure.Same(a.Agree(b.PublicPoint), b.Agree(a.PublicPoint)));
                bool refused = false;
                byte[] offCurve = hex("04" + Rfc5903Gr);
                offCurve[64] ^= 1;
                using (var a = new EcdhKey())
                {
                    try { a.Agree(offCurve); }
                    catch (System.Security.Cryptography.CryptographicException) { refused = true; }
                }
                check("security: a key that isn't on the curve is refused", refused);
            }
            catch (Exception ex) { check("security: ECDH: " + ex.Message, false); }

            var tv = new Channel(hex("01".PadRight(64, '0')), hex("02".PadRight(64, '0')), hex("03".PadRight(64, '0')), hex("04".PadRight(64, '0')));
            var phone = new Channel(hex("03".PadRight(64, '0')), hex("04".PadRight(64, '0')), hex("01".PadRight(64, '0')), hex("02".PadRight(64, '0')));
            string first = tv.Seal("WELCOME TV"), second = tv.Seal("PONG");
            check("security: sealed lines open in order", phone.Open(first) == "WELCOME TV" && phone.Open(second) == "PONG");
            string third = tv.Seal("PONG");
            check("security: a replayed line doesn't open", phone.Open(second) == null);
            byte[] bent = Convert.FromBase64String(third);
            bent[0] ^= 1;
            check("security: a changed line doesn't open", phone.Open(Convert.ToBase64String(bent)) == null && phone.Open(third) == "PONG");
            check("security: the same words seal differently each time", tv.Seal("PING") != tv.Seal("PING"));
        }

        const string Rfc5903I = "C88F01F5 10D9AC3F 70A292DA A2316DE5 44E9AAB8 AFE84049 C62A9C57 862D1433";
        const string Rfc5903Gi = "DAD0B653 94221CF9 B051E1FE CA5787D0 98DFE637 FC90B9EF 945D0C37 72581180" +
                                 "5271A046 1CDB8252 D61F1C45 6FA3E59A B1F45B33 ACCF5F58 389E0577 B8990BB3";
        const string Rfc5903R = "C6EF9C5D 78AE012A 011164AC B397CE20 88685D8F 06BF9BE0 B283AB46 476BEE53";
        const string Rfc5903Gr = "D12DFB52 89C8D4F8 1208B702 70398C34 2296970A 0BCCB74C 736FC755 4494BF63" +
                                 "56FBF3CA 366CC23E 8157854C 13C58D6A AC23F046 ADA30F83 53E74F33 039872AB";
        const string Rfc5903Gir = "D6840F6B 42F6EDAF D13116E0 E1256520 2FEF8E9E CE7DCE03 812464D0 4B9442DE";

        /// <summary>
        /// CouchTV.exe --securityvectors file: the same calculations as the phone app's (Secure.kt), from fixed inputs,
        /// for its unit test (phone-remote/app/src/test/resources/couchtv-vectors.txt). "name = value" lines.
        /// </summary>
        public static int SecurityVectors(string path)
        {
            var v = new StringBuilder();
            v.AppendLine("# Made by CouchTV.exe --securityvectors (src/Diagnostics.cs); checked by SecureTest.kt.");
            Action<string, string> put = (name, value) => v.AppendLine(name + " = " + value);
            Func<string, byte[]> hex = s => Secure.FromHex(s.Replace(" ", ""));
            Func<int, int, byte[]> run = (from, count) =>
            {
                var b = new byte[count];
                for (int i = 0; i < count; i++) b[i] = (byte)(from + i);
                return b;
            };

            // Keys: the RFC 5903 pair, the phone's being i and the TV's r.
            byte[] phonePoint = hex("04" + Rfc5903Gi), tvPoint = hex("04" + Rfc5903Gr);
            byte[] shared;
            using (var phone = new EcdhKey(hex(Rfc5903I), phonePoint)) shared = phone.Agree(tvPoint);
            put("phone_private", Secure.Hex(hex(Rfc5903I)));
            put("phone_point", Secure.Hex(phonePoint));
            put("tv_point", Secure.Hex(tvPoint));
            put("shared", Secure.Hex(shared));

            string phoneHello = "HELLO2 CouchTV-Remote 1.99 00112233445566778899aabbccddeeff " + Convert.ToBase64String(phonePoint);
            string tvHello = "HELLO2 ffeeddccbbaa99887766554433221100 " + Convert.ToBase64String(tvPoint) + " PAIR TESTTV";
            byte[] transcript = Secure.Sha256(Secure.Utf8(phoneHello + "\n" + tvHello));
            put("phone_hello", phoneHello);
            put("tv_hello", tvHello);
            put("transcript", Secure.Hex(transcript));

            put("code", "482913");
            byte[] pairKey = SessionKeys.PairKey(shared, "482913", transcript);
            put("pair_key", Secure.Hex(pairKey));
            byte[] nonce = run(0x40, 16);
            put("commit_nonce", Secure.Hex(nonce));
            put("commit_phone_bit1", Secure.Hex(Secure.Commit(nonce, 'P', phonePoint, tvPoint, 1)));
            put("commit_tv_bit0", Secure.Hex(Secure.Commit(nonce, 'T', tvPoint, phonePoint, 0)));

            SessionKeys keys = SessionKeys.Derive(shared, pairKey, transcript);
            put("phone_proof", Secure.Hex(keys.PhoneProof));
            put("tv_proof", Secure.Hex(keys.TvProof));
            put("phone_enc", Secure.Hex(keys.PhoneEnc));
            put("phone_mac", Secure.Hex(keys.PhoneMac));
            put("tv_enc", Secure.Hex(keys.TvEnc));
            put("tv_mac", Secure.Hex(keys.TvMac));
            put("audio_enc", Secure.Hex(keys.AudioEnc));
            put("audio_mac", Secure.Hex(keys.AudioMac));

            using (var fromPhone = new Channel(keys.PhoneEnc, keys.PhoneMac, keys.TvEnc, keys.TvMac))
            using (var fromTv = new Channel(keys.TvEnc, keys.TvMac, keys.PhoneEnc, keys.PhoneMac))
            {
                put("phone_line0", fromPhone.Seal("HELLO CouchTV-Remote 1.99"));
                put("phone_line1", fromPhone.Seal("TEXT " + Convert.ToBase64String(Secure.Utf8("पंचायत"))));
                put("tv_line0", fromTv.Seal("WELCOME TESTTV"));
                put("tv_line1", fromTv.Seal("AUDIO 0123456789abcdef"));
            }

            put("listen_tag", Secure.Hex(Secure.Take(Secure.Hmac(keys.AudioMac, Secure.Utf8("LISTEN 0123456789abcdef 7")), 0, Secure.TagBytes)));
            put("mirror_enc", Secure.Hex(keys.MirrorEnc));
            put("mirror_mac", Secure.Hex(keys.MirrorMac));
            using (var mirror = new FrameWriter(Stream.Null, keys.MirrorEnc, keys.MirrorMac))
            {
                put("mirror_frame0", Secure.Hex(mirror.Seal(new byte[] { 1, 5, 0, 2, 208 })));   // the picture is 1280 x 720
                put("mirror_frame1", Secure.Hex(mirror.Seal(new byte[] { 5 })));                // a heartbeat
            }
            // A sound packet: header (CTA3, sequence 1234, 48000 Hz, 2 channels, flags 1), samples 0, 1, 2..., sealed.
            var packet = new byte[16 + 1440 + Secure.TagBytes];
            Encoding.ASCII.GetBytes("CTA3", 0, 4, packet, 0);
            BitConverter.GetBytes(1234u).CopyTo(packet, 4);
            BitConverter.GetBytes(48000u).CopyTo(packet, 8);
            packet[12] = 2;
            packet[13] = 1;
            for (int i = 0; i < 1440; i++) packet[16 + i] = (byte)i;
            using (var ctr = new Ctr(keys.AudioEnc)) ctr.Apply(1234, packet, 16, 1440);
            using (var mac = new System.Security.Cryptography.HMACSHA256(keys.AudioMac))
                Buffer.BlockCopy(mac.ComputeHash(packet, 0, 16 + 1440), 0, packet, 16 + 1440, Secure.TagBytes);
            put("audio_packet", Secure.Hex(packet));

            // The receiver box (CouchLink): requests and answers tagged with its key and its current challenge.
            byte[] boardKey = run(0, 32);
            string challenge = "00112233445566778899aabbccddeeff", next = "ffeeddccbbaa99887766554433221100";
            put("board_key", Secure.Hex(boardKey));
            put("board_challenge", challenge);
            put("board_next", next);
            put("board_state_tag", BoardTag(boardKey, "STATE " + challenge));
            put("board_power_tag", BoardTag(boardKey, "POWER 42 " + challenge));
            put("board_reply_tag", BoardTag(boardKey, "R " + challenge + " STATE PC ON TV OFF SENSE ON " + next));
            File.WriteAllText(path, v.ToString().Replace("\r\n", "\n"), new UTF8Encoding(false));
            return 0;
        }

        /// <summary>
        /// CouchTV.exe --bluetoothtest report.txt: what CouchTV sees of this PC's Bluetooth and sound outputs, without
        /// changing anything (no pairing, connecting or switching); the resampler that keeps a copy in step; and, if
        /// there's a second sound output, a few seconds of copying to it.
        /// </summary>
        public static int BluetoothTest(string path)
        {
            var report = new StringBuilder();
            int failures = 0;
            Action<string, bool> check = (name, ok) =>
            {
                report.AppendLine((ok ? "PASS  " : "FAIL  ") + name);
                if (!ok) failures++;
            };
            try
            {
                bool? power = BluetoothPower.IsOn();
                report.AppendLine("Bluetooth switch: " + (power == null ? "no adapter" : power.Value ? "on" : "off"));
                string radio = Bluetooth.RadioName();
                report.AppendLine("Bluetooth adapter: " + (radio ?? "none (switched off, or no adapter)"));
                check("bluetooth: an adapter is found exactly when Bluetooth is on", (radio != null) == (power == true));
                if (radio != null)
                    foreach (BtDevice d in Bluetooth.Devices(false))
                        report.AppendLine("  device: " + d.Label + " (" + d.AddressText + ", " + d.Kind + ", class " + d.ClassOfDevice.ToString("X6") + ")" +
                                          (d.Paired ? ", paired" : "") + (d.Connected ? ", connected" : "") +
                                          (d.LastUsed != DateTime.MinValue ? ", last used " + d.LastUsed.ToString("g") : ""));
                string main = AudioOutputs.DefaultId();
                List<AudioOutput> outputs = AudioOutputs.All();
                foreach (AudioOutput o in outputs)
                    report.AppendLine("  output: " + o.Name + (o.Id == main ? " [main]" : "") + (o.Active ? ", active" : ", unplugged") +
                                      ", form " + o.FormFactor + (o.BtAddress != 0 ? ", Bluetooth " + Bluetooth.AddressText(o.BtAddress) + (o.HandsFree ? " hands-free" : " stereo") +
                                      (AudioOutputs.CanConnect(o.Id) ? ", can connect/disconnect" : ", CAN'T connect/disconnect") : ""));
                check("bluetooth: the sound outputs are listed, with the main one among them", outputs.Count > 0 && outputs.Exists(o => o.Id == main));
                foreach (AudioOutput o in outputs.FindAll(x => x.BtAddress != 0 && !x.HandsFree))
                    check("bluetooth: " + o.Name + " can be connected and disconnected", AudioOutputs.CanConnect(o.Id));
                object policy = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")));
                check("bluetooth: Windows' main-output switch is there", policy != null);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(policy);

                // The resampler: exact at normal speed; at 0.1% faster, 0.1% fewer frames, with a tone kept intact.
                var tone = new float[48000 * 2];
                for (int i = 0; i < 48000; i++) tone[2 * i] = tone[2 * i + 1] = (float)Math.Sin(2 * Math.PI * 1000 * i / 48000.0) * 0.5f;
                float[] same = new SincResampler().Process(tone, 48000, 1.0);
                bool exact = same.Length > 47000 * 2;
                for (int i = 0; i < 40000 && exact; i++) exact = Math.Abs(same[2 * i] - tone[2 * i]) < 1e-6f;
                check("copy: at normal speed the sound comes through unchanged", exact);
                float[] faster = new SincResampler().Process(tone, 48000, 1.001);
                check("copy: 0.1% faster gives 0.1% fewer frames (" + faster.Length / 2 + ")", Math.Abs(faster.Length / 2 - 48000 / 1.001) < 20);
                double peak = 0;
                for (int i = 1000; i < faster.Length / 2 - 1000; i++) peak = Math.Max(peak, Math.Abs(faster[2 * i]));
                check("copy: ...and keeps the tone's level (" + peak.ToString("0.000") + ")", Math.Abs(peak - 0.5) < 0.01);

                AudioOutput other = outputs.Find(o => o.Active && o.Id != main && !o.HandsFree);
                if (other == null) report.AppendLine("copy: no second sound output on this PC to copy to (not tried)");
                else
                {
                    var copy = new AudioCopy(other.Id, other.Name);
                    copy.Start();
                    System.Threading.Thread.Sleep(3000);
                    copy.Stop();
                    check("copy: plays to " + other.Name + " (" + copy.Played + " frames in 3 s)", copy.Played > 48000);
                }
            }
            catch (Exception ex) { check("bluetooth: " + ex.Message, false); }
            report.AppendLine(failures == 0 ? "All passed" : failures + " failed");
            File.WriteAllText(path, report.ToString());
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// CouchTV.exe --mirrortest report.txt: mirrors this PC's screen to a pretend phone over the loopback for 8
        /// seconds, puts the picture back together as the phone does, and reports how big and how fast the first
        /// picture was, the bandwidth after it, the CPU it took, and how close the result is to the screen.
        /// </summary>
        public static int MirrorTest(string path)
        {
            var report = new StringBuilder();
            int failures = 0;
            Action<string, bool> check = (name, ok) =>
            {
                report.AppendLine((ok ? "PASS  " : "FAIL  ") + name);
                if (!ok) failures++;
            };
            PhoneStore phones = PhoneStore.InMemory();
            var server = new RemoteServer(null, phones, 47795, 47796, System.Net.IPAddress.Loopback);
            string shown = null;
            server.PairingCode += (name, digits) => shown = digits;
            server.Start();
            System.Drawing.Bitmap canvas = null;
            try
            {
                System.Threading.Thread.Sleep(300);
                byte[] pairKey;
                using (var p = new TestPhone("22222222222222222222222222222222"))
                {
                    p.Hello(47796);
                    p.Pair("Mirror test", () => shown);
                    p.Auth(p.PairKey);
                    pairKey = p.PairKey;
                }
                using (var m = new TestPhone("22222222222222222222222222222222"))
                {
                    m.Hello(47796);
                    m.Auth(pairKey);
                    TimeSpan cpuBefore = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    m.Send("MIRROR 1280 4000");
                    var frames = new FrameReader(m.Stream, m.Keys.MirrorEnc, m.Keys.MirrorMac);
                    long firstBytes = 0, laterBytes = 0, firstMs = -1;
                    int pictures = 0, heartbeats = 0;
                    byte[] message;
                    System.Drawing.Graphics draw = null;
                    while (clock.ElapsedMilliseconds < 8000 && (message = frames.Next()) != null)
                    {
                        int size = message.Length + 4 + Secure.TagBytes;
                        if (firstMs < 0) firstBytes += size;
                        else laterBytes += size;
                        switch (message[0])
                        {
                            case 1:
                                canvas = new System.Drawing.Bitmap(message[1] << 8 | message[2], message[3] << 8 | message[4]);
                                draw = System.Drawing.Graphics.FromImage(canvas);
                                break;
                            case 2:
                                using (System.Drawing.Image tile = System.Drawing.Image.FromStream(new MemoryStream(message, 9, message.Length - 9)))
                                    draw.DrawImage(tile, message[1] << 8 | message[2], message[3] << 8 | message[4], tile.Width, tile.Height);
                                break;
                            case 3:
                                pictures++;
                                if (firstMs < 0) firstMs = clock.ElapsedMilliseconds;
                                break;
                            case 5:
                                heartbeats++;
                                break;
                        }
                    }
                    double seconds = Math.Max(0.001, (clock.ElapsedMilliseconds - firstMs) / 1000.0);
                    double cpu = (System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds / clock.ElapsedMilliseconds * 100;
                    report.AppendLine("First picture: " + (firstBytes / 1024) + " KB in " + firstMs + " ms");
                    report.AppendLine("After it: " + pictures + " changed pictures, " + heartbeats + " heartbeats, " + (laterBytes * 8 / 1000 / seconds).ToString("0") + " kbps on average");
                    report.AppendLine("CPU (this process: capture, compare, JPEG, and the pretend phone's decoding): " + cpu.ToString("0") + "% of one core");
                    check("mirror: the first picture arrives within 3 s", firstMs >= 0 && firstMs < 3000);
                    check("mirror: then it stays under the 4000 kbps asked for", laterBytes * 8 / 1000 / seconds <= 4000);
                }
                using (var grab = new ScreenGrab(1280))
                {
                    grab.Grab();
                    long difference = 0;
                    var data = canvas.LockBits(new System.Drawing.Rectangle(0, 0, canvas.Width, canvas.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                    var pixels = new int[canvas.Width * canvas.Height];
                    for (int row = 0; row < canvas.Height; row++)
                        System.Runtime.InteropServices.Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)row * data.Stride), pixels, row * canvas.Width, canvas.Width);
                    canvas.UnlockBits(data);
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        int a = pixels[i], b = grab.Pixels[i];
                        difference += Math.Abs((a & 255) - (b & 255)) + Math.Abs((a >> 8 & 255) - (b >> 8 & 255)) + Math.Abs((a >> 16 & 255) - (b >> 16 & 255));
                    }
                    double mean = difference / (pixels.Length * 3.0);
                    report.AppendLine("Phone's picture vs the screen now: " + mean.ToString("0.0") + " of 255 apart on average (JPEG, and whatever changed at the very end)");
                    check("mirror: the phone's picture matches the screen", mean < 12);
                }
            }
            catch (Exception ex) { check("mirror: " + ex.Message, false); }
            finally { server.Stop(); }
            report.AppendLine(failures == 0 ? "All passed" : failures + " failed");
            File.WriteAllText(path, report.ToString());
            return failures == 0 ? 0 : 1;
        }

        /// <summary>A phone's mirror connection, against a server on 47791: the first picture whole, then it keeps going.</summary>
        static void MirrorChecks(Action<string, bool> check, string id, byte[] pairKey)
        {
            using (var m = new TestPhone(id))
            {
                m.Hello(47791);
                m.Auth(pairKey);
                m.Send("MIRROR 1280 4000");
                var frames = new FrameReader(m.Stream, m.Keys.MirrorEnc, m.Keys.MirrorMac);
                byte[] start = frames.Next();
                int w = start != null && start.Length == 5 && start[0] == 1 ? start[1] << 8 | start[2] : 0, h = start != null && start.Length == 5 ? start[3] << 8 | start[4] : 0;
                check("mirror: it starts with the picture's size (" + w + "x" + h + ")", w > 0 && w <= 1280 && h > 0);
                int tiles = 0, area = 0;
                bool decoded = true;
                byte[] message = null;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 8000 && (message = frames.Next()) != null && message[0] != 3)
                {
                    if (message[0] != 2) continue;
                    tiles++;
                    area += (message[5] << 8 | message[6]) * (message[7] << 8 | message[8]);
                    try { using (System.Drawing.Image image = System.Drawing.Image.FromStream(new MemoryStream(message, 9, message.Length - 9))) { } }
                    catch (Exception) { decoded = false; }
                }
                check("mirror: the whole screen comes first, as JPEG strips that decode (" + tiles + " strips)", message != null && message[0] == 3 && area == w * h && decoded);
                byte[] next = frames.Next();
                check("mirror: then it keeps going (changes, the pointer, or a heartbeat each second)", next != null && next[0] >= 2 && next[0] <= 5);
            }
        }

        /// <summary>The phone's side of FrameWriter (ScreenLink.kt does the same): the next message, or null.</summary>
        internal sealed class FrameReader
        {
            readonly Stream _stream;
            readonly Ctr _cipher;
            readonly System.Security.Cryptography.HMACSHA256 _mac;
            ulong _number;

            public FrameReader(Stream stream, byte[] enc, byte[] mac)
            {
                _stream = stream;
                _cipher = new Ctr(enc);
                _mac = new System.Security.Cryptography.HMACSHA256(mac);
            }

            public byte[] Next()
            {
                byte[] head = Exactly(4);
                if (head == null) return null;
                int length = head[0] << 24 | head[1] << 16 | head[2] << 8 | head[3];
                if (length < Secure.TagBytes || length > 4 << 20) return null;
                byte[] body = Exactly(length);
                if (body == null) return null;
                int size = length - Secure.TagBytes;
                var counter = new byte[8];
                Ctr.BigEndian(_number, counter, 0);
                _mac.Initialize();
                _mac.TransformBlock(counter, 0, 8, null, 0);
                _mac.TransformFinalBlock(body, 0, size);
                if (!Secure.Same(Secure.Take(_mac.Hash, 0, Secure.TagBytes), Secure.Take(body, size, Secure.TagBytes))) return null;
                _cipher.Apply(_number, body, 0, size);
                _number++;
                return Secure.Take(body, 0, size);
            }

            byte[] Exactly(int count)
            {
                var buffer = new byte[count];
                try
                {
                    for (int done = 0; done < count;)
                    {
                        int n = _stream.Read(buffer, done, count - done);
                        if (n <= 0) return null;
                        done += n;
                    }
                }
                catch (IOException) { return null; }
                return buffer;
            }
        }

        static string TypingLine(string kind, string name, string value)
        {
            return "INPUT " + kind + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(name)) + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        /// <summary>
        /// CouchTV.exe --typingtest report.txt: shows a small window with a text box, a password box, a read-only box and
        /// a button (another CouchTV.exe, without taking the focus or typing anything), and checks which of them
        /// TypingWatch would pop the phone's keyboard up for, through UI Automation as with any app.
        /// </summary>
        public static int TypingTest(string path)
        {
            var report = new StringBuilder();
            int failures = 0;
            Action<string, bool> check = (name, ok) =>
            {
                report.AppendLine((ok ? "PASS  " : "FAIL  ") + name);
                if (!ok) failures++;
            };
            System.Diagnostics.Process target = null;
            try
            {
                target = System.Diagnostics.Process.Start(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName, "--typingtarget");
                var titled = new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty, "CouchTV typing test");
                System.Windows.Automation.AutomationElement root = null;
                for (int i = 0; i < 100 && root == null; i++)
                {
                    System.Threading.Thread.Sleep(100);
                    root = System.Windows.Automation.AutomationElement.RootElement.FindFirst(System.Windows.Automation.TreeScope.Children, titled);
                }
                if (root == null) throw new Exception("the test window didn't appear");
                var watch = new TypingWatch();
                Func<string, TypingField> describe = name =>
                {
                    var element = root.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                        new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty, name));
                    return element == null ? null : watch.Describe(element);
                };
                TypingField box = describe("Test box"), password = describe("Test password"), readOnly = describe("Test read-only"), button = describe("Test button");
                check("typing: a text box pops the keyboard up, with what's in it", box != null && !box.Password && box.Name == "Test box" && box.Value == "hello");
                check("typing: a password box too, without its value", password != null && password.Password && password.Value == "");
                check("typing: not a read-only box", readOnly == null);
                check("typing: not a button", button == null);
            }
            catch (Exception ex) { check("typing: " + ex.Message, false); }
            finally
            {
                try { if (target != null && !target.HasExited) target.Kill(); } catch (Exception) { }
            }
            report.AppendLine(failures == 0 ? "All passed" : failures + " failed");
            File.WriteAllText(path, report.ToString());
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// CouchTV.exe --touchtest report.txt: a small always-on-top window in the screen's top-left corner with a
        /// button, and one injected tap on that button (TouchInjector, as the screen mirror's touches): did the button
        /// get clicked? Touches nothing else.
        /// </summary>
        /// <summary>
        /// Real touches, as the screen mirror injects them, on the home screen in its small window (kept on top): a tap
        /// on a film opens it, a swipe along the row slides the row (settling on a card) and opens nothing, a swipe up
        /// slides the page. Nothing really opens. Needs suggestions (run the TV, or --suggesttest, once).
        /// </summary>
        public static int DragTest(string path)
        {
            var report = new StringBuilder();
            bool ok = true;
            Action<string, bool> check = (what, pass) =>
            {
                report.AppendLine((pass ? "PASS  " : "FAIL  ") + what);
                if (!pass) ok = false;
            };
            var opened = new List<string>();
            TouchInjector.AsMouse = false;
            var app = new Application();
            var home = new HomeWindow(Config.Load(), new HomeOptions { Windowed = true, Opened = what => { lock (opened) opened.Add(what); } });
            home.Topmost = true;
            int row = HomeWindow.MoviesRow;
            // A finger's path, on another thread so the window sees it arrive as a phone's would.
            Action<Point, Point, int> swipe = (from, to, steps) =>
            {
                Matrix device = PresentationSource.FromVisual(home).CompositionTarget.TransformToDevice;
                double sw = SystemParameters.PrimaryScreenWidth * device.M11, sh = SystemParameters.PrimaryScreenHeight * device.M22;
                Func<double, int> nx = x => (int)Math.Max(0, Math.Min(65535, x * 65535 / (sw - 1)));
                Func<double, int> ny = y => (int)Math.Max(0, Math.Min(65535, y * 65535 / (sh - 1)));
                new System.Threading.Thread(() =>
                {
                    TouchInjector.Touch("test", 0, 'D', nx(from.X), ny(from.Y));
                    for (int i = 1; i <= steps; i++)
                    {
                        System.Threading.Thread.Sleep(16);
                        TouchInjector.Touch("test", 0, 'M', nx(from.X + (to.X - from.X) * i / steps), ny(from.Y + (to.Y - from.Y) * i / steps));
                    }
                    System.Threading.Thread.Sleep(steps == 0 ? 60 : 8);
                    TouchInjector.Touch("test", 0, 'U', nx(to.X), ny(to.Y));
                }) { IsBackground = true }.Start();
            };
            home.ContentRendered += (s, e) =>
            {
                int step = 0;
                double slidRow = 0;
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                timer.Tick += (s2, e2) =>
                {
                    step++;
                    int count;
                    lock (opened) count = opened.Count;
                    if (step == 1)
                    {
                        if (home.SlideLimit(row) <= 0 || home.SlideLimit(-1) <= 0)
                        {
                            report.AppendLine("SKIP  no suggestions to slide (run --suggesttest first)");
                            timer.Stop();
                            app.Shutdown();
                            return;
                        }
                        Point poster = home.ScreenPoint(row, 112 + 300);   // the second film
                        swipe(poster, poster, 0);
                    }
                    else if (step == 2)
                    {
                        lock (opened) check("a tap on a film opens it (" + string.Join(", ", opened) + ")", count == 1);
                        check("...and doesn't slide the films (" + home.Slid(row) + ")", home.Slid(row) == 0);   // the page comes to their row, as for a click
                        swipe(home.ScreenPoint(row, 1500), home.ScreenPoint(row, 600), 12);
                    }
                    else if (step == 3)
                    {
                        slidRow = home.Slid(row);
                        double pitch = home.TilePitch(row), limit = home.SlideLimit(row);
                        double card = slidRow / pitch;
                        check("a swipe along the films slides them (" + Math.Round(slidRow) + " of " + Math.Round(limit) + ")", slidRow > 300);
                        check("...settling with a film at the start (" + Math.Round(card, 2) + " films along)",
                            Math.Abs(card - Math.Round(card)) < 0.01 || Math.Abs(slidRow - limit) < 1);
                        check("...and opens nothing", count == 1);
                        Point from = home.ScreenPoint(row, 900);
                        swipe(from, new Point(from.X, from.Y - 180), 12);   // up, over the films
                    }
                    else if (step == 4)
                    {
                        check("a swipe up slides the page (" + Math.Round(home.Slid(-1)) + ")", home.Slid(-1) > 100);
                        check("...not the films along (" + Math.Round(home.Slid(row)) + ")", Math.Abs(home.Slid(row) - slidRow) < 1);
                        check("...and opens nothing", count == 1);
                        timer.Stop();
                        app.Shutdown();
                    }
                };
                timer.Start();
            };
            app.Run(home);
            report.AppendLine(ok ? "All passed" : "FAILED");
            File.WriteAllText(path, report.ToString());
            return ok ? 0 : 1;
        }

        public static int TouchTest(string path, bool asMouse)
        {
            TouchInjector.AsMouse = asMouse;
            var report = new StringBuilder();
            int clicks = 0, downs = 0;
            var button = new System.Windows.Controls.Button { Content = "Tap", Width = 160, Height = 100 };
            button.Click += (s, e) => clicks++;
            button.PreviewTouchDown += (s, e) => downs++;
            button.PreviewMouseDown += (s, e) => report.AppendLine("  (mouse down promoted from the touch)");
            var window = new Window
            {
                Title = "CouchTV touch test", Content = button, Width = 200, Height = 160, Left = 0, Top = 0, Topmost = true,
                ShowActivated = false, WindowStyle = WindowStyle.ToolWindow, WindowStartupLocation = WindowStartupLocation.Manual,
            };
            var app = new Application();
            window.ContentRendered += (s, e) =>
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                int step = 0;
                timer.Tick += (s2, e2) =>
                {
                    step++;
                    if (step == 1)
                    {
                        report.AppendLine("Pointer before the touch: " + CursorState());
                        Point centre = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                        int sw = (int)SystemParameters.PrimaryScreenWidth, sh = (int)SystemParameters.PrimaryScreenHeight;
                        int nx = (int)(centre.X * 65535 / (sw - 1)), ny = (int)(centre.Y * 65535 / (sh - 1));
                        report.AppendLine("Tapping at " + (int)centre.X + "," + (int)centre.Y + " of " + sw + "x" + sh + (TouchInjector.AsMouse ? " (as the mouse)" : " (a real touch)"));
                        TouchInjector.Touch("test", 0, 'D', nx, ny);
                        System.Threading.Thread.Sleep(60);
                        TouchInjector.Touch("test", 0, 'U', nx, ny);
                    }
                    if (step == 2)
                    {
                        report.AppendLine("Pointer after the touch: " + CursorState());
                        Input.MoveBy(0, 0);
                        report.AppendLine("Pointer after a move to where it is: " + CursorState());
                        Input.MoveBy(40, 0);   // as the phone's trackpad moves it
                    }
                    if (step == 3)
                    {
                        report.AppendLine("Pointer after a trackpad move (absolute): " + CursorState());
                        Input.MouseMove(10, 0);   // a relative move, as a real mouse sends
                    }
                    if (step == 4)
                    {
                        report.AppendLine("Pointer after a relative move: " + CursorState());
                        timer.Stop();
                        report.AppendLine("Touches seen by the button: " + downs + ", clicks: " + clicks);
                        report.AppendLine(clicks == 1 ? "PASS" : "FAIL");
                        app.Shutdown();
                    }
                };
                timer.Start();
            };
            app.Run(window);
            File.WriteAllText(path, report.ToString());
            return clicks == 1 ? 0 : 1;
        }

        /// <summary>Whether Windows draws the pointer: shown, hidden, or suppressed (after touch, until mouse input).</summary>
        static string CursorState()
        {
            var info = new CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(CURSORINFO)) };
            if (!GetCursorInfo(ref info)) return "unknown";
            return (info.flags & 1) != 0 ? "shown" : (info.flags & 2) != 0 ? "suppressed" : "hidden";
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct CURSORINFO
        {
            public int cbSize, flags;
            public IntPtr hCursor;
            public int x, y;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO info);

        /// <summary>
        /// CouchTV.exe --browsertouchtest report.txt [--hold 60]: a small Edge window (a profile of its own) in the
        /// top-left corner, showing a page that counts pointer events and clicks in its title, and one injected tap in
        /// it. Reports what the page saw.
        /// </summary>
        public static int BrowserTouchTest(string path, int holdMs, string browserPath = null, bool drag = false)
        {
            var report = new StringBuilder();
            string edge = browserPath;
            if (edge == null) foreach (string p in new[] { @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe", @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe" })
                if (File.Exists(Environment.ExpandEnvironmentVariables(p))) edge = Environment.ExpandEnvironmentVariables(p);
            if (edge == null) { File.WriteAllText(path, "Edge isn't installed\n"); return 1; }
            string profile = Path.Combine(Path.GetTempPath(), "couchtv-touchtest-edge");
            string page = "data:text/html,<title>ready</title><body style='margin:0'><button id=b style='width:100vw;height:100vh;font-size:30px'>tap</button>" +
                          "<script>var n={};function c(k){n[k]=(n[k]||0)+1;document.title=JSON.stringify(n);}" +
                          "['pointerdown','pointerup','touchstart','touchend','mousedown','click'].forEach(function(k){document.addEventListener(k,function(e){c(k+(e.pointerType?':'+e.pointerType:''));},true);});" +
                          "document.body.style.height='400vh';window.addEventListener('scroll',function(){n.scrolled=Math.round(scrollY);document.title=JSON.stringify(n);});</script>";
            var start = new System.Diagnostics.ProcessStartInfo(edge, "--user-data-dir=\"" + profile + "\" --no-first-run --no-default-browser-check --window-position=0,0 --window-size=360,300 \"--app=" + page + "\"");
            Log.Info("touchtest: starting the browser");
            System.Diagnostics.Process browser = System.Diagnostics.Process.Start(start);
            Log.Info("touchtest: started");
            try
            {
                // Found by its title with plain Windows calls (UI Automation from inside a WPF app can hang).
                IntPtr window = IntPtr.Zero;
                for (int i = 0; i < 100 && window == IntPtr.Zero; i++)
                {
                    System.Threading.Thread.Sleep(150);
                    window = TestFindWindow(null, "ready");
                }
                Log.Info("touchtest: window " + window);
                if (window == IntPtr.Zero) { report.AppendLine("The test page didn't open"); File.WriteAllText(path, report.ToString()); return 1; }
                TEST_RECT wr;
                TestGetWindowRect(window, out wr);
                var r = new System.Windows.Rect(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);
                int x = (int)(r.Left + r.Width / 2), y = (int)(r.Top + r.Height * 0.6);
                int sw = (int)SystemParameters.PrimaryScreenWidth, sh = (int)SystemParameters.PrimaryScreenHeight;
                report.AppendLine("Edge window at " + r + "; tapping " + x + "," + y + " of " + sw + "x" + sh + ", held " + holdMs + " ms");
                int nx = (int)((long)x * 65535 / (sw - 1)), ny = (int)((long)y * 65535 / (sh - 1)), ny2 = ny;
                System.Threading.Thread.Sleep(800);
                Log.Info("touchtest: down");
                TouchInjector.Touch("test", 0, 'D', nx, ny);
                Log.Info("touchtest: down done");
                // As a phone does: moves every 16 ms while the finger's down (without moving; or, with a drag, 100 pixels up).
                int steps = Math.Max(1, holdMs / 16);
                for (int step = 1; step <= steps; step++)
                {
                    System.Threading.Thread.Sleep(16);
                    int dragged = drag ? 100 * step / steps : 0;
                    ny2 = (int)((long)(y - dragged) * 65535 / (sh - 1));
                    TouchInjector.Touch("test", 0, 'M', nx, ny2);
                }
                TouchInjector.Touch("test", 0, 'U', nx, ny2);
                Log.Info("touchtest: up done");
                System.Threading.Thread.Sleep(1000);
                var text = new StringBuilder(512);
                TestGetWindowText(window, text, text.Capacity);
                string title = text.ToString();
                report.AppendLine("The page saw: " + title);
                bool ok = drag ? title.Contains("scrolled") && !title.Contains("click") : title.Contains("click");
                report.AppendLine(ok ? "PASS" : "FAIL");
                File.WriteAllText(path, report.ToString());
                return ok ? 0 : 1;
            }
            finally
            {
                try { if (!browser.HasExited) browser.Kill(); } catch (Exception) { }
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName("msedge"))
                {
                    try { if (p.StartInfo != null && p.MainModule != null) { } } catch (Exception) { }
                }
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct TEST_RECT { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr TestFindWindow(string className, string title);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowRect")]
        static extern bool TestGetWindowRect(IntPtr window, out TEST_RECT rect);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int TestGetWindowText(IntPtr window, StringBuilder text, int most);

        /// <summary>The window --typingtest looks at: shown without taking the focus, gone after 20 s at the latest.</summary>
        public static int TypingTarget()
        {
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(10) };
            var box = new System.Windows.Controls.TextBox { Text = "hello" };
            System.Windows.Automation.AutomationProperties.SetName(box, "Test box");
            var password = new System.Windows.Controls.PasswordBox { Password = "secret" };
            System.Windows.Automation.AutomationProperties.SetName(password, "Test password");
            var readOnly = new System.Windows.Controls.TextBox { Text = "fixed", IsReadOnly = true };
            System.Windows.Automation.AutomationProperties.SetName(readOnly, "Test read-only");
            var button = new System.Windows.Controls.Button { Content = "Button" };
            System.Windows.Automation.AutomationProperties.SetName(button, "Test button");
            foreach (UIElement e in new UIElement[] { box, password, readOnly, button }) panel.Children.Add(e);
            var window = new Window
            {
                Title = "CouchTV typing test", Content = panel, Width = 240, Height = 200, Left = 0, Top = 0,
                ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            };
            var app = new Application();
            var quit = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            quit.Tick += (s, e) => app.Shutdown();
            quit.Start();
            return app.Run(window);
        }

        static string BoardTag(byte[] key, string message)
        {
            return Secure.Hex(Secure.Take(Secure.Hmac(key, Encoding.ASCII.GetBytes(message)), 0, Secure.TagBytes));
        }

        /// <summary>The phone's side of the Wi-Fi remote, as the app does it (WifiLink.kt), for the self-test.</summary>
        internal sealed class TestPhone : IDisposable
        {
            public readonly string Id;
            public string Answer;                      // the TV's HELLO2 line
            public byte[] PairKey, AudioEnc, AudioMac;
            public SessionKeys Keys;
            public Channel Channel;
            public Stream Stream { get { return _stream; } }
            System.Net.Sockets.TcpClient _tcp;
            Stream _stream;
            LineReader _lines;
            EcdhKey _key;
            byte[] _shared, _transcript, _tvPoint;

            public TestPhone(string id) { Id = id; }

            /// <summary>Connects and says hello: "KNOWN" or "PAIR" from a new TV, else whatever it said.</summary>
            public string Hello(int port) { return Hello(System.Net.IPAddress.Loopback, port); }

            /// <summary>The same, to a TV elsewhere on the Wi-Fi (TvLink).</summary>
            public string Hello(System.Net.IPAddress address, int port)
            {
                _tcp = new System.Net.Sockets.TcpClient();
                _tcp.Connect(address, port);
                _tcp.ReceiveTimeout = 5000;
                _stream = _tcp.GetStream();
                _lines = new LineReader(_stream, 65536);
                _key = new EcdhKey();
                string hello = "HELLO2 CouchTV-Remote test " + Id + " " + Convert.ToBase64String(_key.PublicPoint);
                Write(hello);
                Answer = Read();
                if (Answer == null) return null;
                string[] p = Answer.Split(' ');
                if (p[0] != "HELLO2" || p.Length < 5) return Answer;
                _tvPoint = Convert.FromBase64String(p[2]);
                _shared = _key.Agree(_tvPoint);
                _transcript = Secure.Sha256(Secure.Utf8(hello + "\n" + Answer));
                return p[3];
            }

            /// <summary>Pairs, typing the code code() gives once the TV shows one: "OK", or what the TV said instead.</summary>
            public string Pair(string name, Func<string> code)
            {
                Write("PAIR " + Convert.ToBase64String(Secure.Utf8(name)));
                string answer = Read();
                if (answer != "CODE") return answer;
                string typed = code();
                int number = int.Parse(typed);
                byte[] mine = _key.PublicPoint;
                for (int i = 0; i < RemoteServer.CodeBits; i++)
                {
                    int bit = (number >> i) & 1;
                    byte[] nonce = Secure.Random(16);
                    Write("C " + i + " " + Convert.ToBase64String(Secure.Commit(nonce, 'P', mine, _tvPoint, bit)));
                    string commit = Read();
                    if (commit == null || !commit.StartsWith("C " + i + " ")) return commit;
                    Write("R " + i + " " + Convert.ToBase64String(nonce));
                    string reveal = Read();
                    if (reveal == null || !reveal.StartsWith("R " + i + " ")) return reveal;
                    byte[] tvNonce = Convert.FromBase64String(reveal.Split(' ')[2]);
                    if (!Secure.Same(Secure.Commit(tvNonce, 'T', _tvPoint, mine, bit), Convert.FromBase64String(commit.Split(' ')[2])))
                        return "the TV's commitment didn't match";
                }
                PairKey = SessionKeys.PairKey(_shared, typed, _transcript);
                return "OK";
            }

            /// <summary>Proves it holds the pairing key; true once the TV proved it holds it too. Lines are encrypted after.</summary>
            public bool Auth(byte[] pairKey)
            {
                SessionKeys keys = SessionKeys.Derive(_shared, pairKey, _transcript);
                Write("AUTH " + Convert.ToBase64String(keys.PhoneProof));
                string answer = Read();
                if (answer == null || !answer.StartsWith("AUTH ") || !Secure.Same(Convert.FromBase64String(answer.Substring(5)), keys.TvProof)) return false;
                Channel = new Channel(keys.PhoneEnc, keys.PhoneMac, keys.TvEnc, keys.TvMac);
                Keys = keys;
                AudioEnc = keys.AudioEnc;
                AudioMac = keys.AudioMac;
                return true;
            }

            public void Send(string line) { Write(Channel.Seal(line)); }

            /// <summary>The next line, decrypted; null if the TV hung up or it didn't decrypt.</summary>
            public string Receive()
            {
                string line = Read();
                return line == null ? null : Channel.Open(line);
            }

            public void Write(string line)
            {
                byte[] bytes = Secure.Utf8(line + "\n");
                _stream.Write(bytes, 0, bytes.Length);
            }

            public string Read()
            {
                try { return _lines.Read(); }
                catch (IOException) { return null; }
            }

            public void Dispose()
            {
                if (_tcp != null) _tcp.Close();
                if (_key != null) _key.Dispose();
                if (Channel != null) Channel.Dispose();
            }
        }

        /// <summary>
        /// The Wi-Fi remote end to end on this PC, as the phone does it: discover, pair with the code the TV shows,
        /// connect again as a paired phone, press, send words; and that strangers, wrong codes, forged proofs, replayed
        /// or changed lines get nowhere.
        /// </summary>
        static void WifiRemoteTest(Action<string, bool> check)
        {
            var buttons = new List<string>();
            var words = new List<string>();
            var pointer = new List<string>();
            var pairing = new List<string>();
            var typed = new List<string>();
            var touches = new List<string>();
            var phonesNow = new List<int>();
            string shown = null;
            PhoneStore phones = PhoneStore.InMemory();
            var server = new RemoteServer(null, phones, 47790, 47791, System.Net.IPAddress.Loopback);
            server.Button += s => { lock (buttons) buttons.Add(s.Code + (s.IsRepeat ? " R" : " N")); };
            server.Move = (x, y) => { lock (pointer) pointer.Add("move " + x + " " + y); };
            server.Wheel = (d, h) => { lock (pointer) pointer.Add((h ? "hwheel " : "wheel ") + d); };
            server.MouseButton = (r, d) => { lock (pointer) pointer.Add((r ? "right " : "left ") + (d ? "down" : "up")); };
            server.PointerLook += (st, sz, co) => { lock (pointer) pointer.Add("look " + st + " " + sz + " " + co); };
            server.Text += t => { lock (words) words.Add(t); };
            server.KeyChange = (vk, down) => { lock (typed) typed.Add("key " + vk.ToString("X2") + (down ? " down" : " up")); };
            server.TypeText = (back, text) => { lock (typed) typed.Add("edit " + back + " " + text); };
            server.PhonesChanged += count => { lock (phonesNow) phonesNow.Add(count); };
            server.Touch = (owner, finger, kind, x, y) => { lock (touches) touches.Add(finger + "" + kind + " " + x + "," + y); };
            server.LiftTouches = owner => { lock (touches) touches.Add("lift"); };
            server.PairingCode += (name, digits) => { lock (pairing) { shown = digits; pairing.Add(name + " shown"); } };
            server.PairingEnded += (name, outcome) => { lock (pairing) pairing.Add(name + " " + outcome); };
            var played = new List<string>();
            server.Play += id => { lock (played) played.Add(id); };
            var trackpads = new List<bool>();
            server.Trackpad += open => { lock (trackpads) trackpads.Add(open); };
            var sleepTimers = new List<int>();
            server.SleepTimer += minutes => { lock (sleepTimers) sleepTimers.Add(minutes); };
            Func<string> code = () => { lock (pairing) return shown; };
            Func<string> wrongCode = () => { lock (pairing) return ((int.Parse(shown) + 1) % 1000000).ToString("D6"); };
            Func<string> pairingLog = () => { lock (pairing) return string.Join(", ", pairing); };
            const string idA = "0123456789abcdef0123456789abcdef", idB = "fedcba9876543210fedcba9876543210";
            server.Start();
            try
            {
                System.Threading.Thread.Sleep(300);
                string answer = null;
                using (var udp = new System.Net.Sockets.UdpClient())
                {
                    udp.Client.ReceiveTimeout = 2000;
                    byte[] ask = Encoding.ASCII.GetBytes("COUCHTV?");
                    udp.Send(ask, ask.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 47790));
                    var from = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                    try { answer = Encoding.UTF8.GetString(udp.Receive(ref from)); } catch (Exception) { }
                }
                check("Wi-Fi: the TV answers \"COUCHTV?\" with its port and name", answer == "COUCHTV 2 47791 " + RemoteServer.TvName);

                using (var looker = new TestPhone(idA)) looker.Hello(47791);   // a new phone that only looks, and goes
                using (var tcp = new System.Net.Sockets.TcpClient())
                {
                    tcp.Connect(System.Net.IPAddress.Loopback, 47791);
                    tcp.ReceiveTimeout = 2000;
                    var stream = tcp.GetStream();
                    byte[] hello = Encoding.ASCII.GetBytes("HELLO test 1\nPING\n");
                    stream.Write(hello, 0, hello.Length);
                    var reader = new StreamReader(stream);
                    string first = reader.ReadLine(), then = null;
                    try { then = reader.ReadLine(); } catch (IOException) { }
                    check("Wi-Fi: an app from before pairing is told to update, and gets nothing else", first == "UPDATE" && then == null);
                }
                check("Wi-Fi: a phone that only looks doesn't make the TV show a code", pairingLog() == "");

                using (var a = new TestPhone(idA))
                {
                    check("Wi-Fi: a new phone is asked to pair", a.Hello(47791) == "PAIR");
                    check("Wi-Fi: a wrong code is refused", a.Pair("Test phone", wrongCode) == "WRONG");
                }
                check("Wi-Fi: the TV showed a code, then said it was wrong", pairingLog() == "Test phone shown, Test phone wrong");

                byte[] pairKey;
                string session;
                server.CurrentSuggestions = () => new List<string> { "SUGGESTIONS 1", "SUGGEST S s1 SGF3YXllaW4= - WW91VHViZQ== - FF0000 FFFFFF" };
                server.CurrentSleepTimer = () => 2700;
                using (var a = new TestPhone(idA))
                {
                    a.Hello(47791);
                    string paired = a.Pair("Test phone", code);
                    check("Wi-Fi: the right code pairs, and both sides prove they hold the new key", paired == "OK" && a.Auth(a.PairKey));
                    pairKey = a.PairKey;
                    check("Wi-Fi: the TV remembers the phone", phones.Count == 1 && phones.Find(idA) != null && pairingLog().EndsWith("Test phone paired"));
                    a.Send("HELLO CouchTV-Remote test");
                    string welcome = a.Receive(), board = a.Receive(), audio = a.Receive();
                    session = audio != null && audio.StartsWith("AUDIO ") ? audio.Substring(6) : null;
                    check("Wi-Fi: HELLO gets WELCOME, the receiver box's key and a sound session, encrypted",
                        welcome == "WELCOME " + RemoteServer.TvName && board == "BOARD " + Secure.Hex(phones.BoardKey) && server.FindSession(session) != null);
                    string listed = a.Receive(), item = a.Receive();
                    check("Wi-Fi: ...and the suggestions, for the phone's first page", listed == "SUGGESTIONS 1" && item == "SUGGEST S s1 SGF3YXllaW4= - WW91VHViZQ== - FF0000 FFFFFF");
                    check("Wi-Fi: ...and the sleep timer", a.Receive() == "SLEEPTIMER 2700");
                    a.Send("PING");
                    check("Wi-Fi: PING gets PONG", a.Receive() == "PONG");
                    a.Send("SOUND");
                    check("Wi-Fi: SOUND gets the sound session (for the phone's sound service)", a.Receive() == "AUDIO " + session);
                    server.ShowTyping(new TypingField { Name = "Search", Value = "abc" });
                    check("Wi-Fi: a text box getting the focus on the TV is told to the phone", a.Receive() == TypingLine("TEXT", "Search", "abc"));
                    server.ShowTyping(new TypingField { Name = "Password", Password = true });
                    check("Wi-Fi: ...a password box too (its value never sent)", a.Receive() == TypingLine("PASSWORD", "Password", ""));
                    server.ShowTyping(null);
                    check("Wi-Fi: ...and when it loses it", a.Receive() == "INPUT OFF");
                    server.SendToPhones(new List<string> { "SUGGESTIONS 0" });
                    check("Wi-Fi: a new list of suggestions goes to the phone", a.Receive() == "SUGGESTIONS 0");
                    a.Send("PLAY s1");
                    a.Send("PLAY " + new string('x', 61));   // ignored: no id is that long
                    a.Send("TRACKPAD ON");                    // still open when the phone leaves
                    a.Send("SLEEPTIMER 95");
                    a.Send("SLEEPTIMER 0");
                    a.Send("SLEEPTIMER -5");                  // ignored
                    a.Send("SLEEPTIMER 99999");               // ignored: more than a day
                    a.Send("KEY A2 D");      // Ctrl, still held when the phone leaves
                    a.Send("KEY 43 D");
                    a.Send("KEY 43 U");
                    a.Send("KEY ZZ D");      // ignored
                    a.Send("KEY 00 D");      // ignored
                    a.Send("EDIT 2 " + Convert.ToBase64String(Encoding.UTF8.GetBytes("नमस्ते 👋")));
                    a.Send("EDIT 0");
                    a.Send("EDIT 9999 eA==");   // ignored: too many backspaces
                    a.Send("TOUCH 0 D 32768 16384");
                    a.Send("TOUCH 0 M 32800 16400");
                    a.Send("TOUCH 0 U 32800 16400");
                    a.Send("TOUCH 1 D 70000 0");    // ignored: off the screen
                    a.Send("TOUCH 12 D 10 10");     // ignored: ten fingers at most
                    a.Send("TOUCH 2 D 100 200");    // still down when the phone leaves
                    a.Send("BTN CE 05 N");
                    a.Send("BTN CE 06 R");
                    a.Send("BTN ZZ 06 N");   // ignored
                    a.Send("TEXT " + Convert.ToBase64String(Encoding.UTF8.GetBytes("पंचायत season 3")));
                    a.Send("MOVE 12 -7");
                    a.Send("MOVE 99999 0");   // ignored: far too big
                    a.Send("WHEEL -240 36");
                    a.Send("MOUSE L D");
                    a.Send("MOVE 3 4");
                    a.Send("MOUSE X D");     // ignored
                    a.Send("POINTER dot 96 yellow");
                    a.Send("PING");
                    a.Receive();             // everything before it has been handled
                }
                System.Threading.Thread.Sleep(300);    // the server notices the phone left
                server.CurrentSuggestions = null;
                server.CurrentSleepTimer = null;
                check("Wi-Fi: its sound session ends with the connection", server.FindSession(session) == null);
                lock (played) check("Wi-Fi: PLAY plays a suggestion (" + string.Join(", ", played) + ")", string.Join(", ", played) == "s1");
                lock (sleepTimers) check("Wi-Fi: SLEEPTIMER sets the sleep timer, any number of minutes, or 0 for off (" + string.Join(", ", sleepTimers) + ")",
                    string.Join(", ", sleepTimers) == "95, 0");
                lock (trackpads) check("Wi-Fi: the trackpad page open shows the pointer; the phone leaving hides it (" + string.Join(", ", trackpads) + ")",
                    string.Join(", ", trackpads) == "True, False");
                lock (buttons) check("Wi-Fi: buttons arrive as the IR receiver's codes", string.Join(", ", buttons) == "NEC 00CE 0005 N, NEC 00CE 0006 R");
                lock (words) check("Wi-Fi: words arrive whole, Hindi included", words.Count == 1 && words[0] == "पंचायत season 3");
                lock (pointer) check("Wi-Fi: the trackpad moves, scrolls and drags; a phone that leaves mid-drag lets go",
                    string.Join(", ", pointer) == "move 12 -7, wheel -240, hwheel 36, left down, move 3 4, look dot 96 yellow, left up");
                lock (typed) check("Wi-Fi: the keyboard's keys and typed words arrive; a phone that leaves lets go of its keys (" + string.Join(", ", typed) + ")",
                    string.Join(", ", typed) == "key A2 down, key 43 down, key 43 up, edit 2 नमस्ते 👋, edit 0 , key A2 up");
                lock (phonesNow) check("Wi-Fi: the TV knows when a phone is connected as a remote (it watches for text boxes meanwhile)",
                    string.Join(",", phonesNow) == "1,0");
                lock (touches) check("mirror: touches arrive; a phone that leaves lifts its fingers (" + string.Join(", ", touches) + ")",
                    string.Join(", ", touches).StartsWith("0D 32768,16384, 0M 32800,16400, 0U 32800,16400, 2D 100,200, lift"));

                server.ShowTyping(new TypingField { Name = "Search", Value = "" });   // no phone to tell yet

                using (var a = new TestPhone(idA))
                {
                    check("Wi-Fi: the paired phone comes back without pairing again", a.Hello(47791) == "KNOWN" && a.Auth(pairKey));
                    a.Send("HELLO CouchTV-Remote test");
                    a.Receive();
                    a.Receive();
                    a.Receive();
                    check("Wi-Fi: a phone that connects while a text box has the focus is told", a.Receive() == TypingLine("TEXT", "Search", ""));
                    server.ShowTyping(null);
                    a.Receive();
                    string ping = a.Channel.Seal("PING");
                    a.Write(ping);
                    check("Wi-Fi: ...and works", a.Receive() == "PONG");
                    a.Write(ping);
                    check("Wi-Fi: a replayed line ends the connection", a.Read() == null);
                }
                MirrorChecks(check, idA, pairKey);
                using (var a = new TestPhone(idA))
                {
                    a.Hello(47791);
                    a.Auth(pairKey);
                    string ping = a.Channel.Seal("PING");
                    byte[] bent = Convert.FromBase64String(ping);
                    bent[1] ^= 4;
                    a.Write(Convert.ToBase64String(bent));
                    check("Wi-Fi: a changed line ends the connection", a.Read() == null);
                }
                using (var a = new TestPhone(idA))
                    check("Wi-Fi: someone using the paired phone's id without its key gets nowhere", a.Hello(47791) == "KNOWN" && !a.Auth(Secure.Random(32)));
                using (var b = new TestPhone(idB))
                    check("Wi-Fi: a stranger can't skip pairing", b.Hello(47791) == "PAIR" && !b.Auth(pairKey));
                lock (buttons) buttons.Clear();
                using (var b = new TestPhone(idB))
                {
                    b.Hello(47791);
                    b.Write("BTN CE 05 N");
                }
                System.Threading.Thread.Sleep(100);
                lock (buttons) check("Wi-Fi: ...nor press buttons", buttons.Count == 0);

                using (var c = new TestPhone(idB))
                using (var d = new TestPhone("11111111111111111111111111111111"))
                {
                    c.Hello(47791);
                    c.Write("PAIR " + Convert.ToBase64String(Secure.Utf8("Phone C")));
                    bool showing = c.Read() == "CODE";
                    d.Hello(47791);
                    check("Wi-Fi: one pairing at a time", showing && d.Pair("Phone D", code) == "BUSY");
                }
                System.Threading.Thread.Sleep(200);
                check("Wi-Fi: a pairing the phone gave up on takes the code off the TV", pairingLog().EndsWith("Phone C shown, Phone C cancelled"));

                string last = null;
                for (int i = 0; i < RemoteServer.MostWrongCodes; i++)
                    using (var b = new TestPhone(idB))
                    {
                        b.Hello(47791);
                        last = b.Pair("Guesser", wrongCode);
                    }
                check("Wi-Fi: after " + RemoteServer.MostWrongCodes + " wrong codes, pairing waits (" + last + ")", last != null && last.StartsWith("WAIT "));

                using (var a = new TestPhone(idA))
                {
                    a.Hello(47791);
                    a.Auth(pairKey);
                    a.Send("PING");
                    a.Receive();
                    byte[] boardBefore = phones.BoardKey;
                    server.ForgetPhones();
                    check("Wi-Fi: forgetting the phones drops them and changes the receiver box's key",
                        a.Read() == null && phones.Count == 0 && !Secure.Same(boardBefore, phones.BoardKey));
                }
                using (var a = new TestPhone(idA))
                    check("Wi-Fi: ...and a forgotten phone has to pair again", a.Hello(47791) == "PAIR");
            }
            catch (Exception ex) { check("Wi-Fi: " + ex.Message, false); }
            finally { server.Stop(); }
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
