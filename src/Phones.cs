using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CouchTV
{
    /// <summary>
    /// The phones paired with this TV, and the key the receiver box's Wi-Fi (CouchLink) checks Power and status
    /// requests with. Kept in %LOCALAPPDATA%\CouchTV\phones.dat, encrypted by Windows for this user (DPAPI), so it's
    /// useless copied elsewhere. Thread-safe.
    /// </summary>
    internal sealed class PhoneStore
    {
        public const int MostPhones = 20;

        public sealed class Phone
        {
            public string Id;        // 32 hex digits the phone chose
            public string Name;      // e.g. "OPPO CPH2591", as the phone said when pairing
            public byte[] Key;       // the pairing key: proves each connection comes from this phone
            public DateTime Paired;  // UTC
        }

        static readonly byte[] Entropy = Encoding.ASCII.GetBytes("CouchTV phones");
        readonly string _path;   // null: kept in memory only (the self-test)
        readonly object _lock = new object();
        readonly List<Phone> _phones = new List<Phone>();
        string _tvId;
        byte[] _boardKey;

        PhoneStore(string path) { _path = path; }

        public static string DefaultPath { get { return Path.Combine(Config.DataDir, "phones.dat"); } }

        public static PhoneStore InMemory()
        {
            var store = new PhoneStore(null);
            store.Fill();
            return store;
        }

        public static PhoneStore Load()
        {
            var store = new PhoneStore(DefaultPath);
            try
            {
                if (File.Exists(store._path))
                {
                    byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(store._path), Entropy, DataProtectionScope.CurrentUser);
                    store.Parse(new UTF8Encoding(false).GetString(plain));
                }
            }
            catch (Exception ex) { Log.Info("Paired phones: couldn't read " + store._path + " (" + ex.Message + "), starting afresh"); }
            if (store.Fill()) store.Save();
            return store;
        }

        /// <summary>This TV's id, which phones file their pairing key under.</summary>
        public string TvId { get { lock (_lock) return _tvId; } }

        public byte[] BoardKey { get { lock (_lock) return (byte[])_boardKey.Clone(); } }

        public int Count { get { lock (_lock) return _phones.Count; } }

        public Phone Find(string id)
        {
            lock (_lock)
                foreach (Phone p in _phones)
                    if (p.Id == id) return p;
            return null;
        }

        public void Add(Phone phone)
        {
            lock (_lock)
            {
                _phones.RemoveAll(p => p.Id == phone.Id);
                _phones.Add(phone);
                while (_phones.Count > MostPhones) _phones.RemoveAt(0);   // the longest-paired goes
                Save();
            }
        }

        /// <summary>
        /// Forgets every phone, and changes the receiver box's key, so a forgotten phone can't press Power through it
        /// either. Phones still wanted pair again, and get the new box key as they connect.
        /// </summary>
        public void ForgetAll()
        {
            lock (_lock)
            {
                _phones.Clear();
                _boardKey = Secure.Random(32);
                Save();
            }
        }

        bool Fill()
        {
            bool changed = false;
            if (_tvId == null) { _tvId = Secure.Hex(Secure.Random(16)); changed = true; }
            if (_boardKey == null) { _boardKey = Secure.Random(32); changed = true; }
            return changed;
        }

        // "CouchTV phones 1", then "tv <id>", "board <key>", and "phone <id> <key> <unix seconds> <name, base64>" lines.
        void Parse(string text)
        {
            foreach (string raw in text.Split('\n'))
            {
                string[] p = raw.Trim().Split(' ');
                if (p.Length == 2 && p[0] == "tv" && p[1].Length == 32) _tvId = p[1];
                else if (p.Length == 2 && p[0] == "board" && p[1].Length == 64) _boardKey = Secure.FromHex(p[1]);
                else if (p.Length == 5 && p[0] == "phone")
                {
                    byte[] key = Secure.FromHex(p[2]), name = Secure.FromBase64(p[4]);
                    long seconds;
                    if (key == null || key.Length != 32 || name == null || !long.TryParse(p[3], out seconds)) continue;
                    _phones.Add(new Phone { Id = p[1], Key = key, Name = new UTF8Encoding(false).GetString(name), Paired = Epoch.AddSeconds(seconds) });
                }
            }
        }

        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        void Save()
        {
            if (_path == null) return;
            var text = new StringBuilder("CouchTV phones 1\n");
            text.Append("tv ").Append(_tvId).Append('\n');
            text.Append("board ").Append(Secure.Hex(_boardKey)).Append('\n');
            foreach (Phone p in _phones)
                text.Append("phone ").Append(p.Id).Append(' ').Append(Secure.Hex(p.Key)).Append(' ')
                    .Append((long)(p.Paired - Epoch).TotalSeconds).Append(' ').Append(Convert.ToBase64String(Secure.Utf8(p.Name))).Append('\n');
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                byte[] protectedBytes = ProtectedData.Protect(Secure.Utf8(text.ToString()), Entropy, DataProtectionScope.CurrentUser);
                string temp = _path + ".new";
                File.WriteAllBytes(temp, protectedBytes);
                if (File.Exists(_path)) File.Replace(temp, _path, null);
                else File.Move(temp, _path);
            }
            catch (Exception ex) { Log.Error("Saving paired phones to " + _path, ex); }
        }
    }
}
