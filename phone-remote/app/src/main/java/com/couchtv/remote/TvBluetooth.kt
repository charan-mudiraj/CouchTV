package com.couchtv.remote

import java.util.Base64

/**
 * The TV's Bluetooth, as it last said while this phone's Bluetooth page was open ("BT" in ir-receiver/PROTOCOL.md):
 * switched on or off (or no adapter at all), the adapter's name, whether it's searching for this phone, the paired
 * devices and what a search has found.
 */
data class TvBluetooth(
    val power: String = "none",           // "on", "off", or "none" (no adapter, or it's unplugged)
    val radio: String? = null,
    val searching: Boolean = false,
    val devices: List<Device> = emptyList(),
    val found: List<Found> = emptyList(),
) {
    /** A paired device. [kind]: headphones, speaker, audio, phone, computer, keyboard, mouse, gamepad or other. */
    data class Device(
        val address: String, val name: String, val kind: String, val audio: Boolean, val connected: Boolean,
        val playing: Boolean, val together: Boolean, val auto: Boolean, val note: String,
    )

    /** A device in pairing mode nearby, not paired yet. */
    data class Found(val address: String, val name: String, val kind: String)

    val isOn: Boolean get() = power == "on"

    companion object {
        /** "on|off|none <adapter> <searching 0|1>" (the adapter in base64, "-" for none), over [old]'s lists. */
        fun header(line: String, old: TvBluetooth): TvBluetooth {
            val p = line.trim().split(" ")
            return old.copy(
                power = p.getOrNull(0)?.takeIf { it == "on" || it == "off" } ?: "none",
                radio = p.getOrNull(1)?.let { text(it) }?.takeIf { it.isNotEmpty() },
                searching = p.getOrNull(2) == "1",
            )
        }

        /** "<address> <name> <kind> <flags> <note>": flags are letters (A audio, C connected, P playing, T together, U automatic) or "-". */
        fun device(line: String): Device? {
            val p = line.trim().split(" ")
            if (p.size < 5 || p[0].isEmpty()) return null
            val flags = p[3]
            return Device(p[0], text(p[1]) ?: "", p[2], 'A' in flags, 'C' in flags, 'P' in flags, 'T' in flags, 'U' in flags, text(p[4]) ?: "")
        }

        /** "<address> <name> <kind>". */
        fun found(line: String): Found? {
            val p = line.trim().split(" ")
            if (p.size < 3 || p[0].isEmpty()) return null
            return Found(p[0], text(p[1]) ?: p[0], p[2])
        }

        /** "<good|warn|muted> <words>" (base64), or "- -" for nothing to say: the kind and the words. */
        fun says(line: String): Pair<String, String>? {
            val p = line.trim().split(" ")
            if (p[0] == "-" || p.size < 2) return null
            return p[0] to (text(p[1]) ?: return null)
        }

        private fun text(base64: String): String? =
            if (base64 == "-") "" else runCatching { String(Base64.getDecoder().decode(base64), Charsets.UTF_8) }.getOrNull()
    }
}
