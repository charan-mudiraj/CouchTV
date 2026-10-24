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
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView

/**
 * Settings, as a screen of its own over the remote, like a phone's Settings app: cards of rows (a coloured icon, the
 * name, what it's set to, and a switch or an arrow), opened from the gear at the top and closed by its back arrow or
 * Back. The TV's come from the TV, as it lists them, so new ones show up without updating the app; the pointer's
 * three have a page of their own, with the pointer drawn as it will look. Changes go at once ([onTvChange],
 * [onPhoneChange]) and the TV says back what it now is ([show]).
 *
 * The TV's Bluetooth has pages of its own too, as on the TV: switching it on or off, the paired devices (each with a
 * page: connect or disconnect, connect automatically, play with the TV speakers, forget), and adding one (the TV
 * searches while that page is open; tap a device to pair). Pages open one over another and Back goes back one.
 */
class SettingsScreen @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : FrameLayout(context, attrs) {
    var onTvChange: ((String, String) -> Unit)? = null
    var onPhoneChange: ((String, String) -> Unit)? = null
    var onVersion: (() -> Unit)? = null
    var onConnection: (() -> Unit)? = null

    /** Something for the TV's Bluetooth: "POWER ON", "SEARCH ON", "PAIR <address>"... */
    var onBluetooth: ((String) -> Unit)? = null

    /** The Bluetooth pages opened (true) or left: the TV says how its Bluetooth is meanwhile. */
    var onBluetoothPage: ((Boolean) -> Unit)? = null

    /** "Choose a photo" on the Backgrounds page: for the TV's home screen (true), or this remote. */
    var onPickPhoto: ((Boolean) -> Unit)? = null

    private companion object {
        val POINTER_KEYS = listOf("pointerstyle", "pointersize", "pointercolor")
        const val BACKGROUND = "background"   // on the Backgrounds page, not a row of its own
        val EASE = PathInterpolator(0.2f, 0f, 0f, 1f)
    }

    private val density = resources.displayMetrics.density
    private var tv: List<TvSetting> = emptyList()
    private var phone: List<TvSetting> = emptyList()
    private var version = ""
    private var bt: TvBluetooth? = null                  // the TV's Bluetooth, as it last said
    private var btSays: Pair<String, String>? = null     // its last line for these pages: kind and words
    private var btAddress: String? = null                // the device whose page is open
    private var btShownOn = false                        // the Bluetooth pages are open (as last told)
    private val mainList = column()
    private val pointerList = column()
    private val btList = column()
    private val btDeviceList = column()
    private val btAddList = column()
    private val bgList = column()
    private var btDeviceTitle: TextView? = null
    private val mainPage = page(context.getString(R.string.settings_title), mainList) { close() }
    private val pointerPage = page(context.getString(R.string.pointer_title), pointerList) { back() }
    private val btPage = page(context.getString(R.string.bt_title), btList) { back() }
    private val btDevicePage = page("", btDeviceList, { btDeviceTitle = it }) { back() }
    private val btAddPage = page(context.getString(R.string.bt_add), btAddList) { back() }
    private val bgPage = page(context.getString(R.string.background_title), bgList) { back() }
    private val stack = arrayListOf(mainPage)            // the pages open, one over another; the list first

    init {
        setBackgroundColor(context.getColor(R.color.background))
        isClickable = true   // nothing underneath gets the fingers
        // Each page over the ones it's opened from (the order they slide in).
        for (page in listOf(mainPage, pointerPage, btPage, btDevicePage, btAddPage, bgPage)) {
            addView(page, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT))
            if (page !== mainPage) page.visibility = GONE
        }
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

    /** Slides in from the right, on the list. */
    fun open() {
        while (stack.size > 1) {
            val page = stack.removeAt(stack.size - 1)
            leave(page)
            page.animate().cancel()
            page.visibility = GONE
            page.translationX = 0f
        }
        mainPage.visibility = VISIBLE
        mainPage.translationX = 0f
        visibility = VISIBLE
        translationX = (if (width > 0) width else resources.displayMetrics.widthPixels).toFloat()
        animate().translationX(0f).setDuration(300).setInterpolator(EASE).withEndAction(null).start()
        tellBluetoothPage()
    }

    fun close() {
        if (!isOpen) return
        animate().translationX(width.toFloat()).setDuration(240).setInterpolator(EASE).withEndAction {
            visibility = GONE
            tellBluetoothPage()
        }.start()
        if (stack.contains(btAddPage)) onBluetooth?.invoke("SEARCH OFF")
    }

