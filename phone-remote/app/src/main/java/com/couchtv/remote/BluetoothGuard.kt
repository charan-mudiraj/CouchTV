package com.couchtv.remote

import android.Manifest
import android.annotation.SuppressLint
import android.app.admin.DevicePolicyManager
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.os.Build
import android.widget.Toast

/**
 * Keeps the phone's Bluetooth off while CouchTV Remote is in use (on screen, or playing the TV's sound), and switches
 * it off again if it's turned on meanwhile. The TV's sound is best on wired earphones or the phone's speaker, and
 * Bluetooth shares the phone's 2.4 GHz radio with the Wi-Fi the sound comes over, which costs packets.
 *
 * Android 12 and older let an app switch Bluetooth off (from 12 with the "Nearby devices" permission). From Android 13
 * only the phone's device owner may (the dedicated-remote setup), so elsewhere [askUser] shows Android's own
 * "turn off Bluetooth?" question. Everything here runs on the main thread.
 */
object BluetoothGuard {
    private val holders = HashSet<String>()
    private var app: Context? = null
    private var receiver: BroadcastReceiver? = null
    private var asked = false   // the user has been asked since Bluetooth last came on (so declining doesn't loop)

    /** Off in the app's settings: Bluetooth is left alone. */
    var enabled = true

    /** Asks the user to turn Bluetooth off, when this phone doesn't let the app do it. Set while the app is on screen. */
    var askUser: (() -> Unit)? = null

    /** Starts keeping Bluetooth off, on behalf of [who] (the screen, or the sound); a new look each time. */
    fun hold(context: Context, who: String) {
        if (!enabled) return
        app = context.applicationContext
        if (holders.add(who) && holders.size == 1) watch()
        asked = false
        check()
    }

    fun release(who: String) {
        if (holders.remove(who) && holders.isEmpty()) {
            receiver?.let { r -> runCatching { app?.unregisterReceiver(r) } }
            receiver = null
        }
    }

    /** True if this phone lets the app switch Bluetooth off by itself (it may still need the permission first). */
    fun appCanSwitch(context: Context): Boolean =
        Build.VERSION.SDK_INT < 33 ||
            context.getSystemService(DevicePolicyManager::class.java)?.isDeviceOwnerApp(context.packageName) == true

    fun needsPermission(context: Context): Boolean =
        Build.VERSION.SDK_INT >= 31 && context.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED

    fun isOn(context: Context): Boolean = adapter(context)?.state == BluetoothAdapter.STATE_ON

    /** Switches Bluetooth off if the phone allows the app to; false if the user has to. */
    @SuppressLint("MissingPermission")
    @Suppress("DEPRECATION")
    fun switchOff(context: Context): Boolean {
        val adapter = adapter(context) ?: return true
        if (adapter.state != BluetoothAdapter.STATE_ON) return true
        if (!appCanSwitch(context) || needsPermission(context)) return false
        val done = runCatching { adapter.disable() }.getOrDefault(false)
        if (done) Toast.makeText(context, R.string.bluetooth_switched_off, Toast.LENGTH_SHORT).show()
        return done
    }

    private fun check() {
        val context = app ?: return
        if (holders.isEmpty() || !isOn(context) || switchOff(context) || asked) return
        val ask = askUser ?: return   // in the background: asked when the app is next on screen
        asked = true
        ask()
    }

    private fun watch() {
        val context = app ?: return
        val r = object : BroadcastReceiver() {
            override fun onReceive(c: Context, intent: Intent) {
                when (intent.getIntExtra(BluetoothAdapter.EXTRA_STATE, -1)) {
                    BluetoothAdapter.STATE_ON -> check()
                    BluetoothAdapter.STATE_OFF -> asked = false
                }
            }
        }
        val filter = IntentFilter(BluetoothAdapter.ACTION_STATE_CHANGED)
        // Only Android itself can send this one, so letting it in from outside the app is safe.
        if (Build.VERSION.SDK_INT >= 33) context.registerReceiver(r, filter, Context.RECEIVER_EXPORTED)
        else context.registerReceiver(r, filter)
        receiver = r
    }

    private fun adapter(context: Context): BluetoothAdapter? =
        context.getSystemService(BluetoothManager::class.java)?.adapter
}
