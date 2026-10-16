package com.couchtv.remote

import android.content.Context
import android.graphics.BlurMaskFilter
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.Path
import android.util.AttributeSet
import android.view.View

/**
 * The TV's pointer drawn as CouchTV draws it (src/Pointer.cs): an arrow, a dot or a ring, in its colour, with a dark
 * outline and a soft shadow. [scale] is dp per TV pixel, so the sizes compare as they will on the TV.
 */
class PointerPreview @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : View(context, attrs) {
    var style = "arrow"
        set(value) { field = value; invalidate() }
    var size = 64
        set(value) { field = value; invalidate() }
    var color = 0xFFFFFFFF.toInt()
        set(value) { field = value; invalidate() }
    var scale = 0.6f
        set(value) { field = value; invalidate() }

    private val density = resources.displayMetrics.density
    private val fill = Paint(Paint.ANTI_ALIAS_FLAG)
    private val outline = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeJoin = Paint.Join.ROUND
        strokeCap = Paint.Cap.ROUND
        color = 0xFF0B0E14.toInt()
    }
    private val shadow = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0x8C000000.toInt() }
    private val arrow = Path()

    init {
        setLayerType(LAYER_TYPE_SOFTWARE, null)   // the blur
    }

    override fun onDraw(canvas: Canvas) {
        val s = size * scale * density
        canvas.save()
        canvas.translate(width / 2f - s / 2, height / 2f - s / 2)
        shadow.maskFilter = BlurMaskFilter((s / 16).coerceAtLeast(1f), BlurMaskFilter.Blur.NORMAL)
        canvas.save()
        canvas.translate(s / 40, s / 26)
        shape(canvas, s, shadow, shadow, true)
        canvas.restore()
        fill.color = color
        shape(canvas, s, fill, outline, false)
        canvas.restore()
    }

    /** One pointer at size s (its box from 0 to s), filled and outlined. */
    private fun shape(canvas: Canvas, s: Float, body: Paint, edge: Paint, isShadow: Boolean) {
        val c = s / 2
        when (style) {
            "dot" -> {
                canvas.drawCircle(c, c, s * 0.2f, body)
                if (!isShadow) {
                    edge.strokeWidth = s * 0.055f
                    canvas.drawCircle(c, c, s * 0.2f, edge)
                }
            }
            "ring" -> {
                val ring = Paint(body).apply {
                    style = Paint.Style.STROKE
                    strokeWidth = s * 0.075f
                }
                if (!isShadow) {
                    edge.strokeWidth = s * (0.075f + 2 * 0.045f)
                    canvas.drawCircle(c, c, s * 0.3f, edge)
                }
                canvas.drawCircle(c, c, s * 0.3f, ring)
                canvas.drawCircle(c, c, s * 0.05f, body)
            }
            else -> {
                // The classic arrow, drawn on a 32-pixel grid, centred on its middle rather than its tip.
                val k = s / 32
                arrow.reset()
                val dx = (16 - 12.25f) * k
                val dy = (16 - 16.1f) * k
                val points = floatArrayOf(4f, 3f, 4f, 25.5f, 9.3f, 20.6f, 13f, 29.2f, 17f, 27.5f, 13.4f, 19.2f, 20.5f, 19.2f)
                arrow.moveTo(points[0] * k + dx, points[1] * k + dy)
                for (i in 2 until points.size step 2) arrow.lineTo(points[i] * k + dx, points[i + 1] * k + dy)
                arrow.close()
                canvas.drawPath(arrow, body)
                if (!isShadow) {
                    edge.strokeWidth = 1.8f * k
                    canvas.drawPath(arrow, edge)
                }
            }
        }
    }
}
