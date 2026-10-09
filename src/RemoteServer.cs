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
    /// The phone remote over Wi-Fi, for when pointing is a bother or the phone has no IR blaster. See "Wi-Fi" in
    /// ir-receiver/PROTOCOL.md.
    ///   Finding the TV: the phone broadcasts "COUCHTV?" to UDP port 47700; this answers "COUCHTV 1 47701 &lt;name&gt;".
    ///   Then a TCP connection to port 47701 carries lines of text:
    ///     HELLO &lt;app&gt; &lt;version&gt;  →  WELCOME &lt;name&gt;
    ///     PING                     →  PONG          (every second, so both sides notice a dead link quickly)
    ///     BTN &lt;address&gt; &lt;command&gt; N|R   a button, exactly like the IR receiver's "NEC 00CE 00xx" codes
    ///     TEXT &lt;base64 UTF-8&gt;       words for search (voice search)
    /// Buttons go through the same remote.ini mapping as infrared ones, so both remotes behave the same.
    /// </summary>
    internal sealed class RemoteServer
    {
        public const int DiscoveryPort = 47700, ControlPort = 47701;
        public const string RuleName = "CouchTV phone remote";

        public event Action<IrSignal> Button;      // raised on the UI thread
        public event Action<string> Text;          // raised on the UI thread
        public event Action<string> Connected;     // a phone connected (its address), on the UI thread

        readonly Dispatcher _ui;
        readonly int _discoveryPort, _controlPort;
        readonly IPAddress _bind;
        volatile bool _stop;
        UdpClient _udp;
        TcpListener _tcp;
        int _clients;

        public RemoteServer(Dispatcher ui) : this(ui, DiscoveryPort, ControlPort, IPAddress.Any) { }

        /// <summary>bind: IPAddress.Loopback for the self-test, so Windows doesn't ask about the firewall.</summary>
        public RemoteServer(Dispatcher ui, int discoveryPort, int controlPort, IPAddress bind)
        {
            _ui = ui;
            _discoveryPort = discoveryPort;
            _controlPort = controlPort;
            _bind = bind;
        }

        public int Clients { get { return _clients; } }

        public static string TvName { get { return Environment.MachineName; } }

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
                    byte[] answer = Encoding.UTF8.GetBytes("COUCHTV 1 " + _controlPort + " " + TvName);
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
            try
            {
                who = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                client.NoDelay = true;
                client.ReceiveTimeout = 5000;   // the phone pings every second; silence means it's gone
                using (NetworkStream stream = client.GetStream())
                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true })
                {
                    Interlocked.Increment(ref _clients);
                    string line;
                    while (!_stop && (line = reader.ReadLine()) != null)
                    {
                        string reply = Handle(line.Trim(), who);
                        if (reply != null) writer.WriteLine(reply);
                    }
                }
            }
            catch (IOException) { }   // timed out or dropped: the phone went away
            catch (Exception ex) { Log.Info("Wi-Fi remote " + who + ": " + ex.Message); }
            finally
            {
                Interlocked.Decrement(ref _clients);
                client.Close();
            }
        }

        /// <summary>One line from a phone; returns the answer, if any.</summary>
        internal string Handle(string line, string who)
        {
            string[] parts = line.Split(' ');
            switch (parts[0])
            {
                case "HELLO":
                    Log.Info("Wi-Fi remote connected: " + who + " (" + line + ")");
                    Raise(() => { if (Connected != null) Connected(who); });
                    return "WELCOME " + TvName;
                case "PING":
                    return "PONG";
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
                default:
                    return null;
            }
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
}
