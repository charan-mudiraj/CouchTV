package com.couchtv.remote

import android.content.Context
import android.os.SystemClock
import java.util.concurrent.Executors
import kotlin.math.abs

/**
 * Sends to CouchTV over Wi-Fi when the TV is connected on this network (no pointing needed), otherwise by infrared.
 * Either way a held button repeats every 108 ms, like a real remote, so CouchTV treats both the same.
 */
class Remote(context: Context) {
    val ir = IrRemote(context)
    val wifi = WifiLink(context)
    val board = BoardLink(context)

    private val worker = Executors.newSingleThreadExecutor()
    @Volatile private var heldPress = 0
    private var lastPress = 0                       // only touched on the main thread
    private val irPresses = HashMap<Int, Int>()     // our press id -> IrRemote's, for release

    /** Presses go over Wi-Fi right now. */
    val viaWifi: Boolean get() = wifi.connected

    /** Called on the main thread when Power can't be sent: no receiver box on the Wi-Fi, and no IR blaster. */
    var onPowerNeedsIr: (() -> Unit)? = null

    /** Starts a press and returns its id for [release]. With [hold] false, it's a single press. */
    fun press(address: Int, command: Int, hold: Boolean = true): Int {
        val id = ++lastPress
        if (address == Codes.ADDRESS && command == Codes.POWER) {
            // Power always goes to the receiver box, never to the PC itself: the box handles it whether the PC is on
            // (it tells CouchTV, which puts it to sleep) or asleep or off (it wakes it and switches the TV on). By the
            // box's own Wi-Fi when it answers, from any phone; else by infrared.
            when {
                board.connected -> board.power { ok -> if (!ok) powerByIr(address, command) }
                else -> powerByIr(address, command)
            }
            return id
        }
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

    private fun powerByIr(address: Int, command: Int) {
        if (ir.available) ir.press(address, command, hold = false) else onPowerNeedsIr?.invoke()
    }

    fun release(id: Int) {
        if (heldPress == id) heldPress = 0
        irPresses.remove(id)?.let { ir.release(it) }
    }

    // ---------------------------------------------------------------- the trackpad

    // By infrared there's no exact movement, so the trackpad holds the pointer arrow it's sliding towards (CouchTV
    // speeds the pointer up while one is held), clicks on lift, and scrolls a notch per 120 wheel units.
    private var irArrow = -1          // the pointer arrow held for the trackpad, or -1
    private var irArrowPress = 0
    private var irWheel = 0

    fun pointerMove(dx: Int, dy: Int) {
        if (wifi.connected) {
            wifi.move(dx, dy)
            return
        }
        val arrow = if (abs(dx) >= abs(dy)) (if (dx > 0) Codes.POINTER_RIGHT else Codes.POINTER_LEFT)
                    else (if (dy > 0) Codes.POINTER_DOWN else Codes.POINTER_UP)
        if (arrow == irArrow) return
        pointerStop()
        irArrow = arrow
        irArrowPress = press(Codes.ADDRESS, arrow)
    }

    /** The fingers lifted from the trackpad. */
    fun pointerStop() {
        if (irArrow < 0) return
        release(irArrowPress)
        irArrow = -1
    }

    fun pointerWheel(dy: Int, dx: Int) {
        if (wifi.connected) {
            wifi.wheel(dy, dx)
            return
        }
        irWheel += dy
        while (abs(irWheel) >= 120) {
            val up = irWheel > 0
            press(Codes.ADDRESS, if (up) Codes.SCROLL_UP else Codes.SCROLL_DOWN, hold = false)
            irWheel -= if (up) 120 else -120
        }
    }

    fun pointerButton(right: Boolean, down: Boolean) {
        if (wifi.connected) wifi.mouse(right, down)
        else if (!down) press(Codes.ADDRESS, if (right) Codes.RIGHT_CLICK else Codes.CLICK, hold = false)
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
