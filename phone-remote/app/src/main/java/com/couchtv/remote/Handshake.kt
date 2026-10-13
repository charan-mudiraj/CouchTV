package com.couchtv.remote

import android.content.Context

/** This phone's pairings: its own id, and the key each TV it paired with shares with it ("pairing" settings). */
class Pairings(context: Context) {
    private val prefs = context.applicationContext.getSharedPreferences("pairing", Context.MODE_PRIVATE)

    /** 32 hex digits, made up once: how TVs know this phone. */
    val phoneId: String
        get() = synchronized(Pairings::class.java) {
            prefs.getString("phone", null) ?: Secure.hex(Secure.random(16)).also { prefs.edit().putString("phone", it).apply() }
        }

    fun key(tvId: String): ByteArray? = prefs.getString("tv_$tvId", null)?.let { Secure.fromHex(it) }?.takeIf { it.size == 32 }

    fun save(tvId: String, key: ByteArray) = prefs.edit().putString("tv_$tvId", Secure.hex(key)).apply()

    fun forget(tvId: String) = prefs.edit().remove("tv_$tvId").apply()
}

/**
 * The start of every connection to CouchTV ("Security" in ir-receiver/PROTOCOL.md; its side is
 * RemoteServer.Handshake): fresh ECDH keys, then either pairing, with a code the TV shows, or proving this is a phone
 * it paired with. After [auth], every line is encrypted (Channel).
 */
class Handshake private constructor(
    val tvId: String,
    val tvName: String,
    /** The TV knows this phone (else it asks to pair). */
    val known: Boolean,
    private val mine: ByteArray,
    private val tvPoint: ByteArray,
    private val shared: ByteArray,
    private val transcript: ByteArray,
) {
    enum class PairResult { PAIRED, WRONG_CODE, NOT_SAFE, FAILED }

    companion object {
        const val CODE_BITS = 20

        /** Says hello. Null if what answered isn't a CouchTV that pairs (one from before says nothing back). */
        fun start(io: LineIo, phoneId: String, version: String): Handshake? {
            val key = Ecdh.create()
            val hello = "HELLO2 CouchTV-Remote $version $phoneId ${Secure.base64(key.publicPoint)}"
            io.write(hello)
            val answer = io.read() ?: return null
            // HELLO2 <TV id> <TV key> KNOWN|PAIR <name>
            val p = answer.split(" ")
            if (p.size < 5 || p[0] != "HELLO2") return null
            val tvPoint = Secure.fromBase64(p[2]) ?: return null
            val shared = runCatching { key.agree(tvPoint) }.getOrNull() ?: return null
            return Handshake(p[1], p.drop(4).joinToString(" "), p[3] == "KNOWN", key.publicPoint, tvPoint, shared,
                Secure.sha256(Secure.utf8("$hello\n$answer")))
        }
    }

    /** The key [pair] agreed, once it's [PairResult.PAIRED]. */
    var pairKey: ByteArray? = null
        private set

    /**
     * Checks [code] against the one the TV shows without either side saying it: for each of its 20 bits this phone
     * commits to its bit, the TV to its own, then this phone reveals, then the TV. The TV catches a wrong code; a TV
     * whose reveal doesn't match means someone in between ([PairResult.NOT_SAFE]). Each round's next commitment goes
     * with its reveal, so it's one round trip a bit.
     */
    fun pair(io: LineIo, code: String): PairResult {
        val number = code.toIntOrNull() ?: return PairResult.FAILED
        fun bit(i: Int) = (number shr i) and 1
        var nonce = Secure.random(16)
        io.write("C 0 ${Secure.base64(Secure.commit(nonce, 'P', mine, tvPoint, bit(0)))}")
        for (i in 0 until CODE_BITS) {
            val tvCommit = field(io.read() ?: return PairResult.FAILED, "C", i, 32) ?: return PairResult.FAILED
            io.write("R $i ${Secure.base64(nonce)}")
            val next = if (i + 1 < CODE_BITS) Secure.random(16) else null
            if (next != null) io.write("C ${i + 1} ${Secure.base64(Secure.commit(next, 'P', mine, tvPoint, bit(i + 1)))}")
            val reveal = io.read() ?: return PairResult.FAILED
            if (reveal == "WRONG") return PairResult.WRONG_CODE
            val tvNonce = field(reveal, "R", i, 16) ?: return PairResult.FAILED
            if (!Secure.same(Secure.commit(tvNonce, 'T', tvPoint, mine, bit(i)), tvCommit)) return PairResult.NOT_SAFE
            if (next != null) nonce = next
        }
        pairKey = SessionKeys.pairKey(shared, code, transcript)
        return PairResult.PAIRED
    }

    /** Proves this is the paired phone; the keys once the TV proved it's the paired TV too, else null. */
    fun auth(io: LineIo, pairKey: ByteArray): SessionKeys? {
        val keys = SessionKeys.derive(shared, pairKey, transcript)
        io.write("AUTH ${Secure.base64(keys.phoneProof)}")
        val answer = io.read() ?: return null
        if (!answer.startsWith("AUTH ") || !Secure.same(Secure.fromBase64(answer.substring(5)), keys.tvProof)) return null
        return keys
    }

    /** "C|R <round> <base64>" for this round, with that many bytes; else null. */
    private fun field(line: String, kind: String, round: Int, length: Int): ByteArray? {
        val p = line.split(" ")
        if (p.size != 3 || p[0] != kind || p[1] != round.toString()) return null
        return Secure.fromBase64(p[2])?.takeIf { it.size == length }
    }
}
