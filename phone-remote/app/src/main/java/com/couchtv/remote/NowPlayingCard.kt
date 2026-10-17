package com.couchtv.remote

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Outline
import android.graphics.Paint
import android.graphics.drawable.GradientDrawable
import android.os.SystemClock
import android.text.TextUtils
import android.util.TypedValue
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.ViewOutlineProvider
import android.view.animation.DecelerateInterpolator
import android.view.animation.OvershootInterpolator
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import kotlin.math.max

/**
 * What the TV plays, on the remote's first page under the shortcuts: the picture, the title and artist, the timeline
 * (drag it to move along), previous, play/pause, next, and up next under them (YouTube: songs added with Play next,
 * then YouTube's own list; tap one to play it, ✕ takes one of yours out). The card takes on the picture's colour.
 * Hidden while nothing plays. Follows TvPlayer, and its buttons go through it (shown at once, then the TV says so).
 */
class NowPlayingCard(context: Context) : LinearLayout(context) {
    /** A button: "previous", "play", "pause", "next", or "seek" with where to (ms from the start). */
    var onButton: ((String, Long) -> Unit)? = null

    /** A song of up next tapped (its video): play it now. */
    var onQueuePlay: ((String) -> Unit)? = null

    /** A song added with Play next taken out (its video). */
    var onQueueRemove: ((String) -> Unit)? = null

    private val density = resources.displayMetrics.density
    private fun dp(v: Float) = (v * density + 0.5f).toInt()
    private val art = ImageView(context)
    private val placeholder = ImageView(context)
    private val status = TextView(context)
    private val title = TextView(context)
    private val artist = TextView(context)
    private val at = TextView(context)
    private val length = TextView(context)
    private val timeline = Timeline(context)
    private val previous = RoundButton(context, R.drawable.ic_skip_previous, false)
    private val playPause = RoundButton(context, R.drawable.ic_pause, true)
    private val next = RoundButton(context, R.drawable.ic_skip_next, false)
    private val timelineRow = LinearLayout(context)
    private val queueBox = LinearLayout(context)
    private var queue: List<TvQueueItem> = emptyList()
    private var queueOpen = false   // every song shown, not just the first few
    private val back = GradientDrawable().apply { cornerRadius = 22 * density }
    private var media: TvMedia? = null
    private var artShown: Bitmap? = null
    private var titleShown: String? = null

    private val tick = object : Runnable {
        override fun run() {
            showTime()
            if (media?.playing == true && isAttachedToWindow) postDelayed(this, 500)
        }
    }

    init {
        orientation = VERTICAL
        background = back
        setPadding(dp(14f), dp(14f), dp(14f), dp(10f))
        visibility = GONE
        back.setColor(context.getColor(R.color.surface))

        val top = LinearLayout(context).apply { gravity = Gravity.CENTER_VERTICAL }
        val picture = FrameLayout(context).apply {
            background = GradientDrawable().apply {
                cornerRadius = 14 * density
                setColor(context.getColor(R.color.surface_raised))
            }
            outlineProvider = object : ViewOutlineProvider() {
                override fun getOutline(view: View, outline: Outline) = outline.setRoundRect(0, 0, view.width, view.height, 14 * density)
            }
            clipToOutline = true
        }
        placeholder.setImageResource(R.drawable.ic_music)
        placeholder.imageTintList = android.content.res.ColorStateList.valueOf(context.getColor(R.color.muted))
        picture.addView(placeholder, FrameLayout.LayoutParams(dp(28f), dp(28f), Gravity.CENTER))
        art.scaleType = ImageView.ScaleType.CENTER_CROP
        picture.addView(art, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))
        top.addView(picture, LayoutParams(dp(64f), dp(64f)))

