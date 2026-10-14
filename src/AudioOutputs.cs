using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CouchTV
{
    /// <summary>
    /// Windows' list of sound devices. It's one object for the whole process, so it's made by its class id: made as
    /// a C# class instead, a second such class elsewhere (the volume, the phones' sound…) can't be cast to it.
    /// </summary>
    internal static class CoreAudio
    {
        static readonly Type EnumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));

        public static object NewEnumerator() { return Activator.CreateInstance(EnumeratorType); }
    }

    /// <summary>A sound output (a Windows "render endpoint"): the TV's HDMI, the PC's speakers, Bluetooth headphones…</summary>
    internal sealed class AudioOutput
    {
        public string Id;          // Windows' id for it
        public string Name;        // e.g. "Headphones (WH-1000XM4)"
        public bool Active;        // plugged in / connected now (else unplugged: a Bluetooth device that's off, say)
        public int FormFactor;     // 1 speakers, 3 headphones, 5 headset, 9 a display (HDMI)…
        public ulong BtAddress;    // the Bluetooth device it belongs to; 0 if it isn't one
        public bool HandsFree;     // a Bluetooth headset's phone-call output (mono, low quality): never used here
    }

    /// <summary>
    /// Windows' sound outputs (Core Audio): which there are, which is the main one ("default"), switching that, and
    /// connecting or disconnecting a paired Bluetooth audio device, as the Connect button in Windows' sound settings
    /// does (the Bluetooth audio driver's "one-shot reconnect"). Call off the UI thread.
    /// </summary>
    internal static class AudioOutputs
    {
        const int RenderFlow = 0, StateActive = 1, StateUnplugged = 8, ClsctxAll = 23, ClsctxInproc = 1;
        static readonly Guid BtAudioProperties = new Guid("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");   // KSPROPSETID_BtAudio
        const int OneShotReconnect = 0, OneShotDisconnect = 1, KsPropertyGet = 1;
        static readonly PROPERTYKEY FriendlyName = new PROPERTYKEY(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
        static readonly PROPERTYKEY FormFactorKey = new PROPERTYKEY(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
        static readonly Regex BtDevicePath = new Regex(@"BTHENUM#\{(?<service>[0-9a-fA-F-]+)\}.*?&(?<address>[0-9a-fA-F]{12})_C", RegexOptions.Compiled);

        /// <summary>Every output that's there, connected or not (a paired Bluetooth device that's off is "unplugged").</summary>
        public static List<AudioOutput> All()
        {
            var outputs = new List<AudioOutput>();
            var enumerator = (IMMDeviceEnumerator)CoreAudio.NewEnumerator();
            IMMDeviceCollection collection = null;
            try
            {
                if (enumerator.EnumAudioEndpoints(RenderFlow, StateActive | StateUnplugged, out collection) != 0) return outputs;
                uint count;
                collection.GetCount(out count);
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device;
                    if (collection.Item(i, out device) != 0) continue;
                    try { outputs.Add(Describe(device)); }
                    catch (Exception ex) { Log.Info("Sound outputs: " + ex.Message); }
                    finally { Marshal.ReleaseComObject(device); }
                }
            }
            finally
            {
                if (collection != null) Marshal.ReleaseComObject(collection);
                Marshal.ReleaseComObject(enumerator);
            }
            return outputs;
        }

        static AudioOutput Describe(IMMDevice device)
        {
            var output = new AudioOutput();
            device.GetId(out output.Id);
            int state;
            device.GetState(out state);
            output.Active = state == StateActive;
            IPropertyStore store;
            if (device.OpenPropertyStore(0, out store) == 0)
            {
                try
                {
                    output.Name = ReadString(store, FriendlyName) ?? "";
                    output.FormFactor = (int)ReadUInt(store, FormFactorKey);
                }
                finally { Marshal.ReleaseComObject(store); }
            }
            // The driver behind it: for Bluetooth, a path naming the service (stereo or hands-free) and the device's address.
            string driver = ConnectedDeviceId(device);
            if (driver != null)
            {
                Match m = BtDevicePath.Match(driver);
                if (m.Success)
                {
                    output.BtAddress = Convert.ToUInt64(m.Groups["address"].Value, 16);
                    output.HandsFree = m.Groups["service"].Value.StartsWith("0000111e", StringComparison.OrdinalIgnoreCase) ||
                                       m.Groups["service"].Value.StartsWith("0000111f", StringComparison.OrdinalIgnoreCase);
                }
            }
            if (output.BtAddress != 0 && output.FormFactor == 5) output.HandsFree = true;   // a "headset": the phone-call side
            return output;
        }

        /// <summary>The main output's id (what Windows plays to), or null.</summary>
        public static string DefaultId()
        {
            var enumerator = (IMMDeviceEnumerator)CoreAudio.NewEnumerator();
            try
            {
                IMMDevice device;
                if (enumerator.GetDefaultAudioEndpoint(RenderFlow, 1, out device) != 0 || device == null) return null;
                try
                {
                    string id;
                    device.GetId(out id);
                    return id;
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }

        /// <summary>Makes an output the main one, for everything (films, music and calls).</summary>
        public static bool SetDefault(string id)
        {
            object policy = null;
            try
            {
                policy = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")));
                var config = (IPolicyConfig)policy;
                bool ok = true;
                for (int role = 0; role < 3; role++) ok &= config.SetDefaultEndpoint(id, role) == 0;
                return ok;
            }
            catch (Exception ex)
            {
                Log.Info("Sound outputs: couldn't switch the main output: " + ex.Message);
                return false;
            }
            finally { if (policy != null) Marshal.ReleaseComObject(policy); }
        }

        /// <summary>
        /// Asks a Bluetooth audio output's driver to connect to the device (or disconnect it). Returns at once; the
        /// output turns active a few seconds later if the device is on and in range. False if it couldn't be asked.
        /// </summary>
        public static bool BtConnect(string id, bool connect)
        {
            var enumerator = (IMMDeviceEnumerator)CoreAudio.NewEnumerator();
            IMMDevice device = null;
            object topology = null;
            IConnector connector = null, other = null;
            object ks = null;
            try
            {
                if (enumerator.GetDevice(id, out device) != 0) return false;
                Guid topologyId = typeof(IDeviceTopology).GUID;
                if (device.Activate(ref topologyId, ClsctxAll, IntPtr.Zero, out topology) != 0) return false;
                if (((IDeviceTopology)topology).GetConnector(0, out connector) != 0) return false;
                if (connector.GetConnectedTo(out other) != 0) return false;
                Guid ksId = typeof(IKsControl).GUID;
                if (((IPart)other).Activate(ClsctxInproc, ref ksId, out ks) != 0) return false;
                var property = new KSPROPERTY { Set = BtAudioProperties, Id = connect ? OneShotReconnect : OneShotDisconnect, Flags = KsPropertyGet };
                int returned;
                int hr = ((IKsControl)ks).KsProperty(ref property, Marshal.SizeOf(typeof(KSPROPERTY)), IntPtr.Zero, 0, out returned);
                if (hr != 0) Log.Info("Sound outputs: Bluetooth " + (connect ? "connect" : "disconnect") + " refused (0x" + hr.ToString("X8") + ")");
                return hr == 0;
            }
            catch (Exception ex)
            {
                Log.Info("Sound outputs: Bluetooth " + (connect ? "connect" : "disconnect") + ": " + ex.Message);
                return false;
            }
            finally
            {
                foreach (object com in new object[] { ks, other, connector, topology, device, enumerator })
                    if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
            }
        }

        /// <summary>Whether connect/disconnect requests can be sent to this output (for the self-test).</summary>
        public static bool CanConnect(string id)
        {
            var enumerator = (IMMDeviceEnumerator)CoreAudio.NewEnumerator();
            IMMDevice device = null;
            object topology = null, ks = null;
            IConnector connector = null, other = null;
            try
            {
                Guid topologyId = typeof(IDeviceTopology).GUID, ksId = typeof(IKsControl).GUID;
                return enumerator.GetDevice(id, out device) == 0 && device.Activate(ref topologyId, ClsctxAll, IntPtr.Zero, out topology) == 0 &&
                       ((IDeviceTopology)topology).GetConnector(0, out connector) == 0 && connector.GetConnectedTo(out other) == 0 &&
                       ((IPart)other).Activate(ClsctxInproc, ref ksId, out ks) == 0;
            }
            catch (Exception) { return false; }
            finally
            {
                foreach (object com in new object[] { ks, other, connector, topology, device, enumerator })
                    if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
            }
        }

        static string ConnectedDeviceId(IMMDevice device)
        {
            object topology = null;
            IConnector connector = null;
            try
            {
                Guid topologyId = typeof(IDeviceTopology).GUID;
                if (device.Activate(ref topologyId, ClsctxAll, IntPtr.Zero, out topology) != 0) return null;
                if (((IDeviceTopology)topology).GetConnector(0, out connector) != 0) return null;
                string id;
                return connector.GetDeviceIdConnectedTo(out id) == 0 ? id : null;
            }
            catch (Exception) { return null; }
            finally
            {
                if (connector != null) Marshal.ReleaseComObject(connector);
                if (topology != null) Marshal.ReleaseComObject(topology);
            }
        }

        static string ReadString(IPropertyStore store, PROPERTYKEY key)
        {
            PROPVARIANT value;
            if (store.GetValue(ref key, out value) != 0) return null;
            try { return value.vt == 31 ? Marshal.PtrToStringUni(value.pointer) : null; }   // VT_LPWSTR
            finally { PropVariantClear(ref value); }
        }

        static uint ReadUInt(IPropertyStore store, PROPERTYKEY key)
        {
            PROPVARIANT value;
            if (store.GetValue(ref key, out value) != 0) return 0;
            try { return value.vt == 19 ? value.number : 0; }   // VT_UI4
            finally { PropVariantClear(ref value); }
        }

        // ---------------------------------------------------------------- Core Audio and kernel streaming interfaces

        [StructLayout(LayoutKind.Sequential)]
        struct PROPERTYKEY
        {
            public Guid fmtid;
            public int pid;
            public PROPERTYKEY(Guid f, int p) { fmtid = f; pid = p; }
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr pointer;
            [FieldOffset(8)] public uint number;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KSPROPERTY
        {
            public Guid Set;
            public int Id;
            public int Flags;
        }

        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT value);

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object client);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        }

        [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDeviceTopology
        {
            [PreserveSig] int GetConnectorCount(out uint count);
            [PreserveSig] int GetConnector(uint index, out IConnector connector);
        }

        [ComImport, Guid("9c2c4058-23f5-41de-877a-df3af236a09e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IConnector
        {
            [PreserveSig] int GetConnectorType(out int type);
            [PreserveSig] int GetDataFlow(out int flow);
            [PreserveSig] int ConnectTo(IntPtr other);
            [PreserveSig] int Disconnect();
            [PreserveSig] int IsConnected(out bool connected);
            [PreserveSig] int GetConnectedTo(out IConnector other);
            [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }

        [ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPart
        {
            [PreserveSig] int GetName(out IntPtr name);
            [PreserveSig] int GetLocalId(out uint id);
            [PreserveSig] int GetGlobalId(out IntPtr id);
            [PreserveSig] int GetPartType(out int type);
            [PreserveSig] int GetSubType(out Guid subtype);
            [PreserveSig] int GetControlInterfaceCount(out uint count);
            [PreserveSig] int GetControlInterface(uint index, out IntPtr control);
            [PreserveSig] int EnumPartsIncoming(out IntPtr parts);
            [PreserveSig] int EnumPartsOutgoing(out IntPtr parts);
            [PreserveSig] int GetTopologyObject(out IntPtr topology);
            [PreserveSig] int Activate(int clsContext, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object control);
        }

        [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IKsControl
        {
            [PreserveSig] int KsProperty(ref KSPROPERTY property, int propertyLength, IntPtr data, int dataLength, out int returned);
        }

        // Undocumented, but what Windows' own sound settings use to pick the main output (and every audio switcher app).
        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
            [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, bool useDefault, IntPtr format);
            [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, bool useDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
            [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
            [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
            [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
            [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, bool fxStore, IntPtr key, IntPtr value);
            [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, bool fxStore, IntPtr key, IntPtr value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, bool visible);
        }
    }
}
