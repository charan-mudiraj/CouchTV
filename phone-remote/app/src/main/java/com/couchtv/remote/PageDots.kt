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

/** Which page is showing, as dots under the pages (the current one longer and brighter). Tap a dot to go there. */
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
            describe()
            invalidate()
        }

    /** Called with the page tapped. */
    var onPick: ((Int) -> Unit)? = null

    private val density = resources.displayMetrics.density
    private val spacing = 24 * density       // between dot centres: room for a fingertip
    private val dot = 7 * density
    private val activeLength = 22 * density
    private val dim = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = context.getColor(R.color.surface_pressed) }
    private val lit = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = context.getColor(R.color.ink) }
    private val shape = RectF()

    private fun centreOf(i: Int) = width / 2f + (i - (count - 1) / 2f) * spacing

    override fun onDraw(canvas: Canvas) {
        val y = height / 2f
        for (i in 0 until count) {
            val half = (if (i == current) activeLength else dot) / 2
            val x = centreOf(i)
            shape.set(x - half, y - dot / 2, x + half, y + dot / 2)
            canvas.drawRoundRect(shape, dot / 2, dot / 2, if (i == current) lit else dim)
        }
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
