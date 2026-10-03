package com.couchtv.remote

import android.content.Context
import android.hardware.ConsumerIrManager
import android.os.SystemClock
import android.util.Log
import java.util.concurrent.Executors

/**
 * Sends buttons through the phone's IR blaster. A press sends one frame, then "still held" frames every 108 ms
 * until it is released, like a real remote, so CouchTV can repeat arrows and volume and treat a held Back as Home.
 * transmit() blocks, so everything runs on one background thread, in order.
 */
class IrRemote(context: Context) {
    private val ir = context.getSystemService(Context.CONSUMER_IR_SERVICE) as? ConsumerIrManager
    private val worker = Executors.newSingleThreadExecutor()

    @Volatile private var heldPress = 0
    private var lastPress = 0    // only touched on the main thread

    /** The phone has an IR blaster. */
    val available: Boolean get() = ir?.hasIrEmitter() == true

    /** The blaster can send at 38 kHz (true when the phone doesn't say). */
    val supports38kHz: Boolean
        get() = ir?.carrierFrequencies?.any { Nec.CARRIER_HZ in it.minFrequency..it.maxFrequency } ?: true

    /** Starts a press and returns its id for [release]. With [hold] false, only one frame is sent. */
    fun press(address: Int, command: Int, hold: Boolean = true): Int {
        val id = ++lastPress
        heldPress = if (hold) id else 0
        worker.execute {
            send(Nec.frame(address, command))
            while (heldPress == id) send(Nec.REPEAT)
        }
        return id
    }

    fun release(id: Int) {
        if (heldPress == id) heldPress = 0
    }

    /** Sends a pattern and waits out the rest of the 108 ms frame, which is also the gap between frames. */
    private fun send(pattern: IntArray) {
        val started = SystemClock.uptimeMillis()
        try {
            ir?.transmit(Nec.CARRIER_HZ, pattern)
        } catch (e: Exception) {
            Log.w("CouchTV", "IR transmit failed", e)
        }
        val rest = Nec.FRAME_MS - (SystemClock.uptimeMillis() - started)
        if (rest > 0) SystemClock.sleep(rest)
    }
}
