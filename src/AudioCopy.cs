using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace CouchTV
{
    /// <summary>
    /// Plays what the TV plays on a second output as well: Bluetooth headphones, say, while the TV speakers carry on.
    /// Windows only plays to one output, so this copies its main output (WASAPI loopback, at full precision, with
    /// Windows' volume undone as for phones, so the headphones have their own) to the other one. The two run on their own
    /// clocks, so the copy plays a hair faster or slower (a windowed-sinc resampler, inaudible) to keep about 60 ms
    /// ready: no gaps, and no delay piling up. Windows converts the rate if the headphones' differs. Starts again by
    /// itself if either output goes away and comes back, until stopped.
    /// </summary>
    internal sealed class AudioCopy
    {
        const int CushionMs = 60, MostMs = 300;

        public readonly string Target;   // the output's id
        public readonly string Name;
        volatile bool _stop;
        Thread _thread;

        /// <summary>Frames played since it started (for the self-test).</summary>
        public long Played { get; private set; }

        public AudioCopy(string target, string name)
        {
            Target = target;
            Name = name;
        }

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Sound copy to " + Name, Priority = ThreadPriority.Highest };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
        }

        void Run()
        {
            timeBeginPeriod(1);
            try
            {
                while (!_stop)
                {
                    try { CopyUntilSomethingChanges(); }
                    catch (Exception ex) { if (!_stop) Log.Info("Sound copy to " + Name + ": " + ex.Message); }
                    for (int i = 0; i < 10 && !_stop; i++) Thread.Sleep(100);
                }
            }
            finally { timeEndPeriod(1); }
        }

        void CopyUntilSomethingChanges()
        {
            using (var source = new AudioShare.Loopback())
            using (var sink = new RenderStream(Target, source.SampleRate))
            {
                Log.Info("Sound copy: " + source.Description + " to " + Name);
                int rate = source.SampleRate;
                double target = rate * CushionMs / 1000.0;
                var pcm = new List<float>(8192);
                var pending = new List<float>(32768);   // resampled, waiting for room in the output
                var resampler = new SincResampler();
                sink.Write(new float[(int)target * 2], (int)target);   // start with the cushion, in silence
                var clock = System.Diagnostics.Stopwatch.StartNew();
                long quietSince = -1, silence = 0;
                double fill = target;
                while (!_stop)
                {
                    if (source.ReadFloat(pcm)) quietSince = -1;
                    else
                    {
                        // Nothing playing gives no sound data at all: keep the copy going with silence at the real pace.
                        long now = clock.ElapsedMilliseconds;
                        if (quietSince < 0)
                        {
                            quietSince = now;
                            silence = 0;
                        }
                        long due = now - quietSince > 20 ? (now - quietSince - 20) * rate / 1000 - silence : 0;
                        for (long i = 0; i < due; i++)
                        {
                            pcm.Add(0f);
                            pcm.Add(0f);
                        }
                        silence += Math.Max(0, due);
                    }
                    if (pcm.Count > 0)
                    {
                        // More ready than the cushion: play a hair faster; less: a hair slower (at most 0.2%).
                        fill = fill * 0.98 + (sink.Padding + pending.Count / 2) * 0.02;
                        double step = 1 + Math.Max(-0.002, Math.Min(0.002, (fill - target) / (rate * 20.0)));
                        float[] input = pcm.ToArray();
                        pcm.Clear();
                        float[] output = resampler.Process(input, input.Length / 2, step);
                        pending.AddRange(output);
                    }
                    int room = sink.BufferFrames - sink.Padding;
                    int frames = Math.Min(room, pending.Count / 2);
                    if (frames > 0)
                    {
                        sink.Write(pending.GetRange(0, frames * 2).ToArray(), frames);
                        pending.RemoveRange(0, frames * 2);
                        Played += frames;
                    }
                    int most = rate * MostMs / 1000;
                    if (pending.Count / 2 > most) pending.RemoveRange(0, (pending.Count / 2 - (int)target) * 2);   // the output stalled
                    Thread.Sleep(2);
                }
            }
        }

        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint milliseconds);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint milliseconds);

        /// <summary>A shared-mode output stream of 32-bit float stereo at the given rate (Windows converts it to the output's).</summary>
        sealed class RenderStream : IDisposable
        {
            const int ShareModeShared = 0, ClsctxAll = 23;
            const int AutoConvertPcm = unchecked((int)0x80000000), SrcDefaultQuality = 0x08000000;
            object _enumerator, _device, _client, _render;
            readonly IAudioRenderClient _writer;
            readonly IAudioClient _audio;

            public int BufferFrames { get; private set; }

            public int Padding
            {
                get
                {
                    uint padding;
                    Check(_audio.GetCurrentPadding(out padding), "padding");
                    return (int)padding;
                }
            }

            public RenderStream(string id, int rate)
            {
                IntPtr format = Marshal.AllocHGlobal(18);
                try
                {
                    _enumerator = CoreAudio.NewEnumerator();
                    IMMDevice device;
                    Check(((IMMDeviceEnumerator)_enumerator).GetDevice(id, out device), "the output");
                    _device = device;
                    Guid iid = typeof(IAudioClient).GUID;
                    object client;
                    Check(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out client), "activate");
                    _client = client;
                    _audio = (IAudioClient)client;
                    // WAVEFORMATEX: IEEE float, 2 channels, 32 bits.
                    Marshal.WriteInt16(format, 0, 3);
                    Marshal.WriteInt16(format, 2, 2);
                    Marshal.WriteInt32(format, 4, rate);
                    Marshal.WriteInt32(format, 8, rate * 8);
                    Marshal.WriteInt16(format, 12, 8);
                    Marshal.WriteInt16(format, 14, 32);
                    Marshal.WriteInt16(format, 16, 0);
                    Check(_audio.Initialize(ShareModeShared, AutoConvertPcm | SrcDefaultQuality, 2000000, 0, format, IntPtr.Zero), "initialize");
                    uint frames;
                    Check(_audio.GetBufferSize(out frames), "buffer size");
                    BufferFrames = (int)frames;
                    Guid renderId = typeof(IAudioRenderClient).GUID;
                    object render;
                    Check(_audio.GetService(ref renderId, out render), "render service");
                    _render = render;
                    _writer = (IAudioRenderClient)render;
                    Check(_audio.Start(), "start");
                }
                catch
                {
                    Dispose();
                    throw;
                }
                finally { Marshal.FreeHGlobal(format); }
            }

            public void Write(float[] samples, int frames)
            {
                IntPtr data;
                Check(_writer.GetBuffer((uint)frames, out data), "write");
                Marshal.Copy(samples, 0, data, frames * 2);
                Check(_writer.ReleaseBuffer((uint)frames, 0), "release");
            }

            static void Check(int hr, string what)
            {
                if (hr < 0) throw new Exception(what + " failed (0x" + hr.ToString("X8") + ")");
            }

            public void Dispose()
            {
                try { if (_audio != null) _audio.Stop(); } catch (Exception) { }
                foreach (object com in new[] { _render, _client, _device, _enumerator })
                    if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
                _render = _client = _device = _enumerator = null;
            }

            [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
            class MMDeviceEnumerator { }

            [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            interface IMMDeviceEnumerator
            {
                [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
                [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
                [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
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

            [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            interface IAudioRenderClient
            {
                [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
                [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
            }
        }
    }

    /// <summary>
    /// Plays stereo sound a hair faster or slower without a sound you can hear: each output sample is read from between
    /// the input samples with a windowed-sinc filter (16 taps), and the reading position carries on from one chunk to
    /// the next. At normal speed on a whole sample it's exact. The same as the phone app's Resampler.kt, in float.
    /// </summary>
    internal sealed class SincResampler
    {
        const int Half = 8, Taps = 2 * Half, Phases = 256;
        static readonly float[] Filters = Build();

        float[] _buffer = new float[4096];   // input frames not yet passed, stereo interleaved
        int _count = Half - 1;               // frames in it: starts with a little silence
        double _position = Half - 1;         // where the next output frame is read, in input frames

        static float[] Build()
        {
            var table = new float[(Phases + 1) * Taps];
            for (int p = 0; p <= Phases; p++)
            {
                double f = (double)p / Phases, sum = 0;
                var row = new double[Taps];
                for (int k = 0; k < Taps; k++)
                {
                    double x = k - (Half - 1) - f;
                    double sinc = Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
                    row[k] = sinc * Kaiser(x / Half);
                    sum += row[k];
                }
                for (int k = 0; k < Taps; k++) table[p * Taps + k] = (float)(row[k] / sum);
            }
            return table;
        }

        static double Kaiser(double x)
        {
            if (Math.Abs(x) >= 1.0) return 0.0;
            return BesselI0(5.0 * Math.Sqrt(1 - x * x)) / BesselI0(5.0);
        }

        static double BesselI0(double x)
        {
            double sum = 1.0, term = 1.0;
            for (int k = 1; term > 1e-12 * sum; k++)
            {
                term *= (x / (2 * k)) * (x / (2 * k));
                sum += term;
            }
            return sum;
        }

        /// <summary>frames of input (stereo interleaved) read at step input frames per output frame: above 1 plays faster.</summary>
        public float[] Process(float[] input, int frames, double step)
        {
            if (_buffer.Length < (_count + frames) * 2) Array.Resize(ref _buffer, (_count + frames) * 4);
            Array.Copy(input, 0, _buffer, _count * 2, frames * 2);
            _count += frames;
            var output = new List<float>((int)((frames + 2) / step) * 2 + 4);
            double pos = _position;
            while (true)
            {
                int i = (int)pos;
                if (i + Half >= _count) break;   // the filter reaches Half frames ahead
                double p = (pos - i) * Phases;
                int p0 = (int)p;
                float blend = (float)(p - p0);
                int row = p0 * Taps, at = (i - Half + 1) * 2;
                float left = 0, right = 0;
                for (int k = 0; k < Taps; k++)
                {
                    float a = Filters[row + k];
                    float c = a + (Filters[row + Taps + k] - a) * blend;
                    left += _buffer[at] * c;
                    right += _buffer[at + 1] * c;
                    at += 2;
                }
                output.Add(left);
                output.Add(right);
                pos += step;
            }
            int done = (int)pos - (Half - 1);   // frames the filter no longer reaches
            if (done > 0)
            {
                Array.Copy(_buffer, done * 2, _buffer, 0, (_count - done) * 2);
                _count -= done;
                pos -= done;
            }
            _position = pos;
            return output.ToArray();
        }
    }
}
