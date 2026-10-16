package com.couchtv.remote

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.Base64
import android.util.Log
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.util.Locale
import java.util.concurrent.Executors
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

/**
 * The remote over Wi-Fi. Finds CouchTV on the phone's Wi-Fi by itself (a broadcast the TV answers) and keeps a
 * connection to it, so buttons work without pointing, and on phones without an IR blaster. If the TV goes away
 * (asleep, or off the network) it keeps looking, and Remote falls back to infrared meanwhile.
 * Only a phone paired with the TV gets in: it pairs once, with a code the TV shows ([pair]), and after that every
 * connection proves it's this phone and is encrypted (Handshake, Channel), so others on a shared Wi-Fi can't use the
 * TV or read what's sent. The format is "Wi-Fi" and "Security" in ir-receiver/PROTOCOL.md in the CouchTV repo.
 */
class WifiLink(context: Context) {
    companion object {
        const val DISCOVERY_PORT = 47700
        const val CONTROL_PORT = 47701
        const val DEFAULT_ADDRESS = "192.168.0.13"   // the TV PC's reserved address on the home router
        private const val TAG = "CouchTV"
    }

    private class Tv(val address: InetAddress, val port: Int, val name: String)

    /** A connection to the TV once it's proven: its lines and their encryption. */
    private class Link(val socket: Socket, val io: LineIo, val channel: Channel)

    /** How a connection attempt went. */
    private enum class Outcome { NONE, CONNECTED, UNPAIRED, TOO_OLD }

    /** A text box on the TV with the keyboard focus ("INPUT"): what this phone's keyboard pops up for. */
    class TypingField(val password: Boolean, val name: String, val value: String)

    /** Pairing, step by step; all called on the main thread. */
    interface PairListener {
        /** The TV shows a code now: ask for it, then [typeCode]. */
        fun codeShown()
        /** The TV won't pair now: [waitSeconds] 0 means another phone is pairing, else too many wrong codes. */
        fun refused(waitSeconds: Int)
        fun finished(result: Handshake.PairResult)
    }

    /** The TV's address while connected, e.g. for sending its sound to this phone. */
    @Volatile var tvAddress: InetAddress? = null
        private set

    /** The TV's name while connected, else null. */
    @Volatile var tvName: String? = null
        private set

    /** A TV on this Wi-Fi this phone isn't paired with yet (its name), so the app offers to pair; else null. */
    @Volatile var unpairedTv: String? = null
        private set

    /** A TV answered, but its CouchTV is from before pairing, so it needs updating first. */
    @Volatile var tvTooOld = false
        private set

    /** Called on the main thread when the TV connects or goes away, or pairing is needed. */
    var onChange: (() -> Unit)? = null

    /** Called on a background thread with the key the receiver box checks Power with; the TV gives it to each paired phone. */
    var onBoardKey: ((ByteArray) -> Unit)? = null

    /** Called on the main thread when a text box on the TV gets the focus, or (null) loses it, or the link drops. */
    var onTyping: ((TypingField?) -> Unit)? = null

    /** Called on the main thread with the TV's suggestions (films and songs, for the first page), as it connects or changes them. */
    var onSuggestions: ((List<Suggestion>) -> Unit)? = null

    /** Called on the main thread with what the TV has ("SUGGESTSETUP", without its name), for fetching them here while it's off. */
    var onSuggestSetup: ((String) -> Unit)? = null

    /** Called on the main thread when the TV's sleep timer changes (see [sleepEndsAt]). */
    var onSleepTimer: (() -> Unit)? = null

    /** When the TV's sleep timer ends (SystemClock.elapsedRealtime), as it last said; 0 when it's off. */
    @Volatile var sleepEndsAt = 0L
        private set

    /** The TV says how its sleep timer is, so it takes any number of minutes (an older CouchTV only has its steps). */
    @Volatile var sleepTimerKnown = false
        private set

    /** The trackpad page is in front: the TV shows its pointer meanwhile, and hides it otherwise. Told again on each connection. */
    @Volatile var trackpadOpen = false
        set(value) {
            if (field == value) return
            field = value
            send("TRACKPAD " + if (value) "ON" else "OFF")
        }

