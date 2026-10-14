using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CouchTV
{
    /// <summary>
    /// The Bluetooth screen (Settings &amp; power > Bluetooth), worked with the remote: the paired devices and how they
    /// are; adding one (it searches while open, and pairs with a press of OK); and for each device, connecting or
    /// disconnecting it, connecting automatically, playing with the TV speakers or on its own, and forgetting it.
    /// Switching Bluetooth on, too, as there are no Windows settings to reach in TV mode. BluetoothManager does the work.
    /// </summary>
    internal sealed partial class HomeWindow
    {
        enum BtView { Main, Device, Add }

        sealed class BtRow
        {
            public char Glyph;
            public string Title, Detail, Tag;
            public Color TagColor;
            public Action Act;
        }

        const int BtRowsShown = 6;

        BluetoothManager _bluetooth;
        FrameworkElement _bt;
        TextBlock _btTitle, _btStatus, _btIntro, _btMessage;
        Ellipse _btDot;
        StackPanel _btList;
        BtView _btView;
        int _btIndex, _btMainIndex;
        ulong _btDevice;                                   // the one the Device view is about
        bool _btForgetArmed;                               // OK once on Forget: press again to forget
        readonly List<BtDevice> _btFound = new List<BtDevice>();
        bool _btSearching;
        ulong _btPairing;                                  // being paired now, or 0
        bool? _btPower;                                    // Bluetooth switched on? (null: no adapter)
        List<BtRow> _btRows = new List<BtRow>();
        List<BluetoothManager.Device> _btPreviewDevices;   // screenshots only

        bool BluetoothOpen { get { return _bt != null && _bt.Visibility == Visibility.Visible; } }

        void StartBluetooth()
        {
            _bluetooth = new BluetoothManager();
            _bluetooth.Changed += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!BluetoothOpen) return;
                RefreshBluetoothPower();
                RenderBluetooth();
            }));
            _bluetooth.ConnectionChanged += (name, connected, together) => Dispatcher.BeginInvoke(new Action(() =>
            {
                string message = connected ? name + " connected" + (together ? ": playing with the TV speakers" : "") : name + " disconnected";
                if (BluetoothOpen) ShowBluetoothMessage(message, Theme.Good);
                else if (IsActive && !Modal) Toast(message);
                else Osd.ShowMessage('', message, 4);
            }));
            _bluetooth.Start();
        }

        List<BluetoothManager.Device> BtDevices { get { return _bluetooth != null ? _bluetooth.Devices : _btPreviewDevices ?? new List<BluetoothManager.Device>(); } }

        string BtRadio { get { return _bluetooth != null ? _bluetooth.Radio : _btPreviewDevices != null ? "Bluetooth adapter" : null; } }

        // ================================================================ building it

        FrameworkElement BuildBluetooth()
        {
            _btDot = new Ellipse { Width = 12, Height = 12, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            _btStatus = Theme.Text("", 22, Theme.Muted, FontWeights.Normal);
            var status = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            status.Children.Add(_btDot);
            status.Children.Add(_btStatus);
            _btTitle = Theme.Text("Bluetooth", 44, Theme.Ink, FontWeights.SemiBold, Theme.Display);
            _btTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            _btTitle.MaxWidth = 640;
            _btTitle.HorizontalAlignment = HorizontalAlignment.Left;
            var header = new Grid();
            header.Children.Add(_btTitle);
            header.Children.Add(status);

            _btIntro = Theme.Text("", 24, Theme.Muted, FontWeights.Normal);
            _btIntro.TextWrapping = TextWrapping.Wrap;
            _btIntro.Margin = new Thickness(0, 12, 0, 0);
            _btMessage = Theme.Text("", 24, Theme.Good, FontWeights.SemiBold);
            _btMessage.TextWrapping = TextWrapping.Wrap;
            _btMessage.Margin = new Thickness(0, 10, 0, 0);
            _btMessage.Visibility = Visibility.Collapsed;
            _btList = new StackPanel { Margin = new Thickness(0, 26, 0, 0) };

            var hints = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 22, 0, 0) };
            hints.Children.Add(KeyHint("↑ ↓", "Move"));
            hints.Children.Add(KeyHint("OK", "Choose"));
            hints.Children.Add(KeyHint("Back", "Back"));

            var column = new StackPanel();
            column.Children.Add(header);
            column.Children.Add(_btIntro);
            column.Children.Add(_btMessage);
            column.Children.Add(_btList);
            column.Children.Add(hints);

            var overlay = new Grid { Background = Theme.Brush(Color.FromArgb(0xE6, 8, 10, 14)), Visibility = Visibility.Collapsed };
            overlay.Children.Add(new Border
            {
                Width = 1180, CornerRadius = new CornerRadius(28), Padding = new Thickness(64, 52, 64, 46),
                Background = Theme.Brush(Theme.Panel), BorderBrush = Theme.Brush(Color.FromArgb(0x22, 255, 255, 255)),
                BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Child = column,
            });
            return overlay;
        }

        void OpenBluetooth()
        {
            _btView = BtView.Main;
            _btIndex = _btMainIndex = 0;
            _btForgetArmed = false;
            ShowBluetoothMessage(null, Theme.Good);
            RefreshBluetoothPower();
            RenderBluetooth();
            _bt.Visibility = Visibility.Visible;
            if (_bluetooth != null) _bluetooth.Kick();
        }

        void CloseBluetooth()
        {
            _btSearching = false;
            _bt.Visibility = Visibility.Collapsed;
        }

        // ================================================================ the remote's keys

        void BluetoothKey(Key key)
        {
            switch (key)
            {
                case Key.Up: MoveBluetooth(-1); break;
                case Key.Down: MoveBluetooth(1); break;
                case Key.Enter:
                case Key.Space:
                    if (_btIndex >= 0 && _btIndex < _btRows.Count && _btRows[_btIndex].Act != null) _btRows[_btIndex].Act();
                    break;
                case Key.Escape:
                case Key.BrowserBack:
                case Key.Back:
                    BluetoothBack();
                    break;
            }
        }

        void MoveBluetooth(int step)
        {
            int next = Math.Max(0, Math.Min(_btRows.Count - 1, _btIndex + step));
            if (next == _btIndex) return;
            _btIndex = next;
            _btForgetArmed = false;
            RenderBluetooth();
        }

        void BluetoothBack()
        {
            if (_btView == BtView.Main)
            {
                CloseBluetooth();
                return;
            }
            _btSearching = false;
            _btView = BtView.Main;
            _btIndex = _btMainIndex;
            _btForgetArmed = false;
            RenderBluetooth();
        }

        // ================================================================ what's shown

        void RenderBluetooth()
        {
            if (_bt == null) return;
            var rows = new List<BtRow>();
            string radio = BtRadio;
            List<BluetoothManager.Device> devices = BtDevices;
            bool on = radio != null;

            _btDot.Fill = Theme.Brush(on ? Theme.Good : Theme.Bad);
            _btStatus.Text = on ? radio : _btPower == false ? "Bluetooth is off" : "No Bluetooth adapter";
            _btTitle.Text = "Bluetooth";

            switch (_btView)
            {
                case BtView.Main:
                    _btIntro.Text = "Headphones and speakers play along with the TV speakers, and connect by themselves when they're switched on nearby.";
                    if (!on && _btPower == false)
                        rows.Add(new BtRow { Glyph = '', Title = "Turn Bluetooth on", Detail = "It's switched off on this PC", Act = () => SwitchBluetooth(true) });
                    else if (!on)
                        rows.Add(new BtRow { Glyph = '', Title = "No Bluetooth adapter found", Detail = "Plug the Bluetooth dongle into the PC: this updates by itself", Act = null });
                    else
                        rows.Add(new BtRow { Glyph = '', Title = "Add a device", Detail = "Headphones, speakers, keyboards, game controllers", Act = OpenBluetoothAdd });
                    foreach (BluetoothManager.Device d in devices)
                    {
                        ulong address = d.Address;
                        rows.Add(new BtRow
                        {
                            Glyph = BtGlyph(d.Kind), Title = d.Name, Detail = d.Note,
                            Tag = d.Connected ? "Connected" : null, TagColor = Theme.Good,
                            Act = () => OpenBluetoothDevice(address),
                        });
                    }
                    if (on) rows.Add(new BtRow { Glyph = '', Title = "Turn Bluetooth off", Detail = "Until you turn it on again here", Act = () => SwitchBluetooth(false) });
                    break;

                case BtView.Device:
                    BluetoothManager.Device device = devices.Find(x => x.Address == _btDevice);
                    if (device == null)
                    {
                        BluetoothBack();
                        return;
                    }
                    ulong a = device.Address;
                    _btTitle.Text = device.Name;
                    _btIntro.Text = device.Note;
                    if (device.Audio)
                    {
                        if (device.Connected)
                            rows.Add(new BtRow { Glyph = '', Title = "Disconnect", Detail = "It stays disconnected until you connect it here", Act = () => BtWork(() => _bluetooth.Disconnect(a)) });
                        else
                            rows.Add(new BtRow { Glyph = '', Title = "Connect", Detail = "Switch it on first; it takes a few seconds", Act = () => BtWork(() => _bluetooth.Connect(a)) });
                        bool auto = device.AutoConnect, together = device.Together;
                        rows.Add(new BtRow
                        {
                            Glyph = '', Title = "Connect automatically", Detail = "When it's switched on near the TV, and when the TV wakes",
                            Tag = auto ? "On" : "Off", TagColor = auto ? Theme.Good : Theme.Muted,
                            Act = () => BtWork(() => _bluetooth.SetAutoConnect(a, !auto)),
                        });
                        rows.Add(new BtRow
                        {
                            Glyph = '', Title = "Plays",
                            Detail = together ? "Together with the TV speakers, so the room hears the TV too" : "On its own: the TV speakers go quiet (a little less delay)",
                            Tag = together ? "With the TV" : "On its own", TagColor = Theme.Ink,
                            Act = () => BtWork(() => _bluetooth.SetTogether(a, !together)),
                        });
                    }
                    rows.Add(new BtRow
                    {
                        Glyph = '', Title = "Forget this device",
                        Detail = _btForgetArmed ? "Press OK again to forget it" : "To use it again, pair it again",
                        Tag = _btForgetArmed ? "Sure?" : null, TagColor = Theme.Warn, Act = () => ForgetBluetooth(a),
                    });
                    break;

                case BtView.Add:
                    _btTitle.Text = "Add a device";
                    _btIntro.Text = "Put your headphones or speaker in pairing mode: usually, hold its power or Bluetooth button until its light flashes. " +
                                    (_btSearching ? "Looking…" : "");
                    foreach (BtDevice found in _btFound)
                    {
                        BtDevice f = found;
                        rows.Add(new BtRow
                        {
                            Glyph = BtGlyph(f.Kind), Title = f.Label,
                            Detail = _btPairing == f.Address ? "Pairing…" : BtKindName(f.Kind) + " · OK to pair",
                            Act = _btPairing == 0 ? (Action)(() => PairBluetooth(f)) : null,
                        });
                    }
                    if (rows.Count == 0) rows.Add(new BtRow { Glyph = '', Title = _btSearching ? "Looking for devices…" : "Nothing found yet", Detail = "It keeps looking while this is open", Act = null });
                    break;
            }

            _btRows = rows;
            _btIndex = Math.Max(0, Math.Min(_btIndex, rows.Count - 1));
            _btList.Children.Clear();
            int first = Math.Max(0, Math.Min(_btIndex - 2, rows.Count - BtRowsShown));
            for (int i = first; i < rows.Count && i < first + BtRowsShown; i++) _btList.Children.Add(BtRowView(rows[i], i));
        }

        UIElement BtRowView(BtRow row, int index)
        {
            bool focused = index == _btIndex;
            Color ink = focused ? Theme.Base : Theme.Ink, muted = focused ? Theme.Hex("#4A5263") : Theme.Muted;
            var glyph = new TextBlock { Text = row.Glyph.ToString(), FontFamily = Theme.Icons, FontSize = 34, Foreground = Theme.Brush(ink), Width = 64, VerticalAlignment = VerticalAlignment.Center };
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock title = Theme.Text(row.Title, 28, ink, FontWeights.SemiBold);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            texts.Children.Add(title);
            if (!string.IsNullOrEmpty(row.Detail))
            {
                TextBlock detail = Theme.Text(row.Detail, 21, muted, FontWeights.Normal);
                detail.TextTrimming = TextTrimming.CharacterEllipsis;
                texts.Children.Add(detail);
            }
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(texts, 1);
            grid.Children.Add(glyph);
            grid.Children.Add(texts);
            if (!string.IsNullOrEmpty(row.Tag))
            {
                TextBlock tag = Theme.Text(row.Tag, 22, focused ? Theme.Base : row.TagColor, FontWeights.SemiBold);
                tag.VerticalAlignment = VerticalAlignment.Center;
                tag.Margin = new Thickness(20, 0, 0, 0);
                Grid.SetColumn(tag, 2);
                grid.Children.Add(tag);
            }
            var view = new Border
            {
                CornerRadius = new CornerRadius(18), Padding = new Thickness(26, 14, 28, 16), Margin = new Thickness(0, 0, 0, 10),
                Background = Theme.Brush(focused ? Colors.White : Theme.Hex("#1F2633")), Child = grid, Cursor = Cursors.Hand,
            };
            view.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                _btIndex = index;
                RenderBluetooth();
                if (index < _btRows.Count && _btRows[index].Act != null) _btRows[index].Act();
            };
            return view;
        }

        void ShowBluetoothMessage(string text, Color color)
        {
            if (_btMessage == null) return;
            _btMessage.Text = text ?? "";
            _btMessage.Foreground = Theme.Brush(color);
            _btMessage.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        static char BtGlyph(BtKind kind)
        {
            switch (kind)
            {
                case BtKind.Headphones: case BtKind.Audio: return '';
                case BtKind.Speaker: return '';
                case BtKind.Phone: return '';
                case BtKind.Keyboard: return '';
                case BtKind.Mouse: return '';
                case BtKind.Gamepad: return '';
                default: return '';
            }
        }

        static string BtKindName(BtKind kind)
        {
            switch (kind)
            {
                case BtKind.Headphones: return "Headphones";
                case BtKind.Speaker: return "Speaker";
                case BtKind.Audio: return "Audio";
                case BtKind.Phone: return "Phone";
                case BtKind.Computer: return "Computer";
                case BtKind.Keyboard: return "Keyboard";
                case BtKind.Mouse: return "Mouse";
                case BtKind.Gamepad: return "Game controller";
                default: return "Device";
            }
        }

        // ================================================================ doing things (Bluetooth calls block: in the background)

        void BtWork(Action work, Action then = null)
        {
            ThreadPool.QueueUserWorkItem(state =>
            {
                try { if (_bluetooth != null) work(); }
                catch (Exception ex) { Log.Error("Bluetooth", ex); }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (then != null) then();
                    if (BluetoothOpen) RenderBluetooth();
                }));
            });
        }

        void RefreshBluetoothPower()
        {
            bool? power = null;
            ThreadPool.QueueUserWorkItem(state =>
            {
                power = BluetoothPower.IsOn();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (power == _btPower) return;
                    _btPower = power;
                    if (BluetoothOpen) RenderBluetooth();
                }));
            });
        }

        void SwitchBluetooth(bool on)
        {
            ShowBluetoothMessage(on ? "Turning Bluetooth on…" : "Turning Bluetooth off…", Theme.Muted);
            bool ok = false;
            BtWork(() => ok = BluetoothPower.Set(on), () =>
            {
                ShowBluetoothMessage(ok ? null : "Windows didn't let CouchTV switch Bluetooth " + (on ? "on" : "off") + ".", Theme.Warn);
                RefreshBluetoothPower();
                _bluetooth.Kick();
            });
        }

        void OpenBluetoothDevice(ulong address)
        {
            _btMainIndex = _btIndex;
            _btView = BtView.Device;
            _btDevice = address;
            _btIndex = 0;
            _btForgetArmed = false;
            ShowBluetoothMessage(null, Theme.Good);
            RenderBluetooth();
        }

        void ForgetBluetooth(ulong address)
        {
            if (!_btForgetArmed)
            {
                _btForgetArmed = true;
                RenderBluetooth();
                return;
            }
            _btForgetArmed = false;
            string name = _btTitle.Text;
            bool ok = false;
            BtWork(() => ok = _bluetooth.Forget(address), () =>
            {
                ShowBluetoothMessage(ok ? "Forgot " + name : "Couldn't forget " + name + ". Try again.", ok ? Theme.Good : Theme.Warn);
                if (ok) BluetoothBack();
            });
        }

        void OpenBluetoothAdd()
        {
            _btMainIndex = _btIndex;
            _btView = BtView.Add;
            _btIndex = 0;
            _btFound.Clear();
            ShowBluetoothMessage(null, Theme.Good);
            RenderBluetooth();
            if (!_btSearching) SearchBluetooth();
        }

        /// <summary>Looks again and again (about 8 s a time) while the Add view is open, except while pairing.</summary>
        void SearchBluetooth()
        {
            _btSearching = true;
            List<BtDevice> found = null;
            BtWork(() => found = _bluetooth.Search(), () =>
            {
                if (found != null)
                    foreach (BtDevice d in found)
                    {
                        BtDevice known = _btFound.Find(x => x.Address == d.Address);
                        if (known == null) _btFound.Add(d);
                        else if (d.Name.Length > 0) known.Name = d.Name;   // names can arrive a round later
                    }
                if (BluetoothOpen && _btView == BtView.Add && _btSearching)
                {
                    if (_btPairing == 0) SearchBluetooth();
                    else _btSearching = false;
                }
                else _btSearching = false;
            });
        }

        void PairBluetooth(BtDevice device)
        {
            _btPairing = device.Address;
            ShowBluetoothMessage("Pairing with " + device.Label + "…", Theme.Muted);
            RenderBluetooth();
            string problem = null;
            BtWork(() => problem = _bluetooth.Pair(device), () =>
            {
                _btPairing = 0;
                if (problem == null)
                {
                    _btFound.RemoveAll(x => x.Address == device.Address);
                    _btSearching = false;
                    _btView = BtView.Main;
                    _btIndex = 1;
                    ShowBluetoothMessage("Paired " + device.Label + (device.IsAudio ? ": it's connecting" : ""), Theme.Good);
                    _bluetooth.Kick();
                }
                else
                {
                    ShowBluetoothMessage(problem, Theme.Warn);
                    if (BluetoothOpen && _btView == BtView.Add && !_btSearching) SearchBluetooth();
                }
            });
        }

        /// <summary>For screenshots: the screen with made-up devices.</summary>
        void PreviewBluetooth(string view)
        {
            _btPreviewDevices = new List<BluetoothManager.Device>
            {
                new BluetoothManager.Device { Address = 1, Name = "WH-1000XM4", Kind = BtKind.Headphones, Audio = true, Connected = true, Playing = true, Together = true, AutoConnect = true, Note = "Connected · playing with the TV speakers" },
                new BluetoothManager.Device { Address = 2, Name = "JBL Flip 5", Kind = BtKind.Speaker, Audio = true, AutoConnect = true, Together = true, Note = "Not connected · it connects by itself when it's on and nearby" },
                new BluetoothManager.Device { Address = 3, Name = "Xbox Wireless Controller", Kind = BtKind.Gamepad, Note = "Not connected · it connects when you use it" },
            };
            _btPower = true;
            OpenBluetooth();
            if (view == "device") OpenBluetoothDevice(1);
            if (view == "add")
            {
                _btView = BtView.Add;
                _btFound.Add(new BtDevice { Address = 0x112233445566, Name = "boAt Rockerz 450", ClassOfDevice = 0x240418 });
                _btFound.Add(new BtDevice { Address = 0x665544332211, Name = "", ClassOfDevice = 0x240414 });
                _btSearching = true;
                RenderBluetooth();
            }
        }
    }
}
