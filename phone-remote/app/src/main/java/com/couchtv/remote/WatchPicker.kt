package com.couchtv.remote

import android.app.Activity
import android.app.AlertDialog
import android.content.Context
import android.os.Bundle
import android.text.format.DateFormat
import android.util.TypedValue
import android.view.Gravity
import android.widget.LinearLayout
import android.widget.NumberPicker
import android.widget.TextView
import android.widget.Toast
import java.text.DateFormatSymbols
import java.text.SimpleDateFormat
import java.util.Calendar
import java.util.Date
import java.util.Locale

/** Watch times in words, as the phone shows dates and times: "Today, 9:00 PM", "Tomorrow, 21:00", "Sat 18 Oct, 9:00 PM". */
object WatchTime {
    fun clock(context: Context, at: Long): String = DateFormat.getTimeFormat(context).format(Date(at))

    fun describe(context: Context, at: Long, now: Long = System.currentTimeMillis()) = day(context, at, now) + ", " + clock(context, at)

    /** "Today", "Tomorrow", "Yesterday", else "Sat 18 Oct". */
    fun day(context: Context, at: Long, now: Long = System.currentTimeMillis()): String = when (daysBetween(now, at)) {
        0 -> context.getString(R.string.watch_today)
        1 -> context.getString(R.string.watch_tomorrow)
        -1 -> context.getString(R.string.watch_yesterday)
        else -> SimpleDateFormat(DateFormat.getBestDateTimePattern(Locale.getDefault(), "EEEdMMM"), Locale.getDefault()).format(Date(at))
    }

    /** Calendar days from [from] to [to] (0: the same day). */
    fun daysBetween(from: Long, to: Long): Int {
        fun start(ms: Long) = Calendar.getInstance().apply {
            timeInMillis = ms
            set(Calendar.HOUR_OF_DAY, 12)   // midday, so a change to or from summer time can't make it a day out
            set(Calendar.MINUTE, 0)
            set(Calendar.SECOND, 0)
            set(Calendar.MILLISECOND, 0)
        }.timeInMillis
        return Math.round((start(to) - start(from)) / (24.0 * 3600 * 1000)).toInt()
    }
}

/**
 * When to watch a film: the day, hour and minute on wheels (as the sleep timer's), any time from now to two months
 * ahead, with the time in words over them. Adds the film to the watchlist, moves it (Postpone, from a reminder), or
 * takes it off.
 */
class WatchPicker(private val activity: Activity) {
    private companion object {
        const val DAYS = 60
    }

