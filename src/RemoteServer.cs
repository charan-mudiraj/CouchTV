using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace CouchTV
{
    /// <summary>
    /// The phone remote over Wi-Fi, for when pointing is a bother or the phone has no IR blaster. See "Wi-Fi" and
    /// "Security" in ir-receiver/PROTOCOL.md.
    ///   Finding the TV: the phone broadcasts "COUCHTV?" to UDP port 47700; this answers "COUCHTV 2 47701 &lt;name&gt;".
    ///   Then a TCP connection to port 47701. Only phones paired with this TV get any further: a phone pairs once, by
    ///   typing a code the TV shows, and every connection after that proves it's that phone and is encrypted (keys
    ///   from ECDH, see Secure.cs), so others on a shared Wi-Fi can neither use the TV nor read what's sent.
    ///     HELLO2 &lt;app&gt; &lt;version&gt; &lt;phone id&gt; &lt;phone key&gt;  →  HELLO2 &lt;TV id&gt; &lt;TV key&gt; KNOWN|PAIR &lt;name&gt;
    ///     (pairing: PAIR &lt;name&gt;  →  CODE, then 20 rounds of commitments to the code's bits)
    ///     AUTH &lt;proof&gt;           →  AUTH &lt;proof&gt;   and from here on every line is encrypted:
    ///     HELLO &lt;app&gt; &lt;version&gt; →  WELCOME &lt;name&gt;, POINTER &lt;look&gt;, BOARD &lt;receiver box key&gt;, AUDIO &lt;session id&gt;,
    ///                               SUGGESTIONS &lt;count&gt; and that many SUGGEST lines (again whenever the list changes),
    ///                               SLEEPTIMER &lt;seconds left, 0 when off&gt; (again whenever it changes)
    ///     PING                     →  PONG          (every second, so both sides notice a dead link quickly)
    ///     SOUND                    →  AUDIO &lt;session id&gt;   (the phone's sound service has a connection of its own)
    ///     BTN &lt;address&gt; &lt;command&gt; N|R   a button, exactly like the IR receiver's "NEC 00CE 00xx" codes
    ///     TEXT &lt;base64 UTF-8&gt;       words for search (voice search)
    ///     MOVE &lt;dx&gt; &lt;dy&gt;            the phone's trackpad: move the pointer by that many pixels
    ///     WHEEL &lt;dy&gt; [&lt;dx&gt;]         scroll (120 = a mouse wheel notch; up/right when positive)
    ///     MOUSE L|R D|U             a mouse button down or up (a phone that drops lets go of what it held)
    ///     POINTER &lt;style&gt; &lt;size&gt; &lt;colour&gt;   the pointer's look, e.g. "POINTER arrow 64 white"
    ///     KEY &lt;vk&gt; D|U             the keyboard page: a PC key down or up (Windows virtual-key code, hex)
    ///     TOUCH &lt;finger&gt; D|M|U &lt;x&gt; &lt;y&gt;   the screen mirror: a finger down, moved or up (0-65535 across the screen)
    ///     MIRROR &lt;width&gt; &lt;kbps&gt;   on a connection of its own: from then on the TV sends its screen (ScreenMirror)
    ///     EDIT &lt;backspaces&gt; &lt;base64 UTF-8&gt;   the phone's own keyboard: delete that many, then type the text
    ///                  →  INPUT TEXT|PASSWORD &lt;name&gt; &lt;value&gt;, INPUT OFF   a text box got (or lost) the focus on the
    ///                               TV, so the phone pops up its keyboard: told to the phone used last
    ///     PLAY &lt;id&gt; [&lt;what it is&gt;]   play a suggestion (from the SUGGEST lines, or one the phone found) on the TV
    ///     TRACKPAD ON|OFF           the phone's trackpad page is open (or not): the TV shows its pointer meanwhile
    ///     SLEEPTIMER &lt;minutes&gt;      the sleep timer: the TV goes to sleep that many minutes from now (0: off)
    ///     POWERSOON &lt;seconds&gt;      Power was tapped on the phone with the TV on: it goes in that many seconds (0: cancelled)
    ///     LOG [&lt;lines&gt;]             →  LOG &lt;count&gt;, then that many lines "| &lt;line&gt;": the end of the TV's log (CouchTV.exe --tv)
    /// Buttons go through the same remote.ini mapping as infrared ones, so both remotes behave the same.
    /// </summary>
    internal sealed class RemoteServer
    {
        public const int DiscoveryPort = 47700, ControlPort = 47701;
        public const string RuleName = "CouchTV phone remote";
        public const int CodeBits = 20;                           // a 6-digit pairing code fits in 20 bits
        public const int MostWrongCodes = 5, MostPairings = 15;   // in 10 minutes, then pairing waits
        const int MostClients = 16, MostLine = 8192;
        static readonly TimeSpan PairWindow = TimeSpan.FromMinutes(10);

        public event Action<IrSignal> Button;      // raised on the UI thread
        public event Action<string> Text;          // raised on the UI thread
        public event Action<string> Connected;     // a phone connected (its address), on the UI thread
        public event Action<string, int, string> PointerLook;   // the phone chose a pointer: style, size, colour (UI thread)
        public event Action<string, string> PairingCode;    // a phone asked to pair: its name and the code to show (UI thread)
        public event Action<string, string> PairingEnded;   // its name and "paired", "wrong", "failed" or "cancelled" (UI thread)
        public event Action<int> PhonesChanged;              // how many phones are connected as remotes now (UI thread)
        public event Action<string, bool> Mirroring;         // a phone started (true) or stopped mirroring the screen: its name (UI thread)
        public event Action<string> Play;                    // a phone chose a suggestion: its id and what it is (UI thread)
        public event Action<bool> Trackpad;                  // a phone has its trackpad page open (true), or none has now (UI thread)
        public event Action<int> SleepTimer;                 // a phone set the sleep timer: minutes from now, 0 for off (UI thread)
        public event Action<int> PowerSoon;                  // a phone's Power counts down: seconds left, 0 when cancelled (UI thread)

        /// <summary>The pointer's look now ("arrow 64 white"), told to each phone as it connects.</summary>
        internal Func<string> CurrentPointer;

        /// <summary>The suggestions' lines (Suggestions.PhoneLines), told to each phone as it connects; from the network thread.</summary>
        internal Func<List<string>> CurrentSuggestions;

        /// <summary>Seconds until the sleep timer ends (0: off), told to each phone as it connects; from the network thread.</summary>
        internal Func<int> CurrentSleepTimer;

        // What the trackpad lines do, straight from the network thread for the least delay. The self-test swaps
        // these to watch rather than move the real pointer.
        internal Action<int, int> Move = Input.MoveBy;
        internal Action<int, bool> Wheel = Input.WheelBy;           // delta, horizontal
        internal Action<bool, bool> MouseButton = Input.Button;     // right, down
        internal Action<int, bool> KeyChange = (vk, down) => Input.KeyChange((uint)vk, down);
        internal Action<int, string> TypeText = Input.Edit;         // backspaces, then text
        internal Action<string, int, char, int, int> Touch = TouchInjector.Touch;   // connection, finger, D|M|U, x, y
        internal Action<string> LiftTouches = TouchInjector.LiftAll;

        readonly Dispatcher _ui;
        readonly PhoneStore _phones;
        readonly int _discoveryPort, _controlPort;
        readonly IPAddress _bind;
        readonly Dictionary<string, RemoteSession> _sessions = new Dictionary<string, RemoteSession>();
        readonly object _pairLock = new object();
        readonly List<DateTime> _pairStarts = new List<DateTime>(), _wrongCodes = new List<DateTime>();
        bool _pairingNow;
        readonly object _typingLock = new object();
        TypingField _typing;          // the text box with the focus on the TV, if any...
        RemoteSession _typingTold;    // ...and the phone told about it
        volatile bool _stop;
        UdpClient _udp;
        TcpListener _tcp;
        int _clients;

        public RemoteServer(Dispatcher ui, PhoneStore phones) : this(ui, phones, DiscoveryPort, ControlPort, IPAddress.Any) { }

        /// <summary>bind: IPAddress.Loopback for the self-test, so Windows doesn't ask about the firewall.</summary>
        public RemoteServer(Dispatcher ui, PhoneStore phones, int discoveryPort, int controlPort, IPAddress bind)
        {
            _ui = ui;
            _phones = phones;
            _discoveryPort = discoveryPort;
            _controlPort = controlPort;
            _bind = bind;
        }

        public int Clients { get { return _clients; } }

        public int PairedPhones { get { return _phones.Count; } }

        public static string TvName { get { return Environment.MachineName; } }

        /// <summary>A paired phone's connection by the id it was given (for its sound requests); null if it's gone.</summary>
        public RemoteSession FindSession(string id)
        {
            RemoteSession session;
            lock (_sessions) return id != null && _sessions.TryGetValue(id, out session) ? session : null;
        }

        /// <summary>Forgets every paired phone (and changes the receiver box's key), and drops their connections.</summary>
        public void ForgetPhones()
        {
            _phones.ForgetAll();
            List<RemoteSession> open;
            lock (_sessions) open = new List<RemoteSession>(_sessions.Values);
            foreach (RemoteSession s in open)
            {
                try { s.Client.Close(); } catch (Exception) { }
            }
            Log.Info("Wi-Fi remote: forgot the paired phones");
        }

        /// <summary>This PC's IPv4 address on the home network (the adapter with a router), for the phone's settings.</summary>
        public static string LocalAddress()
        {
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    System.Net.NetworkInformation.IPInterfaceProperties ip = ni.GetIPProperties();
                    bool hasRouter = false;
                    foreach (System.Net.NetworkInformation.GatewayIPAddressInformation g in ip.GatewayAddresses)
                        if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) hasRouter = true;
                    if (!hasRouter) continue;
                    foreach (System.Net.NetworkInformation.UnicastIPAddressInformation a in ip.UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork) return a.Address.ToString();
                }
            }
            catch (Exception ex) { Log.Info("Local address: " + ex.Message); }
            return null;
        }

        public void Start()
        {
            new Thread(Discovery) { IsBackground = true, Name = "Wi-Fi remote discovery" }.Start();
            new Thread(Accept) { IsBackground = true, Name = "Wi-Fi remote" }.Start();
        }

        public void Stop()
        {
            _stop = true;
            try { if (_udp != null) _udp.Close(); } catch (Exception) { }
            try { if (_tcp != null) _tcp.Stop(); } catch (Exception) { }
        }

        // ---------------------------------------------------------------- finding the TV

        void Discovery()
        {
            while (!_stop)
            {
                try
                {
                    _udp = new UdpClient();
                    _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _udp.Client.Bind(new IPEndPoint(_bind, _discoveryPort));
                    _udp.EnableBroadcast = true;
                    byte[] answer = Encoding.UTF8.GetBytes("COUCHTV 2 " + _controlPort + " " + TvName);
                    while (!_stop)
                    {
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        byte[] data = _udp.Receive(ref from);
                        if (Encoding.ASCII.GetString(data).StartsWith("COUCHTV?")) _udp.Send(answer, answer.Length, from);
                    }
                }
                catch (Exception ex) { if (!_stop) Log.Info("Wi-Fi remote discovery: " + ex.Message); }
                finally { if (_udp != null) _udp.Close(); }
                for (int i = 0; i < 50 && !_stop; i++) Thread.Sleep(100);   // try again in 5 s (e.g. after sleep)
            }
        }

        // ---------------------------------------------------------------- phones

        void Accept()
        {
            while (!_stop)
            {
                try
                {
                    _tcp = new TcpListener(_bind, _controlPort);
                    _tcp.Start();
                    while (!_stop)
                    {
                        TcpClient client = _tcp.AcceptTcpClient();
                        new Thread(() => Serve(client)) { IsBackground = true, Name = "Wi-Fi remote phone" }.Start();
                    }
                }
                catch (Exception ex) { if (!_stop) Log.Info("Wi-Fi remote: " + ex.Message); }
                finally { if (_tcp != null) _tcp.Stop(); }
                for (int i = 0; i < 50 && !_stop; i++) Thread.Sleep(100);
            }
        }

        void Serve(TcpClient client)
        {
            string who = "?";
            var held = new bool[2];   // the left and right mouse buttons this phone holds down
            var keys = new HashSet<int>();   // and the keys
            RemoteSession session = null;
            Channel channel = null;
            try
            {
                who = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                if (Interlocked.Increment(ref _clients) > MostClients) return;
                client.NoDelay = true;
                client.ReceiveTimeout = 5000;   // the phone pings every second; silence means it's gone
                NetworkStream stream = client.GetStream();
                var lines = new LineReader(stream, MostLine);
                string hello = lines.Read();
                if (hello == null) return;
                if (!hello.StartsWith("HELLO2 "))
                {
                    // An app from before pairing: told to update, it keeps using infrared meanwhile.
                    if (hello.StartsWith("HELLO")) Send(stream, "UPDATE");
                    return;
                }
                channel = Handshake(client, stream, lines, hello, who, out session);
                if (channel == null) return;
                session.Stream = stream;
                session.Channel = channel;
                lock (_sessions) _sessions[session.Id] = session;
                string line;
                while (!_stop && (line = lines.Read()) != null)
                {
                    string plain = channel.Open(line);
                    if (plain == null)
                    {
                        Log.Info("Wi-Fi remote " + who + ": a line that wasn't from the paired phone; disconnected");
                        break;
                    }
                    if (plain.StartsWith("MIRROR "))
                    {
                        Mirror(client, stream, session, plain);   // until the phone goes: the connection carries pictures now
                        break;
                    }
                    string reply = Handle(plain.Trim(), who, held, session, keys);
                    if (reply != null)
                        foreach (string r in reply.Split('\n')) session.Send(r);
                }
            }
            catch (IOException) { }   // timed out or dropped: the phone went away
            catch (Exception ex) { Log.Info("Wi-Fi remote " + who + ": " + ex.Message); }
            finally
            {
                Interlocked.Decrement(ref _clients);
                if (session != null)
                {
                    session.Closed = true;
                    LiftTouches(session.Id);
                    lock (_sessions) _sessions.Remove(session.Id);
                    if (session.Trackpad) TellTrackpad();
                    if (session.Remote) Raise(() => { if (PhonesChanged != null) PhonesChanged(Phones()); });
                }
                if (channel != null) lock (session.SendLock) channel.Dispose();
                client.Close();
                // A phone that went away mid-drag mustn't leave a mouse button (or Ctrl, say) stuck down.
                for (int i = 0; i < held.Length; i++) if (held[i]) MouseButton(i == 1, false);
                foreach (int vk in keys) KeyChange(vk, false);
            }
        }

        /// <summary>"MIRROR &lt;width&gt; &lt;kbps&gt;": streams the screen on this connection until the phone goes.</summary>
        void Mirror(TcpClient client, Stream stream, RemoteSession session, string request)
        {
            string[] p = request.Split(' ');
            int width, kbps;
            if (p.Length < 3 || !TryInt(p[1], 4000, out width) || !TryInt(p[2], 20000, out kbps)) return;
            Log.Info("Screen mirror: to " + session.Name + ", " + width + " wide, at most " + kbps + " kbps");
            Raise(() => { if (Mirroring != null) Mirroring(session.Name, true); });
            client.SendTimeout = 10000;
            try { ScreenMirror.Stream(stream, session.MirrorEnc, session.MirrorMac, width, kbps, () => _stop || session.Closed); }
            catch (IOException) { }   // the phone stopped mirroring, or went
            catch (Exception ex) { Log.Info("Screen mirror: " + ex.Message); }
            Log.Info("Screen mirror: " + session.Name + " stopped");
            Raise(() => { if (Mirroring != null) Mirroring(session.Name, false); });
        }

        static void Send(Stream stream, string line)
        {
            byte[] bytes = Secure.Utf8(line + "\n");
            stream.Write(bytes, 0, bytes.Length);
        }

        // ---------------------------------------------------------------- pairing and proving who's who

        /// <summary>
        /// Agrees fresh keys with the phone (ECDH), pairs it first if it's new, and checks it holds its pairing key.
        /// Returns the encrypted channel, or null if it isn't a paired phone (nothing more is said to it).
        /// </summary>
        Channel Handshake(TcpClient client, Stream stream, LineReader lines, string hello, string who, out RemoteSession session)
        {
            session = null;
            // HELLO2 <app> <version> <phone id> <phone's ECDH key>
            string[] p = hello.Split(' ');
            if (p.Length < 5) return null;
            string phoneId = p[3].ToLowerInvariant();
            byte[] id = Secure.FromHex(phoneId), phonePoint = Secure.FromBase64(p[4]);
            if (id == null || id.Length != 16 || phonePoint == null) return null;
            using (var mine = new EcdhKey())
            {
                byte[] shared;
                try { shared = mine.Agree(phonePoint); }
                catch (System.Security.Cryptography.CryptographicException) { return null; }   // not a key on the curve
                PhoneStore.Phone known = _phones.Find(phoneId);
                byte[] tvPoint = mine.PublicPoint;
                string answer = "HELLO2 " + _phones.TvId + " " + Convert.ToBase64String(tvPoint) + " " + (known != null ? "KNOWN" : "PAIR") + " " + TvName;
                Send(stream, answer);
                byte[] transcript = Secure.Sha256(Secure.Utf8(hello + "\n" + answer));

                byte[] pairKey;
                string name;
                if (known != null)
                {
                    pairKey = known.Key;
                    name = known.Name;
                }
                else
                {
                    // A new phone only goes on when you asked it to pair ("PAIR <its name>"); otherwise it was just looking.
                    string ask = lines.Read();
                    if (ask == null || !ask.StartsWith("PAIR ")) return null;
                    name = PhoneName(ask.Substring(5));
                    string code;
                    if (!Pair(client, stream, lines, name, phonePoint, tvPoint, who, out code)) return null;
                    pairKey = SessionKeys.PairKey(shared, code, transcript);
                }

                SessionKeys keys = SessionKeys.Derive(shared, pairKey, transcript);
                string auth = lines.Read();
                if (auth == null || !auth.StartsWith("AUTH ") || !Secure.Same(Secure.FromBase64(auth.Substring(5)), keys.PhoneProof))
                {
                    Log.Info("Wi-Fi remote " + who + ": didn't prove it's " + name + "; disconnected");
                    if (known == null) Ended(name, "failed");
                    return null;
                }
                if (known == null)   // kept before the phone hears it's paired
                {
                    _phones.Add(new PhoneStore.Phone { Id = phoneId, Name = name, Key = pairKey, Paired = DateTime.UtcNow });
                    Log.Info("Wi-Fi remote: paired " + name + " (" + who + ")");
                    Ended(name, "paired");
                }
                Send(stream, "AUTH " + Convert.ToBase64String(keys.TvProof));
                session = new RemoteSession
                {
                    Id = Secure.Hex(Secure.Random(8)), Phone = ((IPEndPoint)client.Client.RemoteEndPoint).Address, Name = name,
                    AudioEnc = keys.AudioEnc, AudioMac = keys.AudioMac, MirrorEnc = keys.MirrorEnc, MirrorMac = keys.MirrorMac, Client = client,
                };
                return new Channel(keys.TvEnc, keys.TvMac, keys.PhoneEnc, keys.PhoneMac);
            }
        }

        /// <summary>
        /// Shows a code on the TV, and checks the phone typed the same one without either side saying it: for each of
        /// its 20 bits, the phone commits to its bit (bound to both keys), the TV commits to its own, then the phone
        /// reveals, then the TV. Someone between the two can't get the code from one side to fool the other: each round
        /// they must guess a bit, and the code's never used again. A wrong code is caught by the TV in the first round
        /// where they differ.
        /// </summary>
        bool Pair(TcpClient client, Stream stream, LineReader lines, string name, byte[] phonePoint, byte[] tvPoint, string who, out string code)
        {
            code = null;
            string refusal = StartPairing();
            if (refusal != null)
            {
                Log.Info("Wi-Fi remote " + who + ": pairing " + name + " refused for now (" + refusal + ")");
                Send(stream, refusal);
                return false;
            }
            int number = Secure.RandomCode();
            string digits = number.ToString("D6");
            string outcome = "cancelled";
            try
            {
                Log.Info("Wi-Fi remote " + who + ": pairing " + name);
                Raise(() => { if (PairingCode != null) PairingCode(name, digits); });
                Send(stream, "CODE");
                client.ReceiveTimeout = 125000;   // while you read the code off the TV and type it
                for (int i = 0; i < CodeBits; i++)
                {
                    byte[] phoneCommit = Field(lines.Read(), "C", i, 32);
                    if (phoneCommit == null) return false;
                    client.ReceiveTimeout = 10000;
                    int bit = (number >> i) & 1;
                    byte[] nonce = Secure.Random(16);
                    Send(stream, "C " + i + " " + Convert.ToBase64String(Secure.Commit(nonce, 'T', tvPoint, phonePoint, bit)));
                    byte[] phoneNonce = Field(lines.Read(), "R", i, 16);
                    if (phoneNonce == null) return false;
                    if (!Secure.Same(Secure.Commit(phoneNonce, 'P', phonePoint, tvPoint, bit), phoneCommit))
                    {
                        Log.Info("Wi-Fi remote " + who + ": wrong pairing code typed on " + name);
                        outcome = null;
                        EndPairing(name, "wrong");   // before the phone hears, so it can try again at once
                        Send(stream, "WRONG");
                        // Let the phone read it before hanging up (it may have sent its next commitment already).
                        client.Client.Shutdown(SocketShutdown.Send);
                        client.ReceiveTimeout = 2000;
                        while (lines.Read() != null) { }
                        return false;
                    }
                    Send(stream, "R " + i + " " + Convert.ToBase64String(nonce));
                }
                client.ReceiveTimeout = 5000;
                outcome = null;
                lock (_pairLock) _pairingNow = false;   // "paired" is said once the phone proves it has the key
                code = digits;
                return true;
            }
            finally
            {
                if (outcome != null) EndPairing(name, outcome);
            }
        }

        void EndPairing(string name, string outcome)
        {
            lock (_pairLock)
            {
                _pairingNow = false;
                if (outcome == "wrong") _wrongCodes.Add(DateTime.UtcNow);
            }
            Ended(name, outcome);
        }

        /// <summary>Null if a pairing may start now; else what to tell the phone: "BUSY", or "WAIT &lt;seconds&gt;".</summary>
        string StartPairing()
        {
            lock (_pairLock)
            {
                DateTime now = DateTime.UtcNow;
                _pairStarts.RemoveAll(t => now - t > PairWindow);
                _wrongCodes.RemoveAll(t => now - t > PairWindow);
                if (_pairingNow) return "BUSY";
                List<DateTime> limit = _wrongCodes.Count >= MostWrongCodes ? _wrongCodes : _pairStarts.Count >= MostPairings ? _pairStarts : null;
                if (limit != null) return "WAIT " + Math.Max(1, (int)Math.Ceiling((limit[0] + PairWindow - now).TotalSeconds));
                _pairingNow = true;
                _pairStarts.Add(now);
                return null;
            }
        }

        void Ended(string name, string outcome)
        {
            Raise(() => { if (PairingEnded != null) PairingEnded(name, outcome); });
        }

        /// <summary>"C|R &lt;round&gt; &lt;base64&gt;" for this round, with that many bytes; else null.</summary>
        static byte[] Field(string line, string kind, int round, int length)
        {
            if (line == null) return null;
            string[] p = line.Split(' ');
            if (p.Length != 3 || p[0] != kind || p[1] != round.ToString()) return null;
            byte[] value = Secure.FromBase64(p[2]);
            return value != null && value.Length == length ? value : null;
        }

        /// <summary>The name a phone gave (base64 UTF-8), tidied for showing on the TV.</summary>
        static string PhoneName(string encoded)
        {
            string name = "";
            byte[] bytes = Secure.FromBase64(encoded);
            if (bytes != null)
            {
                try { name = new UTF8Encoding(false, true).GetString(bytes); }
                catch (ArgumentException) { }
            }
            var tidy = new StringBuilder();
            foreach (char c in name) if (!char.IsControl(c)) tidy.Append(c);
            name = tidy.ToString().Trim();
            if (name.Length > 40) name = name.Substring(0, 40);
            return name.Length > 0 ? name : "a phone";
        }

        // ---------------------------------------------------------------- what a paired phone says

        /// <summary>One line from a phone; returns the answer, if any (lines split by \n). held: the mouse buttons it holds down.</summary>
        internal string Handle(string line, string who, bool[] held = null, RemoteSession session = null, HashSet<int> keys = null)
        {
            string[] parts = line.Split(' ');
            int dx, dy;
            if (session != null && parts[0] != "PING" && parts[0] != "SOUND")
            {
                session.LastUsed = DateTime.UtcNow;
                ScreenMirror.Poke();   // the screen's about to change: a mirror looks at full speed
            }
            switch (parts[0])
            {
                case "HELLO":
                    Log.Info("Wi-Fi remote connected: " + who + (session != null ? ", " + session.Name : "") + " (" + line + ")");
                    Raise(() => { if (Connected != null) Connected(who); });
                    Func<string> pointer = CurrentPointer;
                    string welcome = "WELCOME " + TvName + (pointer != null ? "\nPOINTER " + pointer() : "") +
                                     (session != null ? "\nBOARD " + Secure.Hex(_phones.BoardKey) + "\nAUDIO " + session.Id : "");
                    if (session != null && !session.Remote)
                    {
                        session.Remote = true;
                        Raise(() => { if (PhonesChanged != null) PhonesChanged(Phones()); });
                        lock (_typingLock)
                        {
                            // A text box already has the focus, and no phone knows: this one does now.
                            if (_typing != null && (_typingTold == null || _typingTold.Closed))
                            {
                                _typingTold = session;
                                welcome += "\n" + TypingLine(_typing);
                            }
                        }
                        Func<List<string>> suggestions = CurrentSuggestions;
                        if (suggestions != null) welcome += "\n" + string.Join("\n", suggestions());
                        Func<int> sleep = CurrentSleepTimer;
                        if (sleep != null) welcome += "\nSLEEPTIMER " + sleep();
                    }
                    return welcome;
                case "PING":
                    return "PONG";
                case "SOUND":   // the phone's sound service, on a connection of its own: just its session id
                    return session != null ? "AUDIO " + session.Id : null;
                case "BTN":
                    int address, command;
                    if (parts.Length >= 3 && TryHex(parts[1], out address) && TryHex(parts[2], out command))
                    {
                        var signal = new IrSignal { Code = string.Format("NEC 00{0:X2} 00{1:X2}", address, command), IsRepeat = parts.Length > 3 && parts[3] == "R" };
                        Raise(() => { if (Button != null) Button(signal); });
                    }
                    return null;
                case "TEXT":
                    if (parts.Length >= 2)
                    {
                        try
                        {
                            string words = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])).Trim();
                            if (words.Length > 0 && words.Length <= 200) Raise(() => { if (Text != null) Text(words); });
                        }
                        catch (FormatException) { }
                    }
                    return null;
                case "MOVE":
                    Pointer.Used();   // a phone too old to say TRACKPAD: shown while it moves
                    if (parts.Length >= 3 && TryInt(parts[1], 2000, out dx) && TryInt(parts[2], 2000, out dy)) Move(dx, dy);
                    return null;
                case "WHEEL":
                    Pointer.Used();
                    if (parts.Length >= 2 && TryInt(parts[1], 2400, out dy) && dy != 0) Wheel(dy, false);
                    if (parts.Length >= 3 && TryInt(parts[2], 2400, out dx) && dx != 0) Wheel(dx, true);
                    return null;
                case "TRACKPAD":
                    if (session != null && parts.Length >= 2 && (parts[1] == "ON" || parts[1] == "OFF"))
                    {
                        session.Trackpad = parts[1] == "ON";
                        TellTrackpad();
                    }
                    return null;
                case "POINTER":
                    int size;
                    if (parts.Length >= 4 && int.TryParse(parts[2], out size))
                    {
                        string style = parts[1], colour = parts[3];
                        Raise(() => { if (PointerLook != null) PointerLook(style, size, colour); });
                    }
                    return null;
                case "MOUSE":
                    Pointer.Used();
                    if (parts.Length >= 3 && (parts[1] == "L" || parts[1] == "R") && (parts[2] == "D" || parts[2] == "U"))
                    {
                        bool right = parts[1] == "R", down = parts[2] == "D";
                        if (held != null) held[right ? 1 : 0] = down;
                        MouseButton(right, down);
                    }
                    return null;
                case "TOUCH":
                    int finger, tx, ty;
                    if (session != null && parts.Length >= 5 && int.TryParse(parts[1], out finger) && finger >= 0 && finger < 10 &&
                        (parts[2] == "D" || parts[2] == "M" || parts[2] == "U") && TryInt(parts[3], 65535, out tx) && TryInt(parts[4], 65535, out ty) && tx >= 0 && ty >= 0)
                        Touch(session.Id, finger, parts[2][0], tx, ty);
                    return null;
                case "KEY":
                    int vk;
                    if (parts.Length >= 3 && TryHex(parts[1], out vk) && vk > 0 && vk < 0xFF && (parts[2] == "D" || parts[2] == "U"))
                    {
                        bool down = parts[2] == "D";
                        if (keys != null)
                        {
                            if (down) keys.Add(vk);
                            else keys.Remove(vk);
                        }
                        KeyChange(vk, down);
                    }
                    return null;
                case "POWERSOON":
                    int seconds;
                    if (parts.Length >= 2 && TryInt(parts[1], 60, out seconds) && seconds >= 0)
                        Raise(() => { if (PowerSoon != null) PowerSoon(seconds); });
                    return null;
                case "LOG":
                    // The end of couchtv.log, for looking into the TV from a laptop (CouchTV.exe --tv); paired only.
                    if (session == null) return null;
                    int wanted;
                    if (parts.Length < 2 || !TryInt(parts[1], 2000, out wanted) || wanted <= 0) wanted = 300;
                    List<string> tail = Log.Tail(wanted);
                    Log.Info("Wi-Fi remote: sent the end of the log to " + session.Name);
                    var log = new StringBuilder("LOG " + tail.Count);
                    foreach (string l in tail) log.Append("\n| ").Append(l.Length > 4000 ? l.Substring(0, 4000) : l);
                    return log.ToString();
                case "SLEEPTIMER":
                    int minutes;
                    if (parts.Length >= 2 && TryInt(parts[1], 24 * 60, out minutes) && minutes >= 0)
                        Raise(() => { if (SleepTimer != null) SleepTimer(minutes); });
                    return null;
                case "PLAY":
                    if (parts.Length >= 2 && parts[1].Length > 0 && parts[1].Length <= 60 && line.Length <= 4000)
                    {
                        string request = line.Substring(5);
                        Raise(() => { if (Play != null) Play(request); });
                    }
                    return null;
                case "EDIT":
                    int backspaces;
                    if (parts.Length >= 2 && TryInt(parts[1], 1000, out backspaces) && backspaces >= 0)
                    {
                        string typed = "";
                        if (parts.Length >= 3)
                        {
                            try { typed = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(parts[2])); }
                            catch (FormatException) { return null; }
                            catch (ArgumentException) { return null; }
                        }
                        if (typed.Length <= 2000) TypeText(backspaces, typed);
                    }
                    return null;
                default:
                    return null;
            }
        }

        int Phones()
        {
            int count = 0;
            lock (_sessions)
                foreach (RemoteSession s in _sessions.Values)
                    if (s.Remote && !s.Closed) count++;
            return count;
        }

        /// <summary>
        /// A text box got the focus on the TV (or none has it now: null): tells the phone used last, so it pops up its
        /// keyboard (and tells one that was told before that it's gone). From any thread.
        /// </summary>
        public void ShowTyping(TypingField field)
        {
            lock (_typingLock)
            {
                _typing = field;
                RemoteSession target = null;
                if (field != null)
                    lock (_sessions)
                        foreach (RemoteSession s in _sessions.Values)
                            if (s.Remote && !s.Closed && (target == null || s.LastUsed > target.LastUsed)) target = s;
                if (_typingTold != null && _typingTold != target && !_typingTold.Closed) TrySend(_typingTold, "INPUT OFF");
                _typingTold = target;
                if (target != null) TrySend(target, TypingLine(field));
            }
        }

        /// <summary>Whether any connected phone has its trackpad page open, to the UI thread.</summary>
        void TellTrackpad()
        {
            bool open = false;
            lock (_sessions)
                foreach (RemoteSession s in _sessions.Values)
                    if (s.Trackpad && !s.Closed) open = true;
            Raise(() => { if (Trackpad != null) Trackpad(open); });
        }

        /// <summary>Tells every connected phone remote these lines (a new list of suggestions); from any thread.</summary>
        public void SendToPhones(List<string> lines)
        {
            ThreadPool.QueueUserWorkItem(state =>
            {
                List<RemoteSession> open;
                lock (_sessions) open = new List<RemoteSession>(_sessions.Values);
                foreach (RemoteSession s in open)
                {
                    if (!s.Remote || s.Closed) continue;
                    try { foreach (string line in lines) s.Send(line); }
                    catch (Exception) { }   // it's going away; its own thread tidies up
                }
            });
        }

        static string TypingLine(TypingField field)
        {
            return "INPUT " + (field.Password ? "PASSWORD " : "TEXT ") + Convert.ToBase64String(Secure.Utf8(field.Name)) + " " +
                   Convert.ToBase64String(Secure.Utf8(field.Value));
        }

        static void TrySend(RemoteSession session, string line)
        {
            try { session.Send(line); }
            catch (Exception) { }   // it's going away; its own thread tidies up
        }

        static bool TryInt(string s, int most, out int value)
        {
            return int.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out value)
                && value >= -most && value <= most;
        }

        static bool TryHex(string s, out int value)
        {
            return int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out value) && value >= 0 && value <= 0xFF;
        }

        void Raise(Action action)
        {
            if (_ui == null) action();
            else _ui.BeginInvoke(action);
        }

        // ---------------------------------------------------------------- Windows Firewall

        /// <summary>Whether the firewall rule that lets phones reach CouchTV is there (readable without admin rights).</summary>
        public static bool FirewallRuleExists()
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", "advfirewall firewall show rule name=\"" + RuleName + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                };
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Log.Info("Checking the firewall: " + ex.Message);
                return true;   // can't tell: don't nag
            }
        }

        /// <summary>
        /// Adds the rule: CouchTV.exe may receive connections, only from the local network. Windows asks for
        /// permission (UAC). Returns false if that was declined or failed.
        /// </summary>
        public static bool AddFirewallRule()
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            var psi = new ProcessStartInfo("netsh", "advfirewall firewall add rule name=\"" + RuleName + "\" dir=in action=allow program=\"" + exe +
                                                     "\" remoteip=localsubnet profile=any enable=yes")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            };
            try
            {
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(20000);
                    Log.Info("Firewall rule for the Wi-Fi remote: exit " + p.ExitCode);
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Log.Info("Firewall rule not added: " + ex.Message);   // e.g. "The operation was canceled by the user"
                return false;
            }
        }
    }

    /// <summary>A paired phone's connection: what its sound requests are checked with, and where the sound goes.</summary>
    internal sealed class RemoteSession
    {
        public string Id;              // 16 hex digits, told to the phone for its sound requests
        public IPAddress Phone;
        public string Name;
        public byte[] AudioEnc, AudioMac;
        public byte[] MirrorEnc, MirrorMac;
        public long LastListen;        // the number of its last sound request: each must be higher (no replays)
        public volatile bool Closed;
        public volatile bool Remote;   // said HELLO: the remote itself (not the phone's sound service)
        public volatile bool Trackpad; // its trackpad page is open
        public DateTime LastUsed;      // when it last sent a button, words, a pointer move... (to tell it about text boxes)
        internal TcpClient Client;
        internal Stream Stream;
        internal Channel Channel;
        internal readonly object SendLock = new object();

        /// <summary>Encrypts and sends one line; from any thread (one at a time, in order).</summary>
        internal void Send(string line)
        {
            lock (SendLock)
            {
                byte[] bytes = Secure.Utf8(Channel.Seal(line) + "\n");
                Stream.Write(bytes, 0, bytes.Length);
            }
        }
    }
}
