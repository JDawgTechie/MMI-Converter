using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace MmiConverter.Services;

public static class SmoothScrollService
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScrollService), new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty LineHeightProperty = DependencyProperty.RegisterAttached(
        "LineHeight", typeof(double), typeof(SmoothScrollService),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.Inherits));

    private static readonly DependencyProperty AnimationProperty = DependencyProperty.RegisterAttached(
        "Animation", typeof(WheelAnimation), typeof(SmoothScrollService));

    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static double GetLineHeight(DependencyObject target) => (double)target.GetValue(LineHeightProperty);
    public static void SetLineHeight(DependencyObject target, double value) => target.SetValue(LineHeightProperty, value);

    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ScrollViewer viewer) return;
        if ((bool)e.NewValue)
            viewer.SetValue(AnimationProperty, new WheelAnimation(viewer));
        else if (viewer.GetValue(AnimationProperty) is WheelAnimation animation)
        {
            animation.Detach();
            viewer.ClearValue(AnimationProperty);
        }
    }

    private sealed class WheelAnimation
    {
        private const double DurationMilliseconds = 160;
        private readonly ScrollViewer viewer;
        private readonly DispatcherTimer timer;
        private double startOffset;
        private double destination;
        private double requestedOffset;
        private long started;

        internal WheelAnimation(ScrollViewer viewer)
        {
            this.viewer = viewer;
            timer = new DispatcherTimer(DispatcherPriority.Render, viewer.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            timer.Tick += OnTick;
            viewer.PreviewMouseWheel += OnWheel;
            viewer.PreviewMouseDown += OnMouseDown;
            viewer.PreviewKeyDown += OnKeyDown;
            viewer.ScrollChanged += OnScrollChanged;
            viewer.Unloaded += OnUnloaded;
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || viewer.ScrollableHeight <= 0 || e.Delta == 0) return;
            e.Handled = true;
            int lines = SystemParameters.WheelScrollLines;
            if (lines == 0) return;

            double distance = lines < 0 || lines == int.MaxValue
                ? viewer.ViewportHeight
                : GetLineHeight(viewer) * lines;
            startOffset = viewer.VerticalOffset;
            double previousTarget = timer.IsEnabled ? destination : startOffset;
            destination = Math.Clamp(previousTarget - e.Delta / 120.0 * distance, 0, viewer.ScrollableHeight);
            requestedOffset = startOffset;
            if (Math.Abs(destination - startOffset) < 0.01)
            {
                timer.Stop();
                return;
            }

            started = Stopwatch.GetTimestamp();
            timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            double progress = Math.Min(1, Stopwatch.GetElapsedTime(started).TotalMilliseconds / DurationMilliseconds);
            double eased = 1 - Math.Pow(1 - progress, 3);
            requestedOffset = Math.Clamp(startOffset + (destination - startOffset) * eased, 0, viewer.ScrollableHeight);
            viewer.ScrollToVerticalOffset(requestedOffset);
            if (progress >= 1) timer.Stop();
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (timer.IsEnabled && e.VerticalChange != 0 && Math.Abs(e.VerticalOffset - requestedOffset) > 1)
                timer.Stop();
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e) => timer.Stop();
        private void OnKeyDown(object sender, KeyEventArgs e) => timer.Stop();
        private void OnUnloaded(object sender, RoutedEventArgs e) => timer.Stop();

        internal void Detach()
        {
            timer.Stop();
            timer.Tick -= OnTick;
            viewer.PreviewMouseWheel -= OnWheel;
            viewer.PreviewMouseDown -= OnMouseDown;
            viewer.PreviewKeyDown -= OnKeyDown;
            viewer.ScrollChanged -= OnScrollChanged;
            viewer.Unloaded -= OnUnloaded;
        }
    }
}
