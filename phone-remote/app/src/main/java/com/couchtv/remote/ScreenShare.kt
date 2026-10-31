package com.couchtv.remote

import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.graphics.drawable.Icon
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioRecord
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.os.IBinder
import android.os.Looper
import android.util.DisplayMetrics
import android.util.Log
import android.view.Display
import android.view.Surface
import android.widget.Toast
import java.io.IOException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket

/**
 * This phone's screen and sound on the TV ("Phone screens" in ir-receiver/PROTOCOL.md; CouchTV's side is PhoneShare),
 * to show everyone in front of it something on the phone: up to 3 phones at once, side by side, their sounds mixed.
 * The screen goes through the phone's own video encoder (H.264, set up for little delay: no B-frames, real-time
 * priority, a picture sent the moment it's made), and what the phone plays (its media and games, from Android 10; not
 * calls, nor apps that refuse to be recorded) as plain 16-bit stereo in 10 ms chunks, both on a paired connection of
 * their own, encrypted (FrameWriter): about a tenth of a second behind the phone. Turning the phone sideways turns the
 * picture too. A foreground service, as Android wants for recording the screen, with Stop in its notification; Back
 * or Home on the TV stops it too.
 */
class ScreenShare : Service() {
    companion object {
        private const val TAG = "CouchTV"
        private const val CHANNEL = "share"
        private const val NOTIFICATION_ID = 9
        private const val EXTRA_RESULT = "result"
        private const val EXTRA_DATA = "data"
        private const val EXTRA_HOST = "host"
        private const val ACTION_STOP = "com.couchtv.remote.STOP_SHARING"
        private const val LONG_SIDE = 1600         // the picture's longer side: sharp on the TV (a film's trimmed out of it there), light on the Wi-Fi
        private const val FPS = 30
        private const val BITRATE = 5_000_000
        private const val RATE = 48000
        private const val CHUNK = 480 * 4          // 10 ms of 16-bit stereo

        /** Sharing now (or connecting to the TV to). */
        @Volatile var running = false
            private set

        /** On the main thread: sharing started (true) or stopped (false), and why it stopped, in words (null: asked to). */
        var onChange: ((Boolean, String?) -> Unit)? = null

        /** [resultCode] and [data]: Android's answer to its "start recording?" question. [host]: the TV's address. */
        fun start(context: Context, resultCode: Int, data: Intent, host: String) {
            context.startForegroundService(Intent(context, ScreenShare::class.java)
                .putExtra(EXTRA_RESULT, resultCode).putExtra(EXTRA_DATA, data).putExtra(EXTRA_HOST, host))
        }

        fun stop(context: Context) {
            if (running) context.startService(Intent(context, ScreenShare::class.java).setAction(ACTION_STOP))
        }
    }

