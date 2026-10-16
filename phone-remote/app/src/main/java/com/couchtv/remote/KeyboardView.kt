package com.couchtv.remote

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.Shader
import android.os.SystemClock
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import kotlin.math.abs
import kotlin.math.min

/**
 * A full PC keyboard on one page: Esc and the function keys, numbers and symbols, letters, Shift / Ctrl / Win / Alt,
 * Enter, Backspace, Tab, Caps Lock, the arrows and the navigation keys. Each key goes to the TV as its Windows
 * virtual-key code, down and up ("KEY" in ir-receiver/PROTOCOL.md), so shortcuts work as on a real keyboard. Drawn like
 * the other keys (key_bg): raised, sinking while pressed.
 *
 * Shift, Ctrl, Alt and Win stay down for the next key (lit), so one finger does Ctrl+C: tap Ctrl, then C. Tapped twice
 * quickly they stay down until tapped again (hold Alt, then Tab, Tab… to switch apps). Two fingers work too: hold
 * Ctrl, tap C. A held key repeats, as on a real keyboard (Windows only repeats real keyboards' keys). A swipe that
 * starts on the keys still turns the page.
 *
 * [sideways]: drawn turned a quarter, along the screen's length, for holding the phone sideways (its top to the left)
 * while the app stays upright: much bigger keys. [action] is a button above the keys (typing with the phone's own
 * keyboard), turned with them.
 */
class KeyboardView @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : View(context, attrs) {

    fun interface Listener {
        fun onKey(vk: Int, down: Boolean)
    }

    var listener: Listener? = null

    /** Laid along the screen's length, for holding the phone sideways with its top to the left. */
    var sideways = false
        set(value) {
            field = value
            layoutKeys()
            invalidate()
        }

    /** A button above the keys (null for none), and what it does. */
    var action: String? = null
        set(value) {
            field = value
            layoutKeys()
            invalidate()
        }
    var onAction: (() -> Unit)? = null

    /** Shown over the keys when they can't reach the TV, e.g. "Connect to the TV over Wi-Fi first". */
    var hint: String? = null
        set(value) {
            field = value
            invalidate()
        }

    private class Key(val vk: Int, val label: String, val shifted: String?, val units: Float, val modifier: Boolean) {
        val rect = RectF()
        var row = 0
    }

    private enum class Mod { OFF, ONCE, LOCKED }

    private companion object {
        const val UNITS = 15f
        const val MOST_ROW_DP = 54f
        const val REPEAT_AFTER_MS = 450L
        const val REPEAT_EVERY_MS = 45L
        const val DOUBLE_TAP_MS = 400L
        const val VK_SHIFT_LEFT = 0xA0
        const val VK_SHIFT_RIGHT = 0xA1
    }

    private fun key(vk: Int, label: String, shifted: String? = null, units: Float = 1f) = Key(vk, label, shifted, units, false)
    private fun mod(vk: Int, label: String, units: Float) = Key(vk, label, null, units, true)
    private fun letters(text: String) = text.map { key(it.code, it.toString()) }

