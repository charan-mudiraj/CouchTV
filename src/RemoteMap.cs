using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace CouchTV
{
    /// <summary>
    /// remote.ini: which remote button (receiver code) does what. Remote setup writes it; people can edit it too.
    /// Any number of remotes can be mapped at once, since every remote's buttons have different codes.
    /// </summary>
    internal sealed class RemoteMap
    {
        public const string FileName = "remote.ini";
        const string ResourceName = "CouchTV.remote.ini";

        readonly Dictionary<string, string> _actions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string _path;

        public int Count { get { return _actions.Count; } }

        public static RemoteMap Load()
        {
            var map = new RemoteMap { _path = Path.Combine(Config.AppDir, FileName) };
            try
            {
                if (!File.Exists(map._path)) File.WriteAllText(map._path, DefaultText());
                map.Parse(File.ReadAllLines(map._path));
            }
            catch (Exception ex)
            {
                Log.Error("Reading " + map._path, ex);
                map.Parse(DefaultText().Split('\n'));
            }
            return map;
        }

        public static RemoteMap FromText(string text)
        {
            var map = new RemoteMap();
            map.Parse(text.Split('\n'));
            return map;
        }

        static string DefaultText()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd();
        }

        /// <summary>Codes may be typed by hand, so compare them with single spaces.</summary>
        public static string Normalize(string code)
        {
            return string.Join(" ", code.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        void Parse(IEnumerable<string> lines)
        {
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string code = Normalize(line.Substring(0, eq));
                string action = line.Substring(eq + 1).Trim();
                if (code.Length > 0 && action.Length > 0) _actions[code] = action;
            }
        }

        public string ActionFor(string code)
        {
            string action;
            return _actions.TryGetValue(Normalize(code), out action) ? action : null;
        }

        public List<string> CodesFor(string action)
        {
            var codes = new List<string>();
            foreach (KeyValuePair<string, string> pair in _actions)
                if (pair.Value.Equals(action, StringComparison.OrdinalIgnoreCase)) codes.Add(pair.Key);
            return codes;
        }

        /// <summary>
        /// Adds button assignments from Remote setup. Lines for the same buttons are replaced; everything else in the
        /// file (other remotes, comments) is kept.
        /// </summary>
        public void Save(List<KeyValuePair<string, string>> assignments)
        {
            var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in assignments) codes.Add(Normalize(pair.Key));

            var output = new StringBuilder();
            foreach (string raw in File.Exists(_path) ? File.ReadAllLines(_path) : new string[0])
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                bool replaced = eq > 0 && line[0] != ';' && line[0] != '#' && codes.Contains(Normalize(line.Substring(0, eq)));
                if (!replaced) output.AppendLine(raw.TrimEnd());
            }
            output.AppendLine();
            output.AppendLine("; Added by Remote setup on " + DateTime.Now.ToString("d MMM yyyy, HH:mm"));
            foreach (KeyValuePair<string, string> pair in assignments)
                output.AppendLine(Normalize(pair.Key) + " = " + pair.Value);
            File.WriteAllText(_path, output.ToString());

            foreach (KeyValuePair<string, string> pair in assignments) _actions[Normalize(pair.Key)] = pair.Value;
        }
    }
}
