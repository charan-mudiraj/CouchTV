package com.couchtv.remote

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Secure.kt against the numbers CouchTV's Secure.cs worked out from the same inputs (couchtv-vectors.txt, made by
 * CouchTV.exe --securityvectors): if any differ, the phone and the TV couldn't understand each other.
 */
class SecureTest {
    private val v: Map<String, String> = javaClass.classLoader!!.getResourceAsStream("couchtv-vectors.txt")!!
        .bufferedReader(Charsets.UTF_8).readLines()
        .filter { " = " in it && !it.startsWith("#") }
        .associate { it.substringBefore(" = ") to it.substringAfter(" = ") }

    private fun bytes(name: String) = Secure.fromHex(v.getValue(name))!!
    private fun keys() = SessionKeys.derive(bytes("shared"), bytes("pair_key"), bytes("transcript"))

    @Test
    fun ecdhAgreesWithCouchTv() {
        val phone = Ecdh.fixed(bytes("phone_private"), bytes("phone_point"))
        assertEquals(v["shared"], Secure.hex(phone.agree(bytes("tv_point"))))
    }

    @Test
    fun freshKeysAgree() {
        val a = Ecdh.create()
        val b = Ecdh.create()
        assertEquals(65, a.publicPoint.size)
        assertEquals(4.toByte(), a.publicPoint[0])
        assertArrayEquals(a.agree(b.publicPoint), b.agree(a.publicPoint))
    }

    @Test
    fun pairingMatchesCouchTv() {
        val transcript = Secure.sha256(Secure.utf8(v.getValue("phone_hello") + "\n" + v.getValue("tv_hello")))
        assertEquals(v["transcript"], Secure.hex(transcript))
        assertEquals(v["pair_key"], Secure.hex(SessionKeys.pairKey(bytes("shared"), v.getValue("code"), transcript)))
        assertEquals(v["commit_phone_bit1"], Secure.hex(Secure.commit(bytes("commit_nonce"), 'P', bytes("phone_point"), bytes("tv_point"), 1)))
        assertEquals(v["commit_tv_bit0"], Secure.hex(Secure.commit(bytes("commit_nonce"), 'T', bytes("tv_point"), bytes("phone_point"), 0)))
    }

    @Test
    fun sessionKeysMatchCouchTv() {
        val keys = keys()
        assertEquals(v["phone_proof"], Secure.hex(keys.phoneProof))
        assertEquals(v["tv_proof"], Secure.hex(keys.tvProof))
        assertEquals(v["phone_enc"], Secure.hex(keys.phoneEnc))
        assertEquals(v["phone_mac"], Secure.hex(keys.phoneMac))
        assertEquals(v["tv_enc"], Secure.hex(keys.tvEnc))
        assertEquals(v["tv_mac"], Secure.hex(keys.tvMac))
        assertEquals(v["audio_enc"], Secure.hex(keys.audioEnc))
        assertEquals(v["audio_mac"], Secure.hex(keys.audioMac))
    }

    @Test
    fun linesMatchCouchTv() {
        val keys = keys()
        val phone = Channel(keys.phoneEnc, keys.phoneMac, keys.tvEnc, keys.tvMac)
        assertEquals(v["phone_line0"], phone.seal("HELLO CouchTV-Remote 1.99"))
        assertEquals(v["phone_line1"], phone.seal("TEXT " + Secure.base64(Secure.utf8("पंचायत"))))
        assertEquals("WELCOME TESTTV", phone.open(v.getValue("tv_line0")))
        assertNull("a replayed line doesn't open", phone.open(v.getValue("tv_line0")))
        val bent = Secure.fromBase64(v.getValue("tv_line1"))!!
        bent[0] = (bent[0] + 1).toByte()
        assertNull("a changed line doesn't open", phone.open(Secure.base64(bent)))
        assertEquals("AUDIO 0123456789abcdef", phone.open(v.getValue("tv_line1")))
    }

    @Test
    fun soundMatchesCouchTv() {
        val keys = keys()
        val sound = SoundKeys("0123456789abcdef", keys.audioEnc, keys.audioMac)
        assertEquals("LISTEN 3 0123456789abcdef 7 " + v["listen_tag"], sound.request("LISTEN", 7))
        val packet = bytes("audio_packet")
        val body = sound.open(packet.copyOf(), packet.size)!!
        assertEquals(16 + 1440, body.size)
        for (i in 0 until 1440) assertEquals(i.toByte(), body[16 + i])
        val bent = packet.copyOf()
        bent[100] = (bent[100] + 1).toByte()
        assertNull("a changed packet is dropped", sound.open(bent, bent.size))
    }

    @Test
    fun boardTagsMatchCouchTv() {
        val key = bytes("board_key")
        val c = v.getValue("board_challenge")
        val next = v.getValue("board_next")
        assertEquals(v["board_state_tag"], Secure.tag(key, "STATE $c"))
        assertEquals(v["board_power_tag"], Secure.tag(key, "POWER 42 $c"))
        assertEquals(v["board_reply_tag"], Secure.tag(key, "R $c STATE PC ON TV OFF SENSE ON $next"))
    }
}
