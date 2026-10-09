# CouchTV Remote (Android)

A remote control app for CouchTV. It works two ways, and picks by itself:

- **Over Wi-Fi**, when the phone is on the same Wi-Fi as the TV: it finds the TV and connects as soon as you open it, with no list to choose from. No pointing needed, and it works on **any** Android phone. The top line says *Connected to … over Wi-Fi*.
- **By infrared**, on phones with an **IR blaster** (most Xiaomi / Redmi / POCO phones), when the TV isn't on Wi-Fi, for example while the PC is asleep. Point the phone at the TV; CouchTV's [CouchIR receiver](../ir-receiver/README.md) picks it up. This is also what wakes the PC.

**What's on it**

- **Voice search.** Tap the blue mic and say what to watch. The phone turns your words into text and sends them to the TV, which lets you pick the app to search in. Tap the search bar to type instead.
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

## Updates

After the first install, the app keeps itself up to date:

- **It checks GitHub when you open it** (at most every 15 minutes). To check right away, tap the version line at the bottom of the screen.
- **When a newer build exists,** a green banner shows *Update available: "commit message"*. Tap **Update**: it downloads the new APK, checks it's exactly the file GitHub built (SHA-256), and installs it.
- **The first update** asks you to allow installs from CouchTV Remote (a switch in Settings; the app takes you there), then Android asks you to confirm. On Android 12+ later updates can install without asking, although Xiaomi's installer may still want one tap.
- **Version numbers** come from GitHub's build count (1.1, 1.2, ...). The version line shows the version and the commit it was built from.

### One-time setup: the signing key (do this before the first push)

Android only installs an update signed with the same key as the installed app, so GitHub must sign every build with one permanent key. A key has been made on the PC that set this up, in `%USERPROFILE%\.couchtv-remote-signing\`. Give it to GitHub as two secrets:

1. On GitHub, open the repo → **Settings → Secrets and variables → Actions → New repository secret**.
2. Name **`REMOTE_KEYSTORE_B64`**, value: everything in `keystore-base64.txt`.
3. Name **`REMOTE_KEYSTORE_PASSWORD`**, value: everything in `password.txt`.

(With the GitHub CLI signed in as the repo owner, the same thing is `gh secret set REMOTE_KEYSTORE_B64 < keystore-base64.txt` and `gh secret set REMOTE_KEYSTORE_PASSWORD < password.txt`, run in that folder.)

- **Back up that folder somewhere safe, and never commit it.** If the key is lost, the app has to be uninstalled once and reinstalled with a new key.
- Without the secrets, builds still work, but each one is signed with a temporary key, and the Actions run shows a warning. Updates then fail with "package conflicts" whenever that key changes.
- Builds you make in Android Studio use your PC's own debug key, so they can't install over a GitHub build or the other way round. Uninstall first when you switch between them.

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

## Listen on this phone

Tap **Listen on this phone** (under the search bar) to hear the TV's sound in this phone's headphones, over Wi-Fi. Several phones can listen at once, each with its own headphones and volume (the phone's volume buttons). It keeps playing with the screen off; tap the button again, or **Stop** in the notification, to end. Wired earphones give the best lip sync (about 0.1 s); Bluetooth headphones add 0.1–0.25 s.

## Voice search

- **The first time,** Android asks to let CouchTV Remote use the microphone. If you say no, the app uses Google's own voice screen instead, which has its own permission.
- **Speech recognition is Google's,** the same as Android's voice typing, so it needs the internet. The listening screen has a language button: **English** (Indian English, which also copes with Hinglish) or **हिंदी**. The app remembers your choice.
- **While it listens,** the TV shows *Listening…* and turns its sound down. Tap the mic to finish early, **Type** to type instead, or **Cancel**.
- **Then the words go to the TV** over infrared, about a second for a short search. Keep pointing at the TV until the search bar says they've arrived. If some got lost, the TV says so; just try again.

If the in-app listening screen fails on your phone (some phones have no speech service built in), the app switches to Google's voice screen by itself. If the phone has neither, it offers typing.

## How it sends

Each button sends an NEC frame (38 kHz, address `0xCE`), then a "still held" frame every 108 ms while your finger stays down, exactly like a real remote. The details are in [`../ir-receiver/PROTOCOL.md`](../ir-receiver/PROTOCOL.md), and the code is in `app/src/main/java/com/couchtv/remote/`:

| File | What it does |
|---|---|
| `Nec.kt` | Builds the infrared timing patterns, including text for voice search |
| `IrRemote.kt` | Sends them through the IR blaster on a background thread, with hold-to-repeat |
| `WifiLink.kt` | Finds the TV on the Wi-Fi and keeps a connection to it |
| `Remote.kt` | Sends each press over Wi-Fi when connected, otherwise by infrared |
| `AudioListener.kt`, `ListenService.kt` | *Listen on this phone*: plays the TV's sound here, with the screen off too |
| `VoiceSearch.kt` | Listens, turns speech into text, and sends it; typing too |
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