    // Fifteen units wide, like a real one (Backspace is two keys wide, and so on).
    private val rows: List<List<Key>> = listOf(
        listOf(key(0x1B, "Esc")) + (1..12).map { key(0x6F + it, "F$it") } + listOf(key(0x2C, "PrtSc"), key(0x2E, "Del")),
        listOf(key(0xC0, "`", "~")) + "1234567890".mapIndexed { i, c -> key(c.code, c.toString(), "!@#$%^&*()"[i].toString()) } +
            listOf(key(0xBD, "-", "_"), key(0xBB, "=", "+"), key(0x08, "⌫", units = 2f)),
        listOf(key(0x09, "Tab", units = 1.5f)) + letters("QWERTYUIOP") +
            listOf(key(0xDB, "[", "{"), key(0xDD, "]", "}"), key(0xDC, "\\", "|", 1.5f)),
        listOf(key(0x14, "Caps", units = 1.75f)) + letters("ASDFGHJKL") +
            listOf(key(0xBA, ";", ":"), key(0xDE, "'", "\""), key(0x0D, "Enter", units = 2.25f)),
        listOf(mod(VK_SHIFT_LEFT, "Shift", 2.25f)) + letters("ZXCVBNM") +
            listOf(key(0xBC, ",", "<"), key(0xBE, ".", ">"), key(0xBF, "/", "?"), mod(VK_SHIFT_RIGHT, "Shift", 2.75f)),
        listOf(mod(0xA2, "Ctrl", 1.75f), mod(0x5B, "Win", 1.5f), mod(0xA4, "Alt", 1.75f), key(0x20, "", units = 6.5f),
            mod(0xA5, "Alt", 1.75f), mod(0xA3, "Ctrl", 1.75f)),
        listOf(key(0x2D, "Ins", units = 1.5f), key(0x24, "Home", units = 1.5f), key(0x23, "End", units = 1.5f),
            key(0x21, "PgUp", units = 1.5f), key(0x22, "PgDn", units = 1.5f), key(0x25, "←", units = 1.5f),
            key(0x26, "↑", units = 1.5f), key(0x28, "↓", units = 1.5f), key(0x27, "→", units = 1.5f), key(0x5D, "Menu", units = 1.5f)),
    )
    private val keys = rows.flatMapIndexed { r, row -> row.onEach { it.row = r } }

    // ---------------------------------------------------------------- state

    private val mods = HashMap<Int, Mod>()          // each modifier key's state
    private val modsDown = HashSet<Int>()           // modifiers down on the TV now
    private val heldMods = HashMap<Int, Boolean>()  // modifiers under a finger: used with another key meanwhile?
    private var lastModTap = 0
    private var lastModTapAt = 0L
    private val down = HashMap<Int, Key>()          // finger (pointer id) -> the key it's pressing
    private val shownDown = HashSet<Key>()          // drawn sunk (a quick tap stays down a moment)

    private fun state(vk: Int) = mods[vk] ?: Mod.OFF
    private val shift get() = state(VK_SHIFT_LEFT) != Mod.OFF || state(VK_SHIFT_RIGHT) != Mod.OFF ||
        heldMods.containsKey(VK_SHIFT_LEFT) || heldMods.containsKey(VK_SHIFT_RIGHT)

    private fun send(vk: Int, isDown: Boolean) = listener?.onKey(vk, isDown)

    private fun modDown(vk: Int) {
        if (modsDown.add(vk)) send(vk, true)
    }

    private fun modUp(vk: Int) {
        if (modsDown.remove(vk)) send(vk, false)
    }

    /** Lets go of everything (leaving the page, the app going to the background): nothing stays down on the TV. */
    fun releaseAll() {
        for (key in down.values) if (!key.modifier) send(key.vk, false)
        down.clear()
        removeCallbacks(repeat)
        repeating = null
        for (vk in modsDown.toList()) modUp(vk)
        mods.clear()
        heldMods.clear()
        shownDown.clear()
        removeCallbacks(startFirst)
        firstKey = null
        firstPointer = -1
        invalidate()
    }

    private fun press(key: Key) {
        shownDown.add(key)
        Haptics.press(this)
        if (key.modifier) {
            heldMods[key.vk] = false
            modDown(key.vk)
        } else {
            for (vk in heldMods.keys) heldMods[vk] = true   // a held modifier is being used: it's a hold, not a tap
            send(key.vk, true)
            startRepeat(key)
        }
        invalidate()
    }

    private fun release(key: Key) {
        Haptics.release(this)
        if (key.modifier) {
            val usedWhileHeld = heldMods.remove(key.vk) ?: false
            val now = SystemClock.uptimeMillis()
            val next = when {
                usedWhileHeld -> if (state(key.vk) == Mod.LOCKED) Mod.LOCKED else Mod.OFF
                state(key.vk) == Mod.OFF -> Mod.ONCE
                state(key.vk) == Mod.ONCE && lastModTap == key.vk && now - lastModTapAt < DOUBLE_TAP_MS -> Mod.LOCKED
                else -> Mod.OFF
            }
            if (!usedWhileHeld) {
                lastModTap = key.vk
                lastModTapAt = now
            }
            mods[key.vk] = next
            if (next == Mod.OFF) modUp(key.vk)
        } else {
            if (repeating === key) {
                removeCallbacks(repeat)
                repeating = null
            }
            send(key.vk, false)
            // Modifiers that were on for one key go off now (unless a finger still holds them).
            for ((vk, mode) in mods.toList()) {
                if (mode == Mod.ONCE && !heldMods.containsKey(vk)) {
                    mods[vk] = Mod.OFF
                    modUp(vk)
                }
            }
        }
        postDelayed({
            if (key !in down.values) {
                shownDown.remove(key)
                invalidate()
            }
        }, 90)
        invalidate()
    }

