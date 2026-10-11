package com.couchtv.remote

import android.annotation.SuppressLint
import android.content.Context
import android.os.Build
import android.os.SystemClock
import android.os.VibrationEffect
import android.os.Vibrator
import android.os.VibratorManager
import android.view.MotionEvent
import android.view.View

/**
 * The feel of a real key under the finger: a crisp click as it goes down and, on phones with a precise vibration
 * motor, a lighter tick as it comes back up. Played straight on the vibrator, so keys click even where Android's
 * own touch vibration is off.
 */
object Haptics {
    private const val GAP_MS = 80L   // a tap's tick comes this long after its click, so the two don't merge
    private var vibrator: Vibrator? = null
    private var pressedAt = 0L

    fun press(view: View) {
        pressedAt = SystemClock.uptimeMillis()
        val v = vibrator(view.context) ?: return
        play(v, if (Build.VERSION.SDK_INT >= 29) VibrationEffect.createPredefined(VibrationEffect.EFFECT_CLICK)
                else VibrationEffect.createOneShot(18, VibrationEffect.DEFAULT_AMPLITUDE))
    }

    fun release(view: View) {
        val v = vibrator(view.context) ?: return
        // A plain buzzing motor can't do a light tick (it would feel like a second press): just the click then.
        if (Build.VERSION.SDK_INT < 29 || !v.hasAmplitudeControl()) return
        val tick = VibrationEffect.createPredefined(VibrationEffect.EFFECT_TICK)
        val wait = GAP_MS - (SystemClock.uptimeMillis() - pressedAt)
        if (wait > 0) view.postDelayed({ play(v, tick) }, wait) else play(v, tick)
    }

    /** For keys that act on a tap (not held): click down and tick up, leaving the tap itself to the key. */
    @SuppressLint("ClickableViewAccessibility")
    fun attach(view: View) {
        view.setOnTouchListener { v, event ->
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> press(v)
                MotionEvent.ACTION_UP -> release(v)
            }
            false
        }
    }

    private fun play(v: Vibrator, effect: VibrationEffect) {
        runCatching { v.vibrate(effect) }
    }

    private fun vibrator(context: Context): Vibrator? {
        val v = vibrator ?: (
            if (Build.VERSION.SDK_INT >= 31) context.getSystemService(VibratorManager::class.java)?.defaultVibrator
            else context.getSystemService(Vibrator::class.java)
        ).also { vibrator = it }
        return v?.takeIf { it.hasVibrator() }
    }
}
