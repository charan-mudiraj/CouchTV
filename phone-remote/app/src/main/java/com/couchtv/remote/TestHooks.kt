package com.couchtv.remote

import android.app.Activity
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.Log
import android.view.InputDevice
import android.view.MotionEvent

/**
 * Testing the app from a PC over adb, on phones that won't let adb tap (Xiaomi's, without a Mi account switch):
 *
 *     adb shell am broadcast -a com.couchtv.remote.TEST --es do "swipe 1800 900 800 900 300"
 *
 * Fingers are made here, in the app, as real touch events in its window (screen pixels), so the views handle them
 * exactly as a finger: `tap x y`, `swipe x1 y1 x2 y2 [ms]`, `click <view id>`; and [more] for the activity's own
 * (pages, the mirror, dialogs). Only the shell or the system can send it: it needs the DUMP permission, which
 * apps don't get.
 */
class TestHooks(private val activity: Activity, private val more: (String) -> Boolean) {
    companion object {
        const val ACTION = "com.couchtv.remote.TEST"
        private const val TAG = "CouchTV test"
    }

    private val main = Handler(Looper.getMainLooper())
    private var registered = false

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            val command = intent.getStringExtra("do")?.trim() ?: return
            Log.i(TAG, command)
            if (!perform(command)) Log.i(TAG, "don't know \"$command\"")
        }
    }

    fun start() {
        if (registered) return
        registered = true
        val filter = IntentFilter(ACTION)
        if (Build.VERSION.SDK_INT >= 33) activity.registerReceiver(receiver, filter, android.Manifest.permission.DUMP, null, Context.RECEIVER_EXPORTED)
        else activity.registerReceiver(receiver, filter, android.Manifest.permission.DUMP, null)
    }

    fun stop() {
        if (!registered) return
        registered = false
        activity.unregisterReceiver(receiver)
    }

    private fun perform(command: String): Boolean {
        val p = command.split(" ").filter { it.isNotEmpty() }
        val n = p.drop(1).map { it.toFloatOrNull() }
        return when (p.firstOrNull()) {
            "tap" -> n.size >= 2 && n[0] != null && n[1] != null && run { swipe(n[0]!!, n[1]!!, n[0]!!, n[1]!!, 60); true }
            "swipe" -> n.size >= 4 && n.take(4).all { it != null } && run {
                swipe(n[0]!!, n[1]!!, n[2]!!, n[3]!!, (n.getOrNull(4) ?: 300f).toLong()); true
            }
            "click" -> p.size >= 2 && run {
                val id = activity.resources.getIdentifier(p[1], "id", activity.packageName)
                id != 0 && activity.findViewById<android.view.View>(id)?.performClick() == true
            }
            else -> more(command)
        }
    }

    /** A finger from (x1, y1) to (x2, y2) over ms, moving every 8 ms as a phone's screen reports it. */
    private fun swipe(x1: Float, y1: Float, x2: Float, y2: Float, ms: Long) {
        val down = SystemClock.uptimeMillis()
        send(MotionEvent.ACTION_DOWN, down, x1, y1)
        val steps = maxOf(1, (ms / 8).toInt())
        for (i in 1..steps) {
            val f = i.toFloat() / steps
            main.postAtTime({ send(MotionEvent.ACTION_MOVE, down, x1 + (x2 - x1) * f, y1 + (y2 - y1) * f) }, down + ms * i / steps)
        }
        main.postAtTime({ send(MotionEvent.ACTION_UP, down, x2, y2) }, down + ms + 8)
    }

    private fun send(action: Int, down: Long, x: Float, y: Float) {
        val event = MotionEvent.obtain(down, SystemClock.uptimeMillis(), action, x, y, 0)
        event.source = InputDevice.SOURCE_TOUCHSCREEN
        activity.dispatchTouchEvent(event)
        event.recycle()
    }
}
