# CouchTV Remote (Android)

A remote control app for phones with an **IR blaster** (most Xiaomi / Redmi / POCO phones). It sends infrared signals that CouchTV's [CouchIR receiver](../ir-receiver/README.md) picks up. No Wi-Fi or pairing needed: point the phone at the TV and press.

**What's on it**

- **D-pad and OK.** Hold a direction to keep moving.
- **Back, Home, Menu.** Holding Back also goes Home.
- **Volume down / Mute / Volume up, and Play-Pause.**
- **Exit, Full screen, Sleep timer, Delete.**
- **Power**, which puts the TV to sleep and wakes it.
- **App shortcuts:** Netflix, YouTube, Prime Video, JioHotstar, Web.
- **A pointer pad** for sites that need a mouse: hold a direction to move the pointer, the middle clicks, plus scroll and right-click.
- **My buttons:** add buttons with your own codes, then teach the TV what they do.

All the codes match CouchTV's default `remote.ini`, so the app works with the TV as soon as the receiver is plugged in, with no setup.

## Get the APK

**Easiest, with no Android Studio:** GitHub builds it automatically whenever `phone-remote/` changes on `main` (see `.github/workflows/phone-remote.yml`). On the phone, open

**https://github.com/charan-mudiraj/CouchTV/releases/download/phone-remote/CouchTV-Remote.apk**

and install it. The first time, Android asks you to allow installs from your browser. To rebuild without changing anything, open the repo's **Actions** tab → *Phone remote APK* → **Run workflow**.

## Build it yourself (Android Studio)

1. In Android Studio, choose **File → Open** and pick this `phone-remote` folder. Let it sync. If it offers to upgrade the Android Gradle Plugin, or to download SDK 35, accept.
2. On the phone, turn on **Developer options → USB debugging**. (Settings → About phone → tap *MIUI/OS version* 7 times to unlock Developer options. On Xiaomi phones, also turn on **Install via USB**.)
3. Connect the phone by USB, choose it in the device list and press **Run ▶**.

To install without a cable: **Build → Build App Bundle(s) / APK(s) → Build APK(s)**, copy `app/build/outputs/apk/debug/app-debug.apk` to the phone and open it.

The project has no third-party libraries, only the Android framework, so there's very little that can go wrong in the build.

## Your own buttons

1. Tap **+ Add a button**, give it a name, and keep the suggested command. Commands `60`–`FF` are free.
2. Teach the TV what it does, in either of two ways:
   - On the TV, open **Settings & power → Remote** and press the new button on the phone when you reach the action you want.
   - Or add a line to `C:\CouchTV\remote.ini`. For example `NEC 00CE 0060 = key:M` makes it press M (mute in Netflix and YouTube), and `NEC 00CE 0061 = open:Kodi` opens a tile. All actions are listed at the top of `remote.ini`. Press F5 on the TV afterwards.
3. Tap **Edit** to rename, re-code or delete buttons.

## How it sends

Each button sends an NEC frame (38 kHz, address `0xCE`), then a "still held" frame every 108 ms while your finger stays down, exactly like a real remote. The details are in [`../ir-receiver/PROTOCOL.md`](../ir-receiver/PROTOCOL.md), and the code is in `app/src/main/java/com/couchtv/remote/`:

| File | What it does |
|---|---|
| `Nec.kt` | Builds the infrared timing patterns |
| `IrRemote.kt` | Sends them through the IR blaster on a background thread, with hold-to-repeat |
| `Codes.kt` | The button codes |
| `DpadView.kt` | The round D-pad |
| `MainActivity.kt` | The screen, and your own buttons |
| `CustomButtons.kt` | Saves your own buttons |

## Feedback to send

This is written but not yet built, so the first build may need a small fix or two. Please send:

1. **Build errors**, if any: copy the first red error from Android Studio's *Build* panel.
2. **Whether the TV reacts.** On the TV, open Settings & power → Remote (or press F2). Every press on the phone should show a green *Got it (NEC 00CE ...)* line. If nothing shows, check that the receiver says *Receiver ready* and that the receiver's RX light blinks when you press.
3. **Range and angle:** how far away it still works.
4. **Anything awkward** about the layout or button sizes on your phone.
