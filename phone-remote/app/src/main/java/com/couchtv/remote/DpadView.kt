package com.couchtv.remote

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.graphics.Shader
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View
import kotlin.math.atan2
import kotlin.math.hypot
import kotlin.math.min

/**
 * A round D-pad: four direction segments around a centre button. Reports press and release, so holding a
 * direction keeps repeating on the TV. Drawn like a real one, matching the keys (key_bg): a raised ring with its side
 * showing below and a light rim on top, a pressed segment that darkens and sinks, and a centre button standing a
 * little proud of the ring that goes down when pressed.
 */
class DpadView @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : View(context, attrs) {

    enum class Part { UP, DOWN, LEFT, RIGHT, CENTER }

    private companion object {
        const val MOST_DP = 320f   // the widest the pad gets
    }

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

    private var pressed: Part? = null   // held down now (what the TV is told)
    private var shown: Part? = null     // drawn down while the view is pressed (a quick tap stays down a moment)
    private val density = resources.displayMetrics.density
    private val depth = 5 * density     // how far the ring's side shows below it
    private val travel = 3 * density    // how far a key goes down

    private fun paint(color: Int) = Paint(Paint.ANTI_ALIAS_FLAG).apply { this.color = context.getColor(color) }

    private val edgePaint = paint(R.color.key_edge)
    private val rimPaint = paint(R.color.key_rim)
    private val pressedRimPaint = paint(R.color.key_pressed_rim)
    private val centerRimPaint = paint(R.color.key_raised_rim)
    private val facePaint = Paint(Paint.ANTI_ALIAS_FLAG)            // gradients set in onSizeChanged
    private val pressedFacePaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val centerFacePaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val centerPressedFacePaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val shadowPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0x66000000 }
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
        color = context.getColor(R.color.ink)
        textAlign = Paint.Align.CENTER
        isFakeBoldText = true
    }
    private val oval = RectF()
    private val chevron = Path()

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        // Square, as wide as allowed but at most 320 dp; given a height (the space left on its page), it fills that
        // and the pad sits in the middle, as big as fits.
        val width = if (MeasureSpec.getMode(widthMeasureSpec) == MeasureSpec.UNSPECIFIED) (240 * density).toInt()
                    else MeasureSpec.getSize(widthMeasureSpec)
        val size = min(width, (MOST_DP * density).toInt())
        val height = when (MeasureSpec.getMode(heightMeasureSpec)) {
            MeasureSpec.EXACTLY -> MeasureSpec.getSize(heightMeasureSpec)
            MeasureSpec.AT_MOST -> min(size, MeasureSpec.getSize(heightMeasureSpec))
            else -> size
        }
        setMeasuredDimension(width, height)
    }

    // The ring's top face is centred at (centerX, centerY); its side shows below, down to centerY + depth.
    private val centerX get() = width / 2f
    private val centerY get() = (height - depth) / 2f
    private val outerRadius get() = min(min(width.toFloat(), height - depth) / 2f, MOST_DP / 2 * density) - 2 * density
    private val innerRadius get() = outerRadius * 0.40f
    private val centerTop get() = centerY - 1 * density   // the centre button stands a little above the ring

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        val outer = outerRadius
        val inner = innerRadius
        facePaint.shader = gradient(centerY - outer, centerY + outer, R.color.key_top, R.color.key_bottom)
        pressedFacePaint.shader = gradient(centerY - outer, centerY + outer, R.color.key_pressed_top, R.color.key_pressed_bottom)
        centerFacePaint.shader = gradient(centerTop - inner, centerTop + inner, R.color.key_raised_top, R.color.key_raised_bottom)
        centerPressedFacePaint.shader = gradient(centerTop - inner, centerTop + inner, R.color.key_pressed_top, R.color.key_pressed_bottom)
    }

    private fun gradient(top: Float, bottom: Float, from: Int, to: Int) =
        LinearGradient(0f, top, 0f, bottom, context.getColor(from), context.getColor(to), Shader.TileMode.CLAMP)

    override fun setPressed(pressed: Boolean) {
        super.setPressed(pressed)
        invalidate()
    }

    override fun onDraw(canvas: Canvas) {
        val cx = centerX
        val cy = centerY
        val outer = outerRadius
        val inner = innerRadius
        val down = if (isPressed) shown else null
        val face = cy + 1 * density

        // The ring: its side, a light rim along the top, then the face.
        canvas.drawCircle(cx, cy + depth, outer, edgePaint)
        canvas.drawCircle(cx, cy, outer, rimPaint)
        canvas.drawCircle(cx, face, outer, facePaint)

        // A pressed segment: darker, lit from below as if dished in, with a shadow along its top.
        oval.set(cx - outer, face - outer, cx + outer, face + outer)
        if (down != null && down != Part.CENTER) {
            canvas.drawArc(oval, startAngle(down), 90f, true, pressedRimPaint)
            oval.offset(0f, 1.5f * density)
            canvas.drawArc(oval, startAngle(down), 90f, true, pressedFacePaint)
            oval.offset(0f, -1.5f * density)
        }

        // Thin gaps between the four segments, and around the centre button.
        val d = outer * 0.7071f
        canvas.drawLine(cx - d, face - d, cx + d, face + d, gapPaint)
        canvas.drawLine(cx - d, face + d, cx + d, face - d, gapPaint)
        canvas.drawCircle(cx, face, inner + 3 * density, gapPaint)

        // The centre button, standing proud of the ring: its shadow on the ring, its side, rim and face.
        val centerDown = down == Part.CENTER
        val top = centerTop + if (centerDown) travel else 0f
        canvas.drawCircle(cx, centerTop + 5 * density, inner, shadowPaint)
        canvas.drawCircle(cx, centerTop + 4 * density, inner, edgePaint)
        canvas.drawCircle(cx, top, inner, if (centerDown) pressedRimPaint else centerRimPaint)
        canvas.drawCircle(cx, top + 1 * density, inner, if (centerDown) centerPressedFacePaint else centerFacePaint)
        labelPaint.textSize = inner * 0.42f
        canvas.drawText(centerLabel, cx, top + 1 * density - (labelPaint.descent() + labelPaint.ascent()) / 2, labelPaint)

        val r = (inner + outer) / 2
        val s = outer * 0.09f
        fun sink(part: Part) = if (down == part) 1.5f * density else 0f
        drawChevron(canvas, cx, face - r + sink(Part.UP), 0f, -1f, s)
        drawChevron(canvas, cx, face + r + sink(Part.DOWN), 0f, 1f, s)
        drawChevron(canvas, cx - r, face + sink(Part.LEFT), -1f, 0f, s)
        drawChevron(canvas, cx + r, face + sink(Part.RIGHT), 1f, 0f, s)
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

    // A swipe that starts on the pad turns the page; holding still or tapping presses (PressGesture, which also
    // shows the view pressed and clicks under the finger).
    private var touchedPart: Part? = null
    private val gesture = PressGesture(
        this,
        onPress = {
            touchedPart?.let { part ->
                pressed = part
                shown = part
                listener?.onPress(part)
                invalidate()
            }
        },
        onRelease = {
            pressed?.let { part ->
                pressed = null
                listener?.onRelease(part)
            }
        },
    )

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(event: MotionEvent): Boolean {
        if (event.actionMasked == MotionEvent.ACTION_DOWN) touchedPart = partAt(event.x, event.y) ?: return false
        return gesture.onTouch(event)
    }
}
