package com.couchtv.remote

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Handler
import android.os.Looper
import android.text.TextUtils
import android.util.AttributeSet
import android.util.LruCache
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.util.concurrent.Executors

/**
 * The first page: what the TV suggests, popular films (each on the app it streams on) and top songs (on YouTube), as
 * posters and covers three to a row, scrolling up and down. A tap plays it on the TV ([onPlay]). The pictures come
 * straight from the internet, and are kept for next time.
 */
class SuggestionsView @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : ScrollView(context, attrs) {
    /** Called when a film or song is tapped. */
    var onPlay: ((Suggestion) -> Unit)? = null

    private val column = LinearLayout(context).apply { orientation = LinearLayout.VERTICAL }
    private var list: List<Suggestion> = emptyList()
    private var connected = false

    init {
        isVerticalScrollBarEnabled = false
        overScrollMode = OVER_SCROLL_NEVER
        addView(column, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT))
        build()
    }

    /** A new list from the TV, or the same one with the TV come or gone ([connected]: over Wi-Fi now). */
    fun show(suggestions: List<Suggestion>, connected: Boolean) {
        if (suggestions === list && connected == this.connected) return
        list = suggestions
        this.connected = connected
        build()
    }

    private fun build() {
        column.removeAllViews()
        // A style's layout_ settings only apply in a layout file: the margins are set here.
        column.addView(TextView(context, null, 0, R.style.PageTitle).apply { setText(R.string.suggestions_title) }, wrap(0, 12))
        if (list.isEmpty()) {
            column.addView(TextView(context).apply {
                setText(if (connected) R.string.suggestions_waiting else R.string.suggestions_offline)
                setTextColor(context.getColor(R.color.muted))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f)
                setPadding(0, dp(8), 0, 0)
            })
            return
        }
        if (!connected) {
            // The list the TV sent last time: a tap turns the TV on, and it plays once the TV's there.
            column.addView(TextView(context).apply {
                setText(R.string.suggestions_tv_off)
                setTextColor(context.getColor(R.color.muted))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
            }, wrap(0, 6))
        }
        section(R.string.suggestions_movies, list.filter { it.movie }, 1.5f)
        section(R.string.suggestions_songs, list.filter { !it.movie }, 1f)
        column.addView(View(context), LinearLayout.LayoutParams(1, dp(24)))
    }

    /** A heading, then the cards three to a row; [ratio] is a picture's height to its width. */
    private fun section(heading: Int, items: List<Suggestion>, ratio: Float) {
        if (items.isEmpty()) return
        val first = column.getChildAt(column.childCount - 1) !is LinearLayout   // nothing but text above
        column.addView(TextView(context, null, 0, R.style.Section).apply { setText(heading) }, wrap(if (first) 0 else 14, 10))
        for (start in items.indices step 3) {
            val row = LinearLayout(context).apply { orientation = LinearLayout.HORIZONTAL }
            for (i in start until start + 3) {
                val cell = if (i < items.size) card(items[i], ratio) else View(context)
                row.addView(cell, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply {
                    marginStart = if (i == start) 0 else dp(5)
                    marginEnd = if (i == start + 2) 0 else dp(5)
                })
            }
            column.addView(row, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply {
                bottomMargin = dp(14)
            })
        }
    }

    private fun card(s: Suggestion, ratio: Float): View {
        val picture = Picture(context, ratio).apply {
            scaleType = ImageView.ScaleType.CENTER_CROP
            background = GradientDrawable().apply {
                cornerRadius = dp(10).toFloat()
                setColor(context.getColor(R.color.surface))
            }
            clipToOutline = true
        }
        s.image?.let { Pictures.load(context, it, picture, dp(160)) }
        val badge = TextView(context).apply {
            text = s.app
            setTextColor(s.appTextColor)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 10f)
            typeface = Typeface.DEFAULT_BOLD
            maxLines = 1
            setPadding(dp(6), dp(2), dp(6), dp(3))
            background = GradientDrawable().apply {
                cornerRadius = dp(5).toFloat()
                setColor(s.appColor)
            }
        }
        val frame = FrameLayout(context).apply {
            addView(picture, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT))
            addView(badge, FrameLayout.LayoutParams(FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.TOP or Gravity.START).apply {
                setMargins(dp(5), dp(5), dp(5), 0)
            })
        }
        val title = TextView(context).apply {
            text = s.title
            setTextColor(context.getColor(R.color.ink))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
            setPadding(dp(2), dp(6), dp(2), 0)
        }
        val details = TextView(context).apply {
            text = s.subtitle
            setTextColor(context.getColor(R.color.muted))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 11f)
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
            setPadding(dp(2), 0, dp(2), 0)
        }
        return LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL
            addView(frame)
            addView(title)
            addView(details)
            isClickable = true
            isFocusable = true
            contentDescription = s.title + ", " + s.app
            setOnClickListener {
                Haptics.press(it)
                onPlay?.invoke(s)
            }
        }
    }

    private fun dp(value: Int) = (value * resources.displayMetrics.density + 0.5f).toInt()

    private fun wrap(top: Int, bottom: Int) =
        LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply {
            topMargin = dp(top)
            bottomMargin = dp(bottom)
        }

    /** A picture as tall as its width times [ratio] (a poster, or a square cover). */
    private class Picture(context: Context, private val ratio: Float) : ImageView(context) {
        override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
            val width = MeasureSpec.getSize(widthMeasureSpec)
            setMeasuredDimension(width, (width * ratio).toInt())
        }
    }

    /** Pictures from the internet: kept in memory while the app runs, and on disk for next time. */
    private object Pictures {
        private val memory = object : LruCache<String, Bitmap>(16 * 1024 * 1024) {
            override fun sizeOf(key: String, value: Bitmap) = value.byteCount
        }
        private val pool = Executors.newFixedThreadPool(3)
        private val main = Handler(Looper.getMainLooper())

        fun load(context: Context, url: String, view: ImageView, width: Int) {
            view.tag = url
            memory.get(url)?.let {
                view.setImageBitmap(it)
                return
            }
            val folder = File(context.cacheDir, "suggestions")
            pool.execute {
                val bitmap = runCatching { fetch(folder, url, width) }.getOrNull() ?: return@execute
                memory.put(url, bitmap)
                main.post { if (view.tag == url) view.setImageBitmap(bitmap) }
            }
        }

        private fun fetch(folder: File, url: String, width: Int): Bitmap? {
            folder.mkdirs()
            val file = File(folder, name(url))
            if (!file.exists()) {
                prune(folder)
                val part = File(folder, file.name + ".part")
                val connection = URL(url).openConnection() as HttpURLConnection
                connection.connectTimeout = 10000
                connection.readTimeout = 15000
                try {
                    connection.inputStream.use { input -> part.outputStream().use { input.copyTo(it) } }
                } finally {
                    connection.disconnect()
                }
                if (!part.renameTo(file)) return null
            }
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeFile(file.path, bounds)
            var sample = 1
            while (bounds.outWidth / (sample * 2) >= width) sample *= 2
            return BitmapFactory.decodeFile(file.path, BitmapFactory.Options().apply { inSampleSize = sample })
        }

        private fun name(url: String) =
            MessageDigest.getInstance("SHA-1").digest(url.toByteArray()).take(10).joinToString("") { "%02x".format(it) }

        /** Lets go of pictures not used for two weeks (the lists change every few hours). */
        private fun prune(folder: File) {
            val old = System.currentTimeMillis() - 14L * 24 * 3600 * 1000
            folder.listFiles()?.forEach { if (it.lastModified() < old) it.delete() }
        }
    }
}
