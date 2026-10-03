package com.couchtv.remote

import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import kotlin.math.abs

/**
 * Turns touches on a remote button into presses without blocking the page's scrolling.
 *
 * A press starts once the finger has stayed put for a moment (the same short delay Android uses for buttons in
 * scrolling lists), or straight away when the finger lifts quickly, which is a tap. If the finger moves first, it's
 * a scroll: nothing is sent and the page scrolls. Once a press has started, the page can no longer take over, so
 * holding (e.g. Back for Home) keeps working even if the finger wobbles.
 */
class PressGesture(
    private val view: View,
    private val onPress: () -> Unit,
    private val onRelease: () -> Unit,
) {
    private val touchSlop = ViewConfiguration.get(view.context).scaledTouchSlop
    private val pressDelay = ViewConfiguration.getTapTimeout().toLong()
    private var downX = 0f
    private var downY = 0f
    private var waiting = false
    private var pressed = false
    private val startPress = Runnable { begin() }

    fun onTouch(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                downX = event.x
                downY = event.y
                waiting = true
                view.postDelayed(startPress, pressDelay)
            }
            MotionEvent.ACTION_MOVE -> {
                if (waiting && (abs(event.x - downX) > touchSlop || abs(event.y - downY) > touchSlop)) stopWaiting()
            }
            MotionEvent.ACTION_UP -> {
                if (waiting) {
                    // A quick tap: send it now.
                    stopWaiting()
                    begin()
                }
                if (pressed) end()
            }
            MotionEvent.ACTION_CANCEL -> {
                // The page took the touch over to scroll.
                stopWaiting()
                if (pressed) end()
            }
        }
        return true
    }

    private fun begin() {
        waiting = false
        pressed = true
        view.parent?.requestDisallowInterceptTouchEvent(true)
        view.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
        onPress()
    }

    private fun end() {
        pressed = false
        onRelease()
    }

    private fun stopWaiting() {
        if (!waiting) return
        waiting = false
        view.removeCallbacks(startPress)
    }
}
