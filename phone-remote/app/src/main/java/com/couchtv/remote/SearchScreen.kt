package com.couchtv.remote

import android.Manifest
import android.app.Activity
import android.app.Dialog
import android.content.pm.PackageManager
import android.graphics.Outline
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.media.MediaRecorder
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.text.Editable
import android.text.TextUtils
import android.text.TextWatcher
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.ViewOutlineProvider
import android.view.WindowInsets
import android.view.WindowManager
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputMethodManager
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.HorizontalScrollView
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import java.io.File
import kotlin.math.sqrt

/**
 * The search page, over the remote (tap the search bar): a box that starts empty, and under it, as you type, films and
 * shows on your apps and songs, from the TV (FIND, FOUND), each played on the TV with a tap (songs also Play next).
 * Enter sends the words to the TV's own search, as before. The music button hums a song: the phone records it, the
 * TV works out which (Gemini) and the guesses show here.
 */
class SearchScreen(private val activity: Activity, private val remote: Remote) {
    companion object {
        const val REQUEST_HUM = 43
        private const val HUM_MS = 10_000L
        private const val TYPING_MS = 350L
    }

    /** The words, to the TV's own search (Enter, or "Search … on the TV"). */
    var onSend: ((String) -> Unit)? = null
    var onPlay: ((Suggestion) -> Unit)? = null
    var onPlayNext: ((Suggestion) -> Unit)? = null
    var onWatch: ((Suggestion) -> Unit)? = null   // a film held down: Add to watchlist
    /** The mic: voice search instead. */
    var onVoice: (() -> Unit)? = null

    private val main = Handler(Looper.getMainLooper())
    private val density = activity.resources.displayMetrics.density
    private fun dp(v: Float) = (v * density + 0.5f).toInt()

    private var dialog: Dialog? = null
    private var input: EditText? = null
    private var list: LinearLayout? = null
    private var humPanel: View? = null
    private var humRings: VoiceRings? = null
    private var humStatus: TextView? = null
    private var humHint: TextView? = null
    private var humAction: TextView? = null

    private var seq = 0                    // the latest FIND or HUM sent; answers to older ones are left
    private var asked = ""                 // the words asked about (FIND)
    private var results: List<Suggestion>? = null
    private var hummed = false             // the results are a hum's guesses
    private var humOnOpen = false
    private var recorder: MediaRecorder? = null
    private var humFile: File? = null
    private var humStarted = 0L
    private var humWaiting = false

    private val typed = Runnable { find() }
    private val humTick = object : Runnable {
        override fun run() {
            val r = recorder ?: return
            val left = HUM_MS - (SystemClock.elapsedRealtime() - humStarted)
            if (left <= 0) {
                finishHum()
                return
            }
            // The rings swell with the sound (amplitude, 0 to 32767, made to look even).
            val amplitude = runCatching { r.maxAmplitude }.getOrDefault(0)
            humRings?.level = sqrt(amplitude / 32767f) * 1.3f
            humHint?.text = activity.getString(R.string.hum_listening_hint, ((left + 999) / 1000).toInt())
            main.postDelayed(this, 80)
        }
    }
    private val humTimeout = Runnable {
        if (humWaiting) humFailed(R.string.hum_failed, R.string.hum_failed_hint)
    }

    // ---------------------------------------------------------------- the page

    /** Opens the page, empty (hum: straight into humming a song). */
    fun open(hum: Boolean = false) {
        if (dialog != null) {
            if (hum) startHum()
            return
        }
        val view = activity.layoutInflater.inflate(R.layout.search_screen, null)
        val shown = Dialog(activity, R.style.SearchScreen)
        shown.setContentView(view)
        shown.window?.let { w ->
            w.setLayout(WindowManager.LayoutParams.MATCH_PARENT, WindowManager.LayoutParams.MATCH_PARENT)
            val keyboard = if (hum) WindowManager.LayoutParams.SOFT_INPUT_STATE_HIDDEN else WindowManager.LayoutParams.SOFT_INPUT_STATE_VISIBLE
            w.setSoftInputMode(resize() or keyboard)
        }
        // Clear of the status bar, the navigation bar and the keyboard (the page is drawn edge to edge).
        val root = view.findViewById<View>(R.id.search_root)
        if (Build.VERSION.SDK_INT >= 30) {
            root.setOnApplyWindowInsetsListener { v, insets ->
                val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.ime())
                v.setPadding(bars.left, bars.top, bars.right, bars.bottom)
                WindowInsets.CONSUMED
            }
        } else root.fitsSystemWindows = true