    private val app = context.applicationContext
    private val connectivity = app.getSystemService(ConnectivityManager::class.java)
    private val main = Handler(Looper.getMainLooper())
    private val prefs = app.getSharedPreferences("wifi", Context.MODE_PRIVATE)
    private val pairings = Pairings(app)
    private val io = Executors.newSingleThreadExecutor()    // finding, connecting, reading
    private val out = Executors.newSingleThreadExecutor()   // writing, in order
    @Volatile private var running = false
    @Volatile private var generation = 0
    @Volatile private var socket: Socket? = null
    @Volatile private var link: Link? = null
    @Volatile private var lastHeard = 0L
    @Volatile private var welcomed = false
    @Volatile private var pairAddress: InetAddress? = null   // where the unpaired TV answered
    @Volatile private var pairSocket: Socket? = null
    private val typed = LinkedBlockingQueue<String>()
    private var incoming: MutableList<String>? = null   // a list of suggestions arriving (on the reading thread)
    private var expected = 0

    /**
     * The TV's address (reserved for the TV PC in the router): it's tried first, with no searching. If it doesn't
     * answer, the app searches the network as usual, so it still works if the TV's address changes. Change it by
     * tapping the status line; empty means "just search".
     */
    var manualAddress: String?
        get() = prefs.getString("manual_ip", DEFAULT_ADDRESS)?.takeIf { it.isNotBlank() }
        set(value) {
            prefs.edit().putString("manual_ip", value?.trim() ?: "").apply()
            restart()
        }

    /** Connected, and the TV answered recently (it answers a ping every second), so it's really there. */
    val connected: Boolean
        get() = tvName != null && SystemClock.elapsedRealtime() - lastHeard < 2500

    fun start() {
        if (running) return
        running = true
        val gen = ++generation
        io.execute { loop(gen) }
    }

    fun stop() {
        running = false
        generation++
        close()
        setTv(null)
    }

    private fun restart() {
        if (!running) return
        stop()
        start()
    }

    /** A button, exactly like an infrared frame: N for a new press, R while it's held. */
    fun button(address: Int, command: Int, repeat: Boolean) {
        send(String.format(Locale.ROOT, "BTN %02X %02X %s", address and 0xFF, command and 0xFF, if (repeat) "R" else "N"))
    }

    /** The trackpad: move the TV's pointer by pixels. */
    fun move(dx: Int, dy: Int) = send("MOVE $dx $dy")

    /** The trackpad: scroll, 120 a mouse-wheel notch, up and right positive. */
    fun wheel(dy: Int, dx: Int) = send(if (dx == 0) "WHEEL $dy" else "WHEEL $dy $dx")

    /** The TV pointer's look as the TV last said, e.g. "arrow 64 white"; null if it hasn't (an older CouchTV). */
    @Volatile var pointerLook: String? = null
        private set

    /** Changes the TV pointer's look: style (arrow, dot, ring), size in pixels, colour. */
    fun setPointer(style: String, size: Int, color: String) {
        pointerLook = "$style $size $color"
        send("POINTER $style $size $color")
    }

    /** The trackpad: a mouse button down or up. */
    fun mouse(right: Boolean, down: Boolean) = send("MOUSE ${if (right) "R" else "L"} ${if (down) "D" else "U"}")

    /** The screen mirror: a finger down ('D'), moved ('M') or up ('U') at x, y from 0 to 65535 across the TV's screen. */
    fun touch(finger: Int, kind: Char, x: Int, y: Int) = send("TOUCH $finger $kind $x $y")

    /** The keyboard page: a PC key (Windows virtual-key code) down or up. */
    fun key(vk: Int, down: Boolean) = send(String.format(Locale.ROOT, "KEY %02X %s", vk and 0xFF, if (down) "D" else "U"))

    /** Typing on this phone's keyboard into the TV's text box: [backspaces], then [text]. */
    fun edit(backspaces: Int, text: String) {
        if (backspaces == 0 && text.isEmpty()) return
        send(if (text.isEmpty()) "EDIT $backspaces" else "EDIT $backspaces " + Base64.encodeToString(text.toByteArray(Charsets.UTF_8), Base64.NO_WRAP))
    }

    /** Words for search. */
    fun text(words: String) {
        send("TEXT " + Base64.encodeToString(words.toByteArray(Charsets.UTF_8), Base64.NO_WRAP))
    }

    // ---------------------------------------------------------------- pairing

    /**
     * Pairs with the TV that asked ([unpairedTv]): it shows a code, which the person types here ([typeCode]). On
     * its own connection, so the search goes on meanwhile; once paired, the link connects again straight away.
     */
    fun pair(listener: PairListener) {
        typed.clear()
        Thread({
            val result = try {
                pairNow(listener)
            } catch (e: Exception) {
                Log.i(TAG, "Pairing: ${e.message}")
                Handshake.PairResult.FAILED
            } finally {
                pairSocket = null
            }
            if (result != null) main.post { listener.finished(result) }
        }, "CouchTV pairing").start()
    }

