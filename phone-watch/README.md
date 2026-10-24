# CouchTV Watch (Android)

The TV's home screen on your phone, for watching on the phone: films, series and songs from all your apps in one place, without thinking about which app has what.

- **Tabs along the top:** *All*, then each app (Netflix, YouTube, Prime Video, JioHotstar, ZEE5, Sun NXT, ETV Win). *All* has what's popular on all of them; an app's tab its own new films and series; YouTube's, the top songs.
- **The chosen title, big at the top:** its picture, name, year, IMDb score, genres and what it's about, then after a moment **its trailer, with sound**. Tap the trailer to turn its sound off or on. It stops when you scroll down, and starts again when you come back up.
- **Rows of posters** under it. Tap one to put it at the top.
- **Watch on Netflix** (or Prime Video…) opens the title in that app on the phone. A song plays on YouTube.
- **Play on TV** hands the title to [CouchTV Remote](../phone-remote/README.md), which turns the TV on if it's off and plays it there.
- **Search** (the magnifier): films and series on your apps as you type.
- **Settings** (the gear): which apps you have, and the version (tap to check for an update).

Install the apps you watch from the Play Store and log in once in each. A title then opens straight in its app; if an app isn't on the phone, its website opens instead. ETV Win's films and shows aren't listed (JustWatch, where the lists come from, doesn't know it); its tab opens the app.

## How it works

- The lists come from the same places as the TV's (see [PROTOCOL.md](../ir-receiver/PROTOCOL.md#suggestions)): JustWatch's films and series popular in India, from its own GraphQL API, and Apple Music's most-played songs in India (`Catalog.kt`). They're kept for offline and fetched again when 6 hours old.
- Trailers are JustWatch's YouTube trailers, or for the titles it has none for, the first of YouTube's search results that's the title's trailer, picked as the TV picks them (`YouTube.kt`, `Trailers.kt`). They play in YouTube's own player inside the app (`TrailerView.kt`, `assets/trailer.html`).
- A title opens in whichever app on the phone takes its link (Android says which), so the app needs no list of the other apps' names (`Opener.kt`).
- Play on TV starts the remote's `PlayOnTv` screen. Only apps signed with the remote's key may (the `com.couchtv.permission.PLAY_ON_TV` permission), so both apps are built with the remote's permanent key (the `REMOTE_KEYSTORE_*` secrets, see the remote's README).
- No libraries, like the remote: only the Android framework, with the screens built in code.
- `.github/workflows/phone-watch.yml` builds it on every change to `phone-watch/` on main and publishes it to the `phone-watch` release, which the app checks for updates. A `dev/*` branch is built and tested without publishing.

Download: https://github.com/charan-mudiraj/CouchTV/releases/download/phone-watch/CouchTV-Watch.apk
