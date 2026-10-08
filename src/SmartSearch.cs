using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CouchTV
{
    internal enum SearchKind { AppSearch, Play, OpenApp, Action }

    /// <summary>One thing a search can lead to: search inside an app, play something, open an app, or a TV action.</summary>
    internal sealed class SearchOption
    {
        public SearchKind Kind;
        public Tile Tile;          // AppSearch, OpenApp
        public string Query;       // AppSearch: the words to search for in the app
        public string Action;      // Action: sleep | sleeptimer | volume | mute | home
        public int Value;          // minutes, or volume percent
        public string Label;       // Action: what the card says
        public char Glyph;         // Action: its icon
        public string Caption;     // under the card, e.g. "Streams here"
        public string Url;         // Play: the page that plays it, e.g. a YouTube video
        public string Title;       // Play: what will play, e.g. the video's title
        public string Thumbnail;   // Play: its picture
    }

    /// <summary>What the search made of the words: a summary and the options, best first.</summary>
    internal sealed class SearchPlan
    {
        public string Summary = "";
        public string SearchText;         // the cleaned-up words (e.g. just the title), for apps it didn't suggest
        public readonly List<SearchOption> Options = new List<SearchOption>();
        public string Source = "rules";   // "Gemini" when the AI understood the words
        public bool UsedTmdb;             // where-to-watch came from TMDB (JustWatch data, which needs a credit)
        public string Note;               // why AI wasn't used, for the screen and the log
        public string Raw;                // the AI's answer, for --searchtest
    }

    /// <summary>What the words ask for, from the AI or from simple rules.</summary>
    internal sealed class SearchIntent
    {
        public string Intent = "unknown";   // watch | music | video | live | open_app | action | web | unknown
        public string Title = "", Kind = "unknown", SearchText = "", Action = "none";
        public int Year, Season, Episode, Value;
        public bool Play, Newest;           // start playing straight away; prefer the newest video
        public readonly List<string> Apps = new List<string>();
    }

    /// <summary>
    /// AI search. Gemini (free tier) works out what the words ask for: a show or film, songs, a video, live sport,
    /// opening an app, or a TV action, in English, Hindi or Hinglish. For shows and films TMDB then says where they
    /// stream in India today (its data comes from JustWatch), because no AI knows current catalogues. Without keys,
    /// or offline, simple rules do what they can and the search screen falls back to the plain app row.
    /// Runs on a background thread; it only reads the config.
    /// </summary>
    internal static class SmartSearch
    {
        const string GeminiBase = "https://generativelanguage.googleapis.com/v1beta/models/";
        const string TmdbBase = "https://api.tmdb.org/3";   // TMDB's second domain, for ISPs that block themoviedb.org
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        static SmartSearch()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public static SearchPlan Plan(string words, Config cfg, ApiKeys keys)
        {
            var plan = new SearchPlan();
            words = (words ?? "").Trim();
            if (words.Length == 0) return plan;

            // "Netflix", "open Netflix", "Netflix kholo": no need to ask anyone.
            Tile direct = OpenCommand(words, cfg.AppTiles);
            if (direct != null)
            {
                plan.Summary = "Open " + direct.Label;
                plan.Options.Add(new SearchOption { Kind = SearchKind.OpenApp, Tile = direct, Caption = "Open" });
                return plan;
            }

            SearchIntent intent = null;
            if (!cfg.SmartSearch) plan.Note = "AI search is off in couchtv.ini";
            else if (string.IsNullOrEmpty(keys.Gemini)) plan.Note = "AI search isn't set up (no Gemini key)";
            else
            {
                try
                {
                    string raw;
                    intent = AskGemini(words, cfg, keys.Gemini, out raw);
                    plan.Raw = raw;
                    plan.Source = "Gemini";
                }
                catch (Exception ex)
                {
                    plan.Note = "AI search didn't answer: " + ex.Message;
                    Log.Info("Gemini: " + ex.Message);
                }
            }
            if (intent == null) intent = Rules(words, cfg.AppTiles, !string.IsNullOrEmpty(keys.Tmdb));
            Build(plan, intent, words, cfg, keys);
            Log.Info("Search \"" + words + "\" (" + plan.Source + (plan.UsedTmdb ? " + TMDB" : "") + "): " + plan.Summary + " -> " + Describe(plan));
            return plan;
        }

        public static string Describe(SearchPlan plan)
        {
            var parts = new List<string>();
            foreach (SearchOption o in plan.Options)
            {
                parts.Add(o.Kind == SearchKind.Action ? o.Action + (o.Value != 0 ? " " + o.Value : "")
                    : o.Kind == SearchKind.OpenApp ? "open " + o.Tile.Name
                    : o.Kind == SearchKind.Play ? "play on " + o.Tile.Name + " “" + o.Title + "” " + o.Url
                    : o.Tile.Name + " \"" + o.Query + "\"" + (string.IsNullOrEmpty(o.Caption) ? "" : " (" + o.Caption + ")"));
            }
            return parts.Count == 0 ? "(no suggestion)" : string.Join(", ", parts);
        }

        // ================================================================ turning an intent into options

        internal static void Build(SearchPlan plan, SearchIntent intent, string words, Config cfg, ApiKeys keys)
        {
            string text = intent.SearchText.Trim().Length > 0 ? intent.SearchText.Trim() : intent.Title.Trim().Length > 0 ? intent.Title.Trim() : words;
            plan.SearchText = text;
            switch (intent.Intent)
            {
                case "action":
                    SearchOption action = ActionOption(intent.Action, intent.Value);
                    if (action != null)
                    {
                        plan.Summary = action.Label;
                        plan.Options.Add(action);
                    }
                    return;

                case "open_app":
                    foreach (string name in intent.Apps)
                    {
                        Tile t = FindApp(name, cfg.AppTiles);
                        if (t != null && !Has(plan, t)) plan.Options.Add(new SearchOption { Kind = SearchKind.OpenApp, Tile = t, Caption = "Open" });
                    }
                    if (plan.Options.Count > 0) plan.Summary = "Open " + plan.Options[0].Tile.Label;
                    return;

                case "watch":
                    plan.Summary = intent.Title.Length > 0 ? intent.Title : text;
                    List<KeyValuePair<Tile, string>> where = null;
                    if (!string.IsNullOrEmpty(keys.Tmdb) && intent.Title.Length > 0)
                    {
                        try { where = WhereToWatch(intent, cfg.AppTiles, keys.Tmdb, plan); }
                        catch (Exception ex)
                        {
                            Log.Info("TMDB: " + ex.Message);
                            if (plan.Note == null) plan.Note = "Couldn't check where it streams: " + ex.Message;
                        }
                    }
                    if (where == null || where.Count == 0)
                    {
                        where = new List<KeyValuePair<Tile, string>>();
                        foreach (string name in intent.Apps)
                        {
                            Tile t = FindApp(name, cfg.AppTiles);
                            if (t != null) where.Add(new KeyValuePair<Tile, string>(t, "Likely here"));
                        }
                    }
                    where = KeepSubscribed(where, cfg.Subscriptions);
                    foreach (KeyValuePair<Tile, string> pair in where) AddSearch(plan, pair.Key, text, pair.Value);
                    if (intent.Season > 0) plan.Summary += " · season " + intent.Season;
                    return;

                case "music":
                case "video":
                case "live":
                case "web":
                    foreach (string name in intent.Apps)
                    {
                        Tile t = FindApp(name, cfg.AppTiles);
                        if (t == null) continue;
                        // "Play some music": find the video on YouTube and play it, instead of showing results.
                        if (intent.Play && intent.Intent != "web" && IsHost(t, "youtube.com") && !Has(plan, t) && AddYouTubePlay(plan, t, text, intent)) continue;
                        AddSearch(plan, t, text, Caption(intent.Intent));
                    }
                    if (plan.Options.Count == 0)
                    {
                        // The AI named no app we have: songs and videos go to YouTube, facts to the web.
                        Tile fallback = intent.Intent == "web" ? FindKind(cfg.AppTiles, TileKind.Browser) : FindHost(cfg.AppTiles, "youtube.com");
                        if (fallback != null) AddSearch(plan, fallback, text, Caption(intent.Intent));
                    }
                    plan.Summary = text;
                    return;
            }
        }

        /// <summary>
        /// Plays the first YouTube video for the words: songs as an endless mix of similar songs, other videos on
        /// their own. False (so the caller shows search results instead) if YouTube couldn't be read.
        /// </summary>
        static bool AddYouTubePlay(SearchPlan plan, Tile t, string query, SearchIntent intent)
        {
            YouTubeVideo video;
            try { video = FirstYouTubeVideo(query, intent.Newest); }
            catch (Exception ex)
            {
                Log.Info("YouTube search: " + ex.Message);
                return false;
            }
            if (video == null) return false;
            bool music = intent.Intent == "music";
            plan.Options.Add(new SearchOption
            {
                Kind = SearchKind.Play, Tile = t, Query = query, Title = video.Title, Thumbnail = video.Thumbnail,
                Url = "https://www.youtube.com/watch?v=" + video.Id + (music ? "&list=RD" + video.Id : ""),   // RD: YouTube's mix
                Caption = music ? "Plays a mix" : "Plays now",
            });
            return true;
        }

        static string Caption(string intent)
        {
            switch (intent)
            {
                case "music": return "Songs";
                case "video": return "Videos";
                case "live": return "Live";
                case "web": return "Look it up";
                default: return "";
            }
        }

        static void AddSearch(SearchPlan plan, Tile t, string query, string caption)
        {
            if (Has(plan, t) || Launcher.SearchTemplate(t) == null) return;
            plan.Options.Add(new SearchOption { Kind = SearchKind.AppSearch, Tile = t, Query = query, Caption = caption });
        }

        static bool Has(SearchPlan plan, Tile t)
        {
            foreach (SearchOption o in plan.Options) if (o.Tile == t) return true;
            return false;
        }

        /// <summary>With subscriptions listed in couchtv.ini, paid apps you don't have are dropped, unless that leaves nothing.</summary>
        static List<KeyValuePair<Tile, string>> KeepSubscribed(List<KeyValuePair<Tile, string>> where, List<string> subscriptions)
        {
            if (subscriptions.Count == 0) return where;
            var kept = where.FindAll(p => p.Value.StartsWith("Free") || Launcher.SearchTemplate(p.Key) != null && IsHost(p.Key, "youtube.com")
                || subscriptions.Exists(s => FindApp(s, new List<Tile> { p.Key }) != null));
            return kept.Count > 0 ? kept : where;
        }

        internal static SearchOption ActionOption(string action, int value)
        {
            switch (action)
            {
                case "sleep": return new SearchOption { Kind = SearchKind.Action, Action = "sleep", Label = "Turn off the TV (sleep)", Glyph = '' };
                case "sleep_timer":
                    int minutes = Math.Max(1, Math.Min(value <= 0 ? 30 : value, 600));
                    return new SearchOption { Kind = SearchKind.Action, Action = "sleeptimer", Value = minutes, Label = "Sleep in " + minutes + " minutes", Glyph = '' };
                case "volume":
                    int level = Math.Max(0, Math.Min(value, 100));
                    return new SearchOption { Kind = SearchKind.Action, Action = "volume", Value = level, Label = "Volume " + level + "%", Glyph = '' };
                case "mute": return new SearchOption { Kind = SearchKind.Action, Action = "mute", Label = "Mute", Glyph = '' };
                case "home": return new SearchOption { Kind = SearchKind.Action, Action = "home", Label = "Home screen", Glyph = '' };
                default: return null;
            }
        }

        // ================================================================ Gemini

        const string Instructions =
@"You are the search brain of a TV in India. People speak or type requests in English, Hindi or Hinglish, often with speech-recognition mistakes. Work out what they want.

intent:
- watch: a specific film or series. title = its official English title (as on TMDB or IMDb), kind = movie or tv, year if you know it, season if they asked for one.
- music: songs, singers, albums, bhajans, playlists.
- video: other videos: trailers, clips, news, recipes, tutorials, comedy, a YouTuber.
- live: live sport or a live TV channel.
- open_app: just open one of the TV's apps.
- action: a TV command. action = sleep (turn the TV off now), sleep_timer (value = minutes), volume (value = 0 to 100), mute, or home.
- web: facts, weather, scores, anything to look up.
- unknown: you can't tell.

search_text: what to type into the app's search box. For watch, only the title (no season, no 'watch'). For music and video, a short clean query, e.g. 'Arijit Singh songs'. For vague music requests ('play some music', 'gaane lagao') pick a popular query an Indian viewer would like, e.g. 'latest Hindi songs'. Fix obvious speech-recognition mistakes in titles and names.
play: true when they want something to start playing now ('play', 'chalao', 'lagao', 'sunao', or naming a song, a singer or one particular video). False when they want to look through results ('search', 'dikhao', 'show me', 'videos about').
newest: true when they want the latest or newest video (e.g. a YouTuber's latest upload).
episode: the episode number if they asked for one, else 0.
apps: names from the given app list only, best first: where this is most likely available in India today. Use YouTube for music and video, Web for web. Leave it empty if nothing fits.
Use empty strings, 0, 'unknown' or 'none' for fields that don't apply.

Examples:
'panchayat ka season 3 lagao' -> watch, title 'Panchayat', kind tv, year 2020, season 3, search_text 'Panchayat', apps ['Prime Video'], play true
'arijit singh ke gaane' -> music, search_text 'Arijit Singh songs', apps ['YouTube'], play true
'play some music' -> music, search_text 'latest Hindi songs', apps ['YouTube'], play true
'mr beast ka latest video' -> video, search_text 'MrBeast', apps ['YouTube'], play true, newest true
'cooking videos dikhao' -> video, search_text 'cooking recipes', apps ['YouTube'], play false
'netflix kholo' -> open_app, apps ['Netflix']
'aadhe ghante baad TV band kar do' -> action sleep_timer, value 30
'kal mausam kaisa rahega' -> web, search_text 'weather tomorrow', apps ['Web']";

        static readonly object Schema = new Dictionary<string, object>
        {
            { "type", "OBJECT" },
            { "properties", new Dictionary<string, object>
                {
                    { "intent", SchemaEnum("watch", "music", "video", "live", "open_app", "action", "web", "unknown") },
                    { "title", SchemaType("STRING") },
                    { "kind", SchemaEnum("movie", "tv", "unknown") },
                    { "year", SchemaType("INTEGER") },
                    { "season", SchemaType("INTEGER") },
                    { "episode", SchemaType("INTEGER") },
                    { "search_text", SchemaType("STRING") },
                    { "apps", new Dictionary<string, object> { { "type", "ARRAY" }, { "items", SchemaType("STRING") } } },
                    { "action", SchemaEnum("none", "sleep", "sleep_timer", "volume", "mute", "home") },
                    { "value", SchemaType("INTEGER") },
                    { "play", SchemaType("BOOLEAN") },
                    { "newest", SchemaType("BOOLEAN") },
                }
            },
            { "required", new[] { "intent", "title", "kind", "year", "season", "episode", "search_text", "apps", "action", "value", "play", "newest" } },
            { "propertyOrdering", new[] { "intent", "title", "kind", "year", "season", "episode", "search_text", "apps", "action", "value", "play", "newest" } },
        };

        static Dictionary<string, object> SchemaType(string type) { return new Dictionary<string, object> { { "type", type } }; }

        static Dictionary<string, object> SchemaEnum(params string[] values)
        {
            return new Dictionary<string, object> { { "type", "STRING" }, { "enum", values } };
        }

        static SearchIntent AskGemini(string words, Config cfg, string key, out string raw)
        {
            var names = new List<string>();
            foreach (Tile t in cfg.AppTiles) names.Add(t.Kind == TileKind.Browser ? t.Name + " (web search)" : t.Name);
            string request = "Apps on this TV: " + string.Join(", ", names) + "\n" +
                             "Subscriptions: " + (cfg.Subscriptions.Count == 0 ? "not known" : string.Join(", ", cfg.Subscriptions)) + "\n" +
                             "Request: \"" + words + "\"";
            var body = new Dictionary<string, object>
            {
                { "systemInstruction", new Dictionary<string, object> { { "parts", new object[] { new Dictionary<string, object> { { "text", Instructions } } } } } },
                { "contents", new object[] { new Dictionary<string, object> { { "role", "user" }, { "parts", new object[] { new Dictionary<string, object> { { "text", request } } } } } } },
                { "generationConfig", new Dictionary<string, object> { { "temperature", 0 }, { "responseMimeType", "application/json" }, { "responseSchema", Schema } } },
            };
            string url = GeminiBase + Uri.EscapeDataString(cfg.AiModel) + ":generateContent";
            string response = Send("POST", url, Json.Serialize(body), new Dictionary<string, string> { { "x-goog-api-key", key } }, 8000);
            raw = AnswerText(response);
            return ParseIntent(raw);
        }

        /// <summary>The model's text from a generateContent response (skipping any "thought" parts).</summary>
        internal static string AnswerText(string response)
        {
            var root = Json.Deserialize<Dictionary<string, object>>(response);
            object[] candidates = Arr(Get(root, "candidates"));
            if (candidates == null || candidates.Length == 0)
                throw new Exception("no answer" + (Get(root, "promptFeedback") != null ? " (blocked)" : ""));
            object[] parts = Arr(Get(Get(candidates[0], "content"), "parts"));
            if (parts != null)
            {
                foreach (object part in parts)
                {
                    if (Get(part, "thought") is bool && (bool)Get(part, "thought")) continue;
                    string text = Get(part, "text") as string;
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }
            throw new Exception("empty answer (" + (Get(candidates[0], "finishReason") ?? "?") + ")");
        }

        internal static SearchIntent ParseIntent(string json)
        {
            var d = Json.Deserialize<Dictionary<string, object>>(json);
            var intent = new SearchIntent
            {
                Intent = Str(d, "intent", "unknown").ToLowerInvariant(),
                Title = Str(d, "title", ""),
                Kind = Str(d, "kind", "unknown").ToLowerInvariant(),
                SearchText = Str(d, "search_text", ""),
                Action = Str(d, "action", "none").ToLowerInvariant(),
                Year = Int(d, "year"),
                Season = Int(d, "season"),
                Episode = Int(d, "episode"),
                Play = Bool(d, "play"),
                Newest = Bool(d, "newest"),
                Value = Int(d, "value"),
            };
            object[] apps = Arr(Get(d, "apps"));
            if (apps != null) foreach (object a in apps) if (a is string && ((string)a).Trim().Length > 0) intent.Apps.Add(((string)a).Trim());
            return intent;
        }

        // ================================================================ TMDB: where it streams in India

        /// <summary>The user's apps that have this title in India: streaming first, then free, then rent or buy.</summary>
        static List<KeyValuePair<Tile, string>> WhereToWatch(SearchIntent intent, List<Tile> apps, string key, SearchPlan plan)
        {
            string found = Send("GET", TmdbUrl("/search/multi?include_adult=false&query=" + Uri.EscapeDataString(intent.Title), key), null, TmdbHeaders(key), 6000);
            Dictionary<string, object> best = BestMatch(found, intent);
            if (best == null) return null;
            string type = (string)Get(best, "media_type");
            string id = Convert.ToString(Get(best, "id"));
            string providers = Send("GET", TmdbUrl("/" + type + "/" + id + "/watch/providers", key), null, TmdbHeaders(key), 6000);
            plan.UsedTmdb = true;

            string title = (Get(best, "title") ?? Get(best, "name") ?? intent.Title) as string;
            string date = (Get(best, "release_date") ?? Get(best, "first_air_date")) as string;
            plan.Summary = title + (type == "tv" ? " · TV show" : " · Film") + (date != null && date.Length >= 4 ? " · " + date.Substring(0, 4) : "");
            return ProvidersToApps(providers, apps);
        }

        static string TmdbUrl(string path, string key)
        {
            return TmdbBase + path + (key.Length == 32 ? (path.Contains("?") ? "&" : "?") + "api_key=" + key : "");
        }

        static Dictionary<string, string> TmdbHeaders(string key)
        {
            var headers = new Dictionary<string, string>();
            if (key.Length != 32) headers["Authorization"] = "Bearer " + key;   // a read access token
            return headers;
        }

        /// <summary>The search result that best fits the AI's title, kind and year.</summary>
        internal static Dictionary<string, object> BestMatch(string json, SearchIntent intent)
        {
            object[] results = Arr(Get(Json.Deserialize<Dictionary<string, object>>(json), "results"));
            if (results == null) return null;
            Dictionary<string, object> best = null;
            int bestScore = int.MinValue;
            for (int i = 0; i < results.Length && i < 10; i++)
            {
                var r = results[i] as Dictionary<string, object>;
                string type = r == null ? null : Get(r, "media_type") as string;
                if (type != "movie" && type != "tv") continue;
                int score = -i;                                            // TMDB's own order counts
                if (type == intent.Kind) score += 5;
                string date = (Get(r, "release_date") ?? Get(r, "first_air_date")) as string;
                int year;
                if (intent.Year > 0 && date != null && date.Length >= 4 && int.TryParse(date.Substring(0, 4), out year) && Math.Abs(year - intent.Year) <= 1) score += 4;
                string name = (Get(r, "title") ?? Get(r, "name")) as string;
                if (name != null && name.Equals(intent.Title, StringComparison.OrdinalIgnoreCase)) score += 3;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = r;
                }
            }
            return best;
        }

        /// <summary>Maps TMDB's India providers (e.g. "Amazon Prime Video") to the user's tiles.</summary>
        internal static List<KeyValuePair<Tile, string>> ProvidersToApps(string json, List<Tile> apps)
        {
            var list = new List<KeyValuePair<Tile, string>>();
            object india = Get(Get(Json.Deserialize<Dictionary<string, object>>(json), "results"), "IN");
            if (india == null) return list;
            string[][] groups = { new[] { "flatrate", "Streams here" }, new[] { "free", "Free" }, new[] { "ads", "Free with ads" } };
            foreach (string[] group in groups) AddProviders(list, india, group[0], group[1], apps);
            if (list.Count == 0)
            {
                AddProviders(list, india, "rent", "Rent or buy", apps);
                AddProviders(list, india, "buy", "Rent or buy", apps);
            }
            return list;
        }

        static void AddProviders(List<KeyValuePair<Tile, string>> list, object india, string group, string caption, List<Tile> apps)
        {
            object[] providers = Arr(Get(india, group));
            if (providers == null) return;
            foreach (object p in providers)
            {
                string name = (Get(p, "provider_name") as string ?? "").ToLowerInvariant();
                Tile t = name.Contains("netflix") ? FindHost(apps, "netflix.com")
                    : name.Contains("prime video") || name.Contains("amazon") ? FindHost(apps, "primevideo.com")
                    : name.Contains("hotstar") || name.Contains("jio") ? FindHost(apps, "hotstar.com")
                    : name.Contains("zee5") ? FindHost(apps, "zee5.com")
                    : name.Contains("sony") ? FindHost(apps, "sonyliv.com")
                    : name.Contains("youtube") ? FindHost(apps, "youtube.com")
                    : null;
                if (t != null && !list.Exists(x => x.Key == t)) list.Add(new KeyValuePair<Tile, string>(t, caption));
            }
        }

        // ================================================================ YouTube: the first video for some words

        internal sealed class YouTubeVideo
        {
            public string Id, Title, Thumbnail;
        }

        // In the search page's data, each ordinary result is a "videoRenderer" (ads, Shorts and channels are not).
        static readonly Regex VideoResult = new Regex(
            "\"videoRenderer\":\\{\"videoId\":\"([A-Za-z0-9_-]{11})\".{0,4000}?\"title\":\\{\"runs\":\\[\\{\"text\":\"((?:[^\"\\\\]|\\\\.)*)\"",
            RegexOptions.Singleline);

        /// <summary>Reads YouTube's own search page, as a browser would, and takes the first video.</summary>
        static YouTubeVideo FirstYouTubeVideo(string query, bool newest)
        {
            // sp: videos only (no channels or playlists); CAISAhAB also sorts by upload date.
            string url = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query) + "&sp=" + (newest ? "CAISAhAB" : "EgIQAQ%3D%3D");
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
            request.Headers["Accept-Language"] = "en-IN,en;q=0.9";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Timeout = 6000;
            request.ReadWriteTimeout = 6000;
            string page;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                page = reader.ReadToEnd();
            return ParseYouTube(page);
        }

        internal static YouTubeVideo ParseYouTube(string page)
        {
            Match m = VideoResult.Match(page);
            if (!m.Success) return null;
            string title;
            try { title = Json.Deserialize<string>("\"" + m.Groups[2].Value + "\""); }
            catch (Exception) { title = m.Groups[2].Value; }
            string id = m.Groups[1].Value;
            return new YouTubeVideo { Id = id, Title = title, Thumbnail = "https://i.ytimg.com/vi/" + id + "/mqdefault.jpg" };
        }

        // ================================================================ rules, for when the AI isn't available

        static readonly Regex OpenWords = new Regex(@"^(?:open|launch|start|go to|kholo|chalao)\s+(.+)$|^(.+?)\s+(?:kholo|khol do|chalao|chala do|open karo|open|start karo|lagao)$", RegexOptions.IgnoreCase);
        static readonly Regex MusicWords = new Regex(@"\b(songs?|gaan[ae]|music|bhajans?|lyrics|album|playlist|jukebox)\b", RegexOptions.IgnoreCase);
        static readonly Regex VideoWords = new Regex(@"\b(trailer|teaser|news|recipe|how to|tutorial|review|vlog|comedy|podcast|highlights)\b", RegexOptions.IgnoreCase);
        static readonly Regex PlayWords = new Regex(@"\b(play|chalao|chala do|lagao|laga do|sunao|bajao)\b", RegexOptions.IgnoreCase);
        static readonly Regex Filler = new Regex(@"\b(watch|dekhna|dekhni|dekhao|lagao|chalao|play|karo|kar do|please|on|pe|par|me|mein)\b", RegexOptions.IgnoreCase);

        /// <summary>"Netflix", "open Netflix", "Netflix kholo" → that tile.</summary>
        internal static Tile OpenCommand(string words, List<Tile> apps)
        {
            Tile exact = FindApp(words, apps, true);
            if (exact != null) return exact;
            Match m = OpenWords.Match(words.Trim());
            if (!m.Success) return null;
            string name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            return FindApp(name, apps, true);
        }

        internal static SearchIntent Rules(string words, List<Tile> apps, bool canLookUp)
        {
            var intent = new SearchIntent { SearchText = words };
            foreach (Tile t in apps)
            {
                if (t.Kind == TileKind.Browser) continue;   // "web series" isn't the Web tile
                // "kota factory on netflix", "netflix pe kota factory": that app, with the app's name taken out.
                Match m = Regex.Match(words, @"\b" + Regex.Escape(t.Label) + @"\b", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                string rest = Clean(words.Remove(m.Index, m.Length));
                if (rest.Length == 0) continue;
                intent.Intent = Launcher.SearchTemplate(t) != null && IsHost(t, "youtube.com") ? "video" : "watch";
                intent.SearchText = rest;
                intent.Apps.Add(t.Name);
                return intent;
            }
            if (MusicWords.IsMatch(words)) intent.Intent = "music";
            else if (VideoWords.IsMatch(words)) intent.Intent = "video";
            else if (canLookUp)
            {
                intent.Intent = "watch";             // let TMDB say where it streams, if it's a title
                intent.Title = intent.SearchText = Clean(words);
            }
            if (intent.Intent == "music" || intent.Intent == "video") intent.Apps.Add("YouTube");
            intent.Play = intent.Intent == "music" || PlayWords.IsMatch(words);
            return intent;
        }

        static string Clean(string words)
        {
            return Regex.Replace(Filler.Replace(words, " "), @"\s+", " ").Trim();
        }

        // ================================================================ helpers

        static Tile FindApp(string name, List<Tile> apps) { return FindApp(name, apps, false); }

        /// <summary>A tile by name or label, ignoring case, spaces and punctuation ("prime" finds Prime Video unless exact).</summary>
        static Tile FindApp(string name, List<Tile> apps, bool exact)
        {
            string wanted = Squash(name);
            if (wanted.Length == 0) return null;
            foreach (Tile t in apps)
                if (Squash(t.Name) == wanted || Squash(t.Label) == wanted) return t;
            if (exact || wanted.Length < 3) return null;
            foreach (Tile t in apps)
                if (Squash(t.Name).Contains(wanted) || wanted.Contains(Squash(t.Name))) return t;
            return null;
        }

        static string Squash(string s)
        {
            var b = new StringBuilder();
            foreach (char c in (s ?? "").ToLowerInvariant()) if (char.IsLetterOrDigit(c)) b.Append(c);
            return b.ToString();
        }

        static Tile FindHost(List<Tile> apps, string host)
        {
            foreach (Tile t in apps) if (IsHost(t, host)) return t;
            return null;
        }

        static bool IsHost(Tile t, string host)
        {
            Uri uri;
            return t.Kind == TileKind.Web && Uri.TryCreate(t.Url, UriKind.Absolute, out uri) && uri.Host.ToLowerInvariant().EndsWith(host)
                && !(host == "youtube.com" && uri.AbsolutePath.StartsWith("/tv"));
        }

        static Tile FindKind(List<Tile> apps, TileKind kind)
        {
            foreach (Tile t in apps) if (t.Kind == kind) return t;
            return null;
        }

        static object Get(object node, string key)
        {
            var d = node as Dictionary<string, object>;
            object value;
            return d != null && d.TryGetValue(key, out value) ? value : null;
        }

        /// <summary>A JSON array, which JavaScriptSerializer returns as object[] or ArrayList depending on how it's called.</summary>
        static object[] Arr(object node)
        {
            var array = node as object[];
            if (array != null) return array;
            var list = node as System.Collections.ArrayList;
            return list == null ? null : list.ToArray();
        }

        static string Str(Dictionary<string, object> d, string key, string fallback)
        {
            string value = Get(d, key) as string;
            return string.IsNullOrEmpty(value) ? fallback : value.Trim();
        }

        static bool Bool(Dictionary<string, object> d, string key)
        {
            object value = Get(d, key);
            return value is bool && (bool)value;
        }

        static int Int(Dictionary<string, object> d, string key)
        {
            object value = Get(d, key);
            try { return value == null ? 0 : Convert.ToInt32(value); }
            catch (Exception) { return 0; }
        }

        /// <summary>One HTTP request. Errors come back with the service's own message (e.g. "API key not valid").</summary>
        static string Send(string method, string url, string body, Dictionary<string, string> headers, int timeoutMs)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.UserAgent = "CouchTV";
            request.Accept = "application/json";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            foreach (KeyValuePair<string, string> h in headers) request.Headers[h.Key] = h.Value;
            try
            {
                if (body != null)
                {
                    byte[] data = Encoding.UTF8.GetBytes(body);
                    request.ContentType = "application/json; charset=utf-8";
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
                if (response == null) throw new Exception(ex.Status == WebExceptionStatus.Timeout ? "timed out" : ex.Message);
                string text;
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) text = reader.ReadToEnd();
                throw new Exception((int)response.StatusCode + " " + ErrorMessage(text));
            }
        }

        static string ErrorMessage(string json)
        {
            try
            {
                var d = Json.Deserialize<Dictionary<string, object>>(json);
                string message = (Get(Get(d, "error"), "message") ?? Get(d, "status_message")) as string;
                if (!string.IsNullOrEmpty(message)) return message.Length > 140 ? message.Substring(0, 140) + "…" : message;
            }
            catch (Exception) { }
            return json.Length > 140 ? json.Substring(0, 140) + "…" : json;
        }
    }
}
