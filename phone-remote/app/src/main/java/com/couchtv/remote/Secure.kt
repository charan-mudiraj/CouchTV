package com.couchtv.remote

import java.io.BufferedInputStream
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.math.BigInteger
import java.security.AlgorithmParameters
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECParameterSpec
import java.security.spec.ECPrivateKeySpec
import java.security.spec.X509EncodedKeySpec
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.IvParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * The cryptography that keeps the TV to your own phones on a shared Wi-Fi ("Security" in ir-receiver/PROTOCOL.md):
 * the same as CouchTV's Secure.cs. ECDH on the P-256 curve agrees keys, HKDF-SHA256 derives them, and each message is
 * AES-256 in counter mode with a 16-byte HMAC-SHA256 tag. Plain Java (no Android), so SecureTest checks it on a
 * computer against numbers CouchTV worked out (couchtv-vectors.txt).
 */
object Secure {
    const val TAG_BYTES = 16
    private val random = SecureRandom()

    fun random(count: Int) = ByteArray(count).also { random.nextBytes(it) }

    fun utf8(text: String) = text.toByteArray(Charsets.UTF_8)

    fun sha256(vararg parts: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").run {
        parts.forEach { update(it) }
        digest()
    }

    fun hmac(key: ByteArray, vararg parts: ByteArray): ByteArray = Mac.getInstance("HmacSHA256").run {
        init(SecretKeySpec(key, "HmacSHA256"))
        parts.forEach { update(it) }
        doFinal()
    }

    /** HKDF with SHA-256 (RFC 5869). */
    fun hkdf(secret: ByteArray, salt: ByteArray, info: ByteArray, length: Int): ByteArray {
        val prk = hmac(if (salt.isEmpty()) ByteArray(32) else salt, secret)
        val okm = ByteArray(length)
        var block = ByteArray(0)
        var done = 0
        var i = 1
        while (done < length) {
            block = hmac(prk, block, info, byteArrayOf(i++.toByte()))
            val n = minOf(block.size, length - done)
            System.arraycopy(block, 0, okm, done, n)
            done += n
        }
        return okm
    }

    /** Equal, taking the same time wherever they differ. */
    fun same(a: ByteArray?, b: ByteArray?) = a != null && b != null && MessageDigest.isEqual(a, b)

    fun hex(bytes: ByteArray): String {
        val digits = "0123456789abcdef"
        val text = StringBuilder(bytes.size * 2)
        for (b in bytes) text.append(digits[(b.toInt() shr 4) and 15]).append(digits[b.toInt() and 15])
        return text.toString()
    }

    /** Null unless it's an even number of hex digits. */
    fun fromHex(text: String): ByteArray? {
        if (text.length % 2 != 0) return null
        val bytes = ByteArray(text.length / 2)
        for (i in bytes.indices) {
            val hi = Character.digit(text[2 * i], 16)
            val lo = Character.digit(text[2 * i + 1], 16)
            if (hi < 0 || lo < 0) return null
            bytes[i] = (hi * 16 + lo).toByte()
        }
        return bytes
    }

    fun base64(bytes: ByteArray): String = Base64.getEncoder().encodeToString(bytes)

    fun fromBase64(text: String): ByteArray? = try {
        Base64.getDecoder().decode(text)
    } catch (e: IllegalArgumentException) {
        null
    }

    /** The first 16 bytes of an HMAC over a line of text, as 32 hex digits (sound requests, the receiver box). */
    fun tag(key: ByteArray, message: String) = hex(hmac(key, utf8(message)).copyOf(TAG_BYTES))

    /**
     * A pairing commitment: binds one bit of the code to both sides' keys without revealing it until the nonce is
     * shown. [role] is 'P' for the phone's, 'T' for the TV's; [first] is the committer's own key.
     */
    fun commit(nonce: ByteArray, role: Char, first: ByteArray, second: ByteArray, bit: Int) =
        hmac(nonce, byteArrayOf(role.code.toByte()), first, second, byteArrayOf(bit.toByte()))
}

/**
 * AES-256 in counter mode. The IV is an 8-byte nonce (big-endian) then 8 zero bytes, and Java counts the blocks up
 * from there: the same blocks CouchTV makes.
 */
class Ctr(key: ByteArray) {
    private val key = SecretKeySpec(key, "AES")
    private val cipher = Cipher.getInstance("AES/CTR/NoPadding")

    /** Encrypts or decrypts (the same thing) [count] bytes in place. */
    fun apply(nonce: Long, data: ByteArray, offset: Int, count: Int) {
        val iv = ByteArray(16)
        for (i in 0 until 8) iv[i] = (nonce ushr (56 - 8 * i)).toByte()
        cipher.init(Cipher.ENCRYPT_MODE, key, IvParameterSpec(iv))
        cipher.doFinal(data, offset, count, data, offset)
    }
}

