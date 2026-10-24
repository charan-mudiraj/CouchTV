package com.couchtv.watch

import android.annotation.SuppressLint
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.ActivityInfo
import android.graphics.Color
import android.os.Build
import android.os.Bundle
import android.view.View
import android.view.ViewGroup
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.webkit.CookieManager
import android.webkit.PermissionRequest
import android.webkit.WebChromeClient
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.FrameLayout

/**
 * Plays a title inside the app: its page on the app's own website, full screen, in that website's player (copy-
 * protected films too: Widevine, as in a browser). For the apps whose websites play on a phone: YouTube, JioHotstar,
 * ZEE5, Sun NXT and ETV Win. Netflix and Prime Video only play in their own apps on a phone, so they never come here.
 * Logins are kept (cookies), so each website is logged into once. The site's own full-screen button turns the phone
 * sideways; Back leaves full screen, then goes back a page, then closes.
 */
class PlayerActivity : Activity() {
    companion object {
        private const val EXTRA_URL = "url"
        private const val MATCH = ViewGroup.LayoutParams.MATCH_PARENT

        /** The apps whose websites play here (JustWatch's names for them; "youtube" for songs). */
        private val HERE = setOf("youtube", "jhs", "zee", "snx", "etv")

        private fun prefs(context: Context) = context.getSharedPreferences("player", Context.MODE_PRIVATE)

        /** Whether this app's titles play here (Settings can send them all to their own apps instead). */
        fun plays(context: Context, appKey: String): Boolean = appKey in HERE && inApp(context)

        fun inApp(context: Context): Boolean = prefs(context).getBoolean("inApp", true)

        fun setInApp(context: Context, on: Boolean) = prefs(context).edit().putBoolean("inApp", on).apply()

        fun open(activity: Activity, url: String) {
            activity.startActivity(Intent(activity, PlayerActivity::class.java).putExtra(EXTRA_URL, url))
        }
    }

    private lateinit var frame: FrameLayout
    private lateinit var web: WebView
    private var fullView: View? = null                               // the video, full screen (the site's button)
    private var fullDone: WebChromeClient.CustomViewCallback? = null
    private val tests = TestHooks(this) { false }

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val url = intent.getStringExtra(EXTRA_URL)
        if (url == null || !url.startsWith("https://")) {
            finish()
            return
        }
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)   // a film plays without anyone touching the phone
        frame = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }
        web = WebView(this)
        frame.addView(web, FrameLayout.LayoutParams(MATCH, MATCH))
        setContentView(frame)
        hideBars()

        CookieManager.getInstance().setAcceptCookie(true)
        CookieManager.getInstance().setAcceptThirdPartyCookies(web, true)
        web.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            mediaPlaybackRequiresUserGesture = false
            useWideViewPort = true
            loadWithOverviewMode = true
        }
        web.webViewClient = object : WebViewClient() {
            // Pages stay here; "open in the app" links (intent:, market:, the app's own) aren't followed: it plays here.
            override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                val scheme = request.url.scheme
                return scheme != "https" && scheme != "http"
            }
        }
        web.webChromeClient = object : WebChromeClient() {
            // Copy-protected video (Widevine) is what the films' players ask for; nothing else is given (no camera or microphone).
            override fun onPermissionRequest(request: PermissionRequest) {
                if (PermissionRequest.RESOURCE_PROTECTED_MEDIA_ID in request.resources) request.grant(arrayOf(PermissionRequest.RESOURCE_PROTECTED_MEDIA_ID))
                else request.deny()
            }

            override fun onShowCustomView(view: View, callback: CustomViewCallback) {
                if (fullView != null) {
                    callback.onCustomViewHidden()
                    return
                }
                fullView = view
                fullDone = callback
                frame.addView(view, FrameLayout.LayoutParams(MATCH, MATCH))
                web.visibility = View.INVISIBLE
                requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
                hideBars()
            }

            override fun onHideCustomView() {
                leaveFullScreen()
            }
        }
        if (savedInstanceState == null || web.restoreState(savedInstanceState) == null) web.loadUrl(url)
    }

    override fun onResume() {
        super.onResume()
        if (::web.isInitialized) web.onResume()
        tests.start()
    }

    override fun onPause() {
        super.onPause()
        tests.stop()
        if (::web.isInitialized) web.onPause()   // the film stops when the app's put away
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        if (::web.isInitialized) web.saveState(outState)
    }

    override fun onDestroy() {
        if (::web.isInitialized) {
            frame.removeAllViews()
            web.destroy()
        }
        super.onDestroy()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideBars()   // back after a swipe showed them, or a dialog
    }

    @Deprecated("Deprecated in Java")
    @Suppress("DEPRECATION")
    override fun onBackPressed() {
        when {
            fullView != null -> {
                fullDone?.onCustomViewHidden()
                leaveFullScreen()
            }
            ::web.isInitialized && web.canGoBack() -> web.goBack()
            else -> super.onBackPressed()
        }
    }

    private fun leaveFullScreen() {
        val view = fullView ?: return
        frame.removeView(view)
        fullView = null
        fullDone = null
        web.visibility = View.VISIBLE
        requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_FULL_USER
    }

    /** The whole screen for the film: the status and navigation bars hide, and a swipe from the edge shows them a while. */
    @Suppress("DEPRECATION")
    private fun hideBars() {
        if (Build.VERSION.SDK_INT >= 28) {
            window.attributes = window.attributes.apply { layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES }
        }
        if (Build.VERSION.SDK_INT >= 30) {
            window.setDecorFitsSystemWindows(false)
            window.insetsController?.let {
                it.hide(WindowInsets.Type.systemBars())
                it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
        } else {
            window.decorView.systemUiVisibility = View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or View.SYSTEM_UI_FLAG_FULLSCREEN or
                View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or
                View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_LAYOUT_STABLE
        }
    }
}
