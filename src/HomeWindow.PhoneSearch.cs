using System;
using System.Collections.Generic;
using System.Threading;

namespace CouchTV
{
    // The phone's search page: as it's typed in, films and shows (JustWatch, on your apps) and songs (Apple's music search,
    // played on YouTube) go back to it, to play on the TV (PLAY, and PLAYNEXT for songs); and a song hummed into it
    // (Gemini works out which, with the key AI search uses), found the same way.
    internal sealed partial class HomeWindow
    {
        const int FoundTitles = 8, FoundSongs = 8;

        readonly Dictionary<string, KeyValuePair<DateTime, List<Suggestion>>> _foundCache = new Dictionary<string, KeyValuePair<DateTime, List<Suggestion>>>();

        void StartPhoneSearch()
        {
            if (_wifi == null) return;
            _wifi.Find += FindForPhone;
            _wifi.Hum += HumForPhone;
        }

        void FindForPhone(RemoteSession phone, int seq, string words)
        {
            string key = words.ToLowerInvariant();
            KeyValuePair<DateTime, List<Suggestion>> cached;
            if (_foundCache.TryGetValue(key, out cached) && DateTime.UtcNow - cached.Key < TimeSpan.FromMinutes(10))
            {
                _wifi.SendTo(phone, Suggestions.FoundLines(seq, cached.Value, FindTile, null));
                return;
            }
            Config cfg = _cfg;
            ThreadPool.QueueUserWorkItem(state =>
            {
                // Both at once: each takes a moment.
                List<Suggestion> titles = new List<Suggestion>(), songs = new List<Suggestion>();
                var done = new ManualResetEvent(false);
                ThreadPool.QueueUserWorkItem(s =>
                {
                    try { songs = Suggestions.SearchSongs(cfg, words, FoundSongs); }
                    catch (Exception ex) { Log.Info("Search on the phone: songs for \"" + words + "\": " + ex.Message); }
                    done.Set();
                });
                try { titles = Suggestions.SearchTitles(cfg, words, FoundTitles); }
                catch (Exception ex) { Log.Info("Search on the phone: films for \"" + words + "\": " + ex.Message); }
                done.WaitOne(10000);
                var all = new List<Suggestion>(titles);
                all.AddRange(songs);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_foundCache.Count > 200) _foundCache.Clear();
                    _foundCache[key] = new KeyValuePair<DateTime, List<Suggestion>>(DateTime.UtcNow, all);
                    _wifi.SendTo(phone, Suggestions.FoundLines(seq, all, FindTile, null));
                }));
            });
        }

        /// <summary>A song hummed into the phone: Gemini's guesses, each found as a song, back to the phone ("FOUND").</summary>
        void HumForPhone(RemoteSession phone, int seq, byte[] audio)
        {
            if (_keys == null) _keys = ApiKeys.Load();
            ApiKeys keys = _keys;
            if (string.IsNullOrEmpty(keys.Gemini))
            {
                _wifi.SendTo(phone, Suggestions.FoundLines(seq, new List<Suggestion>(), FindTile, "nokey"));
                return;
            }
            Config cfg = _cfg;
            Log.Info("Search on the phone: a song hummed (" + audio.Length / 1024 + " KB)");
            ThreadPool.QueueUserWorkItem(state =>
            {
                var found = new List<Suggestion>();
                string why = null;
                try
                {
                    List<KeyValuePair<string, string>> guesses = SmartSearch.IdentifySong(audio, "audio/aac", cfg, keys.Gemini);
                    Log.Info("Search on the phone: hummed song: " + (guesses.Count == 0 ? "none" : string.Join("; ", guesses.ConvertAll(g => g.Key + " / " + g.Value))));
                    foreach (KeyValuePair<string, string> guess in guesses)
                    {
                        // Its picture and proper name from Apple's music search; as it is, without.
                        Suggestion song = null;
                        try
                        {
                            List<Suggestion> matches = Suggestions.SearchSongs(cfg, guess.Key + " " + guess.Value, 1);
                            if (matches.Count > 0) song = matches[0];
                        }
                        catch (Exception) { }
                        Tile youtube = SmartSearch.FindHost(cfg.AppTiles, "youtube.com");
                        if (song == null && youtube != null)
                            song = new Suggestion
                            {
                                Kind = SuggestionKind.Song, Id = "h" + found.Count, Title = guess.Key, Subtitle = guess.Value, App = youtube.Name,
                                Query = guess.Key + " " + Suggestions.LeadArtist(guess.Value),
                            };
                        if (song != null && !found.Exists(f => f.Title == song.Title && f.Subtitle == song.Subtitle)) found.Add(song);
                    }
                    if (found.Count == 0) why = "none";
                }
                catch (Exception ex)
                {
                    Log.Info("Search on the phone: the hummed song: " + ex.Message);
                    why = "error";
                }
                Dispatcher.BeginInvoke(new Action(() => _wifi.SendTo(phone, Suggestions.FoundLines(seq, found, FindTile, why))));
            });
        }
    }
}
