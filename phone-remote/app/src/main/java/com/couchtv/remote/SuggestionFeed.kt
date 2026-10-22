package com.couchtv.remote

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.util.Calendar
import java.util.concurrent.Executors

/**
 * The first page's list, there with the TV off too, so you can choose before turning it on. With the TV connected
 * it's the TV's own list ([fromTv]); otherwise the phone fetches the same rows itself, from the same places as the TV
 * (JustWatch's films and series popular in India, and on each app you pay for once there are two; Apple Music's
 * most-played songs in India), picked for the TV's apps and subscriptions as the TV last told this phone ([setSetup]).
 * Kept between runs; fetched again once 3 hours old.
 */
class SuggestionFeed(context: Context) {
    companion object {
        private const val TAG = "CouchTV"
        private const val STALE_MS = 3 * 3600 * 1000L
        private const val MOST = 20
        private const val MOST_PER_APP = 15
        private const val JUSTWATCH = "https://apis.justwatch.com/graphql"
        private const val JUSTWATCH_IMAGES = "https://images.justwatch.com"
        private const val APPLE_CHART = "https://rss.applemarketingtools.com/api/v2/in/music/most-played/50/songs.json"
        private const val BROWSER = "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36"
        private val POPULAR = """query Popular(${'$'}country: Country!, ${'$'}language: Language!, ${'$'}first: Int!, ${'$'}filter: TitleFilter, ${'$'}offers: OfferFilter!) {
  popularTitles(country: ${'$'}country, filter: ${'$'}filter, first: ${'$'}first, sortBy: POPULAR, sortRandomSeed: 0) {
    edges { node { id objectType
      content(country: ${'$'}country, language: ${'$'}language) { title originalReleaseYear runtime posterUrl(profile: S332, format: JPG) }
      ... on Show { totalSeasonCount }
      offers(country: ${'$'}country, platform: WEB, filter: ${'$'}offers) { monetizationType standardWebURL package { shortName } }
    } }
  }
}"""

        /** "Radhimaa (feat. Sai Smriti) [From "Think Indie"]" → "Radhimaa", as the TV tidies them. */
        fun songTitle(name: String): String {
            val title = name.replace(Regex("""\s*[(\[](?:feat\.?|ft\.?|with|from)\s[^)\]]*[)\]]""", RegexOption.IGNORE_CASE), "")
                .replace(Regex("""\s+"""), " ").trim()
            return title.ifEmpty { name.trim() }
        }

        /** "Karan Aujla & MXRCI" → "Karan Aujla". */
        fun leadArtist(artist: String): String =
            artist.split(Regex("""\s*(?:&|,|\bfeat\.?\s)\s*""", RegexOption.IGNORE_CASE))[0].trim()
    }

    /** One of the TV's apps: JustWatch's name for it (or "youtube"), its name and colours, and whether you subscribe. */
    class App(val key: String, val label: String, val color: Int, val textColor: Int, val yours: Boolean)

    /** What the TV has: which lists it shows, and its apps. */
    class Setup(val movies: Boolean, val music: Boolean, val apps: List<App>) {
        companion object {
            /** Before any TV has said: CouchTV's own apps, all of them yours. */
            val DEFAULT = Setup(true, true, listOf(
                App("nfx", "Netflix", 0xFF000000.toInt(), 0xFFE50914.toInt(), true),
                App("youtube", "YouTube", 0xFFFF1A1A.toInt(), 0xFFFFFFFF.toInt(), true),
                App("prv", "Prime Video", 0xFF00A8E1.toInt(), 0xFFFFFFFF.toInt(), true),
                App("jhs", "JioHotstar", 0xFF1C2B7A.toInt(), 0xFFFFFFFF.toInt(), true),
            ))

            /** "Y|N Y|N key:name:label:RRGGBB:RRGGBB:Y|N,…" (SUGGESTSETUP, without its name); null if it isn't one. */
            fun parse(text: String): Setup? {
                val p = text.trim().split(" ")
                if (p.size < 3 || p[0] !in setOf("Y", "N") || p[1] !in setOf("Y", "N")) return null
                val apps = if (p[2] == "-") emptyList() else p[2].split(",").mapNotNull { entry ->
                    val f = entry.split(":")
                    if (f.size < 6 || f[0].isEmpty()) null
                    else App(f[0], Suggestion.text(f[2]) ?: f[0], Suggestion.colour(f[3], 0xFF262D3A.toInt()), Suggestion.colour(f[4], 0xFFFFFFFF.toInt()), f[5] == "Y")
                }
                return Setup(p[0] == "Y", p[1] == "Y", apps)
            }
        }
    }

    /** Called on the main thread when the phone has fetched a new list itself. */
    var onChange: ((List<Suggestion>) -> Unit)? = null

    /** The list now: the TV's, or the phone's own, whichever came last. */
    var current: List<Suggestion>
        private set

