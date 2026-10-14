using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace CouchTV
{
    /// <summary>
    /// Looks after Bluetooth for the TV, every couple of seconds in the background:
    ///   - Headphones and speakers that connect play along with the TV speakers ("together", the default): Windows would
    ///     switch everything to them, so the main output is put back on the TV and they get a copy (AudioCopy). Set to
    ///     play on their own, they become the main output instead (the TV speakers go quiet; a little less delay).
    ///   - Paired audio devices set to connect automatically (the default) are asked to connect when CouchTV starts,
    ///     when the PC wakes, and every 30 seconds while they aren't connected, so headphones switched on near the TV
    ///     connect by themselves. One you disconnect here stays disconnected until you connect it again.
    /// The Bluetooth screen (HomeWindow.Bluetooth.cs) shows this and drives it. Choices are kept in
    /// %LOCALAPPDATA%\CouchTV\bluetooth.ini.
    /// </summary>
    internal sealed class BluetoothManager
    {
        const int RetrySeconds = 30, ConnectingSeconds = 15;

        /// <summary>A paired device, as the Bluetooth screen shows it.</summary>
        public sealed class Device
        {
            public ulong Address;
            public string Name;
            public BtKind Kind;
            public bool Audio, Connected, Playing, Together, AutoConnect;
            public string Output;   // its stereo output, once Windows has made one (audio devices)
            public string Note;     // how it is, in words
        }

        sealed class Choice
        {
            public bool Auto = true, Together = true;
        }

        /// <summary>The list or a device's state changed. On a background thread.</summary>
        public event Action Changed;

        /// <summary>A device connected (true) or went (false): its name, and whether it plays with the TV speakers. Background thread.</summary>
        public event Action<string, bool, bool> ConnectionChanged;

        readonly object _lock = new object();
        readonly string _path = Path.Combine(Config.DataDir, "bluetooth.ini");
        readonly Dictionary<ulong, Choice> _choices = new Dictionary<ulong, Choice>();
        readonly Dictionary<string, AudioCopy> _copies = new Dictionary<string, AudioCopy>();
        readonly Dictionary<ulong, DateTime> _tried = new Dictionary<ulong, DateTime>();
        readonly HashSet<ulong> _userDisconnected = new HashSet<ulong>();
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        List<Device> _devices = new List<Device>();
        HashSet<ulong> _connected;   // null until the first look (no "connected" news for what's already on at start)
        string _tvOutput, _radio, _shown = "";
        volatile bool _stop, _kick = true;

        /// <summary>The Bluetooth adapter's name; null when there's none (the dongle's out).</summary>
        public string Radio { get { lock (_lock) return _radio; } }

        public List<Device> Devices { get { lock (_lock) return new List<Device>(_devices); } }

        public void Start()
        {
            Load();
            new Thread(Run) { IsBackground = true, Name = "Bluetooth" }.Start();
        }

        public void Stop()
        {
            _stop = true;
            _wake.Set();
            lock (_lock)
            {
                foreach (AudioCopy copy in _copies.Values) copy.Stop();
                _copies.Clear();
            }
        }

        /// <summary>Look now, and try connecting the automatic ones at once (the PC woke, the screen opened).</summary>
        public void Kick()
        {
            _kick = true;
            _wake.Set();
        }

        void Run()
        {
            while (!_stop)
            {
                try { Look(); }
                catch (Exception ex) { Log.Error("Bluetooth", ex); }
                _wake.WaitOne(2000);
            }
        }

        // ---------------------------------------------------------------- what the screen asks for (blocking: call off the UI thread)

        public void Connect(ulong address)
        {
            Device d = Find(address);
            if (d == null || d.Output == null) return;
            lock (_lock)
            {
                _userDisconnected.Remove(address);
                _tried[address] = DateTime.Now;
            }
            AudioOutputs.BtConnect(d.Output, true);
            _wake.Set();
        }

        public void Disconnect(ulong address)
        {
            Device d = Find(address);
            if (d == null || d.Output == null) return;
            lock (_lock) _userDisconnected.Add(address);
            AudioOutputs.BtConnect(d.Output, false);
            _wake.Set();
        }

        public bool Forget(ulong address)
        {
            Device d = Find(address);
            if (d != null && d.Output != null && d.Connected) AudioOutputs.BtConnect(d.Output, false);
            bool ok = Bluetooth.Forget(address) == 0;
            lock (_lock)
            {
                _choices.Remove(address);
                _tried.Remove(address);
                Save();
            }
            _wake.Set();
            return ok;
        }

        public void SetAutoConnect(ulong address, bool on)
        {
            lock (_lock)
            {
                ChoiceFor(address).Auto = on;
                if (on) _userDisconnected.Remove(address);
                Save();
            }
            _wake.Set();
        }

        public void SetTogether(ulong address, bool together)
        {
            lock (_lock)
            {
                ChoiceFor(address).Together = together;
                Save();
            }
            _wake.Set();
        }

        /// <summary>One look for devices in pairing mode nearby (about 8 seconds): the ones not paired yet.</summary>
        public List<BtDevice> Search()
        {
            return Bluetooth.Devices(true, 8).FindAll(d => !d.Paired);
        }

        /// <summary>
        /// Pairs with a device, then for headphones and speakers waits (up to 25 s) for Windows to set up its sound
        /// output and connects it. Null when done, else what went wrong, in words.
        /// </summary>
        public string Pair(BtDevice device)
        {
            int result = Bluetooth.Pair(device);
            if (result != 0)
                return result == 1223 ? "Pairing was cancelled. Put it in pairing mode and try again."
                     : result == 1244 || result == 1326 ? "It didn't accept the pairing. Put it in pairing mode again and retry."
                     : "Pairing didn't work (error " + result + "). Put it in pairing mode again and retry.";
            lock (_lock)
            {
                ChoiceFor(device.Address);
                _userDisconnected.Remove(device.Address);
                Save();
            }
            if (!device.IsAudio)
            {
                _wake.Set();
                return null;
            }
            for (int i = 0; i < 25 && !_stop; i++)
            {
                AudioOutput output = AudioOutputs.All().Find(o => o.BtAddress == device.Address && !o.HandsFree);
                if (output != null)
                {
                    if (!output.Active) AudioOutputs.BtConnect(output.Id, true);
                    break;
                }
                Thread.Sleep(1000);
            }
            _wake.Set();
            return null;
        }

        Device Find(ulong address)
        {
            lock (_lock) return _devices.Find(d => d.Address == address);
        }

        Choice ChoiceFor(ulong address)
        {
            Choice c;
            if (!_choices.TryGetValue(address, out c)) _choices[address] = c = new Choice();
            return c;
        }

        // ---------------------------------------------------------------- the look, every 2 seconds

        void Look()
        {
            string radio = Bluetooth.RadioName();
            List<BtDevice> paired = radio != null ? Bluetooth.Devices(false).FindAll(d => d.Paired) : new List<BtDevice>();
            List<AudioOutput> outputs = AudioOutputs.All();
            string main = AudioOutputs.DefaultId();
            AudioOutput mainOutput = outputs.Find(o => o.Id == main);
            DateTime now = DateTime.Now;
            bool kick = _kick;
            _kick = false;

            // The TV's output: whatever's the main output when it isn't Bluetooth (HDMI, usually).
            if (mainOutput != null && mainOutput.Active && mainOutput.BtAddress == 0 && main != _tvOutput)
            {
                lock (_lock)
                {
                    _tvOutput = main;
                    Save();
                }
            }
            AudioOutput tv = outputs.Find(o => o.Id == _tvOutput && o.Active) ?? outputs.Find(o => o.Active && o.BtAddress == 0);

            var devices = new List<Device>();
            var copyTo = new Dictionary<string, string>();   // output -> device name
            var connectedNow = new HashSet<ulong>();
            var news = new List<KeyValuePair<Device, bool>>();
            foreach (BtDevice b in paired)
            {
                Choice choice;
                bool userDisconnected;
                DateTime tried;
                bool triedBefore;
                lock (_lock)
                {
                    choice = ChoiceFor(b.Address);
                    userDisconnected = _userDisconnected.Contains(b.Address);
                    triedBefore = _tried.TryGetValue(b.Address, out tried);
                }
                AudioOutput output = outputs.Find(o => o.BtAddress == b.Address && !o.HandsFree);
                var d = new Device
                {
                    Address = b.Address, Name = b.Label, Kind = b.Kind, Audio = b.IsAudio || output != null,
                    Output = output != null ? output.Id : null, Together = choice.Together, AutoConnect = choice.Auto,
                    Connected = output != null ? output.Active : b.Connected,
                };
                if (d.Connected) connectedNow.Add(d.Address);

                if (d.Audio && d.Connected && output != null)
                {
                    if (choice.Together && tv != null)
                    {
                        // Windows made it the main output as it connected: give that back to the TV, and copy to it instead.
                        if (main == output.Id && AudioOutputs.SetDefault(tv.Id))
                        {
                            Log.Info("Bluetooth: " + d.Name + " plays along with " + tv.Name);
                            main = tv.Id;
                        }
                        if (main != output.Id)
                        {
                            copyTo[output.Id] = d.Name;
                            d.Playing = true;
                        }
                    }
                    else
                    {
                        if (main != output.Id && AudioOutputs.SetDefault(output.Id)) main = output.Id;
                        d.Playing = main == output.Id;
                    }
                }

                // Connect the automatic ones when they're switched on near the TV: ask now and then while they aren't.
                if (d.Audio && !d.Connected && output != null && choice.Auto && !userDisconnected &&
                    (kick || !triedBefore || (now - tried).TotalSeconds >= RetrySeconds))
                {
                    lock (_lock) _tried[d.Address] = now;
                    tried = now;
                    triedBefore = true;
                    AudioOutputs.BtConnect(output.Id, true);
                }

                d.Note = Note(d, userDisconnected, triedBefore ? (now - tried).TotalSeconds : double.MaxValue);
                devices.Add(d);
                if (_connected != null && d.Connected != _connected.Contains(d.Address)) news.Add(new KeyValuePair<Device, bool>(d, d.Connected));
            }

            // One that played on its own went away: the main output goes back to the TV.
            if ((mainOutput == null || !mainOutput.Active) && tv != null && main != tv.Id) AudioOutputs.SetDefault(tv.Id);

            lock (_lock)
            {
                foreach (KeyValuePair<string, string> want in copyTo)
                {
                    if (_copies.ContainsKey(want.Key)) continue;
                    var copy = new AudioCopy(want.Key, want.Value);
                    _copies[want.Key] = copy;
                    copy.Start();
                }
                foreach (string id in new List<string>(_copies.Keys))
                {
                    if (copyTo.ContainsKey(id)) continue;
                    _copies[id].Stop();
                    _copies.Remove(id);
                }
                _radio = radio;
                _devices = devices;
                _connected = connectedNow;
            }

            foreach (KeyValuePair<Device, bool> n in news)
            {
                Action<string, bool, bool> handler = ConnectionChanged;
                if (handler != null) handler(n.Key.Name, n.Value, n.Key.Audio && n.Key.Together && n.Key.Playing);
            }
            var shown = new StringBuilder(radio ?? "-");
            foreach (Device d in devices) shown.Append('|').Append(d.Address).Append(d.Name).Append(d.Note).Append(d.Together).Append(d.AutoConnect);
            if (shown.ToString() != _shown)
            {
                _shown = shown.ToString();
                Action handler = Changed;
                if (handler != null) handler();
            }
        }

        static string Note(Device d, bool userDisconnected, double sinceTried)
        {
            if (d.Connected)
            {
                if (!d.Audio) return "Connected";
                if (d.Output == null) return "Connected · setting up its sound…";
                if (!d.Playing) return "Connected";
                return d.Together ? "Connected · playing with the TV speakers" : "Connected · playing on its own (TV speakers off)";
            }
            if (!d.Audio) return "Not connected · it connects when you use it";
            if (d.Output == null) return "Not connected · setting up its sound…";
            if (sinceTried < ConnectingSeconds) return "Connecting…";
            if (userDisconnected) return "Disconnected";
            if (d.AutoConnect) return "Not connected · it connects by itself when it's on and nearby";
            return "Not connected";
        }

        // ---------------------------------------------------------------- choices, kept in bluetooth.ini

        // "tv <output id>", then "device <address hex> auto=1 together=1" lines.
        void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                foreach (string raw in File.ReadAllLines(_path))
                {
                    string[] p = raw.Trim().Split(' ');
                    if (p.Length == 2 && p[0] == "tv") _tvOutput = p[1];
                    else if (p.Length >= 2 && p[0] == "device")
                    {
                        ulong address;
                        if (!ulong.TryParse(p[1], System.Globalization.NumberStyles.HexNumber, null, out address)) continue;
                        var c = new Choice();
                        foreach (string setting in p)
                        {
                            if (setting == "auto=0") c.Auto = false;
                            if (setting == "together=0") c.Together = false;
                        }
                        _choices[address] = c;
                    }
                }
            }
            catch (Exception ex) { Log.Info("Bluetooth: couldn't read " + _path + ": " + ex.Message); }
        }

        /// <summary>Under _lock.</summary>
        void Save()
        {
            var text = new StringBuilder();
            if (_tvOutput != null) text.Append("tv ").Append(_tvOutput).Append('\n');
            foreach (KeyValuePair<ulong, Choice> c in _choices)
                text.Append("device ").Append(c.Key.ToString("X12")).Append(c.Value.Auto ? " auto=1" : " auto=0")
                    .Append(c.Value.Together ? " together=1" : " together=0").Append('\n');
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, text.ToString());
            }
            catch (Exception ex) { Log.Info("Bluetooth: couldn't save " + _path + ": " + ex.Message); }
        }
    }
}
