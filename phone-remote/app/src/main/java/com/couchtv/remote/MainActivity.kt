package com.couchtv.remote

import android.annotation.SuppressLint
import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Bundle
import android.os.SystemClock
import android.provider.Settings
import android.text.InputType
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import java.util.concurrent.Executors

/** The remote: D-pad, TV buttons, app shortcuts, a pointer pad, and buttons you add yourself. */
class MainActivity : Activity() {
    private lateinit var remote: IrRemote
    private lateinit var voice: VoiceSearch
    private lateinit var store: CustomButtons
    private lateinit var myButtons: MutableList<CustomButton>
    private var editing = false

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
        remote = IrRemote(this)
        store = CustomButtons(this)
        myButtons = store.load()

        showStatus()

        voice = VoiceSearch(this, remote)
        voice.onStatus = { findViewById<TextView>(R.id.search_hint).text = it }
        findViewById<View>(R.id.voice_search).setOnClickListener { voice.start() }
        findViewById<View>(R.id.search_bar).setOnClickListener { voice.type() }

        findViewById<DpadView>(R.id.dpad).listener = dpadListener(
            Codes.UP, Codes.DOWN, Codes.LEFT, Codes.RIGHT, Codes.OK)
        findViewById<DpadView>(R.id.pointer).apply {
            centerLabel = getString(R.string.click)
            listener = dpadListener(Codes.POINTER_UP, Codes.POINTER_DOWN, Codes.POINTER_LEFT, Codes.POINTER_RIGHT, Codes.CLICK)
        }

        mapOf(
            R.id.power to Codes.POWER,
            R.id.back to Codes.BACK,
            R.id.home to Codes.HOME,
            R.id.menu to Codes.MENU,
            R.id.volume_down to Codes.VOLUME_DOWN,
            R.id.mute to Codes.MUTE,
            R.id.volume_up to Codes.VOLUME_UP,
            R.id.play_pause to Codes.PLAY_PAUSE,
            R.id.exit to Codes.EXIT,
            R.id.full_screen to Codes.FULL_SCREEN,
            R.id.sleep_timer to Codes.SLEEP_TIMER,
            R.id.delete to Codes.DELETE,
            R.id.scroll_up to Codes.SCROLL_UP,
            R.id.right_click to Codes.RIGHT_CLICK,
            R.id.scroll_down to Codes.SCROLL_DOWN,
            R.id.app_netflix to Codes.NETFLIX,
            R.id.app_youtube to Codes.YOUTUBE,
            R.id.app_prime to Codes.PRIME_VIDEO,
            R.id.app_hotstar to Codes.JIOHOTSTAR,
            R.id.app_web to Codes.WEB,
        ).forEach { (id, command) -> bindHold(findViewById(id), Codes.ADDRESS, command) }

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
        InstallReceiver.onFailure = { message ->
            updating = false
            installStarted = false
            findViewById<View>(R.id.update_banner).visibility = View.VISIBLE
            findViewById<TextView>(R.id.update_text).text = message
            findViewById<Button>(R.id.update_now).isEnabled = true
        }
    }

    override fun onDestroy() {
        InstallReceiver.onFailure = null
        voice.release()
        super.onDestroy()
    }

    override fun onPause() {
        voice.stop()
        super.onPause()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == VoiceSearch.REQUEST_MIC) voice.onPermissionResult(grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED)
    }

    /** Google's voice screen (the fallback for voice search) answers here. */
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        if (!voice.onActivityResult(requestCode, resultCode, data)) super.onActivityResult(requestCode, resultCode, data)
    }

    override fun onResume() {
        super.onResume()
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
        status.setText(
            when {
                !remote.available -> R.string.no_ir
                !remote.supports38kHz -> R.string.no_38khz
                else -> R.string.ready
            }
        )
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

    /** Sends while the finger is down, like holding a button on a real remote; a drag scrolls the page instead. */
    @SuppressLint("ClickableViewAccessibility")
    private fun bindHold(view: View, address: Int, command: Int) {
        var pressId = 0
        val gesture = PressGesture(
            view,
            onPress = {
                view.isPressed = true
                pressId = remote.press(address, command)
            },
            onRelease = {
                view.isPressed = false
                remote.release(pressId)
            },
        )
        view.setOnTouchListener { v, event ->
            val handled = gesture.onTouch(event)
            if (event.actionMasked == MotionEvent.ACTION_UP) v.performClick()
            handled
        }
    }

    // ---------------------------------------------------------------- your own buttons

    private fun showMyButtons() {
        val container = findViewById<LinearLayout>(R.id.my_buttons)
        container.removeAllViews()
        findViewById<View>(R.id.my_buttons_empty).visibility = if (myButtons.isEmpty()) View.VISIBLE else View.GONE
        val perRow = 3
        val gap = (8 * resources.displayMetrics.density).toInt()
        myButtons.chunked(perRow).forEach { rowButtons ->
            val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
            for (i in 0 until perRow) {
                val button = rowButtons.getOrNull(i)
                val view = Button(this, null, 0, R.style.RemoteKey_Text).apply {
                    text = button?.label ?: ""
                    visibility = if (button == null) View.INVISIBLE else View.VISIBLE
                }
                if (button != null) {
                    if (editing) view.setOnClickListener { editButton(button) }
                    else bindHold(view, button.address, button.command)
                }
                row.addView(view, LinearLayout.LayoutParams(0, (56 * resources.displayMetrics.density).toInt(), 1f).apply {
                    setMargins(if (i == 0) 0 else gap / 2, gap / 2, if (i == perRow - 1) 0 else gap / 2, gap / 2)
                })
            }
            container.addView(row)
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