/**
 * An ECDH key on the P-256 curve, new for each connection. Public keys travel as the 65-byte uncompressed point
 * (04, x, y); the agreed secret is SHA-256 of the shared x coordinate, as Windows' ECDH gives CouchTV.
 */
class Ecdh private constructor(private val privateKey: PrivateKey, val publicPoint: ByteArray) {
    companion object {
        // What comes before the point in the standard encoding of a P-256 public key (SubjectPublicKeyInfo).
        private val P256_PREFIX = Secure.fromHex("3059301306072a8648ce3d020106082a8648ce3d030107034200")!!

        fun create(): Ecdh {
            val pair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
            val encoded = pair.public.encoded
            return Ecdh(pair.private, encoded.copyOfRange(encoded.size - 65, encoded.size))
        }

        /** A fixed key, for the unit test's known answers. */
        fun fixed(d: ByteArray, point: ByteArray): Ecdh {
            val params = AlgorithmParameters.getInstance("EC").apply { init(ECGenParameterSpec("secp256r1")) }
                .getParameterSpec(ECParameterSpec::class.java)
            return Ecdh(KeyFactory.getInstance("EC").generatePrivate(ECPrivateKeySpec(BigInteger(1, d), params)), point)
        }
    }

    /** SHA-256 of the shared secret. Throws if the other side's key isn't a point on the curve. */
    fun agree(point: ByteArray): ByteArray {
        require(point.size == 65 && point[0] == 4.toByte()) { "not a P-256 public key" }
        val other = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(P256_PREFIX + point))
        val agreement = KeyAgreement.getInstance("ECDH")
        agreement.init(privateKey)
        agreement.doPhase(other, true)
        return Secure.sha256(agreement.generateSecret())
    }
}

/**
 * The keys for one connection, from the ECDH secret, the pairing key and everything both sides said first. Each
 * direction has its own, and so does the TV's sound.
 */
class SessionKeys(
    val phoneProof: ByteArray, val tvProof: ByteArray,
    val phoneEnc: ByteArray, val phoneMac: ByteArray, val tvEnc: ByteArray, val tvMac: ByteArray,
    val audioEnc: ByteArray, val audioMac: ByteArray,
) {
    /** The screen mirror's pictures (TV to phone, on a connection of their own). */
    val mirrorEnc: ByteArray get() = Secure.hmac(tvEnc, Secure.utf8("mirror enc"))
    val mirrorMac: ByteArray get() = Secure.hmac(tvMac, Secure.utf8("mirror mac"))

    companion object {
        fun derive(shared: ByteArray, pairKey: ByteArray, transcript: ByteArray): SessionKeys {
            val okm = Secure.hkdf(shared, pairKey, Secure.utf8("CouchTV session 1") + transcript, 7 * 32)
            fun part(i: Int) = okm.copyOfRange(i * 32, i * 32 + 32)
            val confirm = part(0)
            return SessionKeys(
                Secure.hmac(confirm, Secure.utf8("phone")), Secure.hmac(confirm, Secure.utf8("tv")),
                part(1), part(2), part(3), part(4), part(5), part(6),
            )
        }

        /** The key a pairing leaves both sides with, once the code checked out. */
        fun pairKey(shared: ByteArray, code: String, transcript: ByteArray) =
            Secure.hkdf(shared, Secure.utf8(code), Secure.utf8("CouchTV pair 1") + transcript, 32)
    }
}

/**
 * Lines of text, encrypted: each is base64 of the AES-CTR ciphertext and a 16-byte HMAC tag over the message number
 * and ciphertext. Message numbers count from 0 in each direction and are never sent, so a line that's replayed,
 * dropped, reordered or changed fails its tag. One thread may seal while another opens.
 */
class Channel(sendEnc: ByteArray, sendMac: ByteArray, receiveEnc: ByteArray, receiveMac: ByteArray) {
    private val sendCipher = Ctr(sendEnc)
    private val receiveCipher = Ctr(receiveEnc)
    private val sendMac = SecretKeySpec(sendMac, "HmacSHA256")
    private val receiveMac = SecretKeySpec(receiveMac, "HmacSHA256")
    private val sendLock = Any()
    private val receiveLock = Any()
    private var sent = 0L
    private var received = 0L

    fun seal(line: String): String = synchronized(sendLock) {
        val text = Secure.utf8(line)
        val sealed = text.copyOf(text.size + Secure.TAG_BYTES)
        sendCipher.apply(sent, sealed, 0, text.size)
        tag(sendMac, sent, sealed, text.size).copyInto(sealed, text.size)
        sent++
        Secure.base64(sealed)
    }

    /** The line, or null if it wasn't sealed by the other side in this order. */
    fun open(line: String): String? = synchronized(receiveLock) {
        val sealed = Secure.fromBase64(line) ?: return null
        if (sealed.size < Secure.TAG_BYTES) return null
        val length = sealed.size - Secure.TAG_BYTES
        if (!Secure.same(tag(receiveMac, received, sealed, length), sealed.copyOfRange(length, sealed.size))) return null
        receiveCipher.apply(received, sealed, 0, length)
        received++
        String(sealed, 0, length, Charsets.UTF_8)
    }

