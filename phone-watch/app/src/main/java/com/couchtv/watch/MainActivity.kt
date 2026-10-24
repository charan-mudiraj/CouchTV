package com.couchtv.watch

import android.app.Activity
import android.content.Intent
import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.PorterDuff
import android.graphics.drawable.ColorDrawable
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.text.TextUtils
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.WindowInsets
import android.widget.FrameLayout
import android.widget.HorizontalScrollView
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import java.util.concurrent.Executors

/**
 * CouchTV Watch: what to watch from all your apps, laid out as the TV's home screen: tabs for All and each app along
 * the top, the chosen title big under them (its picture, then its trailer, with sound), and rows of posters below;
 * a tap on a poster puts it at the top. Watch opens it in its own app on this phone; Play on TV hands it to CouchTV
 * Remote, which turns the TV on and plays it there.
 */
class MainActivity : Activity() {
    companion object {
        private const val MATCH = ViewGroup.LayoutParams.MATCH_PARENT
        private const val WRAP = ViewGroup.LayoutParams.WRAP_CONTENT
        private const val TRAILER_DELAY_MS = 1200L
        private const val UPDATE_EVERY_MS = 6 * 3600 * 1000L
    }

    private lateinit var catalog: Catalog
    private lateinit var trailers: Trailers
    private lateinit var updater: AppUpdater
    private val main = Handler(Looper.getMainLooper())
    private val pool = Executors.newFixedThreadPool(2)

    private var apps: List<StreamApp> = emptyList()
    private var tab = "all"                  // "all", or an app's key
    private var hero: Title? = null          // the title at the top (null: an app's tab with nothing listed, or nothing yet)
    private var resumed = false
    private var muted = false                // the trailer's sound, off with a tap on it
    private var trailerOn = false            // a trailer's been asked to play in the hero
    private var scrolledAway = false         // it stopped as the hero went off the screen: it starts again on the way back
    private val lookedFor = HashSet<String>()   // titles whose trailer was looked for on YouTube while the app's open
    private var lastUpdateCheck = 0L
    private var release: AppUpdater.Release? = null

    private lateinit var root: FrameLayout
    private lateinit var tabsRow: LinearLayout
    private lateinit var scroll: ScrollView
    private lateinit var media: RatioFrame
    private lateinit var backdrop: ImageView
    private lateinit var trailer: TrailerView
    private lateinit var sound: ImageView
    private lateinit var heroTitle: TextView
    private lateinit var heroMeta: TextView
    private lateinit var heroGenres: TextView
    private lateinit var heroText: TextView
    private lateinit var watchButton: PillButton
    private lateinit var tvButton: PillButton
    private lateinit var rowsBox: LinearLayout
    private lateinit var updateBar: TextView
    private lateinit var search: SearchPanel
    private lateinit var settings: SettingsPanel
    private val posters = ArrayList<PosterView>()

    private val startTrailer = Runnable { playTrailer() }

