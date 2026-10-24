package com.couchtv.watch

import org.json.JSONObject
import java.util.Base64

enum class Kind { FILM, SERIES, SONG }

/**
 * A film, series or song: what it is, its details ("2026 · 2h 37m", "3 seasons", or the singers), the app it plays in
 * (JustWatch's name for it, or "youtube"), the address that opens it there (a song has none: its video is found on
 * YouTube), its pictures, and for the hero its description, genres, IMDb score and YouTube trailer (JustWatch's, when
 * it lists one). Ids are the TV's: "m" and JustWatch's id for films and series ("mts…" a series), "s" and Apple's for songs.
 */
class Title(
    val kind: Kind,
    val id: String,
    val title: String,
    val details: String,
    val appKey: String,
    val link: String?,
    val poster: String?,
    val backdrop: String?,
    val description: String,
    val genres: String,
    val score: String?,
    val trailer: String?,
    val query: String?,
) {
    fun toJson(): JSONObject = JSONObject()
        .put("kind", kind.name).put("id", id).put("title", title).put("details", details).put("app", appKey)
        .put("link", link).put("poster", poster).put("backdrop", backdrop).put("description", description)
        .put("genres", genres).put("score", score).put("trailer", trailer).put("query", query)

    /**
     * As CouchTV Remote's suggestion line ("M|S <id> <title> <details> <app> <picture> <RRGGBB> <RRGGBB> <app key>
     * <address> <query>", texts in base64), for Play on TV: the TV plays it from its own list by its id, or else from
     * [address] (a song without one is looked for on YouTube by its query).
     */
    fun suggestionLine(address: String?): String {
        val app = Apps.find(appKey)
        return listOf(if (kind == Kind.SONG) "S" else "M", id, b64(title), b64(details), b64(app?.label ?: appKey), b64(poster),
            hex(app?.color ?: Look.RAISED), hex(app?.textColor ?: Look.INK), appKey.ifEmpty { "-" }, b64(address), b64(query))
            .joinToString(" ")
    }

    companion object {
        fun fromJson(o: JSONObject): Title? {
            val kind = runCatching { Kind.valueOf(o.getString("kind")) }.getOrNull() ?: return null
            val id = text(o, "id") ?: return null
            val title = text(o, "title") ?: return null
            return Title(kind, id, title, text(o, "details") ?: "", text(o, "app") ?: "", text(o, "link"), text(o, "poster"),
                text(o, "backdrop"), text(o, "description") ?: "", text(o, "genres") ?: "", text(o, "score"), text(o, "trailer"),
                text(o, "query"))
        }

        /** A JSON text, or null (missing, null or empty). */
        fun text(o: JSONObject, name: String): String? = if (!o.has(name) || o.isNull(name)) null else o.optString(name).ifEmpty { null }

        private fun b64(s: String?) = if (s.isNullOrEmpty()) "-" else Base64.getEncoder().encodeToString(s.toByteArray(Charsets.UTF_8))

        private fun hex(c: Int) = "%06X".format(c and 0xFFFFFF)
    }
}
