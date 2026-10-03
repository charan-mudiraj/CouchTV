package com.couchtv.remote

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject

/** A button you added yourself. The TV learns what it does in Settings & power > Remote, or in remote.ini. */
data class CustomButton(val label: String, val address: Int, val command: Int) {
    /** How the TV's receiver reports it, e.g. "NEC 00CE 0060": the left-hand side of a remote.ini line. */
    val code: String get() = "NEC %04X %04X".format(address, command)
}

/** Your own buttons, saved on the phone. */
class CustomButtons(context: Context) {
    private val prefs = context.getSharedPreferences("custom_buttons", Context.MODE_PRIVATE)

    fun load(): MutableList<CustomButton> {
        val list = mutableListOf<CustomButton>()
        val array = try {
            JSONArray(prefs.getString(KEY, null) ?: "[]")
        } catch (e: Exception) {
            JSONArray()
        }
        for (i in 0 until array.length()) {
            val item = array.optJSONObject(i) ?: continue
            list += CustomButton(item.optString("label", "?"), item.optInt("address", Codes.ADDRESS), item.optInt("command"))
        }
        return list
    }

    fun save(list: List<CustomButton>) {
        val array = JSONArray()
        for (button in list) {
            array.put(JSONObject().put("label", button.label).put("address", button.address).put("command", button.command))
        }
        prefs.edit().putString(KEY, array.toString()).apply()
    }

    /** The first free command for a new button on the CouchTV address. */
    fun nextFreeCommand(list: List<CustomButton>): Int {
        val used = list.filter { it.address == Codes.ADDRESS }.map { it.command }.toSet()
        return Codes.FREE.firstOrNull { it !in used } ?: Codes.FREE.first
    }

    private companion object {
        const val KEY = "buttons"
    }
}
