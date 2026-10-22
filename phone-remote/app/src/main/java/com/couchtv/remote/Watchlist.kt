package com.couchtv.remote

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.util.Log
import java.util.concurrent.Executors

/**
 * The watchlist: films picked on the first page to watch at a time, shared by every phone paired with the TV. Each
 * phone keeps its own copy (here) and reminds its own person (WatchReminders): when another phone adds a film, an
 * hour before, and at the time. Changes travel two ways: to the TV over Wi-Fi while it's on (it keeps a copy for good
 * and passes them straight on to the phones connected: WATCH lines), and through an encrypted mailbox on the internet
 * (Mailbox, MailboxLock), which every phone checks every quarter of an hour or so (WatchJob) and as the app opens, so
 * they arrive with the TV asleep, or the phones away from home, too. A film is one line (WatchItem); the newer change
 * wins wherever two meet. Thread-safe.
 */
class Watchlist private constructor(context: Context) {
    companion object {
        private const val TAG = "CouchTV"
        /** A film not seen in the mailbox this long is left there again (it keeps messages for about 12 hours). */
        private const val REPOST_MS = 8 * WatchItem.HOUR

        @Volatile private var instance: Watchlist? = null

        fun get(context: Context): Watchlist =
            instance ?: synchronized(this) { instance ?: Watchlist(context.applicationContext).also { instance = it } }
    }

    private val app = context.applicationContext
    private val prefs = app.getSharedPreferences("watchlist", Context.MODE_PRIVATE)
    private val main = Handler(Looper.getMainLooper())
    private val work = Executors.newSingleThreadExecutor()   // the mailbox, one thing at a time
    private val book = WatchBook.of(prefs.getString("lines", "")!!.split("\n").filter { it.isNotBlank() }, now())

    /** This phone's pairing id: which films it changed last (no "added" notification for its own). */
    val me: String = Pairings(app).phoneId

    /** Called on the main thread whenever the list changes (the first page shows it). */
    var onChange: (() -> Unit)? = null

    /** Sends a film (a WATCH line without "WATCH ") to the TV while it's connected; set by MainActivity. */
    @Volatile var toTv: ((String) -> Unit)? = null

    private fun now() = System.currentTimeMillis()

    /** The films on the list, soonest first. */
    fun live(): List<WatchItem> = synchronized(book) { book.live(now()) }

    /** [film] on the list, or null. */
    fun find(film: Suggestion): WatchItem? = synchronized(book) { book.get(WatchItem.idOf(film))?.takeIf { it.live(now()) } }

    /** The film by its id, on the list or taken off; null if it isn't kept. */
    fun find(id: String): WatchItem? = synchronized(book) { book.get(id) }

    /** Everything kept, taken off too: for the TV as this phone connects, and the reminders after a restart. */
    fun all(): List<WatchItem> = synchronized(book) {
        book.tidy(now())
        book.all()
    }

    /** Picked here: [film] to watch at [at] (added, or its time changed). */
    fun set(film: Suggestion, at: Long) {
        val t = now()
        val old = synchronized(book) { book.get(WatchItem.idOf(film)) }
        changedHere(old?.let { WatchItem(it.id, film.withoutRow(), at, maxOf(t, it.changed + 1), me, true) } ?: WatchItem.new(film, at, me, t))
    }

    /** Taken off the list here. */
    fun remove(id: String) {
        val old = find(id)?.takeIf { it.on } ?: return
        changedHere(old.changedTo(old.watchAt, false, me, now()))
    }

    private fun changedHere(item: WatchItem) {
        val merged = merge(item) ?: return
        WatchReminders.changed(app, merged, here = true)
        toTv?.invoke(item.line())
        synchronized(prefs) { prefs.edit().putStringSet("pending", pending() + item.id).apply() }
        work.execute { runCatching { sendPending() }.onFailure { Log.i(TAG, "Watchlist mailbox: ${it.message}") } }
    }

    /** A film from the TV (a WATCH line without "WATCH "). */
    fun received(line: String) {
        val item = WatchItem.parse(line) ?: return
        merge(item)?.let { WatchReminders.changed(app, it, here = false) }
    }

