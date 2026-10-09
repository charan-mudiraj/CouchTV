package com.couchtv.remote

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Bundle
import android.speech.RecognitionListener
import android.speech.RecognizerIntent
import android.speech.SpeechRecognizer
import android.text.InputType
import android.view.LayoutInflater
import android.view.WindowManager
import android.view.inputmethod.EditorInfo
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.TextView
import android.widget.Toast

/**
 * Voice search. The phone turns speech into text with Google's speech recognition (which knows Indian English and
 * Hindi), then sends the text to CouchTV over infrared. While the phone listens, the TV shows "Listening…" and
 * turns its sound down. If this phone's recognizer doesn't work, Google's own voice screen is used instead.
 */
class VoiceSearch(private val activity: Activity, private val remote: Remote) {
    companion object {
        const val REQUEST_MIC = 41
        const val REQUEST_POPUP = 42
        private val LANGUAGES = listOf("en-IN", "hi-IN")
        private const val ERROR_SERVER_DISCONNECTED = 11   // SpeechRecognizer constant from Android 12
    }

    /** Shows progress, e.g. in the search bar. */
    var onStatus: ((String) -> Unit)? = null

    private val prefs = activity.getSharedPreferences("voice", Context.MODE_PRIVATE)
    private var recognizer: SpeechRecognizer? = null
    private var panel: AlertDialog? = null
    private var mic: ImageView? = null
    private var status: TextView? = null
    private var heard: TextView? = null
    private var languageButton: Button? = null
    private var listening = false

    private var language: String
        get() = prefs.getString("language", null)?.takeIf { it in LANGUAGES } ?: LANGUAGES[0]
        set(value) = prefs.edit().putString("language", value).apply()

    private var lastText: String
        get() = prefs.getString("last", null) ?: ""
        set(value) = prefs.edit().putString("last", value).apply()

    // ---------------------------------------------------------------- starting

