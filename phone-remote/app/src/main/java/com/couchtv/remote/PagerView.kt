package com.couchtv.remote

import android.content.Context
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.ViewGroup
import android.widget.HorizontalScrollView
import kotlin.math.ceil
import kotlin.math.floor
import kotlin.math.roundToInt

/**
 * Pages side by side, each one screen wide, swiped like a phone's home screen: a flick, or a drag past halfway,
 * moves one page, and the page snaps into place. Its one child is a horizontal LinearLayout holding the pages.
 * Taps and holds on the buttons inside still work (PressGesture tells a press from a swipe).
 */
class PagerView @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : HorizontalScrollView(context, attrs) {
    /** Called with the new page's number when it changes. */
    var onPageChanged: ((Int) -> Unit)? = null

    var page = 0
        private set

    val pageCount: Int get() = (getChildAt(0) as? ViewGroup)?.childCount ?: 0

    private var flung = false

    init {
        isHorizontalScrollBarEnabled = false
        overScrollMode = OVER_SCROLL_NEVER
    }

    /** Moves to [target] (sliding there unless [animate] is false). */
    fun go(target: Int, animate: Boolean = true) {
        val p = target.coerceIn(0, maxOf(0, pageCount - 1))
        if (animate) smoothScrollTo(p * width, 0) else scrollTo(p * width, 0)
        if (p != page) {
            page = p
            onPageChanged?.invoke(p)
        }
    }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        // Every page is exactly as wide as the pager.
        val width = MeasureSpec.getSize(widthMeasureSpec)
        (getChildAt(0) as? ViewGroup)?.let { strip ->
            for (i in 0 until strip.childCount) strip.getChildAt(i).layoutParams.width = width
        }
        super.onMeasure(widthMeasureSpec, heightMeasureSpec)
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        post { scrollTo(page * w, 0) }   // stay on the same page
    }

    override fun onTouchEvent(ev: MotionEvent): Boolean {
        val action = ev.actionMasked
        if (action == MotionEvent.ACTION_DOWN) flung = false
        val handled = super.onTouchEvent(ev)
        // Let go without a flick: settle on whichever page is mostly showing.
        if ((action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_CANCEL) && !flung && width > 0) {
            go((scrollX.toFloat() / width).roundToInt())
        }
        return handled
    }

    /** A flick moves one page in its direction (instead of gliding freely). */
    override fun fling(velocityX: Int) {
        flung = true
        if (width == 0) return
        val at = scrollX.toFloat() / width
        go(if (velocityX > 0) floor(at).toInt() + 1 else ceil(at).toInt() - 1)
    }
}
