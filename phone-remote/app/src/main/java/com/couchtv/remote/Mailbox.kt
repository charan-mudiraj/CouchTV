package com.couchtv.remote

import org.json.JSONObject
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL

/**
 * The mailbox the phones leave watchlist changes in for each other: a topic on ntfy.sh, a free, open-source message
 * relay that needs no account and keeps what's left in a topic for about 12 hours. The topic's name and every message
 * are made unreadable to anyone but the TV's paired phones by MailboxLock; ntfy.sh only ever sees scrambled text.
 * Blocking calls, for a background thread; they throw IOException when the internet's away.
 */
object Mailbox {
    private const val SERVER = "https://ntfy.sh/"

    /** A message: when ntfy.sh got it (seconds since 1970) and what it says. */
    class Message(val time: Long, val body: String)

    /** Leaves [body] in [topic]; false if ntfy.sh said no (too many messages, say). */
    fun post(topic: String, body: String): Boolean {
        val connection = open(SERVER + topic)
        try {
            connection.requestMethod = "POST"
            connection.doOutput = true
            connection.setRequestProperty("Content-Type", "text/plain; charset=utf-8")
            // Only kept for the phones to collect: not pushed through Google to ntfy's own app.
            connection.setRequestProperty("X-Firebase", "no")
            connection.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
            return connection.responseCode in 200..299
        } finally {
            connection.disconnect()
        }
    }

    /** What's in [topic] from [since] (seconds since 1970; 0 for everything it still has), oldest first. */
    fun poll(topic: String, since: Long): List<Message> {
        val connection = open(SERVER + topic + "/json?poll=1&since=" + if (since > 0) since.toString() else "all")
        try {
            if (connection.responseCode !in 200..299) throw IOException("ntfy.sh said ${connection.responseCode}")
            val messages = ArrayList<Message>()
            connection.inputStream.bufferedReader(Charsets.UTF_8).useLines { lines ->
                for (line in lines) {
                    if (line.isBlank()) continue
                    val json = runCatching { JSONObject(line) }.getOrNull() ?: continue
                    if (json.optString("event") != "message") continue
                    messages.add(Message(json.optLong("time"), json.optString("message")))
                }
            }
            return messages
        } finally {
            connection.disconnect()
        }
    }

    private fun open(url: String) = (URL(url).openConnection() as HttpURLConnection).apply {
        connectTimeout = 10_000
        readTimeout = 15_000
        setRequestProperty("User-Agent", "CouchTV-Remote/${BuildConfig.VERSION_NAME}")
    }
}
