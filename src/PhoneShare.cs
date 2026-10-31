using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CouchTV
{
    /// <summary>
    /// Phones' screens and sound on the TV ("Phone screens" in ir-receiver/PROTOCOL.md; the phone's side is
    /// ScreenShare.kt), for showing everyone in front of it something on a phone: up to 3 at once, side by side, their
    /// sounds mixed. Each phone sends its screen as H.264 from its own video encoder, and its sound as plain 16-bit
    /// stereo, on a paired connection of its own (RemoteServer.ShareScreen). This passes them on as they come, without
    /// waiting or decoding, to the page that shows them (home/share.html, in ShareWindow's WebView2), over a WebSocket
    /// on this PC only (127.0.0.1, with a token in the address), where Edge's own decoder shows the pictures (on the
    /// graphics chip) and the sounds are mixed: about a tenth of a second behind the phone.
    ///   To the page, each binary message is the phone's slot (0-2), then the phone's message as it sent it: 1 and its
    ///   picture's width and height (16-bit), 2 and a picture (flags, 1 = whole; its time in µs, 64-bit; H.264, Annex B),
    ///   3 and 10 ms of sound (48 kHz, stereo, 16-bit little-endian). Text messages say which phones there are:
    ///   {"phones":[{"slot":0,"name":"…"}]}. The page says "ready", "key &lt;slot&gt;" (a decoder needs a whole picture,
    ///   asked of that phone) and "stats &lt;json&gt;".
    /// </summary>
    internal sealed class PhoneShare : IDisposable
    {
        public const int Most = 3;

        /// <summary>One phone sharing its screen: its slot on the TV, its name and address, and how to tell it or let it go.</summary>
        public sealed class Phone
        {
            public int Slot;
            public string Name;
            public IPAddress Address;
            public volatile bool Stopped;
            internal Action<string> Tell;
            internal Action Close;
            internal int Pictures, Sounds;
            // Its sound, kept a moment (half a second, 48 kHz stereo) for the others' mixes (OthersFor).
            internal readonly short[] Ring = new short[RingSamples];
            internal long Written;   // samples written to Ring ever: where the next goes is Written % RingSamples
            // Where this phone, listening, has got to in each of the others' sound (by slot), and the part of a sample.
            internal readonly long[] ReadAt = new long[Most];
            internal readonly double[] ReadPart = new double[Most];
            internal readonly bool[] Reading = new bool[Most];
        }

        const int Rate = 48000;
        const int RingSamples = Rate;                 // half a second of stereo
        const int CushionSamples = Rate * 2 / 25;     // 40 ms, as the page keeps
        const int MostBehindSamples = Rate * 2 * 3 / 20;   // 150 ms: further behind than that, it catches up

        /// <summary>Phones came or went: their names now (on a background thread).</summary>
        public event Action<List<string>, string, bool> Changed;

        /// <summary>The page's last word on how it's showing them ("stats …"), for the log and the self-test.</summary>
        public string Stats { get; private set; }

        readonly Phone[] _phones = new Phone[Most];
        readonly TcpListener _listener;
        readonly string _token = Secure.Hex(Secure.Random(16));
        readonly object _pageLock = new object();
        Socket _page;
        volatile bool _closed;

        public PhoneShare()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            new Thread(Accept) { IsBackground = true, Name = "CouchTV phone screens page" }.Start();
        }

        /// <summary>The page, on this PC only.</summary>
        public string PageUrl
        {
            get { return "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/share.html?t=" + _token; }
        }

        public int Count
        {
            get { lock (_phones) { int n = 0; foreach (Phone p in _phones) if (p != null) n++; return n; } }
        }

        // ---------------------------------------------------------------- the phones

        /// <summary>A phone starting to share: its slot, or null when 3 are already on the TV.</summary>
        public Phone Begin(string name, IPAddress address, Action<string> tell, Action close)
        {
            Phone phone;
            lock (_phones)
            {
                int slot = Array.IndexOf(_phones, null);
                if (slot < 0) return null;
                phone = _phones[slot] = new Phone { Slot = slot, Name = string.IsNullOrEmpty(name) ? "A phone" : name, Address = address, Tell = tell, Close = close };
            }
            Log.Info("Phone screens: " + phone.Name + " started showing its screen");
            TellPage(false);   // (its first picture is whole anyway; and it hasn't been told SHARE OK yet)
            Raise(phone.Name, true);
            return phone;
        }

        /// <summary>It stopped, went, or the TV closed it.</summary>
        public void End(Phone phone)
        {
            lock (_phones)
            {
                if (_phones[phone.Slot] != phone) return;
                _phones[phone.Slot] = null;
            }
            phone.Stopped = true;
            Log.Info("Phone screens: " + phone.Name + " stopped (" + phone.Pictures + " pictures, " + phone.Sounds + " sound chunks)");
            TellPage(false);
            Raise(phone.Name, false);
        }

        /// <summary>A message from the phone (its picture's size, a picture, a chunk of sound): to the page as it is.</summary>
        public void Received(Phone phone, byte[] message)
        {
            if (message.Length == 0) return;
            if (message[0] == 2) phone.Pictures++;
            else if (message[0] == 1 && message.Length >= 5)
                Log.Info("Phone screens: " + phone.Name + "'s picture is " + ((message[1] << 8) | message[2]) + "x" + ((message[3] << 8) | message[4]));
            else if (message[0] == 3)
            {
                phone.Sounds++;
                lock (phone.Ring)
                {
                    for (int i = 1; i + 1 < message.Length; i += 2)
                        phone.Ring[phone.Written++ % RingSamples] = (short)(message[i] | (message[i + 1] << 8));
                }
            }
            SendPage(phone.Slot, message);
        }

        /// <summary>
        /// For a phone that listens to the TV's sound while it shares its screen ("Listen on this phone"; AudioShare
        /// asks): the other phones' sound mixed, without its own, which it plays itself, with no delay. [into]: that many
        /// samples, 16-bit stereo, at [rate] (the TV's own sound's). False when that phone isn't sharing: it gets the
        /// TV's sound as it is.
        /// </summary>
        public bool OthersFor(IPAddress address, short[] into, int rate)
        {
            Phone listener = null;
            var others = new List<Phone>();
            lock (_phones)
                foreach (Phone p in _phones)
                {
                    if (p == null) continue;
                    if (listener == null && p.Address != null && p.Address.Equals(address)) listener = p;
                    else others.Add(p);
                }
            if (listener == null) return false;
            Array.Clear(into, 0, into.Length);
            int frames = into.Length / 2;
            double step = (double)Rate / rate;   // the phones' sound is 48 kHz; the TV's may be 44.1
            foreach (Phone other in others)
            {
                lock (other.Ring)
                {
                    int slot = other.Slot;
                    long written = other.Written;
                    long at = listener.ReadAt[slot];
                    // From 40 ms behind the newest when it starts, or after falling more than 150 ms behind.
                    if (!listener.Reading[slot] || written - at > MostBehindSamples || at > written)
                    {
                        at = Math.Max(0, written - CushionSamples) & ~1L;
                        listener.ReadPart[slot] = 0;
                        listener.Reading[slot] = true;
                    }
                    double part = listener.ReadPart[slot];
                    for (int f = 0; f < frames; f++)
                    {
                        long sample = at + 2 * (long)part;
                        if (sample + 1 >= written) break;   // ran dry: silence for the rest, and no further on
                        into[2 * f] = Clip(into[2 * f] + other.Ring[sample % RingSamples]);
                        into[2 * f + 1] = Clip(into[2 * f + 1] + other.Ring[(sample + 1) % RingSamples]);
                        part += step;
                    }
                    long whole = (long)part;
                    listener.ReadAt[slot] = Math.Min(at + 2 * whole, written & ~1L);
                    listener.ReadPart[slot] = part - whole;
                }
            }
            return true;
        }

        static short Clip(int v) { return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, v)); }

        /// <summary>What's playing on the TV behind the phones (muted meanwhile), for the page to say; null when nothing is.</summary>
        public void TellPlayingBehind(string what)
        {
            if (what == _behind) return;
            _behind = what;
            var json = new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "behind", what } });
            SendFrame(0x81, Encoding.UTF8.GetBytes(json), 0, -1);
        }

        string _behind;

        /// <summary>The TV closes them all (Back or Home on the remote): each phone's told, and let go.</summary>
        public void StopAll()
        {
            List<Phone> all;
            lock (_phones) all = new List<Phone>(Array.FindAll(_phones, p => p != null));
            foreach (Phone p in all)
            {
                p.Stopped = true;
                try { p.Tell("STOP"); } catch (Exception) { }
                try { p.Close(); } catch (Exception) { }
            }
        }

        void AskWholePicture(int slot)
        {
            Phone phone;
            lock (_phones) phone = slot >= 0 && slot < Most ? _phones[slot] : null;
            if (phone == null) return;
            try { phone.Tell("KEY"); } catch (Exception) { }
        }

        void Raise(string name, bool started)
        {
            var names = new List<string>();
            lock (_phones) foreach (Phone p in _phones) if (p != null) names.Add(p.Name);
            Action<List<string>, string, bool> changed = Changed;
            if (changed != null) changed(names, name, started);
        }

        // ---------------------------------------------------------------- the page

        void Accept()
        {
            while (!_closed)
            {
                Socket client;
                try { client = _listener.AcceptSocket(); }
                catch (Exception) { if (_closed) return; continue; }
                new Thread(() => Serve(client)) { IsBackground = true, Name = "CouchTV phone screens request" }.Start();
            }
        }

        /// <summary>The page itself (GET /share.html), or its WebSocket (GET /ws); anything else, or without the token, nothing.</summary>
        void Serve(Socket client)
        {
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 5000;
                string head = ReadHead(client);
                if (head == null) { client.Close(); return; }
                string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] request = lines[0].Split(' ');
                if (request.Length < 2 || request[0] != "GET") { client.Close(); return; }
                string path = request[1], query = "";
                int q = path.IndexOf('?');
                if (q >= 0) { query = path.Substring(q + 1); path = path.Substring(0, q); }
                if (query != "t=" + _token) { Respond(client, "404 Not Found", "text/plain", new byte[0]); return; }
                if (path == "/share.html")
                {
                    Respond(client, "200 OK", "text/html; charset=utf-8", Page());
                    return;
                }
                string key = null;
                foreach (string line in lines)
                    if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) key = line.Substring(18).Trim();
                if (path != "/ws" || key == null) { Respond(client, "404 Not Found", "text/plain", new byte[0]); return; }
                string accept;
                using (SHA1 sha = SHA1.Create())
                    accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                byte[] upgrade = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                client.Send(upgrade);
                client.ReceiveTimeout = 0;
                client.SendTimeout = 3000;
                Socket old;
                lock (_pageLock)
                {
                    old = _page;
                    _page = client;
                }
                if (old != null) try { old.Close(); } catch (Exception) { }
                ReadPage(client);
            }
            catch (Exception) { try { client.Close(); } catch (Exception) { } }
        }

        static string ReadHead(Socket client)
        {
            var head = new StringBuilder();
            var one = new byte[1];
            while (head.Length < 8192)
            {
                if (client.Receive(one) <= 0) return null;
                head.Append((char)one[0]);
                if (head.Length >= 4 && head[head.Length - 1] == '\n' && head.ToString(head.Length - 4, 4) == "\r\n\r\n") return head.ToString(0, head.Length - 4);
            }
            return null;
        }

        static void Respond(Socket client, string status, string type, byte[] body)
        {
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                                                  "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            client.Send(head);
            if (body.Length > 0) client.Send(body);
            client.Shutdown(SocketShutdown.Both);
            client.Close();
        }

        static byte[] Page()
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CouchTV.home.share.html"))
            using (var bytes = new MemoryStream())
            {
                s.CopyTo(bytes);
                return bytes.ToArray();
            }
        }

        /// <summary>The page's messages (WebSocket frames from a browser are masked): "ready", "key &lt;slot&gt;", "stats …".</summary>
        void ReadPage(Socket page)
        {
            var head = new byte[14];
            while (!_closed)
            {
                if (!Exactly(page, head, 0, 2)) break;
                int opcode = head[0] & 0x0F;
                long length = head[1] & 0x7F;
                bool masked = (head[1] & 0x80) != 0;
                if (length == 126)
                {
                    if (!Exactly(page, head, 2, 2)) break;
                    length = (head[2] << 8) | head[3];
                }
                else if (length == 127) break;   // the page never says that much
                var mask = new byte[4];
                if (masked && !Exactly(page, mask, 0, 4)) break;
                if (length > 65536) break;
                var payload = new byte[length];
                if (!Exactly(page, payload, 0, (int)length)) break;
                if (masked) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];
                if (opcode == 8) break;   // closing
                if (opcode != 1) continue;
                string text = Encoding.UTF8.GetString(payload);
                if (text == "ready")
                {
                    TellPage(true);
                    string behind = _behind;
                    _behind = null;
                    TellPlayingBehind(behind);
                }
                else if (text.StartsWith("key ")) { int slot; if (int.TryParse(text.Substring(4), out slot)) AskWholePicture(slot); }
                else if (text.StartsWith("stats ")) Stats = text.Substring(6);
            }
            lock (_pageLock) if (_page == page) _page = null;
            try { page.Close(); } catch (Exception) { }
        }

        static bool Exactly(Socket s, byte[] into, int at, int count)
        {
            int done = 0;
            while (done < count)
            {
                int n;
                try { n = s.Receive(into, at + done, count - done, SocketFlags.None); }
                catch (Exception) { return false; }
                if (n <= 0) return false;
                done += n;
            }
            return true;
        }

        /// <summary>Which phones there are; [askWhole]: each asked for a whole picture (a page that's just opened starts on one).</summary>
        void TellPage(bool askWhole)
        {
            var phones = new List<Dictionary<string, object>>();
            lock (_phones)
                foreach (Phone p in _phones)
                    if (p != null) phones.Add(new Dictionary<string, object> { { "slot", p.Slot }, { "name", p.Name } });
            string json = new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "phones", phones } });
            SendFrame(0x81, Encoding.UTF8.GetBytes(json), 0, -1);
            if (askWhole) for (int slot = 0; slot < Most; slot++) AskWholePicture(slot);
        }

        void SendPage(int slot, byte[] message)
        {
            SendFrame(0x82, message, 0, slot);
        }

        /// <summary>A WebSocket frame to the page (unmasked, as a server's are): binary ones start with the phone's slot.</summary>
        void SendFrame(byte first, byte[] payload, int offset, int slot)
        {
            int length = payload.Length - offset + (slot >= 0 ? 1 : 0);
            var head = new byte[11];
            int headLength;
            head[0] = first;
            if (length < 126) { head[1] = (byte)length; headLength = 2; }
            else if (length < 65536) { head[1] = 126; head[2] = (byte)(length >> 8); head[3] = (byte)length; headLength = 4; }
            else
            {
                head[1] = 127;
                for (int i = 0; i < 8; i++) head[2 + i] = (byte)((long)length >> (56 - 8 * i));
                headLength = 10;
            }
            if (slot >= 0) head[headLength++] = (byte)slot;
            lock (_pageLock)
            {
                if (_page == null) return;
                try
                {
                    _page.Send(new List<ArraySegment<byte>> { new ArraySegment<byte>(head, 0, headLength), new ArraySegment<byte>(payload, offset, payload.Length - offset) });
                }
                catch (Exception)
                {
                    try { _page.Close(); } catch (Exception) { }
                    _page = null;
                }
            }
        }

        public void Dispose()
        {
            _closed = true;
            StopAll();
            try { _listener.Stop(); } catch (Exception) { }
            lock (_pageLock) if (_page != null) try { _page.Close(); } catch (Exception) { }
        }
    }
}
