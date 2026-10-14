package com.couchtv.remote

import java.util.Base64

/**
 * A film or song for the first page, from the TV's "SUGGEST" lines or fetched by this phone (SuggestionFeed): what it
 * is, the app it plays in (with that app's colours, as on the TV), its picture on the internet, and what the TV needs
 * to play it ([playLine]): the app (JustWatch's name for it, or "youtube"), the film's or video's address, and for a
 * song, what to look for on YouTube.
 */
class Suggestion(
    val movie: Boolean,
    val id: String,
    val title: String,
    val subtitle: String,
    val app: String,
    val image: String?,
    val appColor: Int,
    val appTextColor: Int,
    val appKey: String = "",
    val link: String? = null,
    val query: String? = null,
) {
    /** The same, as a SUGGEST line (without "SUGGEST "), for keeping it. */
    fun line(): String = listOf(if (movie) "M" else "S", id, b64(title), b64(subtitle), b64(app), b64(image), hex(appColor),
        hex(appTextColor), appKey.ifEmpty { "-" }, b64(link), b64(query)).joinToString(" ")

    /** "PLAY <id> M|S <app> <address> <title> <query>": the TV plays it from its own list, or else from this. */
    fun playLine(): String = "PLAY $id ${if (movie) "M" else "S"} ${appKey.ifEmpty { "-" }} ${b64(link)} ${b64(title)} ${b64(query)}"

    companion object {
        /**
         * "M|S <id> <title> <details> <app> <picture> <RRGGBB> <RRGGBB> [<app key> <address> <query>]" (texts in base64,
         * "-" for none; a TV from before the last three sends without them); null if it isn't one.
         */
        fun parse(line: String): Suggestion? {
            val p = line.trim().split(" ")
            if (p.size < 8 || (p[0] != "M" && p[0] != "S") || p[1].isEmpty()) return null
            val title = text(p[2]) ?: return null
            return Suggestion(p[0] == "M", p[1], title, text(p[3]) ?: "", text(p[4]) ?: "", text(p[5]),
                colour(p[6], 0xFF262D3A.toInt()), colour(p[7], 0xFFFFFFFF.toInt()),
                p.getOrNull(8)?.takeIf { it != "-" } ?: "", p.getOrNull(9)?.let { text(it) }, p.getOrNull(10)?.let { text(it) })
        }

        fun text(field: String): String? =
            if (field == "-") null else runCatching { String(Base64.getDecoder().decode(field), Charsets.UTF_8) }.getOrNull()

        fun colour(hex: String, fallback: Int): Int =
            if (hex.length != 6) fallback else hex.toIntOrNull(16)?.let { it or 0xFF000000.toInt() } ?: fallback

        private fun b64(s: String?) = if (s.isNullOrEmpty()) "-" else Base64.getEncoder().encodeToString(s.toByteArray(Charsets.UTF_8))

        private fun hex(c: Int) = "%06X".format(c and 0xFFFFFF)
    }
}
