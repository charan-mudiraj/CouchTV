using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CouchTV
{
    /// <summary>Command-line checks used while building and installing: screenshots and a self-test report.</summary>
    internal static class Diagnostics
    {
        /// <summary>CouchTV.exe --screenshot out.png [--size 1366x768] [--focus row,col] [--state toast,launch,confirm,osd,update,sleep] [--config file]</summary>
        public static int Screenshot(string[] args, string path)
        {
            int width = 1920, height = 1080, row = 0, col = 0;
            string size = Program.Arg(args, "--size");
            if (size != null)
            {
                string[] parts = size.Split('x');
                width = int.Parse(parts[0]);
                height = int.Parse(parts[1]);
            }
            string focus = Program.Arg(args, "--focus");
            if (focus != null)
            {
                string[] parts = focus.Split(',');
                row = int.Parse(parts[0]);
                col = int.Parse(parts[1]);
            }
            string configPath = Program.Arg(args, "--config");
            Config cfg = configPath != null ? Config.LoadFile(configPath) : Config.Load();

            var window = new HomeWindow(cfg, new HomeOptions { Preview = true });
            FrameworkElement root = window.PreparePreview(row, col, Program.Arg(args, "--state") ?? "");
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream file = File.Create(path)) png.Save(file);
            return 0;
        }

        /// <summary>
        /// CouchTV.exe --updatetest report.txt [--installed sha] [--prepare]: checks GitHub as if the given commit were
        /// installed, and with --prepare also downloads, builds and start-checks the update (without installing it).
        /// </summary>
        public static int UpdateTest(string[] args, string path)
        {
            var report = new StringBuilder();
            Config cfg = Config.Load();
            string installed = Program.Arg(args, "--installed");
            int code = 0;
            try
            {
                report.AppendLine("Repo: " + cfg.UpdateRepo + " (" + cfg.UpdateBranch + "), installed: " + (installed ?? "unknown"));
                AppUpdateInfo update = AppUpdate.Check(cfg.UpdateRepo, cfg.UpdateBranch, installed);
                if (update == null) report.AppendLine("Result: up to date");
                else report.AppendLine("Result: update " + update.Sha + " (" + update.NewCommits + " new) “" + update.Title + "” " + update.Date.ToString("u"));
                AppUpdateInfo again = AppUpdate.Check(cfg.UpdateRepo, cfg.UpdateBranch, installed);   // exercises the not-modified path
                report.AppendLine("Second check agrees: " + ((again == null) == (update == null)));
                if (Program.Has(args, "--prepare") && update != null)
                {
                    PreparedUpdate prepared = AppUpdate.Prepare(cfg.UpdateRepo, update, step => report.AppendLine("  step: " + step));
                    report.AppendLine("Prepared: source " + prepared.Source + ", build " + prepared.Build);
                }
            }
            catch (Exception ex)
            {
                report.AppendLine("FAILED: " + ex.Message);
                code = 1;
            }
            File.WriteAllText(path, report.ToString());
            return code;
        }

        /// <summary>CouchTV.exe --remotetest report.txt: checks the remote-button logic without a receiver.</summary>
        public static int RemoteTest(string path)
        {
            var report = new StringBuilder();
            int failures = 0;
            Action<string, bool> check = (name, ok) =>
            {
                report.AppendLine((ok ? "PASS  " : "FAIL  ") + name);
                if (!ok) failures++;
            };

            IrSignal press = IrLink.ParseLine("IR,NEC 00CE 0001,N");
            IrSignal held = IrLink.ParseLine("IR,Samsung 0707 0002,R");
            check("parse a press", press != null && press.Code == "NEC 00CE 0001" && !press.IsRepeat);
            check("parse a repeat", held != null && held.Code == "Samsung 0707 0002" && held.IsRepeat);
            check("parse a universal-decoder code", IrLink.ParseLine("IR,PulseDistance 1A2B3C4D/48,N").Code == "PulseDistance 1A2B3C4D/48");
            check("ignore other lines", IrLink.ParseLine("COUCHIR 1 0") == null && IrLink.ParseLine("OK") == null);

            RemoteMap map = RemoteMap.FromText(Config.DefaultRemoteText());
            check("phone remote: up", map.ActionFor("NEC 00CE 0001") == "up");
            check("hand-typed code with extra spaces and lower case", map.ActionFor("nec  00ce   0001") == "up");
            check("open a tile", map.ActionFor("NEC 00CE 0040") == "open:Netflix");
            check("unknown button", map.ActionFor("Sony 0001 0015") == null);
            check("one power button", map.CodesFor("power").Count == 1);
            // Must match hashCode() in the receiver firmware (FNV-1a of the upper-cased code text).
            report.AppendLine("      wake hash of 'NEC 00CE 0020' = " + IrLink.Hash("NEC 00CE 0020").ToString("X8"));

            string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "couchtv-remote-test.ini");
            File.WriteAllText(file, "; my comment\r\n[Buttons]\r\nNEC 00CE 0001 = up\r\nNEC 00CE 0002 = down\r\n");
            RemoteMap saved = RemoteMap.LoadFile(file);
            saved.Save(new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Samsung 0707 0060", "up"),
                new KeyValuePair<string, string>("NEC 00CE 0002", "volup"),
            });
            RemoteMap reread = RemoteMap.LoadFile(file);
            string text = File.ReadAllText(file);
            check("save: new remote added", reread.ActionFor("Samsung 0707 0060") == "up");
            check("save: other buttons kept", reread.ActionFor("NEC 00CE 0001") == "up");
            check("save: reassigned button replaced, not duplicated", reread.ActionFor("NEC 00CE 0002") == "volup" && text.IndexOf("= down") < 0);
            check("save: comments kept", text.Contains("; my comment"));
            File.Delete(file);

            report.AppendLine(failures == 0 ? "All passed" : failures + " failed");
            File.WriteAllText(path, report.ToString());
            return failures == 0 ? 0 : 1;
        }

        /// <summary>CouchTV.exe --selftest report.txt: what the launcher sees on this PC, without changing anything.</summary>
        public static int SelfTest(string path)
        {
            var report = new StringBuilder();
            Config cfg = Config.Load();
            report.AppendLine("Config: " + cfg.Source);
            foreach (string warning in cfg.Warnings) report.AppendLine("  warning: " + warning);
            report.AppendLine("Mode now: " + (Shell.ExplorerRunning ? "desktop (Explorer running)" : "TV (no Explorer)"));
            report.AppendLine("Windows update waiting for restart: " + Shell.UpdateWaiting());

            float level;
            bool muted;
            report.AppendLine("Audio: " + (Audio.Change(0, false, out level, out muted)
                ? Math.Round(level * 100) + "%" + (muted ? " (muted)" : "")
                : "no output device"));

            foreach (string browser in new[] { "brave", "edge", "chrome" })
                report.AppendLine("Browser " + browser + ": " + (Launcher.FindBrowser(cfg, browser) ?? "not found"));

            foreach (string name in cfg.HomeKeys)
            {
                HotkeySpec spec = Hotkeys.Parse(name);
                report.AppendLine("Home key '" + name + "': " + (spec == null ? "NOT RECOGNISED"
                    : spec.IsWinTap ? "tap of the Windows key"
                    : spec.IsHold ? "hold vk 0x" + spec.Vk.ToString("X2") + " for 0.7 s"
                    : "vk 0x" + spec.Vk.ToString("X2") + ", modifiers " + spec.Modifiers));
            }

            var tiles = new List<Tile>(cfg.AppTiles);
            tiles.AddRange(cfg.SystemTiles);
            foreach (Tile t in tiles)
                report.AppendLine(string.Format("[{0}] {1,-16} {2,-8} {3}", t.IsSystem ? "system" : "apps  ", t.Label, t.Kind, Launcher.Describe(cfg, t)));

            report.AppendLine("Installed: " + (AppUpdate.IsInstalled ? AppUpdate.InstalledLabel : "not installed (no version.txt), so no update checks") +
                              "; updates from " + cfg.UpdateRepo + " (" + cfg.UpdateBranch + ")" + (cfg.Updates ? "" : ", turned off"));
            try
            {
                AppUpdateInfo update = AppUpdate.Check(cfg.UpdateRepo, cfg.UpdateBranch, AppUpdate.InstalledSha);
                report.AppendLine("Update: " + (update == null ? "none, up to date" : update.ShortSha + " “" + update.Title + "”"));
            }
            catch (Exception ex) { report.AppendLine("Update check failed: " + ex.Message); }
            string receiver = IrLink.Probe(cfg.RemotePort);
            report.AppendLine("IR receiver: " + (receiver ?? "not found (unplugged, or CouchTV is running and using it)"));
            RemoteMap remote = RemoteMap.Load();
            report.AppendLine("Remote buttons in remote.ini: " + remote.Count + ", of which wake the PC: " + remote.CodesFor("power").Count);

            var owners = new List<string>();
            Shell.FindAppWindows(IntPtr.Zero, owners);
            report.AppendLine("Home in TV mode would close windows from: " + (owners.Count == 0 ? "(none)" : string.Join(", ", owners)));
            File.WriteAllText(path, report.ToString());
            return 0;
        }
    }
}
