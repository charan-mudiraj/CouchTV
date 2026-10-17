using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Threading;

namespace CouchTV
{
    /// <summary>A song a phone said to play next: CouchTV puts it on in the YouTube page when the one playing ends.</summary>
    internal sealed class QueueSong
    {
        public string Id, Title, Artist;
    }

    // Up next, for YouTube: songs phones said to play next (Play next on a song), then YouTube's own list beside the video
    // (a mix or a playlist, read from the page: YouTubePage). When a song ends, or Next is pressed, the next of yours goes
    // on in the same page (no new window, nothing in front changes); after yours, YouTube carries on with its own. Phones
    // show it under their player (QUEUE lines), and play or remove a song from it.
    internal sealed partial class HomeWindow
    {
        const int MostQueue = 25;

        readonly List<QueueSong> _upNext = new List<QueueSong>();
        List<PageSong> _pageList = new List<PageSong>();
        string _pageListFor;          // the song YouTube's list was read with
        bool _switching;              // a song of yours being put on, until it's what plays (or a while has gone by)
        string _switchFrom;           // ...the song playing when it started
        DispatcherTimer _switchDone, _queueGone;
        long _typingQuietUntil;       // UTC ticks: CouchTV is typing in YouTube's search box, not you (TypingQuiet)
        string _queueTold;
        int _pageReads;               // reads asked for, so an old one doesn't overwrite a newer

        void StartQueue()
        {
            if (_wifi == null) return;
            _wifi.CurrentQueue = () => new List<string>(_queueLines.Split('\n'));
            _wifi.PlayNext += PlayNextById;
            _wifi.Queue += (what, id) =>
            {
                if (what == "remove")
                {
                    if (_upNext.RemoveAll(s => s.Id == id) > 0) TellQueue();
                }
                else if (what == "play") PlayFromQueue(id);
            };
        }

        volatile string _queueLines = "QUEUE 0";   // what phones were last told (read on the network thread)

        /// <summary>The YouTube window what plays is in, or zero when it isn't YouTube.</summary>
        IntPtr YouTubeWindow()
        {
            MediaNow now = NowPlaying.Current;
            if (now == null) return IntPtr.Zero;
            IntPtr window = BackgroundAlive ? _bgWindow : IntPtr.Zero;
            if (window == IntPtr.Zero)
            {
                string title;
                window = MediaWindow(now, out title);
            }
            if (window == IntPtr.Zero) return IntPtr.Zero;
            string t = Shell.WindowTitle(window);
            return t.IndexOf("YouTube", StringComparison.OrdinalIgnoreCase) >= 0 && t.IndexOf("YouTube Music", StringComparison.OrdinalIgnoreCase) < 0
                ? window : IntPtr.Zero;
        }

        /// <summary>What plays changed: another song means YouTube's list moves on (read again, once the page has).</summary>
        void QueueMediaChanged()
        {
            MediaNow now = NowPlaying.Current;
            if (now == null)
            {
                // Nothing plays: between two songs (YouTube changing over has a moment of nothing), or the end. Up
                // next goes only once nothing has played for a while.
                if (_queueGone == null)
                {
                    _queueGone = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                    _queueGone.Tick += (s, e) =>
                    {
                        _queueGone.Stop();
                        if (NowPlaying.Current != null || _switching) return;
                        _pageList = new List<PageSong>();
                        _pageListFor = null;
                        _upNext.Clear();
                        TellQueue();
                    };
                }
                _queueGone.Stop();
                _queueGone.Start();
                return;
            }
            if (_queueGone != null) _queueGone.Stop();
            if (_switching && _switchFrom != null && now.Title != _switchFrom) SwitchDone();   // yours is on
            if (now.Title == _pageListFor) return;
            _pageListFor = now.Title;
            // A song of yours due, and YouTube moved on by itself at the end (sooner than it said): yours now.
            if (_upNext.Count > 0 && !_switching && _songEnding != null && _songEnding != now.Title) SwitchTo(_upNext[0], false);
            _songEnding = null;
            ReadPageListSoon(1500);
            ReadPageListSoon(5000);
            TellQueue();
        }

        string _songEnding;   // the song whose end was seen coming, with a song of yours to follow

        /// <summary>The song of yours is on (or it's been a while): the next of yours waits for this one's end.</summary>
        void SwitchDone()
        {
            _switching = false;
            _switchFrom = null;
            _songEnding = null;
            if (_switchDone != null) _switchDone.Stop();
            Interlocked.Exchange(ref _typingQuietUntil, DateTime.UtcNow.AddSeconds(3).Ticks);
        }

        /// <summary>CouchTV is using YouTube's search box (putting a song on): not something to type into from a phone.</summary>
        bool TypingQuiet { get { return DateTime.UtcNow.Ticks < Interlocked.Read(ref _typingQuietUntil); } }

