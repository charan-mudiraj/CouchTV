package com.couchtv.remote

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.Base64
import android.util.Log
import java.io.BufferedReader
import java.io.InputStreamReader
import java.io.OutputStreamWriter
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.util.Locale
import java.util.concurrent.Executors

/**
 * The remote over Wi-Fi. Finds CouchTV on the phone's Wi-Fi by itself (a broadcast the TV answers) and keeps a
 * connection to it, so buttons work without pointing, and on phones without an IR blaster. If the TV goes away
 * (asleep, or off the network) it keeps looking, and Remote falls back to infrared meanwhile.
 * The format is "Wi-Fi" in ir-receiver/PROTOCOL.md in the CouchTV repo.
 */
class WifiLink(context: Context) {
    companion object {
        const val DISCOVERY_PORT = 47700
        private const val TAG = "CouchTV"
    }

    private class Tv(val address: InetAddress, val port: Int, val name: String)

    /** The TV's name while connected, else null. */
    @Volatile var tvName: String? = null
        private set

    /** Called on the main thread when the TV connects or goes away. */
    var onChange: (() -> Unit)? = null

    private val app = context.applicationContext
    private val connectivity = app.getSystemService(ConnectivityManager::class.java)
    private val main = Handler(Looper.getMainLooper())
    private val prefs = app.getSharedPreferences("wifi", Context.MODE_PRIVATE)
    private val io = Executors.newSingleThreadExecutor()    // finding, connecting, reading
    private val out = Executors.newSingleThreadExecutor()   // writing, in order
    @Volatile private var running = false
    @Volatile private var generation = 0
    @Volatile private var socket: Socket? = null
    @Volatile private var writer: OutputStreamWriter? = null
    @Volatile private var lastHeard = 0L

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

    /** A button, exactly like an infrared frame: N for a new press, R while it's held. */
    fun button(address: Int, command: Int, repeat: Boolean) {
        send(String.format(Locale.ROOT, "BTN %02X %02X %s", address and 0xFF, command and 0xFF, if (repeat) "R" else "N"))
    }

    /** Words for search. */
    fun text(words: String) {
        send("TEXT " + Base64.encodeToString(words.toByteArray(Charsets.UTF_8), Base64.NO_WRAP))
    }

    // ---------------------------------------------------------------- finding and keeping the TV

    private fun loop(gen: Int) {
        while (running && gen == generation) {
            var found = false
            try {
                val network = wifiNetwork()
                val tv = if (network != null) find(network) else null
                if (network != null && tv != null && running && gen == generation) {
                    found = true
                    session(network, tv)
                }
            } catch (e: Exception) {
                Log.i(TAG, "Wi-Fi remote: ${e.message}")
            }
            close()
            setTv(null)
            if (running && gen == generation) SystemClock.sleep(if (found) 500 else 2000)
        }
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
                    // "COUCHTV 1 <port> <name>"
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

    /** Connected: say hello, ping every second, and read the answers until the TV goes quiet for 3 seconds. */
    private fun session(network: Network, tv: Tv) {
        val s = network.socketFactory.createSocket()
        socket = s
        s.connect(InetSocketAddress(tv.address, tv.port), 2000)
        s.tcpNoDelay = true
        s.soTimeout = 3000
        writer = OutputStreamWriter(s.getOutputStream(), Charsets.UTF_8)
        val reader = BufferedReader(InputStreamReader(s.getInputStream(), Charsets.UTF_8))
        send("HELLO CouchTV-Remote ${BuildConfig.VERSION_NAME}")
        Thread {
            while (socket === s) {
                send("PING")
                SystemClock.sleep(1000)
            }
        }.apply { isDaemon = true }.start()
        while (true) {
            val line = reader.readLine() ?: break   // a timeout throws instead, which ends the session too
            lastHeard = SystemClock.elapsedRealtime()
            if (line.startsWith("WELCOME")) {
                prefs.edit().putString("last_ip", tv.address.hostAddress).apply()
                setTv(line.removePrefix("WELCOME").trim().ifEmpty { tv.name })
            }
        }
    }

    private fun send(line: String) {
        out.execute {
            val w = writer ?: return@execute
            try {
                w.write(line + "\n")
                w.flush()
            } catch (e: Exception) {
                close()
            }
        }
    }

    private fun close() {
        val s = socket
        socket = null
        writer = null
        runCatching { s?.close() }
    }

    private fun setTv(name: String?) {
        if (tvName == name) return
        tvName = name
        main.post { onChange?.invoke() }
    }
}
