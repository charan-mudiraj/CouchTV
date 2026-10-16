package com.couchtv.remote

import java.util.Base64

/**
 * One setting on the Settings page: the TV's (from its SETTING lines, "TV settings" in ir-receiver/PROTOCOL.md) or
 * this phone's own, drawn the same way. [choices] are (value, name); with just "on" and "off" it's a switch.
 */
data class TvSetting(val key: String, val value: String, val label: String, val note: String?, val choices: List<Pair<String, String>>) {
    val isSwitch: Boolean get() = choices.map { it.first } == listOf("on", "off")

    companion object {
        /** "key value label note value:name,…" (texts in base64, "-" for no note); null if it isn't one. */
        fun parse(line: String): TvSetting? {
            val p = line.trim().split(" ")
            if (p.size < 5 || p[0].isEmpty() || p[1].isEmpty()) return null
            val label = text(p[2]) ?: return null
            val note = if (p[3] == "-") null else text(p[3])
            val choices = p[4].split(",").mapNotNull { choice ->
                val colon = choice.indexOf(':')
                if (colon <= 0) null else text(choice.substring(colon + 1))?.let { choice.substring(0, colon) to it }
            }
            if (choices.isEmpty()) return null
            return TvSetting(p[0], p[1], label, note, choices)
        }

        private fun text(base64: String): String? = runCatching { String(Base64.getDecoder().decode(base64), Charsets.UTF_8) }.getOrNull()
    }
}