        /// <summary>Every second: a song of yours goes on as the one playing ends.</summary>
        void TickQueue()
        {
            MediaNow now = NowPlaying.Current;
            if (_upNext.Count == 0 || _switching || now == null || !now.Playing || now.DurationMs <= 0) return;
            long left = now.DurationMs - now.PositionAt(DateTime.UtcNow);
            if (left > 10000) return;
            // An ad before (or in) the song has a timeline of its own under the song's name: not its end, then. The
            // song's own length is in YouTube's list.
            PageSong listed = _pageList.Find(s => s.Title == now.Title);
            if (listed != null && listed.Seconds > 0 && Math.Abs(listed.Seconds * 1000L - now.DurationMs) > 4000) return;
            _songEnding = now.Title;   // should it move on before then, yours goes on at once (QueueMediaChanged)
            if (left > 3000) return;
            SwitchTo(_upNext[0], true);
        }

        /// <summary>Next (the phone's, the card's, the mini player's): a song of yours first, else YouTube's next.</summary>
        void MediaNext()
        {
            if (_upNext.Count > 0 && !_switching && YouTubeWindow() != IntPtr.Zero) SwitchTo(_upNext[0], false);
            else NowPlaying.Command("next");
        }

        /// <summary>
        /// Puts a song of yours on in the YouTube page: its search, then (atEnd: once the one playing has ended) its
        /// result. If the page won't, and YouTube or the home screen is in front, it opens on its own instead.
        /// </summary>
        void SwitchTo(QueueSong song, bool atEnd)
        {
            IntPtr window = YouTubeWindow();
            _upNext.Remove(song);
            TellQueue();
            if (window == IntPtr.Zero)
            {
                PlayOnItsOwn(song);
                return;
            }
            _switching = true;
            _songEnding = null;
            MediaNow playing = NowPlaying.Current;
            string from = playing != null ? playing.Title : null;
            _switchFrom = from;
            Interlocked.Exchange(ref _typingQuietUntil, DateTime.UtcNow.AddSeconds(30).Ticks);
            // Should YouTube never say the new song (or say the same title), it's done after a while all the same.
            if (_switchDone == null)
            {
                _switchDone = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
                _switchDone.Tick += (s, e) => SwitchDone();
            }
            _switchDone.Stop();
            _switchDone.Start();
            Log.Info("Up next: " + song.Title + " (" + song.Id + ")" + (atEnd ? ", as the song playing ends" : ""));
            // It works YouTube's page (its search box, full screen): with YouTube in front, a key pressed meanwhile would
            // land in the middle of it. Behind another app it's out of sight and out of the way.
            bool seen = Native.GetForegroundWindow() == window;
            if (seen) Busy.Show("song", "Putting on " + song.Title, atEnd ? 25 : 15);
            ThreadPool.QueueUserWorkItem(state =>
            {
                bool ok = false;
                try
                {
                    bool wasFullScreen;
                    AutomationElement result = YouTubePage.Find(window, song.Id, out wasFullScreen);
                    if (result != null && atEnd)
                    {
                        // Its last seconds play on meanwhile (in YouTube's small player): on when it ends, or moves on.
                        DateTime until = DateTime.UtcNow.AddSeconds(8);
                        while (DateTime.UtcNow < until)
                        {
                            MediaNow now = NowPlaying.Current;
                            if (now == null || now.Title != from || now.DurationMs - now.PositionAt(DateTime.UtcNow) < 500) break;
                            Thread.Sleep(100);
                        }
                    }
                    ok = result != null && YouTubePage.Click(result);
                    if (ok && wasFullScreen)
                    {
                        Thread.Sleep(2500);   // the new video's page, on
                        YouTubePage.FullScreen(window);
                    }
                }
                catch (Exception ex) { Log.Info("Up next: " + ex.Message); }
                if (seen) Busy.Hide("song");
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ok)
                    {
                        // On its way: done once YouTube says it plays (QueueMediaChanged), so the old one's last
                        // moment, still said meanwhile, doesn't put the next of yours on too.
                        MediaNow now = NowPlaying.Current;
                        if (now != null && from != null && now.Title != from) SwitchDone();
                        return;
                    }
                    SwitchDone();
                    Log.Info("Up next: the YouTube page didn't take " + song.Id);
                    PlayOnItsOwn(song);
                }));
            });
        }

        /// <summary>The song opened in YouTube by itself, when that won't disturb anything (YouTube or the home screen in front).</summary>
        void PlayOnItsOwn(QueueSong song)
        {
            IntPtr front = Native.GetForegroundWindow();
            Tile youtube = _cfg.AppTiles.Find(t => Launcher.SearchTemplate(t) != null && (t.Url ?? "").IndexOf("youtube.com", StringComparison.OrdinalIgnoreCase) >= 0);
            bool quiet = front == _hwnd || Shell.WindowTitle(front).IndexOf("YouTube", StringComparison.OrdinalIgnoreCase) >= 0;
            if (youtube == null || !quiet)
            {
                Log.Info("Up next: left out " + song.Title);
                return;
            }
            Tile play = youtube.WithUrl("https://www.youtube.com/watch?v=" + song.Id + "&list=RD" + song.Id);
            play.Args = ((play.Args ?? "") + " --autoplay-policy=no-user-gesture-required").Trim();
            OpenFromAnywhere(play);
        }

        /// <summary>A phone's Play next on a song (as PLAY says it): first in your up next; with nothing on YouTube, it plays now.</summary>
        void PlayNextById(string request)
        {
            string[] p = request.Split(' ');
            Suggestion s = VisibleSuggestions().Find(x => x.Id == p[0]) ?? Suggestions.FromPhone(p, _cfg.AppTiles);
            if (s == null) return;
            if (s.Kind != SuggestionKind.Song || YouTubeWindow() == IntPtr.Zero)
            {
                PlaySuggestion(s);
                return;
            }
            string id = YouTubePage.Param(s.PlayUrl, "v");
            if (id != null)
            {
                AddNext(id, s);
                return;
            }
            ThreadPool.QueueUserWorkItem(state =>
            {
                string url = null;
                try { url = Suggestions.FindVideo(s.Query); }
                catch (Exception ex) { Log.Info("Play next " + s.Title + ": " + ex.Message); }
                string found = YouTubePage.Param(url, "v");
                if (found != null) Dispatcher.BeginInvoke(new Action(() => AddNext(found, s)));
            });
        }

        void AddNext(string id, Suggestion s)
        {
            _upNext.RemoveAll(x => x.Id == id);
            _upNext.Insert(0, new QueueSong { Id = id, Title = s.Title, Artist = s.Subtitle ?? "" });
            if (_upNext.Count > MostQueue) _upNext.RemoveAt(_upNext.Count - 1);
            Log.Info("Play next: " + s.Title + " (" + id + ")");
            string message = "Plays next: " + s.Title;
            if (IsActive && !Modal) Toast(message);
            else Osd.ShowMessage('\uE8D6', message, 3);
            TellQueue();
        }

        /// <summary>A phone tapped a song in up next: one of yours goes on now; one of YouTube's list is played from it.</summary>
        void PlayFromQueue(string id)
        {
            QueueSong mine = _upNext.Find(s => s.Id == id);
            if (mine != null)
            {
                if (!_switching) SwitchTo(mine, false);
                return;
            }
            PageSong theirs = _pageList.Find(s => s.Id == id);
            IntPtr window = YouTubeWindow();
            if (theirs == null || window == IntPtr.Zero) return;
            bool seen = Native.GetForegroundWindow() == window;
            if (seen) Busy.Show("song", "Putting on " + theirs.Title, 10);
            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    if (!YouTubePage.Jump(window, theirs.Id, theirs.Index)) Log.Info("Up next: couldn't play " + theirs.Title + " from YouTube's list");
                }
                catch (Exception ex) { Log.Info("Up next: " + ex.Message); }
                if (seen) Busy.Hide("song");
            });
        }

        void ReadPageListSoon(int ms)
        {
            int read = ++_pageReads;
            var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            later.Tick += (s, e) =>
            {
                later.Stop();
                IntPtr window = YouTubeWindow();
                if (window == IntPtr.Zero)
                {
                    if (_pageList.Count > 0)
                    {
                        _pageList = new List<PageSong>();
                        TellQueue();
                    }
                    return;
                }
                ThreadPool.QueueUserWorkItem(state =>
                {
                    List<PageSong> list;
                    try { list = YouTubePage.Playlist(window); }
                    catch (Exception ex)
                    {
                        Log.Info("Up next: YouTube's list: " + ex.Message);
                        return;
                    }
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (read < _pageReads - 1) return;   // a newer read was asked for after the next one
                        _pageList = list;
                        TellQueue();
                    }));
                });
            };
            later.Start();
        }

        /// <summary>
        /// For phones: "QUEUE &lt;count&gt;", then that many "QITEM &lt;video&gt; &lt;A|Y&gt; &lt;seconds&gt; &lt;title&gt; &lt;artist&gt;": yours
        /// (A, added from a phone) then YouTube's list after the song playing (Y); texts in base64, "-" for none.
        /// </summary>
        internal static List<string> QueueLines(List<QueueSong> mine, List<PageSong> theirs, string playing)
        {
            var items = new List<string>();
            foreach (QueueSong s in mine)
                if (items.Count < MostQueue) items.Add("QITEM " + s.Id + " A 0 " + MediaB64(s.Title) + " " + MediaB64(s.Artist));
            int at = playing == null ? -1 : theirs.FindIndex(s => s.Title == playing);
            if (at >= 0)
                for (int i = at + 1; i < theirs.Count && items.Count < MostQueue; i++)
                    items.Add("QITEM " + theirs[i].Id + " Y " + theirs[i].Seconds + " " + MediaB64(theirs[i].Title) + " " + MediaB64(theirs[i].Artist));
            var lines = new List<string> { "QUEUE " + items.Count };
            lines.AddRange(items);
            return lines;
        }

        void TellQueue()
        {
            MediaNow now = NowPlaying.Current;
            List<string> lines = QueueLines(_upNext, _pageList, now != null ? now.Title : null);
            string joined = string.Join("\n", lines);
            _queueLines = joined;
            if (joined == _queueTold || _wifi == null) return;
            _queueTold = joined;
            _wifi.SendToPhones(lines);
        }
    }
}
