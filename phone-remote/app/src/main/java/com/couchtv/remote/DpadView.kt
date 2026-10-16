package com.couchtv.remote

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.CornerPathEffect
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.graphics.Shader
import android.graphics.drawable.Drawable
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View
import kotlin.math.atan2
import kotlin.math.hypot
import kotlin.math.min

/**
 * A round D-pad: four direction segments around a centre button, and (with [corners]) a key in each corner of the
 * square round it, shaped like a right-angled triangle whose long side curves along the pad. Reports press and
 * release, so holding a key keeps repeating on the TV. Drawn like a real one, matching the keys (key_bg): raised,
 * with their side showing below and a light rim on top; a pressed key darkens and sinks, and the centre button stands
 * a little proud of the ring.
 */
class DpadView @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : View(context, attrs) {

    enum class Part { UP, DOWN, LEFT, RIGHT, CENTER }

    /** A key in one corner of the square round the pad: 0 top left, 1 top right, 2 bottom left, 3 bottom right. */
    class CornerKey(val name: String, val corner: Int, val icon: Int)

    private companion object {
        const val MOST_DP = 320f          // the widest the pad gets on its own...
        const val MOST_CORNERS_DP = 440f  // ...and with its corner keys
    }

    interface Listener {
        fun onPress(part: Part)
        fun onRelease(part: Part)
    }

    var listener: Listener? = null

    /** The keys in the corners round the pad; none by default. */
    var corners: List<CornerKey> = emptyList()
        set(value) {
            field = value
            icons.clear()
            requestLayout()
            invalidate()
        }

    /** A corner key pressed (true) or let go. */
    var onCornerKey: ((CornerKey, Boolean) -> Unit)? = null

    /** Text in the middle, e.g. "OK" or "Click". */
    var centerLabel: String = "OK"
        set(value) {
            field = value
            invalidate()
        }

    private var pressed: Part? = null   // held down now (what the TV is told)
    private var shown: Part? = null     // drawn down while the view is pressed (a quick tap stays down a moment)
    private var pressedKey: CornerKey? = null
    private var shownKey: CornerKey? = null
    private val density = resources.displayMetrics.density
    private val depth = 5 * density     // how far the ring's side shows below it
    private val travel = 3 * density    // how far a key goes down
    private val icons = HashMap<Int, Drawable>()

    private fun paint(color: Int) = Paint(Paint.ANTI_ALIAS_FLAG).apply { this.color = context.getColor(color) }

    private val edgePaint = paint(R.color.key_edge)
    private val rimPaint = paint(R.color.key_rim)
    private val pressedRimPaint = paint(R.color.key_pressed_rim)
    private val centerRimPaint = paint(R.color.key_raised_rim)
    private val facePaint = Paint(Paint.ANTI_ALIAS_FLAG)            // gradients set in onSizeChanged
    private val pressedFacePaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val centerFacePaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val centerPressedFacePaint = Paint(Paint.ANTI_ALIAS_FLAG)
    // The corner keys: the same, with their sharp points rounded.
    private val rounding = CornerPathEffect(12 * density)
    private val keyEdgePaint = paint(R.color.key_edge).apply { pathEffect = rounding }
    private val keyRimPaint = paint(R.color.key_rim).apply { pathEffect = rounding }
    private val keyPressedRimPaint = paint(R.color.key_pressed_rim).apply { pathEffect = rounding }
    private val keyFacePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { pathEffect = rounding }
    private val keyPressedFacePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { pathEffect = rounding }
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
    private val pieces = arrayOfNulls<Path>(4)   // the corner keys' outlines, made in onSizeChanged
    private val gap = 7 * density                // between the keys, and between them and the pad

