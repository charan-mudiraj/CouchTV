package com.couchtv.watch

import android.content.Context
import android.content.res.ColorStateList
import android.view.Gravity
import android.view.View
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView

/**
 * Settings, over the home: which apps you have (their tabs and rows), whether titles play inside this app, and the
 * version, to check for an update.
 */
class SettingsPanel(context: Context) : LinearLayout(context) {
    var onAppsChanged: (() -> Unit)? = null
    var onCheckUpdate: (() -> Unit)? = null
    var onClose: (() -> Unit)? = null

    private val switches = ArrayList<Pair<StreamApp, Switch>>()
    private val inApp: Switch
    private val version: TextView
    private var quiet = false   // the switches being set to how things are: not a change

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
        list.addView(note("Each has a tab, and its films and series in the rows."))
        for (app in Apps.ALL) {
            val toggle = toggle(app.label) { on ->
                Apps.setYours(context, app.key, on)
                onAppsChanged?.invoke()
            }
            list.addView(row(app.label, null, toggle), LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT))
            switches.add(app to toggle)
        }

        list.addView(section("Playing"))
        inApp = toggle("Play here") { on -> PlayerActivity.setInApp(context, on) }
        list.addView(row("Play here", "YouTube, JioHotstar, ZEE5, Sun NXT and ETV Win play inside this app (log in once in each). " +
            "Netflix and Prime Video play only in their own apps on a phone. Off: everything opens in its own app.", inApp),
            LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT))

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

    private fun note(text: String) = context.label(13f, Look.FAINT).apply {
        this.text = text
        setPadding(0, 0, 0, context.dp(8))
    }

    /** A switch in CouchTV's green; [changed] for changes made with it (not when it's set to how things are). */
    private fun toggle(description: String, changed: (Boolean) -> Unit) = Switch(context).apply {
        thumbTintList = ColorStateList(arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()), intArrayOf(Look.GOOD, Look.MUTED))
        trackTintList = ColorStateList(arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()), intArrayOf(0x663DDC84, Look.RAISED))
        contentDescription = description
        setOnCheckedChangeListener { _, on -> if (!quiet) changed(on) }
    }

    /** A row: its name (and a line about it), and its switch at the end; a tap anywhere on it flips the switch. */
    private fun row(name: String, about: String?, toggle: Switch): View {
        val row = LinearLayout(context).apply {
            orientation = HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            minimumHeight = context.dp(52)
            setPadding(0, context.dp(6), 0, context.dp(6))
            setOnClickListener { toggle.toggle() }
        }
        val words = LinearLayout(context).apply { orientation = VERTICAL }
        words.addView(context.label(16f, Look.INK).apply { text = name })
        if (about != null) words.addView(context.label(13f, Look.FAINT).apply { text = about })
        row.addView(words, LayoutParams(0, LayoutParams.WRAP_CONTENT, 1f).apply { marginEnd = context.dp(12) })
        row.addView(toggle)
        return row
    }

    fun open() {
        quiet = true
        val yours = Apps.yours(context).map { it.key }.toSet()
        for ((app, toggle) in switches) toggle.isChecked = app.key in yours
        inApp.isChecked = PlayerActivity.inApp(context)
        quiet = false
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