    private fun tag(key: SecretKeySpec, number: Long, data: ByteArray, length: Int): ByteArray {
        val counter = ByteArray(8)
        for (i in 0 until 8) counter[i] = (number ushr (56 - 8 * i)).toByte()
        return Mac.getInstance("HmacSHA256").run {
            init(key)
            update(counter)
            update(data, 0, length)
            doFinal()
        }.copyOf(Secure.TAG_BYTES)
    }
}

/**
 * The TV's sound for one connection: its session id, and its keys. Asks for sound with tagged requests, and opens
 * its packets ("CTA3": a 16-byte header, the encrypted samples, a 16-byte tag over both).
 */
class SoundKeys(val session: String, enc: ByteArray, private val mac: ByteArray) {
    private val cipher = Ctr(enc)
    private val macKey = SecretKeySpec(mac, "HmacSHA256")

    /** "LISTEN 3 ..." or "STOP 3 ...": [number] must go up each time. */
    fun request(verb: String, number: Long) =
        "$verb 3 $session $number ${Secure.tag(mac, "$verb $session $number")}"

    /** The packet without its tag, decrypted in place, or null if it isn't one of this connection's. */
    fun open(packet: ByteArray, length: Int): ByteArray? {
        val body = length - Secure.TAG_BYTES
        if (body <= 16) return null
        val tag = Mac.getInstance("HmacSHA256").run {
            init(macKey)
            update(packet, 0, body)
            doFinal()
        }.copyOf(Secure.TAG_BYTES)
        if (!Secure.same(tag, packet.copyOfRange(body, length))) return null
        val sequence = (packet[4].toLong() and 0xFF) or ((packet[5].toLong() and 0xFF) shl 8) or
            ((packet[6].toLong() and 0xFF) shl 16) or ((packet[7].toLong() and 0xFF) shl 24)
        cipher.apply(sequence, packet, 16, body - 16)
        return packet.copyOf(body)
    }
}

/**
 * The TV's binary messages (the screen mirror's pictures): each is its length (4 bytes, big-endian), the AES-CTR
 * ciphertext and a 16-byte HMAC tag over the message number and ciphertext, as CouchTV's FrameWriter seals them.
 */
class FrameReader(private val input: InputStream, enc: ByteArray, mac: ByteArray) {
    private val cipher = Ctr(enc)
    private val macKey = SecretKeySpec(mac, "HmacSHA256")
    private var number = 0L

    /** The next message; null when the connection closed. Throws for one that wasn't sealed by the TV in this order. */
    fun next(): ByteArray? {
        val head = exactly(4) ?: return null
        val length = ((head[0].toInt() and 0xFF) shl 24) or ((head[1].toInt() and 0xFF) shl 16) or
            ((head[2].toInt() and 0xFF) shl 8) or (head[3].toInt() and 0xFF)
        if (length < Secure.TAG_BYTES || length > (4 shl 20)) throw IOException("not a message from the TV")
        val body = exactly(length) ?: return null
        val size = length - Secure.TAG_BYTES
        val counter = ByteArray(8) { (number ushr (56 - 8 * it)).toByte() }
        val tag = Mac.getInstance("HmacSHA256").run {
            init(macKey)
            update(counter)
            update(body, 0, size)
            doFinal()
        }.copyOf(Secure.TAG_BYTES)
        if (!Secure.same(tag, body.copyOfRange(size, length))) throw IOException("a message that didn't come from the TV")
        cipher.apply(number, body, 0, size)
        number++
        return body.copyOf(size)
    }

    private fun exactly(count: Int): ByteArray? {
        val bytes = ByteArray(count)
        var done = 0
        while (done < count) {
            val n = input.read(bytes, done, count - done)
            if (n < 0) return null
            done += n
        }
        return bytes
    }
}

/** Lines of UTF-8 over a connection, refusing any too long (so a stranger posing as the TV can't fill memory). */
class LineIo(input: InputStream, private val output: OutputStream) {
    private val input = BufferedInputStream(input)

    /** The connection's incoming bytes, for binary messages after the lines (anything read ahead stays in order). */
    val stream: InputStream get() = input

    /** The next line, or null when the connection closed. */
    fun read(): String? {
        val line = ByteArrayOutputStream()
        while (true) {
            val b = input.read()
            if (b < 0) return null
            if (b == '\n'.code) return String(line.toByteArray(), Charsets.UTF_8).trimEnd('\r')
            if (line.size() >= 65536) throw IOException("line too long")
            line.write(b)
        }
    }

    fun write(line: String) = synchronized(this) {
        output.write(Secure.utf8(line + "\n"))
        output.flush()
    }
}
