package com.couchtv.watch

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** Picking trailers on YouTube the same way the TV does (its --suggesttest checks the same titles). */
class YouTubeTest {
    @Test
    fun picksTrailers() {
        assertTrue(YouTube.trailerMatches("Drishyam 3", "Drishyam 3 - Official Telugu Trailer | Mohanlal | Jeethu Joseph"))
        assertTrue(YouTube.trailerMatches("Epic - First Semester", "EPIC - First Semester Trailer | Anand Deverakonda"))
        assertTrue(YouTube.trailerMatches("DC", "DC - Release Trailer (Telugu) | Lokesh Kanagaraj"))
        assertTrue(YouTube.trailerMatches("Dorothy", "DOROTHY Official Trailer (Telugu) | Karthik Subbaraj"))
        assertTrue(YouTube.trailerMatches("Bandar", "BANDAR | Official Trailer | Bobby D, Sanya M"))
        assertTrue(YouTube.trailerMatches("Jigris", "JIGRIS Movie Official Trailer | Krishna B"))
    }

    @Test
    fun leavesOthers() {
        assertFalse(YouTube.trailerMatches("Simon", "Love, Simon | Official Trailer [HD] | 20th Century FOX"))
        assertFalse(YouTube.trailerMatches("Azhagu", "Azhagae Azhagu | Episode Promo | 23rd October 2026"))
        assertFalse(YouTube.trailerMatches("Solo", "Solo Boy Trailer | Gautham Krishna | Ramya Pasupuleti"))
        assertFalse(YouTube.trailerMatches("Drishyam 3", "Drishyam 3 Trailer Reaction | Mohanlal"))
        assertFalse(YouTube.trailerMatches("Drishyam 3", "Drishyam 3 | Full Movie Review"))
    }

    @Test
    fun readsSearchResults() {
        val page = """{"contents":[{"videoRenderer":{"videoId":"abcdefghijk","thumbnail":{"x":1},"title":{"runs":[{"text":"Drishyam 3 & \"More\" Trailer"}]}}},""" +
            """{"videoRenderer":{"videoId":"ABCDEFGHI-_","title":{"runs":[{"text":"Second"}]}}}]}"""
        val videos = YouTube.parse(page, 5)
        assertEquals(2, videos.size)
        assertEquals("abcdefghijk", videos[0].id)
        assertEquals("Drishyam 3 & \"More\" Trailer", videos[0].title)
        assertEquals("ABCDEFGHI-_", videos[1].id)
        assertEquals(1, YouTube.parse(page, 1).size)
    }

    @Test
    fun knowsIds() {
        assertTrue(YouTube.isId("dQw4w9WgXcQ"))
        assertFalse(YouTube.isId("short"))
        assertFalse(YouTube.isId("dQw4w9WgXcQ'); alert(1)//"))
    }
}
