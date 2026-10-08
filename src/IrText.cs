using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CouchTV
{
    internal enum IrTextResult { NotText, Started, Progress, Done, Failed }

    /// <summary>
    /// Text from the phone remote (voice search) over infrared. See "Text" in ir-receiver/PROTOCOL.md.
    ///
    /// The phone sends the start button "NEC 00CE 0052", then one NEC frame per two bytes of UTF-8 text, then two
    /// check frames holding a CRC-16. The receiver reports every frame like a button, so no firmware change is needed:
    ///   data frame:   address byte 0 = CE, byte 1 = Pack(second byte); command = first byte (with its inverse)
    ///   check frame:  the same, with Pack(0xFE) and a CRC byte (high byte first)
    /// Byte 1 is never 00 or 31, so the receiver prints these as "NEC xxCE 00yy" (or NEC2 when they come quickly)
    /// and never confuses them with the phone's buttons, which are "NEC 00CE 00yy".
    /// </summary>
    internal sealed class IrText
    {
        public const string VoiceCode = "NEC 00CE 0051";   // the phone started listening
        public const string StartCode = "NEC 00CE 0052";   // text follows
        public const string CancelCode = "NEC 00CE 0053";  // the phone stopped listening without any words
        public const int MaxBytes = 120;
        public const byte CheckMarker = 0xFE, Pad = 0xFF;  // neither ever appears in UTF-8

        static readonly Regex Frame = new Regex(@"^NEC2? ([0-9A-F]{2})CE 00([0-9A-F]{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        readonly List<byte> _bytes = new List<byte>();
        readonly List<byte> _check = new List<byte>();
        bool _active;
        DateTime _last;

        /// <summary>The finished text after Done, or why it failed after Failed.</summary>
        public string Text { get; private set; }
        public string Error { get; private set; }
        public bool Active { get { return _active; } }

        /// <summary>Takes a receiver code. NotText means it's an ordinary button.</summary>
        public IrTextResult Handle(string code, DateTime now)
        {
            string normalized = RemoteMap.Normalize(code).ToUpperInvariant();
            if (normalized == StartCode || normalized == "NEC2" + StartCode.Substring(3))
            {
                _active = true;
                _last = now;
                _bytes.Clear();
                _check.Clear();
                return IrTextResult.Started;
            }
            if (!_active) return IrTextResult.NotText;   // other remotes may use addresses like 12CE too

            Match m = Frame.Match(normalized);
            if (!m.Success || m.Groups[1].Value == "00") return IrTextResult.NotText;
            _last = now;
            byte first = byte.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte second = Unpack(byte.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

            if (second == CheckMarker)
            {
                _check.Add(first);
                return _check.Count < 2 ? IrTextResult.Progress : Finish();
            }
            if (_check.Count > 0) return Fail("a check frame came too early");
            _bytes.Add(first);
            if (second != Pad) _bytes.Add(second);
            if (_bytes.Count > MaxBytes) return Fail("the text is too long");
            return IrTextResult.Progress;
        }

        /// <summary>A message that stopped arriving part way: a frame was missed (the phone pointed away).</summary>
        public bool TimedOut(DateTime now)
        {
            if (!_active || (now - _last).TotalMilliseconds < 700) return false;
            Fail("the text stopped arriving");
            return true;
        }

        public void Reset()
        {
            _active = false;
            _bytes.Clear();
            _check.Clear();
        }

        IrTextResult Finish()
        {
            _active = false;
            int expected = (_check[0] << 8) | _check[1];
            byte[] data = _bytes.ToArray();
            if (Crc16(data) != expected) return Fail("the check didn't match (a frame was garbled)");
            try
            {
                Text = Utf8.GetString(data).Trim();
            }
            catch (DecoderFallbackException)
            {
                return Fail("the text wasn't valid UTF-8");
            }
            if (Text.Length == 0) return Fail("the text was empty");
            Error = null;
            return IrTextResult.Done;
        }

        IrTextResult Fail(string reason)
        {
            _active = false;
            Error = reason;
            Text = null;
            Log.Info("Remote text: " + reason);
            return IrTextResult.Failed;
        }

        // ---------------------------------------------------------------- the format (shared with the phone app)

        /// <summary>
        /// Byte 1 of the address must not be 00 or 31: 31 (= ~CE) would make the receiver print "00CE", the phone's
        /// buttons. XOR with C0 moves those to C0 and F1; C0 never appears in UTF-8, and F1 is sent as 01 instead
        /// (01 would only come from C1, which also never appears in UTF-8).
        /// </summary>
        public static byte Pack(byte value) { return value == 0xF1 ? (byte)0x01 : (byte)(value ^ 0xC0); }

        public static byte Unpack(byte value) { return value == 0x01 ? (byte)0xF1 : (byte)(value ^ 0xC0); }

        /// <summary>CRC-16/CCITT-FALSE: polynomial 1021, start FFFF. "123456789" gives 29B1.</summary>
        public static int Crc16(byte[] data)
        {
            int crc = 0xFFFF;
            foreach (byte b in data)
            {
                crc ^= b << 8;
                for (int i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
            }
            return crc;
        }

        /// <summary>The 32-bit frames the phone sends for a text, as bytes 0..3 (for the self-test).</summary>
        public static List<byte[]> Encode(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            var frames = new List<byte[]>();
            for (int i = 0; i < data.Length; i += 2)
            {
                byte second = i + 1 < data.Length ? data[i + 1] : Pad;
                frames.Add(Raw(data[i], second));
            }
            int crc = Crc16(data);
            frames.Add(Raw((byte)(crc >> 8), CheckMarker));
            frames.Add(Raw((byte)crc, CheckMarker));
            return frames;
        }

        static byte[] Raw(byte first, byte second)
        {
            return new[] { (byte)0xCE, Pack(second), first, (byte)~first };
        }
    }
}
