using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace NetCat.UI;
internal static class HeaderSmoke
{
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    public static void VerifyDragCaption(Window window)
    {
        foreach (var point in new[] { new Point(40,20), new Point(85,20) })
        {
            var screen = window.PointToScreen(point);
            var coords = unchecked((nint)(((int)screen.X & 0xffff) | ((int)screen.Y << 16)));
            var hit = SendMessage(new WindowInteropHelper(window).Handle,0x84,0,coords);
            if (hit != 2) throw new InvalidOperationException($"Header drag area returned {hit}, expected HTCAPTION.");
        }
    }
    public static void ClickTab(Window window, TabControl pages, int index)
    {
        window.UpdateLayout();
        var tab = (TabItem)pages.Items[index];
        var point = tab.TranslatePoint(new Point(tab.ActualWidth/2,tab.ActualHeight/2),window);
        var screen = window.PointToScreen(point);
        var coords = unchecked((nint)(((int)screen.X & 0xffff) | ((int)screen.Y << 16)));
        // WM_NCHITTEST must report client content, not HTCAPTION (drag window).
        var hit = SendMessage(new WindowInteropHelper(window).Handle,0x84,0,coords);
        if (hit != 1) throw new InvalidOperationException($"Вкладка {index}: native hit-test {hit}, ожидался HTCLIENT.");
        var element = window.InputHitTest(point) as DependencyObject;
        var parent = element;
        while (parent != null && parent != tab) parent = VisualTreeHelper.GetParent(parent);
        if (parent != tab || element is not UIElement target) throw new InvalidOperationException($"Вкладка {index} перекрыта другим элементом.");
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left) { RoutedEvent=Mouse.MouseDownEvent });
        if (pages.SelectedIndex != index) throw new InvalidOperationException($"Вкладка {index} не переключается по нажатию.");
    }
}
