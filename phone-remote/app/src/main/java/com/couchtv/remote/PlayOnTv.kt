package com.couchtv.remote

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import java.util.UUID

/**
 * Play on TV from CouchTV Watch (the phone app for watching on the phone): the remote opens and plays the title on
 * the TV, turning the TV on first if need be, as Play on TV from a watchlist reminder does. Only apps signed with
 * the remote's own key may start this (the PLAY_ON_TV permission), and it passes the title on to MainActivity with
 * a token only this process knows, since MainActivity itself is open to every app.
 */
class PlayOnTv : Activity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val line = intent.getStringExtra(EXTRA_SUGGESTION)
        if (line != null && Suggestion.parse(line) != null) {
            startActivity(Intent(this, MainActivity::class.java)
                .setAction(ACTION)
                .putExtra(EXTRA_SUGGESTION, line)
                .putExtra(EXTRA_TOKEN, TOKEN)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP))
        }
        finish()
    }

    companion object {
        const val ACTION = "com.couchtv.remote.PLAY_ON_TV"
        /** The title, as a suggestion line (Suggestion.line). */
        const val EXTRA_SUGGESTION = "suggestion"
        const val EXTRA_TOKEN = "token"
        /** Made afresh each time the app starts: what came through here, and nothing sent to MainActivity directly. */
        val TOKEN: String = UUID.randomUUID().toString()
    }
}