    /** [postpone]: from a reminder, so it starts an hour later than the film's time. [closed]: however it ends. */
    fun show(film: Suggestion, postpone: Boolean = false, closed: (() -> Unit)? = null) {
        val watchlist = Watchlist.get(activity)
        val existing = watchlist.find(film)
        val now = System.currentTimeMillis()
        val start = when {
            existing != null && postpone -> roundUp(maxOf(existing.watchAt, now) + WatchItem.HOUR)
            existing != null && existing.watchAt > now -> existing.watchAt
            else -> tonight(now)
        }
        val density = activity.resources.displayMetrics.density
        fun dp(v: Int) = (v * density + 0.5f).toInt()
        val twelve = !DateFormat.is24HourFormat(activity)

        val readout = TextView(activity).apply {
            setTextColor(activity.getColor(R.color.ink))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 26f)
            typeface = android.graphics.Typeface.create("sans-serif-light", android.graphics.Typeface.NORMAL)
            gravity = Gravity.CENTER
        }
        val note = TextView(activity).apply {
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
            gravity = Gravity.CENTER
        }
        val day = NumberPicker(activity).apply {
            minValue = 0
            maxValue = DAYS - 1
            wrapSelectorWheel = false
            displayedValues = Array(DAYS) { i -> WatchTime.day(activity, now + i * WatchItem.DAY, now) }
            descendantFocusability = NumberPicker.FOCUS_BLOCK_DESCENDANTS
        }
        val hour = NumberPicker(activity).apply {
            minValue = if (twelve) 1 else 0
            maxValue = if (twelve) 12 else 23
            if (!twelve) setFormatter { "%02d".format(it) }
            descendantFocusability = NumberPicker.FOCUS_BLOCK_DESCENDANTS
        }
        val minute = NumberPicker(activity).apply {
            minValue = 0
            maxValue = 59
            setFormatter { "%02d".format(it) }
            descendantFocusability = NumberPicker.FOCUS_BLOCK_DESCENDANTS
        }
        val half = NumberPicker(activity).apply {
            minValue = 0
            maxValue = 1
            displayedValues = DateFormatSymbols.getInstance().amPmStrings
            descendantFocusability = NumberPicker.FOCUS_BLOCK_DESCENDANTS
        }
        // The wheels where [start] is.
        Calendar.getInstance().apply {
            timeInMillis = start
            day.value = WatchTime.daysBetween(now, start).coerceIn(0, DAYS - 1)
            val h = get(Calendar.HOUR_OF_DAY)
            hour.value = if (twelve) (h + 11) % 12 + 1 else h
            half.value = if (h >= 12) 1 else 0
            minute.value = get(Calendar.MINUTE)
        }
        val wheels = LinearLayout(activity).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER
            addView(day, LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply { marginEnd = dp(14) })
            addView(hour)
            addView(TextView(activity).apply {
                text = ":"
                setTextColor(activity.getColor(R.color.muted))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 20f)
                setPadding(dp(4), 0, dp(4), 0)
            })
            addView(minute)
            if (twelve) addView(half, LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply { marginStart = dp(10) })
        }
        val form = LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(12), dp(16), 0)
            addView(readout)
            addView(note)
            addView(wheels, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(8) })
        }
        val builder = AlertDialog.Builder(activity, android.R.style.Theme_Material_Dialog_Alert)
            .setTitle(activity.getString(if (postpone) R.string.watch_postpone_title else R.string.watch_title, film.title))
            .setIcon(R.drawable.ic_schedule)
            .setView(form)
            .setPositiveButton(when {
                postpone -> R.string.watch_postpone
                existing != null -> R.string.save
                else -> R.string.watch_add
            }, null)
            .setNegativeButton(R.string.cancel, null)
        if (existing != null) builder.setNeutralButton(R.string.watch_remove) { _, _ ->
            watchlist.remove(existing.id)
            toast(activity.getString(R.string.watch_removed, film.title))
        }
        val dialog = builder.create()
        dialog.setOnDismissListener { closed?.invoke() }
        dialog.show()
        val save = dialog.getButton(AlertDialog.BUTTON_POSITIVE)

        fun chosen(): Long = Calendar.getInstance().apply {
            timeInMillis = now
            add(Calendar.DAY_OF_YEAR, day.value)
            set(Calendar.HOUR_OF_DAY, if (twelve) hour.value % 12 + half.value * 12 else hour.value)
            set(Calendar.MINUTE, minute.value)
            set(Calendar.SECOND, 0)
            set(Calendar.MILLISECOND, 0)
        }.timeInMillis

        fun refresh() {
            val at = chosen()
            val left = at - System.currentTimeMillis()
            readout.text = WatchTime.describe(activity, at)
            note.text = if (left <= 0) activity.getString(R.string.watch_gone) else activity.getString(R.string.watch_in, untilText(left))
            note.setTextColor(activity.getColor(if (left <= 0) R.color.warn else R.color.muted))
            save.isEnabled = left > 0
        }
        for (wheel in listOf(day, hour, minute, half)) wheel.setOnValueChangedListener { _, _, _ -> refresh() }
        refresh()
        save.setOnClickListener {
            val at = chosen()
            if (at <= System.currentTimeMillis()) return@setOnClickListener refresh()
            watchlist.set(film, at)
            val words = WatchTime.describe(activity, at)
            toast(when {
                postpone -> activity.getString(R.string.watch_moved_toast, film.title, words)
                existing != null -> activity.getString(R.string.watch_moved_toast, film.title, words)
                else -> activity.getString(R.string.watch_added_toast, film.title, words)
            })
            dialog.dismiss()
        }
    }

    /** "3 h 20 min", "2 days", "45 min". */
    private fun untilText(ms: Long): String {
        val minutes = ((ms + 59_999) / 60_000).toInt()
        return when {
            minutes < 60 -> activity.getString(R.string.sleep_mins, minutes)
            minutes < 24 * 60 -> if (minutes % 60 == 0) activity.getString(R.string.sleep_hrs, minutes / 60)
                else activity.getString(R.string.sleep_hrs_mins, minutes / 60, minutes % 60)
            else -> activity.resources.getQuantityString(R.plurals.watch_days, (minutes + 12 * 60) / (24 * 60), (minutes + 12 * 60) / (24 * 60))
        }
    }

    /** 9 PM today, or tomorrow if that's less than half an hour away. */
    private fun tonight(now: Long): Long {
        val at = Calendar.getInstance().apply {
            timeInMillis = now
            set(Calendar.HOUR_OF_DAY, 21)
            set(Calendar.MINUTE, 0)
            set(Calendar.SECOND, 0)
            set(Calendar.MILLISECOND, 0)
        }
        if (at.timeInMillis - now < 30 * 60_000) at.add(Calendar.DAY_OF_YEAR, 1)
        return at.timeInMillis
    }

    /** Up to the next 5 minutes. */
    private fun roundUp(ms: Long): Long {
        val step = 5 * 60_000L
        return (ms + step - 1) / step * step
    }

    private fun toast(message: String) = Toast.makeText(activity, message, Toast.LENGTH_LONG).show()
}

/**
 * Postpone, from a watchlist reminder: just the picker, over whatever's on the screen (not the remote, which would
 * connect to the TV and switch Bluetooth off), gone once it's answered.
 */
class WatchActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val item = intent.getStringExtra(WatchReminders.EXTRA_ID)?.let { Watchlist.get(this).find(it) }
        if (item == null || !item.live(System.currentTimeMillis())) {
            Toast.makeText(this, R.string.watch_not_listed, Toast.LENGTH_SHORT).show()
            finish()
            return
        }
        WatchPicker(this).show(item.film, postpone = true) { finish() }
    }
}
