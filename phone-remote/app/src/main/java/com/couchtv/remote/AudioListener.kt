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
import java.util.concurrent.LinkedBlockingDeque
import java.util.concurrent.TimeUnit

/**
 * Plays the TV's sound on this phone ("Sound on phones" in ir-receiver/PROTOCOL.md). CouchTV sends uncompressed
 * 16-bit stereo in 3.75 ms chunks while this asks for it ("LISTEN 2" every second); each packet also carries the
 * chunk before it, so a packet lost on the Wi-Fi is repaired from the next one.
 *
 * Two threads: one receives packets into a queue, the other writes them to the speaker or headphones and waits
 * whenever the phone's audio system is full, so the phone sets the pace. The reserve (cushion) that rides out Wi-Fi
 * hiccups is kept in that queue, where it can be counted exactly: the audio system's own position reports are
 * unreliable on Bluetooth, which takes sound in big irregular gulps. Playing starts once the queue holds the
 * cushion: 60 ms for the speaker and wired earphones, 160 ms for Bluetooth. If the queue ever runs empty, the
 * cushion is rebuilt 20 ms bigger (up to 300 ms), and after each minute without that, it shrinks by 20 ms. The
 * TV's and the phone's clocks differ very slightly, so when the queue drifts above or below the cushion, packets
 * are played 0.5% faster or slower until it's back: inaudible. Bluetooth headphones add their own delay on top.
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
    private val queue = LinkedBlockingDeque<Chunk>()
    @Volatile private var running = false
    @Volatile private var socket: DatagramSocket? = null

    /** The current cushion in ms: the delay this app adds before the speaker or headphones. */
    @Volatile var cushionMs = SPEAKER_MS
        private set

    // Counters for the notification (see stats()).
    @Volatile private var lost = 0
    @Volatile private var repaired = 0
    @Volatile private var gaps = 0
    @Volatile private var adjusted = 0

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
            val listen = "LISTEN 2".toByteArray(Charsets.US_ASCII)   // 2: each packet also carries the previous chunk
            val buffer = ByteArray(4096)
            var lastSequence = -1L
            var lastAsk = 0L
            var lastChunk: Chunk? = null
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
                    buffer[2] != 'A'.code.toByte() || (buffer[3] != '1'.code.toByte() && buffer[3] != '2'.code.toByte())) continue
                // Old or repeated packets are dropped; a big jump back means CouchTV restarted its sound.
                val sequence = uint32(buffer, 4)
                if (sequence <= lastSequence && lastSequence - sequence < 1000) continue
                val rate = uint32(buffer, 8).toInt()
                val withPrevious = buffer[3] == '2'.code.toByte() && (buffer[13].toInt() and 1) != 0
                val size = if (withPrevious) (packet.length - HEADER) / 2 else packet.length - HEADER
                val missing = if (lastSequence >= 0 && sequence - lastSequence < 1000) (sequence - lastSequence - 1).toInt() else 0
                if (missing > 0) {
                    // Packets lost on the Wi-Fi. The one just before this travels inside it too, so it's repaired;
                    // anything lost before that is filled by repeating the last chunk, which hides a tiny gap far
                    // better than skipping (up to 3; a longer outage is just skipped).
                    val unrecovered = if (withPrevious) missing - 1 else missing
                    for (i in 0 until minOf(unrecovered, 3)) lastChunk?.let { queue.offer(it) }
                    lost += unrecovered
                    if (withPrevious) {
                        queue.offer(Chunk(rate, buffer.copyOfRange(HEADER + size, HEADER + 2 * size)))
                        repaired++
                    }
                }
                lastSequence = sequence
                val chunk = Chunk(rate, buffer.copyOfRange(HEADER, HEADER + size))
                queue.offer(chunk)
                lastChunk = chunk
                while (queue.size > 250) queue.poll()   // about a second behind (the phone stalled): keep the newest
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
            var chunkMs = 3.75
            var filling = true      // building the reserve in the queue before playing on
            var depth = 0.0         // packets waiting in the queue, smoothed over about two seconds
            var bluetooth = false
            var lastCheck = 0L
            var lastGap = SystemClock.elapsedRealtime()
            while (running) {
                // A new stream (or the sound device changed on the TV): an audio track for its sample rate.
                if (track == null || queue.peekFirst()?.let { it.rate != rate } == true) {
                    val first = queue.pollFirst(200, TimeUnit.MILLISECONDS) ?: continue
                    queue.offerFirst(first)
                    track?.release()
                    rate = first.rate
                    chunkMs = first.samples.size / 4 * 1000.0 / rate
                    val created = makeTrack(rate)
                    created.play()
                    track = created
                    bluetooth = isBluetooth(created)
                    cushionMs = if (bluetooth) BLUETOOTH_MS else SPEAKER_MS
                    filling = true
                }
                val t = track ?: continue
                val target = cushionMs / chunkMs   // the reserve, in packets
                if (filling) {
                    if (queue.size < target) {
                        SystemClock.sleep(5)
                        continue
                    }
                    filling = false
                    depth = queue.size.toDouble()
                }
                val chunk = queue.poll(60, TimeUnit.MILLISECONDS)
                val now = SystemClock.elapsedRealtime()
                if (chunk == null) {
                    // The reserve ran out: a Wi-Fi hiccup longer than it covers. Build it again, 20 ms bigger.
                    gaps++
                    cushionMs = minOf(cushionMs + 20, MOST_MS)
                    lastGap = now
                    filling = true
                    continue
                }
                if (now - lastCheck >= 1000) {
                    lastCheck = now
                    val bt = isBluetooth(t)
                    val base = if (bt) BLUETOOTH_MS else SPEAKER_MS
                    if (bt != bluetooth) {
                        bluetooth = bt   // headphones connected or disconnected: that kind's cushion
                        cushionMs = maxOf(cushionMs, base).let { if (bt) it else base }
                        lastGap = now
                    } else if (cushionMs > base && now - lastGap > 60_000) {
                        cushionMs -= 20   // a minute without running out: give back some delay
                        lastGap = now
                    }
                }
                // The two clocks differ slightly: when the queue drifts above or below the reserve, play this
                // packet 0.5% faster or slower (by interpolation, inaudible) until it's back.
                depth = depth * 0.998 + queue.size * 0.002
                val margin = if (bluetooth) 8 else 4   // Bluetooth's gulps make the queue swing more
                val frames = chunk.samples.size / 4
                val samples = when {
                    depth > target + margin -> {
                        adjusted++
                        stretch(chunk.samples, frames - 1)
                    }
                    depth < target - margin -> {
                        adjusted++
                        stretch(chunk.samples, frames + 1)
                    }
                    else -> chunk.samples
                }
                t.write(samples, 0, samples.size)   // waits while the phone's audio system is full
            }
        } catch (e: Exception) {
            if (running) Log.w(TAG, "TV sound (playing)", e)
        } finally {
            track?.release()
        }
    }

    /** 16-bit stereo resampled to [outFrames] frames by straight-line interpolation; its first and last samples stay put. */
    private fun stretch(input: ByteArray, outFrames: Int): ByteArray {
        val inFrames = input.size / 4
        if (inFrames < 2 || outFrames < 2) return input
        val out = ByteArray(outFrames * 4)
        for (i in 0 until outFrames) {
            val position = i.toDouble() * (inFrames - 1) / (outFrames - 1)
            val a = position.toInt()
            val b = minOf(a + 1, inFrames - 1)
            val f = position - a
            for (c in 0..1) {
                val sa = sample(input, a * 4 + c * 2)
                val sb = sample(input, b * 4 + c * 2)
                val v = (sa + (sb - sa) * f).toInt()
                out[i * 4 + c * 2] = v.toByte()
                out[i * 4 + c * 2 + 1] = (v shr 8).toByte()
            }
        }
        return out
    }

    private fun sample(b: ByteArray, at: Int): Int = (b[at].toInt() and 0xFF) or (b[at + 1].toInt() shl 8)

    /** For the notification: cushion, packets lost on the Wi-Fi, times the sound ran out, smooth clock corrections. */
    fun stats(): IntArray = intArrayOf(cushionMs, lost, repaired, gaps, adjusted)

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
        // A small buffer here (the reserve lives in the queue), in the normal mode that Bluetooth uses anyway.
        return AudioTrack.Builder()
            .setAudioAttributes(attributes)
            .setAudioFormat(format)
            .setTransferMode(AudioTrack.MODE_STREAM)
            .setBufferSizeInBytes(maxOf(min * 2, rate / 25 * 4))   // at least 40 ms
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
