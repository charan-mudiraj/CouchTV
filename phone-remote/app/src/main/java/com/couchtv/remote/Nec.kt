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
    fun frame(address: Int, command: Int): IntArray =
        raw(address and 0xFF, address.inv() and 0xFF, command and 0xFF, command.inv() and 0xFF)

    /** "The button is still held", sent every 108 ms after the first frame. */
    val REPEAT = intArrayOf(9000, 2250, 560, 560)

    /** Any 32-bit frame: the leader, four bytes in the order given, and the stop bit. */
    private fun raw(vararg bytes: Int): IntArray {
        val pattern = ArrayList<Int>(68)
        pattern += 9000          // leader
        pattern += 4500
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

    // ---------------------------------------------------------------- text, for voice search

    /** The longest text sent, in UTF-8 bytes. CouchTV accepts up to 120. */
    const val MAX_TEXT_BYTES = 120
    private const val CHECK = 0xFE   // neither appears in UTF-8
    private const val PAD = 0xFF

    /** Trims [text] to what fits, without cutting a letter in half. */
    fun fitText(text: String): String {
        var result = text.trim()
        while (result.toByteArray(Charsets.UTF_8).size > MAX_TEXT_BYTES) {
            result = result.substring(0, result.offsetByCodePoints(result.length, -1))
        }
        return result.trim()
    }

    /**
     * The frames for a text, sent after the TEXT button (see "Text" in ir-receiver/PROTOCOL.md): two bytes of UTF-8
     * per frame, then the CRC-16 in two check frames. Address byte 1 carries the second byte, packed so it is never
     * 00 or 31, which keeps text from ever looking like a button press. Must match IrText.cs in CouchTV.
     */
    fun textFrames(text: String): List<IntArray> {
        val data = text.toByteArray(Charsets.UTF_8)
        val frames = ArrayList<IntArray>()
        for (i in data.indices step 2) {
            val first = data[i].toInt() and 0xFF
            val second = if (i + 1 < data.size) data[i + 1].toInt() and 0xFF else PAD
            frames += textFrame(first, second)
        }
        val crc = crc16(data)
        frames += textFrame(crc shr 8, CHECK)
        frames += textFrame(crc and 0xFF, CHECK)
        return frames
    }

    private fun textFrame(first: Int, second: Int): IntArray =
        raw(Codes.ADDRESS, pack(second), first, first.inv() and 0xFF)

    /** XOR with C0 moves 00 and 31 to C0 and F1. C0 never appears in UTF-8; F1 is sent as 01 (C1 never appears either). */
    private fun pack(value: Int): Int = if (value == 0xF1) 0x01 else value xor 0xC0

    /** CRC-16/CCITT-FALSE: polynomial 1021, start FFFF. "123456789" gives 29B1. */
    fun crc16(data: ByteArray): Int {
        var crc = 0xFFFF
        for (b in data) {
            crc = crc xor ((b.toInt() and 0xFF) shl 8)
            repeat(8) {
                crc = if ((crc and 0x8000) != 0) ((crc shl 1) xor 0x1021) and 0xFFFF else (crc shl 1) and 0xFFFF
            }
        }
        return crc
    }
}
