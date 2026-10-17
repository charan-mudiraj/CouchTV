package com.couchtv.remote

import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder

/**
 * Keeps the app going while the TV plays something and the app's put away, so the notification's player (TvPlayer)
 * stays up to date and its buttons reach the TV. Takes the notification's buttons too. Stops with the player, or when
 * the app is swiped away.
 */
class PlayerService : Service() {
    companion object {
        @Volatile var running = false
            private set
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            TvPlayer.ACTION_BUTTON -> {
                TvPlayer.button(intent.getStringExtra(TvPlayer.EXTRA_DO))
                if (!running) stopSelf()
                return START_NOT_STICKY
            }
            TvPlayer.ACTION_DISMISSED -> {
                TvPlayer.dismissed()
                stopSelf()
                return START_NOT_STICKY
            }
        }
        // Started by TvPlayer: in the foreground, with its notification (Android wants that within a few seconds, even
        // when the player's gone meanwhile, or it closes the app).
        val notification = TvPlayer.notification(this)
        if (notification == null) {
            runCatching {
                val channel = "tv_media_gone"
                getSystemService(android.app.NotificationManager::class.java).createNotificationChannel(
                    android.app.NotificationChannel(channel, getString(R.string.media_channel), android.app.NotificationManager.IMPORTANCE_MIN))
                val empty = android.app.Notification.Builder(this, channel).setSmallIcon(R.drawable.ic_tv).build()
                if (Build.VERSION.SDK_INT >= 29) startForeground(TvPlayer.NOTIFICATION_ID, empty, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK)
                else startForeground(TvPlayer.NOTIFICATION_ID, empty)
            }
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
            return START_NOT_STICKY
        }
        try {
            if (Build.VERSION.SDK_INT >= 29) startForeground(TvPlayer.NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK)
            else startForeground(TvPlayer.NOTIFICATION_ID, notification)
            running = true
        } catch (e: Exception) {
            stopSelf()   // Android said no (the app went away meanwhile): the notification still shows while the app runs
            getSystemService(android.app.NotificationManager::class.java).notify(TvPlayer.NOTIFICATION_ID, notification)
        }
        return START_NOT_STICKY
    }

    /** The app swiped away from the recent apps: the player goes with it. */
    override fun onTaskRemoved(rootIntent: Intent?) {
        TvPlayer.end()
        stopSelf()
        super.onTaskRemoved(rootIntent)
    }

    override fun onDestroy() {
        running = false
        super.onDestroy()
    }
}
