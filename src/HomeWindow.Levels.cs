using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace CouchTV
{
    // The phone's volume and brightness sliders: they set the TV's levels, and the phones hear whenever those change
    // (the volume keys, a voice search, Windows' own slider), so each slider shows what the TV is at.
    internal sealed partial class HomeWindow
    {
        DispatcherTimer _levelWatch;
        int _volumeTold = -1, _brightnessTold = -1;
        bool _mutedTold;

        void StartLevels()
        {
            Brightness.Start(Dispatcher);
            _wifi.CurrentLevels = LevelLines;
            _wifi.Volume += level =>
            {
                float now;
                bool muted;
                if (Audio.Set(level / 100f, false, out now, out muted)) Osd.ShowVolume(now, muted);
            };
            _wifi.Mute += on =>
            {
                float now;
                bool muted;
                if (Audio.Set(-1, on, out now, out muted)) Osd.ShowVolume(now, muted);
                TellLevels();
            };
            _wifi.Brightness += level =>
            {
                Brightness.Set(level);
                Osd.ShowLevel('', "Brightness " + level, level / 100f);
            };
            // Looked at twice a second while a phone is connected: a change from anywhere reaches its sliders.
            _levelWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _levelWatch.Tick += (s, e) => TellLevels();
        }

        /// <summary>Watches the levels only while a phone is connected to show them.</summary>
        void WatchLevels()
        {
            if (_levelWatch == null) return;
            if (_phoneCount > 0) _levelWatch.Start();
            else
            {
                _levelWatch.Stop();
                _volumeTold = _brightnessTold = -1;
            }
        }

        /// <summary>The lines for a phone that's just connected; from the network thread.</summary>
        List<string> LevelLines()
        {
            var lines = new List<string>();
            float level;
            bool muted;
            if (Audio.Get(out level, out muted)) lines.Add(VolumeLine((int)Math.Round(level * 100), muted));
            lines.Add("BRIGHTNESS " + Brightness.Level);
            return lines;
        }

        /// <summary>Tells the phones the levels that changed since they were last told.</summary>
        void TellLevels()
        {
            var lines = new List<string>();
            float level;
            bool muted;
            if (Audio.Get(out level, out muted))
            {
                int volume = (int)Math.Round(level * 100);
                if (volume != _volumeTold || muted != _mutedTold)
                {
                    _volumeTold = volume;
                    _mutedTold = muted;
                    lines.Add(VolumeLine(volume, muted));
                }
            }
            int brightness = Brightness.Level;
            if (brightness != _brightnessTold)
            {
                _brightnessTold = brightness;
                lines.Add("BRIGHTNESS " + brightness);
            }
            if (lines.Count > 0 && _wifi != null) _wifi.SendToPhones(lines);
        }

        static string VolumeLine(int volume, bool muted)
        {
            return "VOLUME " + volume + " " + (muted ? 1 : 0);
        }
    }
}
