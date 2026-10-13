package com.couchtv.remote

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.Log
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.SocketTimeoutException

/**
 * The receiver box's own Wi-Fi: an ESP32 (CouchLink) wired to the IR receiver, which presses the PC's and the TV's
 * power buttons with its relays. It's on even while the PC is asleep or off, so Power works from any phone, IR
 * blaster or not, and it knows whether the PC and the TV are on. "Receiver box Wi-Fi" in ir-receiver/PROTOCOL.md:
 * UDP port 47710; "COUCHBOARD?" (broadcast) finds it.
 *
 * It only does what a paired phone asks: requests carry a tag made with the box's key (which the TV gives paired
 * phones, and the box over USB) and the box's current challenge, so they can't be made up or replayed. Its answers
 * are tagged too, and bring the next challenge. "NONCE" asks for one.
 */
class BoardLink(context: Context) {
    companion object {
        const val PORT = 47710
        private const val TAG = "CouchTV"
        private const val ASK_EVERY_MS = 2500L
        private const val GONE_AFTER_MS = 7000L
    }

    private val connectivity = context.applicationContext.getSystemService(ConnectivityManager::class.java)
    private val prefs = context.getSharedPreferences("board", Context.MODE_PRIVATE)
    private val main = Handler(Looper.getMainLooper())
    @Volatile private var running = false
    @Volatile private var socket: DatagramSocket? = null
    @Volatile private var address: InetAddress? = null
    @Volatile private var lastHeard = 0L
    @Volatile private var acked = 0
    @Volatile private var key: ByteArray? = prefs.getString("key", null)?.let { Secure.fromHex(it) }?.takeIf { it.size == 32 }
    @Volatile private var challenge: String? = null
    private val asked = ArrayDeque<String>()   // the challenges recent requests used, to check answers with
    private var nextId = (System.currentTimeMillis() % 1_000_000).toInt() + 1

    /** Called on the main thread when the board appears, goes, or the PC or TV changes. */
    var onChange: (() -> Unit)? = null

    /** The PC and the TV as the board last said; null when it doesn't know (or isn't answering). */
    @Volatile var pcOn: Boolean? = null
        private set
    @Volatile var tvOn: Boolean? = null
        private set

    /** The board answered lately, to this phone's key. */
    val connected: Boolean get() = SystemClock.elapsedRealtime() - lastHeard < GONE_AFTER_MS

    /** The box's key, from the TV (each time this phone connects to it). */
    fun setKey(newKey: ByteArray) {
        if (Secure.same(key, newKey)) return
        key = newKey
        prefs.edit().putString("key", Secure.hex(newKey)).apply()
    }

    fun start() {
        if (running) return
        running = true
        Thread({ loop() }, "CouchTV board").apply { isDaemon = true }.start()
    }

    fun stop() {
        running = false
        socket?.close()
    }

    /**
     * Presses Power through the board: sent up to five times until it says OK (the same id, so it acts once).
     * [done] gets whether it did, on the main thread.
     */
    fun power(done: (Boolean) -> Unit) {
        val target = address
        val s = socket
        val k = key
        if (target == null || s == null || k == null) {
            done(false)
            return
        }
        val id = nextId++
        Thread {
            var ok = false
            for (attempt in 0 until 5) {
                val c = challenge
                if (c == null) runCatching { send(s, "NONCE", target) }
                else runCatching { send(s, "POWER $id $c ${Secure.tag(k, "POWER $id $c")}", target, c) }
                val until = SystemClock.elapsedRealtime() + 300
                while (SystemClock.elapsedRealtime() < until && acked != id) SystemClock.sleep(15)
                if (acked == id) {
                    ok = true
                    break
                }
            }
            main.post { done(ok) }
        }.start()
    }

    private fun loop() {
        while (running) {
            try {
                val s = DatagramSocket()
                wifiNetwork()?.bindSocket(s)
                s.broadcast = true
                s.soTimeout = 300
                socket = s
                val buffer = ByteArray(512)
                var lastAsk = 0L
                while (running) {
                    val now = SystemClock.elapsedRealtime()
                    if (now - lastAsk >= ASK_EVERY_MS) {
                        lastAsk = now
                        ask(s)
                    }
                    if (!connected && (pcOn != null || tvOn != null)) setState(null, null)
                    val packet = DatagramPacket(buffer, buffer.size)
                    try {
                        s.receive(packet)
                    } catch (e: SocketTimeoutException) {
                        continue
                    }
                    handle(s, String(packet.data, 0, packet.length, Charsets.UTF_8).trim(), packet.address)
                }
            } catch (e: Exception) {
                if (running) {
                    Log.i(TAG, "Receiver box: ${e.message}")
                    SystemClock.sleep(2000)
                }
            } finally {
                socket?.close()
                socket = null
            }
        }
    }

