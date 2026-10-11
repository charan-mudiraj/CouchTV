package com.couchtv.remote

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.RadialGradient
import android.graphics.RectF
import android.graphics.Shader
import android.os.SystemClock
import android.util.AttributeSet
import android.util.TypedValue
import android.view.MotionEvent
import android.view.VelocityTracker
import android.view.View
import android.view.ViewConfiguration
import kotlin.math.abs
import kotlin.math.exp
import kotlin.math.hypot

/**
 * A laptop-style trackpad for the TV's pointer, set into the remote like a pane of glass:
 *  - slide one finger to move the pointer (faster finger, further: slow for precision, quick to cross the screen);
 *  - tap to click; tap twice and slide to drag (the button stays down until the finger lifts);
 *  - slide two fingers to scroll, which glides on after a flick; tap with two fingers to right-click.
 * Movement is in the TV's pixels and scrolling in mouse-wheel units (120 a notch), up and right positive.
 * The pages don't turn while a finger is on the pad.
 */
class TrackpadView @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : View(context, attrs) {
    interface Listener {
        fun onMove(dx: Int, dy: Int)
        fun onWheel(dy: Int, dx: Int)
        fun onButton(right: Boolean, down: Boolean)
        /** All fingers lifted. */
        fun onStop()
    }

    var listener: Listener? = null

    /** Shown in the middle of the pad, e.g. when the TV can't be reached; null for none. */
    var hint: String? = null
        set(value) {
            field = value
            invalidate()
        }

    private companion object {
        const val SLOW_GAIN = 1.4f      // TV pixels per dp, moving slowly…
        const val FAST_GAIN = 4.5f      // …and quickly
        const val SLOW_SPEED = 0.10f    // dp per ms: at or below this, the slow gain
        const val FAST_SPEED = 1.20f    // at or above this, the fast gain
        const val WHEEL_PER_DP = 2.5f   // wheel units per dp of two-finger slide
        const val TAP_MS = 220L
        const val DOUBLE_TAP_MS = 300L
        const val GLIDE_MS = 350f       // a flicked scroll slows by e (2.7x) every this long
    }

    private val density = resources.displayMetrics.density
    private val slop = ViewConfiguration.get(context).scaledTouchSlop.toFloat()

    // One finger
    private var activeId = -1
    private var downTime = 0L
    private var downX = 0f
    private var downY = 0f
    private var lastX = 0f
    private var lastY = 0f
    private var lastTime = 0L
    private var moved = false
    private var dragArmed = false     // the second touch of a double tap: sliding now drags
    private var dragging = false
    private var lastTapTime = 0L
    private var lastTapX = 0f
    private var lastTapY = 0f
    private var leftoverX = 0f        // fractions of a pixel, carried to the next move
    private var leftoverY = 0f

    // Two fingers
    private var fingers = 0
    private var mostFingers = 0
    private var twoDownTime = 0L
    private var twoStartX = 0f
    private var twoStartY = 0f
    private var twoLastX = 0f
    private var twoLastY = 0f
    private var scrolling = false
    private var rightClicked = false  // this touch was a two-finger tap
    private var horizontal = false    // the scroll is locked to one direction once it starts
    private var wheelY = 0f
    private var wheelX = 0f
    private var tracker: VelocityTracker? = null

    // Gliding on after a flicked scroll
    private var glideY = 0f           // wheel units per ms
    private var glideX = 0f
    private var glideLast = 0L
    private val glide = object : Runnable {
        override fun run() {
            val now = SystemClock.uptimeMillis()
            val dt = (now - glideLast).coerceIn(1L, 50L).toFloat()
            glideLast = now
            wheelY += glideY * dt
            wheelX += glideX * dt
            sendWheel()
            val decay = exp(-dt / GLIDE_MS)
            glideY *= decay
            glideX *= decay
            if (abs(glideY) > 0.02f || abs(glideX) > 0.02f) postOnAnimation(this)
        }
    }

