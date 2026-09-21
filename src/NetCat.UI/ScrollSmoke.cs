using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NetCat.UI;
internal static class ScrollSmoke
{
    public static async Task CheckModulesAsync(ListBox modules, string destination)
    {
        if (SystemParameters.WheelScrollLines == 0) return;
        modules.UpdateLayout();
        var inner = SmoothScroll.FindViewer(modules) ?? throw new InvalidOperationException("No module viewer.");
        DependencyObject? parent = VisualTreeHelper.GetParent(modules);
        while (parent != null && parent is not ScrollViewer) parent = VisualTreeHelper.GetParent(parent);
        var page = parent as ScrollViewer ?? throw new InvalidOperationException("No module page viewer.");
        if (inner.ScrollableHeight > 0 || page.ScrollableHeight <= 0) throw new InvalidOperationException("Expected a non-scrolling list inside a scrolling page.");
        var selected = modules.SelectedItem;
        page.ScrollToTop(); modules.UpdateLayout();
        var row = (ListBoxItem)modules.ItemContainerGenerator.ContainerFromIndex(0);
        async Task Wheel(int delta)
        {
            // The desktop may be in use (e.g. Ctrl+V in Codex). Production intentionally
            // leaves modified wheel gestures alone; do not mistake them for scrolling bugs.
            var until=DateTime.UtcNow.AddSeconds(5);
            while(Keyboard.Modifiers!=ModifierKeys.None && DateTime.UtcNow<until) await Task.Delay(50);
            var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            row.RaiseEvent(args);
            if (!args.Handled) throw new InvalidOperationException("Module row swallowed the wheel event. Modifiers: "+Keyboard.Modifiers);
            await Task.Delay(240); modules.UpdateLayout();
        }
        await Wheel(-120); var down = page.VerticalOffset;
        if (down <= 0) throw new InvalidOperationException("Module page did not scroll down.");
        await Wheel(120);
        if (page.VerticalOffset > .5 || !Equals(selected, modules.SelectedItem)) throw new InvalidOperationException("Module wheel changed selection or did not return to top.");
        await File.WriteAllTextAsync(Path.Combine(destination,"modules-wheel-check.txt"), $"Wheel over module row: down {down:F2}px, up {page.VerticalOffset:F2}px; selection unchanged.");
    }
}
