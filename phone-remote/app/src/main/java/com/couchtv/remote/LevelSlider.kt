package com.couchtv.remote

import android.animation.ValueAnimator
import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RadialGradient
import android.graphics.RectF
import android.graphics.Shader
import android.graphics.drawable.Drawable
import android.os.Bundle
import android.os.SystemClock
import android.util.AttributeSet
import android.util.TypedValue
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import android.view.accessibility.AccessibilityNodeInfo
import android.view.animation.DecelerateInterpolator
import android.view.animation.OvershootInterpolator
import kotlin.math.abs
import kotlin.math.exp
import kotlin.math.roundToInt

/**
 * One of the TV's levels (its volume, its brightness) as a thick bar with rounded corners that fills from the left,
 * set into the remote like the search bar. Drag along it, starting anywhere: the level follows by how far the finger moves, so touching
 * it never jumps. Under the finger the bar swells, past 0 or 100 it stretches like a rubber band and springs back,
 * it ticks every 10%, its icon changes with the level ([iconFor]) and the number lights up. A tap on the icon does
 * [onIconTap] (mute). What the TV says ([show]) glides into place, except under the finger and just after.
 */
class LevelSlider @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : View(context, attrs) {
    private companion object {
        const val SEND_EVERY_MS = 60L      // while dragging, the TV hears the level this often at most
        const val QUIET_MS = 900L          // after letting go, the TV's own reports wait this long (they lag behind)
        const val FOLLOW = 0.45f           // how much of the way to the finger the fill moves each frame
        const val GLIDE = 0.16f            // ...and to a level the TV reported
        const val CORNER_DP = 14f          // its corners: rounded, not a pill
        const val REST_X_DP = 19f          // at rest the bar sits this far inside the view at each end...
        const val REST_Y_DP = 4f           // ...and above and below it (the view reaches into the page's margins)
        const val GROW_X_DP = 4f           // under the finger it grows this much at each end...
        const val GROW_Y_DP = 3f           // ...and above and below
        const val MOST_STRETCH_DP = 12f    // past 0 or 100, it stretches at most this far
    }

    /** The level while dragging (a few times a second) and when let go: 0 to 100. */
    var onChange: ((Int) -> Unit)? = null

    /** A drag begins (before the first [onChange]). */
    var onDragStart: (() -> Unit)? = null

    /** The icon tapped (mute and unmute, for the volume). */
    var onIconTap: (() -> Unit)? = null

    /** Touched while it can't do anything ([available] false): say why. */
    var onBlocked: (() -> Unit)? = null

    /** The icon for a level, e.g. a speaker with more waves the louder it is. */
    var iconFor: (level: Int, muted: Boolean) -> Int = { _, _ -> 0 }
        set(value) {
            field = value
            invalidate()
        }

    /** The icon turns with the level, like a dial (the sun, for brightness). */
    var spinIcon = false

    /** Its name, for TalkBack: "Volume". */
    var name = ""

    /** False when it can't reach the TV (drawn faded; a touch calls [onBlocked]). */
    var available = true
        set(value) {
            field = value
            invalidate()
        }

    /** The level shown, 0 to 100. */
    var level = 50
        private set

    var muted = false
        private set

    /** Whether the TV has said its level (else no number is shown). */
    var known = false
        private set

    private val density = resources.displayMetrics.density
    private val slop = ViewConfiguration.get(context).scaledTouchSlop
    private var target = 50f        // where the fill is heading
    private var shown = 50f         // where it's drawn
    private var swell = 0f          // 0 at rest, 1 under the finger
    private var stretch = 0f        // px past either end (negative: past 0)
    private var dragging = false
    private var downX = 0f
    private var startLevel = 0f
    private var onIcon = false
    private var quietUntil = 0L
    private var sentLevel = -1
    private var sentAt = 0L
    private var swellAnimator: ValueAnimator? = null
    private var stretchAnimator: ValueAnimator? = null
    private val icons = HashMap<Int, Drawable>()
    private var iconShown = 0
    private var iconPop = 1f
    private var fromColor = context.getColor(R.color.voice)
    private var toColor = context.getColor(R.color.voice_top)

