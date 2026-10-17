package com.couchtv.remote

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.ColorFilter
import android.graphics.LinearGradient
import android.graphics.Matrix
import android.graphics.Paint
import android.graphics.PixelFormat
import android.graphics.RadialGradient
import android.graphics.Rect
import android.graphics.Shader
import android.graphics.drawable.Drawable
import android.media.ExifInterface
import android.net.Uri
import java.io.ByteArrayOutputStream
import java.io.File
import kotlin.math.max

/**
 * Backgrounds: the remote's, and the TV home screen's (the same looks there, by the same names): a dark base with two
 * soft glows of colour, or your own photo under a dark veil, so the keys (and the TV's tiles) stay easy to see.
 */
object Backdrop {
    class Look(val key: String, val name: Int, val base: Int, val glow: Int, val glow2: Int)

    const val PHOTO = "photo"

    val LOOKS = listOf(
        Look("midnight", R.string.look_midnight, 0xFF0B0E14.toInt(), 0xB31E3A72.toInt(), 0x80472066.toInt()),
        Look("aurora", R.string.look_aurora, 0xFF060C14.toInt(), 0xA00FA896.toInt(), 0x996A3CE0.toInt()),
        Look("sunset", R.string.look_sunset, 0xFF120A0E.toInt(), 0xB3D95B26.toInt(), 0x8CB0306E.toInt()),
        Look("ocean", R.string.look_ocean, 0xFF05101A.toInt(), 0xB30E62A8.toInt(), 0x8C0FA3C2.toInt()),
        Look("forest", R.string.look_forest, 0xFF07110C.toInt(), 0xA01E7A48.toInt(), 0x8C5E7A1E.toInt()),
        Look("rose", R.string.look_rose, 0xFF13090F.toInt(), 0xB3C2366E.toInt(), 0x8C6E3A9A.toInt()),
        Look("graphite", R.string.look_graphite, 0xFF111317.toInt(), 0x30FFFFFF, 0x18FFFFFF),
    )

    /** This phone's photo for the remote. */
    fun photoFile(context: Context) = File(context.filesDir, "backdrop.jpg")

    /** The last photo sent to the TV (to show it on the Backgrounds page). */
    fun tvPhotoFile(context: Context) = File(context.filesDir, "tv_backdrop.jpg")

    /** The look by its name (the photo if it's there; midnight for anything else). */
    fun drawable(context: Context, key: String, photo: File = photoFile(context)): Drawable {
        if (key == PHOTO) {
            val bitmap = runCatching { BitmapFactory.decodeFile(photo.path) }.getOrNull()
            if (bitmap != null) return PhotoDrawable(bitmap)
        }
        val look = LOOKS.find { it.key == key } ?: LOOKS[0]
        return GlowDrawable(look.base, look.glow, look.glow2)
    }

    /**
     * A photo from the gallery, upright (as its camera said), cropped to fill [width] x [height] and made a JPEG; null
     * if it can't be read.
     */
    fun prepare(context: Context, uri: Uri, width: Int, height: Int, quality: Int): ByteArray? = runCatching {
        val resolver = context.contentResolver
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        resolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, bounds) }
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null
        var sample = 1
        while (bounds.outWidth / (sample * 2) >= width && bounds.outHeight / (sample * 2) >= height) sample *= 2
        val raw = resolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, BitmapFactory.Options().apply { inSampleSize = sample }) }
            ?: return null
        val turn = resolver.openInputStream(uri)?.use {
            when (ExifInterface(it).getAttributeInt(ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_NORMAL)) {
                ExifInterface.ORIENTATION_ROTATE_90 -> 90f
                ExifInterface.ORIENTATION_ROTATE_180 -> 180f
                ExifInterface.ORIENTATION_ROTATE_270 -> 270f
                else -> 0f
            }
        } ?: 0f
        val upright = if (turn == 0f) raw else Bitmap.createBitmap(raw, 0, 0, raw.width, raw.height, Matrix().apply { postRotate(turn) }, true)
        // Fill the shape, cropping the middle.
        val scale = max(width.toFloat() / upright.width, height.toFloat() / upright.height)
        val w = (width / scale).toInt().coerceIn(1, upright.width)
        val h = (height / scale).toInt().coerceIn(1, upright.height)
        val cropped = Bitmap.createBitmap(upright, (upright.width - w) / 2, (upright.height - h) / 2, w, h)
        val sized = Bitmap.createScaledBitmap(cropped, width, height, true)
        ByteArrayOutputStream().use { out ->
            sized.compress(Bitmap.CompressFormat.JPEG, quality, out)
            out.toByteArray()
        }
    }.getOrNull()

    /** A dark base and two glows of colour: one from the top left, one from the bottom right (as on the TV). */
    class GlowDrawable(private val base: Int, private val glow: Int, private val glow2: Int) : Drawable() {
        private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
        private val paint2 = Paint(Paint.ANTI_ALIAS_FLAG)

        override fun onBoundsChange(b: Rect) {
            val size = max(b.width(), b.height()).toFloat().coerceAtLeast(1f)
            paint.shader = RadialGradient(b.left + b.width() * 0.06f, b.top - b.height() * 0.05f, size * 0.85f,
                intArrayOf(glow, glow and 0x00FFFFFF), null, Shader.TileMode.CLAMP)
            paint2.shader = RadialGradient(b.right.toFloat(), b.top + b.height() * 1.08f, size * 0.75f,
                intArrayOf(glow2, glow2 and 0x00FFFFFF), null, Shader.TileMode.CLAMP)
        }

        override fun draw(canvas: Canvas) {
            canvas.drawColor(base)
            canvas.drawRect(bounds, paint)
            canvas.drawRect(bounds, paint2)
        }

        override fun setAlpha(alpha: Int) {}
        override fun setColorFilter(colorFilter: ColorFilter?) {}
        @Deprecated("Deprecated in Java")
        override fun getOpacity() = PixelFormat.OPAQUE
    }

    /** A photo filling the space (the middle of it), under a veil that's darker towards the bottom, where the keys are. */
    class PhotoDrawable(private val bitmap: Bitmap) : Drawable() {
        private val paint = Paint(Paint.FILTER_BITMAP_FLAG)
        private val veil = Paint()
        private val matrix = Matrix()

        override fun onBoundsChange(b: Rect) {
            val scale = max(b.width().toFloat() / bitmap.width, b.height().toFloat() / bitmap.height)
            matrix.setScale(scale, scale)
            matrix.postTranslate(b.left + (b.width() - bitmap.width * scale) / 2, b.top + (b.height() - bitmap.height * scale) / 2)
            veil.shader = LinearGradient(0f, b.top.toFloat(), 0f, b.bottom.toFloat(), intArrayOf(0x8C000000.toInt(), 0xB8000000.toInt(), 0xD9000000.toInt()),
                null, Shader.TileMode.CLAMP)
        }

        override fun draw(canvas: Canvas) {
            canvas.drawColor(0xFF0B0E14.toInt())
            canvas.drawBitmap(bitmap, matrix, paint)
            canvas.drawRect(bounds, veil)
        }

        override fun setAlpha(alpha: Int) {}
        override fun setColorFilter(colorFilter: ColorFilter?) {}
        @Deprecated("Deprecated in Java")
        override fun getOpacity() = PixelFormat.OPAQUE
    }
}
