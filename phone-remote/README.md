# CouchTV Remote (Android)

A remote control app for CouchTV. It works two ways, and picks by itself:

- **Over Wi-Fi**, when the phone is on the same Wi-Fi as the TV: it finds the TV and connects as soon as you open it, with no list to choose from. No pointing needed, and it works on **any** Android phone. The top line says *Connected to … over Wi-Fi*. **Power is the exception: it goes to the receiver box behind the TV**, which handles it whether the PC is on, asleep or off: by the box's own Wi-Fi when it has one (an ESP32, see the receiver's README), from any phone, or else by infrared. Then the top line also says whether the PC and the TV are on, even while the PC is off.
- **By infrared**, on phones with an **IR blaster** (most Xiaomi / Redmi / POCO phones), when the TV isn't on Wi-Fi, for example while the PC is asleep. Point the phone at the TV; CouchTV's [CouchIR receiver](../ir-receiver/README.md) picks it up. This is also what wakes the PC.

**What's on it**

Everything fits the screen; nothing scrolls up or down. The top stays put (the connection status, the headphones button for the TV's sound, and **Power**), and underneath, pages swipe sideways like a phone's home screen. The dots at the bottom show which page you're on; tap one to jump there, and Back returns to the first page.

1. **Remote:** search, the D-pad, Back / Home / Menu, volume and Play-Pause.
2. **Apps:** big tiles for Netflix, YouTube, Prime Video, JioHotstar and Web, plus the TV's Exit, Full screen, Sleep timer and Delete.
3. **Trackpad:** like a laptop's, with Left and Right click buttons below it.
4. **My buttons:** your own buttons, and the app's version.

The D-pad, trackpad and grids size themselves to the phone's screen. The keys look and feel like a real remote's: raised, with their sides showing, they sink when pressed and click under your finger (a short vibration going down, and a lighter one coming up on phones with a precise vibration motor).

- **Voice search.** Tap the blue mic and say what to watch. The phone turns your words into text and sends them to the TV, which lets you pick the app to search in. Tap the search bar to type instead.
- **D-pad and OK.** Hold a direction to keep moving.
- **Back, Home, Menu.** Holding Back also goes Home.
- **Volume down / Mute / Volume up, and Play-Pause.**
- **Exit, Full screen, Sleep timer, Delete.**
- **Power**, which puts the TV to sleep and wakes it.
- **App shortcuts:** Netflix, YouTube, Prime Video, JioHotstar, Web.
- **The TV's sound on this phone:** the headphones button at the top (green while it plays).
- **A trackpad** for sites that need a mouse, like a laptop's: slide to move the pointer (a quicker slide goes further), tap to click, tap twice and slide to drag; slide two fingers to scroll (a flick glides on) or tap with two to right-click. Over Wi-Fi it follows your finger exactly; over infrared the pointer moves in steps. Swipe to other pages from outside the pad. **Pointer** (top right of the page) changes how the TV's pointer looks: an arrow, a dot or a ring; Medium to Giant; white, yellow, blue, green or pink. The TV changes as you pick.
- **My buttons:** add buttons with your own codes, then teach the TV what they do.

All the codes match CouchTV's default `remote.ini`, so the app works with the TV as soon as the receiver is plugged in, with no setup.

## Get the APK

**Easiest, with no Android Studio:** GitHub builds it automatically whenever `phone-remote/` changes on `main` (see `.github/workflows/phone-remote.yml`). On the phone, open

**https://github.com/charan-mudiraj/CouchTV/releases/download/phone-remote/CouchTV-Remote.apk**

and install it. The first time, Android asks you to allow installs from your browser. To rebuild without changing anything, open the repo's **Actions** tab → *Phone remote APK* → **Run workflow**.

## Updates

After the first install, the app keeps itself up to date:

- **It checks GitHub when you open it** (at most every 15 minutes). To check right away, tap the version line on the *My buttons* page.
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

Tap the **headphones button** at the top to hear the TV's sound in this phone's headphones, over Wi-Fi. Several phones can listen at once, each with its own headphones and volume (the phone's volume buttons). It keeps playing with the screen off; the button stays green meanwhile. Tap it again, or **Stop** in the notification, to end. Lip sync is about 0.1 s.

The sound plays on wired earphones (or USB, or line out) when they're plugged in, else the phone's speaker, never on Bluetooth: that's where it sounds best, and Bluetooth shares the phone's 2.4 GHz radio with the Wi-Fi the sound comes over. So the app also keeps Bluetooth **off** while it's in use (on screen, or playing the TV's sound), and switches it off again if it's turned on. On Android 12 and older it does that by itself (Android 12 asks for *Nearby devices* once). Android 13 and newer don't let apps switch Bluetooth off, so there the app shows Android's own *Turn off Bluetooth?* question each time it finds Bluetooth on, unless the app is the phone's device owner (the dedicated-remote setup), which can switch it off by itself.

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
| `Resampler.kt`, `Concealer.kt` | Keeping pace with the TV's clock inaudibly, and filling in sound lost on the Wi-Fi |
| `BluetoothGuard.kt` | Keeps Bluetooth off while the app is in use |
| `BoardLink.kt` | The receiver box's Wi-Fi: Power, and whether the PC and TV are on |
| `VoiceSearch.kt` | Listens, turns speech into text, and sends it; typing too |
| `Codes.kt` | The button codes |
| `DpadView.kt` | The round D-pad |
| `MainActivity.kt` | The screen, and your own buttons |
| `Haptics.kt`, `PressGesture.kt` | The click under the finger, and telling a press from a swipe |
| `TrackpadView.kt` | The trackpad: moving, clicking, dragging and scrolling |
| `PagerView.kt`, `PageDots.kt`, `FitGrid.kt` | The sideways pages, their dots, and button grids that shrink to fit |
| `CustomButtons.kt` | Saves your own buttons |

## Feedback to send

This is written but not yet built, so the first build may need a small fix or two. Please send:

1. **Build errors**, if any: copy the first red error from Android Studio's *Build* panel.
2. **Whether the TV reacts.** On the TV, open Settings & power → Remote (or press F2). Every press on the phone should show a green *Got it (NEC 00CE ...)* line. If nothing shows, check that the receiver says *Receiver ready* and that the receiver's RX light blinks when you press.
3. **Range and angle:** how far away it still works.
4. **Anything awkward** about the layout or button sizes on your phone.