    private val main = Handler(Looper.getMainLooper())
    private var projection: MediaProjection? = null
    private var display: VirtualDisplay? = null
    private var encoder: MediaCodec? = null
    private var codecThread: HandlerThread? = null
    private var codec: Handler? = null            // the encoder's thread: its pictures, and any change to it
    private var displays: DisplayManager? = null
    @Volatile private var socket: Socket? = null
    @Volatile private var writer: FrameWriter? = null
    @Volatile private var stopping = false
    @Volatile private var why: String? = null
    private var width = 0
    private var height = 0
    // What's shared, as Android 14 and newer say it (the whole screen, or the one app's window), in pixels; 0 till then.
    @Volatile private var contentWidth = 0
    @Volatile private var contentHeight = 0
    private var config: ByteArray? = null         // the encoder's sequence and picture parameters, for each whole picture

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            finish(null)
            return START_NOT_STICKY
        }
        if (running) return START_NOT_STICKY
        val data = intent?.let { answer(it) }
        val host = intent?.getStringExtra(EXTRA_HOST)
        val result = intent?.getIntExtra(EXTRA_RESULT, 0) ?: 0
        if (data == null || host == null) {
            stopSelf()
            return START_NOT_STICKY
        }
        // In the foreground, with its notification, before Android hands over the screen (as Android 14 wants).
        getSystemService(NotificationManager::class.java)
            .createNotificationChannel(NotificationChannel(CHANNEL, getString(R.string.share_channel), NotificationManager.IMPORTANCE_LOW))
        if (Build.VERSION.SDK_INT >= 29) startForeground(NOTIFICATION_ID, notification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
        else startForeground(NOTIFICATION_ID, notification())
        running = true
        stopping = false
        why = null
        onChange?.invoke(true, null)
        val p = runCatching { getSystemService(MediaProjectionManager::class.java).getMediaProjection(result, data) }.getOrNull()
        if (p == null) {
            finish(getString(R.string.share_refused))
            return START_NOT_STICKY
        }
        projection = p
        p.registerCallback(object : MediaProjection.Callback() {
            override fun onStop() {
                main.post { finish(null) }   // stopped from Android's own controls (or another app took the screen)
            }

            // Android 14 and newer say when what's shared changes size: the phone turned, or the one app shared resized.
            override fun onCapturedContentResize(width: Int, height: Int) {
                contentWidth = width
                contentHeight = height
                codec?.post { fitPicture() }
            }
        }, main)
        Thread({ connect(host) }, "CouchTV share").start()
        return START_NOT_STICKY
    }

    @Suppress("DEPRECATION")
    private fun answer(intent: Intent): Intent? =
        if (Build.VERSION.SDK_INT >= 33) intent.getParcelableExtra(EXTRA_DATA, Intent::class.java) else intent.getParcelableExtra(EXTRA_DATA)

    override fun onDestroy() {
        finish(null)
        super.onDestroy()
    }

    // ---------------------------------------------------------------- the connection

    /** A paired connection of its own: SHARE, then (once the TV says OK) the pictures and sound; the TV's KEY and STOP. */
    private fun connect(host: String) {
        try {
            val s = Socket()
            socket = s
            wifiNetwork()?.bindSocket(s)
            s.sendBufferSize = 1 shl 20
            s.connect(InetSocketAddress(InetAddress.getByName(host), WifiLink.CONTROL_PORT), 3000)
            s.tcpNoDelay = true
            val lines = LineIo(s.getInputStream(), s.getOutputStream())
            val pairings = Pairings(this)
            val hs = Handshake.start(lines, pairings.phoneId, BuildConfig.VERSION_NAME) ?: throw IOException(getString(R.string.share_old_tv))
            val key = pairings.key(hs.tvId)?.takeIf { hs.known } ?: throw IOException(getString(R.string.share_not_paired))
            val keys = hs.auth(lines, key) ?: throw IOException(getString(R.string.share_not_paired))
            val channel = Channel(keys.phoneEnc, keys.phoneMac, keys.tvEnc, keys.tvMac)
            val (w, h) = pictureSize()
            lines.write(channel.seal("SHARE $w $h"))
            when (lines.read()?.let { channel.open(it) }) {
                "SHARE OK" -> Unit
                "SHARE FULL" -> throw IOException(getString(R.string.share_full))
                else -> throw IOException(getString(R.string.share_old_tv))
            }
            writer = FrameWriter(s.getOutputStream(), keys.shareEnc, keys.shareMac)
            startPicture(w, h)
            startSound()
            while (!stopping) {
                when (lines.read()?.let { channel.open(it) } ?: break) {
                    "KEY" -> askWholePicture()
                    "STOP" -> {
                        why = getString(R.string.share_closed_on_tv)
                        break
                    }
                }
            }
            if (!stopping && why == null) why = getString(R.string.share_lost)
        } catch (e: Exception) {
            if (!stopping) {
                why = e.message ?: getString(R.string.share_lost)
                Log.i(TAG, "Share: ${e.message}")
            }
        }
        main.post { finish(why) }
    }

    private fun send(message: ByteArray) {
        val w = writer ?: return
        try {
            w.send(message)
        } catch (e: IOException) {
            if (stopping) return
            why = getString(R.string.share_lost)
            runCatching { socket?.close() }   // the connection's thread then finishes
        }
    }

    // ---------------------------------------------------------------- the picture

    /**
     * The picture's size: the shape of what's shared (as Android 14 and newer say it; else the screen's, as it is
     * now), its longer side 1280, in multiples of 16 (as encoders like).
     */
    @Suppress("DEPRECATION")
    private fun pictureSize(): Pair<Int, Int> {
        var w = contentWidth
        var h = contentHeight
        if (w <= 0 || h <= 0) {
            val metrics = DisplayMetrics()
            getSystemService(DisplayManager::class.java).getDisplay(Display.DEFAULT_DISPLAY).getRealMetrics(metrics)
            w = metrics.widthPixels
            h = metrics.heightPixels
        }
        val scale = minOf(1f, LONG_SIDE.toFloat() / maxOf(w, h))
        fun fit(v: Int) = maxOf(16, (v * scale).toInt() / 16 * 16)
        return fit(w) to fit(h)
    }

    private fun startPicture(w: Int, h: Int) {
        val thread = HandlerThread("CouchTV share picture").apply { start() }
        codecThread = thread
        val handler = Handler(thread.looper)
        codec = handler
        handler.post {
            val p = projection ?: return@post
            try {
                val surface = newEncoder(w, h)
                display = p.createVirtualDisplay("CouchTV share", w, h, resources.displayMetrics.densityDpi,
                    DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, surface, null, handler)
            } catch (e: Exception) {
                Log.i(TAG, "Share picture: ${e.message}")
                why = getString(R.string.share_no_picture)
                runCatching { socket?.close() }
                return@post
            }
            // Turned sideways (or back): the picture turns too. Android says the screen changed, but sometimes before
            // its new size is there to read, so it's looked at again a moment later, and every half second besides.
            val manager = getSystemService(DisplayManager::class.java)
            displays = manager
            manager.registerDisplayListener(turned, handler)
            handler.postDelayed(lookAgain, 500)
        }
    }

    private val turned = object : DisplayManager.DisplayListener {
        override fun onDisplayAdded(displayId: Int) = Unit
        override fun onDisplayRemoved(displayId: Int) = Unit
        override fun onDisplayChanged(displayId: Int) {
            if (displayId != Display.DEFAULT_DISPLAY || stopping) return
            fitPicture()
            codec?.postDelayed({ fitPicture() }, 300)
        }
    }

    private val lookAgain = object : Runnable {
        override fun run() {
            if (stopping) return
            fitPicture()
            codec?.postDelayed(this, 500)
        }
    }

    /**
     * The picture the size of what's shared now (the phone turned, or the shared app's window resized): a new encoder
     * that size, and the screen drawn into it. Nothing's drawn meanwhile, so no picture of the wrong size slips out.
     * On the encoder's thread.
     */
    private fun fitPicture() {
        if (stopping || display == null) return
        val (w, h) = pictureSize()
        if (w == width && h == height) return
        val old = encoder
        try {
            display?.surface = null
            val surface = newEncoder(w, h)
            display?.resize(w, h, resources.displayMetrics.densityDpi)
            display?.surface = surface
            Log.i(TAG, "Share: the picture's ${w}x$h now")
        } catch (e: Exception) {
            Log.i(TAG, "Share picture, resized: ${e.message}")
        }
        old?.let {
            runCatching { it.stop() }
            runCatching { it.release() }
        }
    }

    /** An encoder for pictures this size, sending each picture as it's made; the TV's told the size first. */
    private fun newEncoder(w: Int, h: Int): Surface {
        width = w
        height = h
        config = null
        send(byteArrayOf(1, (w shr 8).toByte(), w.toByte(), (h shr 8).toByte(), h.toByte()))
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, w, h).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, BITRATE)
            setInteger(MediaFormat.KEY_FRAME_RATE, FPS)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 2)                // a whole picture every 2 s, and when the TV asks
            setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 100_000L)  // a still screen still sends 10 a second (µs)
            setInteger(MediaFormat.KEY_PRIORITY, 0)                        // real time
            setInteger(MediaFormat.KEY_LATENCY, 1)                         // each picture out as soon as it's in
            if (Build.VERSION.SDK_INT >= 29) setInteger(MediaFormat.KEY_MAX_B_FRAMES, 0)
        }
        val made = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        made.setCallback(object : MediaCodec.Callback() {
            override fun onInputBufferAvailable(codec: MediaCodec, index: Int) = Unit
            override fun onOutputFormatChanged(codec: MediaCodec, format: MediaFormat) = Unit
            override fun onOutputBufferAvailable(codec: MediaCodec, index: Int, info: MediaCodec.BufferInfo) = sendPicture(codec, index, info)
            override fun onError(codec: MediaCodec, e: MediaCodec.CodecException) {
                Log.i(TAG, "Share encoder: ${e.message}")
                if (codec === encoder && !stopping) {
                    why = getString(R.string.share_no_picture)
                    runCatching { socket?.close() }
                }
            }
        }, codec)
        made.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        val surface = made.createInputSurface()
        made.start()
        encoder = made
        return surface
    }

    /** A picture from the encoder: to the TV ("2", whole or not, its time in µs, H.264), a whole one with its parameters. */
    private fun sendPicture(codec: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
        try {
            val buffer = codec.getOutputBuffer(index)
            if (buffer != null && info.size > 0) {
                val bytes = ByteArray(info.size)
                buffer.position(info.offset)
                buffer.get(bytes, 0, info.size)
                if ((info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) {
                    config = bytes
                } else if (codec === encoder) {
                    val whole = (info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0
                    val parameters = config
                    val data = if (whole && parameters != null && !hasParameters(bytes)) parameters + bytes else bytes
                    val message = ByteArray(10 + data.size)
                    message[0] = 2
                    message[1] = if (whole) 1 else 0
                    val time = info.presentationTimeUs
                    for (i in 0 until 8) message[2 + i] = (time ushr (56 - 8 * i)).toByte()
                    data.copyInto(message, 10)
                    send(message)
                }
            }
            codec.releaseOutputBuffer(index, false)
        } catch (e: IllegalStateException) {
            // That encoder's gone meanwhile (the phone turned, or it's stopping).
        }
    }

    /** Whether a whole picture brings its sequence parameters (an H.264 unit of type 7) already. */
    private fun hasParameters(bytes: ByteArray): Boolean {
        for (i in 0 until minOf(bytes.size - 4, 64)) {
            if (bytes[i].toInt() != 0 || bytes[i + 1].toInt() != 0) continue
            val at = if (bytes[i + 2].toInt() == 1) i + 3 else if (bytes[i + 2].toInt() == 0 && bytes[i + 3].toInt() == 1) i + 4 else continue
            if (at < bytes.size && (bytes[at].toInt() and 0x1F) == 7) return true
        }
        return false
    }

    /** The TV's decoder needs a whole picture to start from (it's just opened, or it fell behind). */
    private fun askWholePicture() {
        codec?.post { runCatching { encoder?.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) }) } }
    }

    // ---------------------------------------------------------------- the sound

    /** What the phone plays, from Android 10 (with the microphone permission, which Android asks for recording any sound). */
    private fun startSound() {
        if (Build.VERSION.SDK_INT < 29) return
        if (checkSelfPermission(android.Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) return
        val p = projection ?: return
        Thread({ sound(p) }, "CouchTV share sound").start()
    }

    @SuppressLint("MissingPermission", "NewApi")
    private fun sound(p: MediaProjection) {
        val record = try {
            // Not this app's own sound: while it shares, Listen on this phone plays the other phones' sound, which must
            // not go back to the TV.
            val capture = AudioPlaybackCaptureConfiguration.Builder(p)
                .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                .addMatchingUsage(AudioAttributes.USAGE_GAME)
                .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                .excludeUid(android.os.Process.myUid())
                .build()
            val format = AudioFormat.Builder().setEncoding(AudioFormat.ENCODING_PCM_16BIT).setSampleRate(RATE)
                .setChannelMask(AudioFormat.CHANNEL_IN_STEREO).build()
            val least = AudioRecord.getMinBufferSize(RATE, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT)
            AudioRecord.Builder().setAudioFormat(format).setBufferSizeInBytes(maxOf(least, CHUNK * 4)).setAudioPlaybackCaptureConfig(capture).build()
        } catch (e: Exception) {
            Log.i(TAG, "Share sound: ${e.message}")
            return
        }
        try {
            record.startRecording()
            val chunk = ByteArray(1 + CHUNK)
            chunk[0] = 3
            while (!stopping) {
                var done = 0
                while (done < CHUNK && !stopping) {
                    val n = record.read(chunk, 1 + done, CHUNK - done)
                    if (n <= 0) break
                    done += n
                }
                if (done < CHUNK) break
                send(chunk)
            }
        } catch (e: Exception) {
            Log.i(TAG, "Share sound: ${e.message}")
        } finally {
            runCatching { record.stop() }
            record.release()
        }
    }

    // ---------------------------------------------------------------- stopping

    /** All of it stops: the screen's given back, the TV let go, and the app told (with [reason], if it wasn't asked to). */
    private fun finish(reason: String?) {
        if (stopping) return
        stopping = true
        running = false
        runCatching { socket?.close() }
        displays?.unregisterDisplayListener(turned)
        val handler = codec
        if (handler != null) {
            handler.post {
                runCatching { display?.release() }
                display = null
                encoder?.let {
                    runCatching { it.stop() }
                    runCatching { it.release() }
                }
                encoder = null
                codecThread?.quitSafely()
            }
        }
        runCatching { projection?.stop() }
        projection = null
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
        onChange?.invoke(false, reason)
        if (reason != null) Toast.makeText(this, reason, Toast.LENGTH_LONG).show()
    }

    private fun notification(): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getService(this, 2, Intent(this, ScreenShare::class.java).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE)
        return Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_screen_share)
            .setContentTitle(getString(R.string.share_on))
            .setContentText(getString(R.string.share_on_text))
            .setOngoing(true)
            .setContentIntent(open)
            .addAction(Notification.Action.Builder(Icon.createWithResource(this, R.drawable.ic_screen_share), getString(R.string.stop), stop).build())
            .build()
    }

    @Suppress("DEPRECATION")
    private fun wifiNetwork(): Network? {
        val connectivity = getSystemService(ConnectivityManager::class.java)
        return connectivity.allNetworks.firstOrNull {
            val caps = connectivity.getNetworkCapabilities(it)
            caps != null && (caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) || caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET))
        }
    }
}