    private var repeating: Key? = null
    private val repeat = object : Runnable {
        override fun run() {
            val key = repeating ?: return
            send(key.vk, true)
            postDelayed(this, REPEAT_EVERY_MS)
        }
    }

    private fun startRepeat(key: Key) {
        removeCallbacks(repeat)
        repeating = key
        postDelayed(repeat, REPEAT_AFTER_MS)
    }

    // ---------------------------------------------------------------- touch

    // The first finger waits a moment (or until it lifts, a tap) before pressing, so a swipe can still turn the page;
    // another finger presses at once, and so does the first one then.
    private val touchSlop = ViewConfiguration.get(context).scaledTouchSlop
    private val pressDelay = ViewConfiguration.getTapTimeout().toLong()
    private var firstPointer = -1
    private var firstKey: Key? = null
    private var downX = 0f
    private var downY = 0f
    private val startFirst = Runnable { beginFirst() }

    private fun beginFirst() {
        val key = firstKey ?: return
        removeCallbacks(startFirst)
        firstKey = null
        parent?.requestDisallowInterceptTouchEvent(true)
        down[firstPointer] = key
        press(key)
    }

    // Where a touch is across and down the keyboard itself (turned a quarter, when sideways).
    private fun keyX(x: Float, y: Float) = if (sideways) height - y else x
    private fun keyY(x: Float, y: Float) = if (sideways) x else y

