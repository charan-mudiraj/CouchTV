package com.couchtv.remote

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View
import kotlin.math.atan2
import kotlin.math.hypot
import kotlin.math.min

/**
 * A round D-pad: four direction segments around a centre button. Reports press and release, so holding a
 * direction keeps repeating on the TV.
 */
class DpadView @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : View(context, attrs) {

    enum class Part { UP, DOWN, LEFT, RIGHT, CENTER }

    interface Listener {
        fun onPress(part: Part)
        fun onRelease(part: Part)
    }

    var listener: Listener? = null

    /** Text in the middle, e.g. "OK" or "Click". */
    var centerLabel: String = "OK"
        set(value) {
            field = value
            invalidate()
        }

    private var pressed: Part? = null
    private val density = resources.displayMetrics.density

    private val ringPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = context.getColor(R.color.surface) }
    private val ringPressedPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = context.getColor(R.color.surface_pressed) }
    private val centerPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = context.getColor(R.color.surface_raised) }
    private val centerPressedPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = context.getColor(R.color.ink) }
    private val gapPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = context.getColor(R.color.background)
        style = Paint.Style.STROKE
        strokeWidth = 4 * density
    }
    private val chevronPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = context.getColor(R.color.ink)
        style = Paint.Style.STROKE
        strokeWidth = 3.5f * density
        strokeCap = Paint.Cap.ROUND
        strokeJoin = Paint.Join.ROUND
    }
    private val labelPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        textAlign = Paint.Align.CENTER
        isFakeBoldText = true
    }
    private val oval = RectF()
    private val chevron = Path()

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        // Square, as wide as allowed but at most 320 dp.
        val width = if (MeasureSpec.getMode(widthMeasureSpec) == MeasureSpec.UNSPECIFIED) (240 * density).toInt()
                    else MeasureSpec.getSize(widthMeasureSpec)
        val size = min(width, (320 * density).toInt())
        setMeasuredDimension(width, size)
    }

    private val centerX get() = width / 2f
    private val centerY get() = height / 2f
    private val outerRadius get() = min(width, height) / 2f - 2 * density
    private val innerRadius get() = outerRadius * 0.40f

    override fun onDraw(canvas: Canvas) {
        val cx = centerX
        val cy = centerY
        val outer = outerRadius
        val inner = innerRadius
        oval.set(cx - outer, cy - outer, cx + outer, cy + outer)

        canvas.drawCircle(cx, cy, outer, ringPaint)
        val part = pressed
        if (part != null && part != Part.CENTER) canvas.drawArc(oval, startAngle(part), 90f, true, ringPressedPaint)

        // Thin gaps between the four segments.
        val d = outer * 0.7071f
        canvas.drawLine(cx - d, cy - d, cx + d, cy + d, gapPaint)
        canvas.drawLine(cx - d, cy + d, cx + d, cy - d, gapPaint)

        canvas.drawCircle(cx, cy, inner + 3 * density, gapPaint)
        canvas.drawCircle(cx, cy, inner, if (part == Part.CENTER) centerPressedPaint else centerPaint)

        labelPaint.color = context.getColor(if (part == Part.CENTER) R.color.background else R.color.ink)
        labelPaint.textSize = inner * 0.42f
        canvas.drawText(centerLabel, cx, cy - (labelPaint.descent() + labelPaint.ascent()) / 2, labelPaint)

        val r = (inner + outer) / 2
        val s = outer * 0.09f
        drawChevron(canvas, cx, cy - r, 0f, -1f, s)
        drawChevron(canvas, cx, cy + r, 0f, 1f, s)
        drawChevron(canvas, cx - r, cy, -1f, 0f, s)
        drawChevron(canvas, cx + r, cy, 1f, 0f, s)
    }

    /** A ">" shape pointing along (dx, dy). */
    private fun drawChevron(canvas: Canvas, x: Float, y: Float, dx: Float, dy: Float, size: Float) {
        chevron.reset()
        chevron.moveTo(x - dx * size - dy * size * 1.6f, y - dy * size - dx * size * 1.6f)
        chevron.lineTo(x + dx * size, y + dy * size)
        chevron.lineTo(x - dx * size + dy * size * 1.6f, y - dy * size + dx * size * 1.6f)
        canvas.drawPath(chevron, chevronPaint)
    }

    private fun startAngle(part: Part) = when (part) {
        Part.RIGHT -> -45f
        Part.DOWN -> 45f
        Part.LEFT -> 135f
        else -> 225f   // UP
    }

    private fun partAt(x: Float, y: Float): Part? {
        val dx = x - centerX
        val dy = y - centerY
        val distance = hypot(dx, dy)
        if (distance > outerRadius) return null
        if (distance < innerRadius) return Part.CENTER
        val angle = Math.toDegrees(atan2(dy, dx).toDouble())   // 0 = right, 90 = down
        return when {
            angle >= -45 && angle < 45 -> Part.RIGHT
            angle >= 45 && angle < 135 -> Part.DOWN
            angle >= -135 && angle < -45 -> Part.UP
            else -> Part.LEFT
        }
    }

    // A drag that starts on the pad scrolls the page; holding still or tapping presses.
    private var touchedPart: Part? = null
    private val gesture = PressGesture(
        this,
        onPress = {
            touchedPart?.let { part ->
                pressed = part
                listener?.onPress(part)
                invalidate()
            }
        },
        onRelease = {
            pressed?.let { part ->
                pressed = null
                listener?.onRelease(part)
                invalidate()
            }
        },
    )

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(event: MotionEvent): Boolean {
        if (event.actionMasked == MotionEvent.ACTION_DOWN) touchedPart = partAt(event.x, event.y) ?: return false
        return gesture.onTouch(event)
    }
}
