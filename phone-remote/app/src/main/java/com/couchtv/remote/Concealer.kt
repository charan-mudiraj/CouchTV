package com.couchtv.remote

import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.roundToInt
import kotlin.math.sqrt

/**
 * Fills in for sound lost on the Wi-Fi when its repair copy was lost too. Repeating the last chunk would click at
 * both ends; instead this carries the waveform on: it finds the stretch of recent sound that best matches how the
 * sound was going (often a whole number of cycles of the note or voice), repeats that, and blends back into the real
 * sound when it returns. Phone calls hide lost packets the same way (G.711 Appendix I).
 */
class Concealer {
    companion object {
        private const val HISTORY = 1024   // frames of recent sound kept
        private const val MATCH = 192      // frames compared when looking for the best repeat (4 ms)
        private const val SHORTEST = 192   // repeat lengths tried: 4 ms (no shorter, or bass turns into a buzz)…
        private const val LONGEST = 720    // …to 15 ms
        private const val BLEND = 96       // 2 ms to blend back into the real sound
    }

    private val history = FloatArray(HISTORY * 2)   // newest last, stereo interleaved
    private var cycle: FloatArray? = null           // what's repeated while sound is missing
    private var phase = 0                           // the next frame of it

    /** The chunk to play: [samples] (16-bit stereo) as they are, or [frames] made up when they were lost (null). */
    fun next(samples: ByteArray?, frames: Int): ByteArray {
        val out: ByteArray
        if (samples == null) {
            val c = cycle ?: bestCycle().also { cycle = it; phase = 0 }
            val length = c.size / 2
            out = ByteArray(frames * 4)
            for (i in 0 until frames) {
                put(out, i * 4, c[phase * 2])
                put(out, i * 4 + 2, c[phase * 2 + 1])
                phase = (phase + 1) % length
            }
        } else {
            val c = cycle
            if (c != null) {
                // Back to real sound: blend in from where the made-up sound had got to, so there's no jump.
                out = samples.copyOf()
                val length = c.size / 2
                val n = minOf(BLEND, samples.size / 4)
                for (i in 0 until n) {
                    val w = (0.5 - 0.5 * cos(PI * (i + 0.5) / n)).toFloat()
                    put(out, i * 4, sample(samples, i * 4) * w + c[phase * 2] * (1 - w))
                    put(out, i * 4 + 2, sample(samples, i * 4 + 2) * w + c[phase * 2 + 1] * (1 - w))
                    phase = (phase + 1) % length
                }
                cycle = null
            } else out = samples
        }
        remember(out)
        return out
    }

    /** The last few milliseconds, as long as the stretch that best matches the sound just before them. */
    private fun bestCycle(): FloatArray {
        val mono = FloatArray(HISTORY) { history[it * 2] + history[it * 2 + 1] }
        var target = 0.0
        for (n in HISTORY - MATCH until HISTORY) target += mono[n].toDouble() * mono[n]
        var best = SHORTEST
        var bestScore = -2.0
        for (lag in SHORTEST..LONGEST) {
            var dot = 0.0
            var energy = 0.0
            for (n in HISTORY - MATCH until HISTORY) {
                val earlier = mono[n - lag].toDouble()
                dot += mono[n] * earlier
                energy += earlier * earlier
            }
            val score = if (energy > 0 && target > 0) dot / sqrt(energy * target) else 0.0
            if (score > bestScore) {
                bestScore = score
                best = lag
            }
        }
        return history.copyOfRange((HISTORY - best) * 2, HISTORY * 2)
    }

    private fun remember(chunk: ByteArray) {
        val frames = minOf(chunk.size / 4, HISTORY)
        System.arraycopy(history, frames * 2, history, 0, (HISTORY - frames) * 2)
        val first = chunk.size / 4 - frames
        for (i in 0 until frames) {
            history[(HISTORY - frames + i) * 2] = sample(chunk, (first + i) * 4).toFloat()
            history[(HISTORY - frames + i) * 2 + 1] = sample(chunk, (first + i) * 4 + 2).toFloat()
        }
    }

    private fun put(out: ByteArray, at: Int, value: Float) {
        val s = value.roundToInt().coerceIn(-32768, 32767)
        out[at] = s.toByte()
        out[at + 1] = (s shr 8).toByte()
    }
}
