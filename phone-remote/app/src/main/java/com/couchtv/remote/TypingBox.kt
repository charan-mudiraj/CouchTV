package com.couchtv.remote

import android.app.Activity
import android.app.AlertDialog
import android.text.Editable
import android.text.InputType
import android.text.TextWatcher
import android.view.Gravity
import android.view.KeyEvent
import android.view.WindowManager
import android.view.inputmethod.EditorInfo
import android.widget.EditText
import android.widget.LinearLayout

/**
 * Typing into a text box on the TV with this phone's own keyboard (any language, swipe or voice typing): it pops up
 * when a text box there gets the focus (CouchTV says so: "INPUT"), or from the keyboard page's Type button. It starts
 * with what the TV's box holds, and each change goes over as it's typed, as backspaces and new text ("EDIT"), so the
 * two stay the same; the TV's cursor is first moved to the end of its box (Ctrl+End). Enter here is Enter there.
 */
class TypingBox(private val activity: Activity, private val wifi: WifiLink) {
    private companion object {
        const val VK_BACK = 0x08
        const val VK_ENTER = 0x0D
        const val VK_CONTROL = 0xA2
        const val VK_END = 0x23
        const val MOST_PER_LINE = 500
    }

    private var dialog: AlertDialog? = null
    private lateinit var input: EditText
    private var sent = ""            // what the TV's box holds, as far as this phone knows
    private var atEnd = false        // the TV's cursor was moved to the end of its box
    private var settingText = false  // changing the text here, not typing

    val showing: Boolean get() = dialog?.isShowing == true

    /** Shows it for [field] (null: whatever has the focus on the TV, typed at its cursor), or refreshes it. */
    fun show(field: WifiLink.TypingField?) {
        val box = dialog ?: create()
        box.setTitle(if (field != null && field.name.isNotBlank()) activity.getString(R.string.typing_into, field.name) else activity.getString(R.string.typing_title))
        settingText = true
        input.inputType = if (field?.password == true) InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
                          else InputType.TYPE_CLASS_TEXT
        input.setText(field?.value ?: "")
        input.setSelection(input.text.length)
        settingText = false
        sent = field?.value ?: ""
        atEnd = false
        if (!box.isShowing) box.show()
        input.requestFocus()
    }

    /** The text box on the TV lost the focus, or the link dropped. */
    fun close() {
        dialog?.dismiss()
    }

    private fun create(): AlertDialog {
        val padding = (20 * activity.resources.displayMetrics.density).toInt()
        input = EditText(activity).apply {
            setSingleLine()
            imeOptions = EditorInfo.IME_ACTION_GO
            setOnEditorActionListener { _, action, event ->
                if (action == EditorInfo.IME_ACTION_GO || event?.keyCode == KeyEvent.KEYCODE_ENTER) {
                    if (event == null || event.action == KeyEvent.ACTION_DOWN) tap(VK_ENTER)
                    true
                } else false
            }
            // Backspace with nothing left here still deletes on the TV (what was there before, say).
            setOnKeyListener { _, keyCode, event ->
                if (keyCode == KeyEvent.KEYCODE_DEL && event.action == KeyEvent.ACTION_DOWN && text.isEmpty()) {
                    tap(VK_BACK)
                    true
                } else false
            }
            addTextChangedListener(object : TextWatcher {
                override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
                override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {}
                override fun afterTextChanged(s: Editable?) {
                    if (!settingText) changed(s?.toString() ?: "")
                }
            })
        }
        val form = LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(padding, padding / 2, padding, 0)
            addView(input)
        }
        val box = AlertDialog.Builder(activity, android.R.style.Theme_Material_Dialog_Alert)
            .setTitle(R.string.typing_title)
            .setView(form)
            .setPositiveButton(R.string.typing_enter, null)
            .setNeutralButton(R.string.typing_clear, null)
            .setNegativeButton(R.string.done, null)
            .create()
        box.window?.let {
            it.setGravity(Gravity.TOP)   // up out of the keyboard's way
            it.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_VISIBLE)
        }
        box.setOnShowListener {
            // These two keep the box open.
            box.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener { tap(VK_ENTER) }
            box.getButton(AlertDialog.BUTTON_NEUTRAL).setOnClickListener { input.setText("") }
        }
        dialog = box
        return box
    }

    /** The text here changed: sends the difference, as backspaces for what went and the text that came. */
    private fun changed(now: String) {
        var same = 0
        val most = minOf(now.length, sent.length)
        while (same < most && now[same] == sent[same]) same++
        if (same > 0 && same < sent.length && Character.isHighSurrogate(sent[same - 1])) same--   // not half an emoji
        val gone = sent.codePointCount(same, sent.length)   // a backspace deletes a character, not half of one
        val added = now.substring(same)
        if (gone == 0 && added.isEmpty()) return
        if (!atEnd && sent.isNotEmpty()) {
            // Typing goes where the TV's cursor is: put it after the text this box started with.
            wifi.key(VK_CONTROL, true)
            wifi.key(VK_END, true)
            wifi.key(VK_END, false)
            wifi.key(VK_CONTROL, false)
        }
        atEnd = true
        if (added.isEmpty()) wifi.edit(gone, "")
        else added.chunked(MOST_PER_LINE).forEachIndexed { i, part -> wifi.edit(if (i == 0) gone else 0, part) }
        sent = now
    }

    private fun tap(vk: Int) {
        wifi.key(vk, true)
        wifi.key(vk, false)
    }
}
