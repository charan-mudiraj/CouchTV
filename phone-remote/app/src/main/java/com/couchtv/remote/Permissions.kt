package com.couchtv.remote

import android.Manifest
import android.app.Activity
import android.app.AlarmManager
import android.app.AlertDialog
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.os.SystemClock
import android.provider.Settings

/**
 * Asks for what the app needs and hasn't got, each time it's opened, until it has it: notifications (the watchlist's
 * reminders, what the TV plays, the TV's sound), the microphone (voice search, humming), "Nearby devices" (switching
 * Bluetooth off), reminders on the minute (Android 12; later ones allow them), running in the background (the
 * watchlist's mailbox checks, and reminders on Xiaomi and Oppo phones), and installing its own updates. Android's
 * own questions where there are some; once Android won't ask any more (refused twice), a note with a button to the
 * app's page in Settings instead. "Opened" is a fresh start, or back after a minute or more away; coming back from one
 * of these questions or Settings pages goes on to the next.
 */
class Permissions(private val activity: Activity, private val wantsNearby: () -> Boolean) {
    companion object {
        const val REQUEST = 48
    }

    private val steps = ArrayDeque<() -> Unit>()
    private var away = false        // on one of Android's screens for a step: carry on when back
    private var leftAt = 0L         // when the app was put away (0: not yet)

    fun onStart() {
        if (away) return
        if (leftAt != 0L && SystemClock.elapsedRealtime() - leftAt < 60_000) return
        if (steps.isEmpty()) start()
    }

    fun onStop() {
        if (!away) leftAt = SystemClock.elapsedRealtime()
    }

    fun onResume() {
        if (!away) return
        away = false
        next()
    }

    private fun start() {
        val pkg = activity.packageName
        steps.clear()
        steps.addLast { runtime() }
        if (Build.VERSION.SDK_INT in 31..32 && !activity.getSystemService(AlarmManager::class.java).canScheduleExactAlarms()) steps.addLast {
            note(R.string.perm_alarms_title, R.string.perm_alarms_message, Intent(Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM, Uri.parse("package:$pkg")))
        }
        if (!activity.getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(pkg)) steps.addLast { background() }
        if (!activity.packageManager.canRequestPackageInstalls()) steps.addLast {
            note(R.string.allow_updates_title, R.string.perm_installs_message, Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:$pkg")))
        }
        // A moment after opening, so the remote's on the screen first.
        activity.window.decorView.postDelayed({ if (!activity.isFinishing) next() }, 600)
    }

    private fun next() {
        if (activity.isFinishing || activity.isDestroyed) return steps.clear()
        steps.removeFirstOrNull()?.invoke()
    }

    /** The ones Android asks about itself, all together. */
    private fun runtime() {
        val missing = buildList {
            if (Build.VERSION.SDK_INT >= 33) add(Manifest.permission.POST_NOTIFICATIONS)
            if (activity.packageManager.hasSystemFeature(PackageManager.FEATURE_MICROPHONE)) add(Manifest.permission.RECORD_AUDIO)
            if (Build.VERSION.SDK_INT >= 31 && wantsNearby()) add(Manifest.permission.BLUETOOTH_CONNECT)
        }.filter { activity.checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED }
        if (missing.isEmpty()) next() else activity.requestPermissions(missing.toTypedArray(), REQUEST)
    }

    /** Android's answer: any it won't ask about again go to Settings. True if it was this one's question. */
    fun onResult(requestCode: Int, permissions: Array<out String>, results: IntArray): Boolean {
        if (requestCode != REQUEST) return false
        val refusedForGood = permissions.filterIndexed { i, p ->
            results.getOrNull(i) != PackageManager.PERMISSION_GRANTED && !activity.shouldShowRequestPermissionRationale(p)
        }
        if (refusedForGood.isEmpty()) {
            next()
            return true
        }
        val what = refusedForGood.mapNotNull {
            when (it) {
                Manifest.permission.POST_NOTIFICATIONS -> activity.getString(R.string.perm_notifications)
                Manifest.permission.RECORD_AUDIO -> activity.getString(R.string.perm_microphone)
                Manifest.permission.BLUETOOTH_CONNECT -> activity.getString(R.string.perm_nearby)
                else -> null
            }
        }
        note(activity.getString(R.string.perm_settings_title), activity.getString(R.string.perm_settings_message, what.joinToString("\n")),
            Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:${activity.packageName}")))
        return true
    }

    /** Android's "let it run in the background?" question. */
    private fun background() {
        away = true
        try {
            activity.startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:${activity.packageName}")))
        } catch (e: Exception) {   // a phone without that question
            away = false
            note(R.string.perm_background_title, R.string.perm_background_message, Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
        }
    }

    private fun note(title: Int, message: Int, settings: Intent) = note(activity.getString(title), activity.getString(message), settings)

    /** What it's for, with Open settings (then on to the next when back) or Not now (on to the next now). */
    private fun note(title: String, message: String, settings: Intent) {
        var opening = false
        AlertDialog.Builder(activity, android.R.style.Theme_Material_Dialog_Alert)
            .setTitle(title)
            .setMessage(message)
            .setPositiveButton(R.string.open_settings) { _, _ ->
                opening = true
                away = true
                try {
                    activity.startActivity(settings)
                } catch (e: Exception) {
                    away = false
                    next()
                }
            }
            .setNegativeButton(R.string.not_now, null)
            .setOnDismissListener { if (!opening) next() }
            .show()
    }
}
