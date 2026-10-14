package com.couchtv.remote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The phone's own lists: tidied as the TV tidies them (Suggestions.cs), and for the apps the TV says it has. */
class SuggestionFeedTest {
    @Test
    fun tidiesSongsAsTheTvDoes() {
        assertEquals("Radhimaa", SuggestionFeed.songTitle("Radhimaa (feat. Sai Smriti) [From \"Think Indie\"]"))
        assertEquals("One Sun One Moon", SuggestionFeed.songTitle("One Sun One Moon (From \"Jailer 2\")"))
        assertEquals("Arz Kiya Hai Coke Studio Bharat", SuggestionFeed.songTitle("Arz Kiya Hai  Coke Studio Bharat"))
        assertEquals("KALYANI (Remix)", SuggestionFeed.songTitle("KALYANI (Remix)"))
        assertEquals("Karan Aujla", SuggestionFeed.leadArtist("Karan Aujla & MXRCI"))
        assertEquals("Shashwat Sachdev", SuggestionFeed.leadArtist("Shashwat Sachdev, Arijit Singh, Irshad Kamil & Armaan Khan"))
    }

    @Test
    fun readsTheTvsSetup() {
        // As CouchTV sent it for a TV subscribed to Netflix only.
        val setup = SuggestionFeed.Setup.parse("Y N nfx:TmV0ZmxpeA==:TmV0ZmxpeA==:000000:E50914:Y,youtube:WW91VHViZQ==:WW91VHViZQ==:FF1A1A:FFFFFF:N," +
            "prv:UHJpbWUgVmlkZW8=:UHJpbWUgVmlkZW8=:00A8E1:FFFFFF:N")!!
        assertTrue(setup.movies)
        assertFalse(setup.music)
        assertEquals(listOf("nfx", "youtube", "prv"), setup.apps.map { it.key })
        assertEquals("Prime Video", setup.apps[2].label)
        assertEquals(0xFF000000.toInt(), setup.apps[0].color)
        assertEquals(0xFFE50914.toInt(), setup.apps[0].textColor)
        assertEquals(listOf(true, false, false), setup.apps.map { it.yours })
    }

    @Test
    fun readsATvWithNoApps() {
        assertEquals(0, SuggestionFeed.Setup.parse("N N -")!!.apps.size)
        assertNull(SuggestionFeed.Setup.parse("maybe Y -"))
        assertNull(SuggestionFeed.Setup.parse("Y Y"))
    }
}
