using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CouchTV
{
    /// <summary>
    /// CouchTV.exe --tv &lt;report&gt; &lt;command&gt;...: this PC as a remote for the TV PC, to test and look into it from a
    /// laptop the way the phone does: paired once (the TV shows a code), then everything encrypted, and only what a
    /// paired phone may do anyway, plus reading the TV's log. The commands run in order, on one connection:
    ///   pair &lt;address&gt;                   pair with the TV there: it shows a code, which goes in tvlink-code.txt next to the report
    ///   log [lines]                         the TV's log (its last lines) into the report
    ///   screen &lt;file.png&gt;                 the TV's screen now, as the phone's mirror gets it
    ///   tap &lt;x&gt; &lt;y&gt;                       a finger on the TV's screen, as the mirror sends it (x, y: 0-65535 across and down)
    ///   swipe &lt;x1&gt; &lt;y1&gt; &lt;x2&gt; &lt;y2&gt; [ms]     a finger sliding, in 16 ms steps as the phone sends them (300 ms if not said)
    ///   btn &lt;address&gt; &lt;command&gt;          a remote button (hex), e.g. "btn CE 05"
    ///   key &lt;vk&gt;                          a PC key pressed and let go (Windows' virtual-key code, hex), e.g. "key 75" for F6
    ///   send &lt;line&gt;                       any line, as the phone would send it (e.g. "send 'TOUCH 0 D 100 200'")
    ///   wait &lt;ms&gt;
    /// The report says how each went, line by line as it happens. The pairing (this PC's id, the TV's address and the
    /// key) is kept in %LOCALAPPDATA%\CouchTV\tvlink.dat, readable only by this Windows user.
    /// </summary>
    internal static class TvLink
    {
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CouchTV tvlink 1");
        static string _report;

        static string StorePath { get { return Path.Combine(Config.DataDir, "tvlink.dat"); } }

        public static int Run(string[] args, int start)
        {
            _report = args[start];
            File.WriteAllText(_report, "");
            string id, address;
            byte[] key;
            Load(out id, out address, out key);
            Diagnostics.TestPhone link = null, mirror = null;
            var canvasLock = new object();
            Bitmap canvas = null;
            int pictures = 0;
            var writeLock = new object();
            bool done = false;
            try
            {
                for (int i = start + 1; i < args.Length; i++)
                {
                    string command = args[i];
                    if (command == "pair")
                    {
                        address = args[++i];
                        id = Secure.Hex(Secure.Random(16));
                        key = Pair(id, address);
                        if (key == null) return 1;
                        Save(id, address, key);
                        continue;
                    }
                    if (key == null) throw new Exception("not paired with a TV yet: pair <address> first");
                    if (link == null)
                    {
                        link = Connect(id, address, key);
                        link.Send("HELLO CouchTV-DevTool 1");
                        Diagnostics.TestPhone pinged = link;
                        // The TV drops a phone that's quiet for 5 s: a ping every second meanwhile.
                        new Thread(() =>
                        {
                            while (!done)
                            {
                                Thread.Sleep(1000);
                                try { lock (writeLock) pinged.Send("PING"); }
                                catch (Exception) { return; }
                            }
                        }) { IsBackground = true }.Start();
                    }
                    switch (command)
                    {
                        case "log":
                        {
                            int lines;
                            if (i + 1 < args.Length && int.TryParse(args[i + 1], out lines)) i++;
                            else lines = 300;
                            lock (writeLock) link.Send("LOG " + lines);
                            string head;
                            while ((head = link.Receive()) != null && !head.StartsWith("LOG ")) { }
                            if (head == null) throw new Exception("the TV went away");
                            int count = int.Parse(head.Substring(4));
                            var text = new StringBuilder();
                            for (int n = 0; n < count; n++)
                            {
                                string line = link.Receive();
                                if (line == null) break;
                                if (line.StartsWith("| ")) text.AppendLine(line.Substring(2));
                                else n--;   // a PONG or the like, in between
                            }
                            Say("The TV's log, its last " + count + " lines:");
                            Say(text.ToString().TrimEnd());
                            break;
                        }
                        case "screen":
                        {
                            string file = args[++i];
                            if (mirror == null)
                            {
                                mirror = Connect(id, address, key);
                                mirror.Send("MIRROR 1600 8000");
                                var frames = new Diagnostics.FrameReader(mirror.Stream, mirror.Keys.MirrorEnc, mirror.Keys.MirrorMac);
                                new Thread(() =>
                                {
                                    Graphics draw = null;
                                    byte[] message;
                                    while (!done && (message = frames.Next()) != null)
                                    {
                                        lock (canvasLock)
                                        {
                                            if (message[0] == 1)
                                            {
                                                canvas = new Bitmap(message[1] << 8 | message[2], message[3] << 8 | message[4]);
                                                draw = Graphics.FromImage(canvas);
                                            }
                                            else if (message[0] == 2 && draw != null)
                                            {
                                                using (Image tile = Image.FromStream(new MemoryStream(message, 9, message.Length - 9)))
                                                    draw.DrawImage(tile, message[1] << 8 | message[2], message[3] << 8 | message[4], tile.Width, tile.Height);
                                            }
                                            else if (message[0] == 3) pictures++;
                                        }
                                    }
                                }) { IsBackground = true }.Start();
                            }
                            // The next whole picture from now (the screen as it is after whatever came before).
                            int seen;
                            lock (canvasLock) seen = pictures;
                            var clock = System.Diagnostics.Stopwatch.StartNew();
                            while (clock.ElapsedMilliseconds < 4000)
                            {
                                Thread.Sleep(50);
                                lock (canvasLock) if (pictures > seen || (canvas != null && clock.ElapsedMilliseconds > 1500)) break;
                            }
                            lock (canvasLock)
                            {
                                if (canvas == null) throw new Exception("no picture of the TV's screen came");
                                canvas.Save(file, ImageFormat.Png);
                                Say("The TV's screen (" + canvas.Width + "x" + canvas.Height + ") is in " + file);
                            }
                            break;
                        }
                        case "tap":
                        {
                            int x = int.Parse(args[++i]), y = int.Parse(args[++i]);
                            lock (writeLock) link.Send("TOUCH 0 D " + x + " " + y);
                            Thread.Sleep(60);
                            lock (writeLock) link.Send("TOUCH 0 U " + x + " " + y);
                            Say("Tapped at " + x + "," + y);
                            break;
                        }
                        case "swipe":
                        {
                            int x1 = int.Parse(args[++i]), y1 = int.Parse(args[++i]), x2 = int.Parse(args[++i]), y2 = int.Parse(args[++i]);
                            int ms = 300;
                            if (i + 1 < args.Length && int.TryParse(args[i + 1], out ms)) i++;
                            else ms = 300;
                            int steps = Math.Max(1, ms / 16);
                            lock (writeLock) link.Send("TOUCH 0 D " + x1 + " " + y1);
                            for (int s = 1; s <= steps; s++)
                            {
                                Thread.Sleep(16);
                                lock (writeLock) link.Send("TOUCH 0 M " + (x1 + (x2 - x1) * s / steps) + " " + (y1 + (y2 - y1) * s / steps));
                            }
                            lock (writeLock) link.Send("TOUCH 0 U " + x2 + " " + y2);
                            Say("Swiped from " + x1 + "," + y1 + " to " + x2 + "," + y2 + " in " + ms + " ms");
                            break;
                        }
                        case "btn":
                            lock (writeLock) link.Send("BTN " + args[++i] + " " + args[++i] + " N");
                            Say("Pressed " + args[i - 1] + " " + args[i]);
                            break;
                        case "key":
                            lock (writeLock) link.Send("KEY " + args[++i] + " D");
                            Thread.Sleep(50);
                            lock (writeLock) link.Send("KEY " + args[i] + " U");
                            Say("Pressed key " + args[i]);
                            break;
                        case "send":
                            lock (writeLock) link.Send(args[++i]);
                            Say("Sent " + args[i]);
                            break;
                        case "wait":
                            Thread.Sleep(int.Parse(args[++i]));
                            break;
                        default:
                            throw new Exception("don't know \"" + command + "\"");
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Say("FAILED: " + ex.Message);
                return 1;
            }
            finally
            {
                done = true;
                if (link != null) link.Dispose();
                if (mirror != null) mirror.Dispose();
            }
        }

        /// <summary>Pairs with the TV at that address: its key, or null.</summary>
        static byte[] Pair(string id, string address)
        {
            string codeFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_report)), "tvlink-code.txt");
            File.Delete(codeFile);
            using (var phone = new Diagnostics.TestPhone(id))
            try
            {
                string hello = phone.Hello(IPAddress.Parse(address), RemoteServer.ControlPort);
                if (hello != "PAIR")
                {
                    Say("FAILED: the TV at " + address + " said " + (hello ?? "nothing"));
                    return null;
                }
                Say("Asking the TV to show a code: put it in " + codeFile);
                string result = phone.Pair("CouchTV test tool on " + Environment.MachineName, () =>
                {
                    Say("The TV shows the code now");
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (clock.Elapsed < TimeSpan.FromSeconds(115))
                    {
                        if (File.Exists(codeFile))
                        {
                            string code = File.ReadAllText(codeFile).Trim();
                            if (code.Length == 6) return code;
                        }
                        Thread.Sleep(250);
                    }
                    throw new TimeoutException("no code came in time, so nothing was typed (the TV doesn't count it as a wrong code)");
                });
                File.Delete(codeFile);
                if (result != "OK" || !phone.Auth(phone.PairKey))
                {
                    Say("FAILED: pairing didn't work (" + result + ")");
                    return null;
                }
                Say("Paired with the TV at " + address);
                return phone.PairKey;
            }
            catch (TimeoutException ex)
            {
                Say("FAILED: " + ex.Message);
                return null;
            }
        }

        static Diagnostics.TestPhone Connect(string id, string address, byte[] key)
        {
            var phone = new Diagnostics.TestPhone(id);
            string hello = phone.Hello(IPAddress.Parse(address), RemoteServer.ControlPort);
            if (hello != "KNOWN" || !phone.Auth(key))
            {
                phone.Dispose();
                throw new Exception("the TV at " + address + " doesn't know this PC any more (" + (hello ?? "no answer") + "): pair again");
            }
            return phone;
        }

        static void Say(string line)
        {
            File.AppendAllText(_report, line + Environment.NewLine);
        }

        static void Load(out string id, out string address, out byte[] key)
        {
            id = address = null;
            key = null;
            try
            {
                if (!File.Exists(StorePath)) return;
                string[] p = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(StorePath), Entropy, DataProtectionScope.CurrentUser)).Split('\n');
                id = p[0];
                address = p[1];
                key = Secure.FromHex(p[2]);
            }
            catch (Exception ex) { Say("(the saved pairing couldn't be read: " + ex.Message + ")"); }
        }

        static void Save(string id, string address, byte[] key)
        {
            byte[] plain = Encoding.UTF8.GetBytes(id + "\n" + address + "\n" + Secure.Hex(key));
            File.WriteAllBytes(StorePath, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        }
    }
}
