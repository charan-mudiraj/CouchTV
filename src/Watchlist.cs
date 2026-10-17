using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CouchTV
{
    /// <summary>
    /// The watchlist the phones share: films picked on a phone to watch at a time, each phone reminding its own person
    /// (an hour before, and at the time). The phones keep it themselves, and pass changes to each other through an
    /// encrypted mailbox on the internet while this PC sleeps; this is the copy kept for good, swapped with each phone
    /// as it connects (WATCH lines, RemoteServer), and a change from one phone goes straight on to the others connected.
    /// One line a film, the same everywhere; the newer change wins:
    ///   &lt;id&gt; &lt;watch at&gt; &lt;changed&gt; &lt;by&gt; on|off &lt;the film, as a SUGGEST line says it&gt;
    /// Times in ms since 1970 (UTC); by: the phone that changed it last (its id, or "-"); off: taken off the list,
    /// remembered a while so an old copy can't bring it back. In %LOCALAPPDATA%\CouchTV\watchlist.txt. Thread-safe.
    /// </summary>
    internal sealed class Watchlist
    {
        public const int MostFilms = 100;
        public const int LongestLine = 4000;
        public const long Day = 24L * 3600 * 1000;
        static readonly Regex Shape = new Regex(@"^([A-Za-z0-9_-]{1,60}) (\d{1,15}) (\d{1,15}) ([0-9a-f]{32}|-) (on|off) (\S.*)$");
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        sealed class Film
        {
            public string Id, Line;
            public long WatchAt, Changed;
            public bool On;
        }

        readonly string _path;   // null: kept in memory only (the self-test)
        readonly object _lock = new object();
        readonly Dictionary<string, Film> _films = new Dictionary<string, Film>();

        Watchlist(string path) { _path = path; }

        public static string DefaultPath { get { return Path.Combine(Config.DataDir, "watchlist.txt"); } }

        /// <summary>Now, as the lines count time: ms since 1970, UTC.</summary>
        public static long Now { get { return (DateTime.UtcNow - Epoch).Ticks / TimeSpan.TicksPerMillisecond; } }

        public static Watchlist InMemory() { return new Watchlist(null); }

        public static Watchlist Load()
        {
            var list = new Watchlist(DefaultPath);
            try
            {
                if (File.Exists(list._path))
                {
                    long now = Now;
                    foreach (string line in File.ReadAllLines(list._path, Encoding.UTF8))
                    {
                        Film f = Parse(line);
                        if (f != null && Keep(f, now)) list._films[f.Id] = f;
                    }
                }
            }
            catch (Exception ex) { Log.Info("Watchlist: couldn't read " + list._path + " (" + ex.Message + ")"); }
            return list;
        }

        public int Count { get { lock (_lock) return _films.Count; } }

        /// <summary>
        /// A film from a phone (a WATCH line without "WATCH "): kept if it's new here, or a newer change than the one
        /// kept. True when it was, so the other phones are told.
        /// </summary>
        public bool Merge(string line, long now)
        {
            Film f = Parse(line);
            if (f == null || !Keep(f, now)) return false;
            lock (_lock)
            {
                Film old;
                if (_films.TryGetValue(f.Id, out old) && !Newer(f, old)) return false;
                _films[f.Id] = f;
                Tidy(now);
                Save();
                return true;
            }
        }

        /// <summary>Every film kept, as WATCH lines, for a phone that's just connected.</summary>
        public List<string> Lines(long now)
        {
            var lines = new List<string>();
            lock (_lock)
            {
                Tidy(now);
                foreach (Film f in _films.Values) lines.Add("WATCH " + f.Line);
            }
            return lines;
        }

        /// <summary>
        /// Kept until a day after its time; one taken off, also until two weeks after that change, so a phone that
        /// missed it (or the mailbox) can't bring it back with an older copy.
        /// </summary>
        static bool Keep(Film f, long now)
        {
            return f.On ? f.WatchAt + Day > now : Math.Max(f.WatchAt + Day, f.Changed + 14 * Day) > now;
        }

        /// <summary>The later change wins; for two at the same moment, the same one everywhere (the line that sorts last).</summary>
        static bool Newer(Film a, Film b)
        {
            return a.Changed > b.Changed || (a.Changed == b.Changed && string.CompareOrdinal(a.Line, b.Line) > 0);
        }

        static Film Parse(string line)
        {
            if (line == null) return null;
            line = line.Trim();
            if (line.Length > LongestLine) return null;
            Match m = Shape.Match(line);
            if (!m.Success) return null;
            long watchAt, changed;
            if (!long.TryParse(m.Groups[2].Value, out watchAt) || !long.TryParse(m.Groups[3].Value, out changed)) return null;
            // The film: "M <id> <title> ..." as a SUGGEST line, at least as far as its picture's colours.
            string[] film = m.Groups[6].Value.Split(' ');
            if (film.Length < 8 || film[0] != "M") return null;
            return new Film { Id = m.Groups[1].Value, WatchAt = watchAt, Changed = changed, On = m.Groups[5].Value == "on", Line = line };
        }

        /// <summary>For the log: "Kantara, 18 Oct 21:00" (or "taken off").</summary>
        public static string Describe(string line)
        {
            Film f = Parse(line);
            if (f == null) return "?";
            string title = f.Id;
            try { title = Encoding.UTF8.GetString(Convert.FromBase64String(f.Line.Split(' ')[7])); }
            catch (Exception) { }
            return title + (f.On ? ", " + Epoch.AddMilliseconds(f.WatchAt).ToLocalTime().ToString("d MMM HH:mm") : ", taken off");
        }

        /// <summary>Lets go of films past keeping, then the earliest ones beyond the most kept.</summary>
        void Tidy(long now)
        {
            var gone = new List<string>();
            foreach (Film f in _films.Values) if (!Keep(f, now)) gone.Add(f.Id);
            foreach (string id in gone) _films.Remove(id);
            while (_films.Count > MostFilms)
            {
                Film earliest = null;
                foreach (Film f in _films.Values) if (earliest == null || f.WatchAt < earliest.WatchAt) earliest = f;
                _films.Remove(earliest.Id);
            }
        }

        void Save()
        {
            if (_path == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var text = new StringBuilder();
                foreach (Film f in _films.Values) text.Append(f.Line).Append('\n');
                string temp = _path + ".new";
                File.WriteAllText(temp, text.ToString(), new UTF8Encoding(false));
                if (File.Exists(_path)) File.Replace(temp, _path, null);
                else File.Move(temp, _path);
            }
            catch (Exception ex) { Log.Error("Saving the watchlist to " + _path, ex); }
        }
    }
}