        val words = LinearLayout(context).apply { orientation = VERTICAL }
        status.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f)
        status.setTextColor(context.getColor(R.color.muted))
        status.setSingleLine()
        status.ellipsize = TextUtils.TruncateAt.END
        title.setTextSize(TypedValue.COMPLEX_UNIT_SP, 16f)
        title.setTextColor(context.getColor(R.color.ink))
        title.paint.isFakeBoldText = true
        title.setSingleLine()
        title.ellipsize = TextUtils.TruncateAt.END
        artist.setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
        artist.setTextColor(context.getColor(R.color.muted))
        artist.setSingleLine()
        artist.ellipsize = TextUtils.TruncateAt.END
        words.addView(status)
        words.addView(title, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT).apply { topMargin = dp(2f) })
        words.addView(artist)
        top.addView(words, LayoutParams(0, LayoutParams.WRAP_CONTENT, 1f).apply { marginStart = dp(14f) })
        addView(top)

        // The timeline: where it's got to, the bar, how long it is.
        timelineRow.gravity = Gravity.CENTER_VERTICAL
        for (t in listOf(at, length)) {
            t.setTextSize(TypedValue.COMPLEX_UNIT_SP, 11f)
            t.setTextColor(context.getColor(R.color.muted))
            t.minWidth = dp(34f)
        }
        length.gravity = Gravity.END
        timelineRow.addView(at)
        timelineRow.addView(timeline, LayoutParams(0, dp(28f), 1f).apply {
            marginStart = dp(6f)
            marginEnd = dp(6f)
        })
        timelineRow.addView(length)
        addView(timelineRow, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT).apply { topMargin = dp(8f) })
        timeline.onSeek = { ms -> onButton?.invoke("seek", ms) }

        val buttons = LinearLayout(context).apply { gravity = Gravity.CENTER }
        buttons.addView(previous, LayoutParams(dp(48f), dp(48f)))
        buttons.addView(playPause, LayoutParams(dp(56f), dp(56f)).apply {
            marginStart = dp(28f)
            marginEnd = dp(28f)
        })
        buttons.addView(next, LayoutParams(dp(48f), dp(48f)))
        addView(buttons, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT).apply { topMargin = dp(2f) })
        queueBox.orientation = VERTICAL
        queueBox.visibility = GONE
        addView(queueBox, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT).apply { topMargin = dp(10f) })
        previous.setOnClickListener { onButton?.invoke("previous", 0) }
        next.setOnClickListener { onButton?.invoke("next", 0) }
        playPause.setOnClickListener { media?.let { onButton?.invoke(if (it.playing) "pause" else "play", 0) } }
        previous.contentDescription = context.getString(R.string.media_previous)
        next.contentDescription = context.getString(R.string.media_next)
    }

    /** What plays now (null: nothing, and the card goes), with its picture. */
    fun show(m: TvMedia?, picture: Bitmap?) {
        removeCallbacks(tick)
        media = m
        if (m == null) {
            if (visibility == VISIBLE) animate().alpha(0f).translationY(-dp(8f).toFloat()).setDuration(180).withEndAction { visibility = GONE }.start()
            return
        }
        if (visibility != VISIBLE || alpha < 1f) {
            visibility = VISIBLE
            alpha = 0f
            translationY = dp(12f).toFloat()
            animate().alpha(1f).translationY(0f).setDuration(320).setInterpolator(DecelerateInterpolator()).withEndAction(null).start()
        }
        status.text = when {
            m.app.isEmpty() -> context.getString(if (m.playing) R.string.media_something else R.string.media_paused)
            else -> context.getString(if (m.playing) R.string.media_playing_on_tv else R.string.media_paused_on_tv, m.app)
        }
        val name = m.title.ifEmpty { m.artist }
        if (name != titleShown && titleShown != null) title.alpha = 0f
        titleShown = name
        title.text = name
        title.animate().alpha(1f).setDuration(250).start()
        artist.text = if (m.title.isEmpty()) "" else m.artist
        artist.visibility = if (artist.text.isEmpty()) GONE else VISIBLE
        if (picture !== artShown) {
            artShown = picture
            art.setImageBitmap(picture)
            placeholder.visibility = if (picture == null) VISIBLE else GONE
            tint(picture)
        }
        playPause.icon(if (m.playing) R.drawable.ic_pause else R.drawable.ic_play)
        playPause.contentDescription = context.getString(if (m.playing) R.string.media_pause else R.string.media_play)
        playPause.able(m.canPlayPause)
        previous.able(m.canPrevious)
        next.able(m.canNext)
        timelineRow.visibility = if (m.durationMs > 0) VISIBLE else GONE
        timeline.seekable = m.canSeek
        showTime()
        if (m.playing) postDelayed(tick, 500)
    }

    /** Up next on the TV (empty: none to show). */
    fun showQueue(list: List<TvQueueItem>) {
        if (list == queue) return
        queue = list
        if (list.size <= SHORT) queueOpen = false
        buildQueue()
    }

    private fun buildQueue() {
        queueBox.removeAllViews()
        queueBox.visibility = if (queue.isEmpty()) GONE else VISIBLE
        if (queue.isEmpty()) return
        val head = LinearLayout(context).apply { gravity = Gravity.CENTER_VERTICAL }
        head.addView(TextView(context).apply {
            setText(R.string.up_next)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
            setTextColor(context.getColor(R.color.ink))
            paint.isFakeBoldText = true
        }, LayoutParams(0, LayoutParams.WRAP_CONTENT, 1f))
        head.addView(TextView(context).apply {
            text = context.getString(R.string.up_next_count, queue.size)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f)
            setTextColor(context.getColor(R.color.muted))
        })
        queueBox.addView(head, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT).apply {
            bottomMargin = dp(4f)
            marginStart = dp(2f)
            marginEnd = dp(2f)
        })
        val shown = if (queueOpen) queue else queue.take(SHORT)
        for (item in shown) queueBox.addView(queueRow(item))
        if (queue.size > SHORT) queueBox.addView(TextView(context).apply {
            text = if (queueOpen) context.getString(R.string.up_next_fewer) else context.getString(R.string.up_next_all, queue.size)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
            setTextColor(context.getColor(R.color.voice_rim))
            gravity = Gravity.CENTER
            setPadding(0, dp(10f), 0, dp(6f))
            isClickable = true
            setOnClickListener {
                queueOpen = !queueOpen
                buildQueue()
            }
        }, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT))
    }

    /** One song of up next: its picture, title, artist and length (or "Added by you"); ✕ on one of yours. */
    private fun queueRow(item: TvQueueItem): View {
        val row = LinearLayout(context).apply {
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(2f), dp(6f), dp(2f), dp(6f))
            isClickable = true
            isFocusable = true
            background = context.getDrawable(R.drawable.row_bg)
            setOnClickListener {
                Haptics.press(it)
                onQueuePlay?.invoke(item.id)
            }
        }
        val picture = ImageView(context).apply {
            scaleType = ImageView.ScaleType.CENTER_CROP
            background = GradientDrawable().apply {
                cornerRadius = 8 * density
                setColor(context.getColor(R.color.surface_raised))
            }
            outlineProvider = object : ViewOutlineProvider() {
                override fun getOutline(view: View, outline: Outline) = outline.setRoundRect(0, 0, view.width, view.height, 8 * density)
            }
            clipToOutline = true
        }
        SuggestionsView.Pictures.load(context, item.picture, picture, dp(96f))
        row.addView(picture, LayoutParams(dp(64f), dp(36f)))
        val words = LinearLayout(context).apply { orientation = VERTICAL }
        words.addView(TextView(context).apply {
            text = item.title
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
            setTextColor(context.getColor(R.color.ink))
            setSingleLine()
            ellipsize = TextUtils.TruncateAt.END
        })
        val about = listOfNotNull(
            if (item.added) context.getString(R.string.up_next_added) else null,
            item.artist.ifEmpty { null },
            if (item.seconds > 0) clock(item.seconds * 1000L) else null,
        ).joinToString(" · ")
        if (about.isNotEmpty()) words.addView(TextView(context).apply {
            text = about
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f)
            setTextColor(context.getColor(if (item.added) R.color.voice_rim else R.color.muted))
            setSingleLine()
            ellipsize = TextUtils.TruncateAt.END
        })
        row.addView(words, LayoutParams(0, LayoutParams.WRAP_CONTENT, 1f).apply { marginStart = dp(12f) })
        if (item.added) row.addView(ImageView(context).apply {
            setImageResource(R.drawable.ic_close)
            imageTintList = android.content.res.ColorStateList.valueOf(context.getColor(R.color.muted))
            setPadding(dp(10f), dp(10f), dp(10f), dp(10f))
            contentDescription = context.getString(R.string.up_next_remove)
            isClickable = true
            background = context.getDrawable(R.drawable.row_bg)
            setOnClickListener {
                Haptics.press(it)
                row.animate().alpha(0f).translationX(row.width * 0.3f).setDuration(180).withEndAction { row.visibility = GONE }.start()
                onQueueRemove?.invoke(item.id)
            }
        }, LayoutParams(dp(40f), dp(40f)))
        return row
    }

    private fun showTime() {
        val m = media ?: return
        if (m.durationMs <= 0) return
        val position = if (timeline.dragging) timeline.draggedMs else m.positionAt(SystemClock.elapsedRealtime())
        at.text = clock(position)
        length.text = clock(m.durationMs)
        timeline.set(position, m.durationMs)
    }

    /** The card takes on the picture's colour, deep enough for white words on it. */
    private fun tint(picture: Bitmap?) {
        val surface = context.getColor(R.color.surface)
        val colour = picture?.let { runCatching { Bitmap.createScaledBitmap(it, 1, 1, true).getPixel(0, 0) }.getOrNull() }
        if (colour == null) {
            back.colors = intArrayOf(surface, surface)
            return
        }
        val hsv = FloatArray(3)
        Color.colorToHSV(colour, hsv)
        hsv[1] = minOf(hsv[1], 0.75f)
        hsv[2] = minOf(hsv[2], 0.42f)
        back.orientation = GradientDrawable.Orientation.TL_BR
        back.colors = intArrayOf(Color.HSVToColor(hsv), surface)
    }

    override fun onDetachedFromWindow() {
        removeCallbacks(tick)
        super.onDetachedFromWindow()
    }

    private companion object {
        const val SHORT = 4   // songs of up next shown before "Show all"
    }

    private fun clock(ms: Long): String {
        val s = max(0L, ms / 1000)
        return if (s >= 3600) "%d:%02d:%02d".format(s / 3600, s / 60 % 60, s % 60) else "%d:%02d".format(s / 60, s % 60)
    }

    /** A round button: the middle one filled white, the others plain. Dimmed when the TV says it doesn't work now. */
    private class RoundButton(context: Context, icon: Int, filled: Boolean) : FrameLayout(context) {
        private val image = ImageView(context)

        init {
            val density = resources.displayMetrics.density
            background = GradientDrawable().apply {
                shape = GradientDrawable.OVAL
                setColor(context.getColor(if (filled) R.color.ink else android.R.color.transparent))
            }
            isClickable = true
            isFocusable = true
            image.setImageResource(icon)
            image.imageTintList = android.content.res.ColorStateList.valueOf(context.getColor(if (filled) R.color.background else R.color.ink))
            val size = ((if (filled) 30 else 28) * density).toInt()
            addView(image, LayoutParams(size, size, Gravity.CENTER))
            setOnTouchListener { v, e ->
                when (e.actionMasked) {
                    MotionEvent.ACTION_DOWN -> {
                        Haptics.press(v)
                        v.animate().scaleX(0.88f).scaleY(0.88f).setDuration(80).start()
                    }
                    MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                        if (e.actionMasked == MotionEvent.ACTION_UP) Haptics.release(v)
                        v.animate().scaleX(1f).scaleY(1f).setDuration(260).setInterpolator(OvershootInterpolator(3f)).start()
                    }
                }
                false
            }
        }

        fun icon(id: Int) = image.setImageResource(id)

        fun able(on: Boolean) {
            isEnabled = on
            alpha = if (on) 1f else 0.3f
        }
    }

    /** The timeline: a thin bar, with a knob to drag when the TV can move it. */
    private class Timeline(context: Context) : View(context) {
        var onSeek: ((Long) -> Unit)? = null
        var seekable = false
            set(value) {
                field = value
                invalidate()
            }
        var dragging = false
            private set
        var draggedMs = 0L
            private set
        private var fraction = 0f
        private var length = 0L
        private val density = resources.displayMetrics.density
        private val track = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0x38FFFFFF }
        private val done = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = context.getColor(R.color.ink) }

        fun set(position: Long, length: Long) {
            this.length = length
            if (!dragging) fraction = if (length > 0) (position.toFloat() / length).coerceIn(0f, 1f) else 0f
            invalidate()
        }

        override fun onDraw(canvas: Canvas) {
            val r = 6 * density
            val left = r
            val right = width - r
            val y = height / 2f
            val h = (if (dragging) 3f else 2f) * density
            canvas.drawRoundRect(left, y - h, right, y + h, h, h, track)
            val x = left + (right - left) * fraction
            canvas.drawRoundRect(left, y - h, x, y + h, h, h, done)
            if (seekable) canvas.drawCircle(x, y, (if (dragging) 8f else 5.5f) * density, done)
        }

        @SuppressLint("ClickableViewAccessibility")
        override fun onTouchEvent(e: MotionEvent): Boolean {
            if (!seekable || length <= 0) return false
            val r = 6 * density
            fun follow() {
                fraction = ((e.x - r) / (width - 2 * r)).coerceIn(0f, 1f)
                draggedMs = (fraction * length).toLong()
                invalidate()
            }
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    parent.requestDisallowInterceptTouchEvent(true)   // the pages and the scroll don't take it
                    dragging = true
                    follow()
                }
                MotionEvent.ACTION_MOVE -> follow()
                MotionEvent.ACTION_UP -> {
                    follow()
                    dragging = false
                    onSeek?.invoke(draggedMs)
                    invalidate()
                }
                MotionEvent.ACTION_CANCEL -> {
                    dragging = false
                    invalidate()
                }
            }
            return true
        }
    }
}