    /** Back: one page back, and from the list away. True if it was open. */
    fun back(): Boolean {
        if (!isOpen) return false
        if (stack.size > 1) pop() else close()
        return true
    }

    /** Opens on the TV's Bluetooth page (its shortcut on the first page). */
    fun openBluetooth() {
        if (!isOpen) open()
        if (stack.last() !== btPage) push(btPage, animate = false)
    }

    /** A page slides in over the one showing. */
    private fun push(page: View, animate: Boolean = true) {
        val going = stack.last()
        stack.add(page)
        slide(page, going, forward = true, animate = animate)
        if (page === btAddPage) onBluetooth?.invoke("SEARCH ON")
        tellBluetoothPage()
    }

    /** The page showing slides away, back to the one under it. */
    private fun pop(animate: Boolean = true) {
        if (stack.size <= 1) return
        val going = stack.removeAt(stack.size - 1)
        leave(going)
        slide(stack.last(), going, forward = false, animate = animate)
        tellBluetoothPage()
    }

    /** A page no longer open: the Add page stops the TV searching. */
    private fun leave(page: View) {
        if (page === btAddPage) onBluetooth?.invoke("SEARCH OFF")
    }

    private fun slide(coming: View, going: View, forward: Boolean, animate: Boolean) {
        val w = (if (width > 0) width else resources.displayMetrics.widthPixels).toFloat()
        coming.animate().cancel()
        going.animate().cancel()
        coming.visibility = VISIBLE
        if (!animate) {
            coming.translationX = 0f
            going.visibility = GONE
            going.translationX = 0f
            return
        }
        coming.translationX = if (forward) w else -w * 0.3f
        coming.animate().translationX(0f).setDuration(280).setInterpolator(EASE).withEndAction(null).start()
        going.animate().translationX(if (forward) -w * 0.3f else w).setDuration(280).setInterpolator(EASE).withEndAction {
            going.visibility = GONE
            going.translationX = 0f
        }.start()
    }

    /** Tells whether the Bluetooth pages are open, when that changes. */
    private fun tellBluetoothPage() {
        val on = isOpen && stack.contains(btPage)
        if (on == btShownOn) return
        btShownOn = on
        if (!on) btSays = null
        onBluetoothPage?.invoke(on)
    }

    /** What to show: the TV's (empty while it isn't connected), this phone's, and the app's version line. */
    fun show(tv: List<TvSetting>, phone: List<TvSetting>, version: String) {
        this.tv = tv
        this.phone = phone
        this.version = version
        fillMain()
        fillPointer()
        fillBackgrounds()
    }

    /** A photo was just chosen (for the TV, or this remote): the page shows it. */
    fun photoChanged() = fillBackgrounds()

    /** The pointer's page (true), or back to the list. */
    fun showPointer(on: Boolean, animate: Boolean = true) {
        if (on) {
            if (stack.last() !== pointerPage) push(pointerPage, animate)
        } else while (stack.size > 1) pop(animate)
    }

    /** How the TV's Bluetooth is now (null: not known, or the TV isn't connected). */
    fun showBluetooth(state: TvBluetooth?) {
        bt = state
        fillBluetooth()
        fillMain()
    }

    /** A line from the TV for the Bluetooth pages (what's happening, or what went wrong); null clears it. */
    fun bluetoothSays(said: Pair<String, String>?) {
        btSays = said
        fillBluetooth()
    }

    // ---------------------------------------------------------------- the list

