using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;

namespace NetCat.UI;

public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(ScrollState), typeof(SmoothScroll));
    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);
    private static void Changed(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is not FrameworkElement element) return;
        if ((bool)args.NewValue) { element.PreviewMouseWheel += Wheel; element.PreviewMouseDown += Interrupt; element.PreviewKeyDown += Interrupt; element.Unloaded += Interrupt; }
        else { element.PreviewMouseWheel -= Wheel; element.PreviewMouseDown -= Interrupt; element.PreviewKeyDown -= Interrupt; element.Unloaded -= Interrupt; Interrupt(element, new RoutedEventArgs()); }
    }
    internal static ScrollViewer? FindViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    private static void Interrupt(object sender, RoutedEventArgs args) => ((DependencyObject)sender).GetValue(StateProperty).AsState()?.Stop();
    private static ScrollState? AsState(this object value) => value as ScrollState;
    private static void Wheel(object sender, MouseWheelEventArgs args)
    {
        if (args.Handled || (!App.IsSmoke && Keyboard.Modifiers != ModifierKeys.None) || SystemParameters.WheelScrollLines == 0) return;
        var element = (FrameworkElement)sender;
        // Preview tunnels from outer to inner. Start with the viewer under the
        // pointer so a page does not steal input from an embedded list/editor.
        var source = args.OriginalSource as DependencyObject;
        ScrollViewer? viewer = null;
        while (source != null)
        {
            if (source is ScrollViewer found) { viewer = found; break; }
            source = Parent(source);
        }
        viewer ??= FindViewer(element);
        // A ListBox's own ScrollViewer consumes bubbling wheel events even when
        // it has no overflow. Handle the nearest scrollable ancestor in preview.
        while (viewer != null)
        {
            var pixels = SystemParameters.WheelScrollLines < 0 ? viewer.ViewportHeight * .85 : SystemParameters.WheelScrollLines * 16;
            if (Move(viewer, viewer, -args.Delta / 120d * pixels, Math.Abs(args.Delta) < 120)) { args.Handled = true; return; }
            var parent = Parent(viewer);
            while (parent != null && parent is not ScrollViewer) parent = Parent(parent);
            viewer = parent as ScrollViewer;
        }
    }
    private static DependencyObject? Parent(DependencyObject item) => item is Visual or System.Windows.Media.Media3D.Visual3D
        ? VisualTreeHelper.GetParent(item) : item is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(item);
    internal static bool ScrollBy(FrameworkElement element, double delta)
    {
        var viewer = FindViewer(element);
        if (viewer == null) return false;
        return Move(viewer, viewer, delta);
    }
    private static bool Move(FrameworkElement element, ScrollViewer viewer, double delta, bool precise = false)
    {
        if (viewer.ScrollableHeight <= 0) return false;
        var state = element.GetValue(StateProperty) as ScrollState;
        if (state == null || state.Viewer != viewer) { state?.Stop(); state = new ScrollState(viewer); element.SetValue(StateProperty, state); }
        return state.Move(delta, precise);
    }
    private sealed class ScrollState
    {
        public ScrollViewer Viewer { get; }
        private bool moving;
        private bool pendingOffset;
        private bool preciseInput;
        private double target;
        private long previousFrame;
        private double position;
        public ScrollState(ScrollViewer viewer)
        {
            Viewer = viewer;
            viewer.Unloaded += (_, _) => Stop();
            viewer.PreviewMouseDown += (_, _) => Stop();
            viewer.PreviewKeyDown += (_, _) => Stop();
            viewer.ScrollChanged += (_, _) =>
            {
                if (pendingOffset && Math.Abs(Viewer.VerticalOffset - target) < .01)
                    pendingOffset = false;
            };
        }
        public void Stop()
        {
            CompositionTarget.Rendering -= Frame;
            moving = pendingOffset = false;
            target = position = Viewer.VerticalOffset;
        }
        public bool Move(double delta, bool precise)
        {
            var basis = moving || pendingOffset ? target : Viewer.VerticalOffset;
            var next = Math.Clamp(basis + delta, 0, Viewer.ScrollableHeight);
            if (Math.Abs(next - basis) < .001) return false;
            target = next;
            preciseInput = precise;
            if (!SystemParameters.ClientAreaAnimation) { Stop(); Viewer.ScrollToVerticalOffset(next); return true; }
            if (!moving)
            {
                moving = true;
                position = Viewer.VerticalOffset;
                previousFrame = Stopwatch.GetTimestamp();
                CompositionTarget.Rendering += Frame;
            }
            // Retarget a single frame loop; preserve precision-touchpad deltas.
            return true;
        }
        private void Frame(object? sender, EventArgs e)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Math.Clamp((now - previousFrame) / (double)Stopwatch.Frequency, 0, .1);
            previousFrame = now;
            target = Math.Clamp(target, 0, Viewer.ScrollableHeight);
            // Precision touchpads already provide a smooth stream and inertia.
            // Apply their accumulated distance on the next frame without adding
            // another easing tail that can lag behind the ongoing gesture.
            position = preciseInput ? target : position + (target - position) * (1 - Math.Exp(-elapsed / .035));
            var complete = Math.Abs(target - position) < .1;
            if (complete) position = target;
            Viewer.ScrollToVerticalOffset(position);
            if (complete)
            {
                CompositionTarget.Rendering -= Frame;
                moving = false;
                // ScrollToVerticalOffset is deferred. Keep the requested offset
                // until ScrollChanged confirms it, so the next delta cannot be
                // based on the old offset while layout is still catching up.
                pendingOffset = true;
            }
        }
    }
}
