package com.couchtv.remote

import android.animation.AnimatorInflater
import android.content.Context
import android.content.res.ColorStateList
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
 * The bottom row of the remote, five across: [home] in the middle, and the TV's first four streaming apps either side
 * of it, each a rounded square with the app's own logo on its own colours (an app without a logo here gets its
 * initial on the TV's colours for its tile), its name under it. A tap opens it on the TV ([onOpen]). The TV's other
 * apps are with the shortcuts on the first page.
 */
class AppsGrid @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : LinearLayout(context, attrs) {
    companion object {
        /** How many apps the row has room for, beside Home. */
        const val ROOM = 4
    }

    var onOpen: ((TvApp) -> Unit)? = null

    private val density = resources.displayMetrics.density
    private var shown: List<TvApp>? = null

    /** Home, in the middle: a key like the D-pad's centre button, held like the remote's other keys. */
    val home: View = cell(FrameLayout(context).apply {
        background = GradientDrawable(GradientDrawable.Orientation.TOP_BOTTOM,
            intArrayOf(context.getColor(R.color.key_raised_top), context.getColor(R.color.key_raised_bottom))).apply {
            cornerRadius = dp(18).toFloat()
            setStroke(dp(1), context.getColor(R.color.key_raised_rim))
        }
        addView(ImageView(context).apply {
            setImageResource(R.drawable.ic_home)
            imageTintList = ColorStateList.valueOf(context.getColor(R.color.ink))
            scaleType = ImageView.ScaleType.CENTER_INSIDE
            setPadding(dp(15), dp(15), dp(15), dp(15))
        }, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))
    }, context.getString(R.string.home)).apply {
        stateListAnimator = AnimatorInflater.loadStateListAnimator(context, R.animator.key_press)
    }

    init {
        orientation = HORIZONTAL
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
            val view = when {
                i == 2 -> home
                app != null -> tile(app)
                else -> View(context)
            }
            addView(view, LayoutParams(0, LayoutParams.WRAP_CONTENT, 1f))
        }
    }

    private fun tile(app: TvApp): View {
        val look = Logos.of(app)
        val square = FrameLayout(context).apply {
            background = GradientDrawable().apply {
                cornerRadius = dp(18).toFloat()
                setColor(look?.background ?: app.background)
                setStroke(dp(1), 0x26FFFFFF)
            }
            if (look != null) {
                addView(ImageView(context).apply {
                    setImageResource(look.logo)
                    scaleType = ImageView.ScaleType.FIT_CENTER
                    val pad = dp(look.padding * 56 / 62)
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
        return cell(square, app.label).apply {
            isClickable = true
            isFocusable = true
            stateListAnimator = AnimatorInflater.loadStateListAnimator(context, R.animator.card_press)
            setOnClickListener {
                Haptics.press(it)
                onOpen?.invoke(app)
            }
        }
    }

    /** A 56 dp square with its name under it. */
    private fun cell(square: View, name: String): LinearLayout {
        val label = TextView(context).apply {
            text = name
            setTextColor(context.getColor(R.color.muted))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 11f)
            gravity = Gravity.CENTER
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
            setPadding(dp(2), dp(5), dp(2), 0)
        }
        return LinearLayout(context).apply {
            orientation = VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            contentDescription = name
            addView(square, LayoutParams(dp(56), dp(56)))
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
