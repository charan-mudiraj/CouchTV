package com.couchtv.remote

import java.util.Base64

/** One of the home screen's apps, as the TV's APP lines say ("APPS" in ir-receiver/PROTOCOL.md): its tile's name and colours. */
data class TvApp(val name: String, val label: String, val background: Int, val foreground: Int) {
    /** As kept between runs (the grid shows the TV's apps while it's off too). */
    fun line(): String = listOf(b64(name), b64(label), "%06X".format(background and 0xFFFFFF), "%06X".format(foreground and 0xFFFFFF)).joinToString(" ")

    companion object {
        /** What a phone without the TV's list shows: the apps the remote's own keys open, by infrared too. */
        val DEFAULTS = listOf(
            TvApp("Netflix", "Netflix", 0xFFE50914.toInt(), 0xFFFFFFFF.toInt()),
            TvApp("YouTube", "YouTube", 0xFFFF1A1A.toInt(), 0xFFFFFFFF.toInt()),
            TvApp("Prime Video", "Prime Video", 0xFF00A8E1.toInt(), 0xFFFFFFFF.toInt()),
            TvApp("JioHotstar", "JioHotstar", 0xFF1C2B7A.toInt(), 0xFFFFFFFF.toInt()),
        )

        /** "name label colour text-colour" (texts in base64, colours RRGGBB); null if it isn't one. */
        fun parse(line: String): TvApp? {
            val p = line.trim().split(" ")
            if (p.size < 4) return null
            val name = text(p[0]) ?: return null
            val label = text(p[1]) ?: return null
            val background = colour(p[2]) ?: return null
            val foreground = colour(p[3]) ?: return null
            return TvApp(name, label, background, foreground)
        }

        private fun text(base64: String): String? = runCatching { String(Base64.getDecoder().decode(base64), Charsets.UTF_8) }.getOrNull()
        private fun b64(text: String): String = Base64.getEncoder().encodeToString(text.toByteArray(Charsets.UTF_8))
        private fun colour(hex: String): Int? = hex.takeIf { it.length == 6 }?.toLongOrNull(16)?.let { (0xFF000000 or it).toInt() }
    }
}