    /** The code the person typed, once [PairListener.codeShown] asked for it. */
    fun typeCode(code: String) {
        typed.offer(code)
    }

    fun cancelPairing() {
        typed.offer("")
        runCatching { pairSocket?.close() }
    }

    /** Null when it ended without a result to show (refused, or cancelled). */
    private fun pairNow(listener: PairListener): Handshake.PairResult? {
        val address = pairAddress ?: return Handshake.PairResult.FAILED
        val network = wifiNetwork() ?: return Handshake.PairResult.FAILED
        network.socketFactory.createSocket().use { s ->
            pairSocket = s
            s.connect(InetSocketAddress(address, CONTROL_PORT), 3000)
            s.tcpNoDelay = true
            s.soTimeout = 5000
            val lines = LineIo(s.getInputStream(), s.getOutputStream())
            val hs = Handshake.start(lines, pairings.phoneId, BuildConfig.VERSION_NAME) ?: return Handshake.PairResult.FAILED
            if (hs.known && pairings.key(hs.tvId) != null) {
                setUnpaired(null)
                restart()
                return Handshake.PairResult.PAIRED
            }
            lines.write("PAIR " + Secure.base64(Secure.utf8(phoneName())))
            val answer = lines.read() ?: return Handshake.PairResult.FAILED
            when {
                answer == "CODE" -> main.post { listener.codeShown() }
                answer == "BUSY" -> {
                    main.post { listener.refused(0) }
                    return null
                }
                answer.startsWith("WAIT ") -> {
                    val seconds = answer.removePrefix("WAIT ").trim().toIntOrNull() ?: 600
                    main.post { listener.refused(maxOf(1, seconds)) }
                    return null
                }
                else -> return Handshake.PairResult.FAILED
            }
            val code = typed.poll(120, TimeUnit.SECONDS)
            if (code.isNullOrEmpty()) return null   // cancelled, or nobody typed it
            s.soTimeout = 10000
            val result = hs.pair(lines, code)
            if (result != Handshake.PairResult.PAIRED) return result
            val key = hs.pairKey ?: return Handshake.PairResult.FAILED
            hs.auth(lines, key) ?: return Handshake.PairResult.NOT_SAFE
            pairings.save(hs.tvId, key)
            Log.i(TAG, "Paired with ${hs.tvName}")
        }
        setUnpaired(null)
        restart()
        return Handshake.PairResult.PAIRED
    }

    /** How the TV shows this phone while pairing, e.g. "OPPO CPH2591". */
    private fun phoneName(): String {
        val maker = Build.MANUFACTURER.replaceFirstChar { it.uppercase() }
        val model = Build.MODEL ?: ""
        return if (model.startsWith(maker, ignoreCase = true)) model else "$maker $model".trim()
    }

    // ---------------------------------------------------------------- finding and keeping the TV

    private fun loop(gen: Int) {
        while (running && gen == generation) {
            var outcome = Outcome.NONE
            val network = wifiNetwork()
            if (network != null) {
                // The address you entered first; then a search of the network, in case it changed.
                val manual = manualAddress
                if (manual != null) {
                    val tv = runCatching { Tv(InetAddress.getByName(manual), CONTROL_PORT, manual) }.getOrNull()
                    if (tv != null) outcome = attempt(network, tv, gen)
                }
                if (outcome == Outcome.NONE && running && gen == generation) {
                    val tv = runCatching { find(network) }.getOrNull()
                    if (tv != null) outcome = attempt(network, tv, gen)
                }
            }
            if (outcome != Outcome.UNPAIRED) setUnpaired(null)
            if (outcome != Outcome.TOO_OLD) setTooOld(false)
            // Connected and dropped: straight back. A TV that needs pairing or updating: ask it again now and then.
            pause(when (outcome) {
                Outcome.CONNECTED -> 500L
                Outcome.NONE -> 2000L
                else -> 5000L
            }, gen)
        }
    }

    private fun pause(ms: Long, gen: Int) {
        val until = SystemClock.elapsedRealtime() + ms
        while (running && gen == generation && SystemClock.elapsedRealtime() < until) SystemClock.sleep(100)
    }

    /** Connects and stays connected while the TV answers. */
    private fun attempt(network: Network, tv: Tv, gen: Int): Outcome {
        if (!running || gen != generation) return Outcome.NONE
        welcomed = false
        var outcome = Outcome.NONE
        try {
            outcome = session(network, tv)
        } catch (e: Exception) {
            Log.i(TAG, "Wi-Fi remote ${tv.address.hostAddress}: ${e.message}")
        }
        close()
        setTv(null)
        return if (welcomed) Outcome.CONNECTED else outcome
    }