    private val app = context.applicationContext
    private val prefs = app.getSharedPreferences("suggestions", Context.MODE_PRIVATE)
    private val main = Handler(Looper.getMainLooper())
    private val pool = Executors.newSingleThreadExecutor()
    private var setup = prefs.getString("setup", null)?.let { Setup.parse(it) } ?: Setup.DEFAULT
    private var updatedAt = prefs.getLong("at", 0L)
    private var generation = 0     // a new list from the TV makes a fetch still going pointless
    private var fetching = false

    init {
        current = prefs.getString("list", null)?.lines()?.mapNotNull { Suggestion.parse(it) } ?: emptyList()
    }

    /** The TV's list, as it connected or changed it. Main thread. */
    fun fromTv(list: List<Suggestion>) {
        generation++
        keep(list)
    }

    /** What the TV has ("SUGGESTSETUP …" without its name). Main thread. */
    fun setSetup(text: String) {
        val parsed = Setup.parse(text) ?: return
        setup = parsed
        prefs.edit().putString("setup", text).apply()
    }

    /** With the TV off: fetches the lists here, if the one kept is 3 hours old (or there's none). Main thread. */
    fun refreshIfStale() {
        if (fetching || System.currentTimeMillis() - updatedAt < STALE_MS || (!setup.movies && !setup.music)) return
        fetching = true
        val wanted = setup
        val started = generation
        val before = current
        pool.execute {
            val titles = if (!wanted.movies) emptyList() else runCatching { titles(wanted, before) }.onFailure { Log.i(TAG, "Films: ${it.message}") }.getOrNull()
            val songs = if (!wanted.music) emptyList() else runCatching { songs(wanted) }.onFailure { Log.i(TAG, "Songs: ${it.message}") }.getOrNull()
            main.post {
                fetching = false
                if ((titles == null && songs == null) || generation != started) return@post   // offline, or the TV's list came meanwhile
                val list = (titles ?: before.filter { it.movie }) + (songs ?: before.filter { !it.movie })
                keep(list)
                onChange?.invoke(list)
            }
        }
    }

    private fun keep(list: List<Suggestion>) {
        current = list
        updatedAt = System.currentTimeMillis()
        prefs.edit().putString("list", list.joinToString("\n") { it.line() }).putLong("at", updatedAt).apply()
    }

    // ---------------------------------------------------------------- fetching, as the TV does (Suggestions.cs)

    /**
     * The films-and-series rows, as the TV makes them (Suggestions.Titles): popular films (this year's and last),
     * popular series, and once you pay for two apps or more, what's popular on each. A title once, in the first row
     * it's in; a row that couldn't be fetched keeps what it had ([before]), and if none could be, this throws.
     */
    private fun titles(setup: Setup, before: List<Suggestion>): List<Suggestion> {
        val apps = setup.apps.filter { it.key != "youtube" }.associateBy { it.key }
        if (apps.isEmpty()) return emptyList()
        val list = ArrayList<Suggestion>()
        val seen = HashSet<String>()
        var tried = 0
        var failures = 0
        var error: Throwable? = null
        fun row(key: String, title: String, from: Map<String, App>, kinds: List<String>, latest: Boolean, most: Int) {
            tried++
            val found = runCatching { popular(from, kinds, latest) }.getOrElse {
                failures++
                error = it
                Log.i(TAG, "Row $key: ${it.message}")
                before.filter { s -> s.row == key }
            }
            var added = 0
            for (s in found) {
                if (added == most || !seen.add(s.id)) continue
                list.add(s.inRow(key, title))
                added++
            }
        }
        row("films", app.getString(R.string.suggestions_movies), apps, listOf("MOVIE"), true, MOST)
        row("series", app.getString(R.string.row_series), apps, listOf("SHOW"), false, MOST)
        val yours = setup.apps.filter { it.yours && it.key in apps }.map { it.key }.distinct()
        if (yours.size >= 2) for (key in yours) {
            val one = apps.getValue(key)
            row("app:$key", app.getString(R.string.row_app, one.label), mapOf(key to one), listOf("MOVIE", "SHOW"), false, MOST_PER_APP)
        }
        if (failures == tried) throw error ?: IOException("nothing fetched")
        return list
    }

