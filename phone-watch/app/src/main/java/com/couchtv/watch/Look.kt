package com.couchtv.watch

import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.Drawable
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.text.TextUtils
import android.util.TypedValue
import android.view.Gravity
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView

/** CouchTV's colours, as on the TV's home screen and the remote. Screens are built in code, with the helpers below. */
object Look {
    const val BACKGROUND = 0xFF0B0E14.toInt()
    const val SURFACE = 0xFF1C222D.toInt()
    const val RAISED = 0xFF262D3A.toInt()
    const val INK = 0xFFF4F6FA.toInt()
    const val MUTED = 0xFFA3ACBC.toInt()
    const val FAINT = 0xFF6E7787.toInt()
    const val GOOD = 0xFF3DDC84.toInt()
}

fun Context.dp(value: Int): Int = (value * resources.displayMetrics.density + 0.5f).toInt()

fun Context.dpf(value: Float): Float = value * resources.displayMetrics.density

fun Context.label(sp: Float, color: Int, bold: Boolean = false): TextView = TextView(this).apply {
    setTextSize(TypedValue.COMPLEX_UNIT_SP, sp)
    setTextColor(color)
    if (bold) typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
}

fun rounded(color: Int, radius: Float, strokeWidth: Int = 0, strokeColor: Int = 0): GradientDrawable = GradientDrawable().apply {
    setColor(color)
    cornerRadius = radius
    if (strokeWidth > 0) setStroke(strokeWidth, strokeColor)
}

/** A press shows as a soft light over [shape] (or, without one, a round glow, for icon buttons). */
fun pressable(shape: Drawable?): RippleDrawable = RippleDrawable(ColorStateList.valueOf(0x33FFFFFF), shape, null)

/** A box as tall as its width times [ratio] (the hero's 16:9). */
class RatioFrame(context: Context, private val ratio: Float) : FrameLayout(context) {
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val width = MeasureSpec.getSize(widthMeasureSpec)
        super.onMeasure(MeasureSpec.makeMeasureSpec(width, MeasureSpec.EXACTLY),
            MeasureSpec.makeMeasureSpec((width * ratio).toInt(), MeasureSpec.EXACTLY))
    }
}

/** A title's poster (a song's cover is square), with a ring while it's the one at the top. */
class PosterView(context: Context, val title: Title, private val square: Boolean) : FrameLayout(context) {
    private val radius = context.dpf(10f)

    init {
        background = rounded(Look.SURFACE, radius)
        clipToOutline = true
        val picture = ImageView(context).apply { scaleType = ImageView.ScaleType.CENTER_CROP }
        addView(picture, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT))
        title.poster?.let { Pictures.load(context, it, picture, 360) }
        contentDescription = title.title
        isClickable = true
        isFocusable = true
    }

    var chosen = false
        set(value) {
            field = value
            foreground = if (value) rounded(Color.TRANSPARENT, radius, context.dp(2), Look.INK) else null
        }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val width = MeasureSpec.getSize(widthMeasureSpec)
        val height = if (square) width else width * 3 / 2
        super.onMeasure(MeasureSpec.makeMeasureSpec(width, MeasureSpec.EXACTLY), MeasureSpec.makeMeasureSpec(height, MeasureSpec.EXACTLY))
    }
}

/** A pill-shaped button with an icon and words: white for the main one (Watch), quiet for the other (Play on TV). */
class PillButton(context: Context, icon: Int, filled: Boolean) : LinearLayout(context) {
    private val words: TextView

    init {
        orientation = HORIZONTAL
        gravity = Gravity.CENTER
        val ink = if (filled) Look.BACKGROUND else Look.INK
        background = pressable(rounded(if (filled) Look.INK else Look.RAISED, context.dpf(24f)))
        setPadding(context.dp(14), 0, context.dp(14), 0)
        val image = ImageView(context).apply {
            setImageResource(icon)
            imageTintList = ColorStateList.valueOf(ink)
        }
        addView(image, LayoutParams(context.dp(20), context.dp(20)).apply { marginEnd = context.dp(8) })
        words = context.label(15f, ink, bold = true).apply {
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
        }
        addView(words, LayoutParams(LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT))
        isClickable = true
        isFocusable = true
    }

    var text: CharSequence
        get() = words.text
        set(value) {
            words.text = value
            contentDescription = value
        }
}
