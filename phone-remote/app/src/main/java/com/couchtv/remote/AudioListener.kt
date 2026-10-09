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
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

/**
 * Plays the TV's sound on this phone ("Sound on phones" in ir-receiver/PROTOCOL.md). CouchTV sends uncompressed
 * 16-bit stereo in 5 ms packets while this asks for it ("LISTEN" every second).
 *
 * Two threads: one receives packets into a queue, the other writes them to the speaker or headphones and waits
 * whenever the phone's own audio system is full, so the phone sets the pace. The cushion (how much sound is ready
 * in the audio system) covers Wi-Fi hiccups: 60 ms for the speaker and wired earphones, 160 ms for Bluetooth, which
 * takes sound in big irregular gulps and shares the phone's radio with the Wi-Fi. If sound still runs out, the
 * cushion grows (up to 300 ms). The TV's and the phone's clocks differ very slightly, so if packets slowly pile up
 * in the queue, one is dropped now and then. Bluetooth headphones add their own delay, which no app can remove.
 */
class AudioListener(context: Context, private val tvHost: String) {
    companion object {
        const val PORT = 47702
        private const val HEADER = 16
        private const val TAG = "CouchTV"
        private const val SPEAKER_MS = 60
        private const val BLUETOOTH_MS = 160
        private const val MOST_MS = 300
    }

    private class Chunk(val rate: Int, val samples: ByteArray)

    private val connectivity = context.applicationContext.getSystemService(ConnectivityManager::class.java)
    private val queue = LinkedBlockingQueue<Chunk>()
    @Volatile private var running = false
    @Volatile private var socket: DatagramSocket? = null

    /** The current cushion in ms: the delay this app adds before the speaker or headphones. */
    @Volatile var cushionMs = SPEAKER_MS
        private set

    fun start() {
        if (running) return
        running = true
        Thread({ receive() }, "CouchTV sound in").start()
        Thread({ play() }, "CouchTV sound out").start()
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

    // ---------------------------------------------------------------- receiving

    private fun receive() {
        Process.setThreadPriority(Process.THREAD_PRIORITY_AUDIO)
        try {
            val tv = InetSocketAddress(InetAddress.getByName(tvHost), PORT)
            val s = DatagramSocket()
            wifiNetwork()?.bindSocket(s)
            s.soTimeout = 200
            s.receiveBufferSize = 256 * 1024
            socket = s
            val listen = "LISTEN".toByteArray(Charsets.US_ASCII)
            val buffer = ByteArray(4096)
            var lastSequence = -1L
            var lastAsk = 0L
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
                queue.offer(Chunk(uint32(buffer, 8).toInt(), buffer.copyOfRange(HEADER, packet.length)))
                while (queue.size > 200) queue.poll()   // over a second behind (the phone stalled): keep the newest
            }
        } catch (e: Exception) {
            if (running) Log.w(TAG, "TV sound (receiving)", e)
        } finally {
            socket?.close()
        }
    }

    // ---------------------------------------------------------------- playing

    private fun play() {
        Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
        var track: AudioTrack? = null
        try {
            var rate = 0
            var started = 0L
            var lastCheck = 0L
            var underruns = 0
            var bluetooth = false
            var backlog = 0.0   // packets waiting here, smoothed over a few seconds
            while (running) {
                val chunk = queue.poll(200, TimeUnit.MILLISECONDS) ?: continue
                if (track == null || chunk.rate != rate) {
                    track?.release()
                    rate = chunk.rate
                    val created = makeTrack(rate)
                    bluetooth = isBluetooth(created)
                    setCushion(created, if (bluetooth) BLUETOOTH_MS else SPEAKER_MS, rate)
                    created.play()
                    track = created
                    started = SystemClock.elapsedRealtime()
                    underruns = 0
                    backlog = 0.0
                }
                val t = track ?: continue
                val now = SystemClock.elapsedRealtime()
                if (now - lastCheck >= 1000) {
                    lastCheck = now
                    // Headphones connected or disconnected meanwhile: start again from that kind's cushion.
                    val bt = isBluetooth(t)
                    if (bt != bluetooth) {
                        bluetooth = bt
                        setCushion(t, if (bt) BLUETOOTH_MS else SPEAKER_MS, rate)
                    }
                    // Ran dry since the last check (not counting the first moments): keep 20 ms more ready.
                    if (now - started > 2000 && t.underrunCount > underruns) setCushion(t, cushionMs + 20, rate)
                    underruns = t.underrunCount
                }
                // Packets slowly piling up here means the TV's clock runs a hair fast: drop one now and then.
                backlog = backlog * 0.995 + queue.size * 0.005
                if (backlog > (if (bluetooth) 16 else 8)) {
                    backlog -= 1.0
                    continue
                }
                t.write(chunk.samples, 0, chunk.samples.size)   // waits while the phone's audio system is full
            }
        } catch (e: Exception) {
            if (running) Log.w(TAG, "TV sound (playing)", e)
        } finally {
            track?.release()
        }
    }

    private fun setCushion(track: AudioTrack, ms: Int, rate: Int) {
        cushionMs = ms.coerceIn(SPEAKER_MS, MOST_MS)
        track.setBufferSizeInFrames(cushionMs * rate / 1000)
    }

    /** Bluetooth headphones and speakers (classic, LE Audio and hearing aids). */
    private fun isBluetooth(track: AudioTrack): Boolean {
        val type = track.routedDevice?.type ?: return false
        // TYPE_BLUETOOTH_SCO 7, _A2DP 8, TYPE_HEARING_AID 23, TYPE_BLE_HEADSET 26, _SPEAKER 27, _BROADCAST 30
        return type == 7 || type == 8 || type == 23 || type == 26 || type == 27 || type == 30
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
            .setBufferSizeInBytes(maxOf(min, MOST_MS * rate / 1000 * 4))   // room for the largest cushion
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
