package com.couchtv.remote

import android.content.Context
import android.util.AttributeSet
import android.view.View
import android.view.ViewGroup
import kotlin.math.min

/**
 * The remote page's column, top to bottom: each part at its own height, the D-pad as big as the width allows (smaller
 * on a short screen), and the height left over shared out evenly between the parts, so a tall phone has no hole round
 * the D-pad. Past [MOST_GAP_DP] a gap, what's still left goes above and below the whole column.
 */
class RemoteColumn @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : ViewGroup(context, attrs) {
    private companion object {
        const val MOST_GAP_DP = 28f
    }

    private val density = resources.displayMetrics.density
    private var gap = 0
    private var lead = 0   // above the first part

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val width = MeasureSpec.getSize(widthMeasureSpec)
        val height = MeasureSpec.getSize(heightMeasureSpec)
        val inner = width - paddingLeft - paddingRight
        val room = height - paddingTop - paddingBottom
        var used = 0
        var parts = 0
        var pad: DpadView? = null
        for (i in 0 until childCount) {
            val child = getChildAt(i)
            if (child.visibility == View.GONE) continue
            parts++
            val lp = child.layoutParams as MarginLayoutParams
            used += lp.topMargin + lp.bottomMargin
            if (child is DpadView) {
                pad = child
                continue
            }
            val heightSpec = if (lp.height >= 0) MeasureSpec.makeMeasureSpec(lp.height, MeasureSpec.EXACTLY)
                             else MeasureSpec.makeMeasureSpec(0, MeasureSpec.UNSPECIFIED)
            child.measure(MeasureSpec.makeMeasureSpec(inner - lp.leftMargin - lp.rightMargin, MeasureSpec.EXACTLY), heightSpec)
            used += child.measuredHeight
        }
        pad?.let {
            val lp = it.layoutParams as MarginLayoutParams
            val padWidth = inner - lp.leftMargin - lp.rightMargin
            val padHeight = min(it.naturalHeight(padWidth), room - used).coerceAtLeast(0)
            it.measure(MeasureSpec.makeMeasureSpec(padWidth, MeasureSpec.EXACTLY), MeasureSpec.makeMeasureSpec(padHeight, MeasureSpec.EXACTLY))
            used += padHeight
        }
        val left = (room - used).coerceAtLeast(0)
        gap = if (parts > 1) min(left / (parts - 1), (MOST_GAP_DP * density).toInt()) else 0
        lead = (left - gap * (parts - 1).coerceAtLeast(0)) / 2
        setMeasuredDimension(width, height)
    }

    override fun onLayout(changed: Boolean, l: Int, t: Int, r: Int, b: Int) {
        var y = paddingTop + lead
        for (i in 0 until childCount) {
            val child = getChildAt(i)
            if (child.visibility == View.GONE) continue
            val lp = child.layoutParams as MarginLayoutParams
            y += lp.topMargin
            val x = paddingLeft + lp.leftMargin
            child.layout(x, y, x + child.measuredWidth, y + child.measuredHeight)
            y += child.measuredHeight + lp.bottomMargin + gap
        }
    }

    override fun generateLayoutParams(attrs: AttributeSet): LayoutParams = MarginLayoutParams(context, attrs)
    override fun generateDefaultLayoutParams(): LayoutParams = MarginLayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT)
    override fun generateLayoutParams(p: LayoutParams): LayoutParams = MarginLayoutParams(p)
    override fun checkLayoutParams(p: LayoutParams): Boolean = p is MarginLayoutParams
}
