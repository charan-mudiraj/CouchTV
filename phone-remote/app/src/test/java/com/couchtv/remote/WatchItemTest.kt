package com.couchtv.remote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The watchlist's lines (as CouchTV's Watchlist.cs keeps them), which change wins, and the mailbox's lock. */
class WatchItemTest {
    private val me = "0123456789abcdef0123456789abcdef"
    private val now = 1_790_000_000_000L
    private val hour = WatchItem.HOUR

    // As CouchTV's self-test writes one (Diagnostics.WatchLine).
    private fun line(id: String, at: Long, changed: Long, on: Boolean) =
        "$id $at $changed $me ${if (on) "on" else "off"} M $id S2FudGFyYQ== MjAyNQ== TmV0ZmxpeA== - E50914 FFFFFF netflix - -"

    @Test
    fun readsTheTvsLinesAndWritesThemTheSame() {
        val text = line("mtm1", now + 5 * hour, now, true)
        val item = WatchItem.parse(text)!!
        assertEquals("mtm1", item.id)
        assertEquals(now + 5 * hour, item.watchAt)
        assertEquals("Kantara", item.film.title)
        assertEquals("Netflix", item.film.app)
        assertEquals(me, item.by)
        assertTrue(item.on)
        assertEquals(text, item.line())
    }

    @Test
    fun refusesWhatIsntAFilmLine() {
        assertNull(WatchItem.parse("mtm1 ${now + hour} $now - on S a1 eA== - - - FF0000 FFFFFF"))   // a song
        assertNull(WatchItem.parse("mtm1 soon $now - on M mtm1 eA== - - - FF0000 FFFFFF"))
        assertNull(WatchItem.parse("../x ${now + hour} $now - on M x eA== - - - FF0000 FFFFFF"))
        assertNotNull(WatchItem.parse("mtm1 ${now + hour} $now - on M mtm1 eA== - - - FF0000 FFFFFF"))
    }

    @Test
    fun aFilmFoundOnTheTvsSearchIsTheSameOne() {
        val found = Suggestion(true, "fmtm1", "Kantara", "", "Netflix", null, 0, 0)
        val listed = Suggestion(true, "mtm1", "Kantara", "", "Netflix", null, 0, 0)
        assertEquals(WatchItem.idOf(listed), WatchItem.idOf(found))
    }

    @Test
    fun theNewerChangeWins() {
        val book = WatchBook()
        val first = WatchItem.parse(line("mtm1", now + 5 * hour, now - 10, true))!!
        assertNull(book.merge(first, now)!!.before)
        assertNull(book.merge(WatchItem.parse(first.line())!!, now))                               // the same again
        assertNull(book.merge(WatchItem.parse(line("mtm1", now + 9 * hour, now - 20, true))!!, now))   // older
        val moved = first.changedTo(now + 7 * hour, true, me, now - 10)                              // same moment: still after
        assertTrue(moved.changed > first.changed)
        val merged = book.merge(moved, now)!!
        assertEquals(first.watchAt, merged.before!!.watchAt)
        assertEquals(now + 7 * hour, book.get("mtm1")!!.watchAt)
        val off = moved.changedTo(moved.watchAt, false, me, now)
        book.merge(off, now)
        assertNull(book.merge(moved, now))   // an older copy can't bring it back
        assertTrue(book.live(now).isEmpty())
        assertEquals(1, book.all().size)
    }

    @Test
    fun keepsFilmsADayPastTheirTimeAndOnesTakenOffTwoWeeks() {
        val film = WatchItem.parse(line("mtm1", now, now - hour, true))!!
        assertTrue(film.live(now + 23 * hour))
        assertFalse(film.live(now + 25 * hour))
        assertNull(WatchBook().merge(WatchItem.parse(line("mtm2", now - 25 * hour, now, true))!!, now))   // too late to take
        val off = WatchItem.parse(line("mtm3", now, now, false))!!
        assertTrue(off.keep(now + 13 * WatchItem.DAY))
        assertFalse(off.keep(now + 15 * WatchItem.DAY))
        val book = WatchBook.of(listOf(film.line(), off.line(), "nonsense"), now)
        book.tidy(now + 2 * WatchItem.DAY)
        assertEquals(listOf(off.line()), book.lines())
    }

    @Test
    fun theMailboxLockMatchesNumbersWorkedOutElsewhere() {
        // Worked out with .NET's HMAC-SHA256 and AES (see the commit that added this): the receiver box's key 00..1F.
        val lock = MailboxLock(ByteArray(32) { it.toByte() })
        assertEquals("couchtv-e64d3a7fbadb895cad766ca36313739f", lock.topic)
        assertEquals("AQIDBAUGBwhRrr5qB4DSCGygNTH8CtElTNzhnY8jrN1Hmg==", lock.seal("WATCH test", byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8)))
        assertEquals("WATCH test", lock.open("AQIDBAUGBwhRrr5qB4DSCGygNTH8CtElTNzhnY8jrN1Hmg=="))
    }

    @Test
    fun theMailboxOpensOnlyItsOwnUnchangedMessages() {
        val lock = MailboxLock(ByteArray(32) { 7 })
        val text = "WATCH " + line("mtm1", now, now, true)
        val sealed = lock.seal(text)
        assertEquals(text, lock.open(sealed))
        assertNull(MailboxLock(ByteArray(32) { 8 }).open(sealed))   // another TV's phones
        val bent = Secure.fromBase64(sealed)!!
        bent[12] = (bent[12].toInt() xor 1).toByte()
        assertNull(lock.open(Secure.base64(bent)))
        assertNull(lock.open("hello"))
        assertTrue(lock.seal(text) != sealed)   // a new nonce each time
    }
}
