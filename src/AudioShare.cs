using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CouchTV
{
    /// <summary>
    /// Plays the TV's sound on phones too, each into its own headphones ("Listen on this phone" in the remote app).
    /// Captures what Windows plays (WASAPI loopback, so it works with any app) and sends it to the phones on the
    /// Wi-Fi as uncompressed 16-bit stereo in 5 ms UDP packets: no encoding delay, and small enough to never split.
    ///   A phone sends "LISTEN" to UDP port 47702 every second while it wants sound, and "STOP" when it's done.
    ///   Each packet: "CTA1", sequence (uint32), sample rate (uint32), channels (byte, 2), 3 zero bytes, then samples.
    /// Capturing only runs while someone is listening.
    /// </summary>
    internal sealed class AudioShare
    {
        public const int Port = 47702;
        const int HeaderBytes = 16;

        public event Action<int> ListenersChanged;   // on a background thread

        readonly int _port;
        readonly IPAddress _bind;
        readonly Dictionary<string, KeyValuePair<IPEndPoint, DateTime>> _listeners = new Dictionary<string, KeyValuePair<IPEndPoint, DateTime>>();
        UdpClient _udp;
        volatile bool _stop;
        Thread _capture;

        public AudioShare() : this(Port, IPAddress.Any) { }

        public AudioShare(int port, IPAddress bind)
        {
            _port = port;
            _bind = bind;
        }

        public int Listeners { get { lock (_listeners) return _listeners.Count; } }

        public void Start()
        {
            new Thread(Receive) { IsBackground = true, Name = "Phone audio listeners" }.Start();
        }

        public void Stop()
        {
            _stop = true;
            try { if (_udp != null) _udp.Close(); } catch (Exception) { }
        }

        // ---------------------------------------------------------------- phones asking for sound

        void Receive()
        {
            while (!_stop)
            {
                try
                {
                    _udp = new UdpClient(new IPEndPoint(_bind, _port));
                    _udp.Client.ReceiveTimeout = 1000;
                    while (!_stop)
                    {
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        byte[] data;
                        try { data = _udp.Receive(ref from); }
                        catch (SocketException ex)
                        {
                            if (ex.SocketErrorCode == SocketError.TimedOut || ex.SocketErrorCode == SocketError.ConnectionReset) { Expire(); continue; }
                            throw;
                        }
                        string message = Encoding.ASCII.GetString(data).Trim();
                        if (message.StartsWith("LISTEN")) Add(from);
                        else if (message.StartsWith("STOP")) Remove(from);
                        Expire();
                    }
                }
                catch (Exception ex) { if (!_stop) Log.Info("Phone audio: " + ex.Message); }
                finally { if (_udp != null) _udp.Close(); }
                for (int i = 0; i < 50 && !_stop; i++) Thread.Sleep(100);
            }
        }

        void Add(IPEndPoint phone)
        {
            bool added;
            lock (_listeners)
            {
                added = !_listeners.ContainsKey(phone.ToString());
                _listeners[phone.ToString()] = new KeyValuePair<IPEndPoint, DateTime>(phone, DateTime.UtcNow);
                if (_capture == null || !_capture.IsAlive)
                {
                    _capture = new Thread(Capture) { IsBackground = true, Name = "Phone audio capture", Priority = ThreadPriority.Highest };
                    _capture.Start();
                }
            }
            if (added)
            {
                Log.Info("Phone audio: " + phone + " listening");
                Changed();
            }
        }

        void Remove(IPEndPoint phone)
        {
            bool removed;
            lock (_listeners) removed = _listeners.Remove(phone.ToString());
            if (removed)
            {
                Log.Info("Phone audio: " + phone + " stopped");
                Changed();
            }
        }

        /// <summary>Phones that stopped asking (switched off, walked away) for 3 seconds are dropped.</summary>
        void Expire()
        {
            var gone = new List<string>();
            lock (_listeners)
            {
                foreach (KeyValuePair<string, KeyValuePair<IPEndPoint, DateTime>> pair in _listeners)
                    if ((DateTime.UtcNow - pair.Value.Value).TotalSeconds > 3) gone.Add(pair.Key);
                foreach (string key in gone) _listeners.Remove(key);
            }
            if (gone.Count > 0) Changed();
        }

        void Changed()
        {
            Action<int> handler = ListenersChanged;
            if (handler != null) handler(Listeners);
        }

        List<IPEndPoint> Targets()
        {
            var targets = new List<IPEndPoint>();
            lock (_listeners) foreach (KeyValuePair<IPEndPoint, DateTime> v in _listeners.Values) targets.Add(v.Key);
            return targets;
        }

        // ---------------------------------------------------------------- capturing and sending

        /// <summary>Runs while anyone listens; starts again after the sound device changes (e.g. HDMI to speakers).</summary>
        void Capture()
        {
            while (!_stop && Listeners > 0)
            {
                try { CaptureUntilDeviceChanges(); }
                catch (Exception ex) { Log.Info("Phone audio capture: " + ex.Message); }
                if (!_stop && Listeners > 0) Thread.Sleep(500);
            }
        }

        void CaptureUntilDeviceChanges()
        {
            using (var loopback = new Loopback())
            {
                Log.Info("Phone audio: capturing " + loopback.Description);
                int perPacket = loopback.SampleRate / 200;   // 5 ms
                var pcm = new List<short>(perPacket * 4);
                var packet = new byte[HeaderBytes + perPacket * 4];
                Encoding.ASCII.GetBytes("CTA1", 0, 4, packet, 0);
                BitConverter.GetBytes((uint)loopback.SampleRate).CopyTo(packet, 8);
                packet[12] = 2;
                uint sequence = 0;
                var sender = new UdpClient(new IPEndPoint(_bind, 0));
                try
                {
                    while (!_stop && Listeners > 0)
                    {
                        if (!loopback.Read(pcm))   // nothing new yet
                        {
                            Thread.Sleep(2);
                            continue;
                        }
                        int offset = 0;
                        while (pcm.Count - offset >= perPacket * 2)
                        {
                            BitConverter.GetBytes(sequence++).CopyTo(packet, 4);
                            for (int i = 0; i < perPacket * 2; i++)
                            {
                                short s = pcm[offset + i];
                                packet[HeaderBytes + i * 2] = (byte)s;
                                packet[HeaderBytes + i * 2 + 1] = (byte)(s >> 8);
                            }
                            offset += perPacket * 2;
                            foreach (IPEndPoint phone in Targets())
                            {
                                try { sender.Send(packet, packet.Length, phone); }
                                catch (SocketException) { }   // that phone's gone; it expires on its own
                            }
                        }
                        pcm.RemoveRange(0, offset);
                    }
                }
                finally { sender.Close(); }
            }
        }

        // ---------------------------------------------------------------- Windows audio (WASAPI loopback)

        /// <summary>What the default speakers are playing, as 16-bit stereo, read in small pieces.</summary>
        internal sealed class Loopback : IDisposable
        {
            const int ShareModeShared = 0, StreamFlagsLoopback = 0x00020000, BufferFlagsSilent = 0x2;
            const int RenderFlow = 0, MultimediaRole = 1, ClsctxAll = 23;
            static readonly Guid FloatFormat = new Guid("00000003-0000-0010-8000-00aa00389b71");
            static readonly Guid PcmFormat = new Guid("00000001-0000-0010-8000-00aa00389b71");

            object _enumerator, _device, _client, _capture;
            readonly IAudioCaptureClient _reader;
            readonly int _channels, _bits;
            readonly bool _float;
            byte[] _buffer = new byte[0];
            float[] _frame;

            public int SampleRate { get; private set; }
            public string Description { get; private set; }

            public Loopback()
            {
                try
                {
                    _enumerator = new MMDeviceEnumerator();
                    IMMDevice device;
                    Check(((IMMDeviceEnumerator)_enumerator).GetDefaultAudioEndpoint(RenderFlow, MultimediaRole, out device), "no speakers");
                    _device = device;
                    Guid iid = typeof(IAudioClient).GUID;
                    object client;
                    Check(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out client), "activate");
                    _client = client;
                    var audio = (IAudioClient)client;

                    IntPtr format;
                    Check(audio.GetMixFormat(out format), "mix format");
                    try
                    {
                        int tag = (ushort)Marshal.ReadInt16(format, 0);
                        _channels = (ushort)Marshal.ReadInt16(format, 2);
                        SampleRate = Marshal.ReadInt32(format, 4);
                        _bits = (ushort)Marshal.ReadInt16(format, 14);
                        Guid sub = tag == 0xFFFE ? (Guid)Marshal.PtrToStructure(new IntPtr(format.ToInt64() + 24), typeof(Guid)) : Guid.Empty;
                        _float = tag == 3 || sub == FloatFormat;
                        bool pcm = tag == 1 || sub == PcmFormat;
                        if (!(_float && _bits == 32) && !(pcm && (_bits == 16 || _bits == 24 || _bits == 32)))
                            throw new Exception("unsupported sound format (tag " + tag + ", " + _bits + " bits)");
                        Description = SampleRate + " Hz, " + _channels + " channels, " + (_float ? "float" : _bits + "-bit");
                        _frame = new float[_channels];
                        // 20 ms of buffer: plenty for a reader that wakes every couple of milliseconds.
                        Check(audio.Initialize(ShareModeShared, StreamFlagsLoopback, 200000, 0, format, IntPtr.Zero), "initialize");
                    }
                    finally { Marshal.FreeCoTaskMem(format); }

                    Guid captureId = typeof(IAudioCaptureClient).GUID;
                    object capture;
                    Check(audio.GetService(ref captureId, out capture), "capture service");
                    _capture = capture;
                    _reader = (IAudioCaptureClient)capture;
                    Check(audio.Start(), "start");
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            /// <summary>Appends any new sound to pcm (16-bit stereo, interleaved). False if there was none yet.</summary>
            public bool Read(List<short> pcm)
            {
                bool any = false;
                uint next;
                Check(_reader.GetNextPacketSize(out next), "packet size");
                while (next > 0)
                {
                    IntPtr data;
                    uint frames, flags;
                    ulong position, qpc;
                    Check(_reader.GetBuffer(out data, out frames, out flags, out position, out qpc), "read");
                    AppendStereo(data, (int)frames, (flags & BufferFlagsSilent) != 0, pcm);
                    Check(_reader.ReleaseBuffer(frames), "release");
                    any = true;
                    Check(_reader.GetNextPacketSize(out next), "packet size");
                }
                return any;
            }

            /// <summary>Converts to 16-bit stereo; 5.1 and 7.1 fold the centre (dialogue) and surrounds into left and right.</summary>
            void AppendStereo(IntPtr data, int frames, bool silent, List<short> pcm)
            {
                int bytes = _bits / 8, stride = bytes * _channels;
                if (silent)
                {
                    for (int f = 0; f < frames * 2; f++) pcm.Add(0);
                    return;
                }
                if (_buffer.Length < frames * stride) _buffer = new byte[frames * stride * 2];
                Marshal.Copy(data, _buffer, 0, frames * stride);
                for (int f = 0; f < frames; f++)
                {
                    for (int c = 0; c < _channels; c++) _frame[c] = Sample(f * stride + c * bytes);
                    float[] frame = _frame;
                    float left = frame[0], right = _channels > 1 ? frame[1] : frame[0];
                    if (_channels >= 6)   // FL FR FC LFE BL BR (SL SR)
                    {
                        left += 0.707f * frame[2] + 0.5f * frame[4];
                        right += 0.707f * frame[2] + 0.5f * frame[5];
                        if (_channels >= 8)
                        {
                            left += 0.5f * frame[6];
                            right += 0.5f * frame[7];
                        }
                    }
                    pcm.Add(ToShort(left));
                    pcm.Add(ToShort(right));
                }
            }

            float Sample(int offset)
            {
                if (_float) return BitConverter.ToSingle(_buffer, offset);
                switch (_bits)
                {
                    case 16: return BitConverter.ToInt16(_buffer, offset) / 32768f;
                    case 24: return ((_buffer[offset] | (_buffer[offset + 1] << 8) | (_buffer[offset + 2] << 16)) << 8 >> 8) / 8388608f;
                    default: return BitConverter.ToInt32(_buffer, offset) / 2147483648f;
                }
            }

            static short ToShort(float value)
            {
                value = Math.Max(-1f, Math.Min(1f, value));
                return (short)(value * 32767f);
            }

            static void Check(int hr, string what)
            {
                if (hr < 0) throw new Exception(what + " failed (0x" + hr.ToString("X8") + ")");
            }

            public void Dispose()
            {
                try { if (_client != null) ((IAudioClient)_client).Stop(); } catch (Exception) { }
                foreach (object com in new[] { _capture, _client, _device, _enumerator })
                    if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
                _capture = _client = _device = _enumerator = null;
            }

            [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
            class MMDeviceEnumerator { }

            [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            interface IMMDeviceEnumerator
            {
                [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
                [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            }

            [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            interface IMMDevice
            {
                [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object client);
            }

            [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            interface IAudioClient
            {
                [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
                [PreserveSig] int GetBufferSize(out uint frames);
                [PreserveSig] int GetStreamLatency(out long latency);
                [PreserveSig] int GetCurrentPadding(out uint frames);
                [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
                [PreserveSig] int GetMixFormat(out IntPtr format);
                [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
                [PreserveSig] int Start();
                [PreserveSig] int Stop();
                [PreserveSig] int Reset();
                [PreserveSig] int SetEventHandle(IntPtr handle);
                [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
            }

            [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            interface IAudioCaptureClient
            {
                [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
                [PreserveSig] int ReleaseBuffer(uint frames);
                [PreserveSig] int GetNextPacketSize(out uint frames);
            }
        }
    }
}
