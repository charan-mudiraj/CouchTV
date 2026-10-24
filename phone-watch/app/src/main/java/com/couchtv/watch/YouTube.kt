package com.couchtv.watch

import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

/**
 * YouTube's own search page, read as a browser would (as the TV does, SmartSearch.FirstYouTubeVideo): for the
 * trailers JustWatch doesn't list (Suggestions.TrailerMatches on the TV picks them the same way), and songs' videos.
 * Nothing here needs Android, so the unit test (YouTubeTest) runs it on the build machine.
 */
object YouTube {
    class Video(val id: String, val title: String)

    // In the search page's data, each ordinary result is a "videoRenderer" (ads, Shorts and channels are not).
    private val RESULT = Regex(
        "\"videoRenderer\":\\{\"videoId\":\"([A-Za-z0-9_-]{11})\".{0,4000}?\"title\":\\{\"runs\":\\[\\{\"text\":\"((?:[^\"\\\\]|\\\\.)*)\"",
        RegexOption.DOT_MATCHES_ALL)
    private val ID = Regex("^[\\w-]{11}$")
    private val TRAILER = Regex("\\b(?:trailer|teaser|promo)\\b", RegexOption.IGNORE_CASE)
    private val NOT_TRAILER = Regex("\\b(?:reaction|reacts?|review|explained|breakdown|spoof|parody|recap)\\b", RegexOption.IGNORE_CASE)
    private val AFTER_ONE_WORD = Regex("^ (?:official|trailer|teaser|promo|movie|film|series|web|release|tamil|telugu|hindi|malayalam|kannada|bengali|marathi)\\b")

    /** A YouTube video's id (11 letters, digits, - and _). */
    fun isId(text: String?): Boolean = text != null && ID.matches(text)

    /** The first [most] videos YouTube's search finds (videos only). Blocking; throws when offline. */
    fun search(query: String, most: Int): List<Video> {
        val url = "https://www.youtube.com/results?search_query=" + URLEncoder.encode(query, "UTF-8") + "&sp=EgIQAQ%3D%3D"
        val c = URL(url).openConnection() as HttpURLConnection
        c.connectTimeout = 8000
        c.readTimeout = 8000
        c.setRequestProperty("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36")
        c.setRequestProperty("Accept-Language", "en-IN,en;q=0.9")
        try {
            return parse(c.inputStream.bufferedReader(Charsets.UTF_8).use { it.readText() }, most)
        } finally {
            c.disconnect()
        }
    }

    fun parse(page: String, most: Int): List<Video> =
        RESULT.findAll(page).take(most).map { Video(it.groupValues[1], unescape(it.groupValues[2])) }.toList()

    /**
     * Whether a video is a trailer for this title: it says trailer (teaser, promo), not a reaction to one, and has most
     * of the title's words. A one-word title ("Simon", "Solo") must be the whole of the video's name before its first
     * "|" or " - ", but for "Official Trailer" and the like: not "Love, Simon" or "Solo Boy Trailer".
     */
    fun trailerMatches(title: String, video: String): Boolean {
        if (!TRAILER.containsMatchIn(video) || NOT_TRAILER.containsMatchIn(video)) return false
        val wanted = words(title).split(" ").filter { it.isNotEmpty() }
        if (wanted.isEmpty()) return false
        if (wanted.size == 1) {
            val name = words(video.split(Regex("\\s[|\\-–:]\\s|[|(\\[]"))[0])
            if (!name.startsWith(wanted[0])) return false
            val rest = name.substring(wanted[0].length)
            return rest.isEmpty() || AFTER_ONE_WORD.containsMatchIn(rest)
        }
        val have = " " + words(video) + " "
        val matched = wanted.count { have.contains(" $it ") }
        return matched * 10 >= wanted.size * 6
    }

    private fun words(text: String) = text.lowercase().replace(Regex("[^\\p{L}\\p{N}]+"), " ").trim()

    /** A JSON string's text: \" \\ \/ \n \t \uXXXX. */
    fun unescape(s: String): String {
        val out = StringBuilder()
        var i = 0
        while (i < s.length) {
            val ch = s[i]
            if (ch != '\\' || i + 1 >= s.length) {
                out.append(ch)
                i++
                continue
            }
            val next = s[i + 1]
            if (next == 'u' && i + 5 < s.length) {
                val code = s.substring(i + 2, i + 6).toIntOrNull(16)
                if (code != null) out.append(code.toChar())
                i += 6
            } else {
                out.append(when (next) {
                    'n' -> '\n'
                    't' -> '\t'
                    else -> next
                })
                i += 2
            }
        }
        return out.toString()
    }
}
