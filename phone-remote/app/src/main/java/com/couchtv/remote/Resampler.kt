package com.couchtv.remote

import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.roundToInt
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * Plays 16-bit stereo sound a hair faster or slower, so the phone keeps pace with the TV's clock without a sound you
 * can hear. Each output sample is read from between the input samples with a windowed-sinc filter (16 taps, flat to
 * 18 kHz), which keeps the treble intact where straight-line interpolation dulls and roughens it, and the reading position carries on from one
 * chunk to the next, so there's no seam every few milliseconds. At normal speed on a whole sample it's exact.
 */
class Resampler {
    companion object {
        private const val HALF = 8                 // taps on each side of the position
        private const val TAPS = 2 * HALF
        private const val PHASES = 256             // filters for positions between two samples; blended in between

        /** Row p is the filter for a position p / PHASES of the way from one sample to the next. */
        private val filters = FloatArray((PHASES + 1) * TAPS).also { table ->
            for (p in 0..PHASES) {
                val f = p.toDouble() / PHASES
                val row = DoubleArray(TAPS) { k ->
                    val x = k - (HALF - 1) - f
                    val sinc = if (abs(x) < 1e-9) 1.0 else sin(PI * x) / (PI * x)
                    sinc * kaiser(x / HALF)
                }
                val sum = row.sum()   // so every position passes steady sound at the same level
                for (k in 0 until TAPS) table[p * TAPS + k] = (row[k] / sum).toFloat()
            }
        }

        private fun kaiser(x: Double): Double {
            if (abs(x) >= 1.0) return 0.0
            val beta = 5.0
            return besselI0(beta * sqrt(1 - x * x)) / besselI0(beta)
        }

        private fun besselI0(x: Double): Double {
            var sum = 1.0
            var term = 1.0
            var k = 1
            while (term > 1e-12 * sum) {
                term *= (x / (2 * k)) * (x / (2 * k))
                sum += term
                k++
            }
            return sum
        }
    }

    private var buffer = FloatArray(1024 * 2)   // input frames not yet passed, stereo interleaved
    private var count = HALF - 1                // frames in it: starts with a little silence before the first sound
    private var position = (HALF - 1).toDouble()   // where the next output frame is read, in input frames

    /**
     * [input] (16-bit stereo) read at [step] input frames per output frame: above 1 plays faster (fewer frames out),
     * below 1 slower. Output trails input by 8 frames (a sixth of a millisecond).
     */
    fun process(input: ByteArray, step: Double): ByteArray {
        val frames = input.size / 4
        if (buffer.size < (count + frames) * 2) buffer = buffer.copyOf((count + frames) * 4)
        for (i in 0 until frames) {
            buffer[(count + i) * 2] = sample(input, i * 4).toFloat()
            buffer[(count + i) * 2 + 1] = sample(input, i * 4 + 2).toFloat()
        }
        count += frames

        val out = ByteArray(((frames + 2) / step).toInt() * 4 + 8)
        val samples = buffer
        val table = filters
        var n = 0
        var pos = position
        while (n * 4 < out.size) {
            val i = pos.toInt()
            if (i + HALF >= count) break   // the filter reaches HALF frames ahead
            val p = (pos - i) * PHASES
            val p0 = p.toInt()
            val blend = (p - p0).toFloat()
            val row = p0 * TAPS
            var left = 0f
            var right = 0f
            var at = (i - HALF + 1) * 2
            for (k in 0 until TAPS) {
                val a = table[row + k]
                val c = a + (table[row + TAPS + k] - a) * blend
                left += samples[at] * c
                right += samples[at + 1] * c
                at += 2
            }
            put(out, n * 4, left)
            put(out, n * 4 + 2, right)
            n++
            pos += step
        }
        // Drop the frames the filter no longer reaches.
        val done = pos.toInt() - (HALF - 1)
        if (done > 0) {
            System.arraycopy(buffer, done * 2, buffer, 0, (count - done) * 2)
            count -= done
            pos -= done
        }
        position = pos
        return if (n * 4 == out.size) out else out.copyOf(n * 4)
    }

    private fun put(out: ByteArray, at: Int, value: Float) {
        val s = value.roundToInt().coerceIn(-32768, 32767)
        out[at] = s.toByte()
        out[at + 1] = (s shr 8).toByte()
    }
}

/** A 16-bit little-endian sample. */
internal fun sample(b: ByteArray, at: Int): Int = (b[at].toInt() and 0xFF) or (b[at + 1].toInt() shl 8)