    /** Asks the board how things are; until it's found (or after it went quiet), the whole network as well. */
    private fun ask(s: DatagramSocket) {
        val known = address ?: prefs.getString("ip", null)?.let { runCatching { InetAddress.getByName(it) }.getOrNull() }
        if (known != null) runCatching { askState(s, known) }
        if (connected) return
        val network = wifiNetwork()
        val targets = ArrayList<InetAddress>()
        network?.let { broadcastAddress(it) }?.let { targets += it }
        targets += InetAddress.getByName("255.255.255.255")
        for (target in targets) runCatching { send(s, "COUCHBOARD?", target) }
    }

    private fun askState(s: DatagramSocket, target: InetAddress) {
        val k = key ?: return   // no key yet: the TV gives it once this phone is paired and connects
        val c = challenge
        if (c == null) send(s, "NONCE", target) else send(s, "STATE $c ${Secure.tag(k, "STATE $c")}", target, c)
    }

    private fun handle(s: DatagramSocket, message: String, from: InetAddress) {
        when {
            message.startsWith("COUCHBOARD ") -> found(from)
            message.startsWith("NONCE ") -> {
                // A fresh challenge: the one this phone used had changed (or it had none). Ask again with it.
                found(from)
                val fresh = message.removePrefix("NONCE ").trim()
                if (fresh.length == 32 && fresh != challenge) {
                    challenge = fresh
                    runCatching { askState(s, from) }
                }
            }
            message == "NOKEY" -> found(from)   // the box hasn't had its key from the TV yet
            else -> answer(message, from)
        }
    }

    /** "<what> <next challenge> <tag>": tagged with the challenge this phone asked with, so it's the real box, now. */
    private fun answer(message: String, from: InetAddress) {
        val k = key ?: return
        val parts = message.split(" ")
        if (parts.size < 3) return
        val tag = parts[parts.size - 1]
        val next = parts[parts.size - 2]
        val body = parts.subList(0, parts.size - 2).joinToString(" ")
        val challenges = synchronized(asked) { asked.toList() }
        if (challenges.none { Secure.tag(k, "R $it $body $next") == tag }) return
        found(from)
        challenge = next
        heard()
        when {
            body.startsWith("STATE") -> if (body == "STATE ?") setState(null, null) else setState(body.contains("PC ON"), body.contains("TV ON"))
            body.startsWith("OK ") -> body.removePrefix("OK ").trim().toIntOrNull()?.let { acked = it }
        }
    }

    private fun found(from: InetAddress) {
        if (address != from) {
            address = from
            prefs.edit().putString("ip", from.hostAddress).apply()
        }
    }

    private fun heard() {
        val wasConnected = connected
        lastHeard = SystemClock.elapsedRealtime()
        if (!wasConnected) main.post { onChange?.invoke() }
    }

    private fun setState(pc: Boolean?, tv: Boolean?) {
        if (pc == pcOn && tv == tvOn) return
        pcOn = pc
        tvOn = tv
        main.post { onChange?.invoke() }
    }

    /** Sends one line; [challenge] is the one it was tagged with, to check the answer against. */
    private fun send(s: DatagramSocket, text: String, target: InetAddress, challenge: String? = null) {
        if (challenge != null) synchronized(asked) {
            asked.remove(challenge)
            asked.addLast(challenge)
            while (asked.size > 4) asked.removeFirst()
        }
        val bytes = text.toByteArray(Charsets.US_ASCII)
        s.send(DatagramPacket(bytes, bytes.size, target, PORT))
    }

    /** The Wi-Fi network (not mobile data). */
    @Suppress("DEPRECATION")
    private fun wifiNetwork(): Network? = connectivity.allNetworks.firstOrNull {
        val caps = connectivity.getNetworkCapabilities(it)
        caps != null && (caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) || caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET))
    }

    private fun broadcastAddress(network: Network): InetAddress? {
        val link = connectivity.getLinkProperties(network) ?: return null
        val v4 = link.linkAddresses.firstOrNull { it.address is Inet4Address } ?: return null
        val ip = v4.address.address
        val value = ((ip[0].toInt() and 0xFF) shl 24) or ((ip[1].toInt() and 0xFF) shl 16) or ((ip[2].toInt() and 0xFF) shl 8) or (ip[3].toInt() and 0xFF)
        val mask = if (v4.prefixLength == 0) 0 else -1 shl (32 - v4.prefixLength)
        val b = value or mask.inv()
        return InetAddress.getByAddress(byteArrayOf((b ushr 24).toByte(), (b ushr 16).toByte(), (b ushr 8).toByte(), b.toByte()))
    }
}
