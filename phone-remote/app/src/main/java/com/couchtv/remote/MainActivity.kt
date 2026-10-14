package com.couchtv.remote

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.content.pm.ActivityInfo
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.SystemClock
import android.provider.Settings
import android.text.InputFilter
import android.text.InputType
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.widget.Button
import android.widget.EditText
import android.widget.ImageButton
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import java.util.concurrent.Executors

/**
 * The remote, fitting the screen without scrolling: the status, the TV's sound here and power stay at the top, and
 * pages swipe sideways underneath (remote, apps, pointer, your own buttons), with dots to show which. It opens on the
 * remote; to its left, what the TV suggests (popular films and songs), to play there with a tap.
 */
class MainActivity : Activity() {
    companion object {
        private const val REMOTE_PAGE = 1                // the page it opens on; the suggestions are page 0
        private const val REQUEST_BLUETOOTH = 44       // the "Nearby devices" permission, to switch Bluetooth off
        private const val REQUEST_BLUETOOTH_OFF = 45   // Android's "turn off Bluetooth?" question
        private const val ACTION_REQUEST_DISABLE = "android.bluetooth.adapter.action.REQUEST_DISABLE"
    }

    private lateinit var remote: Remote
    private lateinit var voice: VoiceSearch
    private lateinit var store: CustomButtons
    private lateinit var myButtons: MutableList<CustomButton>
    private var editing = false
    private lateinit var keyboard: KeyboardView
    private lateinit var typingBox: TypingBox
    private lateinit var mirrorView: MirrorView
    private lateinit var screen: ScreenLink
    private var mirroring = false
    private var resumed = false
    private var pendingTyping: WifiLink.TypingField? = null   // a text box got the focus while this was in the background

    // Updates from GitHub
    private lateinit var updater: AppUpdater
    private val background = Executors.newSingleThreadExecutor()
    private var lastUpdateCheck = 0L
    private var availableUpdate: AppUpdater.Release? = null
    private var updating = false
    private var installStarted = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        remote = Remote(this)
        remote.wifi.onChange = {
            showStatus()
            findViewById<SuggestionsView>(R.id.suggestions).show(remote.wifi.suggestions, remote.wifi.tvName != null)
        }
        remote.board.onChange = { showStatus() }
        remote.onPowerNeedsIr = { toast(getString(R.string.power_needs_ir)) }
        store = CustomButtons(this)
        myButtons = store.load()

        showStatus()

        voice = VoiceSearch(this, remote)
        voice.onStatus = { findViewById<TextView>(R.id.search_hint).text = it }
        findViewById<View>(R.id.voice_search).setOnClickListener { voice.start() }
        findViewById<View>(R.id.search_bar).setOnClickListener { voice.type() }

        // The first page: what the TV suggests; a tap plays it there.
        val suggestions = findViewById<SuggestionsView>(R.id.suggestions)
        suggestions.onPlay = { s ->
            if (remote.wifi.connected) {
                remote.wifi.play(s.id)
                toast(getString(R.string.suggestion_playing, s.title, s.app))
            } else toast(getString(R.string.suggestion_needs_wifi))
        }
        suggestions.show(remote.wifi.suggestions, remote.wifi.tvName != null)
        remote.wifi.onSuggestions = { suggestions.show(it, true) }

        // The screen mirror: the TV's screen here, full screen, touched like a touchscreen.
        mirrorView = findViewById(R.id.mirror_view)
        mirrorView.onTouch = { finger, kind, x, y -> remote.wifi.touch(finger, kind, x, y) }
        screen = ScreenLink(this, mirrorView)
        findViewById<View>(R.id.mirror).setOnClickListener { setMirror(true) }
        findViewById<View>(R.id.mirror_exit).setOnClickListener { setMirror(false) }
        findViewById<View>(R.id.mirror_keyboard).setOnClickListener {
            if (remote.wifi.connected) typingBox.show(null) else toast(getString(R.string.keyboard_offline))
        }
        for (id in intArrayOf(R.id.mirror, R.id.mirror_exit, R.id.mirror_keyboard)) Haptics.attach(findViewById(id))

