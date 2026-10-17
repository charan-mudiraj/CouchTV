using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace CouchTV
{
    /// <summary>A song in the list beside a YouTube video (a mix or a playlist): its video, place and length.</summary>
    internal sealed class PageSong
    {
        public string Id, Title, Artist;
        public int Index, Seconds;
    }

    /// <summary>
    /// The YouTube page in a browser window, through Windows' accessibility (as a screen reader sees it), so nothing is
    /// clicked on the screen, nothing takes the focus, and it works with the window behind other apps: the list of
    /// songs beside the video (YouTube's own "up next"), a song from that list, and any video put on in the same page
    /// (YouTube's search for its address, then its result). The browser turns its accessibility on when first asked,
    /// so a first look that finds nothing looks again. Slow-ish (a few hundred ms): call off the UI thread.
    /// </summary>
    internal static class YouTubePage
    {
        static readonly Condition Links = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink);
        static readonly Condition Buttons = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
        static readonly Regex Length = new Regex(@"(?:,?\s+(\d+)\s+(hour|minute|second)s?)+$");

        /// <summary>The songs in the list beside the video, in order; empty when there's none (or it can't be read).</summary>
        public static List<PageSong> Playlist(IntPtr hwnd)
        {
            AutomationElement window = Window(hwnd);
            if (window == null) return new List<PageSong>();
            AutomationElementCollection links = window.FindAll(TreeScope.Descendants, Links);
            if (links.Count < 3)
            {
                Thread.Sleep(900);   // the browser's accessibility, just woken
                links = window.FindAll(TreeScope.Descendants, Links);
            }
            List<PageSong> songs = Songs(links);
            if (songs.Count > 0) return songs;
            // YouTube folds the list away (after a video's put on from its search, say), leaving it out: open it.
            AutomationElement expand = window.FindFirst(TreeScope.Descendants, new AndCondition(Buttons, new PropertyCondition(AutomationElement.NameProperty, "Expand")));
            if (expand == null || !Invoke(expand)) return songs;
            Thread.Sleep(800);
            return Songs(window.FindAll(TreeScope.Descendants, Links));
        }

        static List<PageSong> Songs(AutomationElementCollection links)
        {
            var songs = new List<PageSong>();
            var seen = new HashSet<int>();
            foreach (AutomationElement a in links)
            {
                string url = Url(a);
                string id = Param(url, "v");
                int index;
                if (id == null || !int.TryParse(Param(url, "index"), out index) || !seen.Add(index)) continue;
                // The link reads "Title 3 minutes, 21 seconds Channel", its first part "Title 3 minutes, 21 seconds".
                string name = Name(a);
                AutomationElement first = TreeWalker.ControlViewWalker.GetFirstChild(a);
                string titled = first != null ? Name(first) : "";
                if (titled.Length == 0 || !name.StartsWith(titled, StringComparison.Ordinal)) titled = name;
                int seconds;
                songs.Add(new PageSong
                {
                    Id = id, Index = index, Title = WithoutLength(titled, out seconds), Seconds = seconds,
                    Artist = name.Length > titled.Length ? name.Substring(titled.Length).Trim() : "",
                });
            }
            songs.Sort((x, y) => x.Index.CompareTo(y.Index));
            return songs;
        }

        /// <summary>"Espresso 3 minutes, 21 seconds" → "Espresso", 201.</summary>
        internal static string WithoutLength(string text, out int seconds)
        {
            seconds = 0;
            Match m = Length.Match(text);
            if (!m.Success || m.Index == 0) return text.Trim();
            for (int i = 0; i < m.Groups[1].Captures.Count; i++)
            {
                int n = int.Parse(m.Groups[1].Captures[i].Value);
                string unit = m.Groups[2].Captures[i].Value;
                seconds += unit == "hour" ? n * 3600 : unit == "minute" ? n * 60 : n;
            }
            return text.Substring(0, m.Index).Trim();
        }

        /// <summary>A song from the list beside the video (by its video and place): played at once.</summary>
        public static bool Jump(IntPtr hwnd, string id, int index)
        {
            AutomationElement window = Window(hwnd);
            if (window == null) return false;
            foreach (AutomationElement a in window.FindAll(TreeScope.Descendants, Links))
            {
                string url = Url(a);
                if (Param(url, "v") == id && Param(url, "index") == index.ToString()) return Invoke(a);
            }
            return false;
        }

        /// <summary>
        /// Puts a video on in the page: YouTube's search for its address finds it first; returns that result, to be
        /// clicked (Click) when wanted, or null if it didn't come. A full-screen video leaves full screen first (the
        /// search box is hidden then): wasFullScreen says so, for FullScreen after. Takes a second or two.
        /// </summary>
        public static AutomationElement Find(IntPtr hwnd, string id, out bool wasFullScreen)
        {
            wasFullScreen = false;
            AutomationElement window = Window(hwnd);
            if (window == null) return null;
            AutomationElement box = SearchBox(window);
            if (box == null)
            {
                Thread.Sleep(900);   // the browser's accessibility, just woken
                box = SearchBox(window);
            }
            if (box == null)
            {
                AutomationElement exit = Button(window, "Exit full screen");
                if (exit != null && Invoke(exit))
                {
                    wasFullScreen = true;
                    Thread.Sleep(800);
                    box = SearchBox(window);
                }
            }
            object value;
            if (box == null || !box.TryGetCurrentPattern(ValuePattern.Pattern, out value)) return null;
            ((ValuePattern)value).SetValue("https://www.youtube.com/watch?v=" + id);
            AutomationElement search = Button(window, "Search");
            if (search == null || !Invoke(search)) return null;
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 9000)
            {
                Thread.Sleep(350);
                foreach (AutomationElement a in window.FindAll(TreeScope.Descendants, Links))
                {
                    string url = Url(a);
                    if (Param(url, "v") == id && Param(url, "index") == null) return a;
                }
            }
            return null;
        }

        /// <summary>The video full screen again (YouTube's own button), once it's on.</summary>
        public static void FullScreen(IntPtr hwnd)
        {
            AutomationElement window = Window(hwnd);
            AutomationElement full = window != null ? Button(window, "Full screen") : null;
            if (full != null) Invoke(full);
        }

        public static bool Click(AutomationElement element)
        {
            try { return Invoke(element); }
            catch (ElementNotAvailableException) { return false; }
        }

        static AutomationElement Window(IntPtr hwnd)
        {
            try { return hwnd == IntPtr.Zero ? null : AutomationElement.FromHandle(hwnd); }
            catch (Exception) { return null; }
        }

        static AutomationElement SearchBox(AutomationElement window)
        {
            return window.FindFirst(TreeScope.Descendants, new AndCondition(
                new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox),
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)),
                new PropertyCondition(AutomationElement.NameProperty, "Search")));
        }

        /// <summary>The first button whose name starts so ("Search", "Exit full screen (f)").</summary>
        static AutomationElement Button(AutomationElement window, string name)
        {
            foreach (AutomationElement b in window.FindAll(TreeScope.Descendants, Buttons))
                if (Name(b).StartsWith(name, StringComparison.OrdinalIgnoreCase)) return b;
            return null;
        }

        static bool Invoke(AutomationElement element)
        {
            object invoke;
            if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out invoke)) return false;
            ((InvokePattern)invoke).Invoke();
            return true;
        }

        static string Name(AutomationElement e)
        {
            try { return e.Current.Name ?? ""; }
            catch (ElementNotAvailableException) { return ""; }
        }

        /// <summary>A link's address ("" if it has none).</summary>
        static string Url(AutomationElement link)
        {
            try
            {
                object value;
                return link.TryGetCurrentPattern(ValuePattern.Pattern, out value) ? ((ValuePattern)value).Current.Value ?? "" : "";
            }
            catch (ElementNotAvailableException) { return ""; }
        }

        /// <summary>A parameter of a YouTube address ("v" in watch?v=...), or null.</summary>
        internal static string Param(string url, string key)
        {
            if (string.IsNullOrEmpty(url) || url.IndexOf("youtube.com/watch", StringComparison.OrdinalIgnoreCase) < 0) return null;
            int q = url.IndexOf('?');
            if (q < 0) return null;
            foreach (string part in url.Substring(q + 1).Split('&', '#'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && part.Substring(0, eq) == key) return Uri.UnescapeDataString(part.Substring(eq + 1));
            }
            return null;
        }
    }
}