    private var actionDown = false

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(event: MotionEvent): Boolean {
        if (actionTouch(event)) return true
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                val key = keyAt(keyX(event.x, event.y), keyY(event.x, event.y)) ?: return false
                firstPointer = event.getPointerId(0)
                firstKey = key
                downX = event.x
                downY = event.y
                postDelayed(startFirst, pressDelay)
            }
            MotionEvent.ACTION_POINTER_DOWN -> {
                beginFirst()
                val i = event.actionIndex
                val key = keyAt(keyX(event.getX(i), event.getY(i)), keyY(event.getX(i), event.getY(i))) ?: return true
                parent?.requestDisallowInterceptTouchEvent(true)
                down[event.getPointerId(i)] = key
                press(key)
            }
            MotionEvent.ACTION_MOVE -> {
                if (firstKey != null && (abs(event.x - downX) > touchSlop || abs(event.y - downY) > touchSlop)) {
                    removeCallbacks(startFirst)   // a swipe: the page turns, nothing is pressed
                    firstKey = null
                }
            }
            MotionEvent.ACTION_POINTER_UP, MotionEvent.ACTION_UP -> {
                val id = event.getPointerId(event.actionIndex)
                if (id == firstPointer && firstKey != null) beginFirst()   // a quick tap
                down.remove(id)?.let { release(it) }
                if (event.actionMasked == MotionEvent.ACTION_UP) firstPointer = -1
            }
            MotionEvent.ACTION_CANCEL -> {
                removeCallbacks(startFirst)
                firstKey = null
                for (key in down.values.toList()) release(key)
                down.clear()
                firstPointer = -1
            }
        }
        return true
    }

    /** The [action] button: pressed, and tapped when lifted on it. True while it has the touch. */
    private fun actionTouch(event: MotionEvent): Boolean {
        val a = keyX(event.x, event.y)
        val d = keyY(event.x, event.y)
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                if (action == null || !actionRect.contains(a, d)) return false
                actionDown = true
                Haptics.press(this)
                invalidate()
                return true
            }
            MotionEvent.ACTION_MOVE -> return actionDown
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                if (!actionDown) return false
                actionDown = false
                invalidate()
                if (event.actionMasked == MotionEvent.ACTION_UP && actionRect.contains(a, d)) {
                    Haptics.release(this)
                    onAction?.invoke()
                }
                return true
            }
        }
        return actionDown
    }

    private fun keyAt(x: Float, y: Float): Key? {
        // Nearest key, so the gaps between keys still count.
        var best: Key? = null
        var bestDistance = Float.MAX_VALUE
        for (key in keys) {
            val r = key.rect
            val dx = if (x < r.left) r.left - x else if (x > r.right) x - r.right else 0f
            val dy = if (y < r.top) r.top - y else if (y > r.bottom) y - r.bottom else 0f
            val d = dx * dx + dy * dy
            if (d < bestDistance) {
                bestDistance = d
                best = key
            }
        }
        return if (bestDistance <= (gap * 2) * (gap * 2)) best else null
    }

    // ---------------------------------------------------------------- drawing

    private val density = resources.displayMetrics.density
    private val gap = 3 * density          // between keys
    private val depth = 3 * density        // how far a key's side shows below it
    private val travel = 2.5f * density    // how far a key goes down
    private val radius = 6 * density
    private var top = 0f
    private var rowPitch = 0f

    private fun paint(color: Int) = Paint(Paint.ANTI_ALIAS_FLAG).apply { this.color = context.getColor(color) }

    private val edgePaint = paint(R.color.key_edge)
    private val rimPaint = paint(R.color.key_rim)
    private val pressedRimPaint = paint(R.color.key_pressed_rim)
    private val onRimPaint = paint(R.color.good_rim)
    private val onEdgePaint = paint(R.color.good_edge)
    private val facePaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val labelPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = context.getColor(R.color.ink)
        textAlign = Paint.Align.CENTER
    }
    private val smallPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = context.getColor(R.color.muted)
        textAlign = Paint.Align.CENTER
    }
    private val hintBack = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xCC0B0E14.toInt() }
    private val hintPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = context.getColor(R.color.ink)
        textAlign = Paint.Align.CENTER
        textSize = 15 * resources.displayMetrics.scaledDensity
    }
    // Per row: the face's gradient up and down, pressed, and lit (a modifier that's on).
    private var faces = emptyList<Shader>()
    private var pressedFaces = emptyList<Shader>()
    private var onFaces = emptyList<Shader>()

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        layoutKeys()
    }

    // The keyboard's own width and height: the view's, or the other way round when sideways.
    private val keyboardWidth get() = if (sideways) height else width
    private val keyboardHeight get() = if (sideways) width else height
    private val actionRect = RectF()
    private val actionPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = context.getColor(R.color.surface) }
    private val actionPressedPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = context.getColor(R.color.surface_pressed) }
    private val actionTextPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = context.getColor(R.color.good)
        textAlign = Paint.Align.CENTER
        isFakeBoldText = true
        textSize = 14 * resources.displayMetrics.scaledDensity
    }

    private fun layoutKeys() {
        val w = keyboardWidth
        val h = keyboardHeight
        if (w <= 0 || h <= 0) return
        val unit = w / UNITS
        val header = if (action != null) 52 * density else 0f   // room for the action above the keys
        rowPitch = min((h - header) / rows.size, MOST_ROW_DP * density)
        top = h - rowPitch * rows.size   // at the bottom, where thumbs reach
        action?.let {
            val pillWidth = actionTextPaint.measureText(it) + 40 * density
            val pillHeight = 38 * density
            val middle = top / 2
            actionRect.set(w - 4 * density - pillWidth, middle - pillHeight / 2, w - 4 * density, middle + pillHeight / 2)
        }
        rows.forEachIndexed { r, row ->
            var x = 0f
            val y = top + r * rowPitch
            for (key in row) {
                key.rect.set(x + gap / 2, y + gap / 2, x + key.units * unit - gap / 2, y + rowPitch - gap / 2 - depth)
                x += key.units * unit
            }
        }
        fun shaders(from: Int, to: Int) = rows.indices.map { r ->
            val y = top + r * rowPitch
            LinearGradient(0f, y, 0f, y + rowPitch - depth, context.getColor(from), context.getColor(to), Shader.TileMode.CLAMP)
        }
        faces = shaders(R.color.key_top, R.color.key_bottom)
        pressedFaces = shaders(R.color.key_pressed_top, R.color.key_pressed_bottom)
        onFaces = shaders(R.color.good_top, R.color.good_bottom)
        val keyHeight = rowPitch - gap - depth
        labelPaint.textSize = min(keyHeight * 0.36f, unit * 0.55f)
        smallPaint.textSize = labelPaint.textSize * 0.72f
    }

    private val drawRect = RectF()

    override fun onDraw(canvas: Canvas) {
        if (faces.isEmpty()) return
        canvas.save()
        if (sideways) {
            // The keyboard's top along the screen's left edge, its left along the bottom.
            canvas.translate(0f, height.toFloat())
            canvas.rotate(-90f)
        }
        action?.let {
            val radius = actionRect.height() / 2
            canvas.drawRoundRect(actionRect, radius, radius, if (actionDown) actionPressedPaint else actionPaint)
            canvas.drawText(it, actionRect.centerX(), actionRect.centerY() - (actionTextPaint.descent() + actionTextPaint.ascent()) / 2, actionTextPaint)
        }
        drawKeys(canvas)
        canvas.restore()
    }

    private fun drawKeys(canvas: Canvas) {
        val shifted = shift
        for (key in keys) {
            val sunk = key in shownDown
            val on = key.modifier && (state(key.vk) != Mod.OFF || heldMods.containsKey(key.vk))
            val r = key.rect
            val dy = if (sunk) travel else 0f
            // Its side, a light rim along the top, then the face.
            drawRect.set(r.left, r.top + depth, r.right, r.bottom + depth)
            canvas.drawRoundRect(drawRect, radius, radius, if (on) onEdgePaint else edgePaint)
            drawRect.set(r.left, r.top + dy, r.right, r.bottom + dy)
            canvas.drawRoundRect(drawRect, radius, radius, if (sunk) pressedRimPaint else if (on) onRimPaint else rimPaint)
            drawRect.top += density
            facePaint.shader = (if (sunk) pressedFaces else if (on) onFaces else faces)[key.row]
            canvas.drawRoundRect(drawRect, radius, radius, facePaint)
            // A key locked down (tapped twice) has a bar under its label.
            val cx = drawRect.centerX()
            val cy = drawRect.centerY()
            labelPaint.color = context.getColor(if (on) R.color.background else R.color.ink)
            val words = key.label.length > 1 && key.label != "⌫"
            labelPaint.textSize = if (words) smallPaint.textSize else smallPaint.textSize / 0.72f
            val room = drawRect.width() - 4 * density   // "PrtSc" on a small key: shrink to fit
            val wide = labelPaint.measureText(key.label)
            if (wide > room && wide > 0) labelPaint.textSize *= room / wide
            if (key.shifted != null) {
                // A symbol key, like a real keycap: the Shift symbol above, the plain one below; the one Shift gives is bright.
                smallPaint.color = context.getColor(if (shifted) R.color.ink else R.color.muted)
                canvas.drawText(key.shifted, cx, cy - drawRect.height() * 0.06f, smallPaint)
                labelPaint.color = context.getColor(if (shifted) R.color.muted else R.color.ink)
                canvas.drawText(key.label, cx, cy + drawRect.height() * 0.32f, labelPaint)
            } else {
                canvas.drawText(key.label, cx, cy - (labelPaint.descent() + labelPaint.ascent()) / 2, labelPaint)
            }
            if (key.modifier && state(key.vk) == Mod.LOCKED) {
                val w = drawRect.width() * 0.3f
                canvas.drawRect(cx - w / 2, drawRect.bottom - 5 * density, cx + w / 2, drawRect.bottom - 3.5f * density, labelPaint)
            }
        }
        hint?.let {
            canvas.drawRect(0f, top, keyboardWidth.toFloat(), keyboardHeight.toFloat(), hintBack)
            canvas.drawText(it, keyboardWidth / 2f, top + rowPitch * rows.size / 2f, hintPaint)
        }
    }
}