    private fun fillMain() {
        mainList.removeAllViews()
        val tvRows = tv.filter { it.key !in POINTER_KEYS && it.key != BACKGROUND }
        heading(mainList, R.string.settings_tv)
        mainList.addView(card {
            if (tv.isEmpty()) note(it, R.drawable.ic_tv, R.color.muted, context.getString(R.string.settings_tv_offline))
            tvRows.forEachIndexed { i, s ->
                if (i > 0) divider(it)
                row(it, s, iconFor(s.key), colourFor(s.key)) { value -> onTvChange?.invoke(s.key, value) }
            }
            if (tv.isNotEmpty()) {
                divider(it)
                link(it, R.drawable.ic_bluetooth, R.color.voice_bottom, context.getString(R.string.bt_title), bluetoothSummary()) { push(btPage) }
            }
            tv.find { s -> s.key == BACKGROUND }?.let { s ->
                divider(it)
                link(it, R.drawable.ic_image, R.color.pointer_pink, s.label, lookName(s.value)) { push(bgPage) }
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
            phone.filter { s -> s.key != BACKGROUND }.forEachIndexed { i, s ->
                if (i > 0) divider(it)
                row(it, s, iconFor(s.key), colourFor(s.key)) { value -> onPhoneChange?.invoke(s.key, value) }
            }
            phone.find { s -> s.key == BACKGROUND }?.let { s ->
                divider(it)
                link(it, R.drawable.ic_image, R.color.pointer_pink, s.label, lookName(s.value)) { push(bgPage) }
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
        "clock" -> R.drawable.ic_schedule
        "films" -> R.drawable.ic_movie
        "songs" -> R.drawable.ic_music
        "typeonphone" -> R.drawable.ic_keyboard
        "sitearrows" -> R.drawable.ic_gamepad
        "youtuberow" -> R.drawable.ic_apps
        "netflixtidy" -> R.drawable.ic_movie
        "updates" -> R.drawable.ic_update
        "vibration" -> R.drawable.ic_vibration
        "bluetooth" -> R.drawable.ic_bluetooth_off
        "mirror" -> R.drawable.ic_mirror
        "livetv" -> R.drawable.ic_tv
        "hotstarads", "primeads" -> R.drawable.ic_movie
        else -> R.drawable.ic_settings
    }

    private fun colourFor(key: String) = when (key) {
        "powercountdown" -> R.color.power
        "clock" -> R.color.prime
        "films" -> R.color.netflix
        "songs" -> R.color.pointer_pink
        "typeonphone" -> R.color.voice
        "sitearrows" -> R.color.prime
        "youtuberow" -> R.color.youtube
        "netflixtidy" -> R.color.netflix
        "updates" -> R.color.good_bottom
        "vibration" -> R.color.prime
        "bluetooth" -> R.color.voice_bottom
        "mirror" -> R.color.hotstar
        "livetv" -> R.color.power
        "hotstarads" -> R.color.hotstar
        "primeads" -> R.color.prime
        else -> R.color.surface_pressed
    }

    // ---------------------------------------------------------------- the TV's Bluetooth

    private fun bluetoothSummary(): String {
        val b = bt ?: return context.getString(R.string.bt_summary)
        return when {
            b.power == "none" -> context.getString(R.string.bt_none_short)
            !b.isOn -> context.getString(R.string.bt_off)
            else -> b.devices.filter { it.connected }.joinToString(", ") { it.name }.ifEmpty { context.getString(R.string.bt_on_nothing) }
        }
    }

    private fun kindIcon(kind: String) = when (kind) {
        "headphones" -> R.drawable.ic_headphones
        "speaker", "audio" -> R.drawable.ic_speaker
        "phone" -> R.drawable.ic_smartphone
        "computer" -> R.drawable.ic_computer
        "keyboard" -> R.drawable.ic_keyboard
        "mouse" -> R.drawable.ic_mouse
        "gamepad" -> R.drawable.ic_gamepad
        else -> R.drawable.ic_bluetooth
    }

    private fun fillBluetooth() {
        fillBluetoothMain()
        fillBluetoothDevice()
        fillBluetoothAdd()
    }

    /** The main Bluetooth page: the switch, the paired devices, adding one. */
    private fun fillBluetoothMain() {
        btList.removeAllViews()
        says(btList)
        val b = bt
        btList.addView(card {
            when {
                b == null -> note(it, R.drawable.ic_bluetooth, R.color.muted, context.getString(if (tv.isEmpty()) R.string.bt_offline else R.string.bt_asking))
                b.power == "none" -> note(it, R.drawable.ic_bluetooth, R.color.muted, context.getString(R.string.bt_none))
                else -> {
                    val power = TvSetting("btpower", if (b.isOn) "on" else "off", context.getString(R.string.bt_title),
                        if (b.isOn) b.radio else context.getString(R.string.bt_off_note), listOf("on" to "", "off" to ""))
                    row(it, power, R.drawable.ic_bluetooth, R.color.voice_bottom) { value -> onBluetooth?.invoke("POWER " + if (value == "on") "ON" else "OFF") }
                }
            }
        })
        if (b == null || !b.isOn) return
        heading(btList, R.string.bt_paired)
        btList.addView(card {
            if (b.devices.isEmpty()) note(it, R.drawable.ic_bluetooth, R.color.muted, context.getString(R.string.bt_none_paired))
            b.devices.forEachIndexed { i, d ->
                if (i > 0) divider(it)
                val row = line(kindIcon(d.kind), if (d.connected) R.color.good_bottom else R.color.surface_pressed,
                    words(d.name, d.note.ifEmpty { null }, if (d.connected) R.color.good else R.color.muted), chevron())
                row.setOnClickListener { v ->
                    Haptics.press(v)
                    btAddress = d.address
                    fillBluetoothDevice()
                    push(btDevicePage)
                }
                it.addView(row)
            }
        })
        btList.addView(View(context), LinearLayout.LayoutParams(1, dp(12)))
        btList.addView(card {
            link(it, R.drawable.ic_add, R.color.good_bottom, context.getString(R.string.bt_add), context.getString(R.string.bt_add_note)) { push(btAddPage) }
        })
        btList.addView(View(context), LinearLayout.LayoutParams(1, dp(32)))
    }

    /** One paired device's page. */
    private fun fillBluetoothDevice() {
        btDeviceList.removeAllViews()
        val d = bt?.devices?.find { it.address == btAddress }
        btDeviceTitle?.text = d?.name ?: ""
        says(btDeviceList)
        if (d == null) {
            btDeviceList.addView(card { note(it, R.drawable.ic_bluetooth, R.color.muted, context.getString(R.string.bt_gone)) })
            return
        }
        btDeviceList.addView(card {
            it.addView(line(kindIcon(d.kind), if (d.connected) R.color.good_bottom else R.color.surface_pressed,
                words(d.name, d.note.ifEmpty { null }, if (d.connected) R.color.good else R.color.muted), null))
        })
        btDeviceList.addView(View(context), LinearLayout.LayoutParams(1, dp(12)))
        btDeviceList.addView(card {
            if (d.audio) {
                val connect = line(if (d.connected) R.drawable.ic_link_off else R.drawable.ic_link, if (d.connected) R.color.surface_pressed else R.color.good_bottom,
                    words(context.getString(if (d.connected) R.string.bt_disconnect else R.string.bt_connect), null), null)
                connect.setOnClickListener { v ->
                    Haptics.press(v)
                    onBluetooth?.invoke((if (d.connected) "DISCONNECT " else "CONNECT ") + d.address)
                }
                it.addView(connect)
                divider(it)
                val auto = TvSetting("btauto", if (d.auto) "on" else "off", context.getString(R.string.bt_auto),
                    context.getString(R.string.bt_auto_note), listOf("on" to "", "off" to ""))
                row(it, auto, R.drawable.ic_bluetooth_searching, R.color.voice) { value -> onBluetooth?.invoke("AUTO ${d.address} " + if (value == "on") "ON" else "OFF") }
                divider(it)
                val together = TvSetting("bttogether", if (d.together) "on" else "off", context.getString(R.string.bt_together),
                    context.getString(R.string.bt_together_note), listOf("on" to "", "off" to ""))
                row(it, together, R.drawable.ic_tv, R.color.hotstar) { value -> onBluetooth?.invoke("TOGETHER ${d.address} " + if (value == "on") "ON" else "OFF") }
            } else note(it, kindIcon(d.kind), R.color.surface_pressed, context.getString(R.string.bt_connects_itself))
        })
        btDeviceList.addView(View(context), LinearLayout.LayoutParams(1, dp(12)))
        btDeviceList.addView(card {
            val forget = line(R.drawable.ic_delete, R.color.power, words(context.getString(R.string.bt_forget), null, R.color.muted), null)
            forget.setOnClickListener { v ->
                Haptics.press(v)
                AlertDialog.Builder(context, android.R.style.Theme_Material_Dialog_Alert)
                    .setTitle(context.getString(R.string.bt_forget_title, d.name))
                    .setMessage(R.string.bt_forget_message)
                    .setPositiveButton(R.string.bt_forget) { _, _ ->
                        onBluetooth?.invoke("FORGET " + d.address)
                        back()
                    }
                    .setNegativeButton(R.string.cancel, null)
                    .show()
            }
            it.addView(forget)
        })
        btDeviceList.addView(View(context), LinearLayout.LayoutParams(1, dp(32)))
    }

    /** Adding a device: the TV searches while this is open; tap one to pair. */
    private fun fillBluetoothAdd() {
        btAddList.removeAllViews()
        says(btAddList)
        btAddList.addView(TextView(context).apply {
            setText(R.string.bt_pairing_mode)
            setTextColor(context.getColor(R.color.muted))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
            setPadding(dp(6), dp(8), dp(6), dp(4))
        })
        val b = bt
        if (b == null || !b.isOn) {
            btAddList.addView(card { note(it, R.drawable.ic_bluetooth, R.color.muted, context.getString(R.string.bt_off_note)) })
            return
        }
        val searching = LinearLayout(context).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(6), dp(14), dp(6), dp(8))
            addView(ProgressBar(context).apply {
                isIndeterminate = true
                indeterminateTintList = ColorStateList.valueOf(context.getColor(R.color.good))
                visibility = if (b.searching) VISIBLE else INVISIBLE
            }, LinearLayout.LayoutParams(dp(20), dp(20)))
            addView(TextView(context).apply {
                setText(if (b.searching) R.string.bt_searching else R.string.bt_found_heading)
                setTextColor(context.getColor(R.color.muted))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
                typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
                isAllCaps = true
                letterSpacing = 0.06f
                setPadding(dp(10), 0, 0, 0)
            })
        }
        btAddList.addView(searching)
        btAddList.addView(card {
            if (b.found.isEmpty()) note(it, R.drawable.ic_bluetooth_searching, R.color.muted, context.getString(R.string.bt_nothing_found))
            b.found.forEachIndexed { i, f ->
                if (i > 0) divider(it)
                val row = line(kindIcon(f.kind), R.color.voice_bottom, words(f.name, context.getString(R.string.bt_tap_to_pair)), chevron())
                row.setOnClickListener { v ->
                    Haptics.press(v)
                    onBluetooth?.invoke("PAIR " + f.address)
                }
                it.addView(row)
            }
        })
        btAddList.addView(View(context), LinearLayout.LayoutParams(1, dp(32)))
    }

    /** The TV's last line for these pages, as a card at the top: green, yellow or grey. */
    private fun says(list: LinearLayout) {
        val (kind, message) = btSays ?: return
        if (message.isEmpty()) return
        list.addView(TextView(context).apply {
            text = message
            setTextColor(context.getColor(when (kind) {
                "good" -> R.color.good
                "warn" -> R.color.warn
                else -> R.color.ink
            }))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
            setPadding(dp(16), dp(12), dp(16), dp(12))
            background = GradientDrawable().apply {
                cornerRadius = dp(16).toFloat()
                setColor(context.getColor(R.color.surface_raised))
            }
        }, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply {
            topMargin = dp(8)
            bottomMargin = dp(4)
        })
    }

    private fun chevron() = ImageView(context).apply {
        setImageResource(R.drawable.ic_chevron_right)
        imageTintList = ColorStateList.valueOf(context.getColor(R.color.muted))
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

    // ---------------------------------------------------------------- backgrounds

    private fun lookName(key: String): String =
        if (key == Backdrop.PHOTO) context.getString(R.string.look_photo)
        else Backdrop.LOOKS.find { it.key == key }?.let { context.getString(it.name) } ?: key

    /** The looks for this remote, then for the TV's home screen, each with "Choose a photo"; the chosen one ringed. */
    private fun fillBackgrounds() {
        bgList.removeAllViews()
        val mine = phone.find { it.key == BACKGROUND }
        if (mine != null) {
            heading(bgList, R.string.background_remote)
            bgList.addView(looks(mine.value, Backdrop.photoFile(context), false) { value ->
                phone = phone.map { if (it.key == BACKGROUND) it.copy(value = value) else it }
                fillBackgrounds()
                fillMain()
                onPhoneChange?.invoke(BACKGROUND, value)
            })
        }
        heading(bgList, R.string.background_tv)
        val theirs = tv.find { it.key == BACKGROUND }
        if (theirs == null) {
            note(bgList, R.drawable.ic_tv, R.color.muted, context.getString(R.string.background_tv_offline))
        } else {
            bgList.addView(looks(theirs.value, Backdrop.tvPhotoFile(context), true, theirs.choices.any { it.first == Backdrop.PHOTO }) { value ->
                tv = tv.map { if (it.key == BACKGROUND) it.copy(value = value) else it }
                fillBackgrounds()
                fillMain()
                onTvChange?.invoke(BACKGROUND, value)
            })
            bgList.addView(TextView(context).apply {
                setText(R.string.background_tv_note)
                setTextColor(context.getColor(R.color.muted))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
                setPadding(dp(4), dp(10), dp(4), 0)
            })
        }
        bgList.addView(View(context), LinearLayout.LayoutParams(1, dp(32)))
    }

    /** The looks as tiles, three to a row: each drawn as it looks; then the photo (if there is one) and "Choose a photo". */
    private fun looks(chosen: String, photo: java.io.File, forTv: Boolean, hasPhoto: Boolean = photo.exists(), choose: (String) -> Unit): View {
        val column = column()
        val tiles = ArrayList<View>()
        for (look in Backdrop.LOOKS) tiles += lookTile(context.getString(look.name), Backdrop.drawable(context, look.key), look.key == chosen) {
            choose(look.key)
        }
        if (hasPhoto) {
            val picture = if (photo.exists()) Backdrop.drawable(context, Backdrop.PHOTO, photo) else null
            tiles += lookTile(context.getString(R.string.look_photo), picture, chosen == Backdrop.PHOTO) { choose(Backdrop.PHOTO) }
        }
        tiles += lookTile(context.getString(R.string.background_choose_photo), null, false, R.drawable.ic_add) { onPickPhoto?.invoke(forTv) }
        for (start in tiles.indices step 3) {
            val row = LinearLayout(context).apply { orientation = LinearLayout.HORIZONTAL }
            for (i in start until start + 3) {
                row.addView(if (i < tiles.size) tiles[i] else View(context), LinearLayout.LayoutParams(0, dp(108), 1f).apply {
                    marginStart = if (i == start) 0 else dp(5)
                    marginEnd = if (i == start + 2) 0 else dp(5)
                })
            }
            column.addView(row, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply {
                bottomMargin = dp(10)
            })
        }
        return column
    }

    /** One look: drawn as it looks (or an icon on plain), its name at the bottom; ringed and ticked when chosen. */
    private fun lookTile(name: String, look: android.graphics.drawable.Drawable?, picked: Boolean, icon: Int = 0, tap: () -> Unit): View {
        val radius = dp(16).toFloat()
        return FrameLayout(context).apply {
            background = look ?: GradientDrawable().apply {
                cornerRadius = radius
                setColor(context.getColor(R.color.surface))
                setStroke(dp(1), context.getColor(R.color.surface_pressed))
            }
            outlineProvider = object : android.view.ViewOutlineProvider() {
                override fun getOutline(view: View, outline: android.graphics.Outline) = outline.setRoundRect(0, 0, view.width, view.height, radius)
            }
            clipToOutline = true
            foreground = GradientDrawable().apply {
                cornerRadius = radius
                setColor(0)
                if (picked) setStroke(dp(3), context.getColor(R.color.good))
            }
            contentDescription = name
            isClickable = true
            setOnClickListener {
                Haptics.press(it)
                tap()
            }
            if (icon != 0) addView(ImageView(context).apply {
                setImageResource(icon)
                imageTintList = ColorStateList.valueOf(context.getColor(R.color.ink))
            }, LayoutParams(dp(28), dp(28), Gravity.CENTER or Gravity.TOP).apply { topMargin = dp(26) })
            if (picked) addView(ImageView(context).apply {
                setImageResource(R.drawable.ic_check)
                imageTintList = ColorStateList.valueOf(context.getColor(R.color.background))
                background = GradientDrawable().apply {
                    shape = GradientDrawable.OVAL
                    setColor(context.getColor(R.color.good))
                }
                setPadding(dp(3), dp(3), dp(3), dp(3))
            }, LayoutParams(dp(22), dp(22), Gravity.TOP or Gravity.END).apply { setMargins(0, dp(8), dp(8), 0) })
            addView(TextView(context).apply {
                text = name
                gravity = Gravity.CENTER
                setTextColor(context.getColor(R.color.ink))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
                typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
                setShadowLayer(dp(4).toFloat(), 0f, dp(1).toFloat(), 0xCC000000.toInt())
                maxLines = 2
            }, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT, Gravity.BOTTOM).apply { setMargins(dp(6), 0, dp(6), dp(10)) })
        }
    }

    // ---------------------------------------------------------------- the pieces

    private fun column() = LinearLayout(context).apply { orientation = LinearLayout.VERTICAL }

    /** A page: a bar with a back arrow and its name ([named] gets it, to change later), over its list, scrolling. */
    private fun page(title: String, list: LinearLayout, named: ((TextView) -> Unit)? = null, back: () -> Unit): View {
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
                text = title
                setTextColor(context.getColor(R.color.ink))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 26f)
                typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
                maxLines = 1
                ellipsize = android.text.TextUtils.TruncateAt.END
                setPadding(dp(14), 0, 0, 0)
                named?.invoke(this)
            }, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
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
        val row = line(icon, colour, words(title, summary), chevron())
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
