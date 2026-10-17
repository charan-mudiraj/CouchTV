package com.couchtv.remote

import android.app.AlarmManager
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.job.JobInfo
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.graphics.drawable.Icon
import android.net.Uri
import android.os.Build
import java.util.concurrent.Executors

/**
 * What this phone says about the watchlist, and when: "added" when another phone adds a film; an hour before its time
 * (with Postpone); and at its time (with Play on TV and Postpone). Nothing once the time has gone. The reminders are
 * this phone's own alarms, so they come with the TV off and no internet, and are set again after a restart
 * (WatchStartup). A reminder missed with the phone off comes as it's back, if the film's time hasn't come yet.
 */
object WatchReminders {
    const val ACTION_REMIND = "com.couchtv.remote.action.REMIND"
    const val ACTION_OPEN = "com.couchtv.remote.action.WATCHLIST"   // a notification tapped: the first page
    const val ACTION_PLAY = "com.couchtv.remote.action.PLAY_WATCH"  // Play on TV
    const val EXTRA_ID = "id"
    private const val EXTRA_KIND = "kind"
    private const val EXTRA_AT = "at"
    private const val HOUR_BEFORE = 0
    private const val AT_TIME = 1
    private const val CHANNEL_REMIND = "watch_remind"
    private const val CHANNEL_ADDED = "watch_added"

    private enum class Say { ADDED, HOUR_BEFORE, AT_TIME, MOVED }

    private val work = Executors.newSingleThreadExecutor()   // pictures for the notifications

    /** A film added, moved or taken off, [here] or on another phone: its alarms, and what's said about it. */
    fun changed(context: Context, m: WatchMerge, here: Boolean) {
        val now = System.currentTimeMillis()
        val item = m.after
        val before = m.before?.takeIf { it.live(now) }
        val coming = item.on && item.watchAt > now
        // Picked (or moved) less than an hour before its time: no "in an hour" for it.
        if (coming && item.watchAt - WatchItem.HOUR <= now) hourDone(context, item)
        schedule(context, item)
        when {
            here || !item.on -> cancel(context, item.id)   // done with whatever was showing about it
            before == null -> if (coming && item.by != Watchlist.get(context).me) say(context, item, Say.ADDED)
            before.watchAt != item.watchAt && showing(context, item.id) -> say(context, item, Say.MOVED)
        }
    }

    /** All the alarms again (after a restart, an update, or as the app opens). */
    fun scheduleAll(context: Context) {
        for (item in Watchlist.get(context).all()) schedule(context, item)
    }

    private fun schedule(context: Context, item: WatchItem) {
        for (kind in intArrayOf(HOUR_BEFORE, AT_TIME)) remindIntent(context, item, kind, create = false)?.let {
            context.getSystemService(AlarmManager::class.java).cancel(it)
            it.cancel()
        }
        val now = System.currentTimeMillis()
        if (!item.on || item.watchAt <= now) return
        val hourAt = item.watchAt - WatchItem.HOUR
        when {
            hourAt > now -> alarm(context, item, HOUR_BEFORE, hourAt)
            !isHourDone(context, item) -> {
                // Its "in an hour" was missed (the phone was off): now, saying how long is left.
                hourDone(context, item)
                say(context, item, Say.HOUR_BEFORE)
            }
        }
        alarm(context, item, AT_TIME, item.watchAt)
    }

