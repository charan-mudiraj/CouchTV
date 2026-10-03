using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CouchTV
{
    /// <summary>CouchTV updates: noticing new commits on GitHub, telling the viewer, and installing them.</summary>
    internal sealed partial class HomeWindow
    {
        const string LaunchHintText = "Press Home on the remote (or hold Back) to come back here";

        AppUpdateInfo _appUpdate;        // a newer version waiting to be installed, or null
        bool _updating, _checkingUpdates;
        DispatcherTimer _updateTimer;
        string _announcedSha;
        FrameworkElement _updatePill;
        TextBlock _launchHint;

        static Tile UpdateTile
        {
            get
            {
                return new Tile { Name = "Update", Label = "Update", IsSystem = true, Kind = TileKind.Action, Action = "update", Glyph = '' };
            }
        }

        FrameworkElement BuildUpdatePill()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = "", FontFamily = Theme.Icons, FontSize = 18, Foreground = Theme.Brush(Theme.Good),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 10, 0),
            });
            row.Children.Add(Theme.Text("Update available", 22, Theme.Ink, FontWeights.Normal));
            var pill = new Border
            {
                CornerRadius = new CornerRadius(22), Padding = new Thickness(18, 8, 20, 9), Margin = new Thickness(0, 0, 14, 0),
                Background = Theme.Brush(Theme.Alpha(Theme.Good, 0x33)), Child = row, Cursor = Cursors.Hand,
                Visibility = _appUpdate == null ? Visibility.Collapsed : Visibility.Visible,
            };
            pill.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                AskToUpdate();
            };
            return pill;
        }

        // ================================================================ checking

        void StartUpdateChecks()
        {
            if (!_cfg.Updates || !AppUpdate.IsInstalled) return;   // previews and dev builds don't update themselves
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };   // give the network a moment after boot
            _updateTimer.Tick += (s, e) =>
            {
                _updateTimer.Interval = TimeSpan.FromMinutes(10);
                CheckForUpdates(false);
            };
            _updateTimer.Start();
        }

        void CheckForUpdatesSoon(int seconds)
        {
            if (_updateTimer == null) return;
            _updateTimer.Stop();
            _updateTimer.Interval = TimeSpan.FromSeconds(seconds);
            _updateTimer.Start();
        }

        /// <summary>Asks GitHub for a newer version. Manual checks (F6, or the Update tile) report the result.</summary>
        async void CheckForUpdates(bool manual)
        {
            if (_checkingUpdates || _updating) return;
            if (!AppUpdate.IsInstalled)
            {
                if (manual) Toast("Updates work once CouchTV is installed with Install-CouchTV.cmd.");
                return;
            }
            if (!_cfg.Updates)
            {
                if (manual) Toast("Updates are turned off in couchtv.ini (Updates = off).");
                return;
            }
            _checkingUpdates = true;
            if (manual) Toast("Checking for updates…");
            string repo = _cfg.UpdateRepo, branch = _cfg.UpdateBranch, installed = AppUpdate.InstalledSha;
            AppUpdateInfo found;
            try
            {
                found = await Task.Run(() => AppUpdate.Check(repo, branch, installed));
            }
            catch (Exception ex)
            {
                Log.Error("Update check", ex);
                if (manual) Toast("Couldn't check for updates: " + ex.Message + ".");
                return;
            }
            finally
            {
                _checkingUpdates = false;
            }

            SetAppUpdate(found);
            if (found == null)
            {
                if (manual) Toast("CouchTV is up to date (" + AppUpdate.InstalledLabel + ").");
                return;
            }
            if (manual || found.Sha != _announcedSha)
            {
                _announcedSha = found.Sha;
                Toast("A CouchTV update is ready: “" + found.Title + "”. Open Update in Settings & power.");
            }
        }

        /// <summary>Shows or hides the Update tile and the header pill.</summary>
        void SetAppUpdate(AppUpdateInfo update)
        {
            bool hadUpdate = _appUpdate != null;
            bool same = hadUpdate && update != null && update.Sha == _appUpdate.Sha;
            _appUpdate = update;
            if (same || (!hadUpdate && update == null)) return;
            // The Update tile goes at the front of the Settings row: keep the highlight on the same tile.
            if (_row == 1)
            {
                if (!hadUpdate) _col[1]++;
                else if (update == null) _col[1] = Math.Max(0, _col[1] - 1);
            }
            _updatePill.Visibility = update == null ? Visibility.Collapsed : Visibility.Visible;
            BuildTiles();
            RefreshFocus(false);
        }

        // ================================================================ installing

        void AskToUpdate()
        {
            if (_updating) return;
            AppUpdateInfo update = _appUpdate;
            if (update == null)
            {
                CheckForUpdates(true);
                return;
            }
            string changes = update.NewCommits > 1 ? update.NewCommits + " new changes, the latest " : "Pushed ";
            string text = "“" + update.Title + "”\n" + changes + Ago(update.Date) + ". " +
                          "The screen goes dark for a few seconds while CouchTV restarts.";
            Confirm("Update CouchTV?", text, "Update", () => InstallUpdate(update));
        }

        async void InstallUpdate(AppUpdateInfo update)
        {
            if (_updating) return;
            _updating = true;
            ShowUpdateProgress("Downloading");
            string repo = _cfg.UpdateRepo;
            Action<string> progress = step => Dispatcher.BeginInvoke(new Action(() => ShowUpdateProgress(step)));
            try
            {
                PreparedUpdate prepared = await Task.Run(() => AppUpdate.Prepare(repo, update, progress));
                ShowUpdateProgress("Restarting");
                Log.Info("Installing update " + update.ShortSha);
                AppUpdate.Apply(prepared);
                await Task.Delay(500);
                _allowClose = true;
                Close();   // apply-update.ps1 swaps the files and starts the new version
            }
            catch (Exception ex)
            {
                Log.Error("Update", ex);
                _updating = false;
                HideLaunch();
                Toast("The update didn't install: " + ex.Message + " CouchTV wasn't changed.");
            }
        }

        void ShowUpdateProgress(string step)
        {
            _launchText.Text = "Updating CouchTV";
            _launchHint.Text = step + "…";
            _launch.Visibility = Visibility.Visible;
            if (!_opt.Preview)
                _spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        static string Ago(DateTime when)
        {
            TimeSpan age = DateTime.Now - when;
            if (age.TotalMinutes < 2) return "just now";
            if (age.TotalHours < 1) return (int)age.TotalMinutes + " minutes ago";
            if (age.TotalHours < 2) return "an hour ago";
            if (age.TotalDays < 1) return (int)age.TotalHours + " hours ago";
            if (age.TotalDays < 2) return "yesterday";
            return (int)age.TotalDays + " days ago";
        }

        /// <summary>The toast for the first start after an update (or a failed one).</summary>
        public static string StartupMessageFor(bool updated, bool failed)
        {
            if (failed) return "The update couldn't be installed, so CouchTV is still on the previous version.";
            if (!updated) return null;
            string title = AppUpdate.InstalledTitle;
            return "CouchTV updated" + (title == null ? "." : ": “" + title + "”");
        }
    }
}