    private val pill = RectF()
    private val fill = RectF()
    private val innerRect = RectF()
    private val clip = Path()
    private val slotPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val lipPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = context.getColor(R.color.slot_lip) }
    private val shadowPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = context.getColor(R.color.slot_shadow) }
    private val fillPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val sheenPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val glowPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val numberPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        textAlign = Paint.Align.RIGHT
        textSize = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_SP, 14f, resources.displayMetrics)
    }

    init {
        isClickable = true
        isFocusable = true
    }

    /** The fill's colours, left to right. */
    fun setColors(from: Int, to: Int) {
        fromColor = from
        toColor = to
        fillPaint.shader = null
        invalidate()
    }

    /** What the TV says its level is (-1: it hasn't said): glides there, unless the finger's on it or just left it. */
    fun show(level: Int, muted: Boolean = false) {
        if (level < 0) {
            known = false
            invalidate()
            return
        }
        if (dragging || SystemClock.uptimeMillis() < quietUntil) return
        known = true
        this.muted = muted
        this.level = level.coerceIn(0, 100)
        target = this.level.toFloat()
        updateDescription()
        animateFill()
    }

    // ---------------------------------------------------------------- touch

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                parent?.requestDisallowInterceptTouchEvent(true)   // sliding sideways moves the level, not the page
                if (!available) {
                    onBlocked?.invoke()
                    return false
                }
                downX = event.x
                startLevel = target
                dragging = false
                onIcon = event.x < pill.left + pill.height()
                animateSwell(1f)
                Haptics.press(this)
            }
            MotionEvent.ACTION_MOVE -> {
                if (!dragging) {
                    if (abs(event.x - downX) < slop) return true
                    dragging = true
                    downX = event.x   // from here, so passing the slop doesn't jump the level
                    if (muted) muted = false   // sliding unmutes, as Windows' own slider does
                    onDragStart?.invoke()
                }
                drag(event.x)
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                val tapped = !dragging && event.actionMasked == MotionEvent.ACTION_UP
                if (dragging) {
                    dragging = false
                    quietUntil = SystemClock.uptimeMillis() + QUIET_MS
                    removeCallbacks(sendLatest)
                    send(force = true)
                    springBack()
                }
                animateSwell(0f)
                Haptics.release(this)
                if (tapped && onIcon) {
                    onIconTap?.invoke()
                    iconPop = 0.6f
                    animateFill()
                }
                if (tapped) performClick()
            }
        }
        return true
    }

    override fun performClick(): Boolean {
        super.performClick()
        return true
    }

    private fun drag(x: Float) {
        val width = pill.width().coerceAtLeast(1f)
        val raw = startLevel + (x - downX) / width * 100f
        val clamped = raw.coerceIn(0f, 100f)
        // Past either end the bar stretches, less and less the further the finger goes.
        val over = (raw - clamped) / 100f * width
        val most = MOST_STRETCH_DP * density
        stretchAnimator?.cancel()
        stretch = if (over == 0f) 0f else most * (1 - exp(-abs(over) / (most * 3))) * if (over > 0) 1 else -1
        val before = level
        target = clamped
        level = clamped.roundToInt()
        if (level / 10 != before / 10 || (level != before && (level == 0 || level == 100))) {
            if (level == 0 || level == 100) Haptics.press(this) else Haptics.release(this)
        }
        updateDescription()
        send(force = false)
        animateFill()
    }

    private val sendLatest = Runnable { send(force = true) }

    /** Tells [onChange] the level, at most every [SEND_EVERY_MS] while dragging; the last one always goes. */
    private fun send(force: Boolean) {
        if (level == sentLevel) return
        val now = SystemClock.uptimeMillis()
        if (!force && now - sentAt < SEND_EVERY_MS) {
            removeCallbacks(sendLatest)
            postDelayed(sendLatest, SEND_EVERY_MS - (now - sentAt))
            return
        }
        sentLevel = level
        sentAt = now
        onChange?.invoke(level)
    }

    // ---------------------------------------------------------------- animation

    private val frame = object : Runnable {
        override fun run() {
            val step = if (dragging) FOLLOW else GLIDE
            shown += (target - shown) * step
            iconPop += (1f - iconPop) * 0.25f
            if (abs(target - shown) < 0.05f) shown = target
            if (abs(1f - iconPop) < 0.01f) iconPop = 1f
            invalidate()
            if (shown != target || iconPop != 1f) postOnAnimation(this)
        }
    }

    private fun animateFill() {
        removeCallbacks(frame)
        postOnAnimation(frame)
    }

    private fun animateSwell(to: Float) {
        swellAnimator?.cancel()
        swellAnimator = ValueAnimator.ofFloat(swell, to).apply {
            duration = if (to > 0) 260 else 220
            interpolator = if (to > 0) OvershootInterpolator(1.6f) else DecelerateInterpolator()
            addUpdateListener {
                swell = it.animatedValue as Float
                invalidate()
            }
            start()
        }
    }

    private fun springBack() {
        if (stretch == 0f) return
        stretchAnimator?.cancel()
        stretchAnimator = ValueAnimator.ofFloat(stretch, 0f).apply {
            duration = 360
            interpolator = OvershootInterpolator(3f)
            addUpdateListener {
                stretch = it.animatedValue as Float
                invalidate()
            }
            start()
        }
    }

    // ---------------------------------------------------------------- drawing

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        slotPaint.shader = LinearGradient(0f, 0f, 0f, h.toFloat(), context.getColor(R.color.slot_top), context.getColor(R.color.slot_bottom), Shader.TileMode.CLAMP)
        sheenPaint.shader = LinearGradient(0f, 0f, 0f, h * 0.55f, 0x33FFFFFF, 0x00FFFFFF, Shader.TileMode.CLAMP)
        fillPaint.shader = null
    }

    override fun onDraw(canvas: Canvas) {
        val w = width.toFloat()
        val h = height.toFloat()
        // At rest the bar sits inside the view, which reaches into the page's margins: room to swell under the finger
        // and stretch at the ends without being cut off.
        val growX = GROW_X_DP * density * swell
        val growY = GROW_Y_DP * density * swell
        pill.set(REST_X_DP * density - growX, REST_Y_DP * density - growY, w - REST_X_DP * density + growX, h - REST_Y_DP * density + growY)
        if (stretch > 0) pill.right += stretch else pill.left += stretch
        val radius = minOf(pill.height() / 2, CORNER_DP * density)
        val faded = !available
        val alpha = if (faded) 110 else 255

        // Set into the remote: a light lip along the bottom, a shadow inside the top edge, then the slot.
        lipPaint.alpha = alpha
        shadowPaint.alpha = alpha
        slotPaint.alpha = alpha
        canvas.drawRoundRect(pill, radius, radius, lipPaint)
        fill.set(pill.left, pill.top, pill.right, pill.bottom - 1 * density)
        canvas.drawRoundRect(fill, radius, radius, shadowPaint)
        fill.top += 2 * density
        canvas.drawRoundRect(fill, radius, radius, slotPaint)

        // The fill, clipped to the bar, with a sheen along its top, and a soft light at its end under the finger.
        val inner = innerRect.apply { set(fill) }
        clip.reset()
        clip.addRoundRect(inner, radius, radius, Path.Direction.CW)
        val end = inner.left + inner.width() * (shown / 100f)
        if (fillPaint.shader == null) {
            fillPaint.shader = LinearGradient(inner.left, 0f, inner.right, 0f, fromColor, toColor, Shader.TileMode.CLAMP)
        }
        canvas.save()
        canvas.clipPath(clip)
        if (shown > 0f) {
            val grey = muted || !known
            fillPaint.alpha = if (faded) 90 else if (grey) 70 else 255
            fill.set(inner.left, inner.top, end, inner.bottom)
            // Rounded at its end too, so a little fill still has a rounded end.
            canvas.drawRoundRect(fill.left - radius, fill.top, fill.right, fill.bottom, radius, radius, fillPaint)
            sheenPaint.alpha = if (grey || faded) 60 else 255
            canvas.drawRoundRect(fill.left - radius, fill.top, fill.right, fill.bottom, radius, radius, sheenPaint)
            if (!grey && !faded && swell > 0.02f && end > inner.left + radius) {
                val glowRadius = inner.height() * 1.2f
                glowPaint.shader = RadialGradient(end, inner.centerY(), glowRadius,
                    (((0x48 * swell).toInt().coerceIn(0, 255)) shl 24) or 0xFFFFFF, 0x00FFFFFF, Shader.TileMode.CLAMP)
                canvas.drawCircle(end, inner.centerY(), glowRadius, glowPaint)
            }
        }
        canvas.restore()

        // The icon, at the left; it changes with the level (and pops when it does), the sun turning with it.
        val iconRes = iconFor(level, muted)
        if (iconRes != 0) {
            if (iconRes != iconShown) {
                if (iconShown != 0) {
                    iconPop = 0.7f
                    animateFill()
                }
                iconShown = iconRes
            }
            val icon = icons.getOrPut(iconRes) { context.getDrawable(iconRes)!!.mutate() }
            icon.setTint(context.getColor(R.color.ink))
            icon.alpha = alpha
            val size = 11 * density * iconPop.coerceAtLeast(0.5f)
            val cx = inner.left + inner.height() / 2 + 4 * density
            val cy = inner.centerY()
            canvas.save()
            if (spinIcon) canvas.rotate(shown * 0.9f, cx, cy)
            icon.setBounds((cx - size).toInt(), (cy - size).toInt(), (cx + size).toInt(), (cy + size).toInt())
            icon.draw(canvas)
            canvas.restore()
        }

        // The number, at the right: bright while dragging.
        val number = when {
            !known -> ""
            muted -> context.getString(R.string.level_muted)
            else -> "$level%"
        }
        if (number.isNotEmpty()) {
            numberPaint.color = context.getColor(if (dragging || swell > 0.5f) R.color.ink else R.color.muted)
            numberPaint.alpha = alpha
            numberPaint.isFakeBoldText = dragging
            canvas.drawText(number, inner.right - inner.height() * 0.42f, inner.centerY() - (numberPaint.descent() + numberPaint.ascent()) / 2, numberPaint)
        }
    }

    // ---------------------------------------------------------------- TalkBack

    private fun updateDescription() {
        contentDescription = if (known) "$name ${if (muted) context.getString(R.string.level_muted) else "$level%"}" else name
    }

    override fun onInitializeAccessibilityNodeInfo(info: AccessibilityNodeInfo) {
        super.onInitializeAccessibilityNodeInfo(info)
        info.className = "android.widget.SeekBar"
        info.rangeInfo = AccessibilityNodeInfo.RangeInfo.obtain(AccessibilityNodeInfo.RangeInfo.RANGE_TYPE_PERCENT, 0f, 100f, level.toFloat())
        if (available) {
            info.addAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_SCROLL_FORWARD)
            info.addAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_SCROLL_BACKWARD)
        }
    }

    override fun performAccessibilityAction(action: Int, arguments: Bundle?): Boolean {
        val step = when (action) {
            AccessibilityNodeInfo.ACTION_SCROLL_FORWARD -> 5
            AccessibilityNodeInfo.ACTION_SCROLL_BACKWARD -> -5
            else -> return super.performAccessibilityAction(action, arguments)
        }
        if (!available) return false
        onDragStart?.invoke()
        level = (level + step).coerceIn(0, 100)
        target = level.toFloat()
        quietUntil = SystemClock.uptimeMillis() + QUIET_MS
        send(force = true)
        updateDescription()
        animateFill()
        return true
    }
}
