package com.couchtv.remote

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.drawable.Icon
import android.net.wifi.WifiManager
import android.os.Build
import android.os.IBinder

/**
 * Keeps the TV's sound playing on this phone with the screen off or the app closed, until Stop (here, or in the
 * notification). Holds the Wi-Fi in its fastest mode meanwhile: phones' Wi-Fi power saving can add 100 ms or more.
 */
class ListenService : Service() {
    companion object {
        const val EXTRA_TV = "tv"
        private const val ACTION_STOP = "com.couchtv.remote.STOP_LISTENING"
        private const val CHANNEL = "listen"
        private const val NOTIFICATION_ID = 7

        @Volatile var running = false
            private set

        /** Called on the main thread when listening starts or stops. */
        var onChange: (() -> Unit)? = null
    }

    private var listener: AudioListener? = null
    private val locks = ArrayList<WifiManager.WifiLock>()

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stopSelf()
            return START_NOT_STICKY
        }
        val tv = intent?.getStringExtra(EXTRA_TV)
        if (tv == null) {
            stopSelf()
            return START_NOT_STICKY
        }
        showNotification()
        if (listener == null) {
            holdWifi()
            listener = AudioListener(this, tv).also { it.start() }
            running = true
            onChange?.invoke()
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        listener?.stop()
        listener = null
        for (lock in locks) if (lock.isHeld) lock.release()
        locks.clear()
        running = false
        onChange?.invoke()
        super.onDestroy()
    }

    @Suppress("DEPRECATION")
    private fun holdWifi() {
        val wifi = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        val modes = ArrayList<Int>()
        modes += WifiManager.WIFI_MODE_FULL_HIGH_PERF
        if (Build.VERSION.SDK_INT >= 29) modes += WifiManager.WIFI_MODE_FULL_LOW_LATENCY
        for (mode in modes) {
            runCatching {
                val lock = wifi.createWifiLock(mode, "CouchTV sound")
                lock.setReferenceCounted(false)
                lock.acquire()
                locks += lock
            }
        }
    }

    private fun showNotification() {
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(NotificationChannel(CHANNEL, getString(R.string.listen_channel), NotificationManager.IMPORTANCE_LOW))
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getService(this, 1, Intent(this, ListenService::class.java).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE)
        val notification = Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_headphones)
            .setContentTitle(getString(R.string.listen_notification_title))
            .setContentText(getString(R.string.listen_notification_text))
            .setContentIntent(open)
            .setOngoing(true)
            .addAction(Notification.Action.Builder(Icon.createWithResource(this, R.drawable.ic_headphones), getString(R.string.stop), stop).build())
            .build()
        if (Build.VERSION.SDK_INT >= 29) startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK)
        else startForeground(NOTIFICATION_ID, notification)
    }
}
