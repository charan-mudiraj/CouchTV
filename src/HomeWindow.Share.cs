using System;
using System.Collections.Generic;
using System.Windows;

namespace CouchTV
{
    /// <summary>
    /// Phones' screens on the TV (PhoneShare): when a phone starts showing its screen, whatever's playing pauses and
    /// the phones show over everything (ShareWindow), side by side; when the last stops, they go, and what was paused
    /// stays paused (Play carries on). Back or Home on the remote closes them all, telling each phone.
    /// </summary>
    internal sealed partial class HomeWindow
    {
        PhoneShare _share;
        ShareWindow _shareWindow;

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
                // Everyone's to watch the phone now: the film or music pauses (Play carries on after), the trailer stops.
                MediaNow now = NowPlaying.Current;
                if (now != null && now.Playing) NowPlaying.Command("pause");
                StopTrailer();
                _shareWindow = new ShareWindow(_share.PageUrl, _opt.Windowed);
                _shareWindow.Show();
                Log.Info("Phone screens: showing");
            }
            else if (names.Count == 0 && _shareWindow != null)
            {
                _shareWindow.Close();
                _shareWindow = null;
                Log.Info("Phone screens: none now" + (_share.Stats != null ? " (last: " + _share.Stats + ")" : ""));
            }
            string message = started ? name + " is showing its screen" : name + " stopped showing its screen";
            if (_shareWindow == null) Osd.ShowMessage('', message, 3);
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
            if (_shareWindow != null) _shareWindow.Close();
            _shareWindow = null;
            if (_share != null) _share.Dispose();
        }

        // For the self-test (Diagnostics.ShareTest).
        internal PhoneShare Share { get { return _share; } }
    }
}
