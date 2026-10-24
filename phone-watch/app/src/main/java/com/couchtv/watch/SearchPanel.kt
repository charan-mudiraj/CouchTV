package com.couchtv.watch

import android.annotation.SuppressLint
import android.content.Context
import android.content.res.ColorStateList
import android.os.Handler
import android.os.Looper
import android.text.Editable
import android.text.InputType
import android.text.TextWatcher
import android.util.TypedValue
import android.view.Gravity
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputMethodManager
import android.widget.EditText
import android.widget.GridLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import java.util.concurrent.Executors

/**
 * The search page, over the home: films and series on your apps as you type (JustWatch's search, as the TV's), three
 * posters a row; a tap puts one at the top of the home, to watch.
 */
@SuppressLint("ViewConstructor")
class SearchPanel(context: Context, private val catalog: Catalog, private val apps: () -> List<StreamApp>) : LinearLayout(context) {
    /** A title tapped. */
    var onPick: ((Title) -> Unit)? = null
    /** The page went away. */
    var onClose: (() -> Unit)? = null

    private val box = EditText(context)
    private val note: TextView
    private val grid = GridLayout(context)
    private val main = Handler(Looper.getMainLooper())
    private val pool = Executors.newSingleThreadExecutor()
    private var asked = 0
    private val searchSoon = Runnable { search() }

    init {
        orientation = VERTICAL
        setBackgroundColor(Look.BACKGROUND)
        isClickable = true   // touches stay here, off the home beneath

        val top = LinearLayout(context).apply {
            orientation = HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(context.dp(4), context.dp(6), context.dp(16), context.dp(6))
        }
        val back = ImageView(context).apply {
            setImageResource(R.drawable.ic_arrow_back)
            imageTintList = ColorStateList.valueOf(Look.INK)
            scaleType = ImageView.ScaleType.CENTER
            background = pressable(null)
            contentDescription = "Back"
            setOnClickListener { close() }
        }
        top.addView(back, LayoutParams(context.dp(48), context.dp(48)))
        box.apply {
            hint = "Films and series on your apps"
            setHintTextColor(Look.FAINT)
            setTextColor(Look.INK)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 16f)
            background = rounded(Look.SURFACE, context.dpf(22f))
            setPadding(context.dp(18), 0, context.dp(18), 0)
            isSingleLine = true
            inputType = InputType.TYPE_CLASS_TEXT
            imeOptions = EditorInfo.IME_ACTION_SEARCH
            setOnEditorActionListener { _, _, _ ->
                main.removeCallbacks(searchSoon)
                search()
                hideKeyboard()
                true
            }
            addTextChangedListener(object : TextWatcher {
                override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) = Unit
                override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) = Unit
                override fun afterTextChanged(s: Editable?) {
                    main.removeCallbacks(searchSoon)
                    main.postDelayed(searchSoon, 450)
                }
            })
        }
        top.addView(box, LayoutParams(0, context.dp(44), 1f))
        addView(top, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT))

        note = context.label(14f, Look.MUTED).apply { setPadding(context.dp(20), context.dp(10), context.dp(20), context.dp(6)) }
        addView(note, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT))

        grid.columnCount = 3
        val scroll = ScrollView(context).apply {
            isVerticalScrollBarEnabled = false
            clipToPadding = false
            setPadding(context.dp(14), 0, context.dp(14), context.dp(24))
            addView(grid)
        }
        addView(scroll, LayoutParams(LayoutParams.MATCH_PARENT, 0, 1f))
    }

    fun open() {
        visibility = VISIBLE
        box.requestFocus()
        box.post {
            (context.getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager).showSoftInput(box, InputMethodManager.SHOW_IMPLICIT)
        }
    }

    fun close() {
        hideKeyboard()
        visibility = GONE
        onClose?.invoke()
    }

    private fun hideKeyboard() {
        (context.getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager).hideSoftInputFromWindow(box.windowToken, 0)
    }

    private fun search() {
        val words = box.text.toString().trim()
        val number = ++asked
        if (words.length < 2) {
            grid.removeAllViews()
            note.text = ""
            return
        }
        note.text = "Looking…"
        val yours = apps()
        pool.execute {
            val found = runCatching { catalog.search(words, yours) }
            main.post {
                if (number != asked) return@post   // more was typed meanwhile
                found.onSuccess { show(it, words) }.onFailure { note.text = "Couldn't search: ${it.message}" }
            }
        }
    }

    private fun show(list: List<Title>, words: String) {
        grid.removeAllViews()
        note.text = if (list.isEmpty()) "Nothing on your apps called “$words”" else ""
        val gap = context.dp(4)
        val cell = (resources.displayMetrics.widthPixels - context.dp(28) - gap * 6) / 3
        for (t in list) {
            val poster = PosterView(context, t, square = false)
            poster.setOnClickListener {
                hideKeyboard()
                onPick?.invoke(t)
            }
            grid.addView(poster, GridLayout.LayoutParams().apply {
                width = cell
                height = LayoutParams.WRAP_CONTENT
                setMargins(gap, gap, gap, gap)
            })
        }
    }
}
