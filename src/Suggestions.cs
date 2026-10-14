using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace CouchTV
{
    internal enum SuggestionKind { Movie, Song }

    /// <summary>A film to watch or a song to play, from the home screen's suggestion rows or the phone's first page.</summary>
    internal sealed class Suggestion
    {
        public SuggestionKind Kind;
        public string Id;          // "m" + JustWatch's id, or "s" + Apple's: unique in the list (the phone plays by it)
        public string Title;       // "Drishyam 3", "Hawayein"
        public string Subtitle;    // "2026 · 2h 37m", "Pritam & Arijit Singh"
        public string App;         // the tile it opens in, e.g. "Netflix"
        public string ImageUrl;    // its poster or cover on the internet (the phone loads it from there)
        public string ImageFile;   // the same, downloaded for the TV: a file name in Suggestions.Folder
        public string PlayUrl;     // what the app opens: the film's own page, made to play; the song's YouTube video
        public string Query;       // a song: what to look for on YouTube, if its video wasn't found beforehand
    }

    /// <summary>
    /// What's popular, for the home screen and the phone remote: films streaming in India now (JustWatch's popularity,
    /// the list its website shows, with each film's own address in each app), and India's most-played songs (Apple
    /// Music's chart, from Apple's free feed), played on YouTube. Fetched every few hours on a background thread and
    /// kept on disk with the pictures, so the rows are there at once after a restart, and offline.
    /// </summary>
    internal static class Suggestions
    {
        public const int MostMovies = 20, MostSongs = 20;
        public static readonly TimeSpan FreshFor = TimeSpan.FromHours(6);
        const string JustWatch = "https://apis.justwatch.com/graphql";
        const string JustWatchImages = "https://images.justwatch.com";
        const string AppleChart = "https://rss.applemarketingtools.com/api/v2/in/music/most-played/50/songs.json";
        const string BrowserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
        const string ListHeader = "CouchTV suggestions 1";
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        // JustWatch's names for the apps CouchTV knows, and the address that tells their tile.
        static readonly string[][] Packages =
        {
            new[] { "nfx", "netflix.com" }, new[] { "prv", "primevideo.com" }, new[] { "jhs", "hotstar.com" },
            new[] { "zee", "zee5.com" }, new[] { "snl", "sonyliv.com" },
        };

        const string PopularQuery = @"query Popular($country: Country!, $language: Language!, $first: Int!, $filter: TitleFilter, $offers: OfferFilter!) {
  popularTitles(country: $country, filter: $filter, first: $first, sortBy: POPULAR, sortRandomSeed: 0) {
    edges { node { id
      content(country: $country, language: $language) { title originalReleaseYear runtime posterUrl(profile: S332, format: JPG) }
      offers(country: $country, platform: WEB, filter: $offers) { monetizationType standardWebURL package { shortName } }
    } }
  }
}";

        static Suggestions()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public static string Folder { get { return Path.Combine(Config.DataDir, "suggestions"); } }

        static string ListFile { get { return Path.Combine(Folder, "list.txt"); } }

        // ================================================================ fetching

        /// <summary>
        /// The lists the settings ask for, fresh, with their pictures downloaded, and saved. A list that couldn't be
        /// fetched keeps what it had (previous); if none could be, this throws, so it's tried again later. Problems
        /// worth knowing go in notes.
        /// </summary>
        public static List<Suggestion> Fetch(Config cfg, List<Suggestion> previous, StringBuilder notes)
        {
            var known = new Dictionary<string, Suggestion>();
            foreach (Suggestion s in previous) known[s.Id] = s;
            var list = new List<Suggestion>();
            var errors = new List<string>();
            int tried = 0;
            if (cfg.SuggestMovies)
            {
                tried++;
                try { list.AddRange(Movies(cfg, known, notes)); }
                catch (Exception ex)
                {
                    errors.Add("films: " + ex.Message);
                    list.AddRange(previous.FindAll(s => s.Kind == SuggestionKind.Movie));
                }
            }
            if (cfg.SuggestSongs)
            {
                tried++;
                try { list.AddRange(Songs(cfg, known, notes)); }
                catch (Exception ex)
                {
                    errors.Add("songs: " + ex.Message);
                    list.AddRange(previous.FindAll(s => s.Kind == SuggestionKind.Song));
                }
            }
            if (tried > 0 && errors.Count == tried) throw new Exception(string.Join("; ", errors));
            foreach (string e in errors) Note(notes, "Couldn't fetch the " + e);

            Directory.CreateDirectory(Folder);
            foreach (Suggestion s in list) Download(s, notes);
            Save(list, DateTime.UtcNow);
            Prune(list);
            return list;
        }

        static List<Suggestion> Movies(Config cfg, Dictionary<string, Suggestion> known, StringBuilder notes)
        {
            var apps = new Dictionary<string, Tile>();
            foreach (string[] p in Packages)
            {
                Tile t = SmartSearch.FindHost(cfg.AppTiles, p[1]);
                if (t != null) apps[p[0]] = t;
            }
            if (apps.Count == 0) return new List<Suggestion>();
            var packages = new List<string>(apps.Keys);
            var money = new[] { "FLATRATE", "FREE", "ADS" };
            var filter = new Dictionary<string, object>
            {
                { "objectTypes", new[] { "MOVIE" } }, { "packages", packages }, { "monetizationTypes", money },
                { "releaseYear", new Dictionary<string, object> { { "min", DateTime.Now.Year - 1 } } },   // the latest: this year's and last year's
            };
            var variables = new Dictionary<string, object>
            {
                { "country", "IN" }, { "language", "en" }, { "first", 80 }, { "filter", filter },
                { "offers", new Dictionary<string, object> { { "packages", packages }, { "monetizationTypes", money } } },
            };
            string body = Json.Serialize(new Dictionary<string, object> { { "query", PopularQuery }, { "variables", variables } });
            List<Suggestion> list = ParseMovies(Http("POST", JustWatch, body, 15000), apps, cfg.Subscriptions);

            // Prime Video's links go through a redirect, and only its final page takes autoplay.
            foreach (Suggestion s in list)
            {
                if (!s.PlayUrl.StartsWith("https://app.primevideo.com/", StringComparison.OrdinalIgnoreCase)) continue;
                Suggestion old;
                if (known.TryGetValue(s.Id, out old) && old.PlayUrl != null && old.PlayUrl.Contains("autoplay=1"))
                {
                    s.PlayUrl = old.PlayUrl;
                    continue;
                }
                try
                {
                    string page = Redirect(s.PlayUrl);
                    if (page != null) s.PlayUrl = page.Split('?')[0] + "?autoplay=1&t=0";
                }
                catch (Exception ex) { Note(notes, "Prime Video's page for " + s.Title + ": " + ex.Message); }
            }
            return list;
        }

        /// <summary>
        /// JustWatch's popular films, in its order, each with the best of your apps to watch it on: one you subscribe to
        /// (couchtv.ini Subscriptions; with none listed, any), otherwise one that has it free. Films only on apps you
        /// don't pay for are left out.
        /// </summary>
        internal static List<Suggestion> ParseMovies(string json, Dictionary<string, Tile> apps, List<string> subscriptions)
        {
            var root = Json.Deserialize<Dictionary<string, object>>(json);
            object[] errors = Arr(Get(root, "errors"));
            if (errors != null && errors.Length > 0) throw new Exception("JustWatch said: " + (Get(errors[0], "message") ?? "an error"));
            object[] edges = Arr(Get(Get(Get(root, "data"), "popularTitles"), "edges"));
            if (edges == null) throw new Exception("JustWatch sent no list");
            var list = new List<Suggestion>();
            foreach (object edge in edges)
            {
                object node = Get(edge, "node"), content = Get(node, "content");
                string id = Get(node, "id") as string, title = Get(content, "title") as string, poster = Get(content, "posterUrl") as string;
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title) || string.IsNullOrEmpty(poster)) continue;
                Tile app;
                string link;
                if (!BestOffer(Arr(Get(node, "offers")), apps, subscriptions, out app, out link)) continue;
                var details = new List<string>();
                int year = Int(Get(content, "originalReleaseYear")), minutes = Int(Get(content, "runtime"));
                if (year > 0) details.Add(year.ToString());
                if (minutes > 0) details.Add(minutes >= 60 ? minutes / 60 + "h " + minutes % 60 + "m" : minutes + "m");
                list.Add(new Suggestion
                {
                    Kind = SuggestionKind.Movie, Id = "m" + id, Title = title.Trim(), Subtitle = string.Join(" · ", details),
                    App = app.Name, ImageUrl = poster.StartsWith("http") ? poster : JustWatchImages + poster, PlayUrl = PlayableUrl(link),
                });
                if (list.Count == MostMovies) break;
            }
            return list;
        }

        static bool BestOffer(object[] offers, Dictionary<string, Tile> apps, List<string> subscriptions, out Tile app, out string link)
        {
            app = null;
            link = null;
            if (offers == null) return false;
            int best = 0;
            foreach (object o in offers)
            {
                string package = Get(Get(o, "package"), "shortName") as string, url = Get(o, "standardWebURL") as string, money = Get(o, "monetizationType") as string;
                Tile t;
                if (package == null || string.IsNullOrEmpty(url) || !apps.TryGetValue(package, out t)) continue;
                bool yours = subscriptions.Count == 0 || SmartSearch.IsSubscribed(t, subscriptions);
                bool free = money == "FREE" || money == "ADS";
                int score = yours ? (money == "FLATRATE" ? 3 : 2) : free ? 1 : 0;
                if (score <= best) continue;
                best = score;
                app = t;
                link = url;
            }
            return app != null;
        }

        /// <summary>
        /// A film's page in its app, made to start playing where the app allows it: Netflix's /watch/ page plays the
        /// film, JioHotstar's ends in /watch; others open the film's own page, one press of Play away.
        /// </summary>
        internal static string PlayableUrl(string url)
        {
            Match m = Regex.Match(url, @"^https?://(?:www\.)?netflix\.com/(?:[a-z]{2}(?:-[a-z]{2})?/)?title/(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) return "https://www.netflix.com/watch/" + m.Groups[1].Value;
            m = Regex.Match(url, @"^(https?://(?:www\.)?hotstar\.com/in/movies/[^/?#]+/\d+)(?:/watch)?(?:[?#].*)?$", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value + "/watch";
            return url;
        }

        static List<Suggestion> Songs(Config cfg, Dictionary<string, Suggestion> known, StringBuilder notes)
        {
            Tile youtube = SmartSearch.FindHost(cfg.AppTiles, "youtube.com");
            if (youtube == null) return new List<Suggestion>();
            List<Suggestion> list = ParseSongs(Http("GET", AppleChart, null, 15000), youtube);
            int missed = 0;
            foreach (Suggestion s in list)
            {
                Suggestion old;
                if (known.TryGetValue(s.Id, out old) && old.PlayUrl != null)
                {
                    s.PlayUrl = old.PlayUrl;
                    continue;
                }
                // Found now, so a song plays the moment it's chosen; one that isn't is looked for then.
                try { s.PlayUrl = FindVideo(s.Query); }
                catch (Exception) { missed++; }
                Thread.Sleep(300);   // gently: these are YouTube's own search pages
            }
            if (missed > 0) Note(notes, missed + " songs' videos weren't found yet");
            return list;
        }

        /// <summary>The chart's songs, at most two by each singer (a new album can fill half of it), with clean titles.</summary>
        internal static List<Suggestion> ParseSongs(string json, Tile app)
        {
            object[] results = Arr(Get(Get(Json.Deserialize<Dictionary<string, object>>(json), "feed"), "results"));
            if (results == null) throw new Exception("Apple sent no chart");
            var list = new List<Suggestion>();
            var perArtist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (object r in results)
            {
                string id = Get(r, "id") as string, name = Get(r, "name") as string, artist = Get(r, "artistName") as string, art = Get(r, "artworkUrl100") as string;
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(artist)) continue;
                string lead = LeadArtist(artist);
                int count;
                perArtist.TryGetValue(lead, out count);
                if (count >= 2) continue;
                perArtist[lead] = count + 1;
                string title = SongTitle(name);
                list.Add(new Suggestion
                {
                    Kind = SuggestionKind.Song, Id = "s" + id, Title = title, Subtitle = artist.Trim(), App = app.Name,
                    ImageUrl = string.IsNullOrEmpty(art) ? null : art.Replace("100x100bb", "400x400bb"), Query = title + " " + lead,
                });
                if (list.Count == MostSongs) break;
            }
            return list;
        }

        /// <summary>"Radhimaa (feat. Sai Smriti) [From "Think Indie"]" → "Radhimaa".</summary>
        internal static string SongTitle(string name)
        {
            string title = Regex.Replace(name, @"\s*[\(\[](?:feat\.?|ft\.?|with|from)\s[^\)\]]*[\)\]]", "", RegexOptions.IgnoreCase);
            title = Regex.Replace(title, @"\s+", " ").Trim();
            return title.Length > 0 ? title : name.Trim();
        }

        /// <summary>"Karan Aujla & MXRCI" → "Karan Aujla".</summary>
        internal static string LeadArtist(string artist)
        {
            return Regex.Split(artist, @"\s*(?:&|,|\bfeat\.?\s)\s*", RegexOptions.IgnoreCase)[0].Trim();
        }

        /// <summary>A song's YouTube video, as a mix of similar songs after it (as voice search plays songs); null if none.</summary>
        public static string FindVideo(string query)
        {
            SmartSearch.YouTubeVideo video = SmartSearch.FirstYouTubeVideo(query, false);
            return video == null ? null : "https://www.youtube.com/watch?v=" + video.Id + "&list=RD" + video.Id;
        }

        static void Download(Suggestion s, StringBuilder notes)
        {
            if (string.IsNullOrEmpty(s.ImageUrl)) return;
            string name = Hash(s.ImageUrl) + ".jpg", path = Path.Combine(Folder, name);
            if (!File.Exists(path))
            {
                try
                {
                    File.WriteAllBytes(path + ".part", HttpBytes(s.ImageUrl, 15000));
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(path + ".part", path);
                }
                catch (Exception ex)
                {
                    Note(notes, "The picture for " + s.Title + ": " + ex.Message);
                    return;
                }
            }
            s.ImageFile = name;
        }

        /// <summary>Deletes pictures no suggestion uses any more.</summary>
        static void Prune(List<Suggestion> list)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Suggestion s in list) if (s.ImageFile != null) used.Add(s.ImageFile);
            foreach (string file in Directory.GetFiles(Folder))
            {
                string name = Path.GetFileName(file);
                if (name.Equals("list.txt", StringComparison.OrdinalIgnoreCase) || used.Contains(name)) continue;
                try { File.Delete(file); } catch (Exception) { }
            }
        }

        // ================================================================ the list on disk

        /// <summary>The list saved last time, and when it was fetched (UTC; MinValue if there's none).</summary>
        public static List<Suggestion> Load(out DateTime fetched)
        {
            fetched = DateTime.MinValue;
            var list = new List<Suggestion>();
            try
            {
                if (!File.Exists(ListFile)) return list;
                string[] lines = File.ReadAllLines(ListFile, Encoding.UTF8);
                if (lines.Length == 0 || !lines[0].StartsWith(ListHeader + "\t")) return list;
                long ticks;
                if (long.TryParse(lines[0].Substring(ListHeader.Length + 1), out ticks)) fetched = new DateTime(ticks, DateTimeKind.Utc);
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] f = lines[i].Split('\t');
                    if (f.Length < 9 || (f[0] != "M" && f[0] != "S")) continue;
                    list.Add(new Suggestion
                    {
                        Kind = f[0] == "M" ? SuggestionKind.Movie : SuggestionKind.Song, Id = f[1], Title = f[2], Subtitle = f[3], App = f[4],
                        ImageUrl = Null(f[5]), ImageFile = Null(f[6]), PlayUrl = Null(f[7]), Query = Null(f[8]),
                    });
                }
            }
            catch (Exception ex) { Log.Info("Suggestions: reading the saved list: " + ex.Message); }
            return list;
        }

        static void Save(List<Suggestion> list, DateTime fetched)
        {
            var text = new StringBuilder(ListHeader + "\t" + fetched.Ticks + "\n");
            foreach (Suggestion s in list)
            {
                text.Append(string.Join("\t", new[]
                {
                    s.Kind == SuggestionKind.Movie ? "M" : "S", Field(s.Id), Field(s.Title), Field(s.Subtitle), Field(s.App),
                    Field(s.ImageUrl), Field(s.ImageFile), Field(s.PlayUrl), Field(s.Query),
                })).Append('\n');
            }
            File.WriteAllText(ListFile + ".part", text.ToString(), new UTF8Encoding(false));
            if (File.Exists(ListFile)) File.Delete(ListFile);
            File.Move(ListFile + ".part", ListFile);
        }

        static string Field(string s) { return s == null ? "" : Regex.Replace(s, @"[\t\r\n]+", " "); }

        static string Null(string s) { return s.Length == 0 ? null : s; }

        // ================================================================ for the phone

        /// <summary>
        /// The list as the phone remote's first page gets it:
        ///   SUGGESTSETUP &lt;films Y|N&gt; &lt;songs Y|N&gt; &lt;apps&gt;: what the TV has, so the phone can fetch the same lists itself
        ///     while the TV is off. Each app as key:name:label:colour:text colour:Y|N (JustWatch's name for it, or
        ///     youtube; Y for one you subscribe to, or any with no Subscriptions), comma separated; "-" for none.
        ///   SUGGESTIONS &lt;count&gt;, then that many
        ///   SUGGEST M|S &lt;id&gt; &lt;title&gt; &lt;details&gt; &lt;app&gt; &lt;picture&gt; &lt;colour&gt; &lt;text colour&gt; &lt;app key&gt; &lt;link&gt; &lt;query&gt;
        /// Texts in base64 UTF-8, "-" for none; colours RRGGBB. The phone plays one with PLAY (FromPhone).
        /// </summary>
        public static List<string> PhoneLines(List<Suggestion> list, Config cfg, Func<string, Tile> tile)
        {
            var apps = new List<string>();
            var keys = new HashSet<string>();
            foreach (Tile t in cfg.AppTiles)
            {
                string key = AppKey(t);
                if (key == null || !keys.Add(key)) continue;   // the first tile for each, as the TV uses
                bool yours = cfg.Subscriptions.Count == 0 || SmartSearch.IsSubscribed(t, cfg.Subscriptions);
                apps.Add(key + ":" + B64(t.Name) + ":" + B64(t.Label) + ":" + Rgb(t.Background) + ":" + Rgb(t.Foreground) + ":" + (yours ? "Y" : "N"));
            }
            var lines = new List<string>
            {
                "SUGGESTSETUP " + (cfg.SuggestMovies ? "Y" : "N") + " " + (cfg.SuggestSongs ? "Y" : "N") + " " + (apps.Count == 0 ? "-" : string.Join(",", apps)),
            };
            var items = new List<string>();
            foreach (Suggestion s in list)
            {
                Tile app = tile(s.App);
                if (app == null) continue;
                items.Add("SUGGEST " + (s.Kind == SuggestionKind.Movie ? "M" : "S") + " " + s.Id + " " + B64(s.Title) + " " + B64(s.Subtitle) + " " +
                          B64(app.Label) + " " + B64(s.ImageUrl) + " " + Rgb(app.Background) + " " + Rgb(app.Foreground) + " " +
                          (AppKey(app) ?? "-") + " " + B64(s.PlayUrl) + " " + B64(s.Query));
            }
            lines.Add("SUGGESTIONS " + items.Count);
            lines.AddRange(items);
            return lines;
        }

        /// <summary>JustWatch's name for a tile's app ("nfx"), or "youtube"; null for others.</summary>
        internal static string AppKey(Tile t)
        {
            var one = new List<Tile> { t };
            foreach (string[] p in Packages) if (SmartSearch.FindHost(one, p[1]) != null) return p[0];
            return SmartSearch.FindHost(one, "youtube.com") != null ? "youtube" : null;
        }

        static Tile TileForKey(string key, List<Tile> apps)
        {
            if (key == "youtube") return SmartSearch.FindHost(apps, "youtube.com");
            foreach (string[] p in Packages) if (p[0] == key) return SmartSearch.FindHost(apps, p[1]);
            return null;
        }

        /// <summary>
        /// A film or song the phone found itself, while the TV was off ("PLAY &lt;id&gt; M|S &lt;app key&gt; &lt;link&gt; &lt;title&gt;
        /// &lt;query&gt;", split, without PLAY): played only on one of the TV's apps, and only from that app's own site
        /// (YouTube: a video); null if it isn't. A song with no link is looked for on YouTube when it's played.
        /// </summary>
        internal static Suggestion FromPhone(string[] p, List<Tile> apps)
        {
            if (p.Length < 6 || p[0].Length == 0 || (p[1] != "M" && p[1] != "S")) return null;
            Tile app = TileForKey(p[2], apps);
            if (app == null) return null;
            string link = Text(p[3]), title = Text(p[4]), query = Text(p[5]);
            bool movie = p[1] == "M";
            if (link == null ? movie || string.IsNullOrEmpty(query) : !OnSite(link, p[2])) return null;
            return new Suggestion
            {
                Kind = movie ? SuggestionKind.Movie : SuggestionKind.Song, Id = p[0], Title = title ?? "", App = app.Name,
                PlayUrl = link == null ? null : PlayableUrl(link), Query = query,
            };
        }

        static bool OnSite(string link, string key)
        {
            Uri uri;
            if (!Uri.TryCreate(link, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps || link.Length > 2000) return false;
            string site = key == "youtube" ? "youtube.com" : null;
            foreach (string[] p in Packages) if (p[0] == key) site = p[1];
            string host = uri.Host.ToLowerInvariant();
            if (site == null || (host != site && !host.EndsWith("." + site))) return false;
            return key != "youtube" || uri.AbsolutePath == "/watch";
        }

        static string Text(string field)
        {
            if (field == "-") return null;
            try { return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(field)); }
            catch (Exception) { return null; }
        }

        static string B64(string s) { return string.IsNullOrEmpty(s) ? "-" : Convert.ToBase64String(Encoding.UTF8.GetBytes(s)); }

        static string Rgb(System.Windows.Media.Color c) { return c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        // ================================================================ helpers

        static void Note(StringBuilder notes, string text)
        {
            if (notes != null) notes.Append(text).Append(". ");
        }

        static string Hash(string text)
        {
            using (var sha = SHA1.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var b = new StringBuilder();
                for (int i = 0; i < 10; i++) b.Append(digest[i].ToString("x2"));
                return b.ToString();
            }
        }

        static HttpWebRequest Request(string method, string url, int timeoutMs)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.UserAgent = BrowserAgent;
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            return request;
        }

        static string Http(string method, string url, string body, int timeoutMs)
        {
            HttpWebRequest request = Request(method, url, timeoutMs);
            request.Accept = "application/json";
            try
            {
                if (body != null)
                {
                    byte[] data = Encoding.UTF8.GetBytes(body);
                    request.ContentType = "application/json";
                    request.ContentLength = data.Length;
                    using (Stream s = request.GetRequestStream()) s.Write(data, 0, data.Length);
                }
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                throw new Exception(response != null ? (int)response.StatusCode + " " + response.StatusDescription : ex.Status == WebExceptionStatus.Timeout ? "timed out" : ex.Message);
            }
        }

        static byte[] HttpBytes(string url, int timeoutMs)
        {
            using (var response = (HttpWebResponse)Request("GET", url, timeoutMs).GetResponse())
            using (Stream s = response.GetResponseStream())
            using (var copy = new MemoryStream())
            {
                s.CopyTo(copy);
                return copy.ToArray();
            }
        }

        /// <summary>Where an address redirects to (its Location), without following it; null if it doesn't.</summary>
        static string Redirect(string url)
        {
            HttpWebRequest request = Request("GET", url, 10000);
            request.AllowAutoRedirect = false;
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    string location = response.Headers["Location"];
                    return string.IsNullOrEmpty(location) ? null : new Uri(new Uri(url), location).AbsoluteUri;
                }
            }
            catch (WebException ex)
            {
                throw new Exception(ex.Status == WebExceptionStatus.Timeout ? "timed out" : ex.Message);
            }
        }

        static object Get(object node, string key)
        {
            var d = node as Dictionary<string, object>;
            object value;
            return d != null && d.TryGetValue(key, out value) ? value : null;
        }

        static object[] Arr(object node)
        {
            var array = node as object[];
            if (array != null) return array;
            var list = node as System.Collections.ArrayList;
            return list == null ? null : list.ToArray();
        }

        static int Int(object value)
        {
            try { return value == null ? 0 : Convert.ToInt32(value); }
            catch (Exception) { return 0; }
        }
    }
}