        val box = view.findViewById<EditText>(R.id.search_input)
        val clear = view.findViewById<View>(R.id.search_clear)
        input = box
        list = view.findViewById(R.id.search_list)
        humPanel = view.findViewById(R.id.hum_panel)
        humRings = view.findViewById<VoiceRings>(R.id.hum_rings).apply { micShare = 108f / 220f }
        humStatus = view.findViewById(R.id.hum_status)
        humHint = view.findViewById(R.id.hum_hint)
        humAction = view.findViewById(R.id.hum_action)
        box.setText("")   // always empty: the last search isn't wanted back
        box.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {}
            override fun afterTextChanged(s: Editable?) {
                clear.visibility = if (s.isNullOrEmpty()) View.GONE else View.VISIBLE
                main.removeCallbacks(typed)
                hummed = false
                main.postDelayed(typed, TYPING_MS)
                render()
            }
        })
        box.setOnEditorActionListener { _, actionId, _ ->
            if (actionId == EditorInfo.IME_ACTION_SEARCH) {
                sendWords()
                true
            } else false
        }
        clear.setOnClickListener {
            box.setText("")
            showKeyboard()
        }
        view.findViewById<View>(R.id.search_back).setOnClickListener { close() }
        view.findViewById<View>(R.id.search_voice).setOnClickListener {
            close()
            onVoice?.invoke()
        }
        view.findViewById<View>(R.id.search_hum).setOnClickListener { startHum() }
        shown.setOnDismissListener { forget() }
        dialog = shown
        seq++
        asked = ""
        results = null
        hummed = false
        render()
        shown.show()
        if (hum) startHum() else box.requestFocus()
    }

    fun close() {
        dialog?.dismiss()
    }

    private fun forget() {
        main.removeCallbacks(typed)
        stopRecording(sendIt = false)
        main.removeCallbacks(humTimeout)
        humWaiting = false
        dialog = null
        input = null
        list = null
        humPanel = null
        humRings = null
        humStatus = null
        humHint = null
        humAction = null
    }

    /** The app went to the background: no recording behind your back. */
    fun stop() {
        if (recorder != null) {
            stopRecording(sendIt = false)
            hideHum()
        }
    }

    private fun showKeyboard() {
        val box = input ?: return
        box.requestFocus()
        activity.getSystemService(InputMethodManager::class.java)?.showSoftInput(box, InputMethodManager.SHOW_IMPLICIT)
    }

    private fun hideKeyboard() {
        val box = input ?: return
        activity.getSystemService(InputMethodManager::class.java)?.hideSoftInputFromWindow(box.windowToken, 0)
    }

    private fun words(): String = input?.text?.toString()?.trim() ?: ""

    private fun sendWords() {
        val text = words()
        if (text.isEmpty()) return
        close()
        onSend?.invoke(text)
    }

    /** Asks the TV what's on for the words (after a pause in the typing). */
    private fun find() {
        val text = words()
        if (text.length < 2 || !remote.wifi.connected || text == asked) return
        asked = text
        results = null
        seq++
        remote.wifi.find(seq, text)
        render()
    }

    /** The TV's answer (FOUND), to the latest FIND or HUM only. */
    fun found(number: Int, items: List<Suggestion>, why: String?) {
        if (number != seq || dialog == null) return
        if (humWaiting) {
            humWaiting = false
            main.removeCallbacks(humTimeout)
            when {
                items.isNotEmpty() -> {
                    hummed = true
                    results = items
                    hideHum()
                    render()
                }
                why == "nokey" -> humFailed(R.string.hum_nokey, R.string.hum_nokey_hint)
                why == "error" -> humFailed(R.string.hum_failed, R.string.hum_failed_hint)
                else -> humFailed(R.string.hum_none, R.string.hum_none_hint)
            }
            return
        }
        results = items
        render()
    }

    // ---------------------------------------------------------------- what's shown under the box

    private fun render() {
        val column = list ?: return
        column.removeAllViews()
        val text = words()
        val found = results
        if (hummed && found != null) {
            heading(column, R.string.search_sounds_like)
            for (s in found) column.addView(songRow(s))
            return
        }
        if (text.isEmpty()) {
            column.addView(TextView(activity).apply {
                setText(R.string.search_empty_title)
                setTextColor(activity.getColor(R.color.ink))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 18f)
                typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
                setPadding(dp(4f), dp(28f), dp(4f), dp(6f))
            })
            column.addView(TextView(activity).apply {
                setText(R.string.search_empty_text)
                setTextColor(activity.getColor(R.color.muted))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f)
                setPadding(dp(4f), 0, dp(4f), 0)
                setLineSpacing(0f, 1.15f)
            })
            return
        }
        // First, the TV's own search for the words (as Enter does).
        column.addView(searchOnTvRow(text))
        if (!remote.wifi.connected) {
            column.addView(note(activity.getString(R.string.search_needs_wifi)))
            return
        }
        if (found == null || text != asked) {
            column.addView(note(activity.getString(R.string.search_looking)))
            return
        }
        val films = found.filter { it.movie }
        val songs = found.filter { !it.movie }
        if (films.isEmpty() && songs.isEmpty()) {
            column.addView(note(activity.getString(R.string.search_nothing, text)))
            return
        }
        if (films.isNotEmpty()) {
            heading(column, R.string.search_films)
            val row = LinearLayout(activity).apply { orientation = LinearLayout.HORIZONTAL }
            for (s in films) row.addView(posterCard(s), LinearLayout.LayoutParams(dp(112f), LinearLayout.LayoutParams.WRAP_CONTENT).apply {
                marginEnd = dp(10f)
            })
            column.addView(HorizontalScrollView(activity).apply {
                isHorizontalScrollBarEnabled = false
                clipToPadding = false
                addView(row)
            }, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT))
        }
        if (songs.isNotEmpty()) {
            heading(column, R.string.search_songs)
            for (s in songs) column.addView(songRow(s))
        }
    }

    private fun heading(column: LinearLayout, text: Int) {
        column.addView(TextView(activity).apply {
            setText(text)
            setTextColor(activity.getColor(R.color.ink))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 16f)
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            setPadding(dp(4f), dp(22f), dp(4f), dp(10f))
        })
    }

    private fun note(text: String) = TextView(activity).apply {
        this.text = text
        setTextColor(activity.getColor(R.color.muted))
        setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
        setPadding(dp(4f), dp(14f), dp(4f), 0)
    }

    /** "Search “…” on the TV": the TV's own search, which picks the app. */
    private fun searchOnTvRow(text: String): View {
        val row = LinearLayout(activity).apply {
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(4f), dp(10f), dp(4f), dp(10f))
            background = activity.getDrawable(R.drawable.row_bg)
            isClickable = true
            setOnClickListener { sendWords() }
        }
        row.addView(ImageView(activity).apply {
            setImageResource(R.drawable.ic_search)
            imageTintList = android.content.res.ColorStateList.valueOf(activity.getColor(R.color.ink))
            background = GradientDrawable().apply {
                shape = GradientDrawable.OVAL
                setColor(activity.getColor(R.color.surface_raised))
            }
            setPadding(dp(10f), dp(10f), dp(10f), dp(10f))
        }, LinearLayout.LayoutParams(dp(40f), dp(40f)))
        val words = LinearLayout(activity).apply { orientation = LinearLayout.VERTICAL }
        words.addView(TextView(activity).apply {
            this.text = activity.getString(R.string.search_on_tv, text)
            setTextColor(activity.getColor(R.color.ink))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f)
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
        })
        words.addView(TextView(activity).apply {
            setText(R.string.search_on_tv_note)
            setTextColor(activity.getColor(R.color.muted))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f)
        })
        row.addView(words, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply { marginStart = dp(14f) })
        row.addView(ImageView(activity).apply {
            setImageResource(R.drawable.ic_chevron_right)
            imageTintList = android.content.res.ColorStateList.valueOf(activity.getColor(R.color.muted))
        }, LinearLayout.LayoutParams(dp(22f), dp(22f)))
        return row
    }

    /** A film or show: its poster with its app on it, its name and year under it. A tap plays it on the TV. */
    private fun posterCard(s: Suggestion): View {
        val poster = ImageView(activity).apply {
            scaleType = ImageView.ScaleType.CENTER_CROP
            background = GradientDrawable().apply {
                cornerRadius = 12 * density
                setColor(activity.getColor(R.color.surface))
            }
            outlineProvider = rounded(12f)
            clipToOutline = true
        }
        s.image?.let { SuggestionsView.Pictures.load(activity, it, poster, dp(160f)) }
        val badge = TextView(activity).apply {
            text = s.app
            setTextColor(s.appTextColor)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 10f)
            typeface = Typeface.DEFAULT_BOLD
            maxLines = 1
            setPadding(dp(6f), dp(2f), dp(6f), dp(3f))
            background = GradientDrawable().apply {
                cornerRadius = 5 * density
                setColor(s.appColor)
            }
        }
        val frame = FrameLayout(activity)
        frame.addView(poster, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, dp(168f)))
        frame.addView(badge, FrameLayout.LayoutParams(FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.TOP or Gravity.START).apply {
            setMargins(dp(6f), dp(6f), 0, 0)
        })
        return LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            isClickable = true
            stateListAnimator = android.animation.AnimatorInflater.loadStateListAnimator(activity, R.animator.card_press)
            addView(frame)
            addView(TextView(activity).apply {
                text = s.title
                setTextColor(activity.getColor(R.color.ink))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
                maxLines = 1
                ellipsize = TextUtils.TruncateAt.END
                setPadding(dp(2f), dp(6f), dp(2f), 0)
            })
            if (s.subtitle.isNotEmpty()) addView(TextView(activity).apply {
                text = s.subtitle
                setTextColor(activity.getColor(R.color.muted))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 11f)
                maxLines = 1
                ellipsize = TextUtils.TruncateAt.END
                setPadding(dp(2f), 0, dp(2f), 0)
            })
            setOnClickListener {
                Haptics.press(it)
                close()
                onPlay?.invoke(s)
            }
            // Held down: play it now, or pick when to watch it (the watchlist).
            setOnLongClickListener { v ->
                Haptics.press(v)
                val listed = Watchlist.get(activity).find(s) != null
                android.widget.PopupMenu(activity, v).apply {
                    menu.add(0, 1, 0, R.string.play_now)
                    menu.add(0, 2, 1, if (listed) R.string.watch_change else R.string.watch_add_to)
                    setOnMenuItemClickListener { item ->
                        if (item.itemId == 1) {
                            close()
                            onPlay?.invoke(s)
                        } else onWatch?.invoke(s)
                        true
                    }
                }.show()
                true
            }
        }
    }

    /** A song: its cover, title and artist; a tap plays it on the TV, the button plays it next. */
    private fun songRow(s: Suggestion): View {
        val row = LinearLayout(activity).apply {
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(4f), dp(7f), dp(2f), dp(7f))
            background = activity.getDrawable(R.drawable.row_bg)
            isClickable = true
            setOnClickListener {
                Haptics.press(it)
                close()
                onPlay?.invoke(s)
            }
        }
        val cover = ImageView(activity).apply {
            scaleType = ImageView.ScaleType.CENTER_CROP
            setImageResource(R.drawable.ic_music)
            background = GradientDrawable().apply {
                cornerRadius = 10 * density
                setColor(activity.getColor(R.color.surface_raised))
            }
            outlineProvider = rounded(10f)
            clipToOutline = true
        }
        s.image?.let { SuggestionsView.Pictures.load(activity, it, cover, dp(120f)) }
        row.addView(cover, LinearLayout.LayoutParams(dp(54f), dp(54f)))
        val words = LinearLayout(activity).apply { orientation = LinearLayout.VERTICAL }
        words.addView(TextView(activity).apply {
            text = s.title
            setTextColor(activity.getColor(R.color.ink))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f)
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
        })
        if (s.subtitle.isNotEmpty()) words.addView(TextView(activity).apply {
            text = s.subtitle
            setTextColor(activity.getColor(R.color.muted))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
        })
        row.addView(words, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply { marginStart = dp(14f) })
        row.addView(ImageView(activity).apply {
            setImageResource(R.drawable.ic_queue_add)
            imageTintList = android.content.res.ColorStateList.valueOf(activity.getColor(R.color.ink))
            background = activity.getDrawable(R.drawable.round_bg)
            setPadding(dp(11f), dp(11f), dp(11f), dp(11f))
            contentDescription = activity.getString(R.string.play_next)
            setOnClickListener {
                Haptics.press(it)
                it.animate().scaleX(1.2f).scaleY(1.2f).setDuration(110).withEndAction { it.animate().scaleX(1f).scaleY(1f).setDuration(200).start() }.start()
                onPlayNext?.invoke(s)
            }
        }, LinearLayout.LayoutParams(dp(44f), dp(44f)))
        return row
    }

    @Suppress("DEPRECATION")
    private fun resize() = WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE

    @Suppress("DEPRECATION")
    private fun newRecorder(): MediaRecorder = if (Build.VERSION.SDK_INT >= 31) MediaRecorder(activity) else MediaRecorder()

    private fun rounded(radius: Float) = object : ViewOutlineProvider() {
        override fun getOutline(view: View, outline: Outline) = outline.setRoundRect(0, 0, view.width, view.height, radius * density)
    }

    // ---------------------------------------------------------------- hum a song

    private fun startHum() {
        if (dialog == null) {
            open(hum = true)
            return
        }
        if (!remote.wifi.connected) {
            Toast.makeText(activity, R.string.hum_needs_wifi, Toast.LENGTH_SHORT).show()
            return
        }
        if (activity.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            activity.requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), REQUEST_HUM)
            return
        }
        hideKeyboard()
        main.removeCallbacks(humTimeout)
        humWaiting = false
        val file = File(activity.cacheDir, "hum.aac")
        file.delete()
        val r = try {
            newRecorder().apply {
                setAudioSource(MediaRecorder.AudioSource.MIC)
                setOutputFormat(MediaRecorder.OutputFormat.AAC_ADTS)
                setAudioEncoder(MediaRecorder.AudioEncoder.AAC)
                setAudioChannels(1)
                setAudioSamplingRate(22050)
                setAudioEncodingBitRate(32000)
                setOutputFile(file.path)
                prepare()
                start()
            }
        } catch (e: Exception) {
            Toast.makeText(activity, R.string.voice_unavailable, Toast.LENGTH_SHORT).show()
            return
        }
        recorder = r
        humFile = file
        humStarted = SystemClock.elapsedRealtime()
        remote.press(Codes.ADDRESS, Codes.SEARCH, hold = false)   // the TV turns its sound down meanwhile
        showHum(R.string.hum_listening, activity.getString(R.string.hum_listening_hint, (HUM_MS / 1000).toInt()), R.string.hum_done) { finishHum() }
        humRings?.active = true
        main.post(humTick)
    }

    /** Recorded (ten seconds, or Done): off to the TV, which says which song it is. */
    private fun finishHum() {
        val long = SystemClock.elapsedRealtime() - humStarted
        val audio = stopRecording(sendIt = true)
        if (audio == null || long < 2500) {
            humFailed(R.string.hum_too_short, activity.getString(R.string.hum_listening_hint, (HUM_MS / 1000).toInt()))
            return
        }
        seq++
        humWaiting = true
        remote.wifi.hum(seq, audio)
        showHum(R.string.hum_finding, activity.getString(R.string.hum_finding_hint), 0, null)
        main.postDelayed(humTimeout, 40_000)
    }

    /** Stops recording; with [sendIt], hands back what was recorded (null if it didn't work). */
    private fun stopRecording(sendIt: Boolean): ByteArray? {
        main.removeCallbacks(humTick)
        val r = recorder ?: return null
        recorder = null
        humRings?.active = false
        remote.press(Codes.ADDRESS, Codes.SEARCH_CANCEL, hold = false)   // the TV's sound back up
        val ok = runCatching { r.stop() }.isSuccess
        r.release()
        val file = humFile
        humFile = null
        if (!sendIt || !ok || file == null) {
            file?.delete()
            return null
        }
        val bytes = runCatching { file.readBytes() }.getOrNull()
        file.delete()
        return bytes?.takeIf { it.size in 1..600 * 1024 }
    }

    private fun humFailed(status: Int, hint: Int) = humFailed(status, activity.getString(hint))

    private fun humFailed(status: Int, hint: String) {
        humWaiting = false
        main.removeCallbacks(humTimeout)
        showHum(status, hint, R.string.hum_again) { startHum() }
    }

    private fun showHum(status: Int, hint: String, action: Int, act: (() -> Unit)?) {
        val panel = humPanel ?: return
        if (panel.visibility != View.VISIBLE) {
            panel.alpha = 0f
            panel.visibility = View.VISIBLE
            panel.animate().alpha(1f).setDuration(200).start()
        }
        humStatus?.setText(status)
        humHint?.text = hint
        humAction?.let { chip ->
            if (action == 0 || act == null) chip.visibility = View.INVISIBLE
            else {
                chip.visibility = View.VISIBLE
                chip.setText(action)
                chip.setOnClickListener { act() }
            }
        }
    }

    private fun hideHum() {
        val panel = humPanel ?: return
        if (panel.visibility != View.VISIBLE) return
        panel.animate().alpha(0f).setDuration(180).withEndAction { panel.visibility = View.GONE }.start()
    }

    /** Android's answer to the microphone question (asked for humming). */
    fun onPermissionResult(granted: Boolean) {
        if (granted) startHum()
    }
}
