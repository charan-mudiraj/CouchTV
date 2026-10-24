using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace CouchTV
{
    /// <summary>
    /// The Live TV rows: channels on YuppTV (the live TV of ACT's bundle), a row for each language chosen (LiveTv in
    /// couchtv.ini: TEL, HIN, ENG…), each with what's on now, from YuppTV's own TV guide (prod-api.yupptv.com, asked as
    /// its website asks it). Without your login the guide lists YuppTV's free channels; once you've logged in on the TV
    /// (the YuppTV tile), CouchTV keeps that session, seen in the site's own requests (PageScripts), and lists yours.
    /// Choosing a channel opens its live page. Fetched every 10 minutes, as programmes change; logos are kept on disk.
    /// </summary>
    internal static class LiveTv
    {
        public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(10);
        public const int MostPerLanguage = 60;
        const string Api = "https://prod-api.yupptv.com/service/api/v1/";
        const string Site = "https://www.yupptv.com/";
        const string Agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static readonly byte[] Entropy = Encoding.ASCII.GetBytes("CouchTV YuppTV");
        static readonly object Lock = new object();
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        static string _pictures;   // where YuppTV's "common" pictures are (its settings say)

        /// <summary>YuppTV's languages: its codes, and their names.</summary>
        public static readonly string[][] Languages =
        {
            new[] { "TEL", "Telugu" }, new[] { "HIN", "Hindi" }, new[] { "ENG", "English" }, new[] { "TAM", "Tamil" },
            new[] { "KAN", "Kannada" }, new[] { "MAL", "Malayalam" }, new[] { "BEN", "Bengali" }, new[] { "MAR", "Marathi" },
        };

        static LiveTv()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public static string LanguageName(string code)
        {
            foreach (string[] l in Languages) if (l[0] == code) return l[1];
            return code;
        }

        public static string Folder { get { return Path.Combine(Config.DataDir, "livetv"); } }

        static string SessionFile { get { return Path.Combine(Config.DataDir, "yupptv.dat"); } }

        // ================================================================ your session, from YuppTV's own page

        /// <summary>
        /// The session YuppTV's own page uses (its session-id and box-id, seen in its requests to its guide and the
        /// like): kept, encrypted by Windows for this user, so the guide lists your channels. From any thread.
        /// </summary>
        public static void TakeSession(string session, string box)
        {
            if (string.IsNullOrEmpty(session) || string.IsNullOrEmpty(box) || session.Length > 300 || box.Length > 100) return;
            lock (Lock)
            {
                string[] old = ReadSession();
                if (old != null && old[0] == session && old[1] == box) return;
                try
                {
                    Directory.CreateDirectory(Config.DataDir);
                    File.WriteAllBytes(SessionFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(session + "\n" + box), Entropy, DataProtectionScope.CurrentUser));
                    Log.Info("Live TV: keeping YuppTV's session from its page, for your channels");
                }
                catch (Exception ex) { Log.Info("Live TV: couldn't keep YuppTV's session: " + ex.Message); }
            }
        }

        /// <summary>Whether a session from YuppTV's page is kept (so the guide is yours, not a guest's).</summary>
        public static bool HaveSession { get { lock (Lock) return ReadSession() != null; } }

        static string[] ReadSession()
        {
            try
            {
                if (!File.Exists(SessionFile)) return null;
                string[] p = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(SessionFile), Entropy, DataProtectionScope.CurrentUser)).Split('\n');
                return p.Length == 2 && p[0].Length > 0 && p[1].Length > 0 ? p : null;
            }
            catch (Exception) { return null; }
        }

        static void ForgetSession()
        {
            lock (Lock)
            {
                try { File.Delete(SessionFile); } catch (Exception) { }
            }
        }

        // ================================================================ the guide

        /// <summary>
        /// The channels for these languages (YuppTV's codes), a row each ("live:TEL"), as suggestions of the Channel
        /// kind for YuppTV's tile ([app]), with their logos downloaded. Throws if the guide couldn't be read at all.
        /// </summary>
        public static List<Suggestion> Fetch(List<string> languages, Tile app, bool clock24, StringBuilder notes)
        {
            var list = new List<Suggestion>();
            if (languages.Count == 0 || app == null) return list;
            string[] yours;
            lock (Lock) yours = ReadSession();
            string session, box;
            if (yours != null)
            {
                session = yours[0];
                box = yours[1];
            }
            else Guest(out session, out box);
            string pictures = Pictures(session, box);
            var seen = new HashSet<string>();
            string error = null;
            foreach (string language in languages)
            {
                string json = null;
                try { json = Ask("tvguide/channels?filter=genreCode:all;langCode:" + language, session, box); }
                catch (UnauthorizedAccessException)
                {
                    if (yours == null) throw new Exception("YuppTV refused its own guest session");
                    // Your session has ended (logged out, or it expired): a guest's until the page gives a new one.
                    Note(notes, "YuppTV's session from its page has ended; showing its free channels until you open YuppTV again");
                    ForgetSession();
                    yours = null;
                    Guest(out session, out box);
                    json = Ask("tvguide/channels?filter=genreCode:all;langCode:" + language, session, box);
                }
                catch (Exception ex) { error = ex.Message; Note(notes, LanguageName(language) + ": " + ex.Message); }
                if (json != null) list.AddRange(ParseChannels(json, language, app, pictures, clock24, seen));
            }
            if (list.Count == 0 && error != null) throw new Exception(error);
            Directory.CreateDirectory(Folder);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Suggestion s in list)
            {
                Download(s, notes);
                if (s.ImageFile != null) used.Add(Path.GetFileName(s.ImageFile));
            }
            foreach (string file in Directory.GetFiles(Folder))
                if (!used.Contains(Path.GetFileName(file)) && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7))
                    try { File.Delete(file); } catch (Exception) { }   // logos not used for a week
            return list;
        }

        /// <summary>
        /// YuppTV's guide for one language (its "data": a channel each, with the programme on now), as Channel
        /// suggestions in its order, each channel once ([seen]); at most MostPerLanguage.
        /// </summary>
        internal static List<Suggestion> ParseChannels(string json, string language, Tile app, string pictures, bool clock24, HashSet<string> seen)
        {
            var list = new List<Suggestion>();
            var root = Json.Deserialize<Dictionary<string, object>>(json);
            object[] data = Arr(Get(Get(root, "response"), "data"));
            if (data == null) return list;
            foreach (object c in data)
            {
                object display = Get(c, "display"), target = Get(c, "target"), page = Get(target, "pageAttributes");
                string id = Convert.ToString(Get(c, "id"), System.Globalization.CultureInfo.InvariantCulture), name = Get(display, "title") as string,
                       path = Get(target, "path") as string, logo = Get(display, "imageUrl") as string;
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path) || path.Contains("..") || path.StartsWith("/")) continue;
                if (!seen.Add(id)) continue;
                string program = (Get(page, "liveProgramName") as string ?? "").Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(program, @"^Program\s*@\s*\d{1,2}:\d{2}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) program = "";   // a channel's placeholder
                long start = Long(Get(page, "startTime")), end = Long(Get(page, "endTime"));
                string when = start > 0 && end > start ? Span(start, end, clock24) : "";
                list.Add(new Suggestion
                {
                    Kind = SuggestionKind.Channel, Id = "c" + id, Title = name.Trim(),
                    Subtitle = program.Length > 0 ? program + (when.Length > 0 ? " · " + when : "") : when,
                    App = app.Name, ImageUrl = Picture(logo, pictures), PlayUrl = Site + path.Trim(), Row = "live:" + language,
                    Start = start, End = end,
                });
                if (list.Count == MostPerLanguage) break;
            }
            return list;
        }

        /// <summary>"8:00–8:30 PM", "11:30 AM–12:00 PM" or "20:00–20:30", local time, from ms since 1970.</summary>
        internal static string Span(long start, long end, bool clock24)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            DateTime a = Epoch.AddMilliseconds(start).ToLocalTime(), b = Epoch.AddMilliseconds(end).ToLocalTime();
            if (clock24) return a.ToString("HH:mm", culture) + "–" + b.ToString("HH:mm", culture);
            bool sameHalf = (a.Hour < 12) == (b.Hour < 12);
            return a.ToString(sameHalf ? "h:mm" : "h:mm tt", culture) + "–" + b.ToString("h:mm tt", culture);
        }

        /// <summary>A picture's address: YuppTV writes "common,channel/logos/x.png" (its "common" place, then the file).</summary>
        static string Picture(string value, string pictures)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (value.StartsWith("https://")) return value;
            int comma = value.IndexOf(',');
            return comma > 0 && pictures != null && value.Substring(0, comma) == "common" ? pictures + value.Substring(comma + 1) : null;
        }

        /// <summary>A guest's session, as YuppTV's website gets one for a visitor: a new box id each time.</summary>
        static void Guest(out string session, out string box)
        {
            box = Guid.NewGuid().ToString();
            var root = Json.Deserialize<Dictionary<string, object>>(Http(Api + "get/token?tenant_code=yupptv&box_id=" + box +
                "&product=yupptv&device_id=5&display_lang_code=ENG&device_sub_type=Chrome&timezone=Asia%2FKolkata", null, null));
            session = Get(Get(root, "response"), "sessionId") as string;
            if (string.IsNullOrEmpty(session)) throw new Exception("YuppTV gave no guest session");
        }

        /// <summary>Where YuppTV's "common" pictures are (from its settings, remembered while CouchTV runs).</summary>
        static string Pictures(string session, string box)
        {
            if (_pictures != null) return _pictures;
            try
            {
                var root = Json.Deserialize<Dictionary<string, object>>(Ask("system/config", session, box));
                object[] profiles = Arr(Get(Get(root, "response"), "resourceProfiles"));
                if (profiles != null)
                    foreach (object p in profiles)
                        if (Get(p, "code") as string == "common") return _pictures = Get(p, "urlPrefix") as string;
            }
            catch (Exception ex) { Log.Info("Live TV: YuppTV's settings: " + ex.Message); }
            return null;
        }

        static string Ask(string what, string session, string box)
        {
            string json = Http(Api + what, session, box);
            var root = Json.Deserialize<Dictionary<string, object>>(json);
            object status = Get(root, "status");
            if (status is bool && !(bool)status)
            {
                object code = Get(Get(root, "error"), "code");
                if (code != null && Convert.ToString(code) == "401") throw new UnauthorizedAccessException();
                throw new Exception("YuppTV said: " + (Get(Get(root, "error"), "message") ?? "no"));
            }
            return json;
        }

        static string Http(string url, string session, string box)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = Agent;
            request.Timeout = request.ReadWriteTimeout = 15000;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Accept = "*/*";   // the guide calls its JSON plain text, and refuses a request for JSON only (406)
            request.Headers["Origin"] = "https://www.yupptv.com";
            request.Referer = Site;
            if (session != null)
            {
                request.Headers["session-id"] = session;
                request.Headers["box-id"] = box;
                request.Headers["tenant-code"] = "yupptv";
            }
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null && (int)response.StatusCode == 401) throw new UnauthorizedAccessException();
                throw new Exception(response != null ? (int)response.StatusCode + " " + response.StatusDescription : ex.Status == WebExceptionStatus.Timeout ? "timed out" : ex.Message);
            }
        }

        static void Download(Suggestion s, StringBuilder notes)
        {
            if (string.IsNullOrEmpty(s.ImageUrl)) return;
            string path;
            using (var sha = SHA1.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(s.ImageUrl));
                var name = new StringBuilder();
                for (int i = 0; i < 10; i++) name.Append(digest[i].ToString("x2"));
                path = Path.Combine(Folder, name + Path.GetExtension(new Uri(s.ImageUrl).AbsolutePath).ToLowerInvariant());
            }
            if (!File.Exists(path))
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(s.ImageUrl);
                    request.UserAgent = Agent;
                    request.Timeout = request.ReadWriteTimeout = 15000;
                    using (var response = request.GetResponse())
                    using (Stream input = response.GetResponseStream())
                    using (var output = File.Create(path + ".part")) input.CopyTo(output);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(path + ".part", path);
                }
                catch (Exception ex)
                {
                    Note(notes, "The logo of " + s.Title + ": " + ex.Message);
                    return;
                }
            }
            else File.SetLastWriteTimeUtc(path, DateTime.UtcNow);   // still used
            s.ImageFile = path;   // a full path: the home screen loads it from here, not the suggestions' folder
        }

        static void Note(StringBuilder notes, string text)
        {
            if (notes != null) notes.Append(text).Append(". ");
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

        static long Long(object value)
        {
            long n;
            return value != null && long.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out n) ? n : 0;
        }
    }
}
