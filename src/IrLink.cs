using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CouchTV
{
    /// <summary>One button press reported by the receiver.</summary>
    internal sealed class IrSignal
    {
        public string Code;      // e.g. "NEC 00CE 0001": the same every time that button is pressed
        public bool IsRepeat;    // the button is being held down
    }

    /// <summary>
    /// Talks to the CouchIR receiver, a USB serial device (see ir-receiver/). Finds it among the USB serial ports,
    /// reads button codes on a background thread, hands them to the UI thread, and reconnects after the receiver is
    /// unplugged or the PC sleeps. Uses Win32 directly: System.IO.Ports can crash the whole process when a USB serial
    /// device disappears, and CouchTV is the shell.
    /// </summary>
    internal sealed class IrLink
    {
        public event Action<IrSignal> Signal;     // raised on the UI thread
        public event Action StatusChanged;        // raised on the UI thread

        readonly Dispatcher _ui;
        readonly string _fixedPort;
        readonly ConcurrentQueue<string> _outbox = new ConcurrentQueue<string>();
        volatile string _port;
        volatile bool _stop;

        public IrLink(Dispatcher ui, string fixedPort)
        {
            _ui = ui;
            _fixedPort = fixedPort;
        }

        /// <summary>The COM port the receiver is on, or null when it isn't connected.</summary>
        public string Port { get { return _port; } }

        public void Start()
        {
            new Thread(Run) { IsBackground = true, Name = "IR receiver" }.Start();
        }

        public void Stop() { _stop = true; }

        /// <summary>Queues a command line for the receiver (sent once connected).</summary>
        public void Send(string line) { _outbox.Enqueue(line); }

        /// <summary>FNV-1a hash of the upper-cased code: how the receiver stores its wake-up buttons.</summary>
        public static uint Hash(string code)
        {
            uint hash = 2166136261;
            foreach (char c in code.ToUpperInvariant())
            {
                hash ^= (byte)c;
                hash *= 16777619;
            }
            return hash;
        }

        /// <summary>Looks for the receiver once, without keeping it open (for the self-test).</summary>
        public static string Probe(string fixedPort)
        {
            foreach (string port in Candidates(fixedPort))
            {
                using (SafeFileHandle handle = Open(port))
                    if (handle != null && Handshake(handle, new StringBuilder())) return port;
            }
            return null;
        }

        // ---------------------------------------------------------------- background thread

        void Run()
        {
            while (!_stop)
            {
                try
                {
                    foreach (string port in Candidates(_fixedPort))
                    {
                        if (_stop) return;
                        using (SafeFileHandle handle = Open(port))
                        {
                            var pending = new StringBuilder();
                            if (handle == null || !Handshake(handle, pending)) continue;
                            SetPort(port);
                            Pump(handle, pending);
                            SetPort(null);
                        }
                    }
                }
                catch (Exception ex) { Log.Error("IR receiver", ex); }
                for (int i = 0; i < 30 && !_stop; i++) Thread.Sleep(100);   // look again in 3 s
            }
        }

        /// <summary>Reads lines until the receiver goes away.</summary>
        void Pump(SafeFileHandle handle, StringBuilder pending)
        {
            var buffer = new byte[256];
            DateTime lastHeard = DateTime.Now, lastPing = DateTime.Now;
            while (!_stop)
            {
                string line;
                while (_outbox.TryDequeue(out line))
                    if (!Write(handle, line)) return;

                if ((DateTime.Now - lastPing).TotalSeconds > 15)
                {
                    lastPing = DateTime.Now;
                    if (!Write(handle, "?")) return;
                }
                if ((DateTime.Now - lastHeard).TotalSeconds > 25) return;   // it stopped answering

                uint read;
                if (!ReadFile(handle, buffer, (uint)buffer.Length, out read, IntPtr.Zero)) return;
                if (read == 0) continue;
                lastHeard = DateTime.Now;
                pending.Append(Encoding.ASCII.GetString(buffer, 0, (int)read));
                foreach (string received in TakeLines(pending)) Handle(received);
            }
        }

        /// <summary>Parses "IR,&lt;code&gt;,&lt;N|R&gt;" from the receiver; null for anything else.</summary>
        public static IrSignal ParseLine(string line)
        {
            if (!line.StartsWith("IR,")) return null;
            int last = line.LastIndexOf(',');
            if (last <= 3) return null;
            var signal = new IrSignal { Code = line.Substring(3, last - 3).Trim(), IsRepeat = line.Substring(last + 1).Trim() == "R" };
            return signal.Code.Length == 0 ? null : signal;
        }

        void Handle(string line)
        {
            IrSignal signal = ParseLine(line);
            if (signal == null) return;
            _ui.BeginInvoke(new Action(() =>
            {
                Action<IrSignal> handler = Signal;
                if (handler != null) handler(signal);
            }));
        }

        void SetPort(string port)
        {
            _port = port;
            Log.Info(port == null ? "IR receiver disconnected" : "IR receiver connected on " + port);
            _ui.BeginInvoke(new Action(() =>
            {
                Action handler = StatusChanged;
                if (handler != null) handler();
            }));
        }

        // ---------------------------------------------------------------- serial port plumbing

        /// <summary>
        /// USB serial ports (usbser.sys, which Arduino-style boards use) from HARDWARE\DEVICEMAP\SERIALCOMM. That list
        /// only has devices that are plugged in, and leaves out Bluetooth serial ports, which can hang when opened.
        /// </summary>
        static List<string> Candidates(string fixedPort)
        {
            var ports = new List<string>();
            if (!string.IsNullOrEmpty(fixedPort))
            {
                ports.Add(fixedPort);
                return ports;
            }
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM"))
                {
                    if (key == null) return ports;
                    foreach (string name in key.GetValueNames())
                    {
                        string port = key.GetValue(name) as string;
                        if (port != null && name.IndexOf("USBSER", StringComparison.OrdinalIgnoreCase) >= 0) ports.Add(port);
                    }
                }
            }
            catch (Exception ex) { Log.Error("Listing serial ports", ex); }
            return ports;
        }

        static SafeFileHandle Open(string port)
        {
            SafeFileHandle handle = CreateFile(@"\\.\" + port, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }
            var dcb = new DCB();
            dcb.DCBlength = (uint)Marshal.SizeOf(typeof(DCB));
            GetCommState(handle, ref dcb);
            // 115200 8N1. Never 1200 baud: on these boards that means "reboot into the bootloader".
            dcb.BaudRate = 115200;
            dcb.ByteSize = 8;
            dcb.Parity = 0;
            dcb.StopBits = 0;
            dcb.Flags = 0x0001 | (1 << 4) | (1 << 12);   // binary mode, DTR on, RTS on
            var timeouts = new COMMTIMEOUTS
            {
                ReadIntervalTimeout = uint.MaxValue,          // these three together: return as soon as
                ReadTotalTimeoutMultiplier = uint.MaxValue,   // anything arrives, or after 500 ms
                ReadTotalTimeoutConstant = 500,
                WriteTotalTimeoutConstant = 500,
            };
            if (!SetCommState(handle, ref dcb) || !SetCommTimeouts(handle, ref timeouts))
            {
                handle.Dispose();
                return null;
            }
            EscapeCommFunction(handle, SETDTR);   // the board only sends while the PC holds DTR
            return handle;
        }

        /// <summary>Sends "?" and waits up to 2 s for "COUCHIR". Other serial devices just ignore it.</summary>
        static bool Handshake(SafeFileHandle handle, StringBuilder pending)
        {
            PurgeComm(handle, PURGE_RXCLEAR | PURGE_TXCLEAR);
            if (!Write(handle, "?")) return false;
            var buffer = new byte[256];
            DateTime until = DateTime.Now.AddSeconds(2);
            while (DateTime.Now < until)
            {
                uint read;
                if (!ReadFile(handle, buffer, (uint)buffer.Length, out read, IntPtr.Zero)) return false;
                pending.Append(Encoding.ASCII.GetString(buffer, 0, (int)read));
                foreach (string line in TakeLines(pending))
                    if (line.StartsWith("COUCHIR")) return true;
            }
            return false;
        }

        static IEnumerable<string> TakeLines(StringBuilder pending)
        {
            var lines = new List<string>();
            string text = pending.ToString();
            int start = 0, newline;
            while ((newline = text.IndexOf('\n', start)) >= 0)
            {
                lines.Add(text.Substring(start, newline - start).Trim());
                start = newline + 1;
            }
            pending.Remove(0, start);
            if (pending.Length > 4096) pending.Clear();   // garbage without newlines
            return lines;
        }

        static bool Write(SafeFileHandle handle, string line)
        {
            byte[] data = Encoding.ASCII.GetBytes(line + "\n");
            uint written;
            return WriteFile(handle, data, (uint)data.Length, out written, IntPtr.Zero) && written == data.Length;
        }

        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, OPEN_EXISTING = 3;
        const uint SETDTR = 5, PURGE_TXCLEAR = 0x4, PURGE_RXCLEAR = 0x8;

        [StructLayout(LayoutKind.Sequential)]
        struct DCB
        {
            public uint DCBlength, BaudRate, Flags;
            public ushort wReserved, XonLim, XoffLim;
            public byte ByteSize, Parity, StopBits;
            public sbyte XonChar, XoffChar, ErrorChar, EofChar, EvtChar;
            public ushort wReserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct COMMTIMEOUTS
        {
            public uint ReadIntervalTimeout, ReadTotalTimeoutMultiplier, ReadTotalTimeoutConstant, WriteTotalTimeoutMultiplier, WriteTotalTimeoutConstant;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetCommState(SafeFileHandle handle, ref DCB dcb);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetCommState(SafeFileHandle handle, ref DCB dcb);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetCommTimeouts(SafeFileHandle handle, ref COMMTIMEOUTS timeouts);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool EscapeCommFunction(SafeFileHandle handle, uint function);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool PurgeComm(SafeFileHandle handle, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, uint count, out uint read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, uint count, out uint written, IntPtr overlapped);
    }
}
