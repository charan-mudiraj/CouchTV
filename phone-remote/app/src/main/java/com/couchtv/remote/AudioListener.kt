package com.couchtv.remote

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Process
import android.os.SystemClock
import android.util.Log
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketTimeoutException

/**
 * Plays the TV's sound on this phone ("Sound on phones" in ir-receiver/PROTOCOL.md). CouchTV sends uncompressed
 * 16-bit stereo in 5 ms packets while this asks for it ("LISTEN" every second). To keep the delay low, about 40 ms
 * of sound is kept ready: more after hiccups (up to 150 ms), and when it falls behind it skips ahead instead of
 * drifting later. Bluetooth headphones add their own delay on top, which no app can remove.
 */
class AudioListener(context: Context, private val tvHost: String) {
    companion object {
        const val PORT = 47702
        private const val HEADER = 16
        private const val TAG = "CouchTV"
    }

    private val connectivity = context.applicationContext.getSystemService(ConnectivityManager::class.java)
    @Volatile private var running = false
    @Volatile private var socket: DatagramSocket? = null

    /** How much sound is kept ready, in ms (it grows if the Wi-Fi hiccups). */
    @Volatile var bufferMs = 40
        private set

    fun start() {
        if (running) return
        running = true
        Thread({ run() }, "CouchTV sound").start()
    }

    fun stop() {
        running = false
        val s = socket ?: return
        Thread {
            runCatching {
                val stop = "STOP".toByteArray(Charsets.US_ASCII)
                s.send(DatagramPacket(stop, stop.size, InetSocketAddress(InetAddress.getByName(tvHost), PORT)))
            }
            s.close()
        }.start()
    }

    private fun run() {
        Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
        var track: AudioTrack? = null
        try {
            val tv = InetSocketAddress(InetAddress.getByName(tvHost), PORT)
            val s = DatagramSocket()
            wifiNetwork()?.bindSocket(s)
            s.soTimeout = 200
            s.receiveBufferSize = 256 * 1024
            socket = s
            val listen = "LISTEN".toByteArray(Charsets.US_ASCII)
            val buffer = ByteArray(4096)
            var rate = 0
            var written = 0L           // frames handed to the track
            var lastSequence = -1L
            var lastAsk = 0L
            var underruns = 0
            while (running) {
                val now = SystemClock.elapsedRealtime()
                if (now - lastAsk >= 1000) {
                    runCatching { s.send(DatagramPacket(listen, listen.size, tv)) }
                    lastAsk = now
                }
                val packet = DatagramPacket(buffer, buffer.size)
                try {
                    s.receive(packet)
                } catch (e: SocketTimeoutException) {
                    continue
                }
                if (packet.length <= HEADER || buffer[0] != 'C'.code.toByte() || buffer[1] != 'T'.code.toByte() ||
                    buffer[2] != 'A'.code.toByte() || buffer[3] != '1'.code.toByte()) continue
                // Old or repeated packets are dropped; a big jump back means CouchTV restarted its sound.
                val sequence = uint32(buffer, 4)
                if (sequence <= lastSequence && lastSequence - sequence < 1000) continue
                lastSequence = sequence

                val packetRate = uint32(buffer, 8).toInt()
                if (track == null || packetRate != rate) {
                    track?.release()
                    rate = packetRate
                    val created = makeTrack(rate)
                    created.play()
                    track = created
                    written = 0
                    underruns = 0
                }
                val t = track ?: continue
                val queuedMs = (written - (t.playbackHeadPosition.toLong() and 0xFFFFFFFFL)) * 1000 / rate
                if (t.underrunCount > underruns) {
                    underruns = t.underrunCount
                    bufferMs = minOf(bufferMs + 10, 150)   // the Wi-Fi hiccuped: keep a little more ready
                }
                if (queuedMs < 5) {
                    // Ran dry (start, or a gap): put the target amount of silence in front, so it doesn't stutter.
                    val silence = ByteArray((bufferMs * rate / 1000) * 4)
                    val n = t.write(silence, 0, silence.size, AudioTrack.WRITE_NON_BLOCKING)
                    if (n > 0) written += n / 4
                } else if (queuedMs > bufferMs + 40) {
                    continue   // fallen behind (clocks drift, or a burst arrived): skip 5 ms to catch up
                }
                val n = t.write(buffer, HEADER, packet.length - HEADER, AudioTrack.WRITE_NON_BLOCKING)
                if (n > 0) written += n / 4
            }
        } catch (e: Exception) {
            if (running) Log.w(TAG, "TV sound", e)
        } finally {
            track?.release()
            socket?.close()
        }
    }

    private fun makeTrack(rate: Int): AudioTrack {
        val format = AudioFormat.Builder()
            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
            .setSampleRate(rate)
            .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
            .build()
        val attributes = AudioAttributes.Builder()
            .setUsage(AudioAttributes.USAGE_MEDIA)
            .setContentType(AudioAttributes.CONTENT_TYPE_MOVIE)
            .build()
        val min = AudioTrack.getMinBufferSize(rate, AudioFormat.CHANNEL_OUT_STEREO, AudioFormat.ENCODING_PCM_16BIT)
        return AudioTrack.Builder()
            .setAudioAttributes(attributes)
            .setAudioFormat(format)
            .setTransferMode(AudioTrack.MODE_STREAM)
            .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
            .setBufferSizeInBytes(maxOf(min, rate * 4 / 5))   // room for 200 ms; only about 40 ms is kept filled
            .build()
    }

    private fun uint32(b: ByteArray, at: Int): Long =
        (b[at].toLong() and 0xFF) or ((b[at + 1].toLong() and 0xFF) shl 8) or
            ((b[at + 2].toLong() and 0xFF) shl 16) or ((b[at + 3].toLong() and 0xFF) shl 24)

    @Suppress("DEPRECATION")
    private fun wifiNetwork(): Network? = connectivity.allNetworks.firstOrNull {
        val caps = connectivity.getNetworkCapabilities(it)
        caps != null && (caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) || caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET))
    }
}
