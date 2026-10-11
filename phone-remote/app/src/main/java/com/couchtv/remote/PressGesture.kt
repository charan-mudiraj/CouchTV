package com.couchtv.remote

import android.os.SystemClock
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import kotlin.math.abs

/**
 * Turns touches on a remote button into presses without blocking the page's swiping, and makes it feel like a real
 * key: it shows pressed (sinking, with animator/key_press) and clicks under the finger (Haptics).
 *
 * A press starts once the finger has stayed put for a moment (the same short delay Android uses for buttons in
 * scrolling lists), or straight away when the finger lifts quickly, which is a tap. If the finger moves first, it's
 * a swipe: nothing is sent and the page turns. Once a press has started, the page can no longer take over, so
 * holding (e.g. Back for Home) keeps working even if the finger wobbles. Even a quick tap shows the key down for a
 * moment, as a real one would be, though the TV is told it's released straight away.
 */
class PressGesture(
    private val view: View,
    private val onPress: () -> Unit,
    private val onRelease: () -> Unit,
) {
    private companion object {
        const val SHOWN_DOWN_MS = 90L
    }

    private val touchSlop = ViewConfiguration.get(view.context).scaledTouchSlop
    private val pressDelay = ViewConfiguration.getTapTimeout().toLong()
    private var downX = 0f
    private var downY = 0f
    private var waiting = false
    private var pressed = false
    private var pressedAt = 0L
    private val startPress = Runnable { begin() }
    private val lift = Runnable { view.isPressed = false }

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
                // The page took the touch over to turn.
                stopWaiting()
                if (pressed) end()
            }
        }
        return true
    }

    private fun begin() {
        waiting = false
        pressed = true
        pressedAt = SystemClock.uptimeMillis()
        view.parent?.requestDisallowInterceptTouchEvent(true)
        view.removeCallbacks(lift)
        view.isPressed = true
        Haptics.press(view)
        onPress()
    }

    private fun end() {
        pressed = false
        onRelease()
        Haptics.release(view)
        val down = SystemClock.uptimeMillis() - pressedAt
        if (down >= SHOWN_DOWN_MS) view.isPressed = false else view.postDelayed(lift, SHOWN_DOWN_MS - down)
    }

    private fun stopWaiting() {
        if (!waiting) return
        waiting = false
        view.removeCallbacks(startPress)
    }
}
