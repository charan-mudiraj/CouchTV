using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace CouchTV
{
    /// <summary>What's playing, as Windows' media controls last said: a snapshot that doesn't change.</summary>
    internal sealed class MediaNow
    {
        public string Source = "";            // the app Windows says it is: "Brave", "MSEdge", "Spotify.exe"...
        public string Title = "", Artist = "", Album = "";
        public bool Playing;
        public bool CanPlayPause, CanNext, CanPrevious, CanSeek;
        public long PositionMs, DurationMs;   // DurationMs 0: not known (a live stream)
        public DateTime AsOf;                 // UTC: when PositionMs was right
        public double Rate = 1;
        public byte[] Art;                    // a JPEG, at most ArtSize across; null for none
        public string ArtId = "-";            // changes with the picture ("-": none)

        /// <summary>Where it's got to at that moment (UTC): on from PositionMs while it plays.</summary>
        public long PositionAt(DateTime utc)
        {
            long at = PositionMs;
            if (Playing) at += (long)(Math.Max(0, (utc - AsOf).TotalMilliseconds) * Rate);
            return DurationMs > 0 ? Math.Min(at, DurationMs) : at;
        }

        /// <summary>The same as before for anything shown, apart from the position moving on as it plays.</summary>
        public bool Looks(MediaNow other)
        {
            return other != null && Source == other.Source && Title == other.Title && Artist == other.Artist && Album == other.Album &&
                   Playing == other.Playing && CanPlayPause == other.CanPlayPause && CanNext == other.CanNext &&
                   CanPrevious == other.CanPrevious && CanSeek == other.CanSeek && DurationMs / 1000 == other.DurationMs / 1000 &&
                   ArtId == other.ArtId;
        }
    }

    /// <summary>
    /// What the TV is playing, from Windows' media controls (the ones behind the media keys): browsers report
    /// YouTube, Netflix, Spotify and the rest there, with the title, artist, picture, timeline and which buttons work.
    /// The home screen shows it while it plays behind (HomeWindow.Media.cs) and phones show it as a notification, with
    /// the buttons going back here. Looked at every second on a thread of its own.
    /// </summary>
    internal static class NowPlaying
    {
        public const int ArtSize = 320, MostArtBytes = 24 * 1024;

        /// <summary>Something shown changed: another track, playing or paused, a jump in the timeline... (UI thread).</summary>
        public static event Action Changed;

        /// <summary>What's playing now, or null for nothing; changed on the UI thread, just before Changed.</summary>
        public static MediaNow Current { get; private set; }

        sealed class Ask
        {
            public string What;
            public long Ms;
        }

        static Dispatcher _ui;
        static Thread _thread;
        static readonly AutoResetEvent _poke = new AutoResetEvent(false);
        static readonly Queue<Ask> _asks = new Queue<Ask>();
        static MediaNow _told;        // the last snapshot handed to the UI thread
        static int _nothing;          // looks in a row that found nothing playing
        static int _quick;            // looks left at a quicker pace, after a button
        static long _startMs;         // the timeline's start, for seeking (the work thread's)

        // The picture, worked out once per track (the work thread's).
        static string _artKey;
        static DateTime _artUntil;
        static byte[] _artRaw, _art;
        static string _artId = "-";

        public static void Start(Dispatcher ui)
        {
            if (_thread != null) return;
            _ui = ui;
            _thread = new Thread(Work) { IsBackground = true, Name = "CouchTV now playing" };
            _thread.Start();
        }

        /// <summary>
        /// A button for what's playing: play, pause, toggle, next, previous, stop, or seek (to ms from the start).
        /// From any thread; done on the media thread.
        /// </summary>
        public static void Command(string what, long ms = 0)
        {
            lock (_asks)
            {
                if (_asks.Count > 8) return;
                _asks.Enqueue(new Ask { What = what, Ms = ms });
            }
            _poke.Set();
        }

        static void Work()
        {
            GlobalSystemMediaTransportControlsSessionManager manager = null;
            int failures = 0;
            while (true)
            {
                try
                {
                    if (manager == null) manager = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                    GlobalSystemMediaTransportControlsSession session = Pick(manager);
                    if (RunAsks(session)) _quick = 8;
                    Publish(session == null ? null : Read(session));
                    failures = 0;
                }
                catch (Exception ex)
                {
                    if (failures++ % 60 == 0) Log.Info("Now playing: " + ex.Message);
                    if (failures % 10 == 0) manager = null;   // ask Windows afresh
                }
                _poke.WaitOne(_quick > 0 ? 250 : 1000);
                if (_quick > 0) _quick--;
            }
        }

        /// <summary>The one to show: whatever's playing, else what Windows calls current, else anything paused.</summary>
        static GlobalSystemMediaTransportControlsSession Pick(GlobalSystemMediaTransportControlsSessionManager manager)
        {
            GlobalSystemMediaTransportControlsSession paused = null;
            foreach (GlobalSystemMediaTransportControlsSession s in manager.GetSessions())
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus status = s.GetPlaybackInfo().PlaybackStatus;
                if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return s;
                if (paused == null && Active(status)) paused = s;
            }
            GlobalSystemMediaTransportControlsSession current = manager.GetCurrentSession();
            if (current != null && Active(current.GetPlaybackInfo().PlaybackStatus)) return current;
            return paused;
        }

        static bool Active(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
        {
            return status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ||
                   status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused ||
                   status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing;
        }

        static MediaNow Read(GlobalSystemMediaTransportControlsSession session)
        {
            GlobalSystemMediaTransportControlsSessionPlaybackInfo info = session.GetPlaybackInfo();
            if (!Active(info.PlaybackStatus)) return null;
            GlobalSystemMediaTransportControlsSessionMediaProperties props = Wait(session.TryGetMediaPropertiesAsync());
            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();
            GlobalSystemMediaTransportControlsSessionPlaybackControls controls = info.Controls;
            var now = new MediaNow
            {
                Source = session.SourceAppUserModelId ?? "",
                Title = Tidy(props.Title),
                Artist = Tidy(props.Artist),
                Album = Tidy(props.AlbumTitle),
                // Changing: between two tracks, or loading. Shown as playing, as it will be in a moment.
                Playing = info.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
                CanPlayPause = controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled,
                CanNext = controls.IsNextEnabled,
                CanPrevious = controls.IsPreviousEnabled,
                CanSeek = controls.IsPlaybackPositionEnabled,
                Rate = info.PlaybackRate != null && info.PlaybackRate.Value > 0 ? info.PlaybackRate.Value : 1,
            };
            if (now.Title.Length == 0 && now.Artist.Length == 0) return null;   // a page that only made a sound
            long start = (long)timeline.StartTime.TotalMilliseconds, end = (long)timeline.EndTime.TotalMilliseconds;
            _startMs = start;
            now.DurationMs = end > start ? end - start : 0;
            now.PositionMs = Math.Max(0, (long)timeline.Position.TotalMilliseconds - start);
            DateTime asOf = timeline.LastUpdatedTime.UtcDateTime, utc = DateTime.UtcNow;
            now.AsOf = asOf.Year < 2000 || asOf > utc ? utc : asOf;
            ReadArt(props, now);
            return now;
        }

        static string Tidy(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();
            return s.Length > 200 ? s.Substring(0, 200) : s;
        }

        /// <summary>
        /// The picture, made small for the home screen and phones. Looked at for a few seconds after a new track
        /// starts: browsers often hand it over a moment after the title.
        /// </summary>
        static void ReadArt(GlobalSystemMediaTransportControlsSessionMediaProperties props, MediaNow now)
        {
            string key = now.Source + "\n" + now.Title + "\n" + now.Artist + "\n" + now.Album;
            DateTime utc = DateTime.UtcNow;
            if (key != _artKey)
            {
                _artKey = key;
                _artUntil = utc.AddSeconds(8);
                _artRaw = _art = null;
                _artId = "-";
            }
            if (utc < _artUntil && props.Thumbnail != null)
            {
                try
                {
                    byte[] raw = ReadAll(Wait(props.Thumbnail.OpenReadAsync()));
                    if (raw != null && raw.Length > 0 && (_artRaw == null || !Same(raw, _artRaw)))
                    {
                        _artRaw = raw;
                        byte[] art = MakeArt(raw);
                        if (art != null)
                        {
                            _art = art;
                            _artId = Secure.Hex(Secure.Sha256(art)).Substring(0, 12);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("Now playing: the picture: " + ex.Message);
                    _artUntil = utc;   // not again for this track
                }
            }
            now.Art = _art;
            now.ArtId = _artId;
        }

        static byte[] ReadAll(IRandomAccessStreamWithContentType stream)
        {
            using (stream)
            {
                if (stream.Size == 0 || stream.Size > 8 * 1024 * 1024) return null;
                using (var reader = new DataReader(stream.GetInputStreamAt(0)))
                {
                    uint got = Wait(reader.LoadAsync((uint)stream.Size));
                    var bytes = new byte[got];
                    reader.ReadBytes(bytes);
                    return bytes;
                }
            }
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// A JPEG at most ArtSize across and MostArtBytes long, without the black bars YouTube's pictures have above
        /// and below (a 16:9 video in a 4:3 picture). Null if it isn't a picture.
        /// </summary>
        internal static byte[] MakeArt(byte[] raw)
        {
            try
            {
                using (var input = new MemoryStream(raw))
                using (var image = new Bitmap(input))
                {
                    Rectangle keep = WithoutBars(image);
                    double scale = Math.Min(1.0, (double)ArtSize / Math.Max(keep.Width, keep.Height));
                    int w = Math.Max(1, (int)Math.Round(keep.Width * scale)), h = Math.Max(1, (int)Math.Round(keep.Height * scale));
                    using (var small = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                    {
                        using (Graphics g = Graphics.FromImage(small))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.Clear(Color.Black);
                            using (var wrap = new ImageAttributes())
                            {
                                wrap.SetWrapMode(WrapMode.TileFlipXY);   // no grey fringe at the edges
                                g.DrawImage(image, new Rectangle(0, 0, w, h), keep.X, keep.Y, keep.Width, keep.Height, GraphicsUnit.Pixel, wrap);
                            }
                        }
                        ImageCodecInfo jpeg = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid);
                        byte[] best = null;
                        foreach (long quality in new long[] { 86, 72, 55, 40 })
                        {
                            using (var output = new MemoryStream())
                            using (var settings = new EncoderParameters(1))
                            {
                                settings.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                                small.Save(output, jpeg, settings);
                                best = output.ToArray();
                            }
                            if (best.Length <= MostArtBytes) return best;
                        }
                        return null;   // a phone takes lines up to 64 KB: no picture rather than one that won't go
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("Now playing: not a picture (" + ex.Message + ")");
                return null;
            }
        }

        /// <summary>The picture without plain black rows at the top and bottom, or columns at the sides.</summary>
        static Rectangle WithoutBars(Bitmap image)
        {
            int w = image.Width, h = image.Height;
            Func<int, bool> darkRow = y =>
            {
                for (int x = 0; x < w; x += Math.Max(1, w / 40))
                    if (Bright(image.GetPixel(x, y))) return false;
                return true;
            };
            Func<int, bool> darkColumn = x =>
            {
                for (int y = 0; y < h; y += Math.Max(1, h / 40))
                    if (Bright(image.GetPixel(x, y))) return false;
                return true;
            };
            int top = 0, bottom = h - 1, left = 0, right = w - 1;
            while (top < h / 3 && darkRow(top)) top++;
            while (bottom > h * 2 / 3 && darkRow(bottom)) bottom--;
            while (left < w / 3 && darkColumn(left)) left++;
            while (right > w * 2 / 3 && darkColumn(right)) right--;
            // Only clear bars go: the same on both sides, as a video's black bars are. A dark picture (night, a black
            // album cover, a dark corner) keeps all of itself.
            if (!Bars(top, h - 1 - bottom, h))
            {
                top = 0;
                bottom = h - 1;
            }
            if (!Bars(left, w - 1 - right, w))
            {
                left = 0;
                right = w - 1;
            }
            return new Rectangle(left, top, right - left + 1, bottom - top + 1);
        }

        static bool Bars(int one, int other, int size)
        {
            return one < size / 3 && other < size / 3 && Math.Min(one, other) >= size / 24 && Math.Abs(one - other) <= Math.Max(2, size / 50);
        }

        static bool Bright(Color c) { return Math.Max(c.R, Math.Max(c.G, c.B)) > 14; }

        /// <summary>Does what phones and the remote asked; true if there was anything.</summary>
        static bool RunAsks(GlobalSystemMediaTransportControlsSession session)
        {
            bool any = false;
            while (true)
            {
                Ask ask;
                lock (_asks)
                {
                    if (_asks.Count == 0) return any;
                    ask = _asks.Dequeue();
                }
                any = true;
                if (session == null) continue;
                bool ok = false;
                try
                {
                    GlobalSystemMediaTransportControlsSessionPlaybackInfo info = session.GetPlaybackInfo();
                    bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    switch (ask.What)
                    {
                        case "play": ok = Wait(session.TryPlayAsync()); break;
                        case "pause": ok = Wait(session.TryPauseAsync()); break;
                        case "toggle":
                            ok = Wait(playing ? session.TryPauseAsync() : session.TryPlayAsync());
                            if (!ok) ok = Wait(session.TryTogglePlayPauseAsync());
                            break;
                        case "next": ok = Wait(session.TrySkipNextAsync()); break;
                        case "previous":
                            // Without a previous track (a single video), back to its start.
                            ok = !info.Controls.IsPreviousEnabled && info.Controls.IsPlaybackPositionEnabled
                                ? Wait(session.TryChangePlaybackPositionAsync(_startMs * 10000))
                                : Wait(session.TrySkipPreviousAsync());
                            break;
                        case "stop": ok = Wait(session.TryStopAsync()); break;
                        case "seek": ok = Wait(session.TryChangePlaybackPositionAsync((_startMs + Math.Max(0, ask.Ms)) * 10000)); break;
                    }
                }
                catch (Exception ex) { Log.Info("Now playing: " + ask.What + ": " + ex.Message); }
                if (!ok) Log.Info("Now playing: " + ask.What + " wasn't taken by " + session.SourceAppUserModelId);
            }
        }

        /// <summary>Hands a snapshot to the UI thread when anything shown changed (nothing: after two looks in a row).</summary>
        static void Publish(MediaNow now)
        {
            if (now == null)
            {
                // Between two videos there can be a moment with nothing.
                if (_told == null || ++_nothing < 2) return;
            }
            else
            {
                _nothing = 0;
                DateTime utc = DateTime.UtcNow;
                if (now.Looks(_told) && Math.Abs(now.PositionAt(utc) - _told.PositionAt(utc)) < 2000) return;
            }
            _told = now;
            if (_ui == null) return;
            _ui.BeginInvoke(new Action(() =>
            {
                Current = now;
                if (Changed != null) Changed();
            }));
        }

        /// <summary>For the self-test and screenshots: as if this were playing.</summary>
        internal static void Pretend(MediaNow now)
        {
            Current = now;
            if (Changed != null) Changed();
        }

        // Windows' asynchronous calls, waited for (without the SDK's helpers, which the compiler in Windows can't use).
        static T Wait<T>(IAsyncOperation<T> operation)
        {
            var clock = Stopwatch.StartNew();
            while (operation.Status == AsyncStatus.Started && clock.ElapsedMilliseconds < 5000) Thread.Sleep(5);
            if (operation.Status != AsyncStatus.Completed) throw new TimeoutException("Windows didn't answer (" + operation.Status + ")");
            return operation.GetResults();
        }
    }
}
