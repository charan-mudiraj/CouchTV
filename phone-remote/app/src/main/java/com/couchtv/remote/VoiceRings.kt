package com.couchtv.remote

import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.os.SystemClock
import android.util.AttributeSet
import android.view.View
import kotlin.math.min

/**
 * The rings round the mic while voice search listens. They swell with your voice (smoothly, rising quickly and
 * settling slowly, rather than jumping with every reading), a slow ripple goes out while it waits for you, and they
 * fade away when it stops. All drawn inside this view's own square, behind the mic, so nothing round it moves or
 * gets covered.
 */
class VoiceRings @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : View(context, attrs) {
    /** How loud the voice is now, 0 to 1. */
    var level = 0f
        set(value) {
            field = value.coerceIn(0f, 1f)
            animateRings()
        }

    /** Listening: the ripple goes out. */
    var active = false
        set(value) {
            field = value
            if (!value) level = 0f
            animateRings()
        }

    /** How big the mic in the middle is, as a share of this view's width. */
    var micShare = 0.55f

    private var shown = 0f         // the level as drawn
    private var ripple = 0f        // 0 to 1: how far out the ripple has gone
    private var presence = 0f      // 0 to 1: the rings fading in while listening, out after
    private var last = 0L
    private val colour = context.getColor(R.color.voice)
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val ripplePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = 2 * resources.displayMetrics.density
    }

    private val frame = object : Runnable {
        override fun run() {
            val now = SystemClock.uptimeMillis()
            val dt = (now - last).coerceIn(1L, 50L).toFloat()
            last = now
            shown += (level - shown) * (if (level > shown) 0.35f else 0.10f)
            presence += ((if (active) 1f else 0f) - presence) * 0.15f
            ripple = (ripple + dt / 1600f) % 1f
            invalidate()
            if (active || presence > 0.01f || shown > 0.01f) postOnAnimation(this) else {
                presence = 0f
                shown = 0f
            }
        }
    }

    private fun animateRings() {
        removeCallbacks(frame)
        last = SystemClock.uptimeMillis()
        postOnAnimation(frame)
    }

    override fun onDraw(canvas: Canvas) {
        val cx = width / 2f
        val cy = height / 2f
        val most = min(width, height) / 2f
        val mic = width * micShare / 2f
        val room = most - mic
        if (presence <= 0.005f || room <= 0) return
        // A ripple going out from the mic every second and a half, fading as it goes.
        ripplePaint.color = colour
        ripplePaint.alpha = (90 * (1 - ripple) * presence).toInt()
        canvas.drawCircle(cx, cy, mic + room * ripple, ripplePaint)
        // Two soft rings that swell with the voice: the outer one further and fainter.
        paint.color = colour
        paint.alpha = (46 * presence).toInt()
        canvas.drawCircle(cx, cy, mic + room * (0.12f + 0.88f * shown), paint)
        paint.alpha = (80 * presence).toInt()
        canvas.drawCircle(cx, cy, mic + room * (0.06f + 0.5f * shown), paint)
    }

    override fun onDetachedFromWindow() {
        removeCallbacks(frame)
        super.onDetachedFromWindow()
    }
}