        findViewById<ImageButton>(R.id.listen).setOnClickListener {
            if (ListenService.running) stopService(Intent(this, ListenService::class.java)) else startListening()
        }
        ListenService.onChange = { showListen() }
        showListen()

        findViewById<DpadView>(R.id.dpad).listener = dpadListener(
            Codes.UP, Codes.DOWN, Codes.LEFT, Codes.RIGHT, Codes.OK)
        findViewById<TrackpadView>(R.id.trackpad).listener = object : TrackpadView.Listener {
            override fun onMove(dx: Int, dy: Int) = remote.pointerMove(dx, dy)
            override fun onWheel(dy: Int, dx: Int) = remote.pointerWheel(dy, dx)
            override fun onButton(right: Boolean, down: Boolean) = remote.pointerButton(right, down)
            override fun onStop() = remote.pointerStop()
        }
        findViewById<View>(R.id.pointer_look).setOnClickListener { choosePointer() }

        // The PC keyboard page, and typing into the TV's text boxes with this phone's own keyboard.
        keyboard = findViewById(R.id.keyboard)
        keyboard.listener = KeyboardView.Listener { vk, down -> if (remote.wifi.connected) remote.wifi.key(vk, down) }
        keyboard.hint = if (remote.wifi.tvName == null) getString(R.string.keyboard_offline) else null
        typingBox = TypingBox(this, remote.wifi)
        findViewById<View>(R.id.keyboard_type).setOnClickListener {
            if (remote.wifi.connected) typingBox.show(null) else toast(getString(R.string.keyboard_offline))
        }
        remote.wifi.onTyping = { field ->
            when {
                field == null -> {
                    pendingTyping = null
                    typingBox.close()
                }
                resumed -> typingBox.show(field)
                else -> {
                    pendingTyping = field
                }
            }
        }
        // The trackpad's buttons, held down for as long as the finger is (to drag with another finger on the pad).
        bindHold(findViewById<View>(R.id.left_click), { remote.pointerButton(false, true) }, { remote.pointerButton(false, false) })
        bindHold(findViewById<View>(R.id.right_click), { remote.pointerButton(true, true) }, { remote.pointerButton(true, false) })

        mapOf(
            R.id.power to Codes.POWER,
            R.id.back to Codes.BACK,
            R.id.home to Codes.HOME,
            R.id.menu to Codes.MENU,
            R.id.volume_down to Codes.VOLUME_DOWN,
            R.id.mute to Codes.MUTE,
            R.id.volume_up to Codes.VOLUME_UP,
            R.id.mirror_back to Codes.BACK,
            R.id.mirror_home to Codes.HOME,
            R.id.mirror_volume_down to Codes.VOLUME_DOWN,
            R.id.mirror_volume_up to Codes.VOLUME_UP,
            R.id.play_pause to Codes.PLAY_PAUSE,
            R.id.exit to Codes.EXIT,
            R.id.full_screen to Codes.FULL_SCREEN,
            R.id.sleep_timer to Codes.SLEEP_TIMER,
            R.id.delete to Codes.DELETE,
            R.id.app_netflix to Codes.NETFLIX,
            R.id.app_youtube to Codes.YOUTUBE,
            R.id.app_prime to Codes.PRIME_VIDEO,
            R.id.app_hotstar to Codes.JIOHOTSTAR,
            R.id.app_web to Codes.WEB,
        ).forEach { (id, command) -> bindHold(findViewById<View>(id), Codes.ADDRESS, command) }

        findViewById<TextView>(R.id.edit_buttons).setOnClickListener {
            editing = !editing
            (it as TextView).setText(if (editing) R.string.done else R.string.edit)
            showMyButtons()
        }
        findViewById<View>(R.id.add_button).setOnClickListener { editButton(null) }
        showMyButtons()

        updater = AppUpdater(this)
        findViewById<TextView>(R.id.version).apply {
            text = getString(R.string.version_label, BuildConfig.VERSION_NAME, BuildConfig.COMMIT)
            setOnClickListener { checkForUpdate(manual = true) }
        }
        findViewById<Button>(R.id.update_now).setOnClickListener { startUpdate() }
        // Keys that act on a tap still click under the finger (the held ones do in bindHold).
        for (id in intArrayOf(R.id.listen, R.id.voice_search, R.id.search_bar, R.id.add_button, R.id.update_now)) {
            Haptics.attach(findViewById(id))
        }