    /** JustWatch's most popular in India of these kinds on these apps; [latest]: this year's and last year's. A series opens on its page. */
    private fun popular(apps: Map<String, App>, kinds: List<String>, latest: Boolean): List<Suggestion> {
        val packages = JSONArray(apps.keys.toList())
        val money = JSONArray(listOf("FLATRATE", "FREE", "ADS"))
        val filter = JSONObject().put("objectTypes", JSONArray(kinds)).put("packages", packages).put("monetizationTypes", money)
        if (latest) filter.put("releaseYear", JSONObject().put("min", Calendar.getInstance().get(Calendar.YEAR) - 1))
        val variables = JSONObject().put("country", "IN").put("language", "en").put("first", 80).put("filter", filter)
            .put("offers", JSONObject().put("packages", packages).put("monetizationTypes", money))
        val root = JSONObject(http(JUSTWATCH, JSONObject().put("query", POPULAR).put("variables", variables).toString()))
        root.optJSONArray("errors")?.takeIf { it.length() > 0 }?.let { throw IOException("JustWatch said: " + it.optJSONObject(0)?.let { e -> str(e, "message") }) }
        val edges = root.optJSONObject("data")?.optJSONObject("popularTitles")?.optJSONArray("edges") ?: throw IOException("JustWatch sent no list")
        val list = ArrayList<Suggestion>()
        for (i in 0 until edges.length()) {
            val node = edges.optJSONObject(i)?.optJSONObject("node") ?: continue
            val content = node.optJSONObject("content") ?: continue
            val id = str(node, "id")
            val title = str(content, "title").trim()
            val poster = str(content, "posterUrl")
            if (id.isEmpty() || title.isEmpty() || poster.isEmpty()) continue
            val (app, link) = bestOffer(node.optJSONArray("offers"), apps) ?: continue
            val series = str(node, "objectType") == "SHOW"
            val year = content.optInt("originalReleaseYear")
            val minutes = content.optInt("runtime")
            val seasons = node.optInt("totalSeasonCount")
            val length = if (series) seasons.takeIf { it > 0 }?.let { if (it == 1) "1 season" else "$it seasons" }
                         else minutes.takeIf { it > 0 }?.let { if (it >= 60) "${it / 60}h ${it % 60}m" else "${it}m" }
            val details = listOfNotNull(year.takeIf { it > 0 }?.toString(), length).joinToString(" · ")
            list.add(Suggestion(true, "m$id", title, details, app.label, if (poster.startsWith("http")) poster else JUSTWATCH_IMAGES + poster,
                app.color, app.textColor, app.key, link))
        }
        return list
    }

    /** The best of your apps for a film: one you subscribe to, else one that has it free; null if neither. */
    private fun bestOffer(offers: JSONArray?, apps: Map<String, App>): Pair<App, String>? {
        if (offers == null) return null
        var best = 0
        var found: Pair<App, String>? = null
        for (i in 0 until offers.length()) {
            val o = offers.optJSONObject(i) ?: continue
            val app = apps[o.optJSONObject("package")?.let { str(it, "shortName") } ?: continue] ?: continue
            val url = str(o, "standardWebURL").ifEmpty { null } ?: continue
            val money = str(o, "monetizationType")
            val score = if (app.yours) (if (money == "FLATRATE") 3 else 2) else if (money == "FREE" || money == "ADS") 1 else 0
            if (score <= best) continue
            best = score
            found = app to url
        }
        return found
    }

    /** The chart's songs, at most two by each singer; the TV finds each one's video when it's played. */
    private fun songs(setup: Setup): List<Suggestion> {
        val app = setup.apps.firstOrNull { it.key == "youtube" } ?: return emptyList()
        val results = JSONObject(http(APPLE_CHART, null)).optJSONObject("feed")?.optJSONArray("results") ?: throw IOException("Apple sent no chart")
        val perArtist = HashMap<String, Int>()
        val list = ArrayList<Suggestion>()
        for (i in 0 until results.length()) {
            val r = results.optJSONObject(i) ?: continue
            val id = str(r, "id")
            val name = str(r, "name")
            val artist = str(r, "artistName").trim()
            if (id.isEmpty() || name.isEmpty() || artist.isEmpty()) continue
            val lead = leadArtist(artist)
            val count = perArtist[lead.lowercase()] ?: 0
            if (count >= 2) continue
            perArtist[lead.lowercase()] = count + 1
            val title = songTitle(name)
            list.add(Suggestion(false, "s$id", title, artist, app.label, str(r, "artworkUrl100").replace("100x100bb", "400x400bb").ifEmpty { null },
                app.color, app.textColor, app.key, null, "$title $lead", "songs", this.app.getString(R.string.suggestions_songs)))
            if (list.size == MOST) break
        }
        return list
    }

    /** A JSON text, or "" (Android's optString turns a JSON null into "null"). */
    private fun str(o: JSONObject, name: String): String = if (o.isNull(name)) "" else o.optString(name)

    private fun http(url: String, body: String?): String {
        val c = URL(url).openConnection() as HttpURLConnection
        c.connectTimeout = 10000
        c.readTimeout = 15000
        c.setRequestProperty("User-Agent", BROWSER)
        c.setRequestProperty("Accept", "application/json")
        try {
            if (body != null) {
                c.requestMethod = "POST"
                c.doOutput = true
                c.setRequestProperty("Content-Type", "application/json")
                c.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
            }
            if (c.responseCode !in 200..299) throw IOException("${c.responseCode} from ${c.url.host}")
            return c.inputStream.bufferedReader(Charsets.UTF_8).use { it.readText() }
        } finally {
            c.disconnect()
        }
    }
}
