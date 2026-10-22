using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
    /// Fingers are read straight from Windows' pointer messages (OnPointer), not WPF's touch events: those depend on
    /// how the PC is set up, and where they don't arrive Windows turns a swipe into a click.
    /// </summary>
    partial class HomeWindow
    {
        const double DragSlop = 16;        // design pixels (of 1920 across) before a press becomes a drag
        const double FlickGlide = 0.32;    // seconds a flick's speed carries the row or page on for
        const double StretchMost = 140;    // past either end it gives a little, never more than this

        enum DragAxis { None, Row, Page }

        bool _pressed, _dragged, _fingerDown;
        DragAxis _axis;
        int _dragRow, _wheelRow = -1, _wheelAt, _fingerUpAt;
        uint _finger;   // the finger being followed (Windows' pointer id), while _fingerDown
        double _dragFrom, _wheelTarget;
        Point _pressAt;
        FrameworkElement _appsHost;
        readonly List<KeyValuePair<int, double>> _trail = new List<KeyValuePair<int, double>>();   // (time, offset) lately, for a flick's speed
        static readonly IEasingFunction GlideEase = CreateGlideEase();

        void HookDragging()
        {
            // The mouse itself; not a finger WPF has made into a mouse, which OnPointer has dealt with already.
            PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.StylusDevice != null)
                {
                    if (FingerJustHere) e.Handled = true;
                    return;
                }
                Press(e.GetPosition(_layers));
            };
            PreviewMouseMove += (s, e) =>
            {
                if (e.StylusDevice == null && _pressed && e.LeftButton == MouseButtonState.Pressed) DragTo(e.GetPosition(_layers));
            };
            MouseWheel += OnWheel;
        }

        /// <summary>
        /// The left button let go (OnClick asks first): true when there's nothing more to do: it was a drag, which
        /// doesn't click, or WPF's click made of a finger OnPointer has dealt with.
        /// </summary>
        bool EndPress(MouseButtonEventArgs e)
        {
            bool done = e.StylusDevice == null ? Release() : FingerJustHere;
            if (done) e.Handled = true;
            return done;
        }

        // ---------------------------------------------------------------- fingers, from Windows

        const int WM_POINTERUPDATE = 0x245, WM_POINTERDOWN = 0x246, WM_POINTERUP = 0x247, WM_POINTERCAPTURECHANGED = 0x24C;
        const int PT_TOUCH = 2, POINTER_FLAG_PRIMARY = 0x2000;

        /// <summary>A finger was followed here a moment ago (or still is): WPF's own mouse clicks made of it are ignored.</summary>
        bool FingerJustHere { get { return _fingerDown || unchecked(Environment.TickCount - _fingerUpAt) < 1500; } }

        /// <summary>
        /// A finger on the screen (the screen mirror's touches), as Windows' pointer messages, before WPF sees them:
        /// true if it's been dealt with here. The first finger slides a row or the page; lifted without sliding, it's a
        /// click there (a real one, so whatever's under it, a tile or the update button, does what a click does).
        /// While a dialog's open, fingers are left to Windows and WPF as before.
        /// </summary>
        bool OnPointer(int msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg != WM_POINTERDOWN && msg != WM_POINTERUPDATE && msg != WM_POINTERUP && msg != WM_POINTERCAPTURECHANGED) return false;
            uint id = (uint)(wParam.ToInt64() & 0xFFFF);
            if (msg == WM_POINTERCAPTURECHANGED)
            {
                if (_fingerDown && id == _finger)
                {
                    _fingerDown = false;
                    _fingerUpAt = Environment.TickCount;
                    Release();
                }
                return false;
            }
            int type;
            if (!GetPointerType(id, out type) || type != PT_TOUCH) return false;
            int x = (short)(lParam.ToInt64() & 0xFFFF), y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            Point at = _layers.PointFromScreen(new Point(x, y));
            if (msg == WM_POINTERDOWN)
            {
                if (_fingerDown) return true;   // a second finger: nothing, while the first slides
                bool primary = ((wParam.ToInt64() >> 16) & POINTER_FLAG_PRIMARY) != 0;
                if (!primary || !Press(at)) return false;
                _finger = id;
                _fingerDown = true;
                return true;
            }
            if (!_fingerDown || id != _finger) return false;
            if (msg == WM_POINTERUPDATE)
            {
                DragTo(at);
                return true;
            }
            _fingerDown = false;
            _fingerUpAt = Environment.TickCount;
            DragAxis axis = _axis;
            int row = _dragRow;
            bool dragged = Release();
            if (dragged) Log.Info("Home screen: a finger slid " + (axis == DragAxis.Row ? "row " + row + " to " + Math.Round(_scroll[row]) : axis == DragAxis.Page ? "the page to " + Math.Round(-_pageShift.Y) : "nothing"));
            else
            {
                Log.Info("Home screen: a finger tapped at " + (int)at.X + "," + (int)at.Y);
                _pointer = true;   // as the mouse: the tile under it gets the click, not the one with the focus
                Input.MoveTo(x, y);
                Input.Click(false);
            }
            return true;
        }

        [DllImport("user32.dll")] static extern bool GetPointerType(uint pointerId, out int pointerType);

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
                    _dragFrom = -Shift(_dragRow).X;
                }
                else if (PageLimit() > 0)
                {
                    _axis = DragAxis.Page;
                    _dragFrom = -_pageShift.Y;
                }
                if (!_fingerDown) CaptureMouse();
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
                double now = _axis == DragAxis.Row ? -Shift(_dragRow).X : -_pageShift.Y;
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
            TranslateTransform shift = _axis == DragAxis.Row ? Shift(_dragRow) : _pageShift;
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
            TranslateTransform shift = row >= 0 ? Shift(row) : _pageShift;
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
                double top = Canvas.GetTop(host), bottom = top + host.Height + (r > RowApps && r < RowSystem ? SuggestionTextH : 0);
                if (onPage >= top - 16 && onPage <= bottom + 16) return r;
            }
            return -1;
        }

        FrameworkElement RowHost(int r)
        {
            if (r == RowApps) return _appsHost;
            return r > RowApps && r < RowSystem ? _content[r - 1].Host : _sysHost;
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

        internal static int MoviesRow { get { return 1; } }   // the first suggestion row: the films
        internal double Slid(int row) { return row >= 0 ? -Shift(row).X : -_pageShift.Y; }
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
