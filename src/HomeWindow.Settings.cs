using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CouchTV
{
    /// <summary>
    /// The TV's own settings: a screen opened from Settings &amp; power, worked with the remote (up and down to choose,
    /// left and right to change, Back to close), and the same list on the phone remote's Settings page, which changes
    /// them too ("TV settings" in ir-receiver/PROTOCOL.md). Each goes into couchtv.ini as soon as it changes, and every
    /// connected phone is told.
    /// </summary>
    partial class HomeWindow
    {
        sealed class TvSetting
        {
            public string Key, Label, Note;
            public string[] Values, Names;
            public Func<string> Get;
            public Action<string> Set;
        }

        static readonly string[] OnOffValues = { "on", "off" }, OnOffNames = { "On", "Off" };

        FrameworkElement _settings;
        StackPanel _settingsList;
        int _settingIndex, _phoneCount;
        List<TvSetting> _tvSettings;

        bool SettingsOpen { get { return _settings != null && _settings.Visibility == Visibility.Visible; } }

        static string OnOff(bool on) { return on ? "on" : "off"; }

        List<TvSetting> TvSettings()
        {
            if (_tvSettings != null) return _tvSettings;
            return _tvSettings = new List<TvSetting>
            {
                new TvSetting
                {
                    Key = "powercountdown", Label = "Power-off countdown",
                    Note = "Power on the phone waits this long with the TV on, so a tap by mistake can be cancelled",
                    Values = new[] { "0", "3", "5", "10" }, Names = new[] { "Off", "3 s", "5 s", "10 s" },
                    Get = () => _cfg.PowerCountdown.ToString(),
                    Set = v => { _cfg.PowerCountdown = int.Parse(v); SaveSetting("PowerCountdown", v); },
                },
                new TvSetting
                {
                    Key = "films", Label = "Popular films", Note = "Under the apps, and on the phone's first page",
                    Values = OnOffValues, Names = OnOffNames, Get = () => OnOff(_cfg.SuggestMovies),
                    Set = v => { _cfg.SuggestMovies = v == "on"; SuggestionChoiceChanged(); },
                },
                new TvSetting
                {
                    Key = "songs", Label = "Top songs", Note = "Under the films, played on YouTube",
                    Values = OnOffValues, Names = OnOffNames, Get = () => OnOff(_cfg.SuggestSongs),
                    Set = v => { _cfg.SuggestSongs = v == "on"; SuggestionChoiceChanged(); },
                },
                new TvSetting
                {
                    Key = "typeonphone", Label = "Phone keyboard for text boxes",
                    Note = "A search box or a login form on the TV pops up the phone's keyboard",
                    Values = OnOffValues, Names = OnOffNames, Get = () => OnOff(_cfg.TypeOnPhone),
                    Set = v => { _cfg.TypeOnPhone = v == "on"; WatchTyping(); SaveSetting("TypeOnPhone", v); },
                },
                new TvSetting
                {
                    Key = "pointerstyle", Label = "Pointer", Note = "Shown while the phone's trackpad is open",
                    Values = Pointer.Styles, Names = new[] { "Arrow", "Dot", "Ring" },
                    Get = () => _cfg.PointerStyle, Set = v => ChangePointer(v, _cfg.PointerSize, _cfg.PointerColor),
                },
                new TvSetting
                {
                    Key = "pointersize", Label = "Pointer size",
                    Values = new[] { "48", "64", "96", "128" }, Names = new[] { "Medium", "Large", "Huge", "Giant" },
                    Get = () => _cfg.PointerSize.ToString(), Set = v => ChangePointer(_cfg.PointerStyle, int.Parse(v), _cfg.PointerColor),
                },
                new TvSetting
                {
                    Key = "pointercolor", Label = "Pointer colour",
                    Values = Pointer.ColorNames, Names = new[] { "White", "Yellow", "Blue", "Green", "Pink" },
                    Get = () => _cfg.PointerColor, Set = v => ChangePointer(_cfg.PointerStyle, _cfg.PointerSize, v),
                },
                new TvSetting
                {
                    Key = "updates", Label = "Check for updates", Note = "Every 10 minutes; the Update tile installs one",
                    Values = OnOffValues, Names = OnOffNames, Get = () => OnOff(_cfg.Updates),
                    Set = v => { _cfg.Updates = v == "on"; SaveSetting("Updates", v); },
                },
            };
        }

        /// <summary>A phone changed one ("SET"): only to one of its values.</summary>
        void ApplySetting(string key, string value)
        {
            TvSetting setting = TvSettings().Find(s => s.Key == key);
            if (setting == null || Array.IndexOf(setting.Values, value) < 0 || setting.Get() == value) return;
            Log.Info("Setting from a phone: " + key + " = " + value);
            setting.Set(value);
        }

        /// <summary>Into couchtv.ini's [CouchTV], then the phones (and this screen) are told.</summary>
        void SaveSetting(string key, string value)
        {
            string path = Path.Combine(Config.AppDir, Config.FileName);
            try
            {
                string text = File.ReadAllText(path);
                string updated = ConfigUpgrade.Set(text, "CouchTV", key, value, true);
                if (updated != text) File.WriteAllText(path, updated, new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Error("Saving " + key + " to " + path, ex); }
            SettingsChanged();
        }

        void SettingsChanged()
        {
            if (_wifi != null) _wifi.SendToPhones(SettingsLines());
            if (SettingsOpen) RenderSettings();
        }

        /// <summary>The films and songs rows on or off: shown (or not) at once, fetched if they're new.</summary>
        void SuggestionChoiceChanged()
        {
            string value = _cfg.SuggestMovies && _cfg.SuggestSongs ? "movies, music" : _cfg.SuggestMovies ? "movies" : _cfg.SuggestSongs ? "music" : "off";
            RebuildSuggestions();
            SuggestionsSettingsChanged();
            SaveSetting("Suggestions", value);
        }

        /// <summary>Text boxes are watched while a phone is connected, if the phone's keyboard is to pop up for them.</summary>
        void WatchTyping()
        {
            if (_typing == null) return;
            if (_cfg.TypeOnPhone && _phoneCount > 0) _typing.Start();
            else
            {
                _typing.Stop();
                if (_wifi != null) _wifi.ShowTyping(null);
            }
        }

        /// <summary>
        /// For the phones: "SETTINGS n", then n lines "SETTING key value label note value:name,…" (texts in base64,
        /// "-" for no note). Any thread: the phones' connections ask as they arrive.
        /// </summary>
        List<string> SettingsLines()
        {
            List<TvSetting> all = TvSettings();
            var lines = new List<string> { "SETTINGS " + all.Count };
            foreach (TvSetting s in all)
            {
                var choices = new List<string>();
                for (int i = 0; i < s.Values.Length; i++) choices.Add(s.Values[i] + ":" + Base64(s.Names[i]));
                lines.Add("SETTING " + s.Key + " " + s.Get() + " " + Base64(s.Label) + " " +
                          (string.IsNullOrEmpty(s.Note) ? "-" : Base64(s.Note)) + " " + string.Join(",", choices));
            }
            return lines;
        }

        static string Base64(string text) { return Convert.ToBase64String(Secure.Utf8(text)); }

        // ================================================================ the screen

        FrameworkElement BuildSettings()
        {
            var title = Theme.Text("Settings", 44, Theme.Ink, FontWeights.SemiBold, Theme.Display);
            var intro = Theme.Text("The TV's own. The phone remote's Settings page has these too, with the phone's.", 24, Theme.Muted, FontWeights.Normal);
            intro.TextWrapping = TextWrapping.Wrap;
            intro.Margin = new Thickness(0, 10, 0, 0);
            _settingsList = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
            var hints = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
            hints.Children.Add(KeyHint("↑ ↓", "Choose"));
            hints.Children.Add(KeyHint("← →", "Change"));
            hints.Children.Add(KeyHint("Back", "Close"));

            var column = new StackPanel();
            column.Children.Add(title);
            column.Children.Add(intro);
            column.Children.Add(_settingsList);
            column.Children.Add(hints);

            var overlay = new Grid { Background = Theme.Brush(Color.FromArgb(0xE6, 8, 10, 14)), Visibility = Visibility.Collapsed };
            overlay.Children.Add(new Border
            {
                Width = 1180, CornerRadius = new CornerRadius(28), Padding = new Thickness(56, 44, 56, 40),
                Background = Theme.Brush(Theme.Panel), BorderBrush = Theme.Brush(Color.FromArgb(0x22, 255, 255, 255)),
                BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Child = column,
            });
            return overlay;
        }

        void OpenSettings()
        {
            _settingIndex = 0;
            RenderSettings();
            _settings.Visibility = Visibility.Visible;
        }

        void CloseSettings() { _settings.Visibility = Visibility.Collapsed; }

        void SettingsKey(Key key)
        {
            int count = TvSettings().Count;
            switch (key)
            {
                case Key.Up:
                    _settingIndex = Math.Max(0, _settingIndex - 1);
                    RenderSettings();
                    break;
                case Key.Down:
                    _settingIndex = Math.Min(count - 1, _settingIndex + 1);
                    RenderSettings();
                    break;
                case Key.Left: StepSetting(-1); break;
                case Key.Right: case Key.Enter: case Key.Space: StepSetting(1); break;
                case Key.Escape: case Key.BrowserBack: case Key.Back: CloseSettings(); break;
            }
        }

        /// <summary>The chosen setting's next (or previous) value, round and round.</summary>
        void StepSetting(int step)
        {
            TvSetting s = TvSettings()[_settingIndex];
            int i = Array.IndexOf(s.Values, s.Get()), n = s.Values.Length;
            s.Set(s.Values[i < 0 ? 0 : ((i + step) % n + n) % n]);
        }

        void RenderSettings()
        {
            _settingsList.Children.Clear();
            List<TvSetting> all = TvSettings();
            for (int i = 0; i < all.Count; i++) _settingsList.Children.Add(SettingRow(all[i], i));
        }

        UIElement SettingRow(TvSetting s, int index)
        {
            bool focused = index == _settingIndex;
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Theme.Text(s.Label, 27, Theme.Ink, FontWeights.SemiBold));
            if (!string.IsNullOrEmpty(s.Note))
            {
                TextBlock note = Theme.Text(s.Note, 19, Theme.Muted, FontWeights.Normal);
                note.Margin = new Thickness(0, 2, 0, 0);
                words.Children.Add(note);
            }
            int at = Array.IndexOf(s.Values, s.Get());
            string name = at >= 0 ? s.Names[at] : s.Get();
            var value = new Border
            {
                CornerRadius = new CornerRadius(22), Padding = new Thickness(22, 7, 22, 9), VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right, Background = Theme.Brush(focused ? Theme.Ink : Theme.Chip),
                Child = Theme.Text((focused ? "‹   " : "") + name + (focused ? "   ›" : ""), 24, focused ? Theme.Base : Theme.Ink, FontWeights.SemiBold),
            };
            var grid = new Grid();
            grid.Children.Add(words);
            grid.Children.Add(value);
            var row = new Border
            {
                CornerRadius = new CornerRadius(18), Padding = new Thickness(24, 11, 18, 11), Margin = new Thickness(0, 0, 0, 4),
                Background = focused ? Theme.Brush(Color.FromArgb(0x1C, 255, 255, 255)) : Brushes.Transparent, Child = grid, Cursor = Cursors.Hand,
            };
            row.MouseLeftButtonUp += (o, e) =>
            {
                e.Handled = true;
                _settingIndex = index;
                StepSetting(1);
                RenderSettings();
            };
            return row;
        }
    }
}
