package com.couchtv.remote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.Base64

/** The lines CouchTV sends (HomeWindow.BluetoothPhone.cs, BluetoothLines), read as the TV means them. */
class TvBluetoothTest {
    private fun b64(text: String) = Base64.getEncoder().encodeToString(text.toByteArray(Charsets.UTF_8))

    @Test
    fun readsHowBluetoothIs() {
        val b = TvBluetooth.header("on ${b64("Intel Wireless Bluetooth")} 1", TvBluetooth())
        assertTrue(b.isOn)
        assertEquals("Intel Wireless Bluetooth", b.radio)
        assertTrue(b.searching)
        val off = TvBluetooth.header("none - 0", b)
        assertEquals("none", off.power)
        assertNull(off.radio)
        assertFalse(off.searching)
    }

    @Test
    fun readsAPairedDevice() {
        val d = TvBluetooth.device("001A7DDA7113 ${b64("WH-1000XM4")} headphones ACPTU ${b64("Connected · playing with the TV speakers")}")!!
        assertEquals("001A7DDA7113", d.address)
        assertEquals("WH-1000XM4", d.name)
        assertEquals("headphones", d.kind)
        assertTrue(d.audio && d.connected && d.playing && d.together && d.auto)
        assertEquals("Connected · playing with the TV speakers", d.note)
        val pad = TvBluetooth.device("A0B1C2D3E4F5 ${b64("Xbox Wireless Controller")} gamepad - -")!!
        assertFalse(pad.audio || pad.connected || pad.playing || pad.together || pad.auto)
        assertEquals("", pad.note)
        assertNull(TvBluetooth.device("A0B1C2D3E4F5 broken"))
    }

    @Test
    fun readsAFoundDeviceAndWhatTheTvSays() {
        val f = TvBluetooth.found("0C1D2E3F4A5B ${b64("JBL Flip 5")} speaker")!!
        assertEquals("JBL Flip 5", f.name)
        assertEquals("speaker", f.kind)
        assertEquals("good" to "Paired JBL Flip 5", TvBluetooth.says("good ${b64("Paired JBL Flip 5")}"))
        assertNull(TvBluetooth.says("- -"))
    }
}
