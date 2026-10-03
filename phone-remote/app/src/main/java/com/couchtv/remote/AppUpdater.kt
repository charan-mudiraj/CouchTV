package com.couchtv.remote

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.os.Build
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

/**
 * Keeps the app up to date from GitHub. Every build of phone-remote/ on main publishes CouchTV-Remote.apk and
 * version.json to the repo's "phone-remote" release (see .github/workflows/phone-remote.yml). The app compares
 * version.json's versionCode with its own, downloads the APK, checks its SHA-256, and installs it with Android's
 * PackageInstaller. Network calls block, so call them off the main thread.
 */
class AppUpdater(private val context: Context) {

    data class Release(
        val versionCode: Int,
        val versionName: String,
        val commit: String,
        val message: String,
        val apkUrl: String,
        val sha256: String,
    )

    /** The newer build on GitHub, or null when this one is the latest. Throws IOException when offline. */
    fun check(): Release? {
        val json = JSONObject(String(fetch(BuildConfig.UPDATE_URL), Charsets.UTF_8))
        val release = Release(
            versionCode = json.getInt("versionCode"),
            versionName = json.optString("versionName"),
            commit = json.optString("commit"),
            message = json.optString("message"),
            apkUrl = json.getString("apk"),
            sha256 = json.getString("sha256").lowercase(),
        )
        return if (release.versionCode > BuildConfig.VERSION_CODE) release else null
    }

    /** Downloads the APK and checks it is exactly the file the build published. Reports progress in percent. */
    fun download(release: Release, progress: (Int) -> Unit): File {
        val folder = File(context.cacheDir, "updates").apply { mkdirs() }
        folder.listFiles()?.forEach { it.delete() }
        val apk = File(folder, "CouchTV-Remote-${release.versionCode}.apk")
        val digest = MessageDigest.getInstance("SHA-256")
        val connection = open(release.apkUrl)
        try {
            val total = connection.contentLengthLong
            var done = 0L
            var lastPercent = -1
            connection.inputStream.use { input ->
                apk.outputStream().use { output ->
                    val buffer = ByteArray(64 * 1024)
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        output.write(buffer, 0, read)
                        digest.update(buffer, 0, read)
                        done += read
                        val percent = if (total > 0) (done * 100 / total).toInt() else 0
                        if (percent != lastPercent) {
                            lastPercent = percent
                            progress(percent)
                        }
                    }
                }
            }
        } finally {
            connection.disconnect()
        }
        val actual = digest.digest().joinToString("") { "%02x".format(it) }
        if (actual != release.sha256) {
            apk.delete()
            throw IOException(context.getString(R.string.update_checksum))
        }
        return apk
    }

    /**
     * Hands the APK to Android's installer. Android shows a confirmation the first time. On Android 12+, later
     * updates can go through without one, because the app installed itself. The result arrives in InstallReceiver;
     * on success Android closes the app and replaces it.
     */
    fun install(apk: File) {
        val installer = context.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
        params.setAppPackageName(context.packageName)
        if (Build.VERSION.SDK_INT >= 31) {
            params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
        }
        val sessionId = installer.createSession(params)
        installer.openSession(sessionId).use { session ->
            session.openWrite("CouchTV-Remote.apk", 0, apk.length()).use { output ->
                apk.inputStream().use { it.copyTo(output) }
                session.fsync(output)
            }
            val flags = PendingIntent.FLAG_UPDATE_CURRENT or (if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0)
            val status = PendingIntent.getBroadcast(context, sessionId, Intent(context, InstallReceiver::class.java), flags)
            session.commit(status.intentSender)
        }
    }

    private fun fetch(url: String): ByteArray {
        val connection = open(url)
        try {
            return connection.inputStream.use { it.readBytes() }
        } finally {
            connection.disconnect()
        }
    }

    /** GitHub release downloads redirect to its file servers (https to https, which HttpURLConnection follows). */
    private fun open(url: String): HttpURLConnection {
        val connection = URL(url).openConnection() as HttpURLConnection
        connection.connectTimeout = 15_000
        connection.readTimeout = 30_000
        connection.useCaches = false
        connection.setRequestProperty("User-Agent", "CouchTV-Remote/${BuildConfig.VERSION_NAME}")
        val code = connection.responseCode
        if (code != HttpURLConnection.HTTP_OK) {
            connection.disconnect()
            throw IOException(if (code == HttpURLConnection.HTTP_NOT_FOUND) context.getString(R.string.update_not_published) else "HTTP $code")
        }
        return connection
    }
}
