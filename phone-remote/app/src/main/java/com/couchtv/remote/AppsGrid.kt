package com.couchtv.remote

import android.animation.AnimatorInflater
import android.content.Context
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.text.TextUtils
import android.util.AttributeSet
import android.util.TypedValue
import android.view.Gravity
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView

/**
 * The TV's streaming apps at the bottom of the remote: up to eight, four to a row, each a rounded square with the
 * app's own logo on its own colours (an app without a logo here gets its initial on the TV's colours for its tile),
 * and its name under it. A tap opens it on the TV ([onOpen]).
 */
class AppsGrid @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : LinearLayout(context, attrs) {
    var onOpen: ((TvApp) -> Unit)? = null

    private companion object {
        const val COLUMNS = 4
        const val MOST = 8
    }

    private val density = resources.displayMetrics.density
    private var shown: List<TvApp> = emptyList()

    init {
        orientation = VERTICAL
    }

    fun show(apps: List<TvApp>) {
        val list = apps.take(MOST)
        if (list == shown) return
        shown = list
        removeAllViews()
        for (start in list.indices step COLUMNS) {
            val row = LinearLayout(context).apply { orientation = HORIZONTAL }
            for (i in start until start + COLUMNS) {
                val cell = if (i < list.size) tile(list[i]) else android.view.View(context)
                row.addView(cell, LayoutParams(0, LayoutParams.WRAP_CONTENT, 1f))
            }
            addView(row, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT).apply { if (start > 0) topMargin = dp(10) })
        }
    }

    private fun tile(app: TvApp): LinearLayout {
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
                    setPadding(dp(look.padding), dp(look.padding), dp(look.padding), dp(look.padding))
                }, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))
            } else {
                addView(TextView(context).apply {
                    text = app.label.take(1).uppercase()
                    setTextColor(app.foreground)
                    setTextSize(TypedValue.COMPLEX_UNIT_SP, 26f)
                    typeface = Typeface.DEFAULT_BOLD
                    gravity = Gravity.CENTER
                }, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))
            }
        }
        val name = TextView(context).apply {
            text = app.label
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
            isClickable = true
            isFocusable = true
            contentDescription = app.label
            stateListAnimator = AnimatorInflater.loadStateListAnimator(context, R.animator.card_press)
            addView(square, LayoutParams(dp(62), dp(62)))
            addView(name, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT))
            setOnClickListener {
                Haptics.press(it)
                onOpen?.invoke(app)
            }
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
