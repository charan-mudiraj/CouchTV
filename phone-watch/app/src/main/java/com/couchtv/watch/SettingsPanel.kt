package com.couchtv.watch

import android.content.Context
import android.content.res.ColorStateList
import android.view.Gravity
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView

/** Settings, over the home: which apps you have (their tabs and rows), and the version, to check for an update. */
class SettingsPanel(context: Context) : LinearLayout(context) {
    var onAppsChanged: (() -> Unit)? = null
    var onCheckUpdate: (() -> Unit)? = null
    var onClose: (() -> Unit)? = null

    private val switches = ArrayList<Pair<StreamApp, Switch>>()
    private val version: TextView

    init {
        orientation = VERTICAL
        setBackgroundColor(Look.BACKGROUND)
        isClickable = true   // touches stay here, off the home beneath

        val top = LinearLayout(context).apply {
            orientation = HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(context.dp(4), context.dp(6), context.dp(16), context.dp(6))
        }
        val back = ImageView(context).apply {
            setImageResource(R.drawable.ic_arrow_back)
            imageTintList = ColorStateList.valueOf(Look.INK)
            scaleType = ImageView.ScaleType.CENTER
            background = pressable(null)
            contentDescription = "Back"
            setOnClickListener { close() }
        }
        top.addView(back, LayoutParams(context.dp(48), context.dp(48)))
        top.addView(context.label(20f, Look.INK, bold = true).apply { text = "Settings" })
        addView(top, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT))

        val list = LinearLayout(context).apply {
            orientation = VERTICAL
            setPadding(context.dp(20), 0, context.dp(20), context.dp(24))
        }
        list.addView(section("Your apps"))
        list.addView(context.label(14f, Look.FAINT).apply {
            text = "Each has a tab, and its films and series in the rows. A title opens in its app on this phone."
            setPadding(0, 0, 0, context.dp(8))
        })
        for (app in Apps.ALL) {
            val row = LinearLayout(context).apply {
                orientation = HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                minimumHeight = context.dp(52)
            }
            row.addView(context.label(16f, Look.INK).apply { text = app.label }, LayoutParams(0, LayoutParams.WRAP_CONTENT, 1f))
            val toggle = Switch(context).apply {
                thumbTintList = ColorStateList(arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()), intArrayOf(Look.GOOD, Look.MUTED))
                trackTintList = ColorStateList(arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()), intArrayOf(0x663DDC84, Look.RAISED))
                contentDescription = app.label
                setOnCheckedChangeListener { _, on ->
                    Apps.setYours(context, app.key, on)
                    onAppsChanged?.invoke()
                }
            }
            row.addView(toggle)
            row.setOnClickListener { toggle.toggle() }
            list.addView(row, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT))
            switches.add(app to toggle)
        }

        list.addView(section("About"))
        val about = LinearLayout(context).apply {
            orientation = VERTICAL
            background = pressable(rounded(Look.SURFACE, context.dpf(16f)))
            setPadding(context.dp(16), context.dp(14), context.dp(16), context.dp(14))
            setOnClickListener { onCheckUpdate?.invoke() }
        }
        about.addView(context.label(16f, Look.INK).apply { text = "CouchTV Watch ${BuildConfig.VERSION_NAME}" })
        version = context.label(14f, Look.MUTED).apply { text = "Tap to check for an update" }
        about.addView(version)
        list.addView(about, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT))

        val scroll = ScrollView(context).apply {
            isVerticalScrollBarEnabled = false
            addView(list)
        }
        addView(scroll, LayoutParams(LayoutParams.MATCH_PARENT, 0, 1f))
    }

    private fun section(name: String) = context.label(14f, Look.MUTED, bold = true).apply {
        text = name
        setPadding(0, context.dp(24), 0, context.dp(8))
    }

    fun open() {
        // As they are now (without telling anyone: only a change from here does).
        val yours = Apps.yours(context).map { it.key }.toSet()
        for ((app, toggle) in switches) {
            val listener = onAppsChanged
            onAppsChanged = null
            toggle.isChecked = app.key in yours
            onAppsChanged = listener
        }
        visibility = VISIBLE
    }

    fun close() {
        visibility = GONE
        onClose?.invoke()
    }

    /** What checking for an update found. */
    fun showVersion(text: String) {
        version.text = text
    }
}
