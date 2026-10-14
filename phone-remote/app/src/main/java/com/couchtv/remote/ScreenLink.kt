package com.couchtv.remote

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.SystemClock
import android.util.Log
import java.io.IOException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket

/**
 * The TV's screen for the mirror ("Screen mirror" in ir-receiver/PROTOCOL.md; CouchTV's side is ScreenMirror): a
 * paired connection of its own that asks "MIRROR <width> <kbps>", then reads the TV's pictures, each message
 * encrypted (FrameReader): the size, JPEG strips of what changed, the pointer, a heartbeat each second when nothing
 * did. Connects again if it drops, until stopped.
 */
class ScreenLink(context: Context, private val view: MirrorView) {
    private companion object {
        const val TAG = "CouchTV"
        const val WIDTH = 1280   // the picture's width: sharp enough for the TV's text on a phone
    }

    private val app = context.applicationContext
    private val pairings = Pairings(app)
    private val connectivity = app.getSystemService(ConnectivityManager::class.java)
    @Volatile private var running = false
    @Volatile private var generation = 0
    @Volatile private var socket: Socket? = null

    /** [host] gives the TV's address for each try; [kbps] the most it may send (less while this phone plays the TV's sound). */
    fun start(host: () -> String?, kbps: () -> Int) {
        if (running) return
        running = true
        val gen = ++generation
        Thread({ run(gen, host, kbps) }, "CouchTV screen").start()
    }

    fun stop() {
        running = false
        generation++
        runCatching { socket?.close() }
    }

    private fun run(gen: Int, host: () -> String?, kbps: () -> Int) {
        var tries = 0
        while (running && gen == generation) {
            try {
                view.status(app.getString(if (tries++ == 0) R.string.mirror_connecting else R.string.mirror_reconnecting))
                val address = host() ?: throw IOException("the TV isn't connected")
                val s = Socket()
                socket = s
                wifiNetwork()?.bindSocket(s)
                s.receiveBufferSize = 512 * 1024
                s.connect(InetSocketAddress(InetAddress.getByName(address), WifiLink.CONTROL_PORT), 3000)
                s.tcpNoDelay = true
                s.soTimeout = 5000   // the TV sends at least a heartbeat every second
                val lines = LineIo(s.getInputStream(), s.getOutputStream())
                val hs = Handshake.start(lines, pairings.phoneId, BuildConfig.VERSION_NAME) ?: throw IOException("not a CouchTV that pairs")
                val key = pairings.key(hs.tvId)?.takeIf { hs.known } ?: throw IOException("this phone isn't paired with the TV")
                val keys = hs.auth(lines, key) ?: throw IOException("not the TV this phone paired with")
                lines.write(Channel(keys.phoneEnc, keys.phoneMac, keys.tvEnc, keys.tvMac).seal("MIRROR $WIDTH ${kbps()}"))
                val frames = FrameReader(lines.stream, keys.mirrorEnc, keys.mirrorMac)
                while (running && gen == generation) {
                    val m = frames.next() ?: break
                    when (m[0].toInt()) {
                        1 -> view.begin(u16(m, 1), u16(m, 3))
                        2 -> view.tile(u16(m, 1), u16(m, 3), m, 9, m.size - 9)
                        3 -> {
                            tries = 0
                            view.commit()
                        }
                        4 -> view.cursor(u16(m, 1), u16(m, 3), m[5].toInt() != 0)
                    }
                }
            } catch (e: Exception) {
                if (running) Log.i(TAG, "Screen mirror: ${e.message}")
            } finally {
                runCatching { socket?.close() }
                socket = null
            }
            if (running && gen == generation) SystemClock.sleep(1000)
        }
    }

    private fun u16(m: ByteArray, at: Int) = ((m[at].toInt() and 0xFF) shl 8) or (m[at + 1].toInt() and 0xFF)

    @Suppress("DEPRECATION")
    private fun wifiNetwork(): Network? = connectivity.allNetworks.firstOrNull {
        val caps = connectivity.getNetworkCapabilities(it)
        caps != null && (caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) || caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET))
    }
}