        val pager = findViewById<PagerView>(R.id.pager)
        val dots = findViewById<PageDots>(R.id.dots)
        pager.go(REMOTE_PAGE, animate = false)
        dots.count = pager.pageCount
        dots.current = pager.page
        pager.onPageChanged = {
            dots.current = it
            keyboard.releaseAll()   // nothing stays held down on the TV after leaving the keyboard
        }
        dots.onPick = { pager.go(it) }
        InstallReceiver.onFailure = { message ->
            updating = false
            installStarted = false
            findViewById<View>(R.id.update_banner).visibility = View.VISIBLE
            findViewById<TextView>(R.id.update_text).text = message
            findViewById<Button>(R.id.update_now).isEnabled = true
        }
    }

    /** The TV's sound in this phone's headphones, sent over Wi-Fi; it keeps playing with the screen off. */
    private fun startListening() {
        val tv = remote.wifi.tvAddress?.hostAddress   // connected (so paired): the sound needs that too
        if (tv == null) {
            toast(getString(R.string.listen_no_tv))
            return
        }
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 43)   // for its Stop button; it plays either way
        }
        startForegroundService(Intent(this, ListenService::class.java).putExtra(ListenService.EXTRA_TV, tv))
        toast(getString(R.string.listen_stop))
    }

    /** The headphones button at the top: green while the TV's sound plays here. */
    private fun showListen() {
        val button = findViewById<ImageButton>(R.id.listen)
        val on = ListenService.running
        button.isSelected = on
        button.imageTintList = ColorStateList.valueOf(getColor(if (on) R.color.background else R.color.ink))
        val label = getString(if (on) R.string.listen_stop else R.string.listen_start)
        button.contentDescription = label
        button.tooltipText = label
    }

    /** Back on another page goes to the remote; on the remote, it leaves as usual. */
    @Suppress("OVERRIDE_DEPRECATION", "DEPRECATION")
    override fun onBackPressed() {
        if (mirroring) {
            setMirror(false)
            return
        }
        val pager = findViewById<PagerView>(R.id.pager)
        if (pager.page != REMOTE_PAGE) pager.go(REMOTE_PAGE) else super.onBackPressed()
    }

    /**
     * The screen mirror on or off: the TV's screen full screen in landscape (the remote's pages aren't needed: touch
     * the picture), and the phone's screen kept on; off, back to the remote, upright.
     */
    private fun setMirror(on: Boolean) {
        if (on && !remote.wifi.connected) {
            toast(getString(R.string.mirror_needs_wifi))
            return
        }
        mirroring = on
        findViewById<View>(R.id.main_ui).visibility = if (on) View.GONE else View.VISIBLE
        findViewById<View>(R.id.mirror_layer).visibility = if (on) View.VISIBLE else View.GONE
        requestedOrientation = if (on) ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE else ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
        fullScreen(on)
        if (on) startMirror()
        else {
            screen.stop()
            mirrorView.clear()
        }
    }

    private fun startMirror() {
        // Less of the Wi-Fi for the picture while this phone plays the TV's sound too.
        screen.start({ remote.wifi.tvAddress?.hostAddress ?: remote.wifi.manualAddress }, { if (ListenService.running) 2500 else 4000 })
    }

    @Suppress("DEPRECATION")
    private fun fullScreen(on: Boolean) {
        if (Build.VERSION.SDK_INT >= 30) {
            window.insetsController?.let { c ->
                if (on) {
                    c.hide(WindowInsets.Type.systemBars())
                    c.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                } else c.show(WindowInsets.Type.systemBars())
            }
        } else {
            window.decorView.systemUiVisibility = if (!on) 0 else View.SYSTEM_UI_FLAG_FULLSCREEN or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or
                View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or View.SYSTEM_UI_FLAG_LAYOUT_STABLE or
                View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
        }
        if (on) window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        else window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
    }

    override fun onDestroy() {
        ListenService.onChange = null
        InstallReceiver.onFailure = null
        voice.release()
        super.onDestroy()
    }

    override fun onPause() {
        voice.stop()
        keyboard.releaseAll()
        resumed = false
        super.onPause()
    }

    // Over Wi-Fi the app finds the TV by itself each time it's opened, and lets go when it's put away. Bluetooth stays
    // off meanwhile (BluetoothGuard).
    override fun onStart() {
        super.onStart()
        remote.wifi.start()
        remote.board.start()
        if (mirroring) startMirror()
        BluetoothGuard.askUser = { askToTurnOffBluetooth() }
        BluetoothGuard.hold(this, "screen")
    }

    override fun onStop() {
        remote.wifi.stop()
        remote.board.stop()
        screen.stop()
        BluetoothGuard.askUser = null
        BluetoothGuard.release("screen")
        super.onStop()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        val granted = grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED
        when (requestCode) {
            VoiceSearch.REQUEST_MIC -> voice.onPermissionResult(granted)
            REQUEST_BLUETOOTH -> if (granted) askAndroidToTurnOffBluetooth() else openBluetoothSettings()
        }
    }

    /** Google's voice screen (the fallback for voice search) and Android's Bluetooth question answer here. */
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        if (requestCode == REQUEST_BLUETOOTH_OFF) {
            if (BluetoothGuard.isOn(this) && resultCode != RESULT_OK) toast(getString(R.string.bluetooth_still_on))
            return
        }
        if (!voice.onActivityResult(requestCode, resultCode, data)) super.onActivityResult(requestCode, resultCode, data)
    }

    // ---------------------------------------------------------------- Bluetooth off

    /** Bluetooth is on and this phone wants the user's OK before the app switches it off. */
    private fun askToTurnOffBluetooth() {
        if (BluetoothGuard.needsPermission(this)) {
            AlertDialog.Builder(this)
                .setTitle(R.string.bluetooth_off_title)
                .setMessage(R.string.bluetooth_off_message)
                .setPositiveButton(R.string.turn_off) { _, _ ->
                    requestPermissions(arrayOf(Manifest.permission.BLUETOOTH_CONNECT), REQUEST_BLUETOOTH)
                }
                .setNegativeButton(R.string.not_now, null)
                .show()
        } else askAndroidToTurnOffBluetooth()
    }

    /** Switches Bluetooth off (Android 12 and older), or shows Android's own question (13 and newer). */
    private fun askAndroidToTurnOffBluetooth() {
        if (BluetoothGuard.switchOff(this)) return
        try {
            startActivityForResult(Intent(ACTION_REQUEST_DISABLE), REQUEST_BLUETOOTH_OFF)
        } catch (e: Exception) {   // a phone without that question, or no permission
            openBluetoothSettings()
        }
    }

    private fun openBluetoothSettings() {
        if (!BluetoothGuard.isOn(this)) return
        toast(getString(R.string.bluetooth_turn_off_here))
        runCatching { startActivity(Intent(Settings.ACTION_BLUETOOTH_SETTINGS)) }
    }

    override fun onResume() {
        super.onResume()
        resumed = true
        pendingTyping?.let {
            pendingTyping = null
            typingBox.show(it)
        }
        if (installStarted) {
            // Back from Android's install screen without updating (if it had updated, this app would have restarted).
            installStarted = false
            updating = false
            showUpdate(availableUpdate)
        }
        if (lastUpdateCheck == 0L || SystemClock.elapsedRealtime() - lastUpdateCheck > 15 * 60_000) checkForUpdate(manual = false)
    }

    // ---------------------------------------------------------------- updates

    private fun checkForUpdate(manual: Boolean) {
        if (updating) return
        lastUpdateCheck = SystemClock.elapsedRealtime()
        if (manual) toast(getString(R.string.checking))
        background.execute {
            val result = runCatching { updater.check() }
            runOnUiThread {
                if (isDestroyed) return@runOnUiThread
                result.onSuccess { release ->
                    showUpdate(release)
                    if (manual && release == null) toast(getString(R.string.up_to_date))
                }.onFailure {
                    if (manual) toast(getString(R.string.check_failed, it.message ?: ""))
                }
            }
        }
    }

    private fun showUpdate(release: AppUpdater.Release?) {
        availableUpdate = release
        findViewById<View>(R.id.update_banner).visibility = if (release == null) View.GONE else View.VISIBLE
        if (release == null) return
        val title = release.message.ifBlank { release.versionName }
        findViewById<TextView>(R.id.update_text).text = getString(R.string.update_available, title)
        findViewById<Button>(R.id.update_now).isEnabled = true
    }

    private fun startUpdate() {
        val release = availableUpdate ?: return
        if (!packageManager.canRequestPackageInstalls()) {
            askToAllowInstalls()
            return
        }
        updating = true
        val text = findViewById<TextView>(R.id.update_text)
        val button = findViewById<Button>(R.id.update_now)
        button.isEnabled = false
        text.text = getString(R.string.downloading, 0)
        background.execute {
            try {
                val apk = updater.download(release) { percent ->
                    runOnUiThread { text.text = getString(R.string.downloading, percent) }
                }
                runOnUiThread {
                    text.setText(R.string.installing)
                    installStarted = true
                }
                updater.install(apk)
            } catch (e: Exception) {
                runOnUiThread {
                    updating = false
                    installStarted = false
                    button.isEnabled = true
                    text.text = getString(R.string.update_failed, e.message ?: e.javaClass.simpleName)
                }
            }
        }
    }

    /** Android only lets an app install updates after you allow it once in Settings. */
    private fun askToAllowInstalls() {
        AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
            .setTitle(R.string.allow_updates_title)
            .setMessage(R.string.allow_updates_message)
            .setPositiveButton(R.string.open_settings) { _, _ ->
                startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:$packageName")))
            }
            .setNegativeButton(R.string.cancel, null)
            .show()
    }

    private fun toast(message: String) = Toast.makeText(this, message, Toast.LENGTH_SHORT).show()

    private fun showStatus() {
        val status = findViewById<TextView>(R.id.status)
        val tv = remote.wifi.tvName
        val address = remote.wifi.manualAddress
        val unpaired = remote.wifi.unpairedTv
        status.setTextColor(getColor(if (tv != null) R.color.good else if (unpaired != null) R.color.warn else R.color.muted))
        val board = remote.board
        val pcOn = board.pcOn
        val tvOn = board.tvOn
        val boardLine = if (board.connected && pcOn != null && tvOn != null)
            getString(R.string.board_state, onOff(pcOn), onOff(tvOn)) else null
        status.text = when {
            tv != null -> getString(R.string.wifi_connected, tv) + (boardLine?.let { " · $it" } ?: "")
            unpaired != null -> getString(R.string.pair_offer, unpaired)
            remote.wifi.tvTooOld -> getString(R.string.tv_too_old)
            board.connected && pcOn == false -> getString(R.string.board_pc_off, onOff(tvOn == true))
            address != null -> getString(if (remote.ir.available) R.string.wifi_trying_ir else R.string.wifi_trying, address)
            !remote.ir.available -> getString(R.string.wifi_looking_no_ir)
            !remote.ir.supports38kHz -> getString(R.string.no_38khz)
            else -> getString(R.string.ready)
        }
        status.setOnClickListener { if (remote.wifi.unpairedTv != null) showPairing() else editTvAddress() }
        findViewById<TrackpadView>(R.id.trackpad).hint =
            if (tv == null && !remote.ir.available) getString(R.string.trackpad_offline) else null
        if (::keyboard.isInitialized) keyboard.hint = if (tv == null) getString(R.string.keyboard_offline) else null
    }

    private fun onOff(on: Boolean) = getString(if (on) R.string.state_on else R.string.state_off)

    /**
     * Pairs this phone with the TV, once: the TV shows a 6-digit code, typed here. Only paired phones can use the TV
     * over Wi-Fi (or hear its sound, or press Power through the receiver box), so neighbours on a shared Wi-Fi can't.
     */
    private fun showPairing() {
        val tv = remote.wifi.unpairedTv ?: return
        val density = resources.displayMetrics.density
        val padding = (20 * density).toInt()
        val message = TextView(this).apply {
            setTextColor(getColor(R.color.muted))
            textSize = 16f
        }
        val input = EditText(this).apply {
            hint = "000000"
            inputType = InputType.TYPE_CLASS_NUMBER
            filters = arrayOf(InputFilter.LengthFilter(6))
            gravity = Gravity.CENTER
            textSize = 32f
            letterSpacing = 0.3f
            setSingleLine()
        }
        val form = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(padding, padding / 2, padding, 0)
            addView(message)
            addView(input)
        }
        var done = false
        var waitingForCode = false
        val dialog = AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
            .setTitle(getString(R.string.pair_title, tv))
            .setView(form)
            .setPositiveButton(R.string.pair, null)
            .setNegativeButton(R.string.cancel, null)
            .setNeutralButton(R.string.tv_address_title) { _, _ -> editTvAddress() }
            .create()
        dialog.setOnDismissListener { if (!done) remote.wifi.cancelPairing() }
        dialog.window?.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_STATE_VISIBLE)
        dialog.show()
        val pair = dialog.getButton(AlertDialog.BUTTON_POSITIVE)

        val listener = object : WifiLink.PairListener {
            override fun codeShown() {
                if (!dialog.isShowing) return
                waitingForCode = true
                message.setText(R.string.pair_type_code)
                input.isEnabled = true
                input.requestFocus()
                pair.setText(R.string.pair)
                pair.isEnabled = true
            }

            override fun refused(waitSeconds: Int) {
                if (!dialog.isShowing) return
                message.text = if (waitSeconds == 0) getString(R.string.pair_busy) else getString(R.string.pair_wait, (waitSeconds + 59) / 60)
                pair.setText(R.string.pair_again)
                pair.isEnabled = true
            }

            override fun finished(result: Handshake.PairResult) {
                if (result == Handshake.PairResult.PAIRED) {
                    done = true
                    dialog.dismiss()
                    toast(getString(R.string.paired, tv))
                    return
                }
                if (!dialog.isShowing) return
                message.setText(when (result) {
                    Handshake.PairResult.WRONG_CODE -> R.string.pair_wrong
                    Handshake.PairResult.NOT_SAFE -> R.string.pair_not_safe
                    else -> R.string.pair_failed
                })
                input.setText("")
                input.isEnabled = false
                pair.setText(R.string.pair_again)
                pair.isEnabled = true
            }
        }
        // Each try asks the TV for a new code: a code is only good once.
        val start = {
            waitingForCode = false
            message.text = getString(R.string.pair_asking, tv)
            input.setText("")
            input.isEnabled = false
            pair.isEnabled = false
            remote.wifi.pair(listener)
        }
        pair.setOnClickListener {
            if (!waitingForCode) {
                start()
                return@setOnClickListener
            }
            val code = input.text.toString()
            if (code.length != 6) {
                input.error = getString(R.string.need_code)
                return@setOnClickListener
            }
            waitingForCode = false
            input.isEnabled = false
            pair.isEnabled = false
            message.setText(R.string.pair_checking)
            remote.wifi.typeCode(code)
        }
        start()
    }

    /** The TV's address on the Wi-Fi (reserved for it in the router), or empty to find it automatically. */
    private fun editTvAddress() {
        val density = resources.displayMetrics.density
        val padding = (20 * density).toInt()
        val input = EditText(this).apply {
            setText(remote.wifi.manualAddress ?: "")
            hint = getString(R.string.tv_address_hint)
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
            setSingleLine()
            setSelection(text.length)
        }
        val help = TextView(this).apply {
            setText(R.string.tv_address_help)
            setTextColor(getColor(R.color.muted))
            setPadding(0, padding / 2, 0, 0)
        }
        val form = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(padding, padding / 2, padding, 0)
            addView(input)
            addView(help)
        }
        val shown = AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
            .setTitle(R.string.tv_address_title)
            .setView(form)
            .setPositiveButton(R.string.save, null)
            .setNeutralButton(R.string.find_automatically) { _, _ ->
                remote.wifi.manualAddress = ""
                showStatus()
            }
            .setNegativeButton(R.string.cancel, null)
            .show()
        shown.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
            val address = input.text.toString().trim()
            if (!Regex("^\\d{1,3}(\\.\\d{1,3}){3}$|^[A-Za-z0-9][A-Za-z0-9.-]*$").matches(address)) {
                input.error = getString(R.string.need_address)
                return@setOnClickListener
            }
            remote.wifi.manualAddress = address
            showStatus()
            shown.dismiss()
        }
    }

    private fun dpadListener(up: Int, down: Int, left: Int, right: Int, center: Int) = object : DpadView.Listener {
        private var pressId = 0

        override fun onPress(part: DpadView.Part) {
            val command = when (part) {
                DpadView.Part.UP -> up
                DpadView.Part.DOWN -> down
                DpadView.Part.LEFT -> left
                DpadView.Part.RIGHT -> right
                DpadView.Part.CENTER -> center
            }
            pressId = remote.press(Codes.ADDRESS, command)
        }

        override fun onRelease(part: DpadView.Part) = remote.release(pressId)
    }

    /** Sends while the finger is down, like holding a button on a real remote; a swipe turns the page instead. */
    private fun bindHold(view: View, address: Int, command: Int) {
        var pressId = 0
        bindHold(view, { pressId = remote.press(address, command) }, { remote.release(pressId) })
    }

    @SuppressLint("ClickableViewAccessibility")
    private fun bindHold(view: View, onPress: () -> Unit, onRelease: () -> Unit) {
        val gesture = PressGesture(view, onPress, onRelease)
        view.setOnTouchListener { v, event ->
            val handled = gesture.onTouch(event)
            if (event.actionMasked == MotionEvent.ACTION_UP) v.performClick()
            handled
        }
    }

    // ---------------------------------------------------------------- the TV's pointer

    /** The TV pointer's style, size and colour: it changes on the TV as you pick (over Wi-Fi). */
    private fun choosePointer() {
        if (!remote.viaWifi) {
            toast(getString(R.string.pointer_needs_wifi))
            return
        }
        val now = (remote.wifi.pointerLook ?: "arrow 64 white").split(" ")
        var style = now.getOrNull(0) ?: "arrow"
        var size = now.getOrNull(1)?.toIntOrNull() ?: 64
        var color = now.getOrNull(2) ?: "white"
        val send = { remote.wifi.setPointer(style, size, color) }

        val padding = (20 * resources.displayMetrics.density).toInt()
        val form = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(padding, padding / 2, padding, 0)
        }
        form.addView(caption(R.string.pointer_style))
        form.addView(choices(listOf("arrow" to R.string.pointer_arrow, "dot" to R.string.pointer_dot, "ring" to R.string.pointer_ring),
            3, { style }) { style = it; send() })
        form.addView(caption(R.string.pointer_size))
        form.addView(choices(listOf(48 to R.string.size_medium, 64 to R.string.size_large, 96 to R.string.size_huge, 128 to R.string.size_giant),
            2, { size }) { size = it; send() })
        form.addView(caption(R.string.pointer_color))
        val colors = mapOf("white" to R.color.pointer_white, "yellow" to R.color.pointer_yellow, "blue" to R.color.pointer_blue,
            "green" to R.color.pointer_green, "pink" to R.color.pointer_pink)
        form.addView(choices(listOf("white" to R.string.color_white, "yellow" to R.string.color_yellow, "blue" to R.string.color_blue,
            "green" to R.string.color_green, "pink" to R.string.color_pink), 3, { color }, colors) { color = it; send() })
        form.addView(caption(R.string.pointer_help))
        AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
            .setTitle(R.string.pointer_title)
            .setView(form)
            .setPositiveButton(R.string.done, null)
            .show()
    }

    /** Keys to pick one of [options] (value to label); the picked one is ticked. [colors] tints each label. */
    private fun <T> choices(options: List<Pair<T, Int>>, columns: Int, picked: () -> T, colors: Map<T, Int>? = null, onPick: (T) -> Unit): FitGrid {
        val density = resources.displayMetrics.density
        val grid = FitGrid(this).apply {
            this.columns = columns
            rowHeight = (50 * density).toInt()
            gap = (8 * density).toInt()
        }
        val keys = options.map { (value, label) -> Triple(value, getString(label), Button(this, null, 0, R.style.RemoteKey_Text)) }
        fun show() {
            for ((value, label, key) in keys) {
                val on = value == picked()
                key.text = if (on) "✓ $label" else label
                key.setTextColor(getColor(colors?.get(value) ?: if (on) R.color.good else R.color.ink))
            }
        }
        for ((value, _, key) in keys) {
            key.setOnClickListener {
                onPick(value)
                show()
            }
            Haptics.attach(key)
            grid.addView(key)
        }
        show()
        return grid
    }

    // ---------------------------------------------------------------- your own buttons

    /** Three to a row; the rows get shorter as buttons are added, so they always fit their page. */
    private fun showMyButtons() {
        val grid = findViewById<FitGrid>(R.id.my_buttons)
        grid.removeAllViews()
        findViewById<View>(R.id.my_buttons_empty).visibility = if (myButtons.isEmpty()) View.VISIBLE else View.GONE
        for (button in myButtons) {
            val view = Button(this, null, 0, R.style.RemoteKey_Text).apply { text = button.label }
            if (editing) {
                view.setOnClickListener { editButton(button) }
                Haptics.attach(view)
            } else bindHold(view, button.address, button.command)
            grid.addView(view)
        }
    }

    /** Adds a button, or edits/deletes one when [existing] is given. */
    private fun editButton(existing: CustomButton?) {
        val density = resources.displayMetrics.density
        val padding = (20 * density).toInt()
        val form = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(padding, padding / 2, padding, 0)
        }
        val label = EditText(this).apply {
            hint = getString(R.string.button_name)
            setText(existing?.label ?: "")
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_CAP_WORDS
        }
        val command = EditText(this).apply {
            hint = getString(R.string.command_hint)
            setText("%02X".format(existing?.command ?: store.nextFreeCommand(myButtons)))
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_CAP_CHARACTERS
        }
        val address = EditText(this).apply {
            hint = getString(R.string.address_hint)
            setText("%02X".format(existing?.address ?: Codes.ADDRESS))
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_CAP_CHARACTERS
        }
        val help = TextView(this).apply {
            setText(R.string.custom_help)
            setTextColor(getColor(R.color.muted))
            gravity = Gravity.START
            setPadding(0, padding / 2, 0, 0)
        }
        form.addView(caption(R.string.button_name))
        form.addView(label)
        form.addView(caption(R.string.command_hint))
        form.addView(command)
        form.addView(caption(R.string.address_hint))
        form.addView(address)
        form.addView(help)

        val dialog = AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
            .setTitle(if (existing == null) R.string.add_button else R.string.edit_button)
            .setView(form)
            .setPositiveButton(R.string.save, null)
            .setNegativeButton(R.string.cancel, null)
        if (existing != null) dialog.setNeutralButton(R.string.delete) { _, _ ->
            myButtons.remove(existing)
            store.save(myButtons)
            showMyButtons()
        }
        val shown = dialog.show()
        // Validate before closing.
        shown.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
            val name = label.text.toString().trim()
            val cmd = command.text.toString().trim().removePrefix("0x").toIntOrNull(16)
            val addr = address.text.toString().trim().removePrefix("0x").toIntOrNull(16)
            if (name.isEmpty()) {
                label.error = getString(R.string.need_name)
                return@setOnClickListener
            }
            if (cmd == null || cmd !in 0..0xFF) {
                command.error = getString(R.string.need_hex)
                return@setOnClickListener
            }
            if (addr == null || addr !in 0..0xFF) {
                address.error = getString(R.string.need_hex)
                return@setOnClickListener
            }
            val button = CustomButton(name, addr, cmd)
            val index = if (existing == null) -1 else myButtons.indexOf(existing)
            if (index >= 0) myButtons[index] = button else myButtons += button
            store.save(myButtons)
            showMyButtons()
            shown.dismiss()
            AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
                .setTitle(R.string.teach_tv_title)
                .setMessage(getString(R.string.teach_tv_message, button.label, button.code))
                .setPositiveButton(R.string.ok, null)
                .show()
        }
    }

    private fun caption(text: Int) = TextView(this).apply {
        setText(text)
        setTextColor(getColor(R.color.muted))
        textSize = 13f
        setPadding(0, (12 * resources.displayMetrics.density).toInt(), 0, 0)
    }
}
