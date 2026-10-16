using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TopNotch {
    public class KnoblessSlider : Slider
    {
        private bool _dragging;

        public KnoblessSlider()
        {
            IsMoveToPointEnabled = true;
        }

        protected override void OnPreviewMouseLeftButtonDown(
            MouseButtonEventArgs e)
        {
            if (!IsEnabled) return;

            _dragging = true;
            CaptureMouse();
            UpdateValue(e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            if (_dragging)
            {
                UpdateValue(e.GetPosition(this));
                e.Handled = true;
            }
        }

        protected override void OnPreviewMouseLeftButtonUp(
            MouseButtonEventArgs e)
        {
            if (_dragging)
            {
                UpdateValue(e.GetPosition(this));
                _dragging = false;
                ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        protected override void OnLostMouseCapture(
            MouseEventArgs e)
        {
            _dragging = false;
            base.OnLostMouseCapture(e);
        }

        private void UpdateValue(Point position)
        {
            if (ActualWidth <= 0) return;

            double percentage = Math.Clamp(
                position.X / ActualWidth, 0, 1);

            Value = Minimum +
                percentage * (Maximum - Minimum);
        }
    }
}