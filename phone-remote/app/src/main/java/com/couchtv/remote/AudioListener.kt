package com.couchtv.remote

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioDeviceInfo
import android.media.AudioFormat
import android.media.AudioManager
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
import java.net.Socket
import java.net.SocketTimeoutException
import java.io.IOException
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong

/**
 * Plays the TV's sound on this phone ("Sound on phones" in ir-receiver/PROTOCOL.md). CouchTV sends uncompressed
 * 16-bit stereo in 3.75 ms chunks while this asks for it ("LISTEN 3" every second); each packet also carries the
 * chunk before it, so a packet lost on the Wi-Fi is repaired from the next one.
 *
 * Only for a phone paired with the TV: this keeps a connection of its own to the TV (the remote's stops with the
 * screen off, the sound doesn't), which gives it a session id and keys. Its requests carry a tag made with them,
 * and each packet comes encrypted and tagged for this phone alone (SoundKeys).
 *
 * Two threads: one receives packets into a queue, the other writes them to the speaker or headphones and waits
 * whenever the phone's own audio system is full, so the phone sets the pace. The cushion (how much sound is ready
 * in the audio system) covers Wi-Fi hiccups: 60 ms for the speaker and wired earphones, 160 ms for Bluetooth, which
 * takes sound in big irregular gulps and shares the phone's radio with the Wi-Fi. If sound still runs out, the
 * cushion grows (up to 300 ms), and after half a minute without running out it shrinks back 10 ms at a time. Sound
 * lost along with its repair copy is filled in (Concealer).
 *
 * Staying in step with the TV: the cushion is all the delay there should be, so sound waiting beyond it is sound
 * that has fallen behind: held up on the Wi-Fi and then arriving all at once, or the TV's clock running a hair fast.
 * Each half second, the fewest packets that were waiting at any moment in it is how far behind the phone is all the
 * time (bursts come and go; that doesn't). A little behind, the sound plays up to 1% faster until it's caught up
 * (Resampler; inaudible); more than 22 ms behind, the late sound is cut at once, cross-faded so there's no click, so
 * the delay never adds up. Running low, it plays a hair slower.
 *
 * The sound goes to wired earphones (or USB, or line out) when they're plugged in, else the phone's speaker: never
 * Bluetooth, even if Bluetooth headphones are connected (BluetoothGuard also keeps Bluetooth off). The Bluetooth
 * cushion stays for phones that ignore that.
 */
class AudioListener(context: Context, private val tvHost: String) {
    companion object {
        const val PORT = 47702
        private const val HEADER = 16
        private const val TAG = "CouchTV"
        private const val SPEAKER_MS = 60
        private const val BLUETOOTH_MS = 160
        private const val MOST_MS = 300
        private const val EASING = 7200.0   // frames owed / this = how much faster or slower (0.15 s to settle)
        private const val MOST_SPEED = 0.01    // 1%: under two frames per chunk, too little to hear
        private const val WINDOW_MS = 500L     // how long the fewest packets waiting is watched before acting on it
        private const val KEEP = 1             // packets that may always be waiting (one's taken as the next to play)
        private const val CUT = 6              // more than this many packets behind (22 ms): cut, rather than catch up
        private const val CALM_MS = 30_000L    // this long without running dry: the cushion shrinks 10 ms

        /** Where the sound goes, first one present wins. */
        private val OUTPUTS = intArrayOf(
            AudioDeviceInfo.TYPE_WIRED_HEADPHONES, AudioDeviceInfo.TYPE_WIRED_HEADSET,
            AudioDeviceInfo.TYPE_USB_HEADSET, AudioDeviceInfo.TYPE_USB_DEVICE, AudioDeviceInfo.TYPE_LINE_ANALOG,
            AudioDeviceInfo.TYPE_BUILTIN_SPEAKER,
        )
    }

    /** [samples] is null for a chunk lost on the Wi-Fi, to be made up ([frames] long). */
    private class Chunk(val rate: Int, val samples: ByteArray?, val frames: Int = (samples?.size ?: 0) / 4)