    /** The Wi-Fi network (not mobile data, even when the phone sends internet traffic that way). */
    @Suppress("DEPRECATION")
    private fun wifiNetwork(): Network? = connectivity.allNetworks.firstOrNull {
        val caps = connectivity.getNetworkCapabilities(it)
        caps != null && (caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) || caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET))
    }

    /** Asks the whole network "COUCHTV?" (and the TV it found last time, directly) and takes the first answer. */
    private fun find(network: Network): Tv? {
        val targets = ArrayList<InetAddress>()
        broadcastAddress(network)?.let { targets += it }
        targets += InetAddress.getByName("255.255.255.255")
        prefs.getString("last_ip", null)?.let { ip -> runCatching { InetAddress.getByName(ip) }.getOrNull()?.let { targets += it } }
        val ask = "COUCHTV?".toByteArray(Charsets.US_ASCII)
        DatagramSocket().use { udp ->
            network.bindSocket(udp)
            udp.broadcast = true
            udp.soTimeout = 300
            val buffer = ByteArray(512)
            repeat(2) {
                for (target in targets) runCatching { udp.send(DatagramPacket(ask, ask.size, target, DISCOVERY_PORT)) }
                val until = SystemClock.elapsedRealtime() + 900
                while (SystemClock.elapsedRealtime() < until) {
                    val packet = DatagramPacket(buffer, buffer.size)
                    try {
                        udp.receive(packet)
                    } catch (e: SocketTimeoutException) {
                        continue
                    }
                    // "COUCHTV <version> <port> <name>"
                    val parts = String(packet.data, 0, packet.length, Charsets.UTF_8).trim().split(" ", limit = 4)
                    if (parts.size < 3 || parts[0] != "COUCHTV") continue
                    val port = parts[2].toIntOrNull() ?: continue
                    return Tv(packet.address, port, parts.getOrElse(3) { "TV" })
                }
            }
        }
        return null
    }

    /** e.g. 192.168.1.255 for a phone at 192.168.1.23/24: some phones don't send to 255.255.255.255. */
    private fun broadcastAddress(network: Network): InetAddress? {
        val link = connectivity.getLinkProperties(network) ?: return null
        val v4 = link.linkAddresses.firstOrNull { it.address is Inet4Address } ?: return null
        val ip = v4.address.address
        val value = ((ip[0].toInt() and 0xFF) shl 24) or ((ip[1].toInt() and 0xFF) shl 16) or ((ip[2].toInt() and 0xFF) shl 8) or (ip[3].toInt() and 0xFF)
        val mask = if (v4.prefixLength == 0) 0 else -1 shl (32 - v4.prefixLength)
        val b = value or mask.inv()
        return InetAddress.getByAddress(byteArrayOf((b ushr 24).toByte(), (b ushr 16).toByte(), (b ushr 8).toByte(), b.toByte()))
    }

    /**
     * Connected: prove who's who, say hello, ping every second, and read the answers until the TV goes quiet for
     * 3 seconds. A TV that doesn't know this phone is left for the person to pair with ([unpairedTv]).
     */
    private fun session(network: Network, tv: Tv): Outcome {
        val s = network.socketFactory.createSocket()
        socket = s
        s.connect(InetSocketAddress(tv.address, tv.port), 2000)
        s.tcpNoDelay = true
        s.soTimeout = 3000
        val lines = LineIo(s.getInputStream(), s.getOutputStream())
        val hs = try {
            Handshake.start(lines, pairings.phoneId, BuildConfig.VERSION_NAME)
        } catch (e: SocketTimeoutException) {
            null
        }
        if (hs == null) {
            // Something's there, but it said nothing sensible back: a CouchTV from before pairing ignores HELLO2.
            setTooOld(true)
            return Outcome.TOO_OLD
        }
        setTooOld(false)
        val key = pairings.key(hs.tvId)
        if (!hs.known || key == null) {
            if (!hs.known && key != null) pairings.forget(hs.tvId)   // the TV forgot this phone: pair again
            pairAddress = tv.address
            setUnpaired(hs.tvName)
            return Outcome.UNPAIRED
        }
        val keys = hs.auth(lines, key) ?: throw IOException("the TV didn't prove it's the one this phone paired with")
        setUnpaired(null)
        val current = Link(s, lines, Channel(keys.phoneEnc, keys.phoneMac, keys.tvEnc, keys.tvMac))
        link = current
        send("HELLO CouchTV-Remote ${BuildConfig.VERSION_NAME}")
        if (trackpadOpen) send("TRACKPAD ON")
        Thread {
            while (link === current) {
                send("PING")
                SystemClock.sleep(1000)
            }
        }.apply { isDaemon = true }.start()
        while (true) {
            val sealed = lines.read() ?: break   // a timeout throws instead, which ends the session too
            val line = current.channel.open(sealed) ?: throw IOException("a line that didn't come from the TV")
            lastHeard = SystemClock.elapsedRealtime()
            when {
                line.startsWith("WELCOME") -> {
                    welcomed = true
                    prefs.edit().putString("last_ip", tv.address.hostAddress).apply()
                    tvAddress = tv.address
                    setTv(line.removePrefix("WELCOME").trim().ifEmpty { hs.tvName })
                }
                line.startsWith("POINTER ") -> pointerLook = line.removePrefix("POINTER ").trim()
                line.startsWith("BOARD ") -> Secure.fromHex(line.removePrefix("BOARD ").trim())?.takeIf { it.size == 32 }?.let { onBoardKey?.invoke(it) }
                line.startsWith("INPUT ") -> typing(line.removePrefix("INPUT "))
                line.startsWith("SLEEPTIMER ") -> line.removePrefix("SLEEPTIMER ").trim().toIntOrNull()?.let { seconds ->
                    setSleepTimer(if (seconds > 0) SystemClock.elapsedRealtime() + seconds * 1000L else 0L, true)
                }
                line.startsWith("SUGGESTSETUP ") -> line.removePrefix("SUGGESTSETUP ").let { setup -> main.post { onSuggestSetup?.invoke(setup) } }
                line.startsWith("SUGGESTIONS ") -> {
                    // A new list: that many SUGGEST lines follow.
                    expected = line.removePrefix("SUGGESTIONS ").trim().toIntOrNull() ?: 0
                    incoming = mutableListOf()
                    if (expected <= 0) setSuggestions(emptyList())
                }
                line.startsWith("SUGGEST ") -> incoming?.let { arriving ->
                    arriving.add(line.removePrefix("SUGGEST "))
                    if (arriving.size >= expected) setSuggestions(arriving)
                }
            }
        }
        return Outcome.CONNECTED
    }

    /** "TEXT|PASSWORD <name> <value>" (base64), or "OFF". */
    private fun typing(what: String) {
        val p = what.split(" ")
        fun decoded(i: Int) = p.getOrNull(i)?.let { runCatching { String(Base64.decode(it, Base64.DEFAULT), Charsets.UTF_8) }.getOrNull() } ?: ""
        val field = if (p[0] == "TEXT" || p[0] == "PASSWORD") TypingField(p[0] == "PASSWORD", decoded(1), decoded(2)) else null
        main.post { onTyping?.invoke(field) }
    }

    /** Plays a suggestion on the TV: one from its own list, or one this phone fetched. */
    fun play(s: Suggestion) = send(s.playLine())

    /** The TV goes to sleep this many minutes from now (0: the timer's off). */
    fun setSleepTimer(minutes: Int) = send("SLEEPTIMER $minutes")

    private fun setSleepTimer(endsAt: Long, known: Boolean) {
        if (endsAt == sleepEndsAt && known == sleepTimerKnown) return
        sleepEndsAt = endsAt
        sleepTimerKnown = known
        main.post { onSleepTimer?.invoke() }
    }

    private fun setSuggestions(lines: List<String>) {
        incoming = null
        val list = lines.mapNotNull { Suggestion.parse(it) }
        main.post { onSuggestions?.invoke(list) }
    }

    private fun send(line: String) {
        out.execute {
            val current = link ?: return@execute
            try {
                current.io.write(current.channel.seal(line))
            } catch (e: Exception) {
                close()
            }
        }
    }

    private fun close() {
        val s = socket
        socket = null
        if (link != null) main.post { onTyping?.invoke(null) }   // no TV to type into
        link = null
        runCatching { s?.close() }
    }

    private fun setTv(name: String?) {
        if (name == null) {
            tvAddress = null
            setSleepTimer(0L, false)   // asleep, or out of reach: it says again when it's back
        }
        if (tvName == name) return
        tvName = name
        main.post { onChange?.invoke() }
    }

    private fun setUnpaired(name: String?) {
        if (unpairedTv == name) return
        unpairedTv = name
        main.post { onChange?.invoke() }
    }

    private fun setTooOld(old: Boolean) {
        if (tvTooOld == old) return
        tvTooOld = old
        main.post { onChange?.invoke() }
    }
}
