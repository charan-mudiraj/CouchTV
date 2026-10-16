# CouchTV Remote (Android)

A remote control app for CouchTV. It works two ways, and picks by itself:

- **Over Wi-Fi**, when the phone is on the same Wi-Fi as the TV: it finds the TV and connects as soon as you open it, with no list to choose from. No pointing needed, and it works on **any** Android phone. The TV icon at the top turns green. **Power is the exception: it goes to the receiver box behind the TV**, which handles it whether the PC is on, asleep or off: by the box's own Wi-Fi when it has one (an ESP32, see the receiver's README), from any phone, or else by infrared. Then the receiver box icon is green too, and tapping the icons says whether the PC and the TV are on, even while the PC is off.
- **By infrared**, on phones with an **IR blaster** (most Xiaomi / Redmi / POCO phones), when the TV isn't on Wi-Fi, for example while the PC is asleep. Point the phone at the TV; CouchTV's [CouchIR receiver](../ir-receiver/README.md) picks it up. This is also what wakes the PC.

**Pairing, once.** Over Wi-Fi the TV only listens to phones paired with it, so neighbours sharing your Wi-Fi can't use it, hear it, or switch it on and off. The first time the app finds the TV, the TV icon at the top turns yellow. Tap it, then **Pair**, and the TV shows a 6-digit code; type it in the app. That's all: from then on it connects by itself, encrypted. A wrong code just asks the TV for a new one. To unpair every phone (say, one was lost), press F8 on the TV's Remote screen (*Forget phones*). Infrared needs no pairing: it only works in the room.

**What's on it**