    // Drawing
    private val pad = RectF()
    private val radius = 22 * density
    private val lipPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = context.getColor(R.color.slot_lip) }
    private val shadowPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = context.getColor(R.color.slot_shadow) }
    private val glassPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val sheenPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val glowRadius = 44 * density
    private val glowPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        shader = RadialGradient(0f, 0f, glowRadius, 0x2EF4F6FA, 0x00F4F6FA, Shader.TileMode.CLAMP)
    }
    private val hintPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = context.getColor(R.color.muted)
        textAlign = Paint.Align.CENTER
        textSize = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_SP, 14f, resources.displayMetrics)
    }
    private val touches = FloatArray(4)   // where up to two fingers are, for their glow
    private var touchCount = 0

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        glassPaint.shader = LinearGradient(0f, 0f, 0f, h.toFloat(),
            context.getColor(R.color.slot_top), context.getColor(R.color.slot_bottom), Shader.TileMode.CLAMP)
        // A faint reflection across the top-left, as on glass.
        sheenPaint.shader = LinearGradient(0f, 0f, w * 0.6f, h * 0.6f, 0x14FFFFFF, 0x00FFFFFF, Shader.TileMode.CLAMP)
    }

    override fun onDraw(canvas: Canvas) {
        val w = width.toFloat()
        val h = height.toFloat()
        // Set into the remote: a light lip along the bottom, a shadow inside the top edge, then the glass.
        pad.set(0f, 0f, w, h)
        canvas.drawRoundRect(pad, radius, radius, lipPaint)
        pad.set(0f, 0f, w, h - 1 * density)
        canvas.drawRoundRect(pad, radius, radius, shadowPaint)
        pad.set(0f, 3 * density, w, h - 1 * density)
        canvas.drawRoundRect(pad, radius, radius, glassPaint)
        canvas.drawRoundRect(pad, radius, radius, sheenPaint)

        canvas.save()
        canvas.clipRect(pad)
        for (i in 0 until touchCount) {
            canvas.save()
            canvas.translate(touches[i * 2], touches[i * 2 + 1])
            canvas.drawCircle(0f, 0f, glowRadius, glowPaint)
            canvas.restore()
        }
        canvas.restore()

        hint?.let { canvas.drawText(it, w / 2, h / 2 - (hintPaint.descent() + hintPaint.ascent()) / 2, hintPaint) }
    }

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(event: MotionEvent): Boolean {
        if (tracker == null) tracker = VelocityTracker.obtain()
        tracker?.addMovement(event)
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> down(event)
            MotionEvent.ACTION_POINTER_DOWN -> pointerDown(event)
            MotionEvent.ACTION_MOVE -> move(event)
            MotionEvent.ACTION_POINTER_UP -> pointerUp(event)
            MotionEvent.ACTION_UP -> up(event, cancelled = false)
            MotionEvent.ACTION_CANCEL -> up(event, cancelled = true)
        }
        showTouches(event)
        return true
    }

    private fun down(event: MotionEvent) {
        parent?.requestDisallowInterceptTouchEvent(true)   // sliding sideways moves the pointer, not the page
        removeCallbacks(glide)
        glideX = 0f
        glideY = 0f
        activeId = event.getPointerId(0)
        downTime = event.eventTime
        downX = event.x
        downY = event.y
        lastX = event.x
        lastY = event.y
        lastTime = event.eventTime
        moved = false
        dragging = false
        leftoverX = 0f
        leftoverY = 0f
        fingers = 1
        mostFingers = 1
        scrolling = false
        rightClicked = false
        dragArmed = event.eventTime - lastTapTime < DOUBLE_TAP_MS &&
            hypot(event.x - lastTapX, event.y - lastTapY) < 48 * density
    }

    private fun pointerDown(event: MotionEvent) {
        fingers = event.pointerCount
        mostFingers = maxOf(mostFingers, fingers)
        if (fingers == 2 && !dragging) {
            listener?.onStop()
            twoDownTime = event.eventTime
            twoStartX = centreX(event)
            twoStartY = centreY(event)
            twoLastX = twoStartX
            twoLastY = twoStartY
            wheelX = 0f
            wheelY = 0f
        }
    }

    private fun move(event: MotionEvent) {
        if (mostFingers >= 2 && !dragging) {
            if (event.pointerCount < 2) return   // one of two fingers lifted: the gesture is over bar the lift
            val x = centreX(event)
            val y = centreY(event)
            if (!scrolling && hypot(x - twoStartX, y - twoStartY) > slop) {
                scrolling = true
                horizontal = abs(x - twoStartX) > 2 * abs(y - twoStartY)
                twoLastX = x
                twoLastY = y
            }
            if (scrolling) {
                // Natural scrolling, as on a phone: the page follows the fingers.
                if (horizontal) wheelX -= (x - twoLastX) / density * WHEEL_PER_DP
                else wheelY += (y - twoLastY) / density * WHEEL_PER_DP
                sendWheel()
            }
            twoLastX = x
            twoLastY = y
            return
        }
        val index = event.findPointerIndex(activeId)
        if (index < 0) return
        val x = event.getX(index)
        val y = event.getY(index)
        if (!moved) {
            if (hypot(x - downX, y - downY) <= slop) return
            moved = true
            if (dragArmed) {
                dragging = true
                listener?.onButton(false, true)
                Haptics.press(this)
            }
            lastX = x   // start from here, so leaving the tap zone doesn't jump the pointer
            lastY = y
            lastTime = event.eventTime
            return
        }
        val dxDp = (x - lastX) / density
        val dyDp = (y - lastY) / density
        val dt = (event.eventTime - lastTime).coerceAtLeast(1L)
        val speed = hypot(dxDp, dyDp) / dt
        val t = ((speed - SLOW_SPEED) / (FAST_SPEED - SLOW_SPEED)).coerceIn(0f, 1f)
        val gain = SLOW_GAIN + (FAST_GAIN - SLOW_GAIN) * t * t * (3 - 2 * t)
        leftoverX += dxDp * gain
        leftoverY += dyDp * gain
        val sendX = leftoverX.toInt()
        val sendY = leftoverY.toInt()
        if (sendX != 0 || sendY != 0) {
            leftoverX -= sendX
            leftoverY -= sendY
            listener?.onMove(sendX, sendY)
        }
        lastX = x
        lastY = y
        lastTime = event.eventTime
    }

    private fun pointerUp(event: MotionEvent) {
        val lifted = event.getPointerId(event.actionIndex)
        fingers = event.pointerCount - 1
        if (mostFingers >= 2 && !dragging) {
            if (fingers == 1 && !scrolling && !rightClicked && event.eventTime - twoDownTime < TAP_MS * 3 / 2) {
                listener?.onButton(true, true)   // a two-finger tap: right-click
                listener?.onButton(true, false)
                Haptics.press(this)
                rightClicked = true
            }
            return
        }
        if (lifted == activeId) {
            // Carry on with the finger that's still down.
            val other = if (event.actionIndex == 0) 1 else 0
            activeId = event.getPointerId(other)
            lastX = event.getX(other)
            lastY = event.getY(other)
            lastTime = event.eventTime
        }
    }

    private fun up(event: MotionEvent, cancelled: Boolean) {
        val wasScrolling = mostFingers >= 2 && scrolling && !dragging
        when {
            dragging -> {
                listener?.onButton(false, false)
                Haptics.release(this)
            }
            !cancelled && mostFingers == 1 && !moved && event.eventTime - downTime < TAP_MS -> {
                listener?.onButton(false, true)    // a tap: click (a second one makes it a double-click)
                listener?.onButton(false, false)
                Haptics.press(this)
                lastTapTime = event.eventTime
                lastTapX = event.x
                lastTapY = event.y
            }
        }
        if (wasScrolling && !cancelled) {
            tracker?.let { t ->
                t.computeCurrentVelocity(1)   // px per ms
                val id = event.getPointerId(0)
                val vx = t.getXVelocity(id) / density
                val vy = t.getYVelocity(id) / density
                if (hypot(vx, vy) > 0.3f) {
                    if (horizontal) glideX = -vx * WHEEL_PER_DP else glideY = vy * WHEEL_PER_DP
                    glideLast = SystemClock.uptimeMillis()
                    postOnAnimation(glide)
                }
            }
        }
        dragging = false
        fingers = 0
        tracker?.recycle()
        tracker = null
        listener?.onStop()
    }

    private fun sendWheel() {
        val y = wheelY.toInt()
        val x = wheelX.toInt()
        if (x == 0 && y == 0) return
        wheelY -= y
        wheelX -= x
        listener?.onWheel(y, x)
    }

    private fun centreX(event: MotionEvent) = (event.getX(0) + event.getX(1)) / 2
    private fun centreY(event: MotionEvent) = (event.getY(0) + event.getY(1)) / 2

    private fun showTouches(event: MotionEvent) {
        val action = event.actionMasked
        touchCount = 0
        if (action != MotionEvent.ACTION_UP && action != MotionEvent.ACTION_CANCEL) {
            for (i in 0 until minOf(event.pointerCount, 2)) {
                if (action == MotionEvent.ACTION_POINTER_UP && i == event.actionIndex) continue
                touches[touchCount * 2] = event.getX(i)
                touches[touchCount * 2 + 1] = event.getY(i)
                touchCount++
            }
        }
        invalidate()
    }

    override fun onDetachedFromWindow() {
        removeCallbacks(glide)
        super.onDetachedFromWindow()
    }
}
