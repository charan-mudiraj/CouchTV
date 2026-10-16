package com.couchtv.remote

import android.animation.Animator
import android.animation.AnimatorListenerAdapter
import android.animation.ValueAnimator
import android.content.Context
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.view.animation.PathInterpolator
import android.widget.HorizontalScrollView
import kotlin.math.abs
import kotlin.math.ceil
import kotlin.math.floor
import kotlin.math.roundToInt

/**
 * Pages side by side, each one screen wide, swiped like a phone's home screen: a flick, or a drag past halfway,
 * moves one page, and the page settles into place, quicker the faster the flick. The pages either side sit a little
 * back (smaller and dimmer) and come forward as they slide in; at either end the page stretches. Its one child is a
 * horizontal LinearLayout holding the pages. Taps and holds on the buttons inside still work (PressGesture tells a
 * press from a swipe).
 */
class PagerView @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : HorizontalScrollView(context, attrs) {
    private companion object {
        const val BACK_SCALE = 0.06f    // a page beside the one showing is this much smaller...
        const val BACK_DIM = 0.4f       // ...and dimmer
        val SETTLE = PathInterpolator(0.2f, 0f, 0f, 1f)   // quick off the mark, a long gentle stop
    }

    /** Called with the new page's number when it changes. */
    var onPageChanged: ((Int) -> Unit)? = null

    /** Called as the pages slide, with where they are: 1.5 is halfway from page 1 to page 2. */
    var onScroll: ((Float) -> Unit)? = null

    var page = 0
        private set

    val pageCount: Int get() = strip?.childCount ?: 0

    private val strip: ViewGroup? get() = getChildAt(0) as? ViewGroup
    private var flung = false
    private var settle: ValueAnimator? = null
    private var layered = false

    init {
        isHorizontalScrollBarEnabled = false
        overScrollMode = OVER_SCROLL_ALWAYS   // Android 12 and newer stretch the page at either end
    }

    /** Moves to [target], sliding there unless [animate] is false; [speed] is a flick's, in pixels a second. */
    fun go(target: Int, animate: Boolean = true, speed: Float = 0f) {
        val p = target.coerceIn(0, maxOf(0, pageCount - 1))
        settle?.cancel()
        val to = p * width
        if (!animate || width == 0 || scrollX == to) {
            scrollTo(to, 0)
            layers(false)
        } else {
            val from = scrollX
            val distance = abs(to - from)
            // A flick keeps its speed into the settle; a let-go drag takes its time over what's left.
            val ms = if (speed > 0) (distance / speed * 1000 * 2.2f).toLong() else (180 + 240L * distance / width)
            settle = ValueAnimator.ofInt(from, to).apply {
                duration = ms.coerceIn(200, 420)
                interpolator = SETTLE
                addUpdateListener { scrollTo(it.animatedValue as Int, 0) }
                addListener(object : AnimatorListenerAdapter() {
                    override fun onAnimationEnd(animation: Animator) {
                        if (settle === animation) layers(false)
                    }
                })
                layers(true)
                start()
            }
        }
        if (p != page) {
            page = p
            onPageChanged?.invoke(p)
        }
    }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        // Every page is exactly as wide as the pager.
        val width = MeasureSpec.getSize(widthMeasureSpec)
        strip?.let { s ->
            for (i in 0 until s.childCount) s.getChildAt(i).layoutParams.width = width
        }
        super.onMeasure(widthMeasureSpec, heightMeasureSpec)
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        post { scrollTo(page * w, 0) }   // stay on the same page
    }

    override fun onScrollChanged(l: Int, t: Int, oldl: Int, oldt: Int) {
        super.onScrollChanged(l, t, oldl, oldt)
        if (width == 0) return
        val at = l.toFloat() / width
        val s = strip ?: return
        for (i in 0 until s.childCount) {
            val away = abs(i - at).coerceAtMost(1f)
            val pageView = s.getChildAt(i)
            val scale = 1f - BACK_SCALE * away
            pageView.scaleX = scale
            pageView.scaleY = scale
            pageView.alpha = 1f - BACK_DIM * away
        }
        onScroll?.invoke(at)
    }

    override fun onInterceptTouchEvent(ev: MotionEvent): Boolean {
        if (ev.actionMasked == MotionEvent.ACTION_DOWN) flung = false   // a press on a button comes here first
        return super.onInterceptTouchEvent(ev)
    }

    override fun onTouchEvent(ev: MotionEvent): Boolean {
        val action = ev.actionMasked
        if (action == MotionEvent.ACTION_DOWN) flung = false
        if (action == MotionEvent.ACTION_DOWN || action == MotionEvent.ACTION_MOVE) {
            settle?.let {
                settle = null
                it.cancel()   // caught mid-slide: it stays under the finger
            }
            layers(true)
        }
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
        go(if (velocityX > 0) floor(at).toInt() + 1 else ceil(at).toInt() - 1, speed = abs(velocityX).toFloat())
    }

    /** While sliding, each page is drawn once into a layer and moved as a picture: smooth, even with dimming. */
    private fun layers(on: Boolean) {
        if (on == layered) return
        layered = on
        val s = strip ?: return
        for (i in 0 until s.childCount) s.getChildAt(i).setLayerType(if (on) View.LAYER_TYPE_HARDWARE else View.LAYER_TYPE_NONE, null)
    }
}
