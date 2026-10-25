using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace TopNotch
{
    public sealed class SnapScrollViewer : ScrollViewer
    {
        private static readonly Duration SnapDuration = TimeSpan.FromMilliseconds(220);

        private int _index;
        private bool _animating;

        public static readonly DependencyProperty SnapIntervalProperty =
            DependencyProperty.Register(nameof(SnapInterval), typeof(double), typeof(SnapScrollViewer),
                new PropertyMetadata(0.0));

        public double SnapInterval
        {
            get => (double)GetValue(SnapIntervalProperty);
            set => SetValue(SnapIntervalProperty, value);
        }

        public static readonly DependencyProperty ItemCountProperty =
            DependencyProperty.Register(nameof(ItemCount), typeof(int), typeof(SnapScrollViewer),
                new PropertyMetadata(0));

        public int ItemCount
        {
            get => (int)GetValue(ItemCountProperty);
            set => SetValue(ItemCountProperty, value);
        }

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

            e.Handled = true;
            Step(e.Delta < 0 ? 1 : -1);
        }

        private void Step(int direction)
        {
            if (ItemCount <= 0 || SnapInterval <= 0) return;

            int current = _animating ? _index : (int)Math.Round(VerticalOffset / SnapInterval);

            _index = Math.Clamp(current + direction, 0, LastIndex());
            AnimateTo(Math.Min(_index * SnapInterval, ScrollableHeight));
        }

        private int LastIndex()
        {
            int byContent = (int)Math.Ceiling(ScrollableHeight / SnapInterval);
            return Math.Clamp(byContent, 0, Math.Max(0, ItemCount - 1));
        }

        private void AnimateTo(double target)
        {
            double from = VerticalOffset;

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

        public void NotifyInsertedAtTop(int itemCount)
        {
            ItemCount = itemCount;
            if (SnapInterval <= 0) return;

            int current = (int)Math.Round(VerticalOffset / SnapInterval);
            if (current <= 0) return;

            _index = current + 1;
            AnimateTo(_index * SnapInterval);
        }
    }
}
