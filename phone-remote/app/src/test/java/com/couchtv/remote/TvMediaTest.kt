package com.couchtv.remote

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.Base64

/** The lines CouchTV sends about what it plays (HomeWindow.Media.cs, MediaLines), read as the TV means them. */
class TvMediaTest {
    private fun b64(text: String) = Base64.getEncoder().encodeToString(text.toByteArray(Charsets.UTF_8))

    @Test
    fun readsWhatPlays() {
        val m = TvMedia.parse("playing 83000 245000 PNBS 3f2a9c ${b64("YouTube")} ${b64("Gehra Hua | Dhurandhar")} ${b64("Saregama Music")}", 1000)!!
        assertTrue(m.playing)
        assertEquals(83000L, m.positionMs)
        assertEquals(245000L, m.durationMs)
        assertTrue(m.canPlayPause && m.canNext && m.canPrevious && m.canSeek)
        assertEquals("3f2a9c", m.artId)
        assertEquals("YouTube", m.app)
        assertEquals("Gehra Hua | Dhurandhar", m.title)
        assertEquals("Saregama Music", m.artist)
    }

    @Test
    fun readsABareOne() {
        val m = TvMedia.parse("paused 0 0 - - - ${b64("Radio")} -", 0)!!
        assertFalse(m.playing)
        assertEquals(0L, m.durationMs)
        assertFalse(m.canPlayPause || m.canNext || m.canPrevious || m.canSeek)
        assertEquals("", m.app)
        assertEquals("Radio", m.title)
        assertEquals("", m.artist)
    }

    @Test
    fun nothingOrNonsenseIsNull() {
        assertNull(TvMedia.parse("none", 0))
        assertNull(TvMedia.parse("playing soon 1 P - - - -", 0))
        assertNull(TvMedia.parse("playing 1 2", 0))
    }

    @Test
    fun theTimelineMovesOnWhilePlaying() {
        val m = TvMedia.parse("playing 10000 200000 PS - - ${b64("Song")} -", 5000)!!
        assertEquals(12500L, m.positionAt(7500))
        assertEquals(200000L, m.positionAt(999999))                 // never past the end
        val paused = m.played(false, 7500)
        assertEquals(12500L, paused.positionAt(60000))              // stopped where it was
        assertEquals(15000L, paused.played(true, 60000).positionAt(62500))
        assertEquals(200000L, m.movedTo(250000, 8000).positionMs)   // a seek past the end goes to the end
    }

    @Test
    fun readsThePicture() {
        val jpeg = byteArrayOf(0xFF.toByte(), 0xD8.toByte(), 1, 2, 3)
        val (id, bytes) = TvMedia.art("a1b2 " + Base64.getEncoder().encodeToString(jpeg))!!
        assertEquals("a1b2", id)
        assertArrayEquals(jpeg, bytes)
        assertEquals("-" to null, TvMedia.art("-"))
        assertNull(TvMedia.art("a1b2 !!!"))
    }
}