The top stays put (how the app reaches the TV, the TV's screen and sound on the phone, and **Power**), and underneath, pages swipe sideways like a phone's home screen: the page settles quicker the faster you flick, the pages beside it sit a little back, and the dots at the bottom follow your finger. The app opens on the remote; tap a dot to jump to its page, and Back returns to the remote.

**How it reaches the TV** is a few icons at the top left, in colour:

- **TV:** green when it's connected over Wi-Fi; yellow when it needs pairing (or CouchTV on the TV needs updating); grey and slowly breathing while the app looks for it; crossed out while the TV PC is asleep or off.
- **Receiver box:** green when the box behind the TV answers on its Wi-Fi (so Power works from any phone), grey when it doesn't. Hidden until the TV has told this phone about the box.
- **IR blaster:** shown only while infrared is what the app is using (the TV isn't on Wi-Fi); yellow if the phone's blaster can't send at 38 kHz.

Tap them for the whole story in words, with what to do about each (**Pair**, **TV address**), and the app's version (tap it to check for updates).

0. **Shortcuts and Popular now** (swipe right from the remote): at the top, a row of five round shortcuts for the things used now and then, as in a Xiaomi phone's App Vault: **Settings**, the **Web** app, the **Sleep** timer (it shows how long is left), **Menu**, and the TV's apps that don't fit the remote's bottom row, each with its logo. The row always has five: with more than five shortcuts, the fifth is **More**, which opens the rest in a dialog. Under it, the films and songs the TV suggests under its apps, as posters three to a row, each with the app it plays in. Tap one and it plays on the TV. With the TV off the phone fetches the same lists itself, for the TV's apps and subscriptions, so you can choose before turning the TV on: a tap then turns the TV on (Power, through the receiver box) and plays it as soon as the TV is back. The only page that scrolls up and down.
1. **Remote:** search; the TV's **volume** and **brightness** as two sliders; the D-pad, as big as the screen allows, with a key in each corner of the square round it (shaped like a right-angled triangle whose long side curves along the pad): **Full screen** and the **trackpad** above, **Back** and **play-pause** below; and at the bottom a row of five: two of the TV's streaming apps, **Home**, and two more, each app a rounded square with its own logo. Over Wi-Fi a tap opens any of the TV's apps (the TV lists them); by infrared, Netflix, YouTube, Prime Video and JioHotstar.
2. **Keyboard:** a full PC keyboard, function keys and all. Landing on this page turns the app sideways, the keyboard filling the screen for bigger keys; its ✕ or Back turns it upright again, on the remote.

The **trackpad** is a dialog over the remote: its key at the D-pad's top right opens it (it grows out of the key), and ✕, Back or a tap outside it puts it away.

**Settings** is a screen of its own, like a phone's Settings app: its shortcut on the first page opens it (Back or its arrow closes it). Cards of rows, each with an icon, what it's set to, and a switch or an arrow. **TV:** the TV's settings, changed from here (the power-off countdown, the films and songs rows on the TV, the phone's keyboard for text boxes, checking for updates), as the TV lists them, so new ones show up without updating the app. **Pointer:** the TV pointer's style, size and colour, on a page of its own with a preview, tiles and colour swatches. **This phone:** vibration, keeping Bluetooth off, and the screen mirror's picture (data saver, balanced or sharp). **About:** the connection's details and the app's version (tap it to check for updates).

The D-pad, trackpad and keyboard size themselves to the phone's screen. The keys look and feel like a real remote's: raised, with their sides showing, they sink when pressed and click under your finger (a short vibration going down, and a lighter one coming up on phones with a precise vibration motor).

- **Voice search.** Tap the blue mic and say what to watch. The phone turns your words into text and sends them to the TV, which lets you pick the app to search in. Tap the search bar to type instead.
- **D-pad and OK.** Hold a direction to keep moving.
- **Back, Home, Menu.** Holding Back also goes Home. Menu is a shortcut on the first page.
- **Volume and brightness sliders.** Drag along either, starting anywhere: the level follows your finger by how far it moves, so touching one never jumps. The pill swells under your finger, stretches like a rubber band past either end, ticks every 10%, and shows the level as you go; the speaker changes with the volume, and the sun turns with the brightness. Tap the speaker to mute or unmute. Both show what the TV is at, whatever changed it (the remote's keys, voice search, Windows). Over Wi-Fi the volume is set exactly; by infrared the slider presses the volume keys for you. Brightness is over Wi-Fi: a screen that takes DDC/CI commands (most monitors, a few TVs) turns its own backlight down; on any other, CouchTV dims the picture itself with a see-through black layer, which screenshots and the screen mirror leave out.
- **Play-Pause, Full screen.**
- **Sleep timer** (a shortcut on the first page): pick any length on two wheels, hours and minutes, and see when the TV will go to sleep; **Turn off** stops it. While it runs its shortcut shows how long is left (*42 min*). Over Wi-Fi; by infrared the key steps through the TV's own 30, 60, 90 and 120 minutes, then off.
- **Power**, which puts the TV to sleep and wakes it. With the TV on it waits first (5 seconds, or as the TV's Settings say), in case the tap was a mistake: a big countdown pops up in the middle, a ring running down round the seconds, with a large **Cancel** and **Turn off now** (tapping outside it, Back, or Power again cancel too), while the TV shows the same countdown. Switching on is at once. Right after switching, the TV ignores its power button for about 15 seconds, so the key then shows a ring running down with the seconds left, and a line under the top says what's happening (*Switching off… The TV can be switched on again in 12 s*). A press meanwhile shakes the key and says why, instead of being missed.
- **App shortcuts:** Netflix, YouTube, Prime Video, JioHotstar, Web.
- **The TV's sound on this phone:** the headphones button at the top (green while it plays).
- **A trackpad** for sites that need a mouse, like a laptop's (how to use it is written faintly on the pad): slide to move the pointer (a quicker slide goes further), tap to click, tap twice and slide to drag; slide two fingers to scroll (a flick glides on) or tap with two to right-click. Over Wi-Fi it follows your finger exactly; over infrared the pointer moves in steps. The TV shows its pointer while the trackpad is open (over Wi-Fi), and hides it when you close it. How it looks (an arrow, a dot or a ring; Medium to Giant; white, yellow, blue, green or pink) is in Settings, on its own page with the pointer drawn as it will look; the TV changes as you pick.
- **The TV's screen on the phone:** the screen button at the top (left of the headphones) shows the TV's screen here, full screen in landscape, for when the TV's too far to read: find the film on Netflix and press play from the sofa. Touch it as you would a touchscreen: tap to click, drag to scroll (CouchTV's home screen too: drag a row of films sideways, or the page up and down, and flick to glide), pinch to zoom, hold to right-click. While the page moves the picture goes a little softer so it keeps up with your finger, and sharpens as soon as it stops. The strip at the side has Back, Home, volume down and up, the phone's keyboard, and ✕ (or the phone's Back) to return to the remote. Over Wi-Fi only, and paired. Copy-protected video (a film playing on Netflix or Prime Video) shows black: their menus and rows don't.
- **A PC keyboard:** Esc and F1–F12, numbers and symbols, letters, Tab, Caps, Enter, Backspace, Shift, Ctrl, Win, Alt, the arrows, Insert / Delete / Home / End / Page Up / Page Down, Print Screen and the menu key, laid out like a real one. Shift, Ctrl, Alt and Win stay on (lit) for the next key, so one finger does Ctrl+C; tap one twice to keep it on (a bar shows) until you tap it again, for Alt then Tab, Tab… Two fingers work too: hold Ctrl and tap C. Held keys repeat. Over Wi-Fi only.
- **Typing with this phone's own keyboard:** when a text box on the TV gets the focus (a search box, a login form), a box pops up at the top of the phone with its keyboard, already holding what the TV's box holds. What you type appears on the TV as you type it, in any language, with swipe or voice typing; **Enter** is Enter on the TV, **Clear** empties both, **Done** closes it. A password box shows dots. **Type** on the keyboard page opens it any time, to type wherever the TV's cursor is. Over Wi-Fi only (`TypeOnPhone` in the TV's couchtv.ini turns the pop-up off).

