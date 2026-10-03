using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CouchTV
{
    /// <summary>Window management, power and shell-registration helpers.</summary>
    internal static class Shell
    {
        public const string WinlogonKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
        const string SettingsKey = @"Software\CouchTV";

        // Processes whose windows are part of Windows itself, never "apps" to close on Home.
        static readonly HashSet<string> SystemProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "dwm", "csrss", "winlogon", "LogonUI", "LockApp", "TextInputHost", "ShellHost",
            "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "Widgets", "CouchTV",
        };

        /// <summary>True when the normal Windows taskbar/desktop is running (desktop mode); false in TV mode.</summary>
        public static bool ExplorerRunning
        {
            get { return Native.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero; }
        }

        static string ExplorerPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"); }
        }

        // ---------------------------------------------------------------- windows

        /// <summary>Asks every visible app window to close. Used by Home in TV mode, like a TV leaving an app.</summary>
        public static int CloseAppWindows(IntPtr self)
        {
            List<IntPtr> windows = FindAppWindows(self, null);
            foreach (IntPtr hwnd in windows) Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (windows.Count > 0) Log.Info("Home: closed " + windows.Count + " window(s)");
            return windows.Count;
        }

        public static List<IntPtr> FindAppWindows(IntPtr self, List<string> owners)
        {
            uint selfPid = (uint)Process.GetCurrentProcess().Id;
            var found = new List<IntPtr>();
            Native.EnumWindows((hwnd, lParam) =>
            {
                if (hwnd == self || !Native.IsWindowVisible(hwnd)) return true;
                if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return true;     // dialogs close with their owner
                int exStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                if ((exStyle & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE)) != 0) return true;
                if (Native.GetWindowTextLength(hwnd) == 0) return true;
                int cloaked;
                if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return true;
                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                if (pid == selfPid) return true;
                string name = ProcessName(pid);
                if (name == null || SystemProcesses.Contains(name)) return true;
                found.Add(hwnd);
                if (owners != null && !owners.Contains(name)) owners.Add(name);
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>Whether a program is still running in this Windows session.</summary>
        public static bool IsRunning(string processName)
        {
            int session = Process.GetCurrentProcess().SessionId;
            bool running = false;
            foreach (Process p in Process.GetProcessesByName(processName))
            {
                if (p.SessionId == session) running = true;
                p.Dispose();
            }
            return running;
        }

        static string ProcessName(uint pid)
        {
            try
            {
                using (Process p = Process.GetProcessById((int)pid)) return p.ProcessName;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Brings a window to the front even when Windows' focus-stealing rules object.</summary>
        public static void ForceForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || Native.GetForegroundWindow() == hwnd) return;
            Native.ShowWindow(hwnd, Native.SW_SHOW);
            if (Native.SetForegroundWindow(hwnd)) return;

            uint ignored;
            uint foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out ignored);
            uint thisThread = Native.GetCurrentThreadId();
            if (foregroundThread == 0 || foregroundThread == thisThread) return;
            Native.AttachThreadInput(thisThread, foregroundThread, true);
            Native.BringWindowToTop(hwnd);
            Native.SetForegroundWindow(hwnd);
            Native.AttachThreadInput(thisThread, foregroundThread, false);
        }

        /// <summary>Used when CouchTV is started a second time: bring the running copy forward.</summary>
        public static void ActivateWindow(string title)
        {
            IntPtr hwnd = Native.FindWindow(null, title);
            if (hwnd == IntPtr.Zero) return;
            Native.ShowWindow(hwnd, Native.SW_RESTORE);
            Native.SetForegroundWindow(hwnd);
        }

        // ---------------------------------------------------------------- power

        public static void Sleep()
        {
            Log.Info("Sleep");
            if (Native.SetSuspendState(false, false, false)) return;
            // Some PCs only have "Modern Standby", where this call can fail. Turning the screen off lets them
            // drop into standby by themselves.
            Log.Info("SetSuspendState failed (error " + Marshal.GetLastWin32Error() + "), turning the screen off instead");
            Native.PostMessage(Native.HWND_BROADCAST, Native.WM_SYSCOMMAND, (IntPtr)Native.SC_MONITORPOWER, (IntPtr)2);
        }

        public static void Shutdown(bool restart)
        {
            Log.Info(restart ? "Restart" : "Shut down");
            if (restart)
            {
                Run("shutdown.exe", "/r /t 0", 0);
                return;
            }
            // /hybrid = Fast Startup, so the next power-on is quicker. It fails if Fast Startup is off.
            if (Run("shutdown.exe", "/s /hybrid /t 0", 5000) != 0) Run("shutdown.exe", "/s /t 0", 0);
        }

        static int Run(string exe, string args, int waitMs)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true };
                using (Process p = Process.Start(psi))
                {
                    if (waitMs <= 0) return 0;
                    return p.WaitForExit(waitMs) ? p.ExitCode : 0;
                }
            }
            catch (Exception ex)
            {
                Log.Error(exe + " " + args, ex);
                return -1;
            }
        }

        /// <summary>A Windows update has been installed and needs a restart.</summary>
        public static bool UpdateWaiting()
        {
            return KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")
                || KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        }

        static bool KeyExists(string path)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path)) return key != null;
            }
            catch (Exception) { return false; }
        }

        // ---------------------------------------------------------------- shell registration
        //
        // The installer makes CouchTV the Windows shell for this user (HKCU ...\Winlogon\Shell), so Windows
        // starts straight into it instead of the desktop, taskbar and start-up apps.

        static string UserShell
        {
            get
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(WinlogonKey))
                    return key == null ? null : key.GetValue("Shell") as string;
            }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(WinlogonKey))
                    key.SetValue("Shell", value);
            }
        }

        static bool IsCouchShell(string value)
        {
            return value != null && value.IndexOf("CouchTV", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string ShellCommand()
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            return exe.Contains(" ") ? "\"" + exe + "\"" : exe;
        }

        /// <summary>Puts the shell setting back if a desktop visit or a crash left it pointing at Explorer.</summary>
        public static void EnsureShellSetting()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey))
                    if (key == null || Convert.ToInt32(key.GetValue("ShellMode", 0)) != 1) return;
                string wanted = ShellCommand();
                if (!string.Equals(UserShell, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    UserShell = wanted;
                    Log.Info("Shell setting restored to " + wanted);
                }
            }
            catch (Exception ex) { Log.Error("EnsureShellSetting", ex); }
        }

        /// <summary>
        /// Opens the normal Windows desktop. Explorer only turns into the full desktop (taskbar, Start) when the
        /// shell setting names it, so swap the setting to explorer.exe and put CouchTV back once the taskbar is up.
        /// </summary>
        public static void StartDesktop()
        {
            string previous = UserShell;
            bool swap = IsCouchShell(previous);
            if (swap) UserShell = "explorer.exe";
            using (Process.Start(ExplorerPath)) { }
            if (!swap) return;

            DateTime started = DateTime.Now;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (s, e) =>
            {
                if (!ExplorerRunning && (DateTime.Now - started).TotalSeconds < 20) return;
                timer.Stop();
                UserShell = previous;
            };
            timer.Start();
        }

        /// <summary>Last resort if CouchTV crashes in TV mode: never leave a black screen.</summary>
        public static void Rescue()
        {
            try
            {
                if (ExplorerRunning) return;
                if (IsCouchShell(UserShell)) UserShell = "explorer.exe";   // CouchTV re-registers itself next start
                using (Process.Start(ExplorerPath)) { }
            }
            catch (Exception) { }
        }
    }
}