    private fun merge(item: WatchItem): WatchMerge? {
        val merged = synchronized(book) {
            book.merge(item, now())?.also {
                book.tidy(now())
                prefs.edit().putString("lines", book.lines().joinToString("\n")).apply()
            }
        } ?: return null
        main.post { onChange?.invoke() }
        return merged
    }

    // ---------------------------------------------------------------- the mailbox

    /** Checks the mailbox and leaves what's due there, in the background; then [done] (on that thread). */
    fun sync(done: (() -> Unit)? = null) {
        work.execute {
            runCatching { syncNow() }.onFailure { Log.i(TAG, "Watchlist mailbox: ${it.message}") }
            done?.invoke()
        }
    }

    /** The mailbox's lock, from the receiver box's key the TV gave this phone; null before it has (never connected). */
    private fun lock(): MailboxLock? {
        val key = app.getSharedPreferences("board", Context.MODE_PRIVATE).getString("key", null)?.let { Secure.fromHex(it) }
        return key?.takeIf { it.size == 32 }?.let { MailboxLock(it) }
    }

    private fun pending(): Set<String> = prefs.getStringSet("pending", emptySet())!!.toSet()

    /**
     * What the other phones left (merged here: their notifications and reminders follow), then what this phone has
     * to leave: its changes not sent yet, and any film the mailbox hasn't seen for a while (so it's still there for a
     * phone that was off or away all day). On [work] only.
     */
    private fun syncNow() {
        val lock = lock() ?: return
        forNewMailbox(lock)
        val since = prefs.getLong("since", 0L)
        var newest = since
        for (message in Mailbox.poll(lock.topic, since)) {
            newest = maxOf(newest, message.time)
            val text = lock.open(message.body) ?: continue
            val item = WatchItem.parse(text.removePrefix("WATCH ").takeIf { text.startsWith("WATCH ") } ?: continue) ?: continue
            val merged = merge(item)
            if (merged != null) {
                WatchReminders.changed(app, merged, here = false)
                toTv?.invoke(item.line())   // the TV (if it's on) hears it too, and tells the phones connected
            }
            if (find(item.id)?.line() == item.line()) seen(item, message.time * 1000)
        }
        if (newest != since) prefs.edit().putLong("since", newest).apply()
        sendPending(lock)
        val now = now()
        for (item in all()) {
            val worth = item.live(now) || (!item.on && item.changed + 2 * WatchItem.DAY > now)
            if (worth && now - seenAt(item) > REPOST_MS) send(lock, item)
        }
    }

    /** The TV forgot its phones (F8) and gave a new key: a new mailbox, with nothing of this phone's in it yet. */
    private fun forNewMailbox(lock: MailboxLock) {
        if (prefs.getString("topic", null) == lock.topic) return
        prefs.edit().putString("topic", lock.topic).putLong("since", 0L).remove("seen").apply()
    }

    private fun sendPending(lock: MailboxLock? = lock()) {
        lock ?: return
        forNewMailbox(lock)
        for (id in pending()) {
            val item = find(id)
            if (item == null || send(lock, item)) synchronized(prefs) { prefs.edit().putStringSet("pending", pending() - id).apply() }
        }
    }

    private fun send(lock: MailboxLock, item: WatchItem): Boolean {
        if (!Mailbox.post(lock.topic, lock.seal("WATCH " + item.line()))) return false
        seen(item, now())
        return true
    }

    /** When the mailbox last had this film as it is now: "<id> <changed> <when>" lines. */
    private fun seenAt(item: WatchItem): Long = prefs.getString("seen", "")!!.split("\n").firstNotNullOfOrNull { line ->
        val p = line.split(" ")
        if (p.size == 3 && p[0] == item.id && p[1] == item.changed.toString()) p[2].toLongOrNull() else null
    } ?: 0L

    private fun seen(item: WatchItem, at: Long) {
        if (seenAt(item) >= at) return
        val kept = synchronized(book) { book.all().map { it.id }.toSet() }
        val lines = prefs.getString("seen", "")!!.split("\n").filter { line ->
            val id = line.substringBefore(" ")
            line.isNotBlank() && id != item.id && id in kept
        } + "${item.id} ${item.changed} $at"
        prefs.edit().putString("seen", lines.joinToString("\n")).apply()
    }
}
