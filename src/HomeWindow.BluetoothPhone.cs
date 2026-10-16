using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace CouchTV
{
    // The TV's Bluetooth on the phone remote (its Settings > Bluetooth): while a phone has that page open it's told how
    // Bluetooth is (switched on, the paired devices and how each is, what a search has found) whenever that changes,
    // and it can do what the TV's own Bluetooth screen does. "BT" in ir-receiver/PROTOCOL.md.
    internal sealed partial class HomeWindow
    {
        volatile bool _btWatched;           // a phone has its Bluetooth page open
        volatile bool _btPhoneSearch;       // ...and its Add page: keep searching
        bool _btPhoneSearching;             // a search for it is under way (UI thread)
        ulong _btPhonePairing;              // being paired for a phone, or 0 (UI thread)
        readonly List<BtDevice> _btPhoneFound = new List<BtDevice>();   // what the phone's searches found (locked)

        /// <summary>Wires the phone's Bluetooth page to the manager. After StartBluetooth, with the Wi-Fi remote running.</summary>
        void StartBluetoothForPhones()
        {
            if (_wifi == null || _bluetooth == null) return;
            _wifi.BluetoothWatch += any =>
            {
                _btWatched = any;
                if (!any) StopPhoneSearch();
                else
                {
                    _bluetooth.Kick();
                    TellPhonesBluetooth();
                }
            };
            _wifi.BluetoothAsk += PhoneBluetooth;
        }

        /// <summary>A phone asked for something on its Bluetooth page. UI thread.</summary>
        void PhoneBluetooth(string[] ask)
        {
            if (_bluetooth == null || ask.Length == 0) return;
            string what = ask[0];
            ulong address = 0;
            if (ask.Length > 1) ulong.TryParse(ask[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
            bool on = ask.Length > 2 ? ask[2] == "ON" : ask.Length > 1 && ask[1] == "ON";
            BluetoothManager.Device device = address != 0 ? _bluetooth.Devices.Find(d => d.Address == address) : null;
            string name = device != null ? device.Name : null;
            switch (what)
            {
                case "POWER":
                    TellPhonesBluetooth("muted", on ? "Turning Bluetooth on…" : "Turning Bluetooth off…");
                    bool switched = false;
                    PhoneBtWork(() => switched = BluetoothPower.Set(on), () =>
                    {
                        TellPhonesBluetooth(switched ? null : "warn", switched ? null : "Windows didn't let CouchTV switch Bluetooth " + (on ? "on" : "off") + ".");
                        _bluetooth.Kick();
                        RefreshBluetoothPower();
                    });
                    break;
                case "SEARCH":
                    if (on)
                    {
                        _btPhoneSearch = true;
                        if (!_btPhoneSearching) PhoneSearch();
                        TellPhonesBluetooth();
                    }
                    else StopPhoneSearch();
                    break;
                case "PAIR":
                    BtDevice found;
                    lock (_btPhoneFound) found = _btPhoneFound.Find(d => d.Address == address);
                    if (found == null || _btPhonePairing != 0) return;
                    _btPhonePairing = address;
                    TellPhonesBluetooth("muted", "Pairing with " + found.Label + "…");
                    string problem = null;
                    PhoneBtWork(() => problem = _bluetooth.Pair(found), () =>
                    {
                        _btPhonePairing = 0;
                        if (problem == null) lock (_btPhoneFound) _btPhoneFound.RemoveAll(d => d.Address == address);
                        TellPhonesBluetooth(problem == null ? "good" : "warn",
                            problem ?? "Paired " + found.Label + (found.IsAudio ? ": it's connecting" : ""));
                        _bluetooth.Kick();
                        if (_btPhoneSearch && !_btPhoneSearching) PhoneSearch();
                    });
                    break;
                case "CONNECT":
                case "DISCONNECT":
                    if (device == null) return;
                    bool connect = what == "CONNECT";
                    TellPhonesBluetooth("muted", (connect ? "Connecting " : "Disconnecting ") + name + "…");
                    PhoneBtWork(() =>
                    {
                        if (connect) _bluetooth.Connect(address);
                        else _bluetooth.Disconnect(address);
                    }, () => TellPhonesBluetooth(null, null));
                    break;
                case "FORGET":
                    if (device == null) return;
                    bool forgot = false;
                    PhoneBtWork(() => forgot = _bluetooth.Forget(address), () =>
                        TellPhonesBluetooth(forgot ? "good" : "warn", forgot ? "Forgot " + name : "Couldn't forget " + name + ". Try again."));
                    break;
                case "AUTO":
                    if (device == null) return;
                    PhoneBtWork(() => _bluetooth.SetAutoConnect(address, on), () => TellPhonesBluetooth());
                    break;
                case "TOGETHER":
                    if (device == null) return;
                    PhoneBtWork(() => _bluetooth.SetTogether(address, on), () => TellPhonesBluetooth());
                    break;
            }
        }

        /// <summary>Looks again and again (about 8 s a time) while a phone's Add page is open, except while pairing.</summary>
        void PhoneSearch()
        {
            _btPhoneSearching = true;
            List<BtDevice> found = null;
            PhoneBtWork(() => found = _bluetooth.Search(), () =>
            {
                if (found != null)
                    lock (_btPhoneFound)
                        foreach (BtDevice d in found)
                        {
                            BtDevice known = _btPhoneFound.Find(x => x.Address == d.Address);
                            if (known == null) _btPhoneFound.Add(d);
                            else if (d.Name.Length > 0) known.Name = d.Name;   // names can arrive a round later
                        }
                if (_btPhoneSearch && _btWatched && _btPhonePairing == 0) PhoneSearch();
                else _btPhoneSearching = false;
                TellPhonesBluetooth();
            });
        }

        void StopPhoneSearch()
        {
            _btPhoneSearch = false;
            lock (_btPhoneFound) _btPhoneFound.Clear();
            if (_btWatched) TellPhonesBluetooth();
        }

        /// <summary>Bluetooth calls block: in the background, then [then] on the UI thread (the TV's own screen redrawn too).</summary>
        void PhoneBtWork(Action work, Action then)
        {
            ThreadPool.QueueUserWorkItem(state =>
            {
                try { work(); }
                catch (Exception ex) { Log.Error("Bluetooth (phone)", ex); }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    then();
                    if (BluetoothOpen) RenderBluetooth();
                }));
            });
        }

        /// <summary>
        /// Tells the phones how Bluetooth is now, and with [kind] (good, warn, muted) a line to show; "-" for kind
        /// clears the line, null leaves it. From any thread (finding out whether it's switched on blocks: in the background).
        /// </summary>
        void TellPhonesBluetooth(string kind = "", string text = null)
        {
            if (_wifi == null || _bluetooth == null || !_btWatched) return;
            bool searching = _btPhoneSearch;
            ThreadPool.QueueUserWorkItem(state =>
            {
                var lines = new List<string>();
                if (kind == null) lines.Add("BTSAYS - -");
                else if (kind.Length > 0) lines.Add("BTSAYS " + kind + " " + B64(text));
                lines.AddRange(BluetoothLines(BluetoothPower.IsOn(), searching));
                _wifi.SendToPhones(lines);
            });
        }

        List<string> BluetoothLines(bool? power, bool searching)
        {
            var lines = new List<string>();
            string radio = _bluetooth.Radio;
            lines.Add("BT " + (power == null ? "none" : power.Value ? "on" : "off") + " " + (string.IsNullOrEmpty(radio) ? "-" : B64(radio)) + " " + (searching ? 1 : 0));
            List<BluetoothManager.Device> devices = _bluetooth.Devices;
            lines.Add("BTDEVICES " + devices.Count);
            foreach (BluetoothManager.Device d in devices)
            {
                string flags = (d.Audio ? "A" : "") + (d.Connected ? "C" : "") + (d.Playing ? "P" : "") + (d.Together ? "T" : "") + (d.AutoConnect ? "U" : "");
                lines.Add("BTDEVICE " + d.Address.ToString("X12") + " " + B64(d.Name) + " " + d.Kind.ToString().ToLowerInvariant() + " " +
                          (flags.Length > 0 ? flags : "-") + " " + (string.IsNullOrEmpty(d.Note) ? "-" : B64(d.Note)));
            }
            List<BtDevice> found;
            lock (_btPhoneFound) found = new List<BtDevice>(_btPhoneFound);
            lines.Add("BTFOUND " + found.Count);
            foreach (BtDevice f in found)
                lines.Add("BTNEW " + f.Address.ToString("X12") + " " + B64(f.Label) + " " + f.Kind.ToString().ToLowerInvariant());
            return lines;
        }

        static string B64(string text) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? "")); }
    }
}
