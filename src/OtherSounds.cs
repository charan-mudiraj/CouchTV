using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace CouchTV
{
    /// <summary>
    /// While phones' screens are on the TV (PhoneShare), every other sound on it is muted: music or a film started behind
    /// them, the home screen's trailer. So the speakers, and the sound phones listen to (AudioShare, which hears what the
    /// speakers play), carry only the phones. Windows' per-app volume does it (the audio sessions on the default
    /// speakers), looking again every second for new ones; the phones' own sound (the phone screens' WebView2, and its
    /// helper processes) and CouchTV's are left alone, and afterwards only what was muted here is unmuted.
    /// </summary>
    internal sealed class OtherSounds : IDisposable
    {
        readonly Func<int> _keep;   // the phone screens' WebView2's process (its helpers are its children)
        readonly HashSet<string> _muted = new HashSet<string>();   // the sessions muted here (their instance ids)
        readonly ManualResetEvent _stop = new ManualResetEvent(false);
        readonly Thread _thread;

        /// <summary>Mutes the others now and every second until disposed (then unmutes them). [keep]: the WebView2's process id, 0 if not known yet.</summary>
        public OtherSounds(Func<int> keep)
        {
            _keep = keep;
            _thread = new Thread(Run) { IsBackground = true, Name = "CouchTV other sounds" };
            _thread.Start();
        }

        /// <summary>How many sessions are muted here now (the self-test).</summary>
        public int Muted { get { lock (_muted) return _muted.Count; } }

        void Run()
        {
            try
            {
                do
                {
                    try { Pass(true); }
                    catch (Exception ex) { Log.Info("Other sounds: " + ex.Message); }
                } while (!_stop.WaitOne(1000));
                Pass(false);
            }
            catch (Exception ex) { Log.Info("Other sounds, unmuting: " + ex.Message); }
        }

        /// <summary>[mute]: mutes every session that isn't the phones' or CouchTV's; else unmutes the ones muted here.</summary>
        void Pass(bool mute)
        {
            HashSet<int> keep = mute ? Kept() : null;
            ForEachSession((id, pid, volume) =>
            {
                Guid none = Guid.Empty;
                bool muted;
                volume.GetMute(out muted);
                lock (_muted)
                {
                    if (mute)
                    {
                        if (keep.Contains(pid) || muted) return;
                        volume.SetMute(true, ref none);
                        if (_muted.Add(id)) Log.Info("Other sounds: muted " + Name(pid) + " while phones show");
                    }
                    else if (_muted.Contains(id) && muted) volume.SetMute(false, ref none);
                }
            });
            if (!mute) lock (_muted) _muted.Clear();
        }

        /// <summary>The apps with a sound on the default speakers now, and whether each is muted (for the self-test: it only looks).</summary>
        internal static List<string> List()
        {
            var list = new List<string>();
            ForEachSession((id, pid, volume) =>
            {
                bool muted;
                volume.GetMute(out muted);
                list.Add(Name(pid) + (muted ? " (muted)" : ""));
            });
            return list;
        }

        /// <summary>Each app's audio session on the default speakers (not Windows' own sounds): its id, its process, its volume.</summary>
        static void ForEachSession(Action<string, int, ISimpleAudioVolume> each)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice device;
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out device) != 0 || device == null) return;   // render, multimedia
            Guid iid = typeof(IAudioSessionManager2).GUID;
            object activated;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out activated));
            var manager = (IAudioSessionManager2)activated;
            IAudioSessionEnumerator sessions;
            Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out sessions));
            int count;
            sessions.GetCount(out count);
            for (int i = 0; i < count; i++)
            {
                object session;
                if (sessions.GetSession(i, out session) != 0 || session == null) continue;
                var control = (IAudioSessionControl2)session;
                var volume = (ISimpleAudioVolume)session;
                string id;
                uint pid;
                control.GetSessionInstanceIdentifier(out id);
                control.GetProcessId(out pid);
                if (id == null || control.IsSystemSoundsSession() == 0) continue;   // S_OK: Windows' own sounds
                each(id, (int)pid, volume);
            }
        }

        /// <summary>CouchTV, and the phone screens' WebView2 with all its helper processes (the sound comes from one).</summary>
        HashSet<int> Kept()
        {
            var keep = new HashSet<int> { Process.GetCurrentProcess().Id };
            int root = _keep();
            if (root <= 0) return keep;
            keep.Add(root);
            Dictionary<int, int> parents = Parents();
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (KeyValuePair<int, int> pair in parents)
                    if (!keep.Contains(pair.Key) && keep.Contains(pair.Value) && pair.Value != 0) { keep.Add(pair.Key); grew = true; }
            }
            return keep;
        }

        static string Name(int pid)
        {
            try { return Process.GetProcessById(pid).ProcessName; }
            catch (Exception) { return "process " + pid; }
        }

        /// <summary>Every process's parent (Toolhelp's snapshot).</summary>
        static Dictionary<int, int> Parents()
        {
            var parents = new Dictionary<int, int>();
            IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);   // TH32CS_SNAPPROCESS
            if (snapshot == new IntPtr(-1)) return parents;
            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                for (bool more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
                    parents[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
            }
            finally { CloseHandle(snapshot); }
            return parents;
        }

        public void Dispose()
        {
            _stop.Set();
            _thread.Join(3000);
        }

        // ---------------------------------------------------------------- Windows' audio sessions and processes

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
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
            [PreserveSig] int GetGroupingParam(out Guid grouping);
            [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out uint pid);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference(bool optOut);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid context);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute(bool mute, ref Guid context);
            [PreserveSig] int GetMute(out bool mute);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    }
}
