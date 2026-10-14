package com.couchtv.remote

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.os.SystemClock
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View
import kotlin.math.min
import kotlin.math.roundToInt

/**
 * The TV's screen, as ScreenLink brings it: a picture kept up to date strip by strip, scaled to fit, with the TV's
 * mouse pointer on it. Fingers on it go to the TV as touches there (CouchTV plays them as a touchscreen's): tap to
 * click, drag to scroll, pinch to zoom, hold to right-click.
 */
class MirrorView @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : View(context, attrs) {
    private companion object {
        const val FINGERS = 10
        const val MOVE_EVERY_MS = 16L   // at most 60 moves a second a finger
    }

    /** A finger: its number, 'D' down, 'M' moved, 'U' up, and where on the TV's screen, 0 to 65535 across and down. */
    var onTouch: ((Int, Char, Int, Int) -> Unit)? = null

    private val lock = Any()
    private var frame: Bitmap? = null
    private var frameCanvas: Canvas? = null
    @Volatile private var cursorX = -1
    @Volatile private var cursorY = -1
    @Volatile private var cursorShown = false
    @Volatile private var message: String? = null
    private val shown = RectF()   // where the picture is drawn (UI thread)
    private val paint = Paint(Paint.FILTER_BITMAP_FLAG or Paint.ANTI_ALIAS_FLAG)
    private val density = resources.displayMetrics.density
    private val textPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = context.getColor(R.color.muted)
        textAlign = Paint.Align.CENTER
        textSize = 16 * density
    }
    private val pointerFill = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE }
    private val pointerEdge = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.BLACK
        style = Paint.Style.STROKE
        strokeWidth = 2 * density
    }
    private val decode = BitmapFactory.Options().apply { inPreferredConfig = Bitmap.Config.ARGB_8888 }

    // ---------------------------------------------------------------- from ScreenLink (its thread)

    /** A line instead of the picture ("Connecting…"), or null. */
    fun status(text: String?) {
        message = text
        postInvalidate()
    }

    /** A new picture this size: the strips that follow fill it. */
    fun begin(width: Int, height: Int) {
        // The old picture isn't recycled: Android may still be drawing it. The garbage collector frees it.
        synchronized(lock) { frame = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888).also { frameCanvas = Canvas(it) } }
    }

    /** A JPEG strip of what changed, at x, y on the picture. */
    fun tile(x: Int, y: Int, data: ByteArray, offset: Int, length: Int) {
        val strip = BitmapFactory.decodeByteArray(data, offset, length, decode) ?: return
        synchronized(lock) { frameCanvas?.drawBitmap(strip, x.toFloat(), y.toFloat(), null) }
        strip.recycle()
    }

    /** The strips of one look at the screen are in: show them. */
    fun commit() {
        message = null
        postInvalidate()
    }

    fun cursor(x: Int, y: Int, visible: Boolean) {
        cursorX = x
        cursorY = y
        cursorShown = visible
        postInvalidate()
    }

    fun clear() {
        synchronized(lock) {
            frame = null
            frameCanvas = null
        }
        postInvalidate()
    }

    // ---------------------------------------------------------------- drawing

    override fun onDraw(canvas: Canvas) {
        canvas.drawColor(Color.BLACK)
        synchronized(lock) {
            val picture = frame
            if (picture != null) {
                val scale = min(width.toFloat() / picture.width, height.toFloat() / picture.height)
                val w = picture.width * scale
                val h = picture.height * scale
                shown.set((width - w) / 2, (height - h) / 2, (width + w) / 2, (height + h) / 2)
                canvas.drawBitmap(picture, null, shown, paint)
                if (cursorShown && cursorX >= 0) {
                    val x = shown.left + cursorX * scale
                    val y = shown.top + cursorY * scale
                    canvas.drawCircle(x, y, 6 * density, pointerFill)
                    canvas.drawCircle(x, y, 6 * density, pointerEdge)
                }
            } else shown.setEmpty()
        }
        message?.let { canvas.drawText(it, width / 2f, height / 2f, textPaint) }
    }

    // ---------------------------------------------------------------- fingers

    private val down = BooleanArray(FINGERS)
    private val lastMove = LongArray(FINGERS)
    private val lastX = FloatArray(FINGERS)
    private val lastY = FloatArray(FINGERS)

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN -> {
                val i = event.actionIndex
                val id = event.getPointerId(i)
                if (id < FINGERS && shown.contains(event.getX(i), event.getY(i))) {
                    down[id] = true
                    send(id, 'D', event.getX(i), event.getY(i))
                }
            }
            MotionEvent.ACTION_MOVE -> {
                val now = SystemClock.uptimeMillis()
                for (i in 0 until event.pointerCount) {
                    val id = event.getPointerId(i)
                    if (id < FINGERS && down[id] && now - lastMove[id] >= MOVE_EVERY_MS) {
                        lastMove[id] = now
                        send(id, 'M', event.getX(i), event.getY(i))
                    }
                }
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP -> {
                val i = event.actionIndex
                val id = event.getPointerId(i)
                if (id < FINGERS && down[id]) {
                    down[id] = false
                    send(id, 'U', event.getX(i), event.getY(i))
                }
            }
            MotionEvent.ACTION_CANCEL -> for (id in 0 until FINGERS) if (down[id]) {
                down[id] = false
                send(id, 'U', lastX[id], lastY[id])
            }
        }
        return true
    }

    private fun send(id: Int, kind: Char, x: Float, y: Float) {
        lastX[id] = x
        lastY[id] = y
        if (shown.isEmpty) return
        val nx = ((x - shown.left) / shown.width() * 65535).roundToInt().coerceIn(0, 65535)
        val ny = ((y - shown.top) / shown.height() * 65535).roundToInt().coerceIn(0, 65535)
        onTouch?.invoke(id, kind, nx, ny)
    }
}
