package com.couchtv.watch

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.util.Calendar
import java.util.Locale
import java.util.concurrent.Callable
import java.util.concurrent.Executors

/** One row of posters: its key ("films", "series", "songs", "app:nfx:films", "app:nfx:series"), its name, its titles. */
class Row(val key: String, val title: String, val items: List<Title>)

/**
 * The films, series and songs, fetched as the TV fetches them (Suggestions.cs): JustWatch's most popular in India on
 * your apps (from its own GraphQL API, the one its website uses), and Apple Music's most-played songs in India. The
 * All tab's rows are the popular films (this year's and last) and series, and the songs; each app's tab has its own
 * new films and series. Kept for offline, and fetched again when 6 hours old.
 */
class Catalog(context: Context) {
    companion object {
        private const val TAG = "CouchTV"
        private const val STALE_MS = 6 * 3600 * 1000L
        private const val RETRY_MS = 60 * 1000L
        private const val MOST = 20
        private const val JUSTWATCH = "https://apis.justwatch.com/graphql"
        private const val IMAGES = "https://images.justwatch.com"
        private const val APPLE_CHART = "https://rss.applemarketingtools.com/api/v2/in/music/most-played/50/songs.json"
        private const val BROWSER = "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36"
        private val QUERY = """query Popular(${'$'}country: Country!, ${'$'}language: Language!, ${'$'}first: Int!, ${'$'}filter: TitleFilter, ${'$'}offers: OfferFilter!) {
  popularTitles(country: ${'$'}country, filter: ${'$'}filter, first: ${'$'}first, sortBy: POPULAR, sortRandomSeed: 0) {
    edges { node { id objectType
      content(country: ${'$'}country, language: ${'$'}language) { title originalReleaseYear runtime posterUrl(profile: S332, format: JPG)
        shortDescription genres { shortName } scoring { imdbScore } backdrops(profile: S1440, format: JPG) { backdropUrl } clips { externalId provider } }
      ... on Show { totalSeasonCount }
      offers(country: ${'$'}country, platform: WEB, filter: ${'$'}offers) { monetizationType standardWebURL package { shortName } }
    } }
  }
}"""

        // JustWatch's genres, by its short names.
        private val GENRES = mapOf(
            "act" to "Action", "ani" to "Animation", "cmy" to "Comedy", "crm" to "Crime", "doc" to "Documentary",
            "drm" to "Drama", "eur" to "European", "fml" to "Family", "fnt" to "Fantasy", "hrr" to "Horror",
            "hst" to "History", "msc" to "Music", "rly" to "Reality", "rma" to "Romance", "scf" to "Sci-Fi",
            "spt" to "Sport", "trl" to "Thriller", "war" to "War", "wsn" to "Western",
        )

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

    /** The rows now: the last fetched (or kept from before). Main thread. */
    var rows: List<Row> = emptyList()
        private set

    /** Fetching now (the screen says so while there's nothing to show). */
    var fetching = false
        private set

    /** Called on the main thread when a fetch is over (with new rows, or not: offline). */
    var onChange: (() -> Unit)? = null

    private val file = File(context.filesDir, "catalog.json")
    private val main = Handler(Looper.getMainLooper())
    private val pool = Executors.newFixedThreadPool(4)
    private var updatedAt = 0L
    private var triedAt = 0L
    private var forApps = ""      // the apps the rows were fetched for

    init {
        load()
    }

    /** Fetches the rows for these apps if they're 6 hours old, or for other apps ([force]: now). Main thread. */
    fun refresh(apps: List<StreamApp>, force: Boolean) {
        val now = System.currentTimeMillis()
        val key = apps.joinToString(",") { it.key }
        if (fetching) return
        if (!force && key == forApps && now - updatedAt < STALE_MS) return
        if (!force && now - triedAt < RETRY_MS) return   // offline just now: not again straight away
        fetching = true
        triedAt = now
        val before = rows
        Thread {
            val fresh = runCatching { fetch(apps, before) }.onFailure { Log.i(TAG, "Catalog: ${it.message}") }.getOrNull()
            main.post {
                fetching = false
                if (fresh != null) {
                    rows = fresh
                    updatedAt = System.currentTimeMillis()
                    forApps = key
                    save()
                }
                onChange?.invoke()
            }
        }.start()
    }

    /** Films and series on your apps with these words in their names (or casts, plots): those named so first. Blocking. */
    fun search(words: String, apps: List<StreamApp>): List<Title> {
        val listed = apps.filter { it.listed }
        val wanted = words.trim()
        if (listed.isEmpty() || wanted.isEmpty()) return emptyList()
        val found = popular(listed, emptyList(), 0, wanted, 40)
        val lower = wanted.lowercase()
        return found.withIndex()
            .sortedWith(compareBy<IndexedValue<Title>>({ if (it.value.title.lowercase().contains(lower)) 0 else 1 }, { it.index }))
            .map { it.value }
            .take(30)
    }

    // ---------------------------------------------------------------- fetching

    private class Ask(val key: String, val title: String, val from: List<StreamApp>, val kind: String, val latest: Boolean)

    /** Every row, fetched side by side. A row that couldn't be fetched keeps what it had; if none could be, this throws. */
    private fun fetch(apps: List<StreamApp>, before: List<Row>): List<Row> {
        val listed = apps.filter { it.listed }
        val asks = ArrayList<Ask>()
        if (listed.isNotEmpty()) {
            asks.add(Ask("films", "Popular films", listed, "MOVIE", true))
            asks.add(Ask("series", "Popular series", listed, "SHOW", false))
            for (a in listed) {
                asks.add(Ask("app:${a.key}:films", "New films on ${a.label}", listOf(a), "MOVIE", true))
                asks.add(Ask("app:${a.key}:series", "Series on ${a.label}", listOf(a), "SHOW", false))
            }
        }
        val year = Calendar.getInstance().get(Calendar.YEAR)
        val asked = asks.map { ask -> pool.submit(Callable { popular(ask.from, listOf(ask.kind), if (ask.latest) year - 1 else 0, null, 80) }) }
        val songs = if (apps.any { it.key == "youtube" }) pool.submit(Callable { songs() }) else null

        val rows = ArrayList<Row>()
        var failures = 0
        val seen = HashMap<String, HashSet<String>>()   // a title once in a tab: All's rows share one, each app's its own
        for ((i, ask) in asks.withIndex()) {
            val found = runCatching { asked[i].get() }.getOrElse {
                failures++
                Log.i(TAG, "Row ${ask.key}: ${it.message}")
                before.firstOrNull { r -> r.key == ask.key }?.items ?: emptyList()
            }
            val tab = if (ask.key.startsWith("app:")) ask.key.split(":")[1] else "all"
            val once = seen.getOrPut(tab) { HashSet() }
            val items = ArrayList<Title>()
            for (t in found) {
                if (items.size == MOST) break
                if (once.add(t.id)) items.add(t)
            }
            if (items.isNotEmpty()) rows.add(Row(ask.key, ask.title, items))
        }
        if (songs != null) {
            val found = runCatching { songs.get() }.getOrElse {
                failures++
                Log.i(TAG, "Songs: ${it.message}")
                before.firstOrNull { r -> r.key == "songs" }?.items ?: emptyList()
            }
            if (found.isNotEmpty()) rows.add(Row("songs", "Top songs", found))
        }
        val tried = asks.size + (if (songs != null) 1 else 0)
        if (tried > 0 && failures == tried) throw IOException("nothing could be fetched")
        return rows
    }

    /**
     * JustWatch's most popular in India on these apps: of these [kinds] (and from [fromYear] on, if not 0), or with
     * these [words]. Each on the best of the apps for it: in your plan, else free.
     */
    private fun popular(apps: List<StreamApp>, kinds: List<String>, fromYear: Int, words: String?, first: Int): List<Title> {
        val packages = JSONArray(apps.map { it.key })
        val money = JSONArray(listOf("FLATRATE", "FREE", "ADS"))
        val filter = JSONObject()
        if (words != null) {
            filter.put("searchQuery", words).put("objectTypes", JSONArray(listOf("MOVIE", "SHOW")))
        } else {
            filter.put("objectTypes", JSONArray(kinds)).put("packages", packages).put("monetizationTypes", money)
        }
        if (fromYear > 0) filter.put("releaseYear", JSONObject().put("min", fromYear))
        val variables = JSONObject().put("country", "IN").put("language", "en").put("first", first).put("filter", filter)
            .put("offers", JSONObject().put("packages", packages).put("monetizationTypes", money))
        val root = JSONObject(http(JUSTWATCH, JSONObject().put("query", QUERY).put("variables", variables).toString()))
        val errors = root.optJSONArray("errors")
        if (errors != null && errors.length() > 0) throw IOException("JustWatch said: " + (errors.optJSONObject(0)?.optString("message") ?: "?"))
        val edges = root.optJSONObject("data")?.optJSONObject("popularTitles")?.optJSONArray("edges") ?: throw IOException("JustWatch sent no list")
        val byKey = apps.associateBy { it.key }
        val list = ArrayList<Title>()
        for (i in 0 until edges.length()) {
            val node = edges.optJSONObject(i)?.optJSONObject("node") ?: continue
            val title = parse(node, byKey)
            if (title != null) list.add(title)
        }
        return list
    }

    private fun parse(node: JSONObject, apps: Map<String, StreamApp>): Title? {
        val content = node.optJSONObject("content") ?: return null
        val id = str(node, "id")
        val name = str(content, "title").trim()
        val poster = str(content, "posterUrl")
        if (id.isEmpty() || name.isEmpty() || poster.isEmpty()) return null
        val offer = bestOffer(node.optJSONArray("offers"), apps) ?: return null
        val series = str(node, "objectType") == "SHOW"
        val year = content.optInt("originalReleaseYear")
        val minutes = content.optInt("runtime")
        val seasons = node.optInt("totalSeasonCount")
        val length = if (series) {
            if (seasons <= 0) null else if (seasons == 1) "1 season" else "$seasons seasons"
        } else {
            if (minutes <= 0) null else if (minutes >= 60) "${minutes / 60}h ${minutes % 60}m" else "${minutes}m"
        }
        val details = listOfNotNull(if (year > 0) year.toString() else null, length).joinToString(" · ")

        val genres = ArrayList<String>()
        val genreList = content.optJSONArray("genres")
        if (genreList != null) {
            for (j in 0 until genreList.length()) {
                val genre = GENRES[genreList.optJSONObject(j)?.let { str(it, "shortName") } ?: ""] ?: continue
                if (genres.size < 3) genres.add(genre)
            }
        }
        val backdrop = content.optJSONArray("backdrops")?.optJSONObject(0)?.let { str(it, "backdropUrl") }?.ifEmpty { null }
        var trailer: String? = null
        val clips = content.optJSONArray("clips")
        if (clips != null) {
            for (j in 0 until clips.length()) {
                val clip = clips.optJSONObject(j) ?: continue
                val video = str(clip, "externalId")
                if (trailer == null && str(clip, "provider") == "YOUTUBE" && YouTube.isId(video)) trailer = video
            }
        }
        val scoring = content.optJSONObject("scoring")
        val imdb = if (scoring == null || scoring.isNull("imdbScore")) 0.0 else scoring.optDouble("imdbScore", 0.0)
        val score = if (imdb > 0) String.format(Locale.US, "%.1f", imdb) else null

        return Title(if (series) Kind.SERIES else Kind.FILM, "m$id", name, details, offer.first.key, offer.second,
            image(poster), backdrop?.let { image(it) }, str(content, "shortDescription").trim(), genres.joinToString(" · "),
            score, trailer, null)
    }

    private fun image(path: String) = if (path.startsWith("http")) path else IMAGES + path

    /** The best of the apps for a title, and its address there: in your plan, else free; null if neither. */
    private fun bestOffer(offers: JSONArray?, apps: Map<String, StreamApp>): Pair<StreamApp, String>? {
        if (offers == null) return null
        var best = 0
        var found: Pair<StreamApp, String>? = null
        for (i in 0 until offers.length()) {
            val o = offers.optJSONObject(i) ?: continue
            val app = apps[o.optJSONObject("package")?.let { str(it, "shortName") } ?: continue] ?: continue
            val url = str(o, "standardWebURL").ifEmpty { null } ?: continue
            val score = if (str(o, "monetizationType") == "FLATRATE") 2 else 1
            if (score <= best) continue
            best = score
            found = app to url.replaceFirst("http://", "https://")
        }
        return found
    }

    /** The chart's songs, at most two by each singer (a new album can fill half of it); their videos are found when wanted. */
    private fun songs(): List<Title> {
        val results = JSONObject(http(APPLE_CHART, null)).optJSONObject("feed")?.optJSONArray("results") ?: throw IOException("Apple sent no chart")
        val perArtist = HashMap<String, Int>()
        val list = ArrayList<Title>()
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
            val art = str(r, "artworkUrl100").replace("100x100bb", "400x400bb").ifEmpty { null }
            list.add(Title(Kind.SONG, "s$id", title, artist, "youtube", null, art, null, "", "", null, null, "$title $lead"))
            if (list.size == MOST) break
        }
        return list
    }

    // ---------------------------------------------------------------- kept between runs

    private fun save() {
        val all = JSONArray()
        for (r in rows) {
            val items = JSONArray()
            for (t in r.items) items.put(t.toJson())
            all.put(JSONObject().put("key", r.key).put("title", r.title).put("items", items))
        }
        val text = JSONObject().put("at", updatedAt).put("apps", forApps).put("rows", all).toString()
        runCatching {
            val part = File(file.path + ".part")
            part.writeText(text)
            part.renameTo(file)
        }
    }

    private fun load() {
        runCatching {
            if (!file.exists()) return
            val o = JSONObject(file.readText())
            val all = o.getJSONArray("rows")
            val list = ArrayList<Row>()
            for (i in 0 until all.length()) {
                val r = all.getJSONObject(i)
                val items = r.getJSONArray("items")
                val titles = ArrayList<Title>()
                for (j in 0 until items.length()) Title.fromJson(items.getJSONObject(j))?.let { titles.add(it) }
                list.add(Row(r.getString("key"), r.getString("title"), titles))
            }
            rows = list
            updatedAt = o.optLong("at")
            forApps = o.optString("apps")
        }
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
