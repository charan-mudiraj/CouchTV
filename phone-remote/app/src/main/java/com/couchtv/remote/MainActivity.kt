package com.couchtv.remote

import android.Manifest
import android.animation.ObjectAnimator
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
import android.os.Handler
import android.os.Looper
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
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import java.util.concurrent.Executors

/**
 * The remote, fitting the screen without scrolling: how it reaches the TV (icons), the TV's screen and sound here and
 * power stay at the top, and pages swipe sideways underneath (remote, apps, trackpad, keyboard), with dots to show
 * which. It opens on the remote; to its left, what the TV suggests (popular films and songs), to play there with a tap.
 */
class MainActivity : Activity() {
    companion object {
        private const val REMOTE_PAGE = 1                // the page it opens on; the suggestions are page 0
        private const val REQUEST_BLUETOOTH = 44       // the "Nearby devices" permission, to switch Bluetooth off
        private const val REQUEST_BLUETOOTH_OFF = 45   // Android's "turn off Bluetooth?" question
        private const val ACTION_REQUEST_DISABLE = "android.bluetooth.adapter.action.REQUEST_DISABLE"
        private const val TV_GAP_MS = 15_000L          // the TV ignores its power button this long after switching
        private const val OFF_DELAY_MS = 5_000L        // Power with the TV on: this long to change your mind
    }

    private lateinit var remote: Remote
    private lateinit var feed: SuggestionFeed
    private var tvWasThere = false
    private lateinit var voice: VoiceSearch
    private lateinit var statusPanel: StatusPanel
    private lateinit var sleepTimer: SleepTimer
    private lateinit var tests: TestHooks
    private var pulsing: ObjectAnimator? = null
    private val handler = Handler(Looper.getMainLooper())
    private lateinit var keyboard: KeyboardView
    private lateinit var typingBox: TypingBox
    private lateinit var mirrorView: MirrorView
    private lateinit var screen: ScreenLink
    private var mirroring = false
    private var resumed = false
    private var pendingTyping: WifiLink.TypingField? = null   // a text box got the focus while this was in the background
    private var pendingPlay: Suggestion? = null               // tapped while the TV was off: plays once it's on...
    private var pendingUntil = 0L                             // ...if it comes on by then

    // Power: when it was pressed, what it was doing, and the TV as the receiver box said then.
    private var powerPressedAt = 0L
    private var powerTurningOff = false
    private var powerBlocked = false                          // pressed again too soon
    private var powerTvOn: Boolean? = null
    private var offAt = 0L                                    // turning off, counting down: when it goes (0: not)
    private var started = false                               // between onStart and onStop
    private var boardAfterPower = false                       // the receiver box link stays up until a turn-off is sent

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
        statusPanel = StatusPanel(this)
        remote = Remote(this)
        feed = SuggestionFeed(this)
        remote.wifi.onChange = {
            showStatus()
            val tvThere = remote.wifi.tvName != null
            findViewById<SuggestionsView>(R.id.suggestions).show(feed.current, tvThere)
            if (tvWasThere && !tvThere) fetchSuggestionsSoon()   // the TV went off: the phone's own list from now on
            tvWasThere = tvThere
        }
        remote.board.onChange = {
            showStatus()
            boardChanged()
        }
        remote.onPowerNeedsIr = {
            powerFailed()
            toast(getString(R.string.power_needs_ir))
        }

        showStatus()
        findViewById<View>(R.id.status).setOnClickListener { openStatus() }
        findViewById<PowerButton>(R.id.power).apply {
            setOnClickListener { pressPower() }
            onReady = { hidePowerNote() }
        }
        findViewById<View>(R.id.power_cancel).setOnClickListener { cancelPowerOff() }
        findViewById<View>(R.id.power_now).setOnClickListener { powerOffNow() }
        for (id in intArrayOf(R.id.power_cancel, R.id.power_now)) Haptics.attach(findViewById(id))

