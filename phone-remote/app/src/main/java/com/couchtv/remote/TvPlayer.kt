package com.couchtv.remote

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.drawable.Icon
import android.media.MediaMetadata
import android.media.session.MediaSession
import android.media.session.PlaybackState
import android.os.Handler
import android.os.Looper
import android.os.SystemClock

/**
 * What the TV plays, on this phone: Android's own media player in the notifications (and on the lock screen), with
 * the picture, previous, play/pause, next and the timeline, all working the TV over Wi-Fi. A MediaSession tells
 * Android what it is; PlayerService keeps the app (and its link to the TV) going for it while the app is put away.
 * It goes when the TV stops playing, a while after it's paused, or when it's swiped away. The remote's first page
 * shows the same player (NowPlayingCard), following [media]. Main thread only.
 */
object TvPlayer {
    private const val CHANNEL = "tv_media"
    const val NOTIFICATION_ID = 8
    const val ACTION_BUTTON = "com.couchtv.remote.MEDIA_BUTTON"     // extra "do": play, pause, next, previous
    const val ACTION_DISMISSED = "com.couchtv.remote.MEDIA_DISMISSED"
    const val EXTRA_DO = "do"
    private const val PAUSED_MS = 15 * 60_000L   // paused this long: the notification goes, and the app lets go of the TV
    private const val GAP_MS = 5_000L            // nothing playing for a moment (between two videos) before it goes...
    private const val AWAY_GAP_MS = 25_000L      // ...or the link to the TV coming back, while the app's put away

    /** Sends "MEDIA <what>" to the TV ("PLAY", "SEEK 83000"...); set by the app while it has a link to send it on. */
    var send: ((String) -> Unit)? = null

    /** Called when the notification goes: the app lets go of the TV then, if it's been put away. */
    var onIdle: (() -> Unit)? = null

    /** Called when what plays changes, or a button changed it: for the player on the remote's first page. */
    var onChange: (() -> Unit)? = null

    /** The app is on the screen (its link to the TV is up for its own sake then). */
    var appVisible = true

    /** The notification is up: the app keeps its link to the TV while it's put away. */
    val active: Boolean get() = session != null

    /** What the TV plays, as it last said (and as the buttons since changed it); null for nothing. */
    var media: TvMedia? = null
        private set

    /** Its picture; null for none. */
    var art: Bitmap? = null
        private set

    private lateinit var app: Context
    private val main = Handler(Looper.getMainLooper())
    private var session: MediaSession? = null
    private var dismissed: String? = null   // swiped away while paused: not again until it plays, or something else does
    private var fading = false              // nothing plays now: it goes in a moment, unless something does again
    private val nothing = Runnable {
        fading = false
        media = null
        art = null
        hide()
        onChange?.invoke()
    }
    private val pausedLong = Runnable { hide() }

    /** What the TV says it plays now (null: nothing), with its picture. */
    fun update(context: Context, now: TvMedia?, picture: Bitmap?) {
        app = context.applicationContext
        if (now == null) {
            if (media != null && !fading) {
                fading = true
                main.removeCallbacks(nothing)
                main.postDelayed(nothing, if (appVisible) GAP_MS else AWAY_GAP_MS)
            }
            return
        }
        fading = false
        main.removeCallbacks(nothing)
        media = now
        art = picture
        onChange?.invoke()
        val key = now.app + "\n" + now.title
        if (dismissed != null) {
            if (dismissed == key && !now.playing) return
            dismissed = null
        }
        main.removeCallbacks(pausedLong)
        if (!now.playing) main.postDelayed(pausedLong, PAUSED_MS)
        show(session == null)
    }

    /** A button: in the notification, on the lock screen, a headset's, or the remote's own player. */
    fun button(what: String?, ms: Long = 0) {
        val m = media ?: return
        val now = SystemClock.elapsedRealtime()
        // Shown at once, before the TV says so: a press that answers straight away.
        when (what) {
            "play" -> media = m.played(true, now)
            "pause" -> media = m.played(false, now)
            "seek" -> media = m.movedTo(ms, now)
            "next", "previous" -> {}
            else -> return
        }
        onChange?.invoke()
        if (session != null) {
            main.removeCallbacks(pausedLong)
            if (media?.playing == false) main.postDelayed(pausedLong, PAUSED_MS)
            show(false)
        }
        send?.invoke(if (what == "seek") "SEEK $ms" else what.uppercase())
    }

    /** The notification swiped away (Android allows it once it's paused): gone until it plays again. */
    fun dismissed() {
        media?.let { dismissed = it.app + "\n" + it.title }
        hide()
    }

    /** The app's closing: nothing to show any more. */
    fun end() {
        main.removeCallbacks(nothing)
        fading = false
        media = null
        art = null
        hide()
    }

    /** The notification and the session go, and the service stops. */
    private fun hide() {
        main.removeCallbacks(pausedLong)
        val s = session ?: return
        s.isActive = false
        s.release()
        session = null
        if (::app.isInitialized) {
            app.getSystemService(NotificationManager::class.java).cancel(NOTIFICATION_ID)
            app.stopService(Intent(app, PlayerService::class.java))
        }
        onIdle?.invoke()
    }

