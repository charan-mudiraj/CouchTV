package com.couchtv.watch

import android.content.Context

/**
 * Each title's YouTube video for the hero: JustWatch's trailer, else one found on YouTube (kept, "" when there's
 * none), and for a song its video (which Watch opens on YouTube, and Play on TV sends to the TV).
 */
class Trailers(context: Context) {
    private val prefs = context.getSharedPreferences("trailers", Context.MODE_PRIVATE)

    /** Its video if known; "" when looked for and there's none; null when not looked for yet. */
    fun known(t: Title): String? = t.trailer ?: prefs.getString(t.id, null)

    /** Looks for it on YouTube (blocking; throws when offline) and keeps what it found. */
    fun find(t: Title): String {
        known(t)?.let { return it }
        val video = if (t.kind == Kind.SONG) {
            YouTube.search(t.query ?: t.title, 1).firstOrNull()?.id
        } else {
            val year = Regex("\\b(?:19|20)\\d\\d\\b").find(t.details)?.value
            val query = t.title + (if (t.kind == Kind.SERIES) " series" else if (year != null) " $year" else "") + " official trailer"
            YouTube.search(query, 5).firstOrNull { YouTube.trailerMatches(t.title, it.title) }?.id
        }
        val found = video ?: ""
        prefs.edit().putString(t.id, found).apply()
        return found
    }
}
