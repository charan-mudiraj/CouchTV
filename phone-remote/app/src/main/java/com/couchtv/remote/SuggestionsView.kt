package com.couchtv.remote

import android.animation.AnimatorInflater
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
import android.view.animation.PathInterpolator
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
 * The first page: the watchlist (films picked to watch at a time, with when), then what the TV suggests, popular
 * films (each on the app it streams on) and top songs (on YouTube), as posters and covers three to a row, scrolling up
 * and down. A tap plays it on the TV ([onPlay]); a film's bookmark puts it on the watchlist ([onWatch]). The pictures
 * come straight from the internet, and are kept for next time. A new list rises into place card by card the first
 * time the page comes into view ([reveal]); a card sinks a little under the finger, and pictures fade in as they arrive.
 */
class SuggestionsView @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : ScrollView(context, attrs) {
    /** Called when a film or song is tapped. */
    var onPlay: ((Suggestion) -> Unit)? = null

    /** Called for Play next on a song (its button, or held down and chosen). */
    var onPlayNext: ((Suggestion) -> Unit)? = null

    /** Called for a film's bookmark, or Add to watchlist / Change time when held: pick when to watch it. */
    var onWatch: ((Suggestion) -> Unit)? = null

    /** Called for Remove from watchlist, when a film is held. */
    var onUnwatch: ((Suggestion) -> Unit)? = null

    private var watching: List<WatchItem> = emptyList()
    private var watchingDay = Int.MIN_VALUE   // the day it was shown on: "Today" and "Tomorrow" change at midnight

    private val column = LinearLayout(context).apply { orientation = LinearLayout.VERTICAL }
    private var list: List<Suggestion> = emptyList()
    private var connected = false
    /** Shown above the suggestions (the shortcuts). */
    var header: View? = null
        set(value) {
            field = value
            build()
        }

    private val waiting = ArrayList<View>()   // cards still to rise into place
    private var entrance = true               // the next build's cards rise in (a new list, not just the TV coming or going)

