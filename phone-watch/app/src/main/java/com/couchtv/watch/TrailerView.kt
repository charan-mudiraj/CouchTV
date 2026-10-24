package com.couchtv.watch

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Color
import android.webkit.JavascriptInterface
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebView
import android.webkit.WebViewClient
import java.io.ByteArrayInputStream

/**
 * The hero's trailer: YouTube's player in a WebView (assets/trailer.html, as the TV's), see-through until the video
 * plays over the title's picture beneath. The page is served at https://couchtv.home/trailer.html (made up, and never
 * fetched: everything at that address comes from here), since YouTube's player wants a proper page to sit in.
 */
@SuppressLint("SetJavaScriptEnabled", "ViewConstructor")
class TrailerView(context: Context) : WebView(context) {
    companion object {
        private const val HOST = "couchtv.home"
        private val ID = Regex("^[\\w-]{11}$")
    }

    /** What the page says: "playing", "ended", "error <code>". Main thread. */
    var onState: ((String) -> Unit)? = null

    private var ready = false          // YouTube's player can be asked
    private var pending: String? = null   // a play asked for before then

    init {
        setBackgroundColor(Color.TRANSPARENT)
        isVerticalScrollBarEnabled = false
        isHorizontalScrollBarEnabled = false
        overScrollMode = OVER_SCROLL_NEVER
        isFocusable = false
        settings.javaScriptEnabled = true
        settings.domStorageEnabled = true
        settings.mediaPlaybackRequiresUserGesture = false   // the trailer plays by itself, with sound
        addJavascriptInterface(Bridge(), "CouchTV")
        webViewClient = object : WebViewClient() {
            override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse? {
                if (request.url.host != HOST) return null
                if (request.url.path == "/trailer.html") return WebResourceResponse("text/html", "utf-8", context.assets.open("trailer.html"))
                return WebResourceResponse("text/plain", "utf-8", 404, "Not here", emptyMap(), ByteArrayInputStream(ByteArray(0)))
            }

            // Nothing else opens in here (a tap that got through to YouTube's logo, say).
            override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean =
                request.isForMainFrame && request.url.host != HOST
        }
        loadUrl("https://$HOST/trailer.html")
    }

    private inner class Bridge {
        @JavascriptInterface
        fun say(message: String) {
            post {
                if (message == "ready") {
                    ready = true
                    pending?.let { evaluateJavascript(it, null) }
                    pending = null
                } else {
                    onState?.invoke(message)
                }
            }
        }
    }

    /** Plays a YouTube video (its 11-character id) from the start, fading in over the picture. */
    fun play(video: String, muted: Boolean) {
        if (!ID.matches(video)) return
        val script = "play('$video', $muted)"
        if (ready) evaluateJavascript(script, null) else pending = script
    }

    fun mute(muted: Boolean) {
        if (ready) evaluateJavascript("mute($muted)", null)
    }

    fun stopTrailer() {
        pending = null
        if (ready) evaluateJavascript("stop()", null)
    }
}
