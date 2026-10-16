package com.couchtv.remote

import android.app.AlertDialog
import android.content.Context
import android.content.res.ColorStateList
import android.graphics.drawable.GradientDrawable
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

/**
 * What the status icons at the top stand for, in full: the TV over Wi-Fi, the receiver box, this phone's IR blaster,
 * each with what to do about it, and the app's version. It keeps up while it's open ([update]).
 */
class StatusPanel(private val context: Context) {
    /** One line: an icon in its colour, what it is, how it is, and something to do about it. */
    class Row(val icon: Int, val color: Int, val title: String, val detail: String, val action: String? = null, val onAction: (() -> Unit)? = null)

    private var dialog: AlertDialog? = null
    private var rows: LinearLayout? = null
    private val density = context.resources.displayMetrics.density

    fun show(lines: List<Row>, version: String, onVersion: () -> Unit) {
        val list = LinearLayout(context).apply { orientation = LinearLayout.VERTICAL }
        rows = list
        fill(lines)
        val footer = TextView(context).apply {
            text = version
            setTextColor(context.getColor(R.color.muted))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
            gravity = Gravity.CENTER
            setPadding(0, dp(14), 0, dp(4))
            setOnClickListener {
                dialog?.dismiss()
                onVersion()
            }
        }
        val content = LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(8), dp(20), 0)
            addView(list)
            addView(footer)
        }
        dialog = AlertDialog.Builder(context, android.R.style.Theme_Material_Dialog_Alert)
            .setView(ScrollView(context).apply { addView(content) })
            .setPositiveButton(R.string.done, null)
            .setOnDismissListener {
                dialog = null
                rows = null
            }
            .show()
    }

    fun dismiss() {
        dialog?.dismiss()
    }

    /** New lines while it's open (the TV came or went, say). */
    fun update(lines: List<Row>) {
        if (dialog?.isShowing == true) fill(lines)
    }

    private fun fill(lines: List<Row>) {
        val list = rows ?: return
        list.removeAllViews()
        for (line in lines) list.addView(row(line))
    }

    private fun row(line: Row): View {
        val icon = ImageView(context).apply {
            setImageResource(line.icon)
            imageTintList = ColorStateList.valueOf(context.getColor(line.color))
            scaleType = ImageView.ScaleType.CENTER
            background = GradientDrawable().apply {
                shape = GradientDrawable.OVAL
                setColor(context.getColor(R.color.surface_raised))
            }
        }
        val words = LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL
            addView(TextView(context).apply {
                text = line.title
                setTextColor(context.getColor(R.color.ink))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 16f)
                typeface = android.graphics.Typeface.create("sans-serif-medium", android.graphics.Typeface.NORMAL)
            })
            addView(TextView(context).apply {
                text = line.detail
                setTextColor(context.getColor(R.color.muted))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
                setPadding(0, dp(2), 0, 0)
            })
            if (line.action != null) addView(TextView(context).apply {
                text = line.action
                setTextColor(context.getColor(R.color.good))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
                typeface = android.graphics.Typeface.create("sans-serif-medium", android.graphics.Typeface.NORMAL)
                setPadding(0, dp(8), dp(16), dp(4))
                setOnClickListener {
                    dialog?.dismiss()
                    line.onAction?.invoke()
                }
            })
        }
        return LinearLayout(context).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(0, dp(12), 0, dp(12))
            addView(icon, LinearLayout.LayoutParams(dp(44), dp(44)))
            addView(words, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply { marginStart = dp(16) })
        }
    }

    private fun dp(value: Int) = (value * density + 0.5f).toInt()
}
