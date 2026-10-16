using System;
using System.Runtime.InteropServices;

namespace CouchTV
{
    /// <summary>
    /// Master volume through the Windows Core Audio API. Explorer normally handles the volume keys; in TV mode
    /// Explorer isn't running, so CouchTV does it.
    /// </summary>
    internal static class Audio
    {
        const int RenderFlow = 0, MultimediaRole = 1, ClsctxAll = 23;

        /// <summary>Changes volume by delta (0..1 scale), or toggles mute. Returns false when there is no audio device.</summary>
        public static bool Change(float delta, bool toggleMute, out float level, out bool muted)
        {
            float l = 0;
            bool m = false;
            bool ok = Use(volume =>
            {
                Guid context = Guid.Empty;
                volume.GetMasterVolumeLevelScalar(out l);
                volume.GetMute(out m);
                if (toggleMute)
                {
                    m = !m;
                    volume.SetMute(m, ref context);
                }
                else if (delta != 0)
                {
                    // Snap to 2% steps, the same as Windows' own volume keys.
                    l = Math.Max(0f, Math.Min(1f, (float)Math.Round((l + delta) * 50) / 50f));
                    volume.SetMasterVolumeLevelScalar(l, ref context);
                    if (m && delta > 0)
                    {
                        m = false;
                        volume.SetMute(false, ref context);
                    }
                }
            });
            level = l;
            muted = m;
            return ok;
        }

        /// <summary>The volume now (0..1) and whether it's muted. Returns false when there is no audio device.</summary>
        public static bool Get(out float level, out bool muted)
        {
            return Change(0, false, out level, out muted);
        }

        /// <summary>
        /// Sets the volume to exactly this level (0..1; below 0 leaves it) and mutes or unmutes (null leaves it), as the
        /// phone's slider does. Returns false when there is no audio device.
        /// </summary>
        public static bool Set(float target, bool? mute, out float level, out bool muted)
        {
            float l = 0;
            bool m = false;
            bool ok = Use(volume =>
            {
                Guid context = Guid.Empty;
                if (target >= 0) volume.SetMasterVolumeLevelScalar(Math.Min(1f, target), ref context);
                if (mute.HasValue) volume.SetMute(mute.Value, ref context);
                volume.GetMasterVolumeLevelScalar(out l);
                volume.GetMute(out m);
            });
            level = l;
            muted = m;
            return ok;
        }

        /// <summary>Sets the volume to a level (0..1), without showing anything. Returns false when there is no audio device.</summary>
        public static bool SetLevel(float target)
        {
            float level;
            bool muted;
            return Change(0, false, out level, out muted) && Change(target - level, false, out level, out muted);
        }

        /// <summary>Runs act on the default speakers' volume control; false when there are none.</summary>
        static bool Use(Action<IAudioEndpointVolume> act)
        {
            object enumerator = null, device = null, endpoint = null;
            try
            {
                enumerator = CoreAudio.NewEnumerator();
                IMMDevice dev;
                if (((IMMDeviceEnumerator)enumerator).GetDefaultAudioEndpoint(RenderFlow, MultimediaRole, out dev) != 0 || dev == null) return false;
                device = dev;

                Guid iid = typeof(IAudioEndpointVolume).GUID;
                if (dev.Activate(ref iid, ClsctxAll, IntPtr.Zero, out endpoint) != 0 || endpoint == null) return false;
                act((IAudioEndpointVolume)endpoint);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Volume", ex);
                return false;
            }
            finally
            {
                Release(endpoint);
                Release(device);
                Release(enumerator);
            }
        }

        static void Release(object com)
        {
            if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
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
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
            [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        }
    }
}