    private val pairings = Pairings(context)
    private val connectivity = context.applicationContext.getSystemService(ConnectivityManager::class.java)
    private val audio = context.applicationContext.getSystemService(AudioManager::class.java)
    private var routedTo: Int? = null   // the output the sound was last pointed at (playing thread only)
    private val queue = LinkedBlockingQueue<Chunk>()
    @Volatile private var running = false
    @Volatile private var socket: DatagramSocket? = null
    @Volatile private var control: Socket? = null
    @Volatile private var sound: SoundKeys? = null   // this connection's session id and keys, once the TV gave them
    private val requests = AtomicLong()               // each request's number goes up

    /** The current cushion in ms: the delay this app adds before the speaker or headphones. */
    @Volatile var cushionMs = SPEAKER_MS
        private set

    // Counters for the notification (see stats()).
    @Volatile private var lost = 0
    @Volatile private var repaired = 0
    @Volatile private var gaps = 0
    @Volatile private var adjusted = 0
    @Volatile private var cuts = 0

    fun start() {
        if (running) return
        running = true
        Thread({ connect() }, "CouchTV sound keys").start()
        Thread({ receive() }, "CouchTV sound in").start()
        Thread({ play() }, "CouchTV sound out").start()
    }

    fun stop() {
        running = false
        val keys = sound
        val c = control
        val s = socket ?: return
        Thread {
            runCatching {
                if (keys != null) {
                    val stop = keys.request("STOP", requests.incrementAndGet()).toByteArray(Charsets.US_ASCII)
                    s.send(DatagramPacket(stop, stop.size, InetSocketAddress(InetAddress.getByName(tvHost), PORT)))
                }
            }
            s.close()
            runCatching { c?.close() }
        }.start()
    }

    // ---------------------------------------------------------------- the keys

