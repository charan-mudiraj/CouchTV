package com.couchtv.remote

import android.animation.ObjectAnimator
import android.animation.ValueAnimator
import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.Typeface
import android.os.SystemClock
import android.util.AttributeSet
import android.util.TypedValue
import android.widget.ImageButton
import kotlin.math.ceil

/**
 * The power key, showing when the TV will listen to it again. Right after switching, the TV ignores its own power
 * button for about 15 seconds (the receiver box presses that button), so for that long a ring around the key runs
 * down, with the seconds left in the middle, and a press meanwhile just shakes the key ([shake]).
 */
class PowerButton @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : ImageButton(context, attrs) {
    /** Called once the wait is over. */
    var onReady: (() -> Unit)? = null

    private var until = 0L
    private var total = 1L
    private val density = resources.displayMetrics.density
    private val ring = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = 3 * density
        strokeCap = Paint.Cap.ROUND
        color = 0xFFFFFFFF.toInt()
    }
    private val track = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = 3 * density
        color = 0x40FFFFFF
    }
    private val number = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = 0xFFFFFFFF.toInt()
        textAlign = Paint.Align.CENTER
        typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        textSize = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_SP, 16f, resources.displayMetrics)
    }
    private val face = RectF()
    private var iconBack: ValueAnimator? = null
    private var waiting = false

    /** Still waiting for the TV. */
    val busy: Boolean get() = SystemClock.elapsedRealtime() < until

    /** Whole seconds left to wait (0 when it isn't). */
    val secondsLeft: Int get() = ceil((until - SystemClock.elapsedRealtime()).coerceAtLeast(0) / 1000.0).toInt()

    /** Waits until [untilMs] (SystemClock.elapsedRealtime), of [totalMs] in all; 0 stops waiting. */
    fun waitUntil(untilMs: Long, totalMs: Long) {
        until = untilMs
        total = totalMs.coerceAtLeast(1)
        if (busy) {
            waiting = true
            iconBack?.cancel()
            imageAlpha = 0   // the seconds take the icon's place
        } else if (waiting) {
            waiting = false
            ready()
        }
        invalidate()
    }

    /** A press while waiting: a shake, like a "no". */
    fun shake() {
        val d = 7 * density
        ObjectAnimator.ofFloat(this, "translationX", 0f, d, -d, d * 0.7f, -d * 0.7f, d * 0.3f, 0f).setDuration(380).start()
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        val left = until - SystemClock.elapsedRealtime()
        if (left <= 0) {
            if (waiting) {
                waiting = false
                ready()
            }
            return
        }
        // The key's face (power_bg): 2 dp in at the sides, its side showing below; the ring just inside its edge.
        val inset = 6 * density
        face.set(2 * density + inset, inset, width - 2 * density - inset, height - 4 * density - inset)
        canvas.drawArc(face, 0f, 360f, false, track)
        canvas.drawArc(face, -90f, 360f * left / total, false, ring)
        canvas.drawText(secondsLeft.toString(), face.centerX(), face.centerY() - (number.descent() + number.ascent()) / 2, number)
        postInvalidateOnAnimation()
    }

    private fun ready() {
        iconBack?.cancel()
        iconBack = ValueAnimator.ofInt(imageAlpha, 255).apply {
            duration = 220
            addUpdateListener { imageAlpha = it.animatedValue as Int }
            start()
        }
        onReady?.invoke()
    }
}
