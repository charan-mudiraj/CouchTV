package com.couchtv.remote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Suggestion.parse against SUGGEST lines as CouchTV's Suggestions.PhoneLines writes them (without "SUGGEST "). */
class SuggestionTest {
    @Test
    fun readsAFilm() {
        val s = Suggestion.parse("M mtm1599021 VmlzaHdhbmF0aCAmIFNvbnM= MjAyNiDCtyAyaCA0MW0= TmV0ZmxpeA== " +
            "aHR0cHM6Ly9pbWFnZXMuanVzdHdhdGNoLmNvbS9wb3N0ZXIvMzQ1ODM2MjExL3MzMzIvdmlzaHdhbmF0aC1hbmQtc29ucy5qcGc= 141414 E50914")!!
        assertTrue(s.movie)
        assertEquals("mtm1599021", s.id)
        assertEquals("Vishwanath & Sons", s.title)
        assertEquals("2026 · 2h 41m", s.subtitle)
        assertEquals("Netflix", s.app)
        assertEquals("https://images.justwatch.com/poster/345836211/s332/vishwanath-and-sons.jpg", s.image)
        assertEquals(0xFF141414.toInt(), s.appColor)
        assertEquals(0xFFE50914.toInt(), s.appTextColor)
    }

    @Test
    fun readsASongWithoutDetailsOrPicture() {
        val s = Suggestion.parse("S s1267390213 SGF3YXllaW4= - WW91VHViZQ== - FF0000 FFFFFF")!!
        assertFalse(s.movie)
        assertEquals("Hawayein", s.title)
        assertEquals("", s.subtitle)
        assertEquals("YouTube", s.app)
        assertNull(s.image)
        assertEquals(0xFFFF0000.toInt(), s.appColor)
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