    /**
     * Keeps a paired connection to the TV for the sound's session id and keys ("SOUND" asks for the id), pinging
     * every second; connects again if it drops.
     */
    private fun connect() {
        while (running) {
            try {
                val s = Socket()
                control = s
                wifiNetwork()?.bindSocket(s)
                s.connect(InetSocketAddress(InetAddress.getByName(tvHost), WifiLink.CONTROL_PORT), 3000)
                s.tcpNoDelay = true
                s.soTimeout = 3000
                val lines = LineIo(s.getInputStream(), s.getOutputStream())
                val hs = Handshake.start(lines, pairings.phoneId, BuildConfig.VERSION_NAME) ?: throw IOException("not a CouchTV that pairs")
                val key = pairings.key(hs.tvId)?.takeIf { hs.known } ?: throw IOException("this phone isn't paired with the TV")
                val keys = hs.auth(lines, key) ?: throw IOException("not the TV this phone paired with")
                val channel = Channel(keys.phoneEnc, keys.phoneMac, keys.tvEnc, keys.tvMac)
                val say = { line: String -> synchronized(lines) { lines.write(channel.seal(line)) } }   // in order
                say("SOUND")
                Thread {
                    while (running && control === s) {
                        runCatching { say("PING") }
                        SystemClock.sleep(1000)
                    }
                }.apply { isDaemon = true }.start()
                while (running) {
                    val line = channel.open(lines.read() ?: break) ?: break
                    if (line.startsWith("AUDIO ")) sound = SoundKeys(line.removePrefix("AUDIO ").trim(), keys.audioEnc, keys.audioMac)
                }
            } catch (e: Exception) {
                if (running) Log.i(TAG, "TV sound connection: ${e.message}")
            } finally {
                sound = null
                runCatching { control?.close() }
                control = null
            }
            if (running) SystemClock.sleep(1000)
        }
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
            val buffer = ByteArray(4096)
            var lastSequence = -1L
            var lastAsk = 0L
            var askedWith: SoundKeys? = null
            while (running) {
                val now = SystemClock.elapsedRealtime()
                val keys = sound
                // Every second, and at once when the keys change (connected again).
                if (keys != null && (now - lastAsk >= 1000 || keys !== askedWith)) {
                    val listen = keys.request("LISTEN", requests.incrementAndGet()).toByteArray(Charsets.US_ASCII)
                    runCatching { s.send(DatagramPacket(listen, listen.size, tv)) }
                    lastAsk = now
                    askedWith = keys
                }
                val packet = DatagramPacket(buffer, buffer.size)
                try {
                    s.receive(packet)
                } catch (e: SocketTimeoutException) {
                    continue
                }
                if (keys == null || packet.length <= HEADER || buffer[0] != 'C'.code.toByte() || buffer[1] != 'T'.code.toByte() ||
                    buffer[2] != 'A'.code.toByte() || buffer[3] != '3'.code.toByte()) continue
                // Only this connection's: checked, then decrypted. Anything else (an old key's, a stranger's) is dropped.
                val body = keys.open(buffer, packet.length) ?: continue
                // Old or repeated packets are dropped; a big jump back means CouchTV restarted its sound.
                val sequence = uint32(body, 4)
                if (sequence <= lastSequence && lastSequence - sequence < 1000) continue
                val rate = uint32(body, 8).toInt()
                val withPrevious = (body[13].toInt() and 1) != 0
                val size = if (withPrevious) (body.size - HEADER) / 2 else body.size - HEADER
                val missing = if (lastSequence >= 0 && sequence - lastSequence < 1000) (sequence - lastSequence - 1).toInt() else 0
                if (missing > 0) {
                    // Packets lost on the Wi-Fi. The one just before this travels inside it too, so it's repaired;
                    // anything lost before that is made up when it's played (up to 3; a longer outage is skipped).
                    val unrecovered = if (withPrevious) missing - 1 else missing
                    for (i in 0 until minOf(unrecovered, 3)) queue.offer(Chunk(rate, null, size / 4))
                    lost += unrecovered
                    if (withPrevious) {
                        queue.offer(Chunk(rate, body.copyOfRange(HEADER + size, HEADER + 2 * size)))
                        repaired++
                    }
                }
                lastSequence = sequence
                queue.offer(Chunk(rate, body.copyOfRange(HEADER, HEADER + size)))
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
            var started = 0L
            var lastCheck = 0L
            var underruns = 0
            var bluetooth = false
            var fill = 0.0      // ms of sound ready in the audio system, smoothed over about a second
            var written = 0L    // frames handed to the audio system
            var owed = 0.0      // frames to drop (above 0) or add (below) by playing a hair faster or slower
            var fewest = Int.MAX_VALUE   // the fewest packets waiting at any moment in this window
            var windowStart = 0L
            var calmSince = 0L           // since the sound last ran dry, or the cushion last changed
            var fadeFrom: ByteArray? = null   // after a cut: the sound that would have come next, to cross-fade out of
            val concealer = Concealer()
            val resampler = Resampler()
            while (running) {
                val chunk = queue.poll(200, TimeUnit.MILLISECONDS) ?: continue
                if (track == null || chunk.rate != rate) {
                    track?.release()
                    rate = chunk.rate
                    val created = makeTrack(rate)
                    routedTo = null
                    route(created)
                    bluetooth = isBluetooth(created)
                    setCushion(created, if (bluetooth) BLUETOOTH_MS else SPEAKER_MS, rate)
                    created.play()
                    track = created
                    started = SystemClock.elapsedRealtime()
                    underruns = 0
                    fill = cushionMs.toDouble()
                    written = 0
                    owed = 0.0
                    fewest = Int.MAX_VALUE
                    windowStart = started
                    calmSince = started
                    fadeFrom = null
                }
                val t = track ?: continue
                // Just after a cut, this (the sound after the late part) fades in from what was cut.
                var input = chunk.samples
                fadeFrom?.let { from ->
                    fadeFrom = null
                    if (input != null) input = crossfade(from, input!!)
                }
                val now = SystemClock.elapsedRealtime()
                if (now - lastCheck >= 1000) {
                    lastCheck = now
                    route(t)   // earphones plugged in or out
                    // Headphones connected or disconnected meanwhile: start again from that kind's cushion.
                    val bt = isBluetooth(t)
                    val base = if (bt) BLUETOOTH_MS else SPEAKER_MS
                    if (bt != bluetooth) {
                        bluetooth = bt
                        setCushion(t, base, rate)
                        calmSince = now
                    }
                    if (now - started > 2000 && t.underrunCount > underruns) {
                        // Ran dry since the last check (not counting the first moments): keep 20 ms more ready.
                        gaps += t.underrunCount - underruns
                        setCushion(t, cushionMs + 20, rate)
                        calmSince = now
                    } else if (cushionMs > base && now - calmSince > CALM_MS) {
                        // Half a minute without running dry: the extra reserve is delay that isn't needed now.
                        setCushion(t, maxOf(base, cushionMs - 10), rate)
                        calmSince = now
                    }
                    underruns = t.underrunCount
                }
                // Staying in step with the TV. Packets waiting beyond the cushion are sound that has fallen behind;
                // the fewest waiting at any moment over half a second is how far behind it is all the time (a burst
                // from the Wi-Fi comes and goes, that doesn't).
                fewest = minOf(fewest, queue.size)
                if (now - windowStart >= WINDOW_MS) {
                    val behind = fewest - KEEP
                    if (behind > CUT) {
                        // Well behind (held up on the Wi-Fi, then all at once): cut the late sound now, cross-fading
                        // from what would have come next into what comes after it, so the delay doesn't add up.
                        fadeFrom = cut(behind)
                        cuts++
                        owed = 0.0
                    } else if (behind > 0 && owed < behind * chunk.frames) {
                        // A little behind (the TV's clock a hair fast): catch up by playing up to 1% faster.
                        adjusted++
                        owed = behind * chunk.frames.toDouble()
                    }
                    fewest = Int.MAX_VALUE
                    windowStart = now
                }
                // Running low (the TV's clock a hair slow): owe a frame now and then, and play a hair slower. Owing
                // eases the speed in and out, and corrections that disagree cancel out rather than wobbling the sound.
                val head = t.playbackHeadPosition.toLong() and 0xFFFFFFFFL
                fill = fill * 0.995 + (written - head) * 1000.0 / rate * 0.005
                if (now - started > 3000 && queue.isEmpty() && fill < cushionMs * 0.4) {
                    adjusted++
                    owed -= 1
                }
                val step = 1.0 + (owed / EASING).coerceIn(-MOST_SPEED, MOST_SPEED)
                val samples = resampler.process(concealer.next(input, chunk.frames), step)
                owed -= samples.size / 4 * (step - 1)   // what this chunk dropped or added
                val n = t.write(samples, 0, samples.size)   // waits while the phone's audio system is full
                if (n > 0) written += n / 4
            }
        } catch (e: Exception) {
            if (running) Log.w(TAG, "TV sound (playing)", e)
        } finally {
            track?.release()
        }
    }

