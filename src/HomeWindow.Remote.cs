using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Threading;
using Microsoft.Win32;

namespace CouchTV
{
    /// <summary>Infrared remotes: acting on buttons from the CouchIR receiver, and the Remote setup screen.</summary>
    internal sealed partial class HomeWindow
    {
        sealed class SetupStep
        {
            public string Action, Label;
            public char Glyph;
            public bool Optional;
        }

        IrLink _ir;
        RemoteServer _wifi;
        PowerCountdownWindow _powerCountdown;
        PhoneStore _phones;
        TypingWatch _typing;
        AudioShare _sound;
        RemoteMap _remote;

        // The button currently being pressed. Remotes repeat their signal while a button is held.
        string _irCode, _irAction;
        DateTime _irFirst, _irLast, _irLastStep, _irHintShown;
        bool _irHoldDone;
        DispatcherTimer _irPointer;

        // Remote setup screen
        FrameworkElement _setup;
        TextBlock _setupStatus, _setupGlyph, _setupLabel, _setupStep, _setupFeedback;
        Ellipse _setupDot;
        List<SetupStep> _steps;
        int _stepIndex;
        readonly List<KeyValuePair<string, string>> _captured = new List<KeyValuePair<string, string>>();
        string _setupLastCode;
        DateTime _setupLastFrame;

        bool SetupOpen { get { return _setup != null && _setup.Visibility == Visibility.Visible; } }

        /// <summary>CouchTV's big pointer, as couchtv.ini says (not in the small test window).</summary>
        void ApplyPointer()
        {
            if (!_opt.Windowed) Pointer.Apply(_cfg.PointerStyle, _cfg.PointerSize, _cfg.PointerColor);
        }

