package com.couchtv.remote

import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.util.AttributeSet
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView

/**
 * The Settings page: the TV's settings (as the TV lists them, so new ones show up without updating the app), and this
 * phone's. On and off is a switch; anything else, a row of choices to tap. A change goes at once ([onTvChange],
 * [onPhoneChange]), and the TV says back what it now is.
 */
class SettingsView @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : ScrollView(context, attrs) {
    var onTvChange: ((String, String) -> Unit)? = null
    var onPhoneChange: ((String, String) -> Unit)? = null
    var onVersion: (() -> Unit)? = null

    private val column = LinearLayout(context).apply { orientation = LinearLayout.VERTICAL }

    init {
        isVerticalScrollBarEnabled = false
        overScrollMode = OVER_SCROLL_IF_CONTENT_SCROLLS
        addView(column, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT))
    }

    /** [tv]: the TV's, empty while it isn't connected; [phone]: this phone's; [version]: the app's version line. */
    fun show(tv: List<TvSetting>, phone: List<TvSetting>, version: String) {
        column.removeAllViews()
        column.addView(TextView(context, null, 0, R.style.PageTitle).apply { setText(R.string.settings_title) }, wrap(0, 4))
        heading(R.string.settings_tv)
        if (tv.isEmpty()) {
            column.addView(TextView(context).apply {
                setText(R.string.settings_tv_offline)
                setTextColor(context.getColor(R.color.muted))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
            }, wrap(0, 4))
        } else for (s in tv) row(s) { value -> onTvChange?.invoke(s.key, value) }
        heading(R.string.settings_phone)
        for (s in phone) row(s) { value -> onPhoneChange?.invoke(s.key, value) }
        column.addView(TextView(context).apply {
            text = version
            setTextColor(context.getColor(R.color.muted))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
            gravity = Gravity.CENTER
            setPadding(0, dp(18), 0, dp(28))
            setOnClickListener { onVersion?.invoke() }
        }, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT))
    }

    private fun heading(text: Int) {
        column.addView(TextView(context, null, 0, R.style.Section).apply { setText(text) }, wrap(22, 4))
    }

    /** A setting: its name and note, and a switch beside them, or its choices under them. */
    private fun row(s: TvSetting, change: (String) -> Unit) {
        val words = LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL
            addView(TextView(context).apply {
                text = s.label
                setTextColor(context.getColor(R.color.ink))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 16f)
                typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            })
            s.note?.let { note ->
                addView(TextView(context).apply {
                    text = note
                    setTextColor(context.getColor(R.color.muted))
                    setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
                    setPadding(0, dp(2), 0, 0)
                })
            }
        }
        val line = LinearLayout(context).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            addView(words, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        }
        val block = LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, dp(12), 0, dp(12))
            addView(line)
        }
        if (s.isSwitch) {
            val switch = Switch(context).apply {
                isChecked = s.value == "on"
                thumbTintList = ColorStateList(arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()),
                    intArrayOf(context.getColor(R.color.good), context.getColor(R.color.muted)))
                trackTintList = ColorStateList(arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()),
                    intArrayOf(context.getColor(R.color.good_edge), context.getColor(R.color.surface_pressed)))
                setOnCheckedChangeListener { v, on ->
                    Haptics.press(v)
                    change(if (on) "on" else "off")
                }
            }
            line.addView(switch, LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply { marginStart = dp(12) })
            block.setOnClickListener { switch.toggle() }
        } else {
            val chips = LinearLayout(context).apply { orientation = LinearLayout.HORIZONTAL }
            val views = ArrayList<TextView>()
            fun paint(picked: String) {
                s.choices.forEachIndexed { i, (value, _) -> chip(views[i], value == picked) }
            }
            s.choices.forEachIndexed { i, (value, name) ->
                val chip = TextView(context).apply {
                    text = name
                    gravity = Gravity.CENTER
                    maxLines = 1
                    setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
                    typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
                    setOnClickListener {
                        Haptics.press(it)
                        paint(value)   // at once; the TV says back what it now is
                        change(value)
                    }
                }
                views += chip
                chips.addView(chip, LinearLayout.LayoutParams(0, dp(40), 1f).apply {
                    marginStart = if (i == 0) 0 else dp(4)
                    marginEnd = if (i == s.choices.size - 1) 0 else dp(4)
                })
            }
            paint(s.value)
            block.addView(chips, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(10) })
        }
        column.addView(block)
        column.addView(View(context).apply { setBackgroundColor(context.getColor(R.color.surface)) },
            LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(1)))
    }

    private fun chip(view: TextView, picked: Boolean) {
        view.background = GradientDrawable().apply {
            cornerRadius = dp(20).toFloat()
            setColor(context.getColor(if (picked) R.color.good else R.color.surface))
        }
        view.setTextColor(context.getColor(if (picked) R.color.background else R.color.ink))
    }

    private fun dp(value: Int) = (value * resources.displayMetrics.density + 0.5f).toInt()

    private fun wrap(top: Int, bottom: Int) =
        LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply {
            topMargin = dp(top)
            bottomMargin = dp(bottom)
        }
}
