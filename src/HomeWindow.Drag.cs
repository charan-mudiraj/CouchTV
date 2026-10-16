using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CouchTV
{
    /// <summary>
    /// Sliding the home screen with a finger (the phone's screen mirror sends real touches) or by dragging the mouse:
    /// sideways along a row slides the row, up and down slides the page, and a flick glides on and settles on a card.
    /// A press that moves less than DragSlop is still a tap, and opens what's under it; a drag never opens anything.
    /// The wheel scrolls too: the page up and down, or sideways along the row under the pointer (a tilting wheel,
    /// Shift and the wheel, or the mirror's fingers when they work as the mouse).
    /// </summary>
    partial class HomeWindow
    {
        const double DragSlop = 16;        // design pixels (of 1920 across) before a press becomes a drag
        const double FlickGlide = 0.32;    // seconds a flick's speed carries the row or page on for
        const double StretchMost = 140;    // past either end it gives a little, never more than this

        enum DragAxis { None, Row, Page }

        bool _pressed, _dragged, _swallowClick;
        DragAxis _axis;
        int _dragRow, _wheelRow = -1, _wheelAt;
        double _dragFrom, _wheelTarget;
        Point _pressAt;
        TouchDevice _touch;
        FrameworkElement _appsHost;
        readonly List<KeyValuePair<int, double>> _trail = new List<KeyValuePair<int, double>>();   // (time, offset) lately, for a flick's speed
        static readonly IEasingFunction GlideEase = CreateGlideEase();

        void HookDragging()
        {
            PreviewTouchDown += (s, e) =>
            {
                _swallowClick = false;
                if (_touch == null && Press(e.GetTouchPoint(_layers).Position)) _touch = e.TouchDevice;
            };
            PreviewTouchMove += (s, e) => { if (e.TouchDevice == _touch) DragTo(e.GetTouchPoint(_layers).Position); };
            PreviewTouchUp += (s, e) =>
            {
                if (e.TouchDevice != _touch) return;
                _touch = null;
                if (Release()) _swallowClick = true;   // the click Windows makes of this touch comes next: not that
            };
            // The mouse itself (not a finger made into a mouse by WPF, which the touch events have already handled).
            PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.StylusDevice != null) return;
                _swallowClick = false;
                Press(e.GetPosition(_layers));
            };
            PreviewMouseMove += (s, e) =>
            {
                if (e.StylusDevice == null && _pressed && e.LeftButton == MouseButtonState.Pressed) DragTo(e.GetPosition(_layers));
            };
            MouseWheel += OnWheel;
        }

        /// <summary>The left button or a finger let go (OnClick asks first): true when it was a drag, so it doesn't click.</summary>
        bool EndPress(MouseButtonEventArgs e)
        {
            bool drag = e.StylusDevice == null ? Release() : _swallowClick;
            _swallowClick = false;
            if (drag) e.Handled = true;
            return drag;
        }

        bool Press(Point at)
        {
            if (Modal) return false;
            _pressed = true;
            _dragged = false;
            _axis = DragAxis.None;
            _pressAt = at;
            _dragRow = RowAt(at.Y);
            _trail.Clear();
            return true;
        }

        void DragTo(Point at)
        {
            if (!_pressed) return;
            if (!_dragged)
            {
                double dx = at.X - _pressAt.X, dy = at.Y - _pressAt.Y;
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < DragSlop) return;
                _dragged = true;
                _pressAt = at;   // slides from here, so it doesn't jump by the slop
                if (Math.Abs(dx) > Math.Abs(dy) && _dragRow >= 0 && RowLimit(_dragRow) > 0)
                {
                    _axis = DragAxis.Row;
                    _dragFrom = -_shift[_dragRow].X;
                }
                else if (PageLimit() > 0)
                {
                    _axis = DragAxis.Page;
                    _dragFrom = -_pageShift.Y;
                }
                if (_touch == null) CaptureMouse();
            }
            if (_axis == DragAxis.None) return;   // nothing there slides: it just isn't a tap
            double moved = _axis == DragAxis.Row ? at.X - _pressAt.X : at.Y - _pressAt.Y;
            double offset = Stretched(_dragFrom - moved, _axis == DragAxis.Row ? RowLimit(_dragRow) : PageLimit());
            SetSlide(offset, 0);
            int now = Environment.TickCount;
            _trail.Add(new KeyValuePair<int, double>(now, offset));
            while (_trail.Count > 2 && unchecked(now - _trail[0].Key) > 100) _trail.RemoveAt(0);
        }

        /// <summary>Let go: a drag glides on at the speed it had and settles (a row on a card); true if it was a drag.</summary>
        bool Release()
        {
            if (!_pressed) return false;
            _pressed = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            if (_axis != DragAxis.None)
            {
                double limit = _axis == DragAxis.Row ? RowLimit(_dragRow) : PageLimit();
                double now = _axis == DragAxis.Row ? -_shift[_dragRow].X : -_pageShift.Y;
                double speed = 0;   // design pixels a second
                if (_trail.Count >= 2 && unchecked(Environment.TickCount - _trail[_trail.Count - 1].Key) < 80)
                {
                    var first = _trail[0];
                    var last = _trail[_trail.Count - 1];
                    int ms = unchecked(last.Key - first.Key);
                    if (ms > 0) speed = (last.Value - first.Value) * 1000 / ms;
                }
                double target = now + speed * FlickGlide;
                if (_axis == DragAxis.Row)
                {
                    double pitch, item;
                    RowMetrics(_dragRow, out pitch, out item);
                    target = Math.Round(target / pitch) * pitch;   // a card's edge at the row's start
                }
                target = Math.Max(0, Math.Min(limit, target));
                double distance = Math.Abs(target - now);
                double glide = Math.Abs(speed) > 200 ? distance / Math.Abs(speed) * 1000 * 2.4 : 260 + distance * 0.5;
                SetSlide(target, Math.Max(260, Math.Min(900, glide)));
                if (_axis == DragAxis.Row) _scroll[_dragRow] = target;
            }
            _axis = DragAxis.None;
            return _dragged;
        }

        /// <summary>Puts the row being dragged (or the page) at this offset: at once, or gliding there over ms.</summary>
        void SetSlide(double offset, double ms)
        {
            TranslateTransform shift = _axis == DragAxis.Row ? _shift[_dragRow] : _pageShift;
            DependencyProperty property = _axis == DragAxis.Row ? TranslateTransform.XProperty : TranslateTransform.YProperty;
            if (ms <= 0)
            {
                Theme.AnimateTo(shift, property, -offset, false);
                return;
            }
            shift.BeginAnimation(property, new DoubleAnimation(-offset, TimeSpan.FromMilliseconds(ms)) { EasingFunction = GlideEase });
        }

        /// <summary>Past either end the offset gives less and less, as on a phone.</summary>
        static double Stretched(double offset, double limit)
        {
            if (offset < 0) return -Give(-offset);
            if (offset > limit) return limit + Give(offset - limit);
            return offset;
        }

        static double Give(double past) { return StretchMost * (1 - 1 / (past * 0.35 / StretchMost + 1)); }

        // ---------------------------------------------------------------- the wheel

        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (Modal || Keyboard.Modifiers == ModifierKeys.Control) return;
            e.Handled = true;
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                int row = RowAt(e.GetPosition(_layers).Y);
                if (row >= 0) WheelSideways(row, -e.Delta);
            }
            else WheelSlide(-1, -e.Delta);   // the wheel towards you (negative) goes down the page
        }

        /// <summary>A sideways wheel turn (WM_MOUSEHWHEEL), to the right when positive: the row under the pointer.</summary>
        void WheelSideways(int delta)
        {
            if (Modal) return;
            int row = RowAt(Mouse.GetPosition(_layers).Y);
            if (row >= 0) WheelSideways(row, delta);
        }

        void WheelSideways(int row, int delta)
        {
            if (RowLimit(row) > 0) WheelSlide(row, delta);
        }

        /// <summary>
        /// Slides a row (or the page, row -1) by a wheel turn: a notch glides, the mirror's many small turns follow the
        /// finger. Quick turns add up from where the last one was going, not from where the glide has got to.
        /// </summary>
        void WheelSlide(int row, int delta)
        {
            TranslateTransform shift = row >= 0 ? _shift[row] : _pageShift;
            DependencyProperty property = row >= 0 ? TranslateTransform.XProperty : TranslateTransform.YProperty;
            double limit = row >= 0 ? RowLimit(row) : PageLimit();
            if (limit <= 0) return;
            int now = Environment.TickCount;
            double from = row == _wheelRow && unchecked(now - _wheelAt) < 250 ? _wheelTarget : -(double)shift.GetValue(property);
            double to = Math.Max(0, Math.Min(limit, from + delta * 1.3));
            _wheelRow = row;
            _wheelAt = now;
            _wheelTarget = to;
            Theme.AnimateTo(shift, property, -to, Math.Abs(delta) >= 120);
            if (row >= 0) _scroll[row] = to;
        }

        // ---------------------------------------------------------------- where things are

        /// <summary>The row whose tiles are at this height on the screen (design pixels), or -1.</summary>
        int RowAt(double y)
        {
            double onPage = y - _pageShift.Y;
            for (int r = 0; r < RowCount; r++)
            {
                FrameworkElement host = RowHost(r);
                if (host == null || host.Visibility != Visibility.Visible || Row(r).Count == 0) continue;
                double top = Canvas.GetTop(host), bottom = top + host.Height + (r == RowMovies || r == RowSongs ? SuggestionTextH : 0);
                if (onPage >= top - 16 && onPage <= bottom + 16) return r;
            }
            return -1;
        }

        FrameworkElement RowHost(int r)
        {
            return r == RowApps ? _appsHost : r == RowMovies ? _movieHost : r == RowSongs ? _songHost : _sysHost;
        }

        /// <summary>How far a row slides at most (0: it all fits).</summary>
        double RowLimit(int r)
        {
            double pitch, item;
            return RowMetrics(r, out pitch, out item);
        }

        /// <summary>How far the page slides at most (0 without suggestions: it all fits).</summary>
        double PageLimit() { return Math.Max(0, _pageBottom - DesignH); }

        // ---------------------------------------------------------------- for the self-test (Diagnostics.DragTest)

        internal static int MoviesRow { get { return RowMovies; } }
        internal double Slid(int row) { return row >= 0 ? -_shift[row].X : -_pageShift.Y; }
        internal double SlideLimit(int row) { return row >= 0 ? RowLimit(row) : PageLimit(); }

        internal double TilePitch(int row)
        {
            double pitch, item;
            RowMetrics(row, out pitch, out item);
            return pitch;
        }

        /// <summary>Where a point on a row is on the screen, in pixels: x across the design's 1920, in the middle of its tiles.</summary>
        internal Point ScreenPoint(int row, double x)
        {
            FrameworkElement host = RowHost(row);
            return _layers.PointToScreen(new Point(x, Canvas.GetTop(host) + host.Height / 2 + _pageShift.Y));
        }

        static IEasingFunction CreateGlideEase()
        {
            var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
            ease.Freeze();
            return ease;
        }
    }
}