    private fun alarm(context: Context, item: WatchItem, kind: Int, at: Long) {
        val alarms = context.getSystemService(AlarmManager::class.java)
        val intent = remindIntent(context, item, kind, create = true) ?: return
        try {
            // On the minute (Android 12 asks for that permission; later ones give it to reminder apps), else near it.
            if (Build.VERSION.SDK_INT < 31 || alarms.canScheduleExactAlarms()) alarms.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at, intent)
            else alarms.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at, intent)
        } catch (e: SecurityException) {
            alarms.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at, intent)
        }
    }

    private fun remindIntent(context: Context, item: WatchItem, kind: Int, create: Boolean): PendingIntent? {
        val intent = Intent(context, WatchReceiver::class.java).setAction(ACTION_REMIND)
            .setData(Uri.parse("couchtv-watch://${item.id}/$kind"))   // one alarm per film and kind
            .putExtra(EXTRA_ID, item.id).putExtra(EXTRA_KIND, kind).putExtra(EXTRA_AT, item.watchAt)
        return PendingIntent.getBroadcast(context, 0, intent,
            PendingIntent.FLAG_IMMUTABLE or if (create) PendingIntent.FLAG_UPDATE_CURRENT else PendingIntent.FLAG_NO_CREATE)
    }

    /** An alarm went off: its reminder, if the film's still on the list at that time. [done] once it's shown. */
    fun fire(context: Context, intent: Intent, done: () -> Unit) {
        val item = intent.getStringExtra(EXTRA_ID)?.let { Watchlist.get(context).find(it) }
        val at = intent.getLongExtra(EXTRA_AT, 0L)
        val now = System.currentTimeMillis()
        if (item == null || !item.on || item.watchAt != at || now > at + 2 * WatchItem.HOUR) return done()
        if (intent.getIntExtra(EXTRA_KIND, AT_TIME) == HOUR_BEFORE) {
            if (now >= at) return done()
            hourDone(context, item)
            say(context, item, Say.HOUR_BEFORE, done)
        } else say(context, item, Say.AT_TIME, done)
    }

    // "In an hour" said (or not to be) for a film at its time: "<id> <watch at>" lines.
    private fun isHourDone(context: Context, item: WatchItem) =
        prefs(context).getString("hour", "")!!.split("\n").contains("${item.id} ${item.watchAt}")

    private fun hourDone(context: Context, item: WatchItem) {
        val kept = Watchlist.get(context).all().map { it.id }.toSet()
        val lines = prefs(context).getString("hour", "")!!.split("\n").filter { it.isNotBlank() && it.substringBefore(" ") in kept }
        prefs(context).edit().putString("hour", (lines + "${item.id} ${item.watchAt}").distinct().joinToString("\n")).apply()
    }

    private fun prefs(context: Context) = context.getSharedPreferences("reminders", Context.MODE_PRIVATE)

    // ---------------------------------------------------------------- notifications

    private fun notificationId(id: String) = 0x5000_0000 or (id.hashCode() and 0x0FFF_FFFF)

    fun cancel(context: Context, id: String) = context.getSystemService(NotificationManager::class.java).cancel(notificationId(id))

    private fun showing(context: Context, id: String) =
        context.getSystemService(NotificationManager::class.java).activeNotifications.any { it.id == notificationId(id) }

    /** Shows what's to be said about [item], with its poster (fetched off the main thread). */
    private fun say(context: Context, item: WatchItem, what: Say, done: () -> Unit = {}) {
        val app = context.applicationContext
        work.execute {
            try {
                val poster = item.film.image?.let { url ->
                    runCatching { SuggestionsView.Pictures.get(app, url, (96 * app.resources.displayMetrics.density).toInt()) }.getOrNull()
                }
                post(app, item, what, poster)
            } catch (e: Exception) {
                android.util.Log.i("CouchTV", "Watchlist notification: ${e.message}")
            } finally {
                done()
            }
        }
    }

    private fun post(context: Context, item: WatchItem, what: Say, poster: android.graphics.Bitmap?) {
        val manager = context.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(NotificationChannel(CHANNEL_REMIND, context.getString(R.string.watch_channel_remind), NotificationManager.IMPORTANCE_HIGH))
        manager.createNotificationChannel(NotificationChannel(CHANNEL_ADDED, context.getString(R.string.watch_channel_added), NotificationManager.IMPORTANCE_DEFAULT))
        val film = item.film
        val now = System.currentTimeMillis()
        val minutes = ((item.watchAt - now + 59_999) / 60_000).toInt()
        val (title, text) = when (what) {
            Say.ADDED -> context.getString(R.string.watch_added_title, film.title) to
                context.getString(R.string.watch_when_on, WatchTime.describe(context, item.watchAt), film.app)
            Say.HOUR_BEFORE -> (if (minutes >= 55) context.getString(R.string.watch_hour_title, film.title)
                else context.getString(R.string.watch_soon_title, film.title, minutes.coerceAtLeast(1))) to
                context.getString(R.string.watch_when_on, WatchTime.clock(context, item.watchAt), film.app)
            Say.AT_TIME -> context.getString(R.string.watch_now_title, film.title) to context.getString(R.string.watch_now_text, film.app)
            Say.MOVED -> context.getString(R.string.watch_moved_title, film.title) to
                context.getString(R.string.watch_when_on, WatchTime.describe(context, item.watchAt), film.app)
        }
        val reminder = what == Say.HOUR_BEFORE || what == Say.AT_TIME
        val builder = Notification.Builder(context, if (reminder) CHANNEL_REMIND else CHANNEL_ADDED)
            .setSmallIcon(R.drawable.ic_bookmark_added)
            .setColor(context.getColor(R.color.good))
            .setContentTitle(title)
            .setContentText(text)
            .setContentIntent(activity(context, Intent(context, MainActivity::class.java).setAction(ACTION_OPEN), item.id, 0))
            .setAutoCancel(true)
            .setShowWhen(false)
            .setOnlyAlertOnce(what == Say.MOVED)   // brings a reminder up to date, without a sound
            .setCategory(if (reminder) Notification.CATEGORY_REMINDER else Notification.CATEGORY_EVENT)
            .setVisibility(Notification.VISIBILITY_PUBLIC)
        poster?.let { builder.setLargeIcon(it) }
        if (what == Say.AT_TIME) builder.addAction(Notification.Action.Builder(Icon.createWithResource(context, R.drawable.ic_play),
            context.getString(R.string.watch_play_on_tv),
            activity(context, Intent(context, MainActivity::class.java).setAction(ACTION_PLAY).putExtra(EXTRA_ID, item.id), item.id, 1)).build())
        if (reminder) builder.addAction(Notification.Action.Builder(Icon.createWithResource(context, R.drawable.ic_schedule),
            context.getString(R.string.watch_postpone),
            activity(context, Intent(context, WatchActivity::class.java).putExtra(EXTRA_ID, item.id), item.id, 2)).build())
        manager.notify(notificationId(item.id), builder.build())
    }

    private fun activity(context: Context, intent: Intent, id: String, which: Int): PendingIntent {
        intent.data = Uri.parse("couchtv-watch://$id/open/$which")   // one for each film and button
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
        return PendingIntent.getActivity(context, 0, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
    }
}

