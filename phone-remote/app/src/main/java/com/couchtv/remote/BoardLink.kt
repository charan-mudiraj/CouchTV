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
 * UDP port 47710; "COUCHBOARD?" (broadcast) finds it, "STATE" asks how things are, "POWER <id>" presses Power.
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
    private var nextId = (System.currentTimeMillis() % 1_000_000).toInt() + 1

    /** Called on the main thread when the board appears, goes, or the PC or TV changes. */
    var onChange: (() -> Unit)? = null

    /** The PC and the TV as the board last said; null when it doesn't know (or isn't answering). */
    @Volatile var pcOn: Boolean? = null
        private set
    @Volatile var tvOn: Boolean? = null
        private set

    /** The board answered lately. */
    val connected: Boolean get() = SystemClock.elapsedRealtime() - lastHeard < GONE_AFTER_MS

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
        if (target == null || s == null) {
            done(false)
            return
        }
        val id = nextId++
        Thread {
            var ok = false
            for (attempt in 0 until 5) {
                runCatching { send(s, "POWER $id", target) }
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
                val buffer = ByteArray(256)
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
                    handle(String(packet.data, 0, packet.length, Charsets.UTF_8).trim(), packet.address)
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
        if (known != null) runCatching { send(s, "STATE", known) }
        if (connected) return
        val network = wifiNetwork()
        val targets = ArrayList<InetAddress>()
        network?.let { broadcastAddress(it) }?.let { targets += it }
        targets += InetAddress.getByName("255.255.255.255")
        for (target in targets) runCatching { send(s, "COUCHBOARD?", target) }
    }

    private fun handle(message: String, from: InetAddress) {
        when {
            message.startsWith("COUCHBOARD ") -> heard(from)
            message.startsWith("STATE") -> {
                heard(from)
                if (message == "STATE ?") setState(null, null)
                else setState(message.contains("PC ON"), message.contains("TV ON"))
            }
            message.startsWith("OK ") -> {
                heard(from)
                message.removePrefix("OK ").trim().toIntOrNull()?.let { acked = it }
            }
        }
    }

    private fun heard(from: InetAddress) {
        val wasConnected = connected
        lastHeard = SystemClock.elapsedRealtime()
        if (address != from) {
            address = from
            prefs.edit().putString("ip", from.hostAddress).apply()
        }
        if (!wasConnected) main.post { onChange?.invoke() }
    }

    private fun setState(pc: Boolean?, tv: Boolean?) {
        if (pc == pcOn && tv == tvOn) return
        pcOn = pc
        tvOn = tv
        main.post { onChange?.invoke() }
    }

    private fun send(s: DatagramSocket, text: String, target: InetAddress) {
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