    /** The mic button. */
    fun start() {
        if (activity.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            activity.requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), REQUEST_MIC)
            return
        }
        if (!SpeechRecognizer.isRecognitionAvailable(activity)) {
            startPopup()
            return
        }
        showPanel()
        listen()
    }

    /** Google's voice screen doesn't need this app's microphone permission, so it's the fallback. */
    fun onPermissionResult(granted: Boolean) {
        if (granted) start() else startPopup()
    }

    /** Back from Google's voice screen. Returns false for other requests. */
    fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?): Boolean {
        if (requestCode != REQUEST_POPUP) return false
        val text = data?.getStringArrayListExtra(RecognizerIntent.EXTRA_RESULTS)?.firstOrNull()?.trim()
        if (resultCode == Activity.RESULT_OK && !text.isNullOrEmpty()) send(text)
        else remote.press(Codes.ADDRESS, Codes.SEARCH_CANCEL, hold = false)
        return true
    }

    /** The app went to the background: stop listening (the TV turns its sound back up). */
    fun stop() {
        if (panel != null) cancel()
    }

    fun release() {
        recognizer?.destroy()
        recognizer = null
    }

    private fun startPopup() {
        remote.press(Codes.ADDRESS, Codes.SEARCH, hold = false)
        val intent = recognizerIntent().putExtra(RecognizerIntent.EXTRA_PROMPT, activity.getString(R.string.voice_prompt))
        try {
            activity.startActivityForResult(intent, REQUEST_POPUP)
        } catch (e: ActivityNotFoundException) {
            remote.press(Codes.ADDRESS, Codes.SEARCH_CANCEL, hold = false)
            Toast.makeText(activity, R.string.voice_unavailable, Toast.LENGTH_LONG).show()
            type()
        }
    }

    private fun recognizerIntent() = Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
        putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_WEB_SEARCH)
        putExtra(RecognizerIntent.EXTRA_LANGUAGE, language)
        putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true)
        putExtra(RecognizerIntent.EXTRA_MAX_RESULTS, 1)
        putExtra(RecognizerIntent.EXTRA_CALLING_PACKAGE, activity.packageName)
    }

    // ---------------------------------------------------------------- the listening panel

    private fun showPanel() {
        if (panel != null) return
        val view = LayoutInflater.from(activity).inflate(R.layout.voice_panel, null)
        mic = view.findViewById(R.id.voice_mic)
        status = view.findViewById(R.id.voice_status)
        heard = view.findViewById(R.id.voice_heard)
        languageButton = view.findViewById(R.id.voice_language)
        // Tap the mic to finish early, or to try again after a miss.
        mic?.setOnClickListener { if (listening) recognizer?.stopListening() else listen() }
        languageButton?.setOnClickListener { nextLanguage() }
        view.findViewById<Button>(R.id.voice_type).setOnClickListener {
            stopListening()
            closePanel()
            type()
        }
        view.findViewById<Button>(R.id.voice_cancel).setOnClickListener { cancel() }
        showLanguage()
        panel = AlertDialog.Builder(activity, android.R.style.Theme_Material_Dialog_Alert)
            .setView(view)
            .setOnCancelListener { cancel() }
            .show()
    }

    private fun listen() {
        recognizer?.destroy()
        val created = try {
            SpeechRecognizer.createSpeechRecognizer(activity)
        } catch (e: Exception) {
            null
        }
        if (created == null) {
            closePanel()
            startPopup()
            return
        }
        recognizer = created
        created.setRecognitionListener(listener)
        listening = true
        heard?.text = ""
        status?.setText(R.string.voice_starting)
        remote.press(Codes.ADDRESS, Codes.SEARCH, hold = false)   // the TV shows "Listening…" and turns down
        created.startListening(recognizerIntent())
    }

    private fun stopListening() {
        listening = false
        recognizer?.cancel()
        resetMic()
    }

    /** Closed without words: the TV closes search and turns its sound back up. */
    private fun cancel() {
        stopListening()
        closePanel()
        remote.press(Codes.ADDRESS, Codes.SEARCH_CANCEL, hold = false)
    }

    private fun closePanel() {
        val shown = panel ?: return
        panel = null
        shown.setOnCancelListener(null)
        shown.dismiss()
        recognizer?.destroy()
        recognizer = null
        mic = null
        status = null
        heard = null
        languageButton = null
    }

    private fun nextLanguage() {
        language = LANGUAGES[(LANGUAGES.indexOf(language) + 1) % LANGUAGES.size]
        showLanguage()
        if (listening) {
            stopListening()
            listen()
        }
    }

    private fun showLanguage() {
        languageButton?.setText(if (language == "hi-IN") R.string.language_hindi else R.string.language_english)
    }

    private fun resetMic() {
        mic?.scaleX = 1f
        mic?.scaleY = 1f
    }

    /** A miss the user can retry from the panel by tapping the mic. */
    private fun missed(message: Int) {
        listening = false
        resetMic()
        status?.setText(message)
    }

    private val listener = object : RecognitionListener {
        override fun onReadyForSpeech(params: Bundle?) {
            status?.setText(R.string.voice_listening)
        }

        override fun onBeginningOfSpeech() {}

        override fun onRmsChanged(rmsdB: Float) {
            val scale = 1f + rmsdB.coerceIn(0f, 10f) / 10f * 0.3f   // the mic grows with your voice
            mic?.scaleX = scale
            mic?.scaleY = scale
        }

        override fun onBufferReceived(buffer: ByteArray?) {}

        override fun onEndOfSpeech() {
            resetMic()
            status?.setText(R.string.voice_thinking)
        }

        override fun onError(error: Int) {
            if (!listening) return   // e.g. the "client" error some phones report after cancel()
            when (error) {
                SpeechRecognizer.ERROR_NO_MATCH, SpeechRecognizer.ERROR_SPEECH_TIMEOUT -> missed(R.string.voice_no_match)
                SpeechRecognizer.ERROR_NETWORK, SpeechRecognizer.ERROR_NETWORK_TIMEOUT, ERROR_SERVER_DISCONNECTED -> missed(R.string.voice_no_internet)
                SpeechRecognizer.ERROR_INSUFFICIENT_PERMISSIONS -> {
                    stopListening()
                    closePanel()
                    activity.requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), REQUEST_MIC)
                }
                else -> {
                    // This phone's recognizer didn't work (busy, missing a language, ...): use Google's screen.
                    stopListening()
                    closePanel()
                    startPopup()
                }
            }
        }

        override fun onResults(results: Bundle?) {
            if (!listening) return
            listening = false
            val text = results?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull()?.trim()
            if (text.isNullOrEmpty()) {
                missed(R.string.voice_no_match)
                return
            }
            closePanel()
            send(text)
        }

        override fun onPartialResults(partialResults: Bundle?) {
            val text = partialResults?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull()
            if (!text.isNullOrBlank()) heard?.text = text
        }

        override fun onEvent(eventType: Int, params: Bundle?) {}
    }

    // ---------------------------------------------------------------- typing, and sending

    /** Typing instead of speaking (tap the search bar). */
    fun type() {
        val density = activity.resources.displayMetrics.density
        val input = EditText(activity).apply {
            setText(lastText)
            selectAll()
            hint = activity.getString(R.string.type_hint)
            inputType = InputType.TYPE_CLASS_TEXT
            imeOptions = EditorInfo.IME_ACTION_SEARCH
            setSingleLine()
        }
        val frame = FrameLayout(activity).apply {
            setPadding((20 * density).toInt(), (8 * density).toInt(), (20 * density).toInt(), 0)
            addView(input)
        }
        val shown = AlertDialog.Builder(activity, android.R.style.Theme_Material_Dialog_Alert)
            .setTitle(R.string.type_title)
            .setView(frame)
            .setPositiveButton(R.string.search, null)
            .setNegativeButton(R.string.cancel) { _, _ -> remote.press(Codes.ADDRESS, Codes.SEARCH_CANCEL, hold = false) }
            .show()
        val submit = {
            val text = input.text.toString().trim()
            if (text.isNotEmpty()) {
                shown.dismiss()
                send(text)
            }
        }
        shown.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener { submit() }
        input.setOnEditorActionListener { _, actionId, _ ->
            if (actionId == EditorInfo.IME_ACTION_SEARCH) {
                submit()
                true
            } else false
        }
        input.requestFocus()
        shown.window?.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_STATE_VISIBLE)
    }

    /** Sends the words to the TV, which shows them and lets you pick the app to search in. */
    private fun send(text: String) {
        val words = Nec.fitText(text)
        if (words.isEmpty()) return
        lastText = words
        if (!remote.viaWifi) onStatus?.invoke(activity.getString(R.string.search_sending, words))   // infrared takes a second
        remote.sendText(words) {
            activity.runOnUiThread { onStatus?.invoke(activity.getString(R.string.search_sent, words)) }
        }
    }
}
