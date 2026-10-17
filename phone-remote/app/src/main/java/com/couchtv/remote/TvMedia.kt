package com.couchtv.remote

import java.util.Base64

/**
 * What the TV is playing, as it last said ("MEDIA" in ir-receiver/PROTOCOL.md), for the notification with its buttons.
 * [at] is when [positionMs] was right (SystemClock.elapsedRealtime), so the timeline moves on by itself while it plays.
 */
data class TvMedia(
    val playing: Boolean,
    val positionMs: Long,
    val durationMs: Long,          // 0: not known (a live stream)
    val at: Long,
    val canPlayPause: Boolean,
    val canNext: Boolean,
    val canPrevious: Boolean,
    val canSeek: Boolean,
    val artId: String,             // the picture (MEDIAART), "-" for none
    val app: String,               // where it plays: "YouTube", "Spotify"... ("" when the TV couldn't tell)
    val title: String,
    val artist: String,
) {
    /** Where it's got to at [now] (elapsedRealtime): on from [positionMs] while it plays, never past the end. */
    fun positionAt(now: Long): Long {
        val p = positionMs + if (playing) (now - at).coerceAtLeast(0) else 0
        return if (durationMs > 0) p.coerceAtMost(durationMs) else p
    }

    /** The same, played or paused at [now]: the timeline stops (or starts again) where it is. */
    fun played(play: Boolean, now: Long) = copy(playing = play, positionMs = positionAt(now), at = now)

    /** The same, moved to [ms] at [now]. */
    fun movedTo(ms: Long, now: Long) = copy(positionMs = if (durationMs > 0) ms.coerceIn(0, durationMs) else ms.coerceAtLeast(0), at = now)

    companion object {
        /**
         * The line after "MEDIA ": "<playing|paused> <position ms> <length ms> <flags> <picture id> <app> <title> <artist>"
         * (texts in base64, "-" for none; flags: P play/pause, N next, B previous, S seek, or "-"), as of [now].
         * Null for "none", or a line that doesn't make sense.
         */
        fun parse(line: String, now: Long): TvMedia? {
            val p = line.trim().split(" ")
            if (p.size < 8 || (p[0] != "playing" && p[0] != "paused")) return null
            val position = p[1].toLongOrNull() ?: return null
            val duration = p[2].toLongOrNull() ?: return null
            val flags = if (p[3] == "-") "" else p[3]
            return TvMedia(
                playing = p[0] == "playing", positionMs = position.coerceAtLeast(0), durationMs = duration.coerceAtLeast(0), at = now,
                canPlayPause = 'P' in flags, canNext = 'N' in flags, canPrevious = 'B' in flags, canSeek = 'S' in flags,
                artId = p[4], app = text(p[5]), title = text(p[6]), artist = text(p[7]),
            )
        }

        /** The line after "MEDIAART ": "<id> <JPEG in base64>", or just "-" for no picture. Null if it doesn't make sense. */
        fun art(line: String): Pair<String, ByteArray?>? {
            val p = line.trim().split(" ")
            val id = p.getOrNull(0)?.takeIf { it.isNotEmpty() } ?: return null
            if (id == "-" || p.size < 2) return id to null
            val bytes = runCatching { Base64.getDecoder().decode(p[1]) }.getOrNull() ?: return null
            return id to bytes
        }

        private fun text(b64: String): String =
            if (b64 == "-") "" else runCatching { String(Base64.getDecoder().decode(b64), Charsets.UTF_8) }.getOrDefault("")
    }
}