    // From a PC over adb (TestHooks): taps and swipes, and "tab <key>", "pick <n>" (the nth title of the tab), "watch",
    // "play <url>" (an address in the player).
    private val tests = TestHooks(this) { command ->
        val p = command.split(" ").filter { it.isNotEmpty() }
        when (p.firstOrNull()) {
            "tab" -> p.size >= 2 && run { pickTab(p[1]); true }
            "pick" -> p.size >= 2 && run {
                val t = rowsFor(tab).flatMap { it.items }.getOrNull(p[1].toIntOrNull() ?: -1)
                if (t != null) choose(t, scrollUp = true)
                t != null
            }
            "watch" -> run { watch(); true }
            "play" -> p.size >= 2 && run { PlayerActivity.open(this, p[1]); true }
            else -> false
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // The lists first: the screen's search page asks them.
        catalog = Catalog(this)
        trailers = Trailers(this)
        updater = AppUpdater(this)
        apps = Apps.yours(this)
        buildScreen()
        setContentView(root)
        catalog.onChange = { showTab(keepHero = true) }
        showTabs()
        showTab(keepHero = false)
        InstallReceiver.onFailure = { message ->
            updateBar.isEnabled = true
            updateBar.text = message
            updateBar.visibility = View.VISIBLE
        }
    }

    override fun onResume() {
        super.onResume()
        resumed = true
        tests.start()
        catalog.refresh(apps, force = false)
        scheduleTrailer()
        checkForUpdate(manual = false)
    }

    override fun onPause() {
        super.onPause()
        resumed = false
        tests.stop()
        stopTrailer()
    }

    override fun onDestroy() {
        super.onDestroy()
        InstallReceiver.onFailure = null
        trailer.destroy()
    }

    /** Back: a page away, then up to the top, then to All, then out. */
    @Deprecated("Deprecated in Java")
    @Suppress("DEPRECATION")
    override fun onBackPressed() {
        when {
            search.visibility == View.VISIBLE -> search.close()
            settings.visibility == View.VISIBLE -> settings.close()
            scroll.scrollY > 0 -> scroll.smoothScrollTo(0, 0)
            tab != "all" -> pickTab("all")
            else -> super.onBackPressed()
        }
    }

    // ---------------------------------------------------------------- the screen

    private fun buildScreen() {
        root = FrameLayout(this).apply { setBackgroundColor(Look.BACKGROUND) }
        // Clear of the status bar, the navigation bar and the keyboard (the screen is drawn edge to edge).
        if (Build.VERSION.SDK_INT >= 30) {
            window.setDecorFitsSystemWindows(false)
            root.setOnApplyWindowInsetsListener { v, insets ->
                val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.ime())
                v.setPadding(bars.left, bars.top, bars.right, bars.bottom)
                WindowInsets.CONSUMED
            }
        } else root.fitsSystemWindows = true

        val column = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        root.addView(column, FrameLayout.LayoutParams(MATCH, MATCH))

        // Along the top: the tabs (sliding sideways when they don't fit), then search and settings.
        val top = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(0, dp(6), dp(4), dp(6))
        }
        tabsRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(dp(12), 0, dp(8), 0)
        }
        val tabsScroll = HorizontalScrollView(this).apply {
            isHorizontalScrollBarEnabled = false
            addView(tabsRow)
        }
        top.addView(tabsScroll, LinearLayout.LayoutParams(0, WRAP, 1f))
        top.addView(iconButton(R.drawable.ic_search, "Search") { openPanel(search) })
        top.addView(iconButton(R.drawable.ic_settings, "Settings") { openPanel(settings) })
        column.addView(top, LinearLayout.LayoutParams(MATCH, WRAP))

        scroll = ScrollView(this).apply { isVerticalScrollBarEnabled = false }
        val page = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, 0, 0, dp(28))
        }
        scroll.addView(page, FrameLayout.LayoutParams(MATCH, WRAP))
        column.addView(scroll, LinearLayout.LayoutParams(MATCH, 0, 1f))

        // The hero: the title's picture, its trailer over it (see-through until it plays), fading into the page.
        media = RatioFrame(this, 9f / 16f)
        backdrop = ImageView(this).apply { scaleType = ImageView.ScaleType.CENTER_CROP }
        media.addView(backdrop, FrameLayout.LayoutParams(MATCH, MATCH))
        trailer = TrailerView(this)
        trailer.onState = { trailerSaid(it) }
        media.addView(trailer, FrameLayout.LayoutParams(MATCH, MATCH))
        val fade = View(this).apply {
            background = GradientDrawable(GradientDrawable.Orientation.TOP_BOTTOM, intArrayOf(Color.TRANSPARENT, Color.TRANSPARENT, Look.BACKGROUND))
        }
        media.addView(fade, FrameLayout.LayoutParams(MATCH, MATCH))
        // Over it all, so YouTube's player never sees the finger: a tap turns the sound off or on (or starts the trailer).
        val shield = View(this).apply { setOnClickListener { tapHero() } }
        media.addView(shield, FrameLayout.LayoutParams(MATCH, MATCH))
        sound = ImageView(this).apply {
            setImageResource(R.drawable.ic_volume_on)
            imageTintList = ColorStateList.valueOf(Look.INK)
            background = rounded(0x66000000, dpf(18f))
            setPadding(dp(8), dp(8), dp(8), dp(8))
            contentDescription = "Sound"
            visibility = View.GONE
            setOnClickListener { tapHero() }
        }
        media.addView(sound, FrameLayout.LayoutParams(dp(36), dp(36), Gravity.BOTTOM or Gravity.END).apply { setMargins(0, 0, dp(16), dp(12)) })
        page.addView(media, LinearLayout.LayoutParams(MATCH, WRAP))

        val info = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), 0, dp(20), 0)
        }
        heroTitle = label(26f, Look.INK, bold = true).apply {
            maxLines = 2
            ellipsize = TextUtils.TruncateAt.END
        }
        heroMeta = label(14f, Look.MUTED).apply { setPadding(0, dp(6), 0, 0) }
        heroGenres = label(13f, Look.FAINT).apply { setPadding(0, dp(2), 0, 0) }
        heroText = label(14f, 0xD8F4F6FA.toInt()).apply {
            maxLines = 3
            ellipsize = TextUtils.TruncateAt.END
            setLineSpacing(0f, 1.15f)
            setPadding(0, dp(10), 0, 0)
        }
        val buttons = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(0, dp(16), 0, 0)
        }
        watchButton = PillButton(this, R.drawable.ic_play, filled = true).apply { setOnClickListener { watch() } }
        tvButton = PillButton(this, R.drawable.ic_tv, filled = false).apply {
            text = "Play on TV"
            setOnClickListener { playOnTv() }
        }
        buttons.addView(watchButton, LinearLayout.LayoutParams(0, dp(48), 1f))
        buttons.addView(tvButton, LinearLayout.LayoutParams(0, dp(48), 1f).apply { marginStart = dp(10) })
        info.addView(heroTitle)
        info.addView(heroMeta)
        info.addView(heroGenres)
        info.addView(heroText)
        info.addView(buttons, LinearLayout.LayoutParams(MATCH, WRAP))
        page.addView(info, LinearLayout.LayoutParams(MATCH, WRAP))

        rowsBox = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        page.addView(rowsBox, LinearLayout.LayoutParams(MATCH, WRAP))

        // The hero off the screen: its trailer stops; back up, it starts again.
        scroll.setOnScrollChangeListener { _, _, _, _, _ -> heroScrolled() }

        updateBar = label(15f, Look.BACKGROUND, bold = true).apply {
            gravity = Gravity.CENTER
            background = pressable(rounded(Look.GOOD, dpf(24f)))
            setPadding(dp(22), dp(12), dp(22), dp(12))
            visibility = View.GONE
            setOnClickListener { installUpdate() }
        }
        root.addView(updateBar, FrameLayout.LayoutParams(WRAP, WRAP, Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL).apply { bottomMargin = dp(20) })

        search = SearchPanel(this, catalog) { apps }.apply {
            visibility = View.GONE
            onPick = { t ->
                close()
                choose(t, scrollUp = true)
            }
            onClose = { scheduleTrailer() }
        }
        root.addView(search, FrameLayout.LayoutParams(MATCH, MATCH))
        settings = SettingsPanel(this).apply {
            visibility = View.GONE
            onAppsChanged = { appsChanged() }
            onCheckUpdate = { checkForUpdate(manual = true) }
            onClose = { scheduleTrailer() }
        }
        root.addView(settings, FrameLayout.LayoutParams(MATCH, MATCH))
    }

    private fun iconButton(icon: Int, description: String, action: () -> Unit) = ImageView(this).apply {
        setImageResource(icon)
        imageTintList = ColorStateList.valueOf(Look.INK)
        scaleType = ImageView.ScaleType.CENTER
        background = pressable(null)
        contentDescription = description
        layoutParams = LinearLayout.LayoutParams(dp(44), dp(44))
        setOnClickListener { action() }
    }

    private fun openPanel(panel: View) {
        stopTrailer()
        if (panel === search) search.open() else settings.open()
    }

    private val panelOpen: Boolean get() = search.visibility == View.VISIBLE || settings.visibility == View.VISIBLE

    // ---------------------------------------------------------------- tabs and rows

    private fun showTabs() {
        tabsRow.removeAllViews()
        val keys = listOf("all") + apps.map { it.key }
        if (tab !in keys) tab = "all"
        for (key in keys) {
            val chip = label(15f, Look.MUTED, bold = true).apply {
                text = if (key == "all") "All" else Apps.find(key)?.label ?: key
                gravity = Gravity.CENTER
                setPadding(dp(16), 0, dp(16), dp(1))
                tag = key
                setOnClickListener { pickTab(key) }
            }
            tabsRow.addView(chip, LinearLayout.LayoutParams(WRAP, dp(38)).apply { marginEnd = dp(4) })
        }
        styleTabs()
    }

    private fun styleTabs() {
        for (i in 0 until tabsRow.childCount) {
            val chip = tabsRow.getChildAt(i) as TextView
            val on = chip.tag == tab
            chip.setTextColor(if (on) Look.BACKGROUND else Look.MUTED)
            chip.background = if (on) rounded(Look.INK, dpf(19f)) else pressable(rounded(Color.TRANSPARENT, dpf(19f)))
        }
    }

    private fun pickTab(key: String) {
        if (key == tab) {
            scroll.smoothScrollTo(0, 0)
            return
        }
        tab = key
        styleTabs()
        scroll.scrollTo(0, 0)
        showTab(keepHero = false)
    }

    /** All: the popular films and series, and the songs; YouTube: the songs; an app: its own new films and series. */
    private fun rowsFor(tab: String): List<Row> = when (tab) {
        "all" -> catalog.rows.filter { !it.key.startsWith("app:") && (it.key != "songs" || apps.any { a -> a.key == "youtube" }) }
        "youtube" -> catalog.rows.filter { it.key == "songs" }
        else -> catalog.rows.filter { it.key.startsWith("app:$tab:") }
    }

    /** The tab's rows, and at the top the first of their titles ([keepHero]: the one there, if it's still among them). */
    private fun showTab(keepHero: Boolean) {
        val rows = rowsFor(tab)
        rowsBox.removeAllViews()
        posters.clear()
        for (row in rows) addRow(row)
        val all = rows.flatMap { it.items }
        val current = hero
        val kept = if (keepHero && current != null) all.firstOrNull { it.id == current.id } else null
        val first = kept ?: all.firstOrNull()
        val app = Apps.find(tab)
        when {
            first != null -> choose(first, scrollUp = false)
            app != null -> showApp(app)
            else -> showNothing()
        }
    }

    private fun addRow(row: Row) {
        rowsBox.addView(label(17f, Look.INK, bold = true).apply {
            text = row.title
            setPadding(dp(20), dp(24), dp(20), dp(10))
        })
        val line = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(dp(20), 0, dp(10), 0)
        }
        val square = row.key == "songs"
        val width = dp(if (square) 128 else 116)
        for (t in row.items) {
            val poster = PosterView(this, t, square)
            poster.chosen = t.id == hero?.id
            poster.setOnClickListener { choose(t, scrollUp = true) }
            line.addView(poster, LinearLayout.LayoutParams(width, WRAP).apply { marginEnd = dp(10) })
            posters.add(poster)
        }
        rowsBox.addView(HorizontalScrollView(this).apply {
            isHorizontalScrollBarEnabled = false
            addView(line)
        })
    }

    // ---------------------------------------------------------------- the hero

    /** A title at the top: its picture, then its trailer; [scrollUp] to show it, from a poster further down. */
    private fun choose(t: Title, scrollUp: Boolean) {
        val changed = hero?.id != t.id
        hero = t
        val app = Apps.find(t.appKey)
        heroTitle.text = t.title
        heroMeta.text = listOfNotNull(t.details.ifEmpty { null }, t.score?.let { "★ $it" }).joinToString("  ·  ")
        heroMeta.visibility = if (heroMeta.text.isEmpty()) View.GONE else View.VISIBLE
        heroGenres.text = t.genres
        heroGenres.visibility = if (t.genres.isEmpty()) View.GONE else View.VISIBLE
        heroText.text = t.description
        heroText.visibility = if (t.description.isEmpty()) View.GONE else View.VISIBLE
        watchButton.text = when {
            t.kind == Kind.SONG -> "Play on YouTube"
            app != null -> "Watch on ${app.label}"
            else -> "Watch"
        }
        watchButton.visibility = View.VISIBLE
        tvButton.visibility = View.VISIBLE
        for (p in posters) p.chosen = p.title.id == t.id
        if (changed) {
            stopTrailer()
            showPicture(t.backdrop, t.poster)
        }
        if (scrollUp) scroll.smoothScrollTo(0, 0)
        if (changed || !trailerOn) scheduleTrailer()
    }

    /** The wide picture; without one, the poster (or a song's cover), darkened. */
    private fun showPicture(wide: String?, poster: String?) {
        backdrop.tag = null   // the last title's, still on its way, doesn't land here
        backdrop.setImageDrawable(null)
        if (wide != null) backdrop.clearColorFilter() else backdrop.setColorFilter(0x99000000.toInt(), PorterDuff.Mode.SRC_ATOP)
        val url = wide ?: poster ?: return
        Pictures.load(this, url, backdrop, if (wide != null) 1080 else 480)
    }

    /** An app's tab with nothing listed (ETV Win, which JustWatch doesn't know): the app itself, to open. */
    private fun showApp(app: StreamApp) {
        hero = null
        stopTrailer()
        heroTitle.text = app.label
        heroMeta.text = when {
            catalog.fetching -> "Fetching its films and series…"
            app.listed -> "Nothing listed right now."
            else -> "Its films and shows aren't listed here."
        }
        heroMeta.visibility = View.VISIBLE
        heroGenres.visibility = View.GONE
        heroText.visibility = View.GONE
        watchButton.text = "Open ${app.label}"
        watchButton.visibility = View.VISIBLE
        tvButton.visibility = View.GONE
        backdrop.clearColorFilter()
        backdrop.tag = null
        backdrop.setImageDrawable(ColorDrawable(app.color))
    }

    /** Nothing to show yet: the first time, until the lists come. */
    private fun showNothing() {
        hero = null
        stopTrailer()
        heroTitle.text = if (catalog.fetching) "Fetching what's popular…" else "Nothing to show yet"
        heroMeta.text = if (catalog.fetching) "A moment." else "The lists come once the phone's online."
        heroMeta.visibility = View.VISIBLE
        heroGenres.visibility = View.GONE
        heroText.visibility = View.GONE
        watchButton.visibility = View.GONE
        tvButton.visibility = View.GONE
        backdrop.tag = null
        backdrop.setImageDrawable(null)
    }

    // ---------------------------------------------------------------- the trailer

    private fun scheduleTrailer() {
        main.removeCallbacks(startTrailer)
        if (hero != null && !trailerOn) main.postDelayed(startTrailer, TRAILER_DELAY_MS)
    }

    private fun heroVisible(): Boolean = media.height == 0 || scroll.scrollY < media.height * 0.6f

    /** The hero's trailer: JustWatch's, else one found on YouTube (looked for now, once), or a song's video. */
    private fun playTrailer() {
        val t = hero ?: return
        if (!resumed || panelOpen || !heroVisible() || trailerOn) return
        val video = trailers.known(t)
        if (video == null) {
            if (!lookedFor.add(t.id)) return   // looked for already, and YouTube couldn't be reached
            pool.execute {
                runCatching { trailers.find(t) }
                main.post { if (hero === t) playTrailer() }
            }
            return
        }
        if (video.isEmpty()) return
        trailerOn = true
        scrolledAway = false
        trailer.play(video, muted)
    }

    private fun stopTrailer() {
        main.removeCallbacks(startTrailer)
        if (trailerOn) trailer.stopTrailer()
        trailerOn = false
        sound.visibility = View.GONE
    }

    private fun trailerSaid(state: String) {
        when {
            state == "playing" && trailerOn -> {
                sound.setImageResource(if (muted) R.drawable.ic_volume_off else R.drawable.ic_volume_on)
                sound.visibility = View.VISIBLE
            }
            state == "playing" -> trailer.stopTrailer()   // too late: stopped while it loaded
            else -> {   // ended, or YouTube wouldn't play it: the picture again
                trailerOn = false
                sound.visibility = View.GONE
            }
        }
    }

    /** A tap on the hero: the trailer's sound off or on; with no trailer playing, it starts. */
    private fun tapHero() {
        if (!trailerOn) {
            main.removeCallbacks(startTrailer)
            playTrailer()
            return
        }
        muted = !muted
        trailer.mute(muted)
        sound.setImageResource(if (muted) R.drawable.ic_volume_off else R.drawable.ic_volume_on)
    }

    private fun heroScrolled() {
        if (!heroVisible() && trailerOn) {
            stopTrailer()
            scrolledAway = true
        } else if (heroVisible() && scrolledAway) {
            scrolledAway = false
            scheduleTrailer()
        }
    }

    // ---------------------------------------------------------------- watching

    /**
     * Watch: the title here, in its website's player (YouTube, JioHotstar, ZEE5, Sun NXT, ETV Win), else in its own app
     * on this phone (Netflix and Prime Video play only there); on an empty app tab, the app itself.
     */
    private fun watch() {
        val t = hero
        if (t == null) {
            val app = Apps.find(tab) ?: return
            show(app.key, app.home, app.label)
            return
        }
        stopTrailer()
        if (t.kind == Kind.SONG) {
            withVideo(t) { video ->
                show("youtube", if (video != null) "https://m.youtube.com/watch?v=$video"
                                else "https://m.youtube.com/results?search_query=" + Uri.encode(t.query ?: t.title), "YouTube")
            }
            return
        }
        val link = t.link ?: return
        show(t.appKey, link, Apps.find(t.appKey)?.label)
    }

    /** An address of an app's: in the player here when its website plays here, else in the app (or, without it, the browser). */
    private fun show(appKey: String, url: String, label: String?) {
        if (PlayerActivity.plays(this, appKey)) return PlayerActivity.open(this, url)
        if (!Opener.open(this, url)) toast(if (label != null) "$label isn't on this phone, so its website opened" else "Opened in the browser")
    }

    /** Play on TV, through CouchTV Remote: a song with its video (else the TV looks for it). */
    private fun playOnTv() {
        val t = hero ?: return
        stopTrailer()
        val send = { address: String? ->
            if (!Opener.playOnTv(this, t.suggestionLine(address))) toast("Play on TV needs CouchTV Remote, up to date: open it to update it")
        }
        if (t.kind == Kind.SONG) withVideo(t) { video -> send(video?.let { "https://www.youtube.com/watch?v=$it&list=RD$it" }) }
        else send(t.link)
    }

    /** A song's video (found on YouTube if it isn't known yet; null when it can't be). */
    private fun withVideo(t: Title, then: (String?) -> Unit) {
        val known = trailers.known(t)
        if (known != null) return then(known.ifEmpty { null })
        pool.execute {
            val found = runCatching { trailers.find(t) }.getOrNull()
            main.post { then(found?.ifEmpty { null }) }
        }
    }

    // ---------------------------------------------------------------- settings and updates

    private fun appsChanged() {
        apps = Apps.yours(this)
        showTabs()
        catalog.refresh(apps, force = true)
        showTab(keepHero = true)
    }

    private fun checkForUpdate(manual: Boolean) {
        val now = System.currentTimeMillis()
        if (!manual && now - lastUpdateCheck < UPDATE_EVERY_MS) return
        lastUpdateCheck = now
        if (manual) settings.showVersion("Checking…")
        pool.execute {
            val result = runCatching { updater.check() }
            main.post {
                result.onSuccess { found ->
                    release = found
                    if (found != null) {
                        updateBar.isEnabled = true
                        updateBar.text = "Update to ${found.versionName}"
                        updateBar.visibility = View.VISIBLE
                    }
                    if (manual) settings.showVersion(if (found != null) "${found.versionName} is out: tap Update on the home" else "Up to date")
                }.onFailure {
                    if (manual) settings.showVersion("Couldn't check: ${it.message}")
                }
            }
        }
    }

    private fun installUpdate() {
        val found = release ?: return
        if (!packageManager.canRequestPackageInstalls()) {
            toast("Allow CouchTV Watch to install its updates, then tap Update again")
            startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:$packageName")))
            return
        }
        updateBar.isEnabled = false
        updateBar.text = "Downloading…"
        pool.execute {
            val result = runCatching {
                val apk = updater.download(found) { percent -> main.post { updateBar.text = "Downloading… $percent%" } }
                updater.install(apk)
            }
            main.post {
                result.onFailure {
                    updateBar.isEnabled = true
                    updateBar.text = "Couldn't update (${it.message}): tap to try again"
                }
            }
        }
    }

    private fun toast(message: String) = Toast.makeText(this, message, Toast.LENGTH_LONG).show()
}
