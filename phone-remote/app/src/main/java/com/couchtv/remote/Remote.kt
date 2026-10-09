package com.couchtv.remote

import android.content.Context
import android.os.SystemClock
import java.util.concurrent.Executors

/**
 * Sends to CouchTV over Wi-Fi when the TV is connected on this network (no pointing needed), otherwise by infrared.
 * Either way a held button repeats every 108 ms, like a real remote, so CouchTV treats both the same.
 */
class Remote(context: Context) {
    val ir = IrRemote(context)
    val wifi = WifiLink(context)

    private val worker = Executors.newSingleThreadExecutor()
    @Volatile private var heldPress = 0
    private var lastPress = 0                       // only touched on the main thread
    private val irPresses = HashMap<Int, Int>()     // our press id -> IrRemote's, for release

    /** Presses go over Wi-Fi right now. */
    val viaWifi: Boolean get() = wifi.connected

    /** Starts a press and returns its id for [release]. With [hold] false, it's a single press. */
    fun press(address: Int, command: Int, hold: Boolean = true): Int {
        val id = ++lastPress
        if (wifi.connected) {
            heldPress = if (hold) id else 0
            worker.execute {
                wifi.button(address, command, repeat = false)
                while (heldPress == id) {
                    SystemClock.sleep(Nec.FRAME_MS)
                    if (heldPress == id) wifi.button(address, command, repeat = true)
                }
            }
        } else {
            irPresses[id] = ir.press(address, command, hold)
        }
        return id
    }

    fun release(id: Int) {
        if (heldPress == id) heldPress = 0
        irPresses.remove(id)?.let { ir.release(it) }
    }

    /** Sends words for search: instantly over Wi-Fi, or as infrared frames (about a second). */
    fun sendText(text: String, done: () -> Unit) {
        if (wifi.connected) {
            wifi.text(text)
            done()
        } else {
            ir.sendText(text, done)
        }
    }
}
