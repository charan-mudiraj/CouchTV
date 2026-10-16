package com.couchtv.remote

import android.animation.AnimatorInflater
import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.text.TextUtils
import android.util.AttributeSet
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView

/**
 * The dock at the bottom of the remote, five across: [home] in the middle, much bigger than the rest (a round blue
 * key standing up out of the dock, easy to find without looking), and the TV's first four streaming apps either side
 * of it on a panel, each a rounded square with the app's own logo on its own colours (an app without a logo here gets
 * its initial on the TV's colours for its tile), its name under it. A tap opens it on the TV ([onOpen]). The TV's
 * other apps are with the shortcuts on the first page.
 */
class AppsGrid @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : LinearLayout(context, attrs) {
    companion object {
        /** How many apps the dock has room for, beside Home. */
        const val ROOM = 4
        private const val TILE_DP = 54
        private const val HOME_DP = 96
        private const val HOME_WEIGHT = 1.7f   // Home's column, against an app's 1
    }

    var onOpen: ((TvApp) -> Unit)? = null

    private val density = resources.displayMetrics.density
    private var shown: List<TvApp>? = null
    private val panel = RectF()
    private val panelPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = context.getColor(R.color.surface) }
    private val rimPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = 0x14FFFFFF
        style = Paint.Style.STROKE
        strokeWidth = 1 * resources.displayMetrics.density
    }

    /**
     * Home, in the middle: the biggest key on the remote, with no name under it (the house says it), its bottom on the
     * names' line so it stands up out of the dock. Held like the remote's other keys; it sinks while pressed.
     */
    val home: View = FrameLayout(context).apply {
        background = context.getDrawable(R.drawable.home_bg)
        stateListAnimator = AnimatorInflater.loadStateListAnimator(context, R.animator.key_press)
        contentDescription = context.getString(R.string.home)
        addView(ImageView(context).apply {
            setImageResource(R.drawable.ic_home)
            imageTintList = ColorStateList.valueOf(context.getColor(R.color.ink))
            scaleType = ImageView.ScaleType.FIT_CENTER
            importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
            // On the key's face, which sits a little above the middle (its side shows below).
            setPadding(dp(29), dp(28), dp(29), dp(32))
        }, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))
    }

    init {
        orientation = HORIZONTAL
        gravity = Gravity.BOTTOM   // the names line up; Home stands taller
        setWillNotDraw(false)
        clipChildren = false
        clipToPadding = false
        setPadding(dp(2), 0, dp(2), dp(8))
        show(emptyList())
    }

    fun show(apps: List<TvApp>) {
        val list = apps.take(ROOM)
        if (list == shown) return
        shown = list
        removeAllViews()
        (home.parent as? android.view.ViewGroup)?.removeView(home)
        for (i in 0 until ROOM + 1) {
            val app = if (i < 2) list.getOrNull(i) else list.getOrNull(i - 1)
            if (i == 2) {
                // Its own size, in the middle of a wider column.
                addView(FrameLayout(context).apply {
                    clipChildren = false   // Home sinks a little below it as it's pressed
                    addView(home, FrameLayout.LayoutParams(dp(HOME_DP), dp(HOME_DP), Gravity.CENTER_HORIZONTAL))
                }, LayoutParams(0, LayoutParams.WRAP_CONTENT, HOME_WEIGHT))
                continue
            }
            addView(if (app != null) tile(app) else View(context), LayoutParams(0, LayoutParams.WRAP_CONTENT, 1f))
        }
    }

    /** The panel behind the apps; Home stands up out of it. */
    override fun onDraw(canvas: Canvas) {
        val top = height - paddingBottom - dp(TILE_DP) - label() - dp(10)
        panel.set(0f, top.toFloat(), width.toFloat(), height.toFloat())
        val radius = dp(22).toFloat()
        canvas.drawRoundRect(panel, radius, radius, panelPaint)
        panel.inset(rimPaint.strokeWidth / 2, rimPaint.strokeWidth / 2)
        canvas.drawRoundRect(panel, radius, radius, rimPaint)
    }

    /** How tall a name under a tile is. */
    private fun label(): Int {
        val child = (0 until childCount).map { getChildAt(it) }.firstOrNull { it is LinearLayout } as? LinearLayout
        return child?.getChildAt(1)?.height ?: dp(19)
    }

    private fun tile(app: TvApp): View {
        val look = Logos.of(app)
        val square = FrameLayout(context).apply {
            background = GradientDrawable().apply {
                cornerRadius = dp(16).toFloat()
                setColor(look?.background ?: app.background)
                setStroke(dp(1), 0x26FFFFFF)
            }
            if (look != null) {
                addView(ImageView(context).apply {
                    setImageResource(look.logo)
                    scaleType = ImageView.ScaleType.FIT_CENTER
                    val pad = dp(look.padding * TILE_DP / 62)
                    setPadding(pad, pad, pad, pad)
                }, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))
            } else {
                addView(TextView(context).apply {
                    text = app.label.take(1).uppercase()
                    setTextColor(app.foreground)
                    setTextSize(TypedValue.COMPLEX_UNIT_SP, 24f)
                    typeface = Typeface.DEFAULT_BOLD
                    gravity = Gravity.CENTER
                }, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))
            }
        }
        return cell(square, app.label, TILE_DP).apply {
            isClickable = true
            isFocusable = true
            stateListAnimator = AnimatorInflater.loadStateListAnimator(context, R.animator.card_press)
            setOnClickListener {
                Haptics.press(it)
                onOpen?.invoke(app)
            }
        }
    }

    /** A square [size] dp across with its name under it. */
    private fun cell(square: View, name: String, size: Int): LinearLayout {
        val label = TextView(context).apply {
            text = name
            setTextColor(context.getColor(R.color.muted))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 11f)
            gravity = Gravity.CENTER
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
            setPadding(dp(2), dp(4), dp(2), 0)
        }
        return LinearLayout(context).apply {
            orientation = VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            contentDescription = name
            addView(square, LayoutParams(dp(size), dp(size)))
            addView(label, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT))
        }
    }

    private fun dp(value: Int) = (value * density + 0.5f).toInt()

    /** The logos the app has, by the app's name: drawn on a tile of the brand's own colour. */
    object Logos {
        class Look(val logo: Int, val background: Int, val padding: Int)

        fun of(app: TvApp): Look? {
            val name = (app.name + " " + app.label).lowercase()
            return when {
                "netflix" in name -> Look(R.drawable.logo_netflix, 0xFF000000.toInt(), 14)
                "youtube" in name && ("for tv" in name || "youtube tv" in name) -> Look(R.drawable.logo_youtubetv, 0xFFFFFFFF.toInt(), 12)
                "youtube" in name -> Look(R.drawable.logo_youtube, 0xFFFFFFFF.toInt(), 12)
                "prime" in name -> Look(R.drawable.logo_primevideo, 0xFF0F171E.toInt(), 8)
                "hotstar" in name -> Look(R.drawable.logo_jiohotstar, 0xFF0E1122.toInt(), 8)
                "spotify" in name -> Look(R.drawable.logo_spotify, 0xFF000000.toInt(), 13)
                "kodi" in name -> Look(R.drawable.logo_kodi, 0xFF1B1B1B.toInt(), 13)
                "vlc" in name -> Look(R.drawable.logo_vlc, 0xFFFFFFFF.toInt(), 12)
                else -> null
            }
        }
    }
}
