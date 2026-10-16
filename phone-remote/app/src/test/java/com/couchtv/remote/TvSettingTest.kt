package com.couchtv.remote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.Base64

class TvSettingTest {
    private fun b64(text: String) = Base64.getEncoder().encodeToString(text.toByteArray(Charsets.UTF_8))

    @Test
    fun readsTheTvsLine() {
        val s = TvSetting.parse("powercountdown 5 ${b64("Power-off countdown")} ${b64("So a tap by mistake can be cancelled")} 0:${b64("Off")},3:${b64("3 s")},5:${b64("5 s")}")!!
        assertEquals("powercountdown", s.key)
        assertEquals("5", s.value)
        assertEquals("Power-off countdown", s.label)
        assertEquals("So a tap by mistake can be cancelled", s.note)
        assertEquals(listOf("0" to "Off", "3" to "3 s", "5" to "5 s"), s.choices)
        assertFalse(s.isSwitch)
    }

    @Test
    fun onAndOffIsASwitch() {
        val s = TvSetting.parse("films on ${b64("Popular films")} - on:${b64("On")},off:${b64("Off")}")!!
        assertNull(s.note)
        assertTrue(s.isSwitch)
    }

    @Test
    fun brokenLinesAreLeftOut() {
        assertNull(TvSetting.parse("films on"))
        assertNull(TvSetting.parse("films on !!! - on:${b64("On")}"))
        assertNull(TvSetting.parse("films on ${b64("Films")} - nothing"))
    }
}
