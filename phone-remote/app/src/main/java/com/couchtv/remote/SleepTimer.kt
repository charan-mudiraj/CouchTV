package com.couchtv.remote

import android.app.AlertDialog
import android.content.Context
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.text.format.DateFormat
import android.util.TypedValue
import android.view.Gravity
import android.widget.Button
import android.widget.LinearLayout
import android.widget.NumberPicker
import android.widget.TextView
import android.widget.Toast
import java.util.Date

/**
 * The TV's sleep timer, for any length: hours and minutes on two wheels, with when that'll be. Over Wi-Fi the TV is
 * told the minutes and says back how long is left, which the key on the Apps page shows ("Sleep · 42 min"). By
 * infrared only the TV's own steps work (30, 60, 90, 120 minutes, then off), a step a tap.
 */
class SleepTimer(private val context: Context, private val remote: Remote, private val key: Button) {
    private companion object {
        const val MOST_HOURS = 12
    }

    private val prefs = context.getSharedPreferences("sleep", Context.MODE_PRIVATE)
    private val main = Handler(Looper.getMainLooper())
    private val tick = Runnable { showKey() }

    init {
        remote.wifi.onSleepTimer = { showKey() }
        showKey()
    }

    /** The key was tapped. */
    fun open() {
        if (!remote.wifi.connected || !remote.wifi.sleepTimerKnown) {
            // By infrared (or an older CouchTV): the TV's own steps, one a tap; the TV shows which.
            remote.press(Codes.ADDRESS, Codes.SLEEP_TIMER, hold = false)
            toast(context.getString(if (remote.wifi.connected) R.string.sleep_needs_update else R.string.sleep_by_ir))
            return
        }
        val density = context.resources.displayMetrics.density
        fun dp(v: Int) = (v * density + 0.5f).toInt()
        val total = prefs.getInt("minutes", 60).coerceIn(1, MOST_HOURS * 60 + 59)
        val readout = TextView(context).apply {
            setTextColor(context.getColor(R.color.ink))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 34f)
            typeface = android.graphics.Typeface.create("sans-serif-light", android.graphics.Typeface.NORMAL)
            gravity = Gravity.CENTER
        }
        val at = TextView(context).apply {
            setTextColor(context.getColor(R.color.muted))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
            gravity = Gravity.CENTER
        }
        val hours = NumberPicker(context).apply {
            minValue = 0
            maxValue = MOST_HOURS
            value = total / 60
        }
        val minutes = NumberPicker(context).apply {
            minValue = 0
            maxValue = 59
            value = total % 60
            setFormatter { "%02d".format(it) }
        }
        fun label(text: Int) = TextView(context).apply {
            setText(text)
            setTextColor(context.getColor(R.color.muted))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f)
            setPadding(dp(6), 0, dp(18), 0)
        }
        val wheels = LinearLayout(context).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER
            addView(hours)
            addView(label(R.string.sleep_hours))
            addView(minutes)
            addView(label(R.string.sleep_minutes))
        }
        val now = TextView(context).apply {
            setTextColor(context.getColor(R.color.good))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
            gravity = Gravity.CENTER
            setPadding(0, dp(10), 0, 0)
        }
        val form = LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(12), dp(20), 0)
            addView(readout)
            addView(at)
            addView(wheels, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(8) })
            addView(now)
        }
        val running = remote.wifi.sleepEndsAt > SystemClock.elapsedRealtime()
        val builder = AlertDialog.Builder(context, android.R.style.Theme_Material_Dialog_Alert)
            .setTitle(R.string.sleep_title)
            .setIcon(R.drawable.ic_bedtime)
            .setView(form)
            .setPositiveButton(if (running) R.string.sleep_change else R.string.sleep_start, null)
            .setNegativeButton(R.string.cancel, null)
        if (running) builder.setNeutralButton(R.string.sleep_off) { _, _ ->
            remote.wifi.setSleepTimer(0)
            toast(context.getString(R.string.sleep_turned_off))
        }
        val dialog = builder.show()
        val start = dialog.getButton(AlertDialog.BUTTON_POSITIVE)

        fun chosen() = hours.value * 60 + minutes.value
        fun refresh() {
            val m = chosen()
            readout.text = duration(m)
            at.text = if (m == 0) context.getString(R.string.sleep_pick) else context.getString(R.string.sleep_at, clock(m * 60_000L))
            start.isEnabled = m > 0
            val left = remote.wifi.sleepEndsAt - SystemClock.elapsedRealtime()
            now.text = if (left > 0) context.getString(R.string.sleep_running, duration(minutesOf(left)), clock(left)) else ""
            now.visibility = if (left > 0) android.view.View.VISIBLE else android.view.View.GONE
        }
        hours.setOnValueChangedListener { _, _, _ -> refresh() }
        minutes.setOnValueChangedListener { _, _, _ -> refresh() }
        refresh()
        start.setOnClickListener {
            val m = chosen()
            prefs.edit().putInt("minutes", m).apply()
            remote.wifi.setSleepTimer(m)
            toast(context.getString(R.string.sleep_set, clock(m * 60_000L)))
            dialog.dismiss()
        }
    }

    /** The key: "Sleep timer", or how long is left while it runs (kept up to date every so often). */
    fun showKey() {
        main.removeCallbacks(tick)
        val left = remote.wifi.sleepEndsAt - SystemClock.elapsedRealtime()
        if (left > 0) {
            key.text = context.getString(R.string.sleep_key_running, duration(minutesOf(left)))
            key.setTextColor(context.getColor(R.color.good))
            main.postDelayed(tick, 15_000)
        } else {
            key.setText(R.string.key_sleep_timer)
            key.setTextColor(context.getColor(R.color.ink))
        }
    }

    fun stop() = main.removeCallbacks(tick)

    private fun minutesOf(ms: Long) = ((ms + 59_999) / 60_000).toInt()

    /** "45 min", "2 h", "1 h 30 min". */
    private fun duration(minutes: Int): String = when {
        minutes < 60 -> context.getString(R.string.sleep_mins, minutes)
        minutes % 60 == 0 -> context.getString(R.string.sleep_hrs, minutes / 60)
        else -> context.getString(R.string.sleep_hrs_mins, minutes / 60, minutes % 60)
    }

    /** The time of day this long from now, as the phone shows times ("11:42 PM" or "23:42"). */
    private fun clock(fromNowMs: Long): String = DateFormat.getTimeFormat(context).format(Date(System.currentTimeMillis() + fromNowMs))

    private fun toast(message: String) = Toast.makeText(context, message, Toast.LENGTH_SHORT).show()
}