    /** Drops the [count] oldest packets waiting (sound that has fallen behind); returns the first one's sound, to fade out of. */
    private fun cut(count: Int): ByteArray? {
        var first: ByteArray? = null
        for (i in 0 until count) {
            val dropped = queue.poll() ?: break
            if (i == 0) first = dropped.samples
        }
        return first
    }

    /** [to], fading in over its length from [from] (the sound that would have played instead), with no click. */
    private fun crossfade(from: ByteArray, to: ByteArray): ByteArray {
        val out = to.copyOf()
        val n = minOf(from.size, to.size) / 4
        for (i in 0 until n * 2) {
            val w = (0.5 - 0.5 * kotlin.math.cos(Math.PI * (i / 2 + 0.5) / n)).toFloat()
            val v = (sample(to, i * 2) * w + sample(from, i * 2) * (1 - w)).toInt().coerceIn(-32768, 32767)
            out[i * 2] = v.toByte()
            out[i * 2 + 1] = (v shr 8).toByte()
        }
        return out
    }

    /**
     * For the notification: cushion, packets lost on the Wi-Fi (and repaired), times the sound ran out, smooth
     * corrections, and times late sound was cut to catch up with the TV.
     */
    fun stats(): IntArray = intArrayOf(cushionMs, lost, repaired, gaps, adjusted, cuts)

    private fun setCushion(track: AudioTrack, ms: Int, rate: Int) {
        cushionMs = ms.coerceIn(SPEAKER_MS, MOST_MS)
        track.setBufferSizeInFrames(cushionMs * rate / 1000)
    }

    /** Points the sound at the first of [OUTPUTS] present, when that changes: never at Bluetooth. */
    private fun route(track: AudioTrack) {
        val outputs = audio.getDevices(AudioManager.GET_DEVICES_OUTPUTS)
        var best: AudioDeviceInfo? = null
        for (type in OUTPUTS) {
            best = outputs.firstOrNull { it.type == type }
            if (best != null) break
        }
        if (best == null || best.id == routedTo) return
        if (track.setPreferredDevice(best)) routedTo = best.id
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
