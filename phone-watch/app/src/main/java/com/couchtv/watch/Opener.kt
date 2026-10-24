package com.couchtv.watch

import android.app.Activity
import android.content.ActivityNotFoundException
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build

/**
 * Opening a title where it plays: on this phone in the app that takes its link (Netflix's in the Netflix app, and so
 * on: Android says which, so no app's name is needed here), or on the TV through CouchTV Remote.
 */
object Opener {
    const val REMOTE = "com.couchtv.remote"

    /** The address in the app that takes it, else (no such app here) in the browser. True if an app took it. */
    fun open(activity: Activity, url: String): Boolean {
        val view = Intent(Intent.ACTION_VIEW, Uri.parse(url)).addCategory(Intent.CATEGORY_BROWSABLE)
        if (Build.VERSION.SDK_INT >= 30) {
            try {
                activity.startActivity(Intent(view).addFlags(Intent.FLAG_ACTIVITY_REQUIRE_NON_BROWSER))
                return true
            } catch (e: ActivityNotFoundException) {
                // No app takes it: the browser, below.
            }
        } else {
            val app = nonBrowser(activity, view)
            if (app != null) {
                try {
                    activity.startActivity(Intent(view).setPackage(app))
                    return true
                } catch (e: ActivityNotFoundException) {
                    // Gone meanwhile: the browser, below.
                }
            }
        }
        try {
            activity.startActivity(view)
        } catch (e: ActivityNotFoundException) {
            // Not even a browser.
        }
        return false
    }

    /** Before Android 11 (which says it with FLAG_ACTIVITY_REQUIRE_NON_BROWSER): the app, not a browser, that takes the link. */
    @Suppress("DEPRECATION")
    private fun nonBrowser(activity: Activity, view: Intent): String? {
        val pm = activity.packageManager
        val anyPage = Intent(Intent.ACTION_VIEW, Uri.parse("https://example.com/")).addCategory(Intent.CATEGORY_BROWSABLE)
        val browsers = pm.queryIntentActivities(anyPage, PackageManager.MATCH_ALL).map { it.activityInfo.packageName }.toSet()
        return pm.queryIntentActivities(view, PackageManager.MATCH_ALL).map { it.activityInfo.packageName }
            .firstOrNull { it !in browsers && it != activity.packageName }
    }

    /**
     * Play on TV: CouchTV Remote opens, turns the TV on if need be, and plays it there ([line]: Title.suggestionLine).
     * False when the remote isn't on this phone, or is from before Play on TV.
     */
    fun playOnTv(activity: Activity, line: String): Boolean {
        val intent = Intent().setClassName(REMOTE, "$REMOTE.PlayOnTv").putExtra("suggestion", line)
        return try {
            activity.startActivity(intent)
            true
        } catch (e: ActivityNotFoundException) {
            false
        } catch (e: SecurityException) {
            false
        }
    }
}