    private val most: Float get() = if (corners.isEmpty()) MOST_DP else MOST_CORNERS_DP

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        // Square, as wide as allowed but at most 320 dp (440 with corner keys); given a height (the space left on its page),
        // it fills that and the pad sits in the middle, as big as fits.
        val width = if (MeasureSpec.getMode(widthMeasureSpec) == MeasureSpec.UNSPECIFIED) (240 * density).toInt()
                    else MeasureSpec.getSize(widthMeasureSpec)
        val size = min(width, (most * density).toInt())
        val height = when (MeasureSpec.getMode(heightMeasureSpec)) {
            MeasureSpec.EXACTLY -> MeasureSpec.getSize(heightMeasureSpec)
            MeasureSpec.AT_MOST -> min(size, MeasureSpec.getSize(heightMeasureSpec))
            else -> size
        }
        setMeasuredDimension(width, height)
    }

    // Everything is centred at (centerX, centerY); sides show below, down to centerY + depth.
    private val centerX get() = width / 2f
    private val centerY get() = (height - depth) / 2f
    private val half get() = min(min(width.toFloat(), height - depth) / 2f, most / 2 * density) - 2 * density   // half the square
    // The pad's: with corner keys, its circle (and the gap round it) reaches the square's sides, so each corner key is
    // the square's corner outside it.
    private val outerRadius get() = if (corners.isEmpty()) half else half - gap
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
        keyFacePaint.shader = gradient(centerY - half, centerY + half, R.color.key_top, R.color.key_bottom)
        keyPressedFacePaint.shader = gradient(centerY - half, centerY + half, R.color.key_pressed_top, R.color.key_pressed_bottom)
        // Each corner key: its quarter of the square, less the pad and a gap round it.
        val round = Path().apply { addCircle(centerX, centerY, outer + gap, Path.Direction.CW) }
        for (corner in 0..3) {
            val right = corner % 2 == 1
            val top = corner < 2
            val quarter = RectF(
                if (right) centerX + gap / 2 else centerX - half, if (top) centerY - half else centerY + gap / 2,
                if (right) centerX + half else centerX - gap / 2, if (top) centerY - gap / 2 else centerY + half,
            )
            pieces[corner] = Path().apply {
                addRoundRect(quarter, 18 * density, 18 * density, Path.Direction.CW)
                op(round, Path.Op.DIFFERENCE)
            }
        }
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

        for (key in corners) drawCornerKey(canvas, key, if (isPressed) shownKey == key else false)

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

    /** One corner key: its side below, its rim, its face (sunk while pressed), and its icon towards its corner. */
    private fun drawCornerKey(canvas: Canvas, key: CornerKey, isDown: Boolean) {
        val piece = pieces[key.corner.coerceIn(0, 3)] ?: return
        val sink = if (isDown) travel else 0f
        canvas.save()
        canvas.translate(0f, depth)
        canvas.drawPath(piece, keyEdgePaint)
        canvas.translate(0f, sink - depth)
        canvas.drawPath(piece, if (isDown) keyPressedRimPaint else keyRimPaint)
        canvas.translate(0f, 1 * density)
        canvas.drawPath(piece, if (isDown) keyPressedFacePaint else keyFacePaint)
        canvas.restore()
        // The icon sits along the diagonal, between the pad and the corner.
        val icon = icons.getOrPut(key.icon) { context.getDrawable(key.icon)!!.mutate().apply { setTint(context.getColor(R.color.ink)) } }
        val reach = outerRadius + gap + (half * 1.4142f - outerRadius - gap) * 0.42f
        val along = reach * 0.7071f
        val x = centerX + if (key.corner % 2 == 1) along else -along
        val y = centerY + 1 * density + sink + if (key.corner < 2) -along else along
        val size = half * 0.11f
        icon.setBounds((x - size).toInt(), (y - size).toInt(), (x + size).toInt(), (y + size).toInt())
        icon.draw(canvas)
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

    private fun keyAt(x: Float, y: Float): CornerKey? {
        val dx = x - centerX
        val dy = y - centerY
        if (corners.isEmpty() || hypot(dx, dy) < outerRadius + gap / 2) return null
        val reach = half + 6 * density
        if (dx < -reach || dx > reach || dy < -reach || dy > reach) return null
        val corner = (if (dy < 0) 0 else 2) + (if (dx < 0) 0 else 1)
        return corners.firstOrNull { it.corner == corner }
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
    private var touchedKey: CornerKey? = null
    private val gesture = PressGesture(
        this,
        onPress = {
            touchedKey?.let { key ->
                pressedKey = key
                shownKey = key
                shown = null
                onCornerKey?.invoke(key, true)
                invalidate()
            }
            touchedPart?.let { part ->
                pressed = part
                shown = part
                shownKey = null
                listener?.onPress(part)
                invalidate()
            }
        },
        onRelease = {
            pressedKey?.let { key ->
                pressedKey = null
                onCornerKey?.invoke(key, false)
            }
            pressed?.let { part ->
                pressed = null
                listener?.onRelease(part)
            }
        },
    )

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(event: MotionEvent): Boolean {
        if (event.actionMasked == MotionEvent.ACTION_DOWN) {
            touchedKey = keyAt(event.x, event.y)
            touchedPart = if (touchedKey == null) partAt(event.x, event.y) else null
            if (touchedKey == null && touchedPart == null) return false
        }
        return gesture.onTouch(event)
    }
}
