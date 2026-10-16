using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace TopNotch
{
    /// <summary>
    /// A ScrollViewer that scrolls like a magnet: a wheel flick never leaves the content
    /// resting between two rows, it springs straight to the neighbouring one. Every row is
    /// assumed to be <see cref="SnapInterval"/> tall (the item template binds its height to
    /// this), so resting positions are always whole multiples of it.
    /// </summary>
    public sealed class SnapScrollViewer : ScrollViewer
    {
        private static readonly Duration SnapDuration = TimeSpan.FromMilliseconds(220);

        private int _index;        // row we're animating towards (only trusted mid-animation)
        private bool _animating;

        public static readonly DependencyProperty SnapIntervalProperty =
            DependencyProperty.Register(nameof(SnapInterval), typeof(double), typeof(SnapScrollViewer),
                new PropertyMetadata(0.0));

        /// <summary>Height of a single row; scrolling always settles on a multiple of it.</summary>
        public double SnapInterval
        {
            get => (double)GetValue(SnapIntervalProperty);
            set => SetValue(SnapIntervalProperty, value);
        }

        public static readonly DependencyProperty ItemCountProperty =
            DependencyProperty.Register(nameof(ItemCount), typeof(int), typeof(SnapScrollViewer),
                new PropertyMetadata(0));

        /// <summary>Number of rows, so scrolling can be clamped to the last one.</summary>
        public int ItemCount
        {
            get => (int)GetValue(ItemCountProperty);
            set => SetValue(ItemCountProperty, value);
        }

        // Not a scroll position itself: animating it drives ScrollToVerticalOffset, since the
        // real offset isn't a dependency property and therefore can't be eased directly.
        private static readonly DependencyProperty AnimatedOffsetProperty =
            DependencyProperty.Register("AnimatedOffset", typeof(double), typeof(SnapScrollViewer),
                new PropertyMetadata(0.0, (d, e) =>
                    ((SnapScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));

        protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
        {
            if (SnapInterval <= 0)
            {
                base.OnPreviewMouseWheel(e);
                return;
            }

            e.Handled = true;   // take over from the default free-form scrolling
            Step(e.Delta < 0 ? 1 : -1);
        }

        /// <summary>Snaps to the next (+) or previous (-) row, easing the whole way.</summary>
        private void Step(int direction)
        {
            if (ItemCount <= 0 || SnapInterval <= 0) return;

            // Derive where we are from the real offset each time, so the row we step from can
            // never drift out of sync (a relayout or a resize can move it behind our back).
            // Mid-animation the offset is between rows, so trust our own target instead.
            int current = _animating ? _index : (int)Math.Round(VerticalOffset / SnapInterval);

            _index = Math.Clamp(current + direction, 0, LastIndex());
            AnimateTo(Math.Min(_index * SnapInterval, ScrollableHeight));
        }

        // The last row that can be brought to rest against the bottom of the viewport.
        private int LastIndex()
        {
            int byContent = (int)Math.Ceiling(ScrollableHeight / SnapInterval);
            return Math.Clamp(byContent, 0, Math.Max(0, ItemCount - 1));
        }

        private void AnimateTo(double target)
        {
            double from = VerticalOffset;   // wherever we're actually showing right now

            BeginAnimation(AnimatedOffsetProperty, null);
            SetCurrentValue(AnimatedOffsetProperty, from);

            _animating = true;
            var anim = new DoubleAnimation(from, target, SnapDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            anim.Completed += (_, _) => _animating = false;

            BeginAnimation(AnimatedOffsetProperty, anim);
        }

        /// <summary>
        /// Called after a row has been inserted at the top of the list. If the user is scrolled
        /// down we ride along by exactly one row, so the same notification stays under their eyes
        /// instead of the whole list silently jumping down a slot.
        /// </summary>
        public void NotifyInsertedAtTop(int itemCount)
        {
            ItemCount = itemCount;
            if (SnapInterval <= 0) return;

            int current = (int)Math.Round(VerticalOffset / SnapInterval);
            if (current <= 0) return;   // already showing the newest; nothing to compensate for

            _index = current + 1;
            AnimateTo(_index * SnapInterval);   // unclamped: one row lower is always reachable
        }
    }
}
