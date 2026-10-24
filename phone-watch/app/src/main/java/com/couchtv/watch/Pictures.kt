package com.couchtv.watch

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.os.Handler
import android.os.Looper
import android.util.LruCache
import android.widget.ImageView
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.util.concurrent.Executors

/** Pictures from the internet (as the remote's): kept in memory while the app runs, and on disk for next time. */
object Pictures {
    private val memory = object : LruCache<String, Bitmap>(32 * 1024 * 1024) {
        override fun sizeOf(key: String, value: Bitmap) = value.byteCount
    }
    private val pool = Executors.newFixedThreadPool(3)
    private val main = Handler(Looper.getMainLooper())

    /** Into [view], at least [width] pixels wide; fading in when it had to be fetched or read from the disk. */
    fun load(context: Context, url: String, view: ImageView, width: Int) {
        view.tag = url
        memory.get(url)?.let {
            view.setImageBitmap(it)
            return
        }
        val folder = File(context.cacheDir, "pictures")
        pool.execute {
            val bitmap = runCatching { fetch(folder, url, width) }.getOrNull() ?: return@execute
            memory.put(url, bitmap)
            main.post {
                if (view.tag != url) return@post
                view.alpha = 0f
                view.setImageBitmap(bitmap)
                view.animate().alpha(1f).setDuration(260).start()
            }
        }
    }

    private fun fetch(folder: File, url: String, width: Int): Bitmap? {
        folder.mkdirs()
        val file = File(folder, name(url))
        if (!file.exists()) {
            prune(folder)
            val part = File(folder, file.name + ".part")
            val connection = URL(url).openConnection() as HttpURLConnection
            connection.connectTimeout = 10000
            connection.readTimeout = 15000
            try {
                connection.inputStream.use { input -> part.outputStream().use { input.copyTo(it) } }
            } finally {
                connection.disconnect()
            }
            if (!part.renameTo(file)) return null
        }
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(file.path, bounds)
        var sample = 1
        while (bounds.outWidth / (sample * 2) >= width) sample *= 2
        // Half the memory of full colour, which posters don't need: a row or two of them is a lot of pictures.
        return BitmapFactory.decodeFile(file.path, BitmapFactory.Options().apply {
            inSampleSize = sample
            inPreferredConfig = Bitmap.Config.RGB_565
        })
    }

    private fun name(url: String) =
        MessageDigest.getInstance("SHA-1").digest(url.toByteArray()).take(10).joinToString("") { "%02x".format(it) }

    /** Lets go of pictures not used for two weeks (the lists change every few hours). */
    private fun prune(folder: File) {
        val old = System.currentTimeMillis() - 14L * 24 * 3600 * 1000
        folder.listFiles()?.forEach { if (it.lastModified() < old) it.delete() }
    }
}
