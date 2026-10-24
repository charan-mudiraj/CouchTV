# CouchTV Watch (Android)

The TV's home screen on your phone, for watching on the phone: films, series and songs from all your apps in one place, without thinking about which app has what.

- **Tabs along the top:** *All*, then each app (Netflix, YouTube, Prime Video, JioHotstar, ZEE5, Sun NXT, ETV Win). *All* has what's popular on all of them; an app's tab its own new films and series; YouTube's, the top songs.
- **The chosen title, big at the top:** its picture, name, year, IMDb score, genres and what it's about, then after a moment **its trailer, with sound**. Tap the trailer to turn its sound off or on. It stops when you scroll down, and starts again when you come back up.
- **Rows of posters** under it. Tap one to put it at the top.
- **Watch** plays the title **inside the app** for YouTube, JioHotstar, ZEE5, Sun NXT and ETV Win: their own website's player, full screen (its full-screen button turns the phone sideways; Back leaves it). Log in once on each, inside the app. **Netflix and Prime Video only play in their own apps on a phone** (they don't play in a browser there), so for them Watch opens that app. *Play here* in Settings switches the in-app player off.
- **Play on TV** hands the title to [CouchTV Remote](../phone-remote/README.md), which turns the TV on if it's off and plays it there.
- **Search** (the magnifier): films and series on your apps as you type.
- **Settings** (the gear): which apps you have, and the version (tap to check for an update).

For Netflix and Prime Video, install their apps from the Play Store and log in once. ETV Win's films and shows aren't listed (JustWatch, where the lists come from, doesn't know it); its tab opens its website in the player.

## How it works

- The lists come from the same places as the TV's (see [PROTOCOL.md](../ir-receiver/PROTOCOL.md#suggestions)): JustWatch's films and series popular in India, from its own GraphQL API, and Apple Music's most-played songs in India (`Catalog.kt`). They're kept for offline and fetched again when 6 hours old.
- Trailers are JustWatch's YouTube trailers, or for the titles it has none for, the first of YouTube's search results that's the title's trailer, picked as the TV picks them (`YouTube.kt`, `Trailers.kt`). They play in YouTube's own player inside the app (`TrailerView.kt`, `assets/trailer.html`).
- The player (`PlayerActivity.kt`) is a WebView on the title's page, with copy-protected video (Widevine) allowed, the logins kept, and "open in the app" links not followed. Ads on those sites aren't blocked, unlike on the TV.
- Netflix's and Prime Video's titles open in whichever app on the phone takes their link (Android says which), so the app needs no list of the other apps' names (`Opener.kt`).
- `TestHooks.kt` (as the remote's) lets a PC drive it over adb: `adb shell am broadcast -a com.couchtv.watch.TEST --es do "'tab zee'"` (also `pick <n>`, `watch`, `play <url>`, `tap x y`, `swipe …`, and `back` in the player).
- Play on TV starts the remote's `PlayOnTv` screen. Only apps signed with the remote's key may (the `com.couchtv.permission.PLAY_ON_TV` permission), so both apps are built with the remote's permanent key (the `REMOTE_KEYSTORE_*` secrets, see the remote's README).
- No libraries, like the remote: only the Android framework, with the screens built in code.
- `.github/workflows/phone-watch.yml` builds it on every change to `phone-watch/` on main and publishes it to the `phone-watch` release, which the app checks for updates. A `dev/*` branch is built and tested without publishing.

Download: https://github.com/charan-mudiraj/CouchTV/releases/download/phone-watch/CouchTV-Watch.apk