        voice = VoiceSearch(this, remote)
        voice.onStatus = { findViewById<TextView>(R.id.search_hint).text = it }
        findViewById<View>(R.id.voice_search).setOnClickListener { voice.start() }
        findViewById<View>(R.id.search_bar).setOnClickListener { voice.type() }

        // The first page: popular films and songs, the TV's list, or with the TV off the phone's own; a tap plays it there.
        val suggestions = findViewById<SuggestionsView>(R.id.suggestions)
        suggestions.onPlay = { playSuggestion(it) }
        suggestions.show(feed.current, false)
        feed.onChange = { list -> if (remote.wifi.tvName == null) suggestions.show(list, false) }
        remote.wifi.onSuggestSetup = { feed.setSetup(it) }
        remote.wifi.onSuggestions = { list ->
            feed.fromTv(list)
            suggestions.show(list, true)
            // The TV sends its list as it connects: the one tapped while it was off can play now.
            pendingPlay?.let { wanted ->
                pendingPlay = null
                if (SystemClock.elapsedRealtime() > pendingUntil) return@let
                remote.wifi.play(wanted)
                toast(getString(R.string.suggestion_playing, wanted.title, wanted.app))
            }
        }

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
        findViewById<TrackpadView>(R.id.trackpad).help = getString(R.string.trackpad_help)
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
            R.id.delete to Codes.DELETE,
            R.id.app_netflix to Codes.NETFLIX,
            R.id.app_youtube to Codes.YOUTUBE,
            R.id.app_prime to Codes.PRIME_VIDEO,
            R.id.app_hotstar to Codes.JIOHOTSTAR,
            R.id.app_web to Codes.WEB,
        ).forEach { (id, command) -> bindHold(findViewById<View>(id), Codes.ADDRESS, command) }

        // The sleep timer: any length, picked on the phone (over Wi-Fi); the key shows how long is left.
        sleepTimer = SleepTimer(this, remote, findViewById(R.id.sleep_timer))
        findViewById<View>(R.id.sleep_timer).setOnClickListener { sleepTimer.open() }

        // Testing from a PC over adb (TestHooks): pages, the mirror and the dialogs, besides fingers.
        tests = TestHooks(this) { command ->
            val p = command.split(" ")
            when (p[0]) {
                "page" -> p.getOrNull(1)?.toIntOrNull()?.let { findViewById<PagerView>(R.id.pager).go(it) } != null
                "mirror" -> {
                    setMirror(p.getOrNull(1) != "off")
                    true
                }
                "sleep" -> {
                    sleepTimer.open()
                    true
                }
                "status" -> {
                    openStatus()
                    true
                }
                "close" -> {
                    statusPanel.dismiss()
                    sleepTimer.dismiss()
                    true
                }
                else -> false
            }
        }

        updater = AppUpdater(this)
        findViewById<Button>(R.id.update_now).setOnClickListener { startUpdate() }
        // Keys that act on a tap still click under the finger (the held ones do in bindHold).
        for (id in intArrayOf(R.id.listen, R.id.voice_search, R.id.search_bar, R.id.update_now, R.id.status, R.id.power, R.id.sleep_timer)) {
            Haptics.attach(findViewById(id))
        }

        val pager = findViewById<PagerView>(R.id.pager)
        val dots = findViewById<PageDots>(R.id.dots)
        pager.go(REMOTE_PAGE, animate = false)
        dots.count = pager.pageCount
        dots.current = pager.page
        pager.onScroll = { at ->
            dots.position = at
            if (at < REMOTE_PAGE - 0.02f) suggestions.reveal()   // the suggestions coming into view: their cards rise in
        }
        pager.onPageChanged = {
            dots.current = it
            keyboard.releaseAll()   // nothing stays held down on the TV after leaving the keyboard
            showTvPointer()
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

    /**
     * A film or song from the first page: played on the TV. With the TV off (asleep, or shut down), Power turns it on
     * (through the receiver box, as the Power key does) and it plays as soon as the TV is back, within 3 minutes.
     */
    private fun playSuggestion(s: Suggestion) {
        if (remote.wifi.connected) {
            remote.wifi.play(s)
            toast(getString(R.string.suggestion_playing, s.title, s.app))
            return
        }
        if (remote.board.pcOn == true) {
            // Power would put it to sleep: it's on, this phone just can't reach CouchTV.
            toast(getString(R.string.suggestion_tv_unreachable))
            return
        }
        if (!remote.board.connected && !remote.ir.available) {
            toast(getString(R.string.power_needs_ir))   // no way to send Power from here
            return
        }
        val already = pendingPlay != null && SystemClock.elapsedRealtime() < pendingUntil
        pendingPlay = s
        pendingUntil = SystemClock.elapsedRealtime() + 180_000
        if (!already) remote.press(Codes.ADDRESS, Codes.POWER, hold = false)   // a second tap meanwhile just changes what plays
        toast(getString(R.string.suggestion_turning_on, s.title))
    }

    /**
     * The phone's own list, fetched if the one kept is a few hours old: a moment after the app opens, so a TV that's on
     * connects first and gives its own.
     */
    private fun fetchSuggestionsSoon() {
        findViewById<View>(R.id.pager).postDelayed({ if (remote.wifi.tvName == null) feed.refreshIfStale() }, 4000)
    }

    /**
     * The TV shows its pointer only while the trackpad page is in front on this phone (and hides it otherwise, so it
     * doesn't sit over a film): it's told whenever that changes.
     */
    private fun showTvPointer() {
        val pager = findViewById<PagerView>(R.id.pager)
        val page = findViewById<View>(R.id.trackpad).parent as View
        val trackpadPage = (pager.getChildAt(0) as android.view.ViewGroup).indexOfChild(page)
        remote.wifi.trackpadOpen = resumed && !mirroring && pager.page == trackpadPage
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
        showTvPointer()
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
        screen.start({ remote.wifi.tvAddress?.hostAddress ?: remote.wifi.manualAddress }, { if (ListenService.running) 2500 else 8000 })
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
        sleepTimer.stop()
        handler.removeCallbacksAndMessages(null)
        voice.release()
        super.onDestroy()
    }

    override fun onPause() {
        voice.stop()
        keyboard.releaseAll()
        resumed = false
        tests.stop()
        showTvPointer()
        super.onPause()
    }

    // Over Wi-Fi the app finds the TV by itself each time it's opened, and lets go when it's put away. Bluetooth stays
    // off meanwhile (BluetoothGuard).
    override fun onStart() {
        super.onStart()
        started = true
        boardAfterPower = false
        remote.wifi.start()
        remote.board.start()
        fetchSuggestionsSoon()
        if (mirroring) startMirror()
        BluetoothGuard.askUser = { askToTurnOffBluetooth() }
        BluetoothGuard.hold(this, "screen")
    }

    override fun onStop() {
        started = false
        remote.wifi.stop()
        // A turn-off counting down still goes (Power, then the phone locked): the box link waits for it.
        if (offAt != 0L) boardAfterPower = true else remote.board.stop()
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
        tests.start()
        showTvPointer()
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

    /**
     * The icons at the top: the TV (green when connected over Wi-Fi, yellow when it needs pairing or updating, grey and
     * breathing while it's looked for, "off" while the PC sleeps), the receiver box (green when it answers) and the IR
     * blaster (only while infrared is what's used). Tapping them shows it all in words ([openStatus]).
     */
    private fun showStatus() {
        val tv = remote.wifi.tvName
        val board = remote.board
        val asleep = tv == null && board.connected && board.pcOn == false
        val tvIcon = findViewById<ImageView>(R.id.status_tv)
        tvIcon.setImageResource(if (asleep) R.drawable.ic_tv_off else R.drawable.ic_tv)
        tint(tvIcon, when {
            tv != null -> R.color.good
            remote.wifi.unpairedTv != null || remote.wifi.tvTooOld -> R.color.warn
            else -> R.color.muted
        })
        pulse(tvIcon, tv == null && !asleep)
        val box = findViewById<ImageView>(R.id.status_box)
        box.visibility = if (board.connected || board.known) View.VISIBLE else View.GONE
        tint(box, if (board.connected) R.color.good else R.color.muted)
        box.alpha = if (board.connected) 1f else 0.55f
        val ir = findViewById<ImageView>(R.id.status_ir)
        ir.visibility = if (tv == null && remote.ir.available) View.VISIBLE else View.GONE
        tint(ir, if (remote.ir.supports38kHz) R.color.ink else R.color.warn)
        val status = findViewById<View>(R.id.status)
        val words = statusText()
        status.contentDescription = words
        status.tooltipText = words
        statusPanel.update(statusRows())
        findViewById<TrackpadView>(R.id.trackpad).hint =
            if (tv == null && !remote.ir.available) getString(R.string.trackpad_offline) else null
        if (::keyboard.isInitialized) keyboard.hint = if (tv == null) getString(R.string.keyboard_offline) else null
    }

    private fun tint(icon: ImageView, color: Int) {
        icon.imageTintList = ColorStateList.valueOf(getColor(color))
    }

    /** The TV icon breathes while the TV's being looked for (or waits to be paired). */
    private fun pulse(icon: View, on: Boolean) {
        if (on == (pulsing != null)) return
        pulsing?.cancel()
        pulsing = null
        if (on) pulsing = ObjectAnimator.ofFloat(icon, View.ALPHA, 1f, 0.35f).apply {
            duration = 900
            repeatMode = ObjectAnimator.REVERSE
            repeatCount = ObjectAnimator.INFINITE
            start()
        } else icon.alpha = 1f
    }

    /** The status in one line, for TalkBack and a long press on the icons. */
    private fun statusText(): String {
        val tv = remote.wifi.tvName
        val address = remote.wifi.manualAddress
        val unpaired = remote.wifi.unpairedTv
        val board = remote.board
        val pcOn = board.pcOn
        val tvOn = board.tvOn
        val boardLine = if (board.connected && pcOn != null && tvOn != null)
            getString(R.string.board_state, onOff(pcOn), onOff(tvOn)) else null
        return when {
            tv != null -> getString(R.string.wifi_connected, tv) + (boardLine?.let { " · $it" } ?: "")
            unpaired != null -> getString(R.string.pair_offer, unpaired)
            remote.wifi.tvTooOld -> getString(R.string.tv_too_old)
            board.connected && pcOn == false -> getString(R.string.board_pc_off, onOff(tvOn == true))
            address != null -> getString(if (remote.ir.available) R.string.wifi_trying_ir else R.string.wifi_trying, address)
            !remote.ir.available -> getString(R.string.wifi_looking_no_ir)
            !remote.ir.supports38kHz -> getString(R.string.no_38khz)
            else -> getString(R.string.ready)
        }
    }

    /** The status icons tapped: what each stands for, in words, with what to do about it, and the app's version. */
    private fun openStatus() {
        statusPanel.show(statusRows(), getString(R.string.version_label, BuildConfig.VERSION_NAME, BuildConfig.COMMIT)) {
            checkForUpdate(manual = true)
        }
    }

    private fun statusRows(): List<StatusPanel.Row> {
        val wifi = remote.wifi
        val board = remote.board
        val tv = wifi.tvName
        val unpaired = wifi.unpairedTv
        val address = wifi.manualAddress
        val tvTitle = getString(R.string.status_tv)
        val boxTitle = getString(R.string.status_box)
        val irTitle = getString(R.string.status_ir)
        val pcOn = board.pcOn
        val tvOn = board.tvOn
        return listOf(
            when {
                tv != null -> StatusPanel.Row(R.drawable.ic_tv, R.color.good, tvTitle, getString(R.string.status_tv_connected, tv))
                unpaired != null -> StatusPanel.Row(R.drawable.ic_tv, R.color.warn, tvTitle, getString(R.string.status_tv_unpaired, unpaired),
                    getString(R.string.action_pair)) { showPairing() }
                wifi.tvTooOld -> StatusPanel.Row(R.drawable.ic_tv, R.color.warn, tvTitle, getString(R.string.tv_too_old))
                board.connected && pcOn == false -> StatusPanel.Row(R.drawable.ic_tv_off, R.color.muted, tvTitle, getString(R.string.status_tv_asleep))
                address != null -> StatusPanel.Row(R.drawable.ic_tv, R.color.muted, tvTitle, getString(R.string.status_tv_trying, address),
                    getString(R.string.action_address)) { editTvAddress() }
                else -> StatusPanel.Row(R.drawable.ic_tv, R.color.muted, tvTitle, getString(R.string.status_tv_looking),
                    getString(R.string.action_address)) { editTvAddress() }
            },
            when {
                board.connected -> StatusPanel.Row(R.drawable.ic_router, R.color.good, boxTitle,
                    if (pcOn != null && tvOn != null) getString(R.string.status_box_state, onOff(pcOn), onOff(tvOn)) else getString(R.string.status_box_on))
                board.known -> StatusPanel.Row(R.drawable.ic_router, R.color.muted, boxTitle,
                    getString(if (remote.ir.available) R.string.status_box_quiet else R.string.status_box_quiet_no_ir))
                else -> StatusPanel.Row(R.drawable.ic_router, R.color.muted, boxTitle, getString(R.string.status_box_unknown))
            },
            when {
                !remote.ir.available -> StatusPanel.Row(R.drawable.ic_ir, R.color.muted, irTitle, getString(R.string.status_ir_none))
                !remote.ir.supports38kHz -> StatusPanel.Row(R.drawable.ic_ir, R.color.warn, irTitle, getString(R.string.no_38khz))
                else -> StatusPanel.Row(R.drawable.ic_ir, if (tv == null) R.color.ink else R.color.muted, irTitle, getString(R.string.status_ir_ready))
            },
        )
    }

    // ---------------------------------------------------------------- power

    /**
     * Power, through the receiver box (or by infrared). With the TV on it doesn't go at once: for 5 s the key counts
     * down and a card offers Cancel and Turn off now (tapping Power again cancels too), and the TV shows it's about to
     * go, so a tap by mistake does nothing. Switching on is at once. For about 15 s after switching, the TV ignores
     * its own power button (which the box presses), so then the key counts that down with a line under the top
     * saying what's going on; a press meanwhile shakes the key and says why, instead of being lost.
     */
    private fun pressPower() {
        val power = findViewById<PowerButton>(R.id.power)
        if (offAt != 0L) {
            cancelPowerOff()   // tapped again while it counts down: that's a no
            return
        }
        if (power.busy) {
            power.shake()
            powerBlocked = true
            showPowerNote()
            return
        }
        if (!remote.board.connected && !remote.ir.available) {
            toast(getString(R.string.power_needs_ir))
            return
        }
        if (remote.wifi.connected || remote.board.pcOn == true) startPowerOff() else sendPower(turningOff = false)
    }

    private val offNow = Runnable { powerOffNow() }
    private val offTick = Runnable { showPowerOff() }

    private fun startPowerOff() {
        offAt = SystemClock.elapsedRealtime() + OFF_DELAY_MS
        // The ring runs a moment past the turn-off, so the key goes straight on to the TV's 15 s.
        findViewById<PowerButton>(R.id.power).waitUntil(offAt + 300, OFF_DELAY_MS + 300, getColor(R.color.warn))
        handler.postDelayed(offNow, OFF_DELAY_MS)
        remote.wifi.powerSoon((OFF_DELAY_MS / 1000).toInt())
        showPowerOff()
    }

    /** The card under the top while a turn-off counts down: the seconds left, Cancel, Turn off now. */
    private fun showPowerOff() {
        handler.removeCallbacks(offTick)
        val card = findViewById<View>(R.id.power_off)
        if (offAt == 0L) {
            if (card.visibility == View.VISIBLE) card.animate().alpha(0f).setDuration(180).withEndAction { card.visibility = View.GONE }.start()
            return
        }
        val left = ((offAt - SystemClock.elapsedRealtime() + 999) / 1000).toInt().coerceAtLeast(1)
        findViewById<TextView>(R.id.power_off_text).text = getString(R.string.power_off_soon, left)
        if (card.visibility != View.VISIBLE) {
            hidePowerNote()
            card.animate().cancel()
            card.alpha = 0f
            card.visibility = View.VISIBLE
            card.animate().alpha(1f).setDuration(180).start()
        }
        handler.postDelayed(offTick, 200)
    }

    private fun cancelPowerOff() {
        if (offAt == 0L) return
        handler.removeCallbacks(offNow)
        offAt = 0L
        findViewById<PowerButton>(R.id.power).waitUntil(0, 1)
        showPowerOff()
        remote.wifi.powerSoon(0)
        toast(getString(R.string.power_cancelled))
    }

    private fun powerOffNow() {
        handler.removeCallbacks(offNow)
        if (offAt == 0L) return
        offAt = 0L
        showPowerOff()
        sendPower(turningOff = true)
    }

    private fun sendPower(turningOff: Boolean) {
        val power = findViewById<PowerButton>(R.id.power)
        powerTurningOff = turningOff
        powerBlocked = false
        powerTvOn = remote.board.tvOn
        powerPressedAt = SystemClock.elapsedRealtime()
        remote.press(Codes.ADDRESS, Codes.POWER, hold = false)
        power.waitUntil(powerPressedAt + TV_GAP_MS, TV_GAP_MS)
        showPowerNote()
        if (boardAfterPower) {
            // Sent while the app's in the background: the box link closes once the box has had it.
            boardAfterPower = false
            handler.postDelayed({ if (!started) remote.board.stop() }, 3000)
        }
    }

    private val powerTick = Runnable { showPowerNote() }

    /** The line under the top while the power key counts down, kept up to date. */
    private fun showPowerNote() {
        handler.removeCallbacks(powerTick)
        val power = findViewById<PowerButton>(R.id.power)
        if (!power.busy) {
            hidePowerNote()
            return
        }
        val note = findViewById<TextView>(R.id.power_note)
        note.text = getString(when {
            powerBlocked -> R.string.power_wait
            powerTurningOff -> R.string.power_turning_off
            else -> R.string.power_turning_on
        }, power.secondsLeft)
        note.setTextColor(getColor(if (powerBlocked) R.color.warn else R.color.ink))
        if (note.visibility != View.VISIBLE) {
            note.alpha = 0f
            note.visibility = View.VISIBLE
            note.animate().alpha(1f).setDuration(200).start()
        }
        handler.postDelayed(powerTick, 250)
    }

    private fun hidePowerNote() {
        handler.removeCallbacks(powerTick)
        val note = findViewById<TextView>(R.id.power_note)
        if (note.visibility != View.VISIBLE) return
        note.animate().alpha(0f).setDuration(200).withEndAction { note.visibility = View.GONE }.start()
    }

    /** Power couldn't be sent after all (the box didn't answer, and there's no IR blaster): nothing to wait for. */
    private fun powerFailed() {
        findViewById<PowerButton>(R.id.power).waitUntil(0, 1)
        hidePowerNote()
    }

    /**
     * The box says the TV switched while the key counts down: the box presses the TV's button once the PC has gone
     * to sleep (or woken), so the TV's 15 s run from then.
     */
    private fun boardChanged() {
        val tvOn = remote.board.tvOn ?: return
        val power = findViewById<PowerButton>(R.id.power)
        val before = powerTvOn
        powerTvOn = tvOn
        if (offAt != 0L || !power.busy || before == null || before == tvOn) return
        val until = minOf(SystemClock.elapsedRealtime() + TV_GAP_MS, powerPressedAt + 45_000)
        power.waitUntil(until, until - powerPressedAt)
        showPowerNote()
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

    private fun caption(text: Int) = TextView(this).apply {
        setText(text)
        setTextColor(getColor(R.color.muted))
        textSize = 13f
        setPadding(0, (12 * resources.displayMetrics.density).toInt(), 0, 0)
    }
}