/** A watchlist reminder's alarm. */
class WatchReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != WatchReminders.ACTION_REMIND) return
        val pending = goAsync()
        WatchReminders.fire(context, intent) { pending.finish() }
    }
}

/** The phone restarted, or the app was updated: Android dropped the alarms, so they're set again; the mailbox checks go on. */
class WatchStartup : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED && intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        WatchReminders.scheduleAll(context)
        WatchJob.schedule(context)
    }
}

/**
 * Checks the watchlist's mailbox every quarter of an hour or so (Android picks the moment, and waits longer while the
 * phone lies idle), with the app closed too: films added or moved on the other phones arrive with their notification.
 */
class WatchJob : JobService() {
    companion object {
        private const val ID = 4703

        fun schedule(context: Context) {
            val jobs = context.getSystemService(JobScheduler::class.java)
            if (jobs.getPendingJob(ID) != null) return
            jobs.schedule(JobInfo.Builder(ID, ComponentName(context, WatchJob::class.java))
                .setPeriodic(15 * 60_000L)
                .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY)
                .setPersisted(true)
                .build())
        }
    }

    override fun onStartJob(params: JobParameters): Boolean {
        Watchlist.get(this).sync { jobFinished(params, false) }
        return true
    }

    override fun onStopJob(params: JobParameters) = true
}
