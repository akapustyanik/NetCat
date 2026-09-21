using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

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
        if (args.Handled || Keyboard.Modifiers != ModifierKeys.None || SystemParameters.WheelScrollLines == 0) return;
        var element = (FrameworkElement)sender;
        var viewer = FindViewer(element);
        // A ListBox's own ScrollViewer consumes bubbling wheel events even when
        // it has no overflow. Handle the nearest scrollable ancestor in preview.
        while (viewer != null)
        {
            var pixels = SystemParameters.WheelScrollLines < 0 ? viewer.ViewportHeight * .85 : SystemParameters.WheelScrollLines * 16;
            if (Move(element, viewer, -args.Delta / 120d * pixels)) { args.Handled = true; return; }
            var parent = VisualTreeHelper.GetParent(viewer);
            while (parent != null && parent is not ScrollViewer) parent = VisualTreeHelper.GetParent(parent);
            viewer = parent as ScrollViewer;
        }
    }
    internal static bool ScrollBy(FrameworkElement element, double delta)
    {
        var viewer = FindViewer(element);
        if (viewer == null) return false;
        return Move(element, viewer, delta);
    }
    private static bool Move(FrameworkElement element, ScrollViewer viewer, double delta)
    {
        if (viewer.ScrollableHeight <= 0) return false;
        var state = element.GetValue(StateProperty) as ScrollState;
        if (state == null || state.Viewer != viewer) { state?.Stop(); state = new ScrollState(viewer); element.SetValue(StateProperty, state); }
        return state.Move(delta);
    }
    private sealed class ScrollState(ScrollViewer viewer) : Animatable
    {
        public ScrollViewer Viewer { get; } = viewer;
        private bool moving;
        private double target;
        private static readonly DependencyProperty OffsetProperty = DependencyProperty.Register("Offset", typeof(double), typeof(ScrollState), new PropertyMetadata(0d, (obj, e) => ((ScrollState)obj).Viewer.ScrollToVerticalOffset((double)e.NewValue)));
        protected override Freezable CreateInstanceCore() => new ScrollState(Viewer);
        public void Stop() { var position = Viewer.VerticalOffset; BeginAnimation(OffsetProperty, null); SetValue(OffsetProperty, position); moving = false; }
        public bool Move(double delta)
        {
            var next = Math.Clamp((moving ? target : Viewer.VerticalOffset) + delta, 0, Viewer.ScrollableHeight);
            if (Math.Abs(next - Viewer.VerticalOffset) < .1) return false;
            var from = Viewer.VerticalOffset; Stop(); target = next;
            if (!SystemParameters.ClientAreaAnimation) { Viewer.ScrollToVerticalOffset(next); return true; }
            moving = true;
            var animation = new DoubleAnimation(from, next, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            SetValue(OffsetProperty, next);
            animation.Completed += (_, _) => { moving = false; Viewer.ScrollToVerticalOffset(target); };
            BeginAnimation(OffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
            return true;
        }
    }
}
