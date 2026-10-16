package com.couchtv.remote

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View
import kotlin.math.abs

/**
 * Which page is showing, as dots under the pages (the current one longer and brighter), following the pages as they
 * slide: the lit dot stretches across to the next and shrinks back. Tap a dot to go there.
 */
class PageDots @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : View(context, attrs) {
    var count = 0
        set(value) {
            field = value
            describe()
            invalidate()
        }

    var current = 0
        set(value) {
            field = value
            if (!sliding) position = value.toFloat()
            describe()
            invalidate()
        }

    /** Where the pages are while they slide (1.5: halfway from the second to the third). */
    var position = 0f
        set(value) {
            field = value
            sliding = abs(value - Math.round(value)) > 0.001f
            invalidate()
        }

    private var sliding = false

    /** Called with the page tapped. */
    var onPick: ((Int) -> Unit)? = null

    private val density = resources.displayMetrics.density
    private val spacing = 24 * density       // between dot centres: room for a fingertip
    private val dot = 7 * density
    private val activeLength = 22 * density
    private val dimColor = context.getColor(R.color.surface_pressed)
    private val litColor = context.getColor(R.color.ink)
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val shape = RectF()

    private fun centreOf(i: Int) = width / 2f + (i - (count - 1) / 2f) * spacing

    override fun onDraw(canvas: Canvas) {
        val y = height / 2f
        for (i in 0 until count) {
            val lit = (1f - abs(position - i)).coerceIn(0f, 1f)   // 1: this page is showing, 0: it's away
            val half = (dot + (activeLength - dot) * lit) / 2
            val x = centreOf(i)
            shape.set(x - half, y - dot / 2, x + half, y + dot / 2)
            paint.color = blend(dimColor, litColor, lit)
            canvas.drawRoundRect(shape, dot / 2, dot / 2, paint)
        }
    }

    private fun blend(from: Int, to: Int, t: Float): Int {
        fun mix(shift: Int) = (((from shr shift) and 0xFF) + ((((to shr shift) and 0xFF) - ((from shr shift) and 0xFF)) * t)).toInt() shl shift
        return mix(24) or mix(16) or mix(8) or mix(0)
    }

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(event: MotionEvent): Boolean {
        if (count == 0) return false
        if (event.actionMasked == MotionEvent.ACTION_UP) {
            val nearest = (0 until count).minByOrNull { abs(centreOf(it) - event.x) } ?: return true
            if (abs(centreOf(nearest) - event.x) <= spacing) onPick?.invoke(nearest)
        }
        return true
    }

    private fun describe() {
        contentDescription = context.getString(R.string.page_of, current + 1, count)
    }
}
