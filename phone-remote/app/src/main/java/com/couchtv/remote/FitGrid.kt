package com.couchtv.remote

import android.content.Context
import android.util.AttributeSet
import android.view.View
import android.view.ViewGroup
import kotlin.math.max
import kotlin.math.min

/**
 * Its children in a grid of [columns], from the top. Rows are [rowHeight] tall, or shorter when that many don't fit
 * the space, so the grid never needs scrolling. With [fillLastRow], a short last row shares out the full width
 * (e.g. five app tiles: two, two, then one wide one).
 */
class FitGrid @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : ViewGroup(context, attrs) {
    var columns = 3
    var rowHeight = (60 * resources.displayMetrics.density).toInt()
    var gap = (10 * resources.displayMetrics.density).toInt()
    var fillLastRow = false

    private var cellHeight = 0

    init {
        context.obtainStyledAttributes(attrs, R.styleable.FitGrid).apply {
            columns = getInt(R.styleable.FitGrid_columns, columns)
            rowHeight = getDimensionPixelSize(R.styleable.FitGrid_rowHeight, rowHeight)
            gap = getDimensionPixelSize(R.styleable.FitGrid_gap, gap)
            fillLastRow = getBoolean(R.styleable.FitGrid_fillLastRow, fillLastRow)
            recycle()
        }
    }

    private fun shown(): List<View> = (0 until childCount).map { getChildAt(it) }.filter { it.visibility != View.GONE }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val width = MeasureSpec.getSize(widthMeasureSpec)
        val children = shown()
        val rows = (children.size + columns - 1) / columns
        val wanted = if (rows == 0) 0 else rows * rowHeight + (rows - 1) * gap
        val height = when (MeasureSpec.getMode(heightMeasureSpec)) {
            MeasureSpec.EXACTLY -> MeasureSpec.getSize(heightMeasureSpec)
            MeasureSpec.AT_MOST -> min(wanted, MeasureSpec.getSize(heightMeasureSpec))
            else -> wanted
        }
        cellHeight = if (rows == 0) 0 else max(0, min(rowHeight, (height - (rows - 1) * gap) / rows))
        children.forEachIndexed { i, child ->
            child.measure(
                MeasureSpec.makeMeasureSpec(cellWidth(width, i, children.size), MeasureSpec.EXACTLY),
                MeasureSpec.makeMeasureSpec(cellHeight, MeasureSpec.EXACTLY),
            )
        }
        setMeasuredDimension(width, height)
    }

    override fun onLayout(changed: Boolean, l: Int, t: Int, r: Int, b: Int) {
        val children = shown()
        children.forEachIndexed { i, child ->
            val row = i / columns
            val w = child.measuredWidth
            val left = (i % columns) * (w + gap)
            val top = row * (cellHeight + gap)
            child.layout(left, top, left + w, top + cellHeight)
        }
    }

    /** How wide child [i] of [count] is: a column, or a share of the last row when that's short and filled. */
    private fun cellWidth(width: Int, i: Int, count: Int): Int {
        val inLastRow = count % columns
        val across = if (fillLastRow && inLastRow != 0 && i >= count - inLastRow) inLastRow else columns
        return max(0, (width - (across - 1) * gap) / across)
    }
}
