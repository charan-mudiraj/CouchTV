using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace CouchTV
{
    internal enum BtKind { Headphones, Speaker, Audio, Phone, Computer, Keyboard, Mouse, Gamepad, Other }

    /// <summary>A Bluetooth device this PC knows of (paired), or found nearby while searching.</summary>
    internal sealed class BtDevice
    {
        public ulong Address;
        public string Name = "";
        public uint ClassOfDevice;
        public bool Connected, Paired;
        public DateTime LastSeen, LastUsed;   // local time; MinValue when never

        public BtKind Kind { get { return Bluetooth.KindOf(ClassOfDevice); } }
        public bool IsAudio { get { BtKind k = Kind; return k == BtKind.Headphones || k == BtKind.Speaker || k == BtKind.Audio; } }
        public string AddressText { get { return Bluetooth.AddressText(Address); } }
        public string Label { get { return Name.Length > 0 ? Name : AddressText; } }
    }

    /// <summary>
    /// The PC's Bluetooth through Windows' own Bluetooth functions (bthprops): which adapter, which devices are paired
    /// and connected, searching for new ones, pairing (accepted here: headphones and speakers have no screen to show a
    /// code on, and old ones use the PIN 0000) and forgetting. Everything here blocks: call it off the UI thread.
    /// </summary>
    internal static class Bluetooth
    {
        public static readonly Guid AudioSink = new Guid("0000110b-0000-1000-8000-00805f9b34fb");   // A2DP: stereo sound
        const int ERROR_SUCCESS = 0, ERROR_NO_MORE_ITEMS = 259;
        const int MethodLegacy = 1, MethodNumericComparison = 3, MethodPasskeyNotification = 4, MethodPasskey = 5;
        const int MitmNotRequiredBonding = 2;

        /// <summary>The adapter's name (e.g. "Realtek Bluetooth 5.3 Adapter"), or null with no Bluetooth adapter (the dongle's out).</summary>
        public static string RadioName()
        {
            IntPtr radio;
            var p = new FIND_RADIO_PARAMS { dwSize = Marshal.SizeOf(typeof(FIND_RADIO_PARAMS)) };
            IntPtr find = BluetoothFindFirstRadio(ref p, out radio);
            if (find == IntPtr.Zero) return null;
            try
            {
                var info = new RADIO_INFO { dwSize = (uint)Marshal.SizeOf(typeof(RADIO_INFO)) };
                return BluetoothGetRadioInfo(radio, ref info) == ERROR_SUCCESS && !string.IsNullOrEmpty(info.szName) ? info.szName : "Bluetooth adapter";
            }
            finally
            {
                CloseHandle(radio);
                BluetoothFindRadioClose(find);
            }
        }

        /// <summary>
        /// The paired and connected devices; with search, also devices nearby that are ready to pair (it takes about
        /// that many seconds, and sound on Bluetooth headphones may stutter meanwhile).
        /// </summary>
        public static List<BtDevice> Devices(bool search, int seconds = 8)
        {
            var found = new List<BtDevice>();
            var p = new SEARCH_PARAMS
            {
                fReturnAuthenticated = 1, fReturnRemembered = 1, fReturnConnected = 1,
                fReturnUnknown = search ? 1 : 0, fIssueInquiry = search ? 1 : 0,
                cTimeoutMultiplier = (byte)Math.Max(1, Math.Min(48, (int)Math.Round(seconds / 1.28))),
            };
            p.dwSize = Marshal.SizeOf(typeof(SEARCH_PARAMS));
            var info = NewInfo();
            IntPtr find = BluetoothFindFirstDevice(ref p, ref info);
            if (find == IntPtr.Zero) return found;   // none (ERROR_NO_MORE_ITEMS), or no adapter
            try
            {
                do
                {
                    found.Add(new BtDevice
                    {
                        Address = info.Address, Name = (info.szName ?? "").Trim(), ClassOfDevice = info.ulClassofDevice,
                        Connected = info.fConnected != 0, Paired = info.fRemembered != 0 || info.fAuthenticated != 0,
                        LastSeen = Time(info.stLastSeen), LastUsed = Time(info.stLastUsed),
                    });
                    info = NewInfo();
                } while (BluetoothFindNextDevice(find, ref info));
            }
            finally { BluetoothFindDeviceClose(find); }
            return found;
        }

        /// <summary>
        /// Pairs with a device that's in pairing mode, accepting its request (no code to compare on headphones; PIN
        /// 0000 for old ones), and makes sure its stereo sound is switched on. 0, or the Windows error.
        /// </summary>
        public static int Pair(BtDevice device)
        {
            var info = NewInfo();
            info.Address = device.Address;
            info.ulClassofDevice = device.ClassOfDevice;
            info.szName = device.Name;
            IntPtr registration = IntPtr.Zero;
            AuthCallback callback = Accept;
            GCHandle keep = GCHandle.Alloc(callback);   // alive until Windows can't call it any more
            try
            {
                int registered = BluetoothRegisterForAuthenticationEx(ref info, out registration, callback, IntPtr.Zero);
                if (registered != ERROR_SUCCESS) Log.Info("Bluetooth: couldn't listen for pairing requests (" + registered + ")");
                int result = BluetoothAuthenticateDeviceEx(IntPtr.Zero, IntPtr.Zero, ref info, IntPtr.Zero, MitmNotRequiredBonding);
                Log.Info("Bluetooth: pairing " + device.Label + ": " + (result == ERROR_SUCCESS ? "done" : "error " + result));
                if (result != ERROR_SUCCESS) return result;
                if (device.IsAudio) EnableService(device.Address, AudioSink);
                return ERROR_SUCCESS;
            }
            finally
            {
                if (registration != IntPtr.Zero) BluetoothUnregisterAuthentication(registration);
                keep.Free();
            }
        }

        /// <summary>Forgets (unpairs) a device. 0, or the Windows error.</summary>
        public static int Forget(ulong address)
        {
            int result = BluetoothRemoveDevice(ref address);
            Log.Info("Bluetooth: forgot " + AddressText(address) + (result == ERROR_SUCCESS ? "" : " (error " + result + ")"));
            return result;
        }

        /// <summary>Switches a paired device's service on (e.g. its stereo sound), if Windows hasn't already.</summary>
        static void EnableService(ulong address, Guid service)
        {
            WithRadio(radio =>
            {
                var info = NewInfo();
                info.Address = address;
                if (BluetoothGetDeviceInfo(radio, ref info) != ERROR_SUCCESS) return;
                var services = new Guid[16];
                int count = services.Length;
                if (BluetoothEnumerateInstalledServices(radio, ref info, ref count, services) == ERROR_SUCCESS)
                    for (int i = 0; i < count; i++) if (services[i] == service) return;   // already on: leave it be
                int result = BluetoothSetServiceState(radio, ref info, ref service, 1);
                Log.Info("Bluetooth: switched on " + service + " for " + AddressText(address) + " (" + result + ")");
            });
        }

        /// <summary>Accepts the device's pairing request: "just works" or a code to show (nothing to compare it with here), or PIN 0000.</summary>
        static bool Accept(IntPtr context, IntPtr request)
        {
            try
            {
                ulong address = (ulong)Marshal.ReadInt64(request, 8);
                int method = Marshal.ReadInt32(request, DeviceInfoSize);
                uint value = (uint)Marshal.ReadInt32(request, DeviceInfoSize + 12);
                IntPtr response = Marshal.AllocHGlobal(48);
                try
                {
                    for (int i = 0; i < 48; i++) Marshal.WriteByte(response, i, 0);
                    Marshal.WriteInt64(response, 0, (long)address);
                    Marshal.WriteInt32(response, 8, method);
                    if (method == MethodLegacy)
                    {
                        for (int i = 0; i < 4; i++) Marshal.WriteByte(response, 12 + i, (byte)'0');   // PIN 0000
                        Marshal.WriteByte(response, 28, 4);
                    }
                    else if (method == MethodNumericComparison || method == MethodPasskeyNotification || method == MethodPasskey)
                    {
                        Marshal.WriteInt32(response, 12, (int)value);
                    }
                    int result = BluetoothSendAuthenticationResponseEx(IntPtr.Zero, response);
                    Log.Info("Bluetooth: accepted the pairing request of " + AddressText(address) + " (method " + method + ", " + result + ")");
                }
                finally { Marshal.FreeHGlobal(response); }
            }
            catch (Exception ex) { Log.Info("Bluetooth: pairing request: " + ex.Message); }
            return true;
        }

        static void WithRadio(Action<IntPtr> work)
        {
            IntPtr radio;
            var p = new FIND_RADIO_PARAMS { dwSize = Marshal.SizeOf(typeof(FIND_RADIO_PARAMS)) };
            IntPtr find = BluetoothFindFirstRadio(ref p, out radio);
            if (find == IntPtr.Zero) return;
            try { work(radio); }
            finally
            {
                CloseHandle(radio);
                BluetoothFindRadioClose(find);
            }
        }

        public static string AddressText(ulong address)
        {
            var parts = new string[6];
            for (int i = 0; i < 6; i++) parts[i] = ((address >> (8 * (5 - i))) & 0xFF).ToString("X2");
            return string.Join(":", parts);
        }

        /// <summary>What a device is, from its Bluetooth "class of device".</summary>
        public static BtKind KindOf(uint cod)
        {
            uint major = (cod >> 8) & 0x1F, minor = (cod >> 2) & 0x3F;
            switch (major)
            {
                case 0x04:   // audio / video
                    if (minor == 0x01 || minor == 0x02 || minor == 0x06) return BtKind.Headphones;
                    if (minor == 0x05 || minor == 0x07 || minor == 0x08 || minor == 0x0A) return BtKind.Speaker;
                    return BtKind.Audio;
                case 0x02: return BtKind.Phone;
                case 0x01: return BtKind.Computer;
                case 0x05:   // peripheral
                    if ((cod & 0x40) != 0) return BtKind.Keyboard;
                    if ((cod & 0x80) != 0) return BtKind.Mouse;
                    if (((cod >> 2) & 0x0F) == 0x01 || ((cod >> 2) & 0x0F) == 0x02) return BtKind.Gamepad;
                    return BtKind.Other;
                default:
                    return (cod & (1u << 21)) != 0 ? BtKind.Audio : BtKind.Other;   // the "audio" service bit
            }
        }

        static DateTime Time(SYSTEMTIME t)
        {
            if (t.wYear < 1601) return DateTime.MinValue;
            try { return new DateTime(t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, DateTimeKind.Utc).ToLocalTime(); }
            catch (ArgumentException) { return DateTime.MinValue; }
        }

        static DEVICE_INFO NewInfo() { return new DEVICE_INFO { dwSize = (uint)DeviceInfoSize, szName = "" }; }

        static readonly int DeviceInfoSize = Marshal.SizeOf(typeof(DEVICE_INFO));   // 560

        // ---------------------------------------------------------------- Windows' Bluetooth API

        [StructLayout(LayoutKind.Sequential)]
        struct FIND_RADIO_PARAMS { public int dwSize; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct RADIO_INFO
        {
            public uint dwSize;
            public ulong address;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
            public uint ulClassofDevice;
            public ushort lmpSubversion;
            public ushort manufacturer;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SEARCH_PARAMS
        {
            public int dwSize;
            public int fReturnAuthenticated, fReturnRemembered, fReturnUnknown, fReturnConnected, fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEMTIME { public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVICE_INFO
        {
            public uint dwSize;
            public ulong Address;
            public uint ulClassofDevice;
            public int fConnected, fRemembered, fAuthenticated;
            public SYSTEMTIME stLastSeen, stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate bool AuthCallback(IntPtr context, IntPtr request);

        [DllImport("bthprops.cpl", SetLastError = true)] static extern IntPtr BluetoothFindFirstRadio(ref FIND_RADIO_PARAMS p, out IntPtr radio);
        [DllImport("bthprops.cpl", SetLastError = true)] static extern bool BluetoothFindRadioClose(IntPtr find);
        [DllImport("bthprops.cpl", SetLastError = true)] static extern int BluetoothGetRadioInfo(IntPtr radio, ref RADIO_INFO info);
        [DllImport("bthprops.cpl", SetLastError = true)] static extern IntPtr BluetoothFindFirstDevice(ref SEARCH_PARAMS p, ref DEVICE_INFO info);
        [DllImport("bthprops.cpl", SetLastError = true)] static extern bool BluetoothFindNextDevice(IntPtr find, ref DEVICE_INFO info);
        [DllImport("bthprops.cpl", SetLastError = true)] static extern bool BluetoothFindDeviceClose(IntPtr find);
        [DllImport("bthprops.cpl", SetLastError = true)] static extern int BluetoothGetDeviceInfo(IntPtr radio, ref DEVICE_INFO info);
        [DllImport("bthprops.cpl", SetLastError = true)] static extern int BluetoothRemoveDevice(ref ulong address);
        [DllImport("bthprops.cpl", SetLastError = true)] static extern int BluetoothAuthenticateDeviceEx(IntPtr parent, IntPtr radio, ref DEVICE_INFO info, IntPtr oob, int requirements);
        [DllImport("bthprops.cpl", SetLastError = true)] static extern int BluetoothRegisterForAuthenticationEx(ref DEVICE_INFO info, out IntPtr registration, AuthCallback callback, IntPtr context);
        [DllImport("bthprops.cpl", SetLastError = true)] static extern bool BluetoothUnregisterAuthentication(IntPtr registration);
        [DllImport("bthprops.cpl", SetLastError = true)] static extern int BluetoothSendAuthenticationResponseEx(IntPtr radio, IntPtr response);
        [DllImport("bthprops.cpl", SetLastError = true)] static extern int BluetoothSetServiceState(IntPtr radio, ref DEVICE_INFO info, ref Guid service, int flags);
        [DllImport("bthprops.cpl", SetLastError = true)] static extern int BluetoothEnumerateInstalledServices(IntPtr radio, ref DEVICE_INFO info, ref int count, [Out] Guid[] services);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    }
}
