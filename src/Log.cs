using System;
using System.IO;

namespace CouchTV
{
    /// <summary>Tiny append-only log in %LOCALAPPDATA%\CouchTV\couchtv.log, for troubleshooting.</summary>
    internal static class Log
    {
        static string _path;

        public static string FilePath { get { return _path; } }

        public static void Init()
        {
            try
            {
                Directory.CreateDirectory(Config.DataDir);
                _path = Path.Combine(Config.DataDir, "couchtv.log");
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > 512 * 1024) File.Delete(_path);
            }
            catch { _path = null; }
        }

        public static void Info(string message) { Write("INFO ", message); }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", ex == null ? message : message + ": " + ex);
        }

        static void Write(string level, string message)
        {
            if (_path == null) return;
            try
            {
                File.AppendAllText(_path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + level + " " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
