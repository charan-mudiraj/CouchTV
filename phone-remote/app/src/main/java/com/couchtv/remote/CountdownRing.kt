package com.couchtv.remote

import android.animation.ValueAnimator
import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.Typeface
import android.os.SystemClock
import android.util.AttributeSet
import android.view.View
import android.view.animation.DecelerateInterpolator
import android.view.animation.OvershootInterpolator
import kotlin.math.ceil
import kotlin.math.min

/**
 * The power-off countdown's dial, as on the TV: a glowing ring running down round the seconds left, each second
 * popping in; cancelled, the ring fills back up green round a tick; at the end, the power symbol.
 */
class CountdownRing @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : View(context, attrs) {
    private enum class State { RUNNING, CANCELLED, DONE }

    private val density = resources.displayMetrics.density
    private val stroke = 12 * density
    private val red = context.getColor(R.color.power)
    private val green = context.getColor(R.color.good)
    private val track = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = stroke
        color = 0x2EFFFFFF
    }
    private val arc = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = stroke
        strokeCap = Paint.Cap.ROUND
    }
    private val number = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = context.getColor(R.color.ink)
        textAlign = Paint.Align.CENTER
        typeface = Typeface.create("sans-serif-light", Typeface.NORMAL)
    }
    private val box = RectF()
    private val tick = context.getDrawable(R.drawable.ic_check)!!.mutate()
    private val power = context.getDrawable(R.drawable.ic_power)!!.mutate()

    private var state = State.RUNNING
    private var started = 0L
    private var total = 1L
    private var shown = -1
    private var pop = 1f          // how big the number (or symbol) is now: it pops in from bigger
    private var fill = 0f         // cancelled: the ring filling back up
    private var popper: ValueAnimator? = null

    /** Counts down totalMs from now. */
    fun start(totalMs: Long) {
        state = State.RUNNING
        started = SystemClock.uptimeMillis()
        total = totalMs.coerceAtLeast(1)
        shown = -1
        paint(red)
        invalidate()
    }

    /** Cancelled: green, filling back up, round a tick. */
    fun cancelled() {
        val from = left()
        state = State.CANCELLED
        paint(green)
        popIn(0.4f)
        ValueAnimator.ofFloat(from, 1f).apply {
            duration = 420
            interpolator = DecelerateInterpolator()
            addUpdateListener {
                fill = it.animatedValue as Float
                invalidate()
            }
            start()
        }
    }

    /** The countdown's run out (or Turn off now): the power symbol. */
    fun done() {
        state = State.DONE
        popIn(0.4f)
        invalidate()
    }

    private fun left(): Float = (1f - (SystemClock.uptimeMillis() - started).toFloat() / total).coerceIn(0f, 1f)

    private fun paint(color: Int) {
        arc.color = color
        arc.setShadowLayer(14 * density, 0f, 0f, color)
    }

    private fun popIn(from: Float) {
        popper?.cancel()
        popper = ValueAnimator.ofFloat(from, 1f).apply {
            duration = 420
            interpolator = OvershootInterpolator(2.2f)
            addUpdateListener {
                pop = it.animatedValue as Float
                invalidate()
            }
            start()
        }
    }

    override fun onDraw(canvas: Canvas) {
        val size = min(width, height).toFloat()
        val inset = stroke / 2 + 16 * density   // room for the glow
        box.set((width - size) / 2 + inset, (height - size) / 2 + inset, (width + size) / 2 - inset, (height + size) / 2 - inset)
        canvas.drawOval(box, track)
        val sweep = when (state) {
            State.RUNNING -> left()
            State.CANCELLED -> fill
            State.DONE -> 0f
        }
        if (sweep > 0f) canvas.drawArc(box, -90f, 360f * sweep, false, arc)
        val cx = box.centerX()
        val cy = box.centerY()
        when (state) {
            State.RUNNING -> {
                val seconds = ceil((1f - (SystemClock.uptimeMillis() - started).toFloat() / total) * total / 1000f).toInt().coerceAtLeast(1)
                if (seconds != shown) {
                    shown = seconds
                    popIn(1.45f)
                }
                number.textSize = box.height() * 0.46f * pop
                canvas.drawText(seconds.toString(), cx, cy - (number.descent() + number.ascent()) / 2, number)
                postInvalidateOnAnimation()
            }
            State.CANCELLED -> symbol(canvas, tick, green, cx, cy)
            State.DONE -> symbol(canvas, power, context.getColor(R.color.ink), cx, cy)
        }
    }

    private fun symbol(canvas: Canvas, drawable: android.graphics.drawable.Drawable, color: Int, cx: Float, cy: Float) {
        val half = (box.height() * 0.22f * pop).toInt()
        drawable.setTint(color)
        drawable.setBounds((cx - half).toInt(), (cy - half).toInt(), (cx + half).toInt(), (cy + half).toInt())
        drawable.draw(canvas)
    }
}
