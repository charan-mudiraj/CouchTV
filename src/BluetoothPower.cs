using System;
using System.Diagnostics;
using System.Threading;
using Windows.Devices.Radios;
using Windows.Foundation;

namespace CouchTV
{
    /// <summary>
    /// Whether the PC's Bluetooth is switched on, and switching it (Windows' Radio API, as the Bluetooth switch in
    /// Settings does): there's no Settings app to reach in TV mode. Switched off, Windows' Bluetooth functions find no
    /// adapter at all. Blocks for a moment: call off the UI thread.
    /// </summary>
    internal static class BluetoothPower
    {
        /// <summary>True on, false off, null when there's no Bluetooth adapter (or Windows won't say).</summary>
        public static bool? IsOn()
        {
            try
            {
                Radio radio = Find();
                return radio == null ? (bool?)null : radio.State == RadioState.On;
            }
            catch (Exception ex)
            {
                Log.Info("Bluetooth switch: " + ex.Message);
                return null;
            }
        }

        public static bool Set(bool on)
        {
            try
            {
                Radio radio = Find();
                if (radio == null || Wait(Radio.RequestAccessAsync()) != RadioAccessStatus.Allowed) return false;
                bool ok = Wait(radio.SetStateAsync(on ? RadioState.On : RadioState.Off)) == RadioAccessStatus.Allowed;
                Log.Info("Bluetooth switched " + (on ? "on" : "off") + (ok ? "" : ": not allowed"));
                return ok;
            }
            catch (Exception ex)
            {
                Log.Info("Bluetooth switch: " + ex.Message);
                return false;
            }
        }

        static Radio Find()
        {
            foreach (Radio radio in Wait(Radio.GetRadiosAsync()))
                if (radio.Kind == RadioKind.Bluetooth) return radio;
            return null;
        }

        // Windows' asynchronous calls, waited for (without the SDK's helpers, which the compiler in Windows can't use).
        static T Wait<T>(IAsyncOperation<T> operation)
        {
            var clock = Stopwatch.StartNew();
            while (operation.Status == AsyncStatus.Started && clock.ElapsedMilliseconds < 10000) Thread.Sleep(10);
            if (operation.Status != AsyncStatus.Completed) throw new TimeoutException("Windows didn't answer (" + operation.Status + ")");
            return operation.GetResults();
        }
    }
}
