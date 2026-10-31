using System;
using System.Collections.Generic;
using System.Windows;

namespace CouchTV
{
    /// <summary>
    /// Phones' screens on the TV (PhoneShare): when a phone starts showing its screen, whatever's playing pauses and
    /// the phones show over everything (ShareWindow), side by side. Meanwhile every other sound on the TV is muted
    /// (OtherSounds): music or a film started behind them plays silently, and the phones' page says it's playing. When
    /// the last phone stops, they go, the sounds come back, and what was paused stays paused (Play carries on). Back or
    /// Home on the remote closes them all, telling each phone.
    /// </summary>
    internal sealed partial class HomeWindow
    {
        PhoneShare _share;
        ShareWindow _shareWindow;
        OtherSounds _others;

        /// <summary>Phones' screens are on the TV now.</summary>
        internal bool SharesShown { get { return _shareWindow != null; } }

        void StartPhoneShares()
        {
            try { _share = new PhoneShare(); }
            catch (Exception ex)
            {
                Log.Info("Phone screens: not offered (" + ex.Message + ")");
                return;
            }
            _share.Changed += (names, name, started) => Dispatcher.BeginInvoke(new Action(() => SharesChanged(names, name, started)));
            _wifi.Share = _share;
        }

        void SharesChanged(List<string> names, string name, bool started)
        {
            if (names.Count > 0 && _shareWindow == null)
            {
                // Everyone's to watch the phone now: the film or music pauses (Play carries on after), the trailer stops,
                // and anything else that makes a sound on the TV is muted until the phones go.
                MediaNow now = NowPlaying.Current;
                if (now != null && now.Playing) NowPlaying.Command("pause");
                StopTrailer();
                ShareWindow window = new ShareWindow(_share.PageUrl, _opt.Windowed);
                _shareWindow = window;
                window.Show();
                _others = new OtherSounds(() => window.BrowserProcess);
                Log.Info("Phone screens: showing");
            }
            else if (names.Count == 0 && _shareWindow != null)
            {
                CloseShareWindow();
                Log.Info("Phone screens: none now" + (_share.Stats != null ? " (last: " + _share.Stats + ")" : ""));
            }
            string message = started ? name + " is showing its screen" : name + " stopped showing its screen";
            if (_shareWindow == null) Osd.ShowMessage('', message, 3);
        }

        /// <summary>Every second while phones show: what's playing behind them on the TV (muted), for their page to say.</summary>
        void TickShares()
        {
            if (!SharesShown) return;
            MediaNow now = NowPlaying.Current;
            string behind = null;
            if (now != null && now.Playing)
                behind = string.IsNullOrEmpty(now.Artist) ? (now.Title ?? "") : now.Title + " · " + now.Artist;
            _share.TellPlayingBehind(behind == null ? null : behind.Trim().Length > 0 ? behind.Trim() : "Something");
        }

        void CloseShareWindow()
        {
            if (_others != null) _others.Dispose();   // the TV's other sounds come back
            _others = null;
            if (_shareWindow != null) _shareWindow.Close();
            _shareWindow = null;
            if (_share != null) _share.TellPlayingBehind(null);
        }

        /// <summary>Back or Home on the remote while phones are showing: they all go (each phone's told).</summary>
        bool CloseShares()
        {
            if (!SharesShown) return false;
            _share.StopAll();
            return true;
        }

        void StopPhoneShares()
        {
            CloseShareWindow();
            if (_share != null) _share.Dispose();
        }

        // For the self-test (Diagnostics.ShareTest).
        internal PhoneShare Share { get { return _share; } }
    }
}
