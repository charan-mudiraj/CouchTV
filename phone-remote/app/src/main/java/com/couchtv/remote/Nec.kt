package com.couchtv.remote

/**
 * NEC infrared frames, the format CouchTV's receiver expects (see ir-receiver/PROTOCOL.md in the CouchTV repo).
 * Patterns are alternating on/off durations in microseconds, as ConsumerIrManager.transmit() wants them.
 */
object Nec {
    const val CARRIER_HZ = 38_000

    /** One frame lasts 108 ms. IrRemote waits out the rest of the 108 ms after each transmit. */
    const val FRAME_MS = 108L

    /** A button press: address, ~address, command, ~command, each sent least significant bit first. */
    fun frame(address: Int, command: Int): IntArray {
        val pattern = ArrayList<Int>(68)
        pattern += 9000          // leader
        pattern += 4500
        val bytes = intArrayOf(address and 0xFF, address.inv() and 0xFF, command and 0xFF, command.inv() and 0xFF)
        for (byte in bytes) {
            for (bit in 0 until 8) {
                pattern += 560
                pattern += if ((byte shr bit) and 1 == 1) 1690 else 560
            }
        }
        pattern += 560           // stop bit
        pattern += 560           // end on a gap: some IR drivers want an even-length pattern
        return pattern.toIntArray()
    }

    /** "The button is still held", sent every 108 ms after the first frame. */
    val REPEAT = intArrayOf(9000, 2250, 560, 560)
}
