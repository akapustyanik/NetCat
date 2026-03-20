using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Media;

namespace v2rayN.Common;

public static class SmoothScrollBehavior
{
    private const double WheelStep = 115.2;
    private const double TouchpadExponent = 0.65;
    private const double TouchpadMultiplier = 0.9;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty MouseWheelMultiplierProperty =
        DependencyProperty.RegisterAttached(
            "MouseWheelMultiplier",
            typeof(double),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(1d));

    private static readonly DependencyProperty VerticalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "VerticalOffset",
            typeof(double),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(0d, OnVerticalOffsetChanged));

    public static bool GetIsEnabled(DependencyObject obj)
    {
        return (bool)obj.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(DependencyObject obj, bool value)
    {
        obj.SetValue(IsEnabledProperty, value);
    }

    public static double GetMouseWheelMultiplier(DependencyObject obj)
    {
        return (double)obj.GetValue(MouseWheelMultiplierProperty);
    }

    public static void SetMouseWheelMultiplier(DependencyObject obj, double value)
    {
        obj.SetValue(MouseWheelMultiplierProperty, value);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        }
        else
        {
            element.PreviewMouseWheel -= OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject dependencyObject)
        {
            return;
        }

        var scrollViewer = FindScrollViewer(dependencyObject);
        if (scrollViewer == null || scrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        var sourceDependencyObject = e.OriginalSource as DependencyObject;
        var targetScrollViewer = sourceDependencyObject == null
            ? null
            : FindAncestorScrollViewer(sourceDependencyObject) ?? FindDescendantScrollViewer(sourceDependencyObject);

        if (targetScrollViewer != null
            && !ReferenceEquals(targetScrollViewer, scrollViewer)
            && CanScroll(targetScrollViewer, e.Delta))
        {
            return;
        }

        if (!CanScroll(scrollViewer, e.Delta))
        {
            return;
        }

        e.Handled = true;

        var currentOffset = scrollViewer.VerticalOffset;
        var deltaStep = GetScrollStep(scrollViewer, e.Delta);
        var targetOffset = currentOffset - deltaStep;
        targetOffset = Math.Max(0, Math.Min(scrollViewer.ScrollableHeight, targetOffset));

        if (Math.Abs(e.Delta) < 120)
        {
            // Touchpads emit many small deltas; direct pixel scrolling feels smoother than restarting animations.
            scrollViewer.BeginAnimation(VerticalOffsetProperty, null);
            scrollViewer.ScrollToVerticalOffset(targetOffset);
            return;
        }

        var animation = new DoubleAnimation
        {
            From = currentOffset,
            To = targetOffset,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        scrollViewer.BeginAnimation(VerticalOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static double GetScrollStep(ScrollViewer scrollViewer, int delta)
    {
        if (Math.Abs(delta) >= 120)
        {
            var multiplier = Math.Max(0.1, GetMouseWheelMultiplier(scrollViewer));
            return delta / 120.0 * WheelStep * multiplier;
        }

        var direction = Math.Sign(delta);
        var normalized = Math.Abs(delta) / 120.0;
        var accelerated = Math.Pow(normalized, TouchpadExponent) * WheelStep * TouchpadMultiplier;
        return direction * accelerated;
    }

    private static bool CanScroll(ScrollViewer scrollViewer, int delta)
    {
        if (scrollViewer.ScrollableHeight <= 0)
        {
            return false;
        }

        if (delta > 0)
        {
            return scrollViewer.VerticalOffset > 0;
        }

        if (delta < 0)
        {
            return scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight;
        }

        return false;
    }

    private static void OnVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer scrollViewer)
        {
            scrollViewer.ScrollToVerticalOffset((double)e.NewValue);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject dependencyObject)
    {
        if (dependencyObject is ScrollViewer viewer)
        {
            return viewer;
        }

        if (dependencyObject is TextBoxBase textBox)
        {
            return FindDescendantScrollViewer(textBox);
        }

        if (dependencyObject is ItemsControl itemsControl)
        {
            return FindDescendantScrollViewer(itemsControl);
        }

        return FindAncestorScrollViewer(dependencyObject);
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject current)
    {
        while (current != null)
        {
            if (current is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject current)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
        {
            var child = VisualTreeHelper.GetChild(current, i);
            if (child is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            var nested = FindDescendantScrollViewer(child);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }
}
