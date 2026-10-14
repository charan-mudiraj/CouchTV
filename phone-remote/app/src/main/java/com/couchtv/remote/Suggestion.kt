package com.couchtv.remote

import java.util.Base64

/**
 * A film or song the TV suggests (its "SUGGEST" lines), for the first page: what it is, the app it plays in (with
 * that app's colours, as on the TV), and its picture on the internet. "PLAY <id>" plays it on the TV.
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
) {
    companion object {
        /** "M|S <id> <title> <subtitle> <app> <picture> <RRGGBB> <RRGGBB>" (texts in base64, "-" for none); null if it isn't one. */
        fun parse(line: String): Suggestion? {
            val p = line.trim().split(" ")
            if (p.size < 8 || (p[0] != "M" && p[0] != "S") || p[1].isEmpty()) return null
            val title = text(p[2]) ?: return null
            return Suggestion(p[0] == "M", p[1], title, text(p[3]) ?: "", text(p[4]) ?: "", text(p[5]),
                colour(p[6], 0xFF262D3A.toInt()), colour(p[7], 0xFFFFFFFF.toInt()))
        }

        private fun text(field: String): String? =
            if (field == "-") null else runCatching { String(Base64.getDecoder().decode(field), Charsets.UTF_8) }.getOrNull()

        private fun colour(hex: String, fallback: Int): Int =
            if (hex.length != 6) fallback else hex.toIntOrNull(16)?.let { it or 0xFF000000.toInt() } ?: fallback
    }
}
