using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace CouchTV
{
    /// <summary>The newest commit on the update branch.</summary>
    internal sealed class AppUpdateInfo
    {
        public string Sha, Message;
        public DateTime Date;
        public int NewCommits;   // how many commits newer than the installed one; 0 when unknown

        public string ShortSha { get { return Sha.Length > 7 ? Sha.Substring(0, 7) : Sha; } }

        public string Title
        {
            get
            {
                string first = (Message ?? "").Split('\n')[0].Trim();
                return first.Length > 90 ? first.Substring(0, 87) + "..." : first;
            }
        }
    }

    /// <summary>A downloaded, built and checked update, ready to swap in.</summary>
    internal sealed class PreparedUpdate
    {
        public string Source, Build;
    }

    /// <summary>
    /// Keeps CouchTV in step with a branch on GitHub. version.txt (written by the installer and by every update) says
    /// which commit is installed. New versions are installed by downloading that commit's source, building it with the
    /// C# compiler built into Windows, checking that the new build starts and draws, and only then handing over to
    /// scripts\apply-update.ps1, which swaps the files and restarts CouchTV (rolling back if anything fails).
    /// </summary>
    internal static class AppUpdate
    {
        public const string VersionFile = "version.txt";
        const string UserAgent = "CouchTV-updater";

        static readonly object Gate = new object();
        static string _etag;
        static AppUpdateInfo _latest;            // last answer, reused when GitHub says "not modified"
        static string _comparedKey;              // installed...latest pair already compared
        static bool _comparedNewer;
        static int _comparedAhead;

        static AppUpdate()
        {
            // The compiler doesn't stamp a target framework, so .NET would default to old TLS versions GitHub refuses.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        static string VersionPath { get { return Path.Combine(Config.AppDir, VersionFile); } }

        /// <summary>Updates only run for an installed copy (the installer writes version.txt), not previews or dev builds.</summary>
        public static bool IsInstalled { get { return File.Exists(VersionPath); } }

        /// <summary>The installed commit, or null if unknown.</summary>
        public static string InstalledSha
        {
            get
            {
                string line = ReadVersionLine(0);
                return line != null && line.Length >= 7 && IsHex(line) ? line : null;
            }
        }

        /// <summary>The installed commit's message (written by updates), or null.</summary>
        public static string InstalledTitle { get { return ReadVersionLine(1); } }

        public static string InstalledLabel
        {
            get
            {
                string sha = InstalledSha;
                return sha == null ? "unknown version" : "version " + sha.Substring(0, 7);
            }
        }

        static string ReadVersionLine(int index)
        {
            try
            {
                string[] lines = File.ReadAllLines(VersionPath);
                return lines.Length > index && lines[index].Trim().Length > 0 ? lines[index].Trim() : null;
            }
            catch (Exception) { return null; }
        }

        static bool IsHex(string s)
        {
            foreach (char c in s) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        // ---------------------------------------------------------------- checking

        /// <summary>
        /// The branch's newest commit if it is newer than the installed one, otherwise null. Throws on network errors.
        /// Runs on a background thread.
        /// </summary>
        public static AppUpdateInfo Check(string repo, string branch, string installedSha)
        {
            AppUpdateInfo latest = LatestCommit(repo, branch);
            if (installedSha == null) return latest;                       // unknown install: offer the current version
            if (latest.Sha.Equals(installedSha, StringComparison.OrdinalIgnoreCase)) return null;

            // Only offer commits that come after the installed one (not older ones, e.g. after a branch reset).
            string key = installedSha + "..." + latest.Sha;
            lock (Gate)
            {
                if (_comparedKey != key)
                {
                    string status;
                    int ahead;
                    if (Compare(repo, installedSha, latest.Sha, out status, out ahead))
                    {
                        _comparedNewer = status != "behind" && status != "identical";
                        _comparedAhead = ahead;
                    }
                    else
                    {
                        _comparedNewer = true;   // installed commit isn't on GitHub (e.g. installed from local changes)
                        _comparedAhead = 0;
                    }
                    _comparedKey = key;
                }
                if (!_comparedNewer) return null;
                latest.NewCommits = _comparedAhead;
            }
            return latest;
        }

        static AppUpdateInfo LatestCommit(string repo, string branch)
        {
            HttpWebRequest request = Request("https://api.github.com/repos/" + repo + "/commits/" + Uri.EscapeDataString(branch));
            lock (Gate)
                if (_etag != null && _latest != null) request.Headers[HttpRequestHeader.IfNoneMatch] = _etag;
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    Dictionary<string, object> data = ParseJson(ReadAll(response));
                    var commit = (Dictionary<string, object>)data["commit"];
                    var committer = (Dictionary<string, object>)commit["committer"];
                    var latest = new AppUpdateInfo
                    {
                        Sha = (string)data["sha"],
                        Message = (string)commit["message"],
                        Date = DateTime.Parse((string)committer["date"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime(),
                    };
                    lock (Gate)
                    {
                        _latest = latest;
                        _etag = response.Headers[HttpResponseHeader.ETag];
                    }
                    return Copy(latest);
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotModified)
                    lock (Gate) if (_latest != null) return Copy(_latest);   // nothing new; doesn't count against GitHub's limit
                throw new InvalidOperationException(Describe(ex), ex);
            }
        }

        static bool Compare(string repo, string from, string to, out string status, out int ahead)
        {
            status = null;
            ahead = 0;
            try
            {
                HttpWebRequest request = Request("https://api.github.com/repos/" + repo + "/compare/" + from + "..." + to + "?per_page=1");
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    Dictionary<string, object> data = ParseJson(ReadAll(response));
                    status = data["status"] as string;
                    ahead = Convert.ToInt32(data["ahead_by"], CultureInfo.InvariantCulture);
                    return status != null;
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound) return false;
                throw new InvalidOperationException(Describe(ex), ex);
            }
        }

        static AppUpdateInfo Copy(AppUpdateInfo info)
        {
            return new AppUpdateInfo { Sha = info.Sha, Message = info.Message, Date = info.Date };
        }

        static HttpWebRequest Request(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = UserAgent;
            request.Accept = "application/vnd.github+json";
            request.Timeout = 20000;
            request.ReadWriteTimeout = 20000;
            return request;
        }

        static string ReadAll(HttpWebResponse response)
        {
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) return reader.ReadToEnd();
        }

        static Dictionary<string, object> ParseJson(string json)
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(json);
        }

        static string Describe(WebException ex)
        {
            var response = ex.Response as HttpWebResponse;
            if (response == null) return "no connection to GitHub";
            if ((int)response.StatusCode == 403 || (int)response.StatusCode == 429) return "GitHub is limiting requests; trying again later";
            if (response.StatusCode == HttpStatusCode.NotFound) return "the update repository or branch wasn't found";
            return "GitHub answered " + (int)response.StatusCode;
        }

        // ---------------------------------------------------------------- installing

        /// <summary>
        /// Downloads, builds and checks a commit without touching the installed copy. Throws with a readable message.
        /// Runs on a background thread; progress gets short step names.
        /// </summary>
        public static PreparedUpdate Prepare(string repo, AppUpdateInfo update, Action<string> progress)
        {
            string root = Path.Combine(Config.DataDir, "update");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);

            progress("Downloading");
            string zip = Path.Combine(root, "source.zip");
            try
            {
                using (var client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                    client.DownloadFile("https://codeload.github.com/" + repo + "/zip/" + update.Sha, zip);
                }
            }
            catch (WebException ex) { throw new InvalidOperationException("Download failed (" + Describe(ex) + ").", ex); }

            progress("Unpacking");
            string unpacked = Path.Combine(root, "unpacked");
            ZipFile.ExtractToDirectory(zip, unpacked);
            string source = FindSource(unpacked);
            if (source == null) throw new InvalidOperationException("The download doesn't contain CouchTV's build script.");

            progress("Building");
            string build = Path.Combine(root, "build");
            string output;
            int code = Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(source, "scripts", "build.ps1") + "\" -OutDir \"" + build + "\"", 300000, out output);
            Log.Info("Update build (exit " + code + "): " + output.Trim());
            string exe = Path.Combine(build, "CouchTV.exe");
            if (code != 0 || !File.Exists(exe)) throw new InvalidOperationException("The new version didn't build.");

            // Draw the new version's home screen offscreen: proves it starts before anything is replaced.
            progress("Checking the new version");
            string picture = Path.Combine(root, "check.png");
            code = Run(exe, "--screenshot \"" + picture + "\" --config \"" + Path.Combine(source, Config.FileName) + "\"", 60000, out output);
            if (code != 0 || !File.Exists(picture)) throw new InvalidOperationException("The new version failed its start-up check.");

            File.WriteAllLines(Path.Combine(build, VersionFile), new[] { update.Sha, update.Title, update.Date.ToString("o", CultureInfo.InvariantCulture) });
            return new PreparedUpdate { Source = source, Build = build };
        }

        /// <summary>Starts the new version's apply-update.ps1, which waits for this process to exit. The caller then exits.</summary>
        public static void Apply(PreparedUpdate update)
        {
            string script = Path.Combine(update.Source, "scripts", "apply-update.ps1");
            if (!File.Exists(script)) throw new InvalidOperationException("The new version has no apply-update script.");
            string installDir = Config.AppDir.TrimEnd('\\');   // a trailing \ would escape the closing quote
            string args = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"" +
                " -Source \"" + update.Source + "\" -Build \"" + update.Build + "\" -InstallDir \"" + installDir + "\"" +
                " -WaitPid " + Process.GetCurrentProcess().Id;
            using (Process.Start(new ProcessStartInfo("powershell.exe", args) { UseShellExecute = false, CreateNoWindow = true })) { }
        }

        static string FindSource(string folder)
        {
            if (File.Exists(Path.Combine(folder, "scripts", "build.ps1"))) return folder;
            foreach (string child in Directory.GetDirectories(folder))
                if (File.Exists(Path.Combine(child, "scripts", "build.ps1"))) return child;
            return null;
        }

        static int Run(string exe, string args, int timeoutMs, out string output)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            var text = new StringBuilder();
            using (var process = new Process { StartInfo = psi })
            {
                process.OutputDataReceived += (s, e) => { if (e.Data != null) lock (text) text.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (text) text.AppendLine(e.Data); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch (Exception) { }
                    output = text.ToString();
                    return -1;
                }
                process.WaitForExit();   // flush the output events
                output = text.ToString();
                return process.ExitCode;
            }
        }
    }
}