        /// <summary>The phone remote chose a pointer: show it now, and keep it in couchtv.ini.</summary>
        void ChangePointer(string style, int size, string color)
        {
            Pointer.Normalise(ref style, ref size, ref color);
            _cfg.PointerStyle = style;
            _cfg.PointerSize = size;
            _cfg.PointerColor = color;
            ApplyPointer();
            string path = System.IO.Path.Combine(Config.AppDir, Config.FileName);
            try
            {
                string text = System.IO.File.ReadAllText(path);
                string updated = ConfigUpgrade.Set(text, "CouchTV", "PointerStyle", style, true);
                updated = ConfigUpgrade.Set(updated, "CouchTV", "PointerSize", size.ToString(), true);
                updated = ConfigUpgrade.Set(updated, "CouchTV", "PointerColor", color, true);
                if (updated != text) System.IO.File.WriteAllText(path, updated, new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Error("Saving the pointer to " + path, ex); }
        }

        void StartRemote()
        {
            _remote = RemoteMap.Load();
            _ir = new IrLink(Dispatcher, _cfg.RemotePort);
            _ir.Signal += OnIrSignal;
            _ir.StatusChanged += OnIrStatus;
            _ir.Start();

            // The same phone remote over Wi-Fi: its buttons use the same mapping, and its words go straight to search.
            // Only phones paired with this TV (a code shown here, typed on the phone) can use it.
            _phones = PhoneStore.Load();
            _wifi = new RemoteServer(Dispatcher, _phones);
            _wifi.PairingCode += ShowPairingCode;
            _wifi.PairingEnded += PairingEnded;
            TouchInjector.AsMouse = _cfg.MirrorTouchAsMouse;
            // Someone's watching this screen on a phone: say so, on the TV.
            _wifi.Mirroring += (phone, on) =>
            {
                string message = on ? "This screen is showing on " + phone : phone + " stopped showing this screen";
                if (IsActive && !Modal) Toast(message);
                else Osd.ShowMessage('\uE8EA', message, 3);
            };
            // A text box getting the focus pops up the phone's keyboard: watched only while a phone is connected.
            if (_cfg.TypeOnPhone)
            {
                _typing = new TypingWatch();
                _typing.Changed += _wifi.ShowTyping;
                _wifi.PhonesChanged += count =>
                {
                    if (count > 0) _typing.Start();
                    else _typing.Stop();
                };
            }
            _wifi.Button += OnIrSignal;
            _wifi.Text += ReceiveSearchText;
            _wifi.Connected += who => { if (IsActive && !SearchOpen) Toast("Phone remote connected over Wi-Fi"); };
            _wifi.CurrentPointer = () => _cfg.PointerStyle + " " + _cfg.PointerSize + " " + _cfg.PointerColor;
            _wifi.CurrentSuggestions = () => _phoneSuggestions;
            _wifi.Play += PlaySuggestionById;
            _wifi.CurrentSleepTimer = SleepSecondsLeft;
            // Power on the phone, with a few seconds to change your mind: a big countdown on the TV meanwhile.
            _wifi.PowerSoon += seconds =>
            {
                Log.Info(seconds > 0 ? "Power on a phone: going in " + seconds + " s" : "Power on a phone: cancelled");
                if (_powerCountdown == null) _powerCountdown = new PowerCountdownWindow();
                if (seconds > 0) _powerCountdown.Start(seconds);
                else _powerCountdown.Cancel();
            };
            _wifi.SleepTimer += minutes =>
            {
                string message = SetSleepTimer(minutes);
                if (IsActive && !Modal) Toast(message);
                else Osd.ShowMessage('\uE916', message, 3);
            };
            _wifi.Trackpad += Pointer.Trackpad;
            _wifi.PointerLook += ChangePointer;
            _wifi.Start();
            ApplyPointer();

            // The TV's sound on phones too, each into its own headphones ("Listen on this phone" in the remote app).
            _sound = new AudioShare();
            _sound.Sessions = _wifi.FindSession;
            _sound.ListenersChanged += count => Dispatcher.BeginInvoke(new Action(() =>
            {
                string message = count == 0 ? "No phones are playing the TV's sound now" : "The TV's sound is also playing on " + count + (count == 1 ? " phone" : " phones");
                if (IsActive) Toast(message);
                else Osd.ShowMessage('', message, 3);
            }));
            _sound.Start();

            var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            later.Tick += (s, e) =>
            {
                later.Stop();
                OfferWifiRemote(false);
            };
            later.Start();
        }

        /// <summary>
        /// Windows Firewall blocks phones from reaching CouchTV until a rule allows it. Ask once (or again with F7):
        /// Windows then shows its permission prompt. The rule only allows the local network.
        /// </summary>
        void OfferWifiRemote(bool again)
        {
            const string key = @"Software\CouchTV";
            if (!again)
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(key))
                    if (k != null && k.GetValue("WifiRemoteAsked") != null) return;
            }
            ThreadPool.QueueUserWorkItem(state =>
            {
                if (RemoteServer.FirewallRuleExists())
                {
                    if (again) Dispatcher.BeginInvoke(new Action(() => Toast("The phone remote can already connect over Wi-Fi. This TV's address is " +
                                                                             (RemoteServer.LocalAddress() ?? "unknown") + ".")));
                    return;
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (Modal) return;   // busy: ask next time
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(key)) k.SetValue("WifiRemoteAsked", 1, RegistryValueKind.DWord);
                    Confirm("Use the phone remote over Wi-Fi?",
                        "Phones on your Wi-Fi can then control the TV without pointing, including phones without an IR blaster. " +
                        "Windows will ask for permission once. Only your home network is allowed.",
                        "Allow", () => ThreadPool.QueueUserWorkItem(s2 =>
                        {
                            bool ok = RemoteServer.AddFirewallRule();
                            Dispatcher.BeginInvoke(new Action(() => Toast(ok
                                ? "Done. Open the remote app on a phone on the same Wi-Fi. This TV's address is " + (RemoteServer.LocalAddress() ?? "shown under Remote") + "."
                                : "Not allowed. Press F7 on the home screen to try again.")));
                        }));
                }));
            });
        }

        void OnIrStatus()
        {
            SyncWakeButtons();
            SendBoardKey();
            UpdateSetupStatus();
        }

        /// <summary>
        /// Gives the receiver box's Wi-Fi (an ESP32 behind the receiver) the key it checks phones' Power and status
        /// requests with, by USB through the receiver; paired phones get the same key over their encrypted connection.
        /// </summary>
        void SendBoardKey()
        {
            if (_ir == null || _ir.Port == null || _phones == null) return;
            _ir.Send("LINKKEY " + Secure.Hex(_phones.BoardKey));
        }

        void ShowPairingCode(string phone, string code)
        {
            Osd.ShowCode('', "To pair " + phone + ", type this code on it", code.Substring(0, 3) + " " + code.Substring(3), 125);
        }

        void PairingEnded(string phone, string outcome)
        {
            string message;
            switch (outcome)
            {
                case "paired": message = "Paired " + phone + ": it can use the TV over Wi-Fi now"; break;
                case "wrong": message = "That wasn't the code: " + phone + " isn't paired"; break;
                case "failed": message = "Pairing " + phone + " didn't work. Try again from the phone."; break;
                default:
                    Osd.HideNow();
                    return;
            }
            Osd.ShowMessage('', message, 5);
            UpdateSetupStatus();
        }

        /// <summary>Forgets every paired phone, after asking (from Remote setup: F8).</summary>
        void AskForgetPhones()
        {
            _setup.Visibility = Visibility.Collapsed;
            int count = _wifi != null ? _wifi.PairedPhones : 0;
            Confirm("Forget the paired phones?",
                (count == 0 ? "No phones are paired now. " : count + (count == 1 ? " phone is paired now. " : " phones are paired now. ")) +
                "To use the TV over Wi-Fi again, each pairs again with a code shown here. Until then they can't press Power through the receiver box either.",
                "Forget", () =>
                {
                    if (_wifi == null) return;
                    _wifi.ForgetPhones();
                    SendBoardKey();
                    Toast("Forgot the paired phones. To pair one again, tap the Wi-Fi line in its remote app.");
                });
        }

        /// <summary>Tells the receiver which buttons mean "power", so it can wake the PC with them.</summary>
        void SyncWakeButtons()
        {
            if (_ir == null || _ir.Port == null) return;
            _ir.Send("WAKE CLEAR");
            foreach (string code in _remote.CodesFor("power")) _ir.Send("WAKE " + IrLink.Hash(code).ToString("X8"));
            _ir.Send("WAKE SAVE");
        }

        // ================================================================ buttons

        void OnIrSignal(IrSignal signal)
        {
            if (_updating) return;
            DateTime now = DateTime.Now;
            IrTextResult text = _irText.Handle(signal.Code, now);   // words from the phone's voice search
            if (text != IrTextResult.NotText)
            {
                OnIrText(text);
                return;
            }
            if (SetupOpen)
            {
                SetupCapture(signal);
                return;
            }
            double sinceLast = (now - _irLast).TotalMilliseconds;
            bool sameButton = signal.Code == _irCode && (sinceLast < 250 || (signal.IsRepeat && sinceLast < 600));
            if (sameButton)
            {
                _irLast = now;
                ButtonHeld(now);
                return;
            }
            if (signal.IsRepeat) return;   // the tail end of a press we didn't see start

            _irCode = signal.Code;
            _irFirst = _irLast = _irLastStep = now;
            _irHoldDone = false;
            _irAction = _remote.ActionFor(signal.Code);
            if (_irAction == null)
            {
                HintUnknownButton();
                return;
            }
            RunRemoteAction(_irAction);
        }

        void ButtonHeld(DateTime now)
        {
            if (_irAction == null) return;
            double held = (now - _irFirst).TotalMilliseconds;
            string action = _irAction.ToLowerInvariant();
            if (action == "back")
            {
                // Hold Back = Home, the same as on the remote's own Home key.
                if (!_irHoldDone && held >= 700)
                {
                    _irHoldDone = true;
                    GoHome();
                }
                return;
            }
            // Keep repeating arrows, volume and scrolling while held, after a short pause like a keyboard does.
            // The pause also swallows remotes that send every press several times (Sony).
            if (!Repeats(action) || held < 400 || (now - _irLastStep).TotalMilliseconds < 90) return;
            _irLastStep = now;
            RunRemoteAction(_irAction);
        }

        static bool Repeats(string action)
        {
            switch (action)
            {
                case "up": case "down": case "left": case "right": case "volup": case "voldown": case "backspace":
                case "pageup": case "pagedown": case "tab": case "mouse:scrollup": case "mouse:scrolldown":
                    return true;
                default:
                    return action.StartsWith("key:");
            }
        }

        void RunRemoteAction(string action)
        {
            string name = action.ToLowerInvariant();
            switch (name)
            {
                case "up": Input.Key(0x26); return;
                case "down": Input.Key(0x28); return;
                case "left": Input.Key(0x25); return;
                case "right": Input.Key(0x27); return;
                case "ok": Input.Key(0x0D); return;
                case "back": Input.Key(0xA6); return;          // Browser Back
                case "menu": Input.Key(0x5D); return;
                case "escape": Input.Key(0x1B); return;
                case "space": Input.Key(0x20); return;
                case "backspace": Input.Key(0x08); return;
                case "tab": Input.Key(0x09); return;
                case "pageup": Input.Key(0x21); return;
                case "pagedown": Input.Key(0x22); return;
                case "playpause":
                    // Space pauses Netflix, YouTube, Prime Video and JioHotstar. On the home screen it would open a tile.
                    if (Native.GetForegroundWindow() != _hwnd) Input.Key(0x20);
                    return;
                case "home": GoHome(); return;
                case "search": OpenSearch(false); return;
                case "voicesearch": OpenSearch(true); return;   // the phone remote started listening
                case "voicecancel": CancelVoiceSearch(); return;
                case "volup": ChangeVolume(0.02f, false); return;
                case "voldown": ChangeVolume(-0.02f, false); return;
                case "mute": ChangeVolume(0, true); return;
                case "power": SleepNow(); return;
                case "sleeptimer":
                    string message = CycleSleepTimer();
                    if (!IsActive) Osd.ShowMessage('', message, 3);
                    return;
                case "mouse:click": Pointer.Used(); Input.Click(false); return;
                case "mouse:rightclick": Pointer.Used(); Input.Click(true); return;
                case "mouse:scrollup": Pointer.Used(); Input.Wheel(1); return;
                case "mouse:scrolldown": Pointer.Used(); Input.Wheel(-1); return;
                case "mouse:up": case "mouse:down": case "mouse:left": case "mouse:right": Pointer.Used(); StartPointer(); return;
            }
            if (name.StartsWith("open:"))
            {
                Tile tile = FindTile(action.Substring(5).Trim());
                if (tile != null) OpenFromAnywhere(tile);
                else Toast("remote.ini: there's no tile called \"" + action.Substring(5).Trim() + "\"");
                return;
            }
            if (name.StartsWith("key:"))
            {
                HotkeySpec spec = Hotkeys.Parse(action.Substring(4).Trim());
                if (spec != null && spec.Vk != 0 && !spec.IsHold) Input.Combo(spec.Modifiers, spec.Vk);
                else Log.Info("remote.ini: unknown key in '" + action + "'");
                return;
            }
            Log.Info("remote.ini: unknown action '" + action + "'");
        }

        /// <summary>Moves the pointer smoothly, speeding up, for as long as the button is held.</summary>
        void StartPointer()
        {
            if (_irPointer == null)
            {
                _irPointer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
                _irPointer.Tick += (s, e) =>
                {
                    DateTime now = DateTime.Now;
                    string action = _irAction == null ? "" : _irAction.ToLowerInvariant();
                    if ((now - _irLast).TotalMilliseconds > 250 || !action.StartsWith("mouse:"))
                    {
                        _irPointer.Stop();
                        return;
                    }
                    int step = (int)Math.Min(2 + (now - _irFirst).TotalMilliseconds / 50, 24);
                    int dx = action == "mouse:left" ? -step : action == "mouse:right" ? step : 0;
                    int dy = action == "mouse:up" ? -step : action == "mouse:down" ? step : 0;
                    Input.MouseMove(dx, dy);
                    Pointer.Used();
                };
            }
            _irPointer.Start();
        }

        Tile FindTile(string name)
        {
            var tiles = new List<Tile>(_cfg.AppTiles);
            tiles.AddRange(_cfg.SystemTiles);
            foreach (Tile t in tiles)
                if (t.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || t.Label.Equals(name, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        /// <summary>
        /// A remote button that opens a tile directly. Like switching channels: close what's open first. The old
        /// browser must finish quitting before the new tile starts, or the launch is handed to the dying browser.
        /// </summary>
        void OpenFromAnywhere(Tile t)
        {
            if (t.Kind == TileKind.Action)
            {
                RunAction(t);
                return;
            }
            int closed = Shell.ExplorerRunning ? 0 : Shell.CloseAppWindows(_hwnd);
            BringHome();
            if (closed == 0)
            {
                Open(t);
                return;
            }
            ShowLaunch(t);
            string process = Launcher.ProcessNameFor(_cfg, t);
            DateTime started = DateTime.Now;
            var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            wait.Tick += (s, e) =>
            {
                bool gone = Shell.FindAppWindows(_hwnd, null).Count == 0 && (process == null || !Shell.IsRunning(process));
                if (!gone && (DateTime.Now - started).TotalSeconds < 6) return;
                wait.Stop();
                Open(t);
            };
            wait.Start();
        }

        void HintUnknownButton()
        {
            if (!IsActive || (DateTime.Now - _irHintShown).TotalSeconds < 15) return;
            _irHintShown = DateTime.Now;
            Toast("That remote button isn't set up yet. Use Remote in Settings & power.");
        }

        // ================================================================ Remote setup screen

        FrameworkElement BuildSetup()
        {
            _setupDot = new Ellipse { Width = 12, Height = 12, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            _setupStatus = Theme.Text("", 22, Theme.Muted, FontWeights.Normal);
            var status = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            status.Children.Add(_setupDot);
            status.Children.Add(_setupStatus);
            var header = new Grid();
            header.Children.Add(Theme.Text("Set up a remote", 44, Theme.Ink, FontWeights.SemiBold, Theme.Display));
            header.Children.Add(status);

            TextBlock intro = Theme.Text("Press each button on your remote when it's asked for. Any infrared remote works, and you can set up more than one.", 24, Theme.Muted, FontWeights.Normal);
            intro.TextWrapping = TextWrapping.Wrap;
            intro.Margin = new Thickness(0, 12, 0, 0);

            _setupGlyph = new TextBlock { FontFamily = Theme.Icons, FontSize = 60, Foreground = Theme.Brush(Theme.Ink), Width = 110, VerticalAlignment = VerticalAlignment.Center };
            _setupLabel = Theme.Text("", 54, Theme.Ink, FontWeights.SemiBold, Theme.Display);
            var prompt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            prompt.Children.Add(Theme.Text("Press the button for", 24, Theme.Muted, FontWeights.Normal));
            prompt.Children.Add(_setupLabel);
            var promptRow = new StackPanel { Orientation = Orientation.Horizontal };
            promptRow.Children.Add(_setupGlyph);
            promptRow.Children.Add(prompt);
            var card = new Border
            {
                CornerRadius = new CornerRadius(22), Background = Theme.Brush(Theme.Hex("#1F2633")), Padding = new Thickness(40, 30, 40, 32),
                Margin = new Thickness(0, 34, 0, 0), Child = promptRow,
            };

            _setupStep = Theme.Text("", 22, Theme.Faint, FontWeights.Normal);
            _setupStep.Margin = new Thickness(0, 18, 0, 0);
            _setupFeedback = Theme.Text("", 26, Theme.Muted, FontWeights.Normal);
            _setupFeedback.TextWrapping = TextWrapping.Wrap;
            _setupFeedback.Margin = new Thickness(0, 8, 0, 0);
            _setupFeedback.MinHeight = 36;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 34, 0, 0) };
            buttons.Children.Add(SetupButton("Skip", "Backspace", SkipStep));
            buttons.Children.Add(SetupButton("Save", "Enter", () => FinishSetup(true)));
            buttons.Children.Add(SetupButton("Cancel", "Esc", () => FinishSetup(false)));
            buttons.Children.Add(SetupButton("Forget phones", "F8", AskForgetPhones));

            var column = new StackPanel();
            column.Children.Add(header);
            column.Children.Add(intro);
            column.Children.Add(card);
            column.Children.Add(_setupStep);
            column.Children.Add(_setupFeedback);
            column.Children.Add(buttons);

            var overlay = new Grid { Background = Theme.Brush(Color.FromArgb(0xE6, 8, 10, 14)), Visibility = Visibility.Collapsed };
            overlay.Children.Add(new Border
            {
                Width = 1060, CornerRadius = new CornerRadius(28), Padding = new Thickness(64, 52, 64, 52),
                Background = Theme.Brush(Theme.Panel), BorderBrush = Theme.Brush(Color.FromArgb(0x22, 255, 255, 255)),
                BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Child = column,
            });
            return overlay;
        }

        Border SetupButton(string text, string key, Action action)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Theme.Text(text, 24, Theme.Ink, FontWeights.SemiBold));
            TextBlock hint = Theme.Text(key, 19, Theme.Muted, FontWeights.Normal);
            hint.Margin = new Thickness(12, 0, 0, 0);
            hint.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(hint);
            var button = new Border
            {
                CornerRadius = new CornerRadius(30), Padding = new Thickness(32, 12, 32, 14), Margin = new Thickness(0, 0, 18, 0),
                Background = Theme.Brush(Theme.Hex("#262D3A")), Child = row, Cursor = Cursors.Hand,
            };
            button.MouseEnter += (s, e) => button.Background = Theme.Brush(Theme.Hex("#343D4E"));
            button.MouseLeave += (s, e) => button.Background = Theme.Brush(Theme.Hex("#262D3A"));
            button.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                action();
            };
            return button;
        }

        List<SetupStep> BuildSteps()
        {
            var steps = new List<SetupStep>
            {
                new SetupStep { Action = "up", Label = "Up", Glyph = '' },
                new SetupStep { Action = "down", Label = "Down", Glyph = '' },
                new SetupStep { Action = "left", Label = "Left", Glyph = '' },
                new SetupStep { Action = "right", Label = "Right", Glyph = '' },
                new SetupStep { Action = "ok", Label = "OK / Select", Glyph = '' },
                new SetupStep { Action = "back", Label = "Back", Glyph = '' },
                new SetupStep { Action = "home", Label = "Home", Glyph = '' },
                new SetupStep { Action = "volup", Label = "Volume up", Glyph = '' },
                new SetupStep { Action = "voldown", Label = "Volume down", Glyph = '' },
                new SetupStep { Action = "mute", Label = "Mute", Glyph = '' },
                new SetupStep { Action = "playpause", Label = "Play / Pause", Glyph = '' },
                new SetupStep { Action = "power", Label = "Power", Glyph = '' },
            };
            foreach (Tile t in _cfg.AppTiles)
                steps.Add(new SetupStep { Action = "open:" + t.Name, Label = "Open " + t.Label, Glyph = '', Optional = true });
            return steps;
        }

        void OpenRemoteSetup()
        {
            _steps = BuildSteps();
            _stepIndex = 0;
            _captured.Clear();
            _setupLastCode = null;
            SetFeedback("", Theme.Muted);
            ShowStep();
            UpdateSetupStatus();
            _setup.Visibility = Visibility.Visible;
        }

        void ShowStep()
        {
            SetupStep step = _steps[_stepIndex];
            _setupGlyph.Text = step.Glyph.ToString();
            _setupLabel.Text = step.Label;
            string note = step.Optional ? "  ·  optional: Skip if your remote has no button for it"
                : step.Action == "power" ? "  ·  this button will also wake the PC from sleep"
                : "";
            _setupStep.Text = "Button " + (_stepIndex + 1) + " of " + _steps.Count + note;
        }

        void UpdateSetupStatus()
        {
            if (_setupStatus == null) return;
            bool connected = _ir != null && _ir.Port != null;
            _setupDot.Fill = Theme.Brush(connected ? Theme.Good : Theme.Bad);
            string address = RemoteServer.LocalAddress();
            int phones = _wifi != null ? _wifi.PairedPhones : 0;
            _setupStatus.Text = (connected ? "Receiver ready on " + _ir.Port : "Receiver not found - plug it in") +
                                (address != null ? "   ·   Wi-Fi remote: " + address : "") +
                                "   ·   " + phones + (phones == 1 ? " phone" : " phones") + " paired";
        }

        void SetFeedback(string text, Color color)
        {
            _setupFeedback.Text = text;
            _setupFeedback.Foreground = Theme.Brush(color);
        }

        void SetupCapture(IrSignal signal)
        {
            DateTime now = DateTime.Now;
            // Ignore the rest of a press: repeats while held, and remotes that send each press several times.
            bool samePress = signal.Code == _setupLastCode && (now - _setupLastFrame).TotalMilliseconds < 700;
            _setupLastFrame = now;
            if (signal.IsRepeat || samePress) return;
            _setupLastCode = signal.Code;

            foreach (KeyValuePair<string, string> pair in _captured)
            {
                if (pair.Key != signal.Code) continue;
                SetFeedback("That button is already set for " + StepLabel(pair.Value) + ". Press a different one, or Skip.", Theme.Warn);
                return;
            }
            _captured.Add(new KeyValuePair<string, string>(signal.Code, _steps[_stepIndex].Action));
            SetFeedback("✓  Got it (" + signal.Code + ")", Theme.Good);
            Advance();
        }

        string StepLabel(string action)
        {
            foreach (SetupStep step in _steps) if (step.Action == action) return step.Label;
            return action;
        }

        void SkipStep()
        {
            SetFeedback("", Theme.Muted);
            Advance();
        }

        void Advance()
        {
            _stepIndex++;
            if (_stepIndex >= _steps.Count) FinishSetup(true);
            else ShowStep();
        }

        void FinishSetup(bool save)
        {
            _setup.Visibility = Visibility.Collapsed;
            if (!save)
            {
                Toast("Remote setup cancelled. Nothing was changed.");
                return;
            }
            if (_captured.Count == 0)
            {
                Toast("No buttons were set up.");
                return;
            }
            try
            {
                _remote.Save(new List<KeyValuePair<string, string>>(_captured));
            }
            catch (Exception ex)
            {
                Log.Error("Saving remote.ini", ex);
                Toast("Couldn't save remote.ini");
                return;
            }
            SyncWakeButtons();
            Toast("Remote saved: " + _captured.Count + " button" + (_captured.Count == 1 ? "" : "s") + " set up.");
        }

        /// <summary>Keyboard keys while Remote setup is open (the new remote isn't set up yet, so keys drive it).</summary>
        void SetupKey(Key key)
        {
            switch (key)
            {
                case Key.Escape: FinishSetup(false); break;
                case Key.Back: SkipStep(); break;
                case Key.Enter: FinishSetup(true); break;
                case Key.F8: AskForgetPhones(); break;
            }
        }
    }
}
