package com.couchtv.watch

import android.content.Context

/**
 * One of the apps titles play in: JustWatch's name for it ("nfx"; "youtube" for the songs; "etv" for ETV Win, which
 * JustWatch doesn't list), its name, its colours (as its tile on the TV), and its website, which opens the app itself.
 */
class StreamApp(val key: String, val label: String, val color: Int, val textColor: Int, val home: String) {
    /** JustWatch lists its films and series. */
    val listed: Boolean get() = key != "youtube" && key != "etv"
}

/** The apps of ACT's bundle that have films and series (the TV's tabs), and which of them you have on this phone. */
object Apps {
    val ALL = listOf(
        StreamApp("nfx", "Netflix", 0xFF000000.toInt(), 0xFFE50914.toInt(), "https://www.netflix.com/"),
        StreamApp("youtube", "YouTube", 0xFFFF1A1A.toInt(), 0xFFFFFFFF.toInt(), "https://www.youtube.com/"),
        StreamApp("prv", "Prime Video", 0xFF00A8E1.toInt(), 0xFFFFFFFF.toInt(), "https://www.primevideo.com/"),
        StreamApp("jhs", "JioHotstar", 0xFF1C2B7A.toInt(), 0xFFFFFFFF.toInt(), "https://www.hotstar.com/in"),
        StreamApp("zee", "ZEE5", 0xFF8230C6.toInt(), 0xFFFFFFFF.toInt(), "https://www.zee5.com/"),
        StreamApp("snx", "Sun NXT", 0xFF262626.toInt(), 0xFFFF9A1F.toInt(), "https://www.sunnxt.com/"),
        StreamApp("etv", "ETV Win", 0xFF0E2A6B.toInt(), 0xFFFFC21A.toInt(), "https://www.etvwin.com/"),
    )

    fun find(key: String?): StreamApp? = ALL.firstOrNull { it.key == key }

    private fun prefs(context: Context) = context.getSharedPreferences("apps", Context.MODE_PRIVATE)

    /** Yours, in the TV's order: all of them, but those switched off in Settings. */
    fun yours(context: Context): List<StreamApp> {
        val off = prefs(context).getStringSet("off", null) ?: emptySet()
        return ALL.filter { it.key !in off }
    }

    fun setYours(context: Context, key: String, yours: Boolean) {
        val off = HashSet(prefs(context).getStringSet("off", null) ?: emptySet())
        if (yours) off.remove(key) else off.add(key)
        prefs(context).edit().putStringSet("off", off).apply()
    }
}
