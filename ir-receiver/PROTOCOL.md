# CouchTV phone remote: signal format

This is the contract between the remote app ([`phone-remote/`](../phone-remote/README.md), for the Mi phone's IR blaster) and CouchTV. The receiver accepts any remote, so the app can send any code. These defaults are already mapped in `remote.ini`, so the app works with no setup.

## Transmission

- **Protocol:** NEC, 38 kHz carrier, sent with Android's [`ConsumerIrManager`](https://developer.android.com/reference/android/hardware/ConsumerIrManager). It needs the `android.permission.TRANSMIT_IR` permission (a normal permission, granted at install). Check `hasIrEmitter()` first.
- **Address:** `0xCE`, sent as `CE` then its inverse `31`. The receiver reports these frames as `NEC 00CE 00xx`, where `xx` is the command in hex.
- **Bytes, least significant bit first:** `address, ~address, command, ~command`.
- **Timing (µs):** leader 9000 on + 4500 off. A `0` bit is 560 on + 560 off; a `1` bit is 560 on + 1690 off. Stop bit: 560 on. Pad with silence so one frame lasts **108 ms**.
- **Holding a button:** after the first frame, send a **repeat frame** (9000 on, 2250 off, 560 on, then silence to 108 ms) every 108 ms until the button is released. CouchTV repeats arrows and volume while it keeps getting these frames, moves the pointer smoothly, and treats a held Back as Home.

```kotlin
const val COUCHTV_ADDRESS = 0xCE
const val CARRIER_HZ = 38_000

/** One NEC frame, padded to 108 ms. */
fun necFrame(command: Int, address: Int = COUCHTV_ADDRESS): IntArray {
    val pattern = mutableListOf(9000, 4500)
    for (byte in intArrayOf(address, address.inv() and 0xFF, command, command.inv() and 0xFF)) {
        for (bit in 0 until 8) {
            pattern += 560
            pattern += if ((byte shr bit) and 1 == 1) 1690 else 560
        }
    }
    pattern += 560                                  // stop bit
    pattern += 108_000 - pattern.sum()              // silence to 108 ms (about 40 ms)
    return pattern.toIntArray()
}

/** "Still held", padded to 108 ms. */
val NEC_REPEAT = intArrayOf(9000, 2250, 560, 108_000 - 11_810)

// transmit() blocks until the pattern has been sent, so call it off the main thread:
//   irManager.transmit(CARRIER_HZ, necFrame(0x05))                    // press OK
//   while (pressed) irManager.transmit(CARRIER_HZ, NEC_REPEAT)        // while held
```

## Default buttons

| Command | Button | Action in `remote.ini` |
|---|---|---|
| `0x01`–`0x04` | Up, Down, Left, Right | `up` `down` `left` `right` |
| `0x05` | OK | `ok` (Enter) |
| `0x06` | Back (hold for Home) | `back` |
| `0x07` | Home | `home` |
| `0x08` | Menu | `menu` |
| `0x09` | Exit | `escape` |
| `0x0A` | Play / Pause | `playpause` (Space, which pauses all the streaming sites) |
| `0x0B` | Delete | `backspace` |
| `0x10` `0x11` `0x12` | Volume up, down, mute | `volup` `voldown` `mute` |
| `0x20` | Power: sleep, and wake the PC | `power` |
| `0x21` | Sleep timer | `sleeptimer` |
| `0x30`–`0x33` | Pointer up, down, left, right (hold) | `mouse:up` ... `mouse:right` |
| `0x34` `0x35` | Click, right-click | `mouse:click` `mouse:rightclick` |
| `0x36` `0x37` | Scroll up, down | `mouse:scrollup` `mouse:scrolldown` |
| `0x40`–`0x44` | Netflix, YouTube, Prime Video, JioHotstar, Web | `open:Netflix` ... |
| `0x50` | Full screen | `key:F` |
| `0x51` | Voice search started: the TV opens search, shows *Listening…* and turns the sound down | `voicesearch` (built in) |
| `0x52` | Text follows (see *Text* below) | handled by CouchTV |
| `0x53` | Voice search cancelled: the TV closes search if nothing arrived | `voicecancel` (built in) |

`0x51` and `0x53` work even with a `remote.ini` from before voice search existed, and a `remote.ini` line can still remap them. `0x60`–`0xFF` are free for your own buttons. Give a new button an unused command, then either:

- add a line to `remote.ini`, e.g. `NEC 00CE 0060 = key:M` (press M, which mutes YouTube and Netflix), then press F5 on the home screen; or
- run **Remote setup** on the TV and press the new button when it's asked for.

Any CouchTV action works. The full list is at the top of `remote.ini`.

## Text (voice search)

The phone turns speech into text itself (Google's speech recognition), then sends the text. Audio never travels over infrared, which is far too slow for it. Text uses ordinary 32-bit NEC frames, so **the receiver's firmware needs no change**: it reports each frame like a button, and CouchTV puts the text back together (`src/IrText.cs`).

1. Send the button `0x52` (text follows).
2. Take the text as **UTF-8**, at most 120 bytes. Hindi and emoji are fine; trim at a whole character.
3. For each **two bytes** `a, b` (use `b = FF` when the last pair has only `a`), send one frame with the bytes
   `CE, pack(b), a, ~a`. The receiver reports it as `NEC2 xxCE 00aa`, where `xx` is `pack(b)`.
4. Send two **check frames** the same way, with `b = FE` and `a` = the CRC's high byte, then its low byte. The CRC is
   **CRC-16/CCITT-FALSE** (polynomial `1021`, start `FFFF`) of the UTF-8 bytes. `"123456789"` gives `29B1`.

`pack(b)` is `b XOR C0`, except that `F1` becomes `01`. It makes sure byte 1 is never `00` or `31`. With `31` (the inverse of `CE`) the receiver would report `NEC 00CE ...`, a phone button. `C0` and `C1` never appear in UTF-8, and neither do the markers `FE` and `FF`.

Send the frames **108 ms apart** (start to start), like everything else. A 20-letter search takes about 1.3 s. CouchTV rejects a message whose CRC doesn't match, or that stops arriving for 0.7 s, and asks you to try again. It only treats `xxCE` frames as text after a `0x52`, so another remote that happens to use such an address still works as a remote.

```kotlin
fun textFrames(text: String): List<IntArray> {             // send necFrame(0x52) first
    val data = text.toByteArray(Charsets.UTF_8)
    fun pack(b: Int) = if (b == 0xF1) 0x01 else b xor 0xC0
    fun frame(a: Int, b: Int) = rawFrame(0xCE, pack(b), a, a.inv() and 0xFF)   // like necFrame, any 4 bytes
    val frames = (data.indices step 2).map { i ->
        frame(data[i].toInt() and 0xFF, if (i + 1 < data.size) data[i + 1].toInt() and 0xFF else 0xFF)
    }.toMutableList()
    val crc = crc16(data)                                    // CRC-16/CCITT-FALSE
    frames += frame(crc shr 8, 0xFE)
    frames += frame(crc and 0xFF, 0xFE)
    return frames
}
```

The app's version is `phone-remote/app/src/main/java/com/couchtv/remote/Nec.kt`. CouchTV's self-test (`CouchTV.exe --remotetest report.txt`) mirrors how the receiver reports each frame and checks Hindi, emoji, the digit 1 (byte `31`), garbled frames and missing frames.

## Wi-Fi

The same remote also works over the home network, with no pointing, and on phones without an IR blaster. It uses the same button codes, so `remote.ini` applies to both. The phone prefers Wi-Fi while the TV answers, and falls back to infrared when it doesn't, for example when the PC is asleep.

1. **Finding the TV.** The phone sends the text `COUCHTV?` by UDP to port **47700**, both as a broadcast (the subnet's, like `192.168.1.255`, and `255.255.255.255`) and directly to the address that answered last time. CouchTV replies `COUCHTV 1 47701 <PC name>`: protocol 1, TCP port 47701.
2. **Connecting.** The phone opens TCP port **47701**. Both sides send lines of UTF-8 text ending in `\n`:

| Phone sends | CouchTV answers | Meaning |
|---|---|---|
| `HELLO CouchTV-Remote <version>` | `WELCOME <PC name>` | connected |
| `PING` (every second) | `PONG` | keeps it alive; either side drops the link after 3–5 s without one |
| `BTN CE 05 N`, then `BTN CE 05 R` every 108 ms while held | | a button: address and command in hex, like an NEC frame; CouchTV treats it exactly like the receiver's `NEC 00CE 0005` |
| `TEXT <base64 of UTF-8>` | | words for search, all at once (no frames or CRC needed) |

Windows Firewall has to allow CouchTV in: the installer adds the rule *CouchTV phone remote* (CouchTV.exe, local network only). On a PC installed before Wi-Fi mode, CouchTV asks once and Windows shows its permission prompt; press F7 on the home screen to ask again. The code is `src/RemoteServer.cs` and `phone-remote/.../WifiLink.kt`.

## Sound on phones

*Listen on this phone* in the app plays the TV's sound in that phone's headphones, so several people can listen at once, each with their own. CouchTV captures whatever Windows plays (WASAPI loopback, so every app works) and sends it over Wi-Fi.

- The phone sends `LISTEN` to UDP port **47702** every second while it wants sound, and `STOP` when done. CouchTV drops a phone that's been quiet for 3 seconds, and only captures while someone listens. When nothing plays (a paused video), Windows produces no sound data, so CouchTV sends silence at the real pace after 20 ms; phones never run dry.
- Each packet is a chunk of 180 frames (3.75 ms at 48 kHz) of uncompressed 16-bit stereo: `CTA1`, a sequence number (uint32), the sample rate (uint32), the channel count (1 byte, 2), flags (1 byte), 2 zero bytes, then the samples, little-endian. A phone that sends `LISTEN 2` gets `CTA2` packets instead, which also carry the previous chunk after the current one (flags bit 1): a packet lost on the Wi-Fi is repaired from the next, and two chunks plus the header (1456 bytes) still fit one Wi-Fi frame. 5.1 and 7.1 are folded into stereo, keeping the centre (dialogue).
- The phone lets its own audio system set the pace, and keeps a cushion ready: 60 ms for the speaker and wired earphones, 160 ms for Bluetooth (which takes sound in big, irregular gulps and shares the phone's radio with 2.4 GHz Wi-Fi), filled with silence before playback starts and again after any gap (sound arrives exactly as fast as it plays, so nothing else would fill it), growing by 20 ms whenever sound runs out, up to 300 ms, and shrinking by 20 ms after each minute without a gap. The TV's and phone's clocks differ slightly, so when packets slowly pile up or the cushion runs low it plays a packet 0.4% faster or slower (inaudible) instead of dropping or padding 5 ms, which would be an audible break. If two or more packets in a row are lost, the phone repeats the last chunk instead of skipping (up to 3). The notification shows the cushion and counts of lost and repaired packets, gaps and smooth corrections. It holds the Wi-Fi in low-latency mode. Bluetooth headphones add their own 100–250 ms on top; wired earphones don't.

`CouchTV.exe --audiotest report.txt` plays a quiet tone and checks it arrives intact over this PC's loopback. The code is `src/AudioShare.cs` and `phone-remote/.../AudioListener.kt`.
