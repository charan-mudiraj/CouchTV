package com.couchtv.remote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.Base64

/** Suggestion against SUGGEST lines as CouchTV's Suggestions.PhoneLines writes them (without "SUGGEST "), and PLAY as FromPhone reads it. */
class SuggestionTest {
    private fun b64(s: String) = Base64.getEncoder().encodeToString(s.toByteArray(Charsets.UTF_8))

    @Test
    fun readsAFilm() {
        val s = Suggestion.parse("M mtm1599021 VmlzaHdhbmF0aCAmIFNvbnM= MjAyNiDCtyAyaCA0MW0= TmV0ZmxpeA== " +
            "aHR0cHM6Ly9pbWFnZXMuanVzdHdhdGNoLmNvbS9wb3N0ZXIvMzQ1ODM2MjExL3MzMzIvdmlzaHdhbmF0aC1hbmQtc29ucy5qcGc= 141414 E50914 nfx " +
            b64("https://www.netflix.com/watch/82034837") + " -")!!
        assertTrue(s.movie)
        assertEquals("mtm1599021", s.id)
        assertEquals("Vishwanath & Sons", s.title)
        assertEquals("2026 · 2h 41m", s.subtitle)
        assertEquals("Netflix", s.app)
        assertEquals("https://images.justwatch.com/poster/345836211/s332/vishwanath-and-sons.jpg", s.image)
        assertEquals(0xFF141414.toInt(), s.appColor)
        assertEquals(0xFFE50914.toInt(), s.appTextColor)
        assertEquals("nfx", s.appKey)
        assertEquals("https://www.netflix.com/watch/82034837", s.link)
        assertNull(s.query)
    }

    @Test
    fun readsASongWithoutDetailsOrPicture() {
        val s = Suggestion.parse("S s1267390213 SGF3YXllaW4= - WW91VHViZQ== - FF0000 FFFFFF")!!   // a TV from before the last three
        assertFalse(s.movie)
        assertEquals("Hawayein", s.title)
        assertEquals("", s.subtitle)
        assertEquals("YouTube", s.app)
        assertNull(s.image)
        assertEquals(0xFFFF0000.toInt(), s.appColor)
        assertEquals("", s.appKey)
        assertNull(s.link)
    }

    @Test
    fun keepsAndPlaysOne() {
        val song = Suggestion(false, "s1267390213", "Hawayein", "Pritam & Arijit Singh", "YouTube", null, 0xFFFF1A1A.toInt(),
            0xFFFFFFFF.toInt(), "youtube", null, "Hawayein Pritam")
        val again = Suggestion.parse(song.line())!!
        assertEquals(song.line(), again.line())
        assertEquals("Pritam & Arijit Singh", again.subtitle)
        assertEquals("Hawayein Pritam", again.query)
        assertEquals("PLAY s1267390213 S youtube - " + b64("Hawayein") + " " + b64("Hawayein Pritam"), song.playLine())
    }

    @Test
    fun ignoresWhatIsntOne() {
        assertNull(Suggestion.parse("M mtm1 SGF3YXllaW4= - WW91VHViZQ== - FF0000"))                 // a field short
        assertNull(Suggestion.parse("X s1 SGF3YXllaW4= - WW91VHViZQ== - FF0000 FFFFFF"))             // neither film nor song
        assertNull(Suggestion.parse("S s1 - - WW91VHViZQ== - FF0000 FFFFFF"))                        // no title
        assertNull(Suggestion.parse("S s1 %%%% - WW91VHViZQ== - FF0000 FFFFFF"))                     // not base64
        assertEquals(0xFF262D3A.toInt(), Suggestion.parse("S s1 SGF3YXllaW4= - WW91VHViZQ== - red FFFFFF")!!.appColor)   // a colour it can't read
    }
}