**Testing from a PC over adb.** Some phones (Xiaomi's) won't let adb tap the screen, so the app takes test commands itself while it's open: `adb shell am broadcast -a com.couchtv.remote.TEST --es do "<command>"`, where the command is `tap x y`, `swipe x1 y1 x2 y2 [ms]` (screen pixels; made into real touch events inside the app), `click <view id>`, `page <n>`, `mirror on|off`, `sleep` or `status` (open those dialogs), `close` (closes them). Only adb (the shell) or the system can send them: they need the DUMP permission, which apps don't get. The code is `TestHooks.kt`.

All the codes match CouchTV's default `remote.ini`, so the app works with the TV as soon as the receiver is plugged in, with no setup.

## Get the APK

**Easiest, with no Android Studio:** GitHub builds it automatically whenever `phone-remote/` changes on `main` (see `.github/workflows/phone-remote.yml`). On the phone, open

**https://github.com/charan-mudiraj/CouchTV/releases/download/phone-remote/CouchTV-Remote.apk**

and install it. The first time, Android asks you to allow installs from your browser. To rebuild without changing anything, open the repo's **Actions** tab → *Phone remote APK* → **Run workflow**.

## Updates

After the first install, the app keeps itself up to date:

- **It checks GitHub when you open it** (at most every 15 minutes). To check right away, tap the icons at the top left, then the version line.
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
| `WifiLink.kt` | Finds the TV on the Wi-Fi and keeps a connection to it; pairing |
| `Secure.kt`, `Handshake.kt` | The encryption, and proving who's who (pairing with the TV's code, then each connection); checked by `SecureTest.kt` against CouchTV's numbers |
| `Remote.kt` | Sends each press over Wi-Fi when connected, otherwise by infrared |
| `AudioListener.kt`, `ListenService.kt` | *Listen on this phone*: plays the TV's sound here, with the screen off too |
| `Resampler.kt`, `Concealer.kt` | Keeping pace with the TV's clock inaudibly, and filling in sound lost on the Wi-Fi |
| `BluetoothGuard.kt` | Keeps Bluetooth off while the app is in use |
| `BoardLink.kt` | The receiver box's Wi-Fi: Power, and whether the PC and TV are on |
| `VoiceSearch.kt` | Listens, turns speech into text, and sends it; typing too |
| `Codes.kt` | The button codes |
| `DpadView.kt` | The round D-pad, with its corner keys |
| `LevelSlider.kt` | The volume and brightness sliders |
| `AppsGrid.kt`, `TvApp.kt` | The bottom row: the TV's apps with their logos, Home in the middle |
| `MainActivity.kt` | The screen, and your own buttons |
| `Haptics.kt`, `PressGesture.kt` | The click under the finger, and telling a press from a swipe |
| `TrackpadView.kt` | The trackpad: moving, clicking, dragging and scrolling |
| `KeyboardView.kt` | The PC keyboard page (sideways) |
| `ScreenLink.kt`, `MirrorView.kt` | The TV's screen on the phone, and touching it |
| `TypingBox.kt` | Typing into the TV's text boxes with the phone's own keyboard |
| `PagerView.kt`, `PageDots.kt`, `FitGrid.kt` | The sideways pages, their dots, and button grids that shrink to fit |
| `SuggestionsView.kt`, `Suggestion.kt`, `SuggestionFeed.kt` | The *Popular now* page: the TV's suggestions or, with the TV off, the phone's own; their pictures; playing one |
| `CustomButtons.kt` | Saves your own buttons |

## Feedback to send

This is written but not yet built, so the first build may need a small fix or two. Please send:

1. **Build errors**, if any: copy the first red error from Android Studio's *Build* panel.
2. **Whether the TV reacts.** On the TV, open Settings & power → Remote (or press F2). Every press on the phone should show a green *Got it (NEC 00CE ...)* line. If nothing shows, check that the receiver says *Receiver ready* and that the receiver's RX light blinks when you press.
3. **Range and angle:** how far away it still works.
4. **Anything awkward** about the layout or button sizes on your phone.
