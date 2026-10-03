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
