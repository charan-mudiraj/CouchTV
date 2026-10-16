package com.couchtv.remote

import android.app.AlertDialog
import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.util.AttributeSet
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.WindowInsets
import android.view.animation.PathInterpolator
import android.widget.FrameLayout
import android.widget.ImageButton
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView

/**
 * Settings, as a screen of its own over the remote, like a phone's Settings app: cards of rows (a coloured icon, the
 * name, what it's set to, and a switch or an arrow), opened from the gear at the top and closed by its back arrow or
 * Back. The TV's come from the TV, as it lists them, so new ones show up without updating the app; the pointer's
 * three have a page of their own, with the pointer drawn as it will look. Changes go at once ([onTvChange],
 * [onPhoneChange]) and the TV says back what it now is ([show]).
 */
class SettingsScreen @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : FrameLayout(context, attrs) {
    var onTvChange: ((String, String) -> Unit)? = null
    var onPhoneChange: ((String, String) -> Unit)? = null
    var onVersion: (() -> Unit)? = null
    var onConnection: (() -> Unit)? = null

    private companion object {
        val POINTER_KEYS = listOf("pointerstyle", "pointersize", "pointercolor")
        val EASE = PathInterpolator(0.2f, 0f, 0f, 1f)
    }

    private val density = resources.displayMetrics.density
    private var tv: List<TvSetting> = emptyList()
    private var phone: List<TvSetting> = emptyList()
    private var version = ""
    private val mainList = LinearLayout(context).apply { orientation = LinearLayout.VERTICAL }
    private val pointerList = LinearLayout(context).apply { orientation = LinearLayout.VERTICAL }
    private val mainPage = page(R.string.settings_title, mainList) { close() }
    private val pointerPage = page(R.string.pointer_title, pointerList) { showPointer(false) }

