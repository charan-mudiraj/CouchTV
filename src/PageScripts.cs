using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CouchTV
{
    /// <summary>
    /// The websites' touches (the sites folder, Sites.cs) put into a browser running on one of CouchTV's own profiles
    /// (Netflix, Prime Video and JioHotstar in Edge) through the browser's DevTools protocol, as Edge no longer loads
    /// an extension from the command line. The browser is started with --remote-debugging-port=0: it picks a free port,
    /// listening on this PC only, and writes it to DevToolsActivePort in the profile's folder. CouchTV connects, finds
    /// each page (tab or app window) as it opens, and adds the scripts to it: in a world of their own beside the page's
    /// (as an extension's are), run on every page it loads from then on and on the one already showing. Settings
    /// changes go to the open pages at once. One connection a profile, kept while the browser runs.
    /// </summary>
    internal static class PageScripts
    {
        const string World = "couchtv";

        sealed class Link
        {
            public string Folder, Port;
            public ClientWebSocket Socket;
            public int NextId;
            public readonly object Sending = new object();
            public readonly Dictionary<int, Action<Dictionary<string, object>>> Waiting = new Dictionary<int, Action<Dictionary<string, object>>>();
            public readonly Dictionary<int, string> Asked = new Dictionary<int, string>();   // what each waiting request was
            public readonly Dictionary<string, string> Sessions = new Dictionary<string, string>();   // session → its script's identifier
            public readonly HashSet<string> Targets = new HashSet<string>();
            public readonly Dictionary<string, string> TargetSessions = new Dictionary<string, string>();   // target → its session
            public readonly HashSet<string> Watched = new HashSet<string>();                                // sessions whose requests are seen (YuppTV, JioHotstar)
            public readonly HashSet<string> Hotstar = new HashSet<string>();                                // sessions on JioHotstar: its ad servers refused
            public readonly Dictionary<string, string> PageIds = new Dictionary<string, string>();          // session → its page script's identifier
            public readonly Dictionary<string, int> Refused = new Dictionary<string, int>();                // session → ad requests refused
        }

        static readonly Dictionary<string, Link> Links = new Dictionary<string, Link>(StringComparer.OrdinalIgnoreCase);
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        /// <summary>After a browser on this profile folder was started (or given a new window): connect, if not already.</summary>
        public static void Attach(string folder)
        {
            ThreadPool.QueueUserWorkItem(state =>
            {
                try { Connect(folder); }
                catch (Exception ex) { Log.Error("Websites: reaching the browser through DevTools", ex); }
            });
        }

        /// <summary>Whether a connection to this profile's browser is open (for the diagnosis).</summary>
        public static string Describe()
        {
            var text = new StringBuilder();
            lock (Links)
            {
                foreach (Link link in Links.Values)
                    text.AppendLine(link.Folder + ": port " + link.Port + ", " + link.Socket.State + ", " + link.Sessions.Count + " page(s)");
            }
            return text.Length > 0 ? text.ToString() : "(none)\n";
        }

        static void Connect(string folder)
        {
            lock (Links)
            {
                Link live;
                if (Links.TryGetValue(folder, out live) && live.Socket.State == WebSocketState.Open) return;   // it finds the new window itself
            }
            // The port file can be left from the last time; the browser starting now writes it afresh. Try what's there
            // until a connection takes, for up to 25 s.
            string file = Path.Combine(folder, "DevToolsActivePort");
            var clock = Stopwatch.StartNew();
            Link link = null;
            string tried = null;
            while (link == null && clock.ElapsedMilliseconds < 25000)
            {
                string[] lines = null;
                try { if (File.Exists(file)) lines = File.ReadAllLines(file); }
                catch (IOException) { }
                if (lines != null && lines.Length >= 2 && lines[0].Trim().Length > 0)
                {
                    string url = "ws://127.0.0.1:" + lines[0].Trim() + lines[1].Trim();
                    if (url != tried || clock.ElapsedMilliseconds % 2000 < 300)
                    {
                        tried = url;
                        var socket = new ClientWebSocket();
                        try
                        {
                            if (socket.ConnectAsync(new Uri(url), CancellationToken.None).Wait(3000) && socket.State == WebSocketState.Open)
                                link = new Link { Folder = folder, Port = lines[0].Trim(), Socket = socket };
                        }
                        catch (AggregateException) { }
                        if (link == null) socket.Dispose();
                    }
                }
                if (link == null) Thread.Sleep(300);
            }
            if (link == null)
            {
                Log.Info("Websites: the browser on " + Path.GetFileName(folder) + " didn't open its DevTools port, so its pages are as they come");
                return;
            }
            lock (Links) Links[folder] = link;
            Log.Info("Websites: connected to the browser on " + Path.GetFileName(folder) + " (DevTools on port " + link.Port + ")");
            var reader = new Thread(() => Read(link)) { IsBackground = true, Name = "DevTools " + link.Port };
            reader.Start();
            Send(link, null, "Target.setDiscoverTargets", new Dictionary<string, object> { { "discover", true } }, null);
        }

        static void Read(Link link)
        {
            var buffer = new byte[65536];
            var message = new MemoryStream();
            try
            {
                while (link.Socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult got = link.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).Result;
                    if (got.MessageType == WebSocketMessageType.Close) break;
                    message.Write(buffer, 0, got.Count);
                    if (!got.EndOfMessage) continue;
                    string text = Encoding.UTF8.GetString(message.ToArray());
                    message.SetLength(0);
                    try { Handle(link, Json.Deserialize<Dictionary<string, object>>(text)); }
                    catch (Exception ex) { Log.Error("Websites: a DevTools message", ex); }
                }
            }
            catch (Exception) { /* the browser closed */ }
            lock (Links) { Link now; if (Links.TryGetValue(link.Folder, out now) && now == link) Links.Remove(link.Folder); }
            try { link.Socket.Dispose(); } catch (Exception) { }
        }

        static void Handle(Link link, Dictionary<string, object> m)
        {
            object idValue;
            if (m.TryGetValue("id", out idValue))
            {
                Action<Dictionary<string, object>> then = null;
                string asked = null;
                lock (link.Waiting)
                {
                    int id = Convert.ToInt32(idValue);
                    if (link.Waiting.TryGetValue(id, out then)) link.Waiting.Remove(id);
                    if (link.Asked.TryGetValue(id, out asked)) link.Asked.Remove(id);
                }
                var error = m.ContainsKey("error") ? m["error"] as Dictionary<string, object> : null;
                if (error != null) Log.Info("Websites: the browser refused " + asked + ": " + error["message"]);
                var answer = m.ContainsKey("result") ? m["result"] as Dictionary<string, object> : null;
                var failed = answer != null && answer.ContainsKey("exceptionDetails") ? answer["exceptionDetails"] as Dictionary<string, object> : null;
                if (failed != null)
                {
                    var exception = failed.ContainsKey("exception") ? failed["exception"] as Dictionary<string, object> : null;
                    Log.Info("Websites: the scripts failed in the page: " + (exception != null && exception.ContainsKey("description") ? exception["description"] : failed["text"]));
                }
                if (then != null) then(m.ContainsKey("result") ? m["result"] as Dictionary<string, object> : null);
                return;
            }
            string method = m.ContainsKey("method") ? m["method"] as string : null;
            var p = m.ContainsKey("params") ? m["params"] as Dictionary<string, object> : null;
            if (method == "Target.targetCreated" && p != null)
            {
                var info = p["targetInfo"] as Dictionary<string, object>;
                if (info == null || (info["type"] as string) != "page") return;
                string target = info["targetId"] as string;
                lock (link.Targets) { if (!link.Targets.Add(target)) return; }
                Send(link, null, "Target.attachToTarget", new Dictionary<string, object> { { "targetId", target }, { "flatten", true } }, result =>
                {
                    if (result == null) return;
                    string session = result["sessionId"] as string;
                    lock (link.TargetSessions) link.TargetSessions[target] = session;
                    Inject(link, session, target, info["url"] as string);
                    SiteRules(link, session, info["url"] as string);
                });
            }
            else if (method == "Target.targetInfoChanged" && p != null)
            {
                // A page went somewhere new: YuppTV's, say.
                var info = p["targetInfo"] as Dictionary<string, object>;
                string session = null;
                if (info != null) lock (link.TargetSessions) link.TargetSessions.TryGetValue(info["targetId"] as string ?? "", out session);
                if (session != null)
                {
                    SiteRules(link, session, info["url"] as string);
                    if (IsHost(info["url"] as string, "hotstar.com") && (info["url"] as string).Contains("/watch")) SayAds(link, session);
                }
            }
            else if (method == "Network.loadingFailed" && p != null)
            {
                // One of JioHotstar's ad servers refused (by setBlockedURLs): counted, for the log.
                string session = m.ContainsKey("sessionId") ? m["sessionId"] as string : null;
                if (session != null && p.ContainsKey("blockedReason") && p["blockedReason"] as string == "inspector")
                    lock (link.Refused) { int n; link.Refused.TryGetValue(session, out n); link.Refused[session] = n + 1; }
            }
            else if (method == "Network.requestWillBeSent" && p != null)
            {
                // YuppTV's page asking its own services: the session it uses, kept for the Live TV rows (LiveTv).
                var request = p["request"] as Dictionary<string, object>;
                var headers = request != null ? request["headers"] as Dictionary<string, object> : null;
                string url = request != null ? request["url"] as string : null;
                if (headers == null || url == null || !url.StartsWith("https://prod-", StringComparison.Ordinal) || url.IndexOf(".yupptv.com/", StringComparison.Ordinal) < 0) return;
                string sessionId = null, box = null;
                foreach (var h in headers)
                {
                    if (h.Key.Equals("session-id", StringComparison.OrdinalIgnoreCase)) sessionId = h.Value as string;
                    else if (h.Key.Equals("box-id", StringComparison.OrdinalIgnoreCase)) box = h.Value as string;
                }
                if (sessionId != null && box != null) LiveTv.TakeSession(sessionId, box);
            }
            else if (method == "Target.detachedFromTarget" && p != null)
            {
                string gone = p["sessionId"] as string;
                lock (link.Sessions) link.Sessions.Remove(gone);
                lock (link.PageIds) link.PageIds.Remove(gone);
                lock (link.Hotstar) link.Hotstar.Remove(gone);
                lock (link.Watched) link.Watched.Remove(gone);
                lock (link.Refused) link.Refused.Remove(gone);
            }
            else if (method == "Target.targetDestroyed" && p != null)
            {
                lock (link.Targets) link.Targets.Remove(p["targetId"] as string);
                lock (link.TargetSessions) link.TargetSessions.Remove(p["targetId"] as string);
            }
        }

        /// <summary>
        /// What some sites' pages get. YuppTV's: their requests are seen (Network), for the session they send with
        /// them. JioHotstar's: Edge refuses its ad servers (with HotstarAds on). Nothing else's requests are looked at.
        /// </summary>
        static void SiteRules(Link link, string session, string url)
        {
            bool yupp = IsHost(url, "yupptv.com"), hotstar = IsHost(url, "hotstar.com");
            if (!yupp && !hotstar) return;
            bool first;
            lock (link.Watched) first = link.Watched.Add(session);
            if (first) Send(link, session, "Network.enable", new Dictionary<string, object>(), null);
            if (!hotstar) return;
            lock (link.Hotstar) { if (!link.Hotstar.Add(session)) return; }
            BlockAds(link, session);
        }

        static bool IsHost(string url, string site)
        {
            Uri uri;
            return url != null && Uri.TryCreate(url, UriKind.Absolute, out uri) && (uri.Host == site || uri.Host.EndsWith("." + site));
        }

        /// <summary>
        /// JioHotstar's ad servers refused on this page, or let through again (HotstarAds off). Edge's older way of
        /// saying it first ("urls"), then its newer one ("urlPatterns") if it won't take that.
        /// </summary>
        static void BlockAds(Link link, string session)
        {
            string[] servers = Sites.BlockHotstarAds ? Sites.HotstarAdServers : new string[0];
            Send(link, session, "Network.setBlockedURLs", new Dictionary<string, object> { { "urls", servers } }, result =>
            {
                if (result != null) return;
                var patterns = new List<object>();
                foreach (string s in servers) patterns.Add(new Dictionary<string, object> { { "urlPattern", s }, { "block", true } });
                Send(link, session, "Network.setBlockedURLs", new Dictionary<string, object> { { "urlPatterns", patterns } }, null);
            });
        }

        /// <summary>A JioHotstar film or show opened: in the log a while later, how many ad slots and ad requests were kept off it.</summary>
        static void SayAds(Link link, string session)
        {
            ThreadPool.QueueUserWorkItem(state =>
            {
                Thread.Sleep(20000);
                Send(link, session, "Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", "document.documentElement.getAttribute('data-couchtv-ads') || '0'" }, { "returnByValue", true },
                }, result =>
                {
                    var value = result != null && result.ContainsKey("result") ? result["result"] as Dictionary<string, object> : null;
                    int refused;
                    lock (link.Refused) link.Refused.TryGetValue(session, out refused);
                    Log.Info("Websites: JioHotstar: " + (Sites.BlockHotstarAds ? "ads kept off, " + (value != null ? value["value"] : "?") +
                             " ad slots taken out of the player's data, " + refused + " ad requests refused" : "ads allowed (HotstarAds off)"));
                });
            });
        }

        /// <summary>The scripts into a page: for every page it loads from now on, and the one it shows.</summary>
        static void Inject(Link link, string session, string target, string url)
        {
            string settings = Sites.CurrentSettings;
            Send(link, session, "Page.addScriptToEvaluateOnNewDocument",
                new Dictionary<string, object> { { "source", Sites.Bundle(settings) }, { "worldName", World } }, result =>
                {
                    if (result != null) lock (link.Sessions) link.Sessions[session] = result["identifier"] as string;
                });
            Send(link, session, "Page.addScriptToEvaluateOnNewDocument", new Dictionary<string, object> { { "source", Sites.PageBundle() } }, result =>
            {
                if (result != null) lock (link.PageIds) link.PageIds[session] = result["identifier"] as string;
            });
            Send(link, session, "Page.createIsolatedWorld", new Dictionary<string, object> { { "frameId", target }, { "worldName", World } }, result =>
            {
                if (result == null) return;
                Send(link, session, "Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", Sites.Bundle(settings) }, { "contextId", result["executionContextId"] },
                }, null);
            });
            Send(link, session, "Runtime.evaluate", new Dictionary<string, object> { { "expression", Sites.PageBundle() } }, null);
            // A look a few seconds on: they're running (the page's mark), and what they made of it.
            ThreadPool.QueueUserWorkItem(state =>
            {
                Thread.Sleep(8000);
                Send(link, session, "Runtime.evaluate", new Dictionary<string, object>
                {
                    { "expression", "location.hostname + ' | ' + document.documentElement.getAttribute('data-couchtv') + ' | ' + " +
                                    "((document.querySelector('[role=\"note\"][aria-label^=\"CouchTV:\"]') || {}).ariaLabel || '')" },
                    { "returnByValue", true },
                }, result =>
                {
                    var value = result != null && result.ContainsKey("result") ? result["result"] as Dictionary<string, object> : null;
                    string said = value != null && value.ContainsKey("value") ? value["value"] as string : null;
                    if (said != null && said.IndexOf("| null |", StringComparison.Ordinal) < 0)
                        Log.Info("Websites: running in " + said);
                    else if (said != null && !said.StartsWith(" |"))
                        Log.Info("Websites: not running in " + said.Split('|')[0].Trim() + " (not a site they're for, or they failed)");
                });
            });
        }

        /// <summary>A setting changed: to every open page now, and to the pages they load next.</summary>
        public static void SettingsChanged(string settings)
        {
            var links = new List<Link>();
            lock (Links) links.AddRange(Links.Values);
            foreach (Link link in links)
            {
                var sessions = new Dictionary<string, string>();
                lock (link.Sessions) foreach (var pair in link.Sessions) sessions[pair.Key] = pair.Value;
                foreach (var pair in sessions)
                {
                    string session = pair.Key;
                    Send(link, session, "Runtime.evaluate", new Dictionary<string, object>
                    {
                        { "expression", "document.documentElement.setAttribute('data-couchtv-settings', " + Json.Serialize(settings) + ")" },
                    }, null);
                    if (pair.Value != null)
                        Send(link, session, "Page.removeScriptToEvaluateOnNewDocument", new Dictionary<string, object> { { "identifier", pair.Value } }, null);
                    Send(link, session, "Page.addScriptToEvaluateOnNewDocument",
                        new Dictionary<string, object> { { "source", Sites.Bundle(settings) }, { "worldName", World } }, result =>
                        {
                            if (result != null) lock (link.Sessions) if (link.Sessions.ContainsKey(session)) link.Sessions[session] = result["identifier"] as string;
                        });
                    // The page scripts too (JioHotstar's ads on or off for its next page), and its ad servers.
                    string page;
                    lock (link.PageIds) link.PageIds.TryGetValue(session, out page);
                    if (page != null) Send(link, session, "Page.removeScriptToEvaluateOnNewDocument", new Dictionary<string, object> { { "identifier", page } }, null);
                    Send(link, session, "Page.addScriptToEvaluateOnNewDocument", new Dictionary<string, object> { { "source", Sites.PageBundle() } }, result =>
                    {
                        if (result != null) lock (link.PageIds) link.PageIds[session] = result["identifier"] as string;
                    });
                    bool hotstar;
                    lock (link.Hotstar) hotstar = link.Hotstar.Contains(session);
                    if (hotstar) BlockAds(link, session);
                }
            }
        }

        static void Send(Link link, string session, string method, Dictionary<string, object> parameters, Action<Dictionary<string, object>> then)
        {
            int id;
            lock (link.Waiting)
            {
                id = ++link.NextId;
                if (then != null) link.Waiting[id] = then;
                link.Asked[id] = method;
            }
            var message = new Dictionary<string, object> { { "id", id }, { "method", method }, { "params", parameters ?? new Dictionary<string, object>() } };
            if (session != null) message["sessionId"] = session;
            byte[] bytes = Encoding.UTF8.GetBytes(Json.Serialize(message));
            try
            {
                lock (link.Sending) link.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait(5000);
            }
            catch (Exception) { lock (link.Waiting) link.Waiting.Remove(id); }
        }

        static string Short(string url)
        {
            if (string.IsNullOrEmpty(url)) return "a new page";
            int query = url.IndexOf('?');
            return query > 0 ? url.Substring(0, query) : url;
        }
    }
}
