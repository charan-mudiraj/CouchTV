package com.couchtv.remote

/**
 * A film on the watchlist: when to watch it, and which phone changed that last. The same line on every phone, on the
 * TV (CouchTV's Watchlist.cs) and in the mailbox between phones:
 *   <id> <watch at> <changed> <by> on|off <the film, as a SUGGEST line says it>
 * Times in ms since 1970 (UTC); by: the phone's pairing id ("-" for none); off: taken off the list, remembered a
 * while so an old copy can't bring it back. The newer change wins ([newerThan]). Plain Kotlin, for WatchItemTest.
 */
class WatchItem(val id: String, val film: Suggestion, val watchAt: Long, val changed: Long, val by: String, val on: Boolean) {
    fun line() = "$id $watchAt $changed ${by.ifEmpty { "-" }} ${if (on) "on" else "off"} ${film.line()}"

    /** The later change wins; for two at the same moment, the same one everywhere (the line that sorts last). */
    fun newerThan(other: WatchItem): Boolean = changed > other.changed || (changed == other.changed && line() > other.line())

    /** Kept until a day after its time; one taken off, also until two weeks after that change (as the TV keeps them). */
    fun keep(now: Long) = if (on) watchAt + DAY > now else maxOf(watchAt + DAY, changed + 14 * DAY) > now

    /** On the list: on it, and not more than a day past its time. */
    fun live(now: Long) = on && watchAt + DAY > now

    /** The same film, now at [at] (or taken off), changed by this phone ([me]) just now (always after the last change). */
    fun changedTo(at: Long, on: Boolean, me: String, now: Long) = WatchItem(id, film, at, maxOf(now, changed + 1), me, on)

    companion object {
        const val DAY = 24L * 3600 * 1000
        const val HOUR = 3600L * 1000
        private val SHAPE = Regex("^([A-Za-z0-9_-]{1,60}) (\\d{1,15}) (\\d{1,15}) ([0-9a-f]{32}|-) (on|off) (\\S.*)$")

        /**
         * The film's id on the watchlist: the TV's search puts "f" before its films' ids, so a film found there is the
         * same one as on the first page.
         */
        fun idOf(film: Suggestion): String {
            val id = film.id.removePrefix("f").ifEmpty { film.id }
            return id.replace(Regex("[^A-Za-z0-9_-]"), "_").take(60)
        }

        /** A film picked just now by this phone ([me]) to watch at [at]. */
        fun new(film: Suggestion, at: Long, me: String, now: Long) = WatchItem(idOf(film), film, at, now, me, true)

        /** Null if it isn't a watchlist line (or isn't a film). */
        fun parse(line: String): WatchItem? {
            val trimmed = line.trim()
            if (trimmed.length > 4000) return null
            val m = SHAPE.matchEntire(trimmed) ?: return null
            val film = Suggestion.parse(m.groupValues[6])?.takeIf { it.movie } ?: return null
            val watchAt = m.groupValues[2].toLongOrNull() ?: return null
            val changed = m.groupValues[3].toLongOrNull() ?: return null
            return WatchItem(m.groupValues[1], film, watchAt, changed, m.groupValues[4].takeIf { it != "-" } ?: "", m.groupValues[5] == "on")
        }
    }
}

/** What a merge changed: the film as it was here (null: new here) and as it is now. */
class WatchMerge(val before: WatchItem?, val after: WatchItem)

/** The watchlist as this phone has it: a film an id, newest change kept. Not thread-safe (Watchlist locks round it). */
class WatchBook {
    private val items = LinkedHashMap<String, WatchItem>()

    fun get(id: String): WatchItem? = items[id]

    /** Everything kept, the ones taken off too. */
    fun all(): List<WatchItem> = items.values.toList()

    /** The films on the list, soonest first. */
    fun live(now: Long): List<WatchItem> = items.values.filter { it.live(now) }.sortedBy { it.watchAt }

    /** A copy from elsewhere (or a change here): kept if it's new or newer. What changed, or null if nothing did. */
    fun merge(item: WatchItem, now: Long): WatchMerge? {
        if (!item.keep(now)) return null
        val before = items[item.id]
        if (before != null && !item.newerThan(before)) return null
        items[item.id] = item
        return WatchMerge(before, item)
    }

    /** Lets go of films past keeping, then the earliest beyond [MOST]. */
    fun tidy(now: Long) {
        items.values.removeAll { !it.keep(now) }
        while (items.size > MOST) items.remove(items.values.minByOrNull { it.watchAt }!!.id)
    }

    fun lines(): List<String> = items.values.map { it.line() }

    companion object {
        const val MOST = 100

        fun of(lines: List<String>, now: Long) = WatchBook().apply {
            lines.mapNotNull { WatchItem.parse(it) }.forEach { merge(it, now) }
        }
    }
}

/**
 * The lock on the mailbox the phones leave watchlist changes in for each other while the TV sleeps (ntfy.sh, a free
 * message relay on the internet; see Mailbox): the mailbox's name and its keys, all made from the receiver box's key,
 * which the TV gives every phone paired with it (and changes when it forgets them all, F8), so only those phones can
 * find it or read it. A message is base64 of an 8-byte random nonce, the AES-256-CTR ciphertext and a 16-byte
 * HMAC-SHA256 tag over both.
 */
class MailboxLock(boardKey: ByteArray) {
    val topic = "couchtv-" + Secure.hex(Secure.hkdf(boardKey, ByteArray(0), Secure.utf8("CouchTV watchlist topic"), 16))
    private val enc = Secure.hkdf(boardKey, ByteArray(0), Secure.utf8("CouchTV watchlist enc"), 32)
    private val mac = Secure.hkdf(boardKey, ByteArray(0), Secure.utf8("CouchTV watchlist mac"), 32)

    fun seal(text: String, nonce: ByteArray = Secure.random(8)): String {
        val plain = Secure.utf8(text)
        val sealed = ByteArray(8 + plain.size + Secure.TAG_BYTES)
        nonce.copyInto(sealed, 0, 0, 8)
        plain.copyInto(sealed, 8)
        Ctr(enc).apply(number(sealed), sealed, 8, plain.size)
        Secure.hmac(mac, sealed.copyOf(8 + plain.size)).copyInto(sealed, 8 + plain.size, 0, Secure.TAG_BYTES)
        return Secure.base64(sealed)
    }

    /** The text, or null if it wasn't sealed with this lock (someone else's message, or changed on the way). */
    fun open(message: String): String? {
        val sealed = Secure.fromBase64(message.trim()) ?: return null
        if (sealed.size < 8 + Secure.TAG_BYTES) return null
        val length = sealed.size - 8 - Secure.TAG_BYTES
        val tag = Secure.hmac(mac, sealed.copyOf(8 + length)).copyOf(Secure.TAG_BYTES)
        if (!Secure.same(tag, sealed.copyOfRange(8 + length, sealed.size))) return null
        Ctr(enc).apply(number(sealed), sealed, 8, length)
        return String(sealed, 8, length, Charsets.UTF_8)
    }

    private fun number(sealed: ByteArray): Long {
        var n = 0L
        for (i in 0 until 8) n = (n shl 8) or (sealed[i].toLong() and 0xFF)
        return n
    }
}
