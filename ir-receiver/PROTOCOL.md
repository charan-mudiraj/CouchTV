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

`0x60`–`0xFF` are free for your own buttons. Give a new button an unused command, then either:

- add a line to `remote.ini`, e.g. `NEC 00CE 0060 = key:M` (press M, which mutes YouTube and Netflix), then press F5 on the home screen; or
- run **Remote setup** on the TV and press the new button when it's asked for.

Any CouchTV action works. The full list is at the top of `remote.ini`.

## Typing text (later)

To search with the phone's keyboard, map characters onto spare commands (for example `0x80` + letter index) and add matching `key:A` ... `key:Z` lines. Or use a second address (e.g. `0xCF`) just for letters, so they don't use up the main remote's command space.
