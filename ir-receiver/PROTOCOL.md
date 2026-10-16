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

The same remote also works over the home network, with no pointing, and on phones without an IR blaster. It uses the same button codes, so `remote.ini` applies to both. The phone prefers Wi-Fi while the TV answers, and falls back to infrared when it doesn't, for example when the PC is asleep. **Power never goes to the PC directly**, even while connected: it goes to the receiver box (by its own Wi-Fi, below, or else by infrared), which handles it whether the PC is on (it passes it to CouchTV) or asleep or off (it wakes it). Sent to the PC over Wi-Fi it raced the link, which drops as the PC sleeps and comes back as it wakes.

1. **Finding the TV.** The phone sends the text `COUCHTV?` by UDP to port **47700**, both as a broadcast (the subnet's, like `192.168.1.255`, and `255.255.255.255`) and directly to the address that answered last time. CouchTV replies `COUCHTV 2 47701 <PC name>`: protocol 2, TCP port 47701.
2. **Connecting.** The phone opens TCP port **47701**. Both sides send lines of UTF-8 text ending in `\n`. Only a phone paired with the TV gets past the first lines, and everything after them is encrypted: see *Security* below. Inside the encryption:

| Phone sends | CouchTV answers | Meaning |
|---|---|---|
| `HELLO CouchTV-Remote <version>` | `WELCOME <PC name>`, `POINTER <look>`, `BOARD <key>`, `AUDIO <session>` | connected; `BOARD` is the receiver box's key (below), `AUDIO` the id for this connection's sound requests |
| `PING` (every second) | `PONG` | keeps it alive; either side drops the link after 3–5 s without one |
| `SOUND` | `AUDIO <session>` | the phone's sound service, which keeps a connection of its own (it plays on with the screen off) |
| `BTN CE 05 N`, then `BTN CE 05 R` every 108 ms while held | | a button: address and command in hex, like an NEC frame; CouchTV treats it exactly like the receiver's `NEC 00CE 0005` |
| `TEXT <base64 of UTF-8>` | | words for search, all at once (no frames or CRC needed) |
| `MOVE <dx> <dy>` | | the trackpad: move the pointer by that many pixels (up to ±2000); the phone does the acceleration, so CouchTV skips Windows' |
| `WHEEL <dy> [<dx>]` | | the trackpad: scroll, 120 a mouse-wheel notch (smaller amounts scroll smoothly in browsers), up and right positive |
| `MOUSE L\|R D\|U` | | the trackpad: left or right mouse button down or up; if the phone drops while holding one, CouchTV lets go of it |
| `POINTER <style> <size> <colour>` | | the pointer's look: `arrow`, `dot` or `ring`; 32–128 pixels; `white`, `yellow`, `blue`, `green` or `pink`. CouchTV shows it at once and keeps it in couchtv.ini |
| | `POINTER arrow 64 white` (after `WELCOME`) | the pointer's look now, so the phone can show it |
| `TRACKPAD ON\|OFF` | | the phone's trackpad page is in front (or not any more; also when the app goes to the background or mirrors the screen). CouchTV shows its pointer while any phone has it open, and otherwise hides it everywhere (a blank pointer in place of all of Windows'), apart from a few seconds after anything else moves it: `MOVE`, `WHEEL` and `MOUSE` from a phone too old to say `TRACKPAD`, the trackpad by infrared, an air mouse or a real mouse (seen by raw input; CouchTV's own moves come from no device). Sent again after each `HELLO` |
| `TOUCH <finger> D\|M\|U <x> <y>` | | the screen mirror: a finger (0–9) down, moved or up, at x and y from 0 to 65535 across the TV's screen; played as a touchscreen's touch (below) |
| `KEY <vk> D\|U` | | the keyboard page: a PC key down or up, as its Windows virtual-key code in hex (`A2` left Ctrl, `43` C, `70` F1…). Sent down again every 45 ms while held, since Windows only repeats real keyboards' keys. CouchTV lets go of keys a phone still held when it drops |
| `EDIT <backspaces> [<base64 of UTF-8>]` | | typing with the phone's own keyboard: that many Backspaces, then the text, typed as characters (any language, emoji), whatever the PC's keyboard layout; a line break is Enter |
| | `INPUT TEXT\|PASSWORD <name> <value>`, or `INPUT OFF` | a text box got the keyboard focus on the TV (its name and what's in it, base64; a password box's value is never sent), or none has it now. Told to the phone that sent something last, which pops up its keyboard. CouchTV watches the focus with UI Automation (Windows' accessibility interface) while a phone is connected; `TypeOnPhone = off` in couchtv.ini stops it |
| | `SUGGESTSETUP <films Y\|N> <songs Y\|N> <apps>` | what the TV has, so the phone can fetch the suggestions itself while the TV is off: which rows are on, and each app as `key:name:label:colour:text colour:Y\|N` (JustWatch's name for it, `nfx` `prv` `jhs` `zee` `snl`, or `youtube`; name and label in base64; `Y` for one you subscribe to, or every app with no `Subscriptions`), comma separated (`-` for none) |
| | `SUGGESTIONS <count>`, then that many `SUGGEST M\|S <id> <title> <details> <app> <picture> <app colour> <text colour> <app key> <link> <query>` | the suggestions under the home screen's apps (*Suggestions* below), for the phone's first page: a film (`M`) or song (`S`), its title, details (year and length, or the singers) and app name, its picture's address on the internet, the app's colours as `RRGGBB`, then the app's key, the address that plays it, and for a song what to look for on YouTube (texts in base64, `-` for none). Sent after `WELCOME`, and again whenever the list changes |
| `POWERSOON <seconds>` | | Power was tapped on the phone with the TV on: it's sent that many seconds later unless cancelled (`POWERSOON 0`), so a tap by mistake does nothing. The TV says so on screen meanwhile |
| `LOG [<lines>]` | `LOG <count>`, then that many lines `\| <line>` | the end of the TV's log (`couchtv.log`; 300 lines unless asked, at most 2000), for looking into the TV from a laptop paired like a phone (`CouchTV.exe --tv`, below). Paired connections only, like everything here; the TV notes in its log who asked |
| `SLEEPTIMER <minutes>` | | the sleep timer: the TV goes to sleep that many minutes from now (up to a day), or `0` turns it off. The phone's Sleep timer key picks any length |
| | `SLEEPTIMER <seconds left>` | the sleep timer as it is now, `0` when it's off: after `WELCOME`, and again whenever it changes (set from a phone, the remote's own key or voice search, or run out). The phone's key shows how long is left. A phone that never hears it (an older CouchTV) sends the remote's Sleep timer key instead, whose steps are 30, 60, 90, 120 minutes, then off |
| `PLAY <id> M\|S <app key> <link> <title> <query>` | | play a suggestion on the TV, as OK on it on the home screen does: the TV's own by its id, or else this one (one the phone fetched while the TV was off). CouchTV plays it only on one of its apps and only from that app's site over https (YouTube: a `/watch` video); a song without a link is looked for on YouTube |

Windows Firewall has to allow CouchTV in: the installer adds the rule *CouchTV phone remote* (CouchTV.exe, local network only). On a PC installed before Wi-Fi mode, CouchTV asks once and Windows shows its permission prompt; press F7 on the home screen to ask again. The code is `src/RemoteServer.cs` and `phone-remote/.../WifiLink.kt`.

An app from before pairing that says `HELLO` straight away is answered `UPDATE` and nothing else, and keeps using infrared.

## Suggestions

Under the apps, the home screen shows what's popular, and the phone remote's first page (left of the remote) shows the same; OK, or a tap on the phone, plays it on the TV. CouchTV fetches the lists every 6 hours (`src/Suggestions.cs`), keeps them in `%LOCALAPPDATA%\CouchTV\suggestions\` with their pictures, and shows the saved ones at once after a restart or offline.

- **Films:** JustWatch's popularity in India (the list its website shows, from its own GraphQL API at `apis.justwatch.com`), films from this year and last, available on the TV's apps (Netflix, Prime Video, JioHotstar, ZEE5, SonyLIV). Each comes with the best of your apps for it: one in couchtv.ini's `Subscriptions` (any, with none listed), else one that has it free; films only on apps you don't pay for are left out. JustWatch gives each film's own address in the app, so it plays straight away where the app allows it: Netflix's `/watch/<id>` page starts the film, JioHotstar's address gets `/watch`, Prime Video's (after its redirect) `?autoplay=1`; ZEE5 and SonyLIV open the film's page.
- **Songs:** India's most-played songs on Apple Music (Apple's free chart feed, `rss.applemarketingtools.com`), at most two per singer, played on YouTube: CouchTV finds each song's video beforehand (as voice search does) and plays it as a mix of similar songs, full screen.

`Suggestions = movies, music` in couchtv.ini chooses the rows (`off` for none). `CouchTV.exe --suggesttest report.txt` fetches the lists and reports them.

With the TV off, the phone fetches the same two lists itself (`SuggestionFeed.kt`), from the same places, for the apps and subscriptions the TV last sent in `SUGGESTSETUP` (CouchTV's own apps before any TV has), so there's something to choose before turning the TV on. It keeps the last list, the TV's or its own, and fetches again when it's 3 hours old. Its songs come without their YouTube videos: the TV finds one when it's played. A tap with the TV off sends Power (as the Power key does, unless the receiver box says the PC is on) and, once the TV has connected and sent its list, `PLAY` with everything the TV needs.

## Security

A Wi-Fi shared with neighbours (or a router without a guest network) puts everyone's devices on the same network, so without this anyone on it could press the TV's buttons, type into its search, hear its sound, or switch the PC and TV on and off through the receiver box. So only **paired** phones get any of that, and what they send is encrypted.

**Pairing, once per phone.** When the app finds a TV it isn't paired with, its top line says so; tapping it asks the TV for a code. The TV shows a fresh 6-digit code on screen (for 2 minutes, or until the phone gives up), and you type it on the phone. Seeing the code means being in the room. Neither side ever sends the code: for each of its 20 bits, the phone commits to its bit (an HMAC keyed with a random nonce, over the bit and both sides' ECDH keys), the TV commits to its own, then the phone reveals its nonce, then the TV. A wrong code is caught by the TV in the first bit where they differ (`WRONG`). Someone sitting between the two (on a shared Wi-Fi that's possible) can't take the code from one side to fool the other: in every round they'd have to guess a bit before seeing it, and each code works once (as in Bluetooth's passkey pairing). The TV pairs one phone at a time (`BUSY`), and after 5 wrong codes, or 15 pairings, in 10 minutes it waits (`WAIT <seconds>`). The TV keeps up to 20 phones. *Forget phones* on the TV's Remote screen (F8) forgets them all, drops their connections and changes the receiver box's key.

**Every connection.** Each side makes a fresh ECDH key on the P-256 curve; the shared secret is SHA-256 of the x coordinate. Then:

| Phone sends | CouchTV answers |
|---|---|
| `HELLO2 CouchTV-Remote <version> <phone id> <phone key>` | `HELLO2 <TV id> <TV key> KNOWN\|PAIR <PC name>` |
| (if `PAIR`, only when you asked to pair) `PAIR <phone name, base64>` | `CODE` (it's on the TV now), `BUSY` or `WAIT <seconds>` |
| `C <i> <commitment>` | `C <i> <commitment>` |
| `R <i> <nonce>` | `R <i> <nonce>`, or `WRONG` |
| `AUTH <phone proof>` | `AUTH <TV proof>` |

Keys are 65-byte points (`04`, x, y) and binary values are base64; the phone id is 32 hex digits it made up once. The *transcript* is SHA-256 of the two `HELLO2` lines (joined by `\n`). Pairing ends with the **pairing key** = HKDF-SHA256(secret, salt: the 6 digits, info: `CouchTV pair 1` + transcript), 32 bytes, which both keep. Each connection then derives 224 bytes = HKDF-SHA256(secret, salt: pairing key, info: `CouchTV session 1` + transcript): a confirmation key, then encryption and MAC keys for what the phone sends, for what the TV sends, and for the sound (32 bytes each). The proofs are HMAC(confirmation key, `phone`) and HMAC(confirmation key, `tv`); the TV checks the phone's before it answers with its own, and the phone checks the TV's before it sends anything that matters. A phone without the pairing key gets nothing more, nor does a stranger posing as the TV.

**Encrypted lines.** From then on each line is base64 of: the text encrypted with AES-256 in counter mode (the 16-byte counter block is the line's number, 8 bytes big-endian, then the block number, 8 bytes big-endian from 0), then 16 bytes of HMAC-SHA256(MAC key, line number as 8 bytes big-endian + ciphertext). Line numbers count from 0 each way and aren't sent, so a line replayed, dropped, reordered or changed fails, and CouchTV hangs up. CouchTV refuses lines over 8 KB, and more than 16 connections at once.

**Sound.** See *Sound on phones*: requests carry an HMAC with the connection's sound key, and each phone's packets are encrypted with its own.

**The receiver box.** CouchTV makes the box's key (32 random bytes) and sends it to the receiver over USB on every connect (`LINKKEY <hex>`), which passes it to the ESP32 (`KEY <hex>`), which keeps it; paired phones get it in `BOARD`. See *Receiver box Wi-Fi*. The box's setup network only opens when its BOOT button is held (or before it has a Wi-Fi), so nobody on the Wi-Fi can move it to another network.

The pairing keys live in `%LOCALAPPDATA%\CouchTV\phones.dat`, encrypted by Windows for that user (DPAPI), and on the phone in its app storage, left out of backups. The code: `src/Secure.cs`, `src/Phones.cs` and `src/RemoteServer.cs`; `phone-remote/.../Secure.kt` and `Handshake.kt`; `firmware/CouchLink`. `CouchTV.exe --remotetest` checks the cryptography against published test vectors and runs pairing, wrong codes, forged proofs, replayed and changed lines; `CouchTV.exe --securityvectors` writes the numbers the phone app's unit test (`SecureTest.kt`, run by every build) checks its own against.

Also worth doing on a shared Wi-Fi: set the TV PC's network to **Public** (Settings > Network & internet > Wi-Fi or Ethernet > the network > Public network), which turns off file and printer sharing and network discovery; the phone remote keeps working, since its firewall rule covers both. Your own router behind the shared one (a network only your devices join) keeps neighbours off it altogether.

## Screen mirror

*Show the TV's screen here* (the button at the top of the app) shows the TV's screen on the phone, full screen in landscape, for choosing what to watch from across the room. Fingers on it work the TV as a touchscreen would: tap to click, drag to scroll, pinch to zoom, hold to right-click. A strip at the side has Back, Home, volume, the phone's keyboard and the way back to the remote.

- The phone opens a paired connection of its own (*Security*) and, after `AUTH`, sends `MIRROR <width> <kbps>`: the widest picture it wants (1280) and the most it may send (4000 kbps; 2500 while that phone is also playing the TV's sound, so the sound keeps its share of the Wi-Fi). From then on that connection carries the TV's pictures, one way.
- CouchTV captures the main screen scaled to that width (GDI, HALFTONE scaling), splits it into 64-pixel tiles and sends only tiles that changed since its last look, as JPEG strips along each row (quality 72): the first picture whole (about 90 KB), then a moving highlight costs a few KB. While a fifth of the screen or more changes at once (a page scrolling under a finger) the strips go at quality 38, about half the size, so the picture keeps up; three looks after it settles, those tiles are sent again at 72. It looks 15 times a second while things change or the phone sends anything, 5 times a second once all's been still for 2 s, and waits longer when needed to stay under the cap.
- Each message is binary: its length (4 bytes, big-endian), then AES-256-CTR ciphertext, then 16 bytes of HMAC-SHA256 over the message number (8 bytes, big-endian, from 0) and the ciphertext, with keys of their own: HMAC(TV encryption key, `mirror enc`) and HMAC(TV MAC key, `mirror mac`). Inside, the first byte says what it is; numbers are 16-bit big-endian:

| Type | Then | Meaning |
|---|---|---|
| 1 | width, height | a new picture of that size (at the start, and if the screen's resolution changes) |
| 2 | x, y, width, height, JPEG | a strip of what changed |
| 3 | | that look's strips are all in: show them |
| 4 | x, y, shown (1 byte) | where the mouse pointer is |
| 5 | | nothing changed (a heartbeat, at least every second) |

- Touches come on the phone's normal connection (`TOUCH`, above). CouchTV plays them as real touches (Windows' touch injection, up to 10 fingers, repeated every 100 ms while held, as Windows lets go of a touch that goes quiet; the TV shows a dot where you touch), so browsers scroll and zoom as on a phone. CouchTV's own screens (WPF) take them too, as it uses WPF's pointer-message touch: WPF's older tablet stack never sees injected touches, which is why the home screen's tiles first ignored taps. Where touch can't be injected, one finger works as the mouse. With `MirrorTouch = mouse` in couchtv.ini, they're played as the mouse instead, for an app that ignores touch: the first finger puts the pointer where it lands; lifted within 0.6 s without moving more than 14 pixels it clicks there, held longer it right-clicks; moved, it scrolls the page along with the finger (1.2 wheel units a pixel, both ways); a second finger pinches to zoom (Ctrl and the wheel, a notch each 40 pixels). A phone that goes away lifts its fingers.
- The TV says on screen when a phone starts and stops showing it.
- Copy-protected video (Netflix, Prime Video, JioHotstar while playing) is black in any screen capture; their menus, rows and search aren't, and nor is anything else.

**Testing the TV from a laptop.** `CouchTV.exe --tv report.txt <commands>` makes a PC a remote for the TV, paired once like a phone (`pair <address>`: the TV shows a code, which goes in `tvlink-code.txt` next to the report), then encrypted as a phone's connection is. The commands run in order: `log [lines]` (the end of the TV's log), `screen file.png` (the TV's screen, as the mirror gets it, and where the pointer is and whether Windows draws it), `tap x y` and `swipe x1 y1 x2 y2 [ms]` (fingers, as the mirror sends them; 0 to 65535 across and down), `btn CE 05` (a remote button), `key 75` (a PC key: F6, say, then the arrows and Enter, to update the TV), `send "<line>"` (any line, as the phone sends it), `wait ms`. The pairing is kept in `%LOCALAPPDATA%\CouchTV\tvlink.dat`, for that Windows user only (DPAPI). The code is `src/TvLink.cs`.

`CouchTV.exe --dragtest report.txt` puts real touches, as the mirror injects them, on the home screen in its small window: a tap on a film opens it (nothing really opens), a swipe along the row slides it and settles on a card, a swipe up slides the page, and neither opens anything.

`CouchTV.exe --mirrortest report.txt` mirrors the PC's screen to a pretend phone over the loopback and reports the first picture's size and time, the bandwidth after it, the CPU and how close the phone's picture is to the screen. The code is `src/ScreenMirror.cs` and `phone-remote/.../ScreenLink.kt` and `MirrorView.kt`.

## Receiver box Wi-Fi

With an ESP32 wired to the receiver (firmware `firmware/CouchLink`), the box has Wi-Fi of its own. It's on while the PC is asleep or off, so any phone paired with the TV can switch the PC and the TV on, and see whether they're on. Its first start opens the network *CouchTV-Setup*, whose page takes your Wi-Fi's name and password (kept on the ESP32 only); after that it's `couchtv-board` on your Wi-Fi. To change networks, hold its BOOT button for 3 seconds: its blue light blinks and CouchTV-Setup opens for 10 minutes. Its page on the home Wi-Fi only shows its version and whether it has its key.

It only obeys requests tagged with its key (*Security*): `tag(text)` is the first 16 bytes of HMAC-SHA256(key, text) as 32 hex digits. The box has a **challenge**, 32 random hex digits, that it changes every minute and after each Power, so a request can't be replayed; every tagged answer brings the current one, and the previous one still works for a moment. Phones send it one line per UDP packet on port **47710**, and it answers the sender:

| Phone sends | Box answers | Meaning |
|---|---|---|
| `COUCHBOARD?` (broadcast) | `COUCHBOARD 2 couchtv-board` | finding it |
| `NONCE` | `NONCE <challenge>` | a challenge to start with |
| `STATE <c> <tag("STATE <c>")>` (every 2.5 s) | `STATE PC ON TV OFF SENSE OFF ... <next> <tag>`, or `STATE ? <next> <tag>` | the PC and TV as the receiver sees them; `?` if the receiver isn't answering |
| `POWER <id> <c> <tag("POWER <id> <c>")>` | `OK <id> <next> <tag>` | the Power button; resent until `OK`, and the same id again within 10 s is answered without pressing |
| anything it can't check | `NONCE <challenge>` (or `NOKEY` before CouchTV gave it a key) | the phone asks again with that challenge |

An answer's tag is `tag("R <c> <answer> <next>")`, with the challenge the request used, so the phone knows it's the real box, now.

The ESP32 passes Power to the receiver as `POWER BUTTON` over a serial link (Leonardo pins 0 and 1, 115200 baud), and the receiver treats it exactly like Power by infrared. It asks the receiver `STATE` every 2 s.

## Sound on phones

*Listen on this phone* in the app plays the TV's sound in that phone's headphones, so several people can listen at once, each with their own. CouchTV captures whatever Windows plays (WASAPI loopback, so every app works) and sends it over Wi-Fi.

- Only paired phones get it. The phone's sound service keeps a paired connection of its own to the TV (*Security*), which gives it a session id and sound keys. It sends `LISTEN 3 <session> <n> <tag>` to UDP port **47702** every second while it wants sound, and `STOP 3 <session> <n> <tag>` when done: `n` goes up each time (CouchTV ignores one that doesn't, so requests can't be replayed), and the tag is the first 16 bytes of HMAC-SHA256(sound MAC key, `LISTEN <session> <n>`) in hex. It must come from the connection's address, and stops counting when the connection ends. CouchTV drops a phone that's been quiet for 3 seconds, and only captures while someone listens. When nothing plays (a paused video), Windows produces no sound data, so CouchTV sends silence at the real pace after 20 ms; phones never run dry.
- Each packet is a chunk of 180 frames (3.75 ms at 48 kHz) of uncompressed 16-bit stereo: `CTA3`, a sequence number (uint32), the sample rate (uint32), the channel count (1 byte, 2), flags (1 byte), 2 zero bytes, then the samples, little-endian, then the previous chunk (flags bit 1), so a packet lost on the Wi-Fi is repaired from the next. The samples are encrypted for that phone alone with AES-256-CTR (sound encryption key; the counter block is the sequence number as 8 bytes big-endian, then the block number), then 16 bytes of HMAC-SHA256(sound MAC key, header + ciphertext) follow: 1472 bytes, exactly one Wi-Fi frame. The sequence never starts again while CouchTV runs, so no counter block repeats under a key. 5.1 and 7.1 are folded into stereo, keeping the centre (dialogue). Sound outputs without a volume control of their own (HDMI usually) have Windows' volume applied before the loopback hears it, so CouchTV undoes it there (up to 40 dB): phones get full-quality sound at any TV volume and set their own. Peaks that would clip are turned down briefly instead, and the conversion to 16 bits is rounded and dithered.
- The phone lets its own audio system set the pace and keeps a cushion of 60 ms (speaker, wired) or 160 ms (Bluetooth, which takes sound in big, irregular gulps and shares the phone's radio with 2.4 GHz Wi-Fi), growing by 20 ms whenever sound runs out, up to 300 ms. The TV's and phone's clocks differ slightly, so when packets slowly pile up or the cushion runs low the sound plays up to 0.6% faster or slower, easing in and out, instead of dropping or padding sound; it's resampled with a 16-tap windowed-sinc filter (flat to 18 kHz) whose position carries across chunks, and at normal speed it's bit-exact. If two or more packets in a row are lost, the phone fills in up to 3 chunks by repeating the stretch of recent sound (4–15 ms) that best continues the waveform, then blends back into the real sound over 2 ms (like G.711 Appendix I), instead of repeating the last chunk, which clicked. It holds the Wi-Fi in low-latency mode. The notification shows the cushion and counts of lost and repaired packets, gaps and smooth corrections. The phone plays it on wired earphones (or USB, or line out), else its speaker, never on Bluetooth (`setPreferredDevice`), and keeps the phone's Bluetooth off while the app is in use, so the Wi-Fi has the 2.4 GHz radio to itself (Android 12 and older: the app switches it off; 13 and newer: Android's *Turn off Bluetooth?* question, unless the app is device owner). The Bluetooth cushion stays for phones that ignore the routing; on Bluetooth the low delay came with an occasional tiny break, and a bigger reserve removed the breaks but added noticeable delay.

`CouchTV.exe --audiotest report.txt` plays a quiet tone and checks it arrives intact over this PC's loopback, and that requests without the key, or replayed, get nothing. The code is `src/AudioShare.cs` and `phone-remote/.../AudioListener.kt` (with `Resampler.kt`, `Concealer.kt` and `BluetoothGuard.kt`).