    init {
        setBackgroundColor(context.getColor(R.color.background))
        isClickable = true   // nothing underneath gets the fingers
        addView(mainPage, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT))
        addView(pointerPage, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT))
        pointerPage.visibility = GONE
        visibility = GONE
        // Clear of the status bar and the gesture bar.
        setOnApplyWindowInsetsListener { v, insets ->
            if (Build.VERSION.SDK_INT >= 30) {
                val bars = insets.getInsets(WindowInsets.Type.systemBars())
                v.setPadding(0, bars.top, 0, bars.bottom)
            } else {
                @Suppress("DEPRECATION")
                v.setPadding(0, insets.systemWindowInsetTop, 0, insets.systemWindowInsetBottom)
            }
            insets
        }
    }

    val isOpen: Boolean get() = visibility == VISIBLE

    /** Slides in from the right. */
    fun open() {
        showPointer(false, animate = false)
        visibility = VISIBLE
        translationX = (if (width > 0) width else resources.displayMetrics.widthPixels).toFloat()
        animate().translationX(0f).setDuration(300).setInterpolator(EASE).withEndAction(null).start()
    }

    fun close() {
        if (!isOpen) return
        animate().translationX(width.toFloat()).setDuration(240).setInterpolator(EASE).withEndAction { visibility = GONE }.start()
    }

    /** Back: from the pointer's page to the list, from the list away. True if it was open. */
    fun back(): Boolean {
        if (!isOpen) return false
        if (pointerPage.visibility == VISIBLE) showPointer(false) else close()
        return true
    }

    /** What to show: the TV's (empty while it isn't connected), this phone's, and the app's version line. */
    fun show(tv: List<TvSetting>, phone: List<TvSetting>, version: String) {
        this.tv = tv
        this.phone = phone
        this.version = version
        fillMain()
        fillPointer()
    }

    fun showPointer(on: Boolean, animate: Boolean = true) {
        val (coming, going) = if (on) pointerPage to mainPage else mainPage to pointerPage
        if (coming.visibility == VISIBLE && going.visibility != VISIBLE) return
        val w = (if (width > 0) width else resources.displayMetrics.widthPixels).toFloat()
        coming.visibility = VISIBLE
        if (!animate) {
            coming.translationX = 0f
            going.visibility = GONE
            return
        }
        coming.translationX = if (on) w else -w * 0.3f
        coming.animate().translationX(0f).setDuration(280).setInterpolator(EASE).start()
        going.animate().translationX(if (on) -w * 0.3f else w).setDuration(280).setInterpolator(EASE).withEndAction {
            going.visibility = GONE
            going.translationX = 0f
        }.start()
    }

    // ---------------------------------------------------------------- the list

    private fun fillMain() {
        mainList.removeAllViews()
        val tvRows = tv.filter { it.key !in POINTER_KEYS }
        heading(mainList, R.string.settings_tv)
        mainList.addView(card {
            if (tv.isEmpty()) note(it, R.drawable.ic_tv, R.color.muted, context.getString(R.string.settings_tv_offline))
            tvRows.forEachIndexed { i, s ->
                if (i > 0) divider(it)
                row(it, s, iconFor(s.key), colourFor(s.key)) { value -> onTvChange?.invoke(s.key, value) }
            }
        })
        if (tv.any { it.key in POINTER_KEYS }) {
            heading(mainList, R.string.pointer_title)
            mainList.addView(card {
                link(it, R.drawable.ic_mouse, R.color.pointer_pink, context.getString(R.string.pointer_title), pointerSummary()) { showPointer(true) }
            })
        }
        heading(mainList, R.string.settings_phone)
        mainList.addView(card {
            phone.forEachIndexed { i, s ->
                if (i > 0) divider(it)
                row(it, s, iconFor(s.key), colourFor(s.key)) { value -> onPhoneChange?.invoke(s.key, value) }
            }
        })
        heading(mainList, R.string.settings_about)
        mainList.addView(card {
            link(it, R.drawable.ic_router, R.color.good, context.getString(R.string.settings_connection), null) { onConnection?.invoke() }
            divider(it)
            link(it, R.drawable.ic_update, R.color.voice, version, context.getString(R.string.settings_check_updates)) { onVersion?.invoke() }
        })
        mainList.addView(View(context), LinearLayout.LayoutParams(1, dp(32)))
    }

    private fun pointerSummary(): String = POINTER_KEYS.mapNotNull { key ->
        tv.find { it.key == key }?.let { s -> s.choices.find { it.first == s.value }?.second ?: s.value }
    }.joinToString(" · ")

    private fun iconFor(key: String) = when (key) {
        "powercountdown" -> R.drawable.ic_timer
        "films" -> R.drawable.ic_movie
        "songs" -> R.drawable.ic_music
        "typeonphone" -> R.drawable.ic_keyboard
        "updates" -> R.drawable.ic_update
        "vibration" -> R.drawable.ic_vibration
        "bluetooth" -> R.drawable.ic_bluetooth_off
        "mirror" -> R.drawable.ic_mirror
        else -> R.drawable.ic_settings
    }

    private fun colourFor(key: String) = when (key) {
        "powercountdown" -> R.color.power
        "films" -> R.color.netflix
        "songs" -> R.color.pointer_pink
        "typeonphone" -> R.color.voice
        "updates" -> R.color.good_bottom
        "vibration" -> R.color.prime
        "bluetooth" -> R.color.voice_bottom
        "mirror" -> R.color.hotstar
        else -> R.color.surface_pressed
    }

    // ---------------------------------------------------------------- the pointer's page

    private fun fillPointer() {
        pointerList.removeAllViews()
        val style = tv.find { it.key == "pointerstyle" }
        val size = tv.find { it.key == "pointersize" }
        val colour = tv.find { it.key == "pointercolor" }
        if (style == null || size == null || colour == null) {
            note(pointerList, R.drawable.ic_mouse, R.color.muted, context.getString(R.string.settings_tv_offline))
            return
        }
        // The pointer as it will look, on a picture like the home screen.
        val preview = PointerPreview(context).apply {
            this.style = style.value
            this.size = size.value.toIntOrNull() ?: 64
            this.color = pointerColour(colour.value)
            scale = 0.7f
            background = GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(0xFF1E3A72.toInt(), 0xFF0B0E14.toInt(), 0xFF472066.toInt())).apply {
                cornerRadius = dp(22).toFloat()
            }
        }
        pointerList.addView(preview, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(170)).apply { topMargin = dp(8) })
        pointerList.addView(TextView(context).apply {
            setText(R.string.pointer_help)
            setTextColor(context.getColor(R.color.muted))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
            setPadding(dp(4), dp(10), dp(4), 0)
        })

        heading(pointerList, R.string.pointer_style)
        pointerList.addView(tiles(style) { value, tile ->
            tile.addView(PointerPreview(context).apply {
                this.style = value
                this.size = 64
                this.color = pointerColour(colour.value)
                scale = 0.55f
            }, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(52)))
        })
        heading(pointerList, R.string.pointer_size)
        pointerList.addView(tiles(size) { value, tile ->
            tile.addView(PointerPreview(context).apply {
                this.style = style.value
                this.size = value.toIntOrNull() ?: 64
                this.color = pointerColour(colour.value)
                scale = 0.4f
            }, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(56)))
        })
        heading(pointerList, R.string.pointer_color)
        pointerList.addView(swatches(colour))
        pointerList.addView(View(context), LinearLayout.LayoutParams(1, dp(32)))
    }

    /** One tile a choice, with something drawn in it ([draw]) over its name; the chosen one outlined. */
    private fun tiles(s: TvSetting, draw: (String, LinearLayout) -> Unit): View {
        val row = LinearLayout(context).apply { orientation = LinearLayout.HORIZONTAL }
        s.choices.forEachIndexed { i, (value, name) ->
            val picked = value == s.value
            val tile = LinearLayout(context).apply {
                orientation = LinearLayout.VERTICAL
                gravity = Gravity.CENTER_HORIZONTAL
                setPadding(0, dp(10), 0, dp(10))
                background = GradientDrawable().apply {
                    cornerRadius = dp(16).toFloat()
                    setColor(context.getColor(R.color.surface))
                    if (picked) setStroke(dp(2), context.getColor(R.color.good))
                }
                isClickable = true
                setOnClickListener {
                    Haptics.press(it)
                    pick(s.key, value)
                }
            }
            draw(value, tile)
            tile.addView(TextView(context).apply {
                text = name
                gravity = Gravity.CENTER
                setTextColor(context.getColor(if (picked) R.color.good else R.color.ink))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
                typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
                setPadding(0, dp(6), 0, 0)
            })
            row.addView(tile, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply {
                marginStart = if (i == 0) 0 else dp(5)
                marginEnd = if (i == s.choices.size - 1) 0 else dp(5)
            })
        }
        return row
    }

    /** The colours as round swatches, the chosen one ringed and ticked. */
    private fun swatches(s: TvSetting): View {
        val row = LinearLayout(context).apply { orientation = LinearLayout.HORIZONTAL }
        s.choices.forEach { (value, name) ->
            val picked = value == s.value
            val swatch = FrameLayout(context).apply {
                contentDescription = name
                isClickable = true
                setOnClickListener {
                    Haptics.press(it)
                    pick(s.key, value)
                }
                addView(View(context).apply {
                    background = GradientDrawable().apply {
                        shape = GradientDrawable.OVAL
                        setColor(pointerColour(value))
                        setStroke(dp(if (picked) 3 else 1), context.getColor(if (picked) R.color.good else R.color.surface_pressed))
                    }
                }, LayoutParams(dp(44), dp(44), Gravity.CENTER))
                if (picked) addView(ImageView(context).apply {
                    setImageResource(R.drawable.ic_check)
                    imageTintList = ColorStateList.valueOf(context.getColor(R.color.background))
                }, LayoutParams(dp(22), dp(22), Gravity.CENTER))
            }
            row.addView(swatch, LinearLayout.LayoutParams(0, dp(56), 1f))
        }
        return row
    }

    /** A pointer choice: shown at once here (the TV says back what it now is). */
    private fun pick(key: String, value: String) {
        tv = tv.map { if (it.key == key) it.copy(value = value) else it }
        fillPointer()
        fillMain()
        onTvChange?.invoke(key, value)
    }

    private fun pointerColour(value: String) = context.getColor(when (value) {
        "yellow" -> R.color.pointer_yellow
        "blue" -> R.color.pointer_blue
        "green" -> R.color.pointer_green
        "pink" -> R.color.pointer_pink
        else -> R.color.pointer_white
    })

    // ---------------------------------------------------------------- the pieces

    /** A page: a bar with a back arrow and its name, over its list, scrolling. */
    private fun page(title: Int, list: LinearLayout, back: () -> Unit): View {
        val bar = LinearLayout(context).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(8), dp(10), dp(16), dp(6))
            addView(ImageButton(context).apply {
                setImageResource(R.drawable.ic_arrow_back)
                imageTintList = ColorStateList.valueOf(context.getColor(R.color.ink))
                background = context.getDrawable(R.drawable.status_bg)
                contentDescription = context.getString(R.string.back)
                setOnClickListener { back() }
                Haptics.attach(this)
            }, LinearLayout.LayoutParams(dp(48), dp(48)))
            addView(TextView(context).apply {
                setText(title)
                setTextColor(context.getColor(R.color.ink))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 26f)
                typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
                setPadding(dp(14), 0, 0, 0)
            })
        }
        val scroll = ScrollView(context).apply {
            isVerticalScrollBarEnabled = false
            overScrollMode = OVER_SCROLL_IF_CONTENT_SCROLLS
            list.setPadding(dp(16), 0, dp(16), 0)
            addView(list, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT))
        }
        return LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(context.getColor(R.color.background))
            addView(bar)
            addView(scroll, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f))
        }
    }

    private fun heading(list: LinearLayout, text: Int) {
        list.addView(TextView(context).apply {
            setText(text)
            setTextColor(context.getColor(R.color.muted))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            isAllCaps = true
            letterSpacing = 0.06f
            setPadding(dp(6), dp(22), 0, dp(8))
        })
    }

    private fun card(fill: (LinearLayout) -> Unit) = LinearLayout(context).apply {
        orientation = LinearLayout.VERTICAL
        background = GradientDrawable().apply {
            cornerRadius = dp(20).toFloat()
            setColor(context.getColor(R.color.surface))
        }
        clipToOutline = true
        fill(this)
    }

    private fun divider(card: LinearLayout) {
        card.addView(View(context).apply { setBackgroundColor(context.getColor(R.color.background)) },
            LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(1)).apply { marginStart = dp(64) })
    }

    /** The coloured square an icon sits on, as in a phone's Settings. */
    private fun icon(drawable: Int, colour: Int) = ImageView(context).apply {
        setImageResource(drawable)
        imageTintList = ColorStateList.valueOf(context.getColor(R.color.ink))
        scaleType = ImageView.ScaleType.CENTER_INSIDE
        setPadding(dp(6), dp(6), dp(6), dp(6))
        background = GradientDrawable().apply {
            cornerRadius = dp(9).toFloat()
            setColor(context.getColor(colour))
        }
    }

    private fun words(title: String, summary: String?, summaryColour: Int = R.color.muted) = LinearLayout(context).apply {
        orientation = LinearLayout.VERTICAL
        addView(TextView(context).apply {
            text = title
            setTextColor(context.getColor(R.color.ink))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 16f)
        })
        if (summary != null) addView(TextView(context).apply {
            text = summary
            setTextColor(context.getColor(summaryColour))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
            setPadding(0, dp(2), 0, 0)
        })
    }

    private fun line(icon: Int, colour: Int, words: View, end: View?): LinearLayout = LinearLayout(context).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
        minimumHeight = dp(64)
        setPadding(dp(16), dp(10), dp(12), dp(10))
        background = context.getDrawable(R.drawable.row_bg)
        addView(icon(icon, colour), LinearLayout.LayoutParams(dp(34), dp(34)))
        addView(words, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply { marginStart = dp(16) })
        if (end != null) addView(end)
    }

    /** A setting: a switch for on and off; otherwise what it's set to, and a tap to choose. */
    private fun row(card: LinearLayout, s: TvSetting, icon: Int, colour: Int, change: (String) -> Unit) {
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
            val row = line(icon, colour, words(s.label, s.note), switch)
            row.setOnClickListener { switch.toggle() }
            card.addView(row)
            return
        }
        val chosen = s.choices.find { it.first == s.value }?.second ?: s.value
        val value = TextView(context).apply {
            text = chosen
            setTextColor(context.getColor(R.color.muted))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
        }
        val end = LinearLayout(context).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            addView(value)
            addView(ImageView(context).apply {
                setImageResource(R.drawable.ic_chevron_right)
                imageTintList = ColorStateList.valueOf(context.getColor(R.color.muted))
            }, LinearLayout.LayoutParams(dp(24), dp(24)).apply { marginStart = dp(4) })
        }
        val row = line(icon, colour, words(s.label, s.note), end)
        row.setOnClickListener {
            Haptics.press(it)
            val names = s.choices.map { c -> c.second }.toTypedArray()
            AlertDialog.Builder(context, android.R.style.Theme_Material_Dialog_Alert)
                .setTitle(s.label)
                .setSingleChoiceItems(names, s.choices.indexOfFirst { c -> c.first == s.value }) { dialog, which ->
                    value.text = names[which]   // at once; the TV says back what it now is
                    change(s.choices[which].first)
                    dialog.dismiss()
                }
                .setNegativeButton(R.string.cancel, null)
                .show()
        }
        card.addView(row)
    }

    /** A row that opens something: the pointer's page, the connection's details, a check for updates. */
    private fun link(card: LinearLayout, icon: Int, colour: Int, title: String, summary: String?, open: () -> Unit) {
        val row = line(icon, colour, words(title, summary), ImageView(context).apply {
            setImageResource(R.drawable.ic_chevron_right)
            imageTintList = ColorStateList.valueOf(context.getColor(R.color.muted))
        })
        row.setOnClickListener {
            Haptics.press(it)
            open()
        }
        card.addView(row)
    }

    private fun note(card: LinearLayout, icon: Int, colour: Int, text: String) {
        card.addView(line(icon, colour, words(text, null), null))
    }

    private fun dp(value: Int) = (value * density + 0.5f).toInt()
}