    private fun show(first: Boolean) {
        val m = media ?: return
        val s = session ?: MediaSession(app, "CouchTV").also {
            it.setCallback(callback)
            session = it
        }
        val metadata = MediaMetadata.Builder()
            .putString(MediaMetadata.METADATA_KEY_TITLE, title(m))
            .putString(MediaMetadata.METADATA_KEY_ARTIST, subtitle(m))
            .putString(MediaMetadata.METADATA_KEY_DISPLAY_TITLE, title(m))
            .putString(MediaMetadata.METADATA_KEY_DISPLAY_SUBTITLE, subtitle(m))
            .putLong(MediaMetadata.METADATA_KEY_DURATION, if (m.durationMs > 0) m.durationMs else -1)
        art?.let { metadata.putBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART, it) }
        s.setMetadata(metadata.build())
        var actions = 0L
        if (m.canPlayPause) actions = actions or PlaybackState.ACTION_PLAY or PlaybackState.ACTION_PAUSE or PlaybackState.ACTION_PLAY_PAUSE
        if (m.canNext) actions = actions or PlaybackState.ACTION_SKIP_TO_NEXT
        if (m.canPrevious) actions = actions or PlaybackState.ACTION_SKIP_TO_PREVIOUS
        if (m.canSeek && m.durationMs > 0) actions = actions or PlaybackState.ACTION_SEEK_TO
        s.setPlaybackState(PlaybackState.Builder()
            .setActions(actions)
            .setState(if (m.playing) PlaybackState.STATE_PLAYING else PlaybackState.STATE_PAUSED, m.positionMs, if (m.playing) 1f else 0f, m.at)
            .build())
        s.isActive = true
        if (first || !PlayerService.running) {
            // The service keeps the app going (and shows this) while it's put away. Android only lets it start while
            // the app's on the screen; otherwise the notification shows for as long as the app's left running.
            try {
                app.startForegroundService(Intent(app, PlayerService::class.java))
                return
            } catch (e: Exception) { }
        }
        app.getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, notification(app))
    }

    private fun title(m: TvMedia) = m.title.ifEmpty { m.artist.ifEmpty { app.getString(R.string.media_something) } }

    /** The artist (or channel), and where it plays: "Saregama Music · YouTube on the TV". */
    private fun subtitle(m: TvMedia): String {
        val where = if (m.app.isEmpty()) app.getString(R.string.media_on_the_tv) else app.getString(R.string.media_on_tv, m.app)
        val who = if (m.title.isEmpty()) "" else m.artist
        return if (who.isEmpty()) where else "$who · $where"
    }

    /** The notification as it is now; for PlayerService too. Null when there's nothing to show. */
    fun notification(context: Context): Notification? {
        val m = media ?: return null
        val s = session ?: return null
        val manager = context.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(NotificationChannel(CHANNEL, context.getString(R.string.media_channel), NotificationManager.IMPORTANCE_LOW).apply {
            setShowBadge(false)
        })
        val open = PendingIntent.getActivity(context, 0, Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_IMMUTABLE)
        val gone = PendingIntent.getService(context, 20, Intent(context, PlayerService::class.java).setAction(ACTION_DISMISSED),
            PendingIntent.FLAG_IMMUTABLE)
        val builder = Notification.Builder(context, CHANNEL)
            .setSmallIcon(R.drawable.ic_tv)
            .setContentTitle(title(m))
            .setContentText(subtitle(m))
            .setContentIntent(open)
            .setDeleteIntent(gone)
            .setOngoing(m.playing)
            .setShowWhen(false)
            .setOnlyAlertOnce(true)
            .setCategory(Notification.CATEGORY_TRANSPORT)
            .setVisibility(Notification.VISIBILITY_PUBLIC)
        art?.let { builder.setLargeIcon(it) }
        // The buttons (Android 13 and newer take them from the session instead; these are for older phones).
        val compact = ArrayList<Int>()
        fun add(icon: Int, label: Int, what: String) {
            val intent = PendingIntent.getService(context, what.hashCode(), Intent(context, PlayerService::class.java)
                .setAction(ACTION_BUTTON).putExtra(EXTRA_DO, what), PendingIntent.FLAG_IMMUTABLE)
            compact += compact.size
            builder.addAction(Notification.Action.Builder(Icon.createWithResource(context, icon), context.getString(label), intent).build())
        }
        if (m.canPrevious) add(R.drawable.ic_skip_previous, R.string.media_previous, "previous")
        if (m.canPlayPause) {
            if (m.playing) add(R.drawable.ic_pause, R.string.media_pause, "pause")
            else add(R.drawable.ic_play, R.string.media_play, "play")
        }
        if (m.canNext) add(R.drawable.ic_skip_next, R.string.media_next, "next")
        builder.setStyle(Notification.MediaStyle().setMediaSession(s.sessionToken).setShowActionsInCompactView(*compact.take(3).toIntArray()))
        return builder.build()
    }

    private val callback = object : MediaSession.Callback() {
        override fun onPlay() = button("play")
        override fun onPause() = button("pause")
        override fun onSkipToNext() = button("next")
        override fun onSkipToPrevious() = button("previous")
        override fun onSeekTo(pos: Long) = button("seek", pos)
        override fun onStop() = button("pause")
    }
}
