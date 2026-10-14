using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CouchTV
{
    /// <summary>
    /// The cryptography that keeps the TV to your own phones on a shared Wi-Fi ("Security" in ir-receiver/PROTOCOL.md):
    /// ECDH on the P-256 curve to agree keys, HKDF-SHA256 to derive them, and AES-256 in counter mode with an
    /// HMAC-SHA256 tag for each message. All of it is built into Windows. The phone app does the same in Secure.kt, and
    /// both are checked against the same numbers (Diagnostics.SecurityVectors).
    /// </summary>
    internal static class Secure
    {
        public const int TagBytes = 16;
        static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        public static byte[] Random(int count)
        {
            var bytes = new byte[count];
            lock (Rng) Rng.GetBytes(bytes);
            return bytes;
        }

        /// <summary>0 to 999999, evenly: the pairing code.</summary>
        public static int RandomCode()
        {
            while (true)
            {
                uint value = BitConverter.ToUInt32(Random(4), 0);
                if (value < 4294000000u) return (int)(value % 1000000);
            }
        }

        public static byte[] Utf8(string text) { return new UTF8Encoding(false).GetBytes(text); }

        public static byte[] Join(params byte[][] parts)
        {
            int length = 0;
            foreach (byte[] p in parts) length += p.Length;
            var all = new byte[length];
            int at = 0;
            foreach (byte[] p in parts)
            {
                Buffer.BlockCopy(p, 0, all, at, p.Length);
                at += p.Length;
            }
            return all;
        }

        public static byte[] Take(byte[] bytes, int offset, int count)
        {
            var part = new byte[count];
            Buffer.BlockCopy(bytes, offset, part, 0, count);
            return part;
        }

        public static byte[] Sha256(params byte[][] parts)
        {
            using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(Join(parts));
        }

        public static byte[] Hmac(byte[] key, params byte[][] parts)
        {
            using (var mac = new HMACSHA256(key)) return mac.ComputeHash(Join(parts));
        }

        /// <summary>HKDF with SHA-256 (RFC 5869).</summary>
        public static byte[] Hkdf(byte[] secret, byte[] salt, byte[] info, int length)
        {
            byte[] prk = Hmac(salt != null && salt.Length > 0 ? salt : new byte[32], secret);
            var okm = new byte[length];
            byte[] block = new byte[0];
            for (int done = 0, i = 1; done < length; i++)
            {
                block = Hmac(prk, block, info, new[] { (byte)i });
                int n = Math.Min(block.Length, length - done);
                Buffer.BlockCopy(block, 0, okm, done, n);
                done += n;
            }
            return okm;
        }

        /// <summary>Equal, taking the same time wherever they differ (so a forger can't learn a tag byte by byte).</summary>
        public static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static string Hex(byte[] bytes)
        {
            var text = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) text.Append(b.ToString("x2"));
            return text.ToString();
        }

        /// <summary>Null unless it's an even number of hex digits.</summary>
        public static byte[] FromHex(string text)
        {
            if (text == null || text.Length % 2 != 0) return null;
            var bytes = new byte[text.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                int hi = HexDigit(text[2 * i]), lo = HexDigit(text[2 * i + 1]);
                if (hi < 0 || lo < 0) return null;
                bytes[i] = (byte)(hi * 16 + lo);
            }
            return bytes;
        }

        static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        /// <summary>Null if it isn't base64.</summary>
        public static byte[] FromBase64(string text)
        {
            try { return text == null ? null : Convert.FromBase64String(text); }
            catch (FormatException) { return null; }
        }

        /// <summary>
        /// A pairing commitment: binds one bit of the code to both sides' keys without revealing it until the nonce
        /// is shown. role is 'P' for the phone's, 'T' for the TV's; first is the committer's own key.
        /// </summary>
        public static byte[] Commit(byte[] nonce, char role, byte[] first, byte[] second, int bit)
        {
            return Hmac(nonce, new[] { (byte)role }, first, second, new[] { (byte)bit });
        }
    }

    /// <summary>
    /// AES-256 in counter mode. The counter block is an 8-byte nonce then an 8-byte block number from 0, both
    /// big-endian: the same blocks Java's "AES/CTR/NoPadding" makes from that 16-byte IV.
    /// </summary>
    internal sealed class Ctr : IDisposable
    {
        readonly Aes _aes;
        readonly ICryptoTransform _ecb;
        byte[] _counters = new byte[0], _stream = new byte[0];

        public Ctr(byte[] key)
        {
            _aes = Aes.Create();
            _aes.Mode = CipherMode.ECB;
            _aes.Padding = PaddingMode.None;
            _aes.Key = key;
            _ecb = _aes.CreateEncryptor();
        }

        /// <summary>Encrypts or decrypts (the same thing) count bytes in place.</summary>
        public void Apply(ulong nonce, byte[] data, int offset, int count, ulong firstBlock = 0)
        {
            int blocks = (count + 15) / 16;
            if (_counters.Length < blocks * 16)
            {
                _counters = new byte[blocks * 16];
                _stream = new byte[blocks * 16];
            }
            for (int b = 0; b < blocks; b++)
            {
                BigEndian(nonce, _counters, b * 16);
                BigEndian(firstBlock + (ulong)b, _counters, b * 16 + 8);
            }
            _ecb.TransformBlock(_counters, 0, blocks * 16, _stream, 0);
            for (int i = 0; i < count; i++) data[offset + i] ^= _stream[i];
        }

        public static void BigEndian(ulong value, byte[] to, int at)
        {
            for (int i = 7; i >= 0; i--)
            {
                to[at + i] = (byte)value;
                value >>= 8;
            }
        }

        public void Dispose()
        {
            _ecb.Dispose();
            _aes.Dispose();
        }
    }

    /// <summary>
    /// A one-off ECDH key on the P-256 curve. Public keys travel as the 65-byte uncompressed point (04, x, y); the
    /// agreed secret is SHA-256 of the shared x coordinate, which is what Windows' ECDH gives with these settings.
    /// </summary>
    internal sealed class EcdhKey : IDisposable
    {
        const int PublicMagic = 0x314B4345, PrivateMagic = 0x324B4345;   // "ECK1" / "ECK2": BCRYPT_ECDH_*_P256_MAGIC
        readonly ECDiffieHellmanCng _ecdh;

        public EcdhKey() : this(new ECDiffieHellmanCng(256)) { }

        /// <summary>A fixed key, for the self-test's known answers: d is the private number, point the public key.</summary>
        public EcdhKey(byte[] d, byte[] point) : this(new ECDiffieHellmanCng(CngKey.Import(Blob(PrivateMagic, point, d), CngKeyBlobFormat.EccPrivateBlob))) { }

        EcdhKey(ECDiffieHellmanCng ecdh)
        {
            _ecdh = ecdh;
            _ecdh.KeyDerivationFunction = ECDiffieHellmanKeyDerivationFunction.Hash;
            _ecdh.HashAlgorithm = CngAlgorithm.Sha256;
        }

        public byte[] PublicPoint
        {
            get
            {
                byte[] blob = _ecdh.PublicKey.ToByteArray();   // magic, length, x, y
                var point = new byte[65];
                point[0] = 4;
                Buffer.BlockCopy(blob, 8, point, 1, 64);
                return point;
            }
        }

        /// <summary>SHA-256 of the shared secret. Throws if the other side's key isn't a point on the curve.</summary>
        public byte[] Agree(byte[] point)
        {
            if (point == null || point.Length != 65 || point[0] != 4) throw new CryptographicException("not a P-256 public key");
            using (ECDiffieHellmanPublicKey other = ECDiffieHellmanCngPublicKey.FromByteArray(Blob(PublicMagic, point, null), CngKeyBlobFormat.EccPublicBlob))
                return _ecdh.DeriveKeyMaterial(other);
        }

        static byte[] Blob(int magic, byte[] point, byte[] d)
        {
            var blob = new MemoryStream();
            blob.Write(BitConverter.GetBytes(magic), 0, 4);
            blob.Write(BitConverter.GetBytes(32), 0, 4);
            blob.Write(point, 1, 64);
            if (d != null) blob.Write(d, 0, 32);
            return blob.ToArray();
        }

        public void Dispose() { _ecdh.Dispose(); }
    }

    /// <summary>
    /// The keys for one connection, from the ECDH secret, the pairing key and everything both sides said first, so a
    /// connection can't be replayed or spliced. Each direction has its own; the sound has its own too.
    /// </summary>
    internal sealed class SessionKeys
    {
        public byte[] PhoneProof, TvProof;
        public byte[] PhoneEnc, PhoneMac, TvEnc, TvMac;   // what the phone sends; what the TV sends
        public byte[] AudioEnc, AudioMac;
        public byte[] MirrorEnc, MirrorMac;   // the screen mirror's pictures, TV to phone, on their own connection

        public static SessionKeys Derive(byte[] shared, byte[] pairKey, byte[] transcript)
        {
            byte[] okm = Secure.Hkdf(shared, pairKey, Secure.Join(Secure.Utf8("CouchTV session 1"), transcript), 7 * 32);
            byte[] confirm = Secure.Take(okm, 0, 32);
            var keys = new SessionKeys
            {
                PhoneProof = Secure.Hmac(confirm, Secure.Utf8("phone")),
                TvProof = Secure.Hmac(confirm, Secure.Utf8("tv")),
                PhoneEnc = Secure.Take(okm, 32, 32), PhoneMac = Secure.Take(okm, 64, 32),
                TvEnc = Secure.Take(okm, 96, 32), TvMac = Secure.Take(okm, 128, 32),
                AudioEnc = Secure.Take(okm, 160, 32), AudioMac = Secure.Take(okm, 192, 32),
            };
            keys.MirrorEnc = Secure.Hmac(keys.TvEnc, Secure.Utf8("mirror enc"));
            keys.MirrorMac = Secure.Hmac(keys.TvMac, Secure.Utf8("mirror mac"));
            return keys;
        }

        /// <summary>The key a pairing leaves both sides with, once the code checked out.</summary>
        public static byte[] PairKey(byte[] shared, string code, byte[] transcript)
        {
            return Secure.Hkdf(shared, Secure.Utf8(code), Secure.Join(Secure.Utf8("CouchTV pair 1"), transcript), 32);
        }
    }

    /// <summary>
    /// Lines of text, encrypted: each one is base64 of the AES-CTR ciphertext and a 16-byte HMAC tag over the message
    /// number and ciphertext. Message numbers count from 0 in each direction and are never sent: a line that's
    /// replayed, dropped, reordered or changed fails its tag. Not thread-safe: one sender and one receiver.
    /// </summary>
    internal sealed class Channel : IDisposable
    {
        readonly Ctr _sendCipher, _receiveCipher;
        readonly HMACSHA256 _sendMac, _receiveMac;
        ulong _sent, _received;

        public Channel(byte[] sendEnc, byte[] sendMac, byte[] receiveEnc, byte[] receiveMac)
        {
            _sendCipher = new Ctr(sendEnc);
            _receiveCipher = new Ctr(receiveEnc);
            _sendMac = new HMACSHA256(sendMac);
            _receiveMac = new HMACSHA256(receiveMac);
        }

        public string Seal(string line)
        {
            byte[] text = Secure.Utf8(line);
            var sealedText = new byte[text.Length + Secure.TagBytes];
            Buffer.BlockCopy(text, 0, sealedText, 0, text.Length);
            _sendCipher.Apply(_sent, sealedText, 0, text.Length);
            Buffer.BlockCopy(Tag(_sendMac, _sent, sealedText, text.Length), 0, sealedText, text.Length, Secure.TagBytes);
            _sent++;
            return Convert.ToBase64String(sealedText);
        }

        /// <summary>The line, or null if it wasn't sealed by the other side in this order.</summary>
        public string Open(string line)
        {
            byte[] sealedText = Secure.FromBase64(line);
            if (sealedText == null || sealedText.Length < Secure.TagBytes) return null;
            int length = sealedText.Length - Secure.TagBytes;
            if (!Secure.Same(Tag(_receiveMac, _received, sealedText, length), Secure.Take(sealedText, length, Secure.TagBytes))) return null;
            _receiveCipher.Apply(_received, sealedText, 0, length);
            _received++;
            try { return new UTF8Encoding(false, true).GetString(sealedText, 0, length); }
            catch (ArgumentException) { return null; }
        }

        static byte[] Tag(HMACSHA256 mac, ulong number, byte[] data, int length)
        {
            var counter = new byte[8];
            Ctr.BigEndian(number, counter, 0);
            mac.Initialize();   // TransformFinalBlock leaves the inner hash as it was
            mac.TransformBlock(counter, 0, 8, null, 0);
            mac.TransformFinalBlock(data, 0, length);
            return Secure.Take(mac.Hash, 0, Secure.TagBytes);
        }

        public void Dispose()
        {
            _sendCipher.Dispose();
            _receiveCipher.Dispose();
            _sendMac.Dispose();
            _receiveMac.Dispose();
        }
    }

    /// <summary>Reads lines of UTF-8 from a connection, refusing any longer than a limit (so a stranger can't fill memory).</summary>
    internal sealed class LineReader
    {
        readonly Stream _stream;
        readonly int _most;
        readonly byte[] _buffer = new byte[4096];
        int _start, _end;

        public LineReader(Stream stream, int most)
        {
            _stream = stream;
            _most = most;
        }

        /// <summary>The next line without its line break; null when the connection closed or the line was too long.</summary>
        public string Read()
        {
            var line = new MemoryStream();
            while (true)
            {
                if (_start == _end)
                {
                    _start = 0;
                    _end = _stream.Read(_buffer, 0, _buffer.Length);
                    if (_end <= 0) return null;
                }
                int newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                int stop = newline >= 0 ? newline : _end;
                line.Write(_buffer, _start, stop - _start);
                _start = newline >= 0 ? newline + 1 : _end;
                if (line.Length > _most) return null;
                if (newline >= 0) return new UTF8Encoding(false).GetString(line.ToArray()).TrimEnd('\r');
            }
        }
    }
}