    init {
        isVerticalScrollBarEnabled = false
        overScrollMode = OVER_SCROLL_IF_CONTENT_SCROLLS   // Android 12 and newer stretch it at the top and bottom
        addView(column, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT))
        build()
    }

    /** A new list from the TV, or the same one with the TV come or gone ([connected]: over Wi-Fi now). */
    fun show(suggestions: List<Suggestion>, connected: Boolean) {
        if (suggestions === list && connected == this.connected) return
        if (suggestions.map { it.id } != list.map { it.id }) entrance = true
        list = suggestions
        this.connected = connected
        build()
    }

    /** The watchlist changed (or the day did, which changes how its times read). */
    fun showWatchlist(items: List<WatchItem>) {
        val day = WatchTime.daysBetween(0, System.currentTimeMillis())
        if (day == watchingDay && items.map { it.line() } == watching.map { it.line() }) return   // (keeps a list rising in)
        watchingDay = day
        watching = items
        build()
    }

    /** The page is coming into view: a new list's cards rise into place, one after another. */
    fun reveal() {
        waiting.forEachIndexed { i, card ->
            card.animate().alpha(1f).translationY(0f)
                .setStartDelay(minOf(i, 11) * 45L).setDuration(460).setInterpolator(RISE).start()
        }
        waiting.clear()
    }

    private fun build() {
        column.removeAllViews()
        waiting.clear()
        val rise = entrance && list.isNotEmpty()
        if (rise) entrance = false
        header?.let { top ->
            (top.parent as? android.view.ViewGroup)?.removeView(top)
            column.addView(top, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply {
                topMargin = dp(4)
                bottomMargin = dp(18)
            })
        }
        if (watching.isNotEmpty()) grid(context.getString(R.string.watchlist_title), watching.map { card(it.film, 1.5f, it) }, false, top = true)
        // A style's layout_ settings only apply in a layout file: the margins are set here.
        column.addView(TextView(context, null, 0, R.style.PageTitle).apply { setText(R.string.suggestions_title) },
            wrap(if (watching.isEmpty()) 0 else 18, 12))
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
        // A section a row, in the TV's order: popular films, series, each app you pay for, songs.
        val rows = LinkedHashMap<String, MutableList<Suggestion>>()
        for (s in list) rows.getOrPut(s.rowKey) { ArrayList() }.add(s)
        for ((key, items) in rows) section(rowName(key, items[0]), items, if (items[0].movie) 1.5f else 1f, rise)
        column.addView(View(context), LinearLayout.LayoutParams(1, dp(24)))
    }

    /** A row's name, as the TV gave it (or for a TV from before rows, the films' or the songs'). */
    private fun rowName(key: String, first: Suggestion): String = first.rowTitle.ifEmpty {
        context.getString(when (key) {
            "series" -> R.string.row_series
            "songs" -> R.string.suggestions_songs
            else -> R.string.suggestions_movies
        })
    }

    /** A heading, then the cards three to a row; [ratio] is a picture's height to its width; [rise]: they wait for [reveal]. */
    private fun section(heading: String, items: List<Suggestion>, ratio: Float, rise: Boolean) {
        if (items.isNotEmpty()) grid(heading, items.map { card(it, ratio) }, rise)
    }

    /** A heading ([top]: the page's own, as big as its title), then [cards] three to a row. */
    private fun grid(heading: String, cards: List<View>, rise: Boolean, top: Boolean = false) {
        val first = column.getChildAt(column.childCount - 1) !is LinearLayout   // nothing but text above
        column.addView(TextView(context, null, 0, if (top) R.style.PageTitle else R.style.Section).apply { text = heading },
            wrap(if (first || top) 0 else 14, if (top) 12 else 10))
        for (start in cards.indices step 3) {
            val row = LinearLayout(context).apply { orientation = LinearLayout.HORIZONTAL }
            for (i in start until start + 3) {
                val cell = if (i < cards.size) cards[i] else View(context)
                if (rise && i < cards.size) {
                    cell.alpha = 0f
                    cell.translationY = dp(36).toFloat()
                    waiting += cell
                }
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

    /** A film or song; [watch]: a film on the watchlist, with when instead of its details. */
    private fun card(s: Suggestion, ratio: Float, watch: WatchItem? = null): View {
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
            if (!s.movie) {
                // A song: a round button in its corner plays it next (after the one playing on the TV).
                addView(ImageView(context).apply {
                    setImageResource(R.drawable.ic_queue_add)
                    imageTintList = android.content.res.ColorStateList.valueOf(context.getColor(R.color.ink))
                    setPadding(dp(5), dp(5), dp(5), dp(5))
                    background = GradientDrawable().apply {
                        shape = GradientDrawable.OVAL
                        setColor(0xB3000000.toInt())
                    }
                    contentDescription = context.getString(R.string.play_next)
                    isClickable = true
                    setOnClickListener {
                        Haptics.press(it)
                        it.animate().scaleX(1.25f).scaleY(1.25f).setDuration(110).withEndAction {
                            it.animate().scaleX(1f).scaleY(1f).setDuration(200).start()
                        }.start()
                        onPlayNext?.invoke(s)
                    }
                }, FrameLayout.LayoutParams(dp(30), dp(30), Gravity.BOTTOM or Gravity.END).apply { setMargins(0, 0, dp(5), dp(5)) })
            }
            if (s.movie && watch == null) {
                // A film: its bookmark in the corner puts it on the watchlist (filled in green once it's there).
                val listed = watching.any { it.id == WatchItem.idOf(s) }
                addView(ImageView(context).apply {
                    setImageResource(if (listed) R.drawable.ic_bookmark_added else R.drawable.ic_bookmark_add)
                    imageTintList = android.content.res.ColorStateList.valueOf(context.getColor(if (listed) R.color.good else R.color.ink))
                    setPadding(dp(5), dp(5), dp(5), dp(5))
                    background = GradientDrawable().apply {
                        shape = GradientDrawable.OVAL
                        setColor(0xB3000000.toInt())
                    }
                    contentDescription = context.getString(if (listed) R.string.watch_change else R.string.watch_add_to)
                    isClickable = true
                    setOnClickListener {
                        Haptics.press(it)
                        onWatch?.invoke(s)
                    }
                }, FrameLayout.LayoutParams(dp(30), dp(30), Gravity.BOTTOM or Gravity.END).apply { setMargins(0, 0, dp(5), dp(5)) })
            }
        }
        val title = TextView(context).apply {
            text = s.title
            setTextColor(context.getColor(R.color.ink))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
            setPadding(dp(2), dp(6), dp(2), 0)
        }
        val now = System.currentTimeMillis()
        val details = TextView(context).apply {
            text = if (watch != null) WatchTime.describe(context, watch.watchAt, now) else s.subtitle
            // When it's coming today: in green.
            val soon = watch != null && watch.watchAt > now && WatchTime.daysBetween(now, watch.watchAt) == 0
            setTextColor(context.getColor(if (soon) R.color.good else R.color.muted))
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
            stateListAnimator = AnimatorInflater.loadStateListAnimator(context, R.animator.card_press)
            contentDescription = s.title + ", " + s.app
            setOnClickListener {
                Haptics.press(it)
                onPlay?.invoke(s)
            }
            setOnLongClickListener { v ->
                // Held down: a song plays now or next; a film plays now, or goes on the watchlist (or changes there).
                Haptics.press(v)
                val listed = s.movie && watching.any { it.id == WatchItem.idOf(s) }
                android.widget.PopupMenu(context, v).apply {
                    menu.add(0, 1, 0, R.string.play_now)
                    if (!s.movie) menu.add(0, 2, 1, R.string.play_next)
                    else menu.add(0, 3, 1, if (listed) R.string.watch_change else R.string.watch_add_to)
                    if (listed) menu.add(0, 4, 2, R.string.watch_remove_from)
                    setOnMenuItemClickListener { item ->
                        when (item.itemId) {
                            1 -> onPlay?.invoke(s)
                            2 -> onPlayNext?.invoke(s)
                            3 -> onWatch?.invoke(s)
                            4 -> onUnwatch?.invoke(s)
                        }
                        true
                    }
                }.show()
                true
            }
        }
    }

    private fun dp(value: Int) = (value * resources.displayMetrics.density + 0.5f).toInt()

    private companion object {
        val RISE = PathInterpolator(0.2f, 0f, 0f, 1f)
    }

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

    /** Pictures from the internet: kept in memory while the app runs, and on disk for next time. Up next's too. */
    object Pictures {
        private val memory = object : LruCache<String, Bitmap>(16 * 1024 * 1024) {
            override fun sizeOf(key: String, value: Bitmap) = value.byteCount
        }
        private val pool = Executors.newFixedThreadPool(3)
        private val main = Handler(Looper.getMainLooper())

        /** The picture now (from memory, the disk, or the internet): for a notification, off the main thread. */
        fun get(context: Context, url: String, width: Int): Bitmap? =
            memory.get(url) ?: fetch(File(context.cacheDir, "suggestions"), url, width)?.also { memory.put(url, it) }

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
                main.post {
                    if (view.tag != url) return@post
                    view.alpha = 0f   // fresh from the internet (or the disk): it fades in
                    view.setImageBitmap(bitmap)
                    view.animate().alpha(1f).setDuration(260).start()
                }
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
