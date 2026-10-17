package com.couchtv.remote

import android.content.Context

/** This phone's own settings (the Settings page's "This phone"), kept in the app's storage. */
class PhoneSettings(context: Context) {
    private val prefs = context.getSharedPreferences("phone", Context.MODE_PRIVATE)

    /** The keys click under the finger (a short vibration). */
    var vibration: Boolean
        get() = prefs.getBoolean("vibration", true)
        set(value) = prefs.edit().putBoolean("vibration", value).apply()

    /** Bluetooth is switched off while the app is open (it slows the Wi-Fi the TV's sound comes over). */
    var bluetoothOff: Boolean
        get() = prefs.getBoolean("bluetooth_off", true)
        set(value) = prefs.edit().putBoolean("bluetooth_off", value).apply()

    /** The remote's background: one of Backdrop's looks ("midnight"...), or "photo". */
    var background: String
        get() = prefs.getString("background", "midnight") ?: "midnight"
        set(value) = prefs.edit().putString("background", value).apply()

    /** The screen mirror's picture: "saver", "balanced" or "sharp". */
    var mirror: String
        get() = prefs.getString("mirror", "balanced") ?: "balanced"
        set(value) = prefs.edit().putString("mirror", value).apply()

    /** The most the mirror asks the TV for, in kbps (less while the TV's sound plays here too). */
    val mirrorKbps: Int get() = when (mirror) { "saver" -> 2500; "sharp" -> 16000; else -> 8000 }

    /** The TV's power-off countdown, as it last said (for when only the receiver box is reachable): seconds, 0 for none. */
    var powerCountdown: Int
        get() = prefs.getInt("power_countdown", 5)
        set(value) = prefs.edit().putInt("power_countdown", value).apply()
}
