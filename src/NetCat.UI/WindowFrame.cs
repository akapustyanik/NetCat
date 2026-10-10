using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Shell;
using System.Windows.Input;
using System.Windows.Media;
namespace NetCat.UI;
public static class WindowFrame
{
    private sealed record FrameSize(double MinWidth, double MinHeight, double Width, double Height, bool Main);
    private static readonly DependencyProperty FrameSizeProperty = DependencyProperty.RegisterAttached("FrameSize", typeof(FrameSize), typeof(WindowFrame));
    public static void UpdateScale(double scale)
    {
        if (Application.Current == null) return;
        foreach (Window window in Application.Current.Windows)
            if (window.GetValue(FrameSizeProperty) is FrameSize size) ApplyScale(window, size, scale, false);
    }
    private static void ApplyScale(Window window, FrameSize size, double scale, bool initial)
    {
        var work = GetWorkArea(window);
        var width = Math.Max(280, work.Width - 24);
        var height = Math.Max(280, work.Height - 24);
        window.MinWidth = Math.Min(size.MinWidth * scale, width);
        window.MinHeight = Math.Min(size.MinHeight * scale, height);
        var chrome = WindowChrome.GetWindowChrome(window);
        if (chrome != null) chrome.CaptionHeight = (size.Main ? 38 : 40) * scale;
        if (window.WindowState != WindowState.Normal) return;
        if (double.IsFinite(size.Width)) window.Width = Math.Clamp(initial ? size.Width * scale : window.Width, window.MinWidth, width);
        if (double.IsFinite(size.Height)) window.Height = Math.Clamp(initial ? size.Height * scale : window.Height, window.MinHeight, height);
        if (window.IsVisible && !App.IsSmoke)
        {
            var currentWidth = double.IsFinite(window.Width) ? window.Width : window.ActualWidth;
            var currentHeight = double.IsFinite(window.Height) ? window.Height : window.ActualHeight;
            if (double.IsFinite(window.Left) && currentWidth > 0)
                window.Left = Math.Clamp(window.Left, work.Left, Math.Max(work.Left, work.Right - currentWidth));
            if (double.IsFinite(window.Top) && currentHeight > 0)
                window.Top = Math.Clamp(window.Top, work.Top, Math.Max(work.Top, work.Bottom - currentHeight));
        }
    }
    internal static Rect GetWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return SystemParameters.WorkArea;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)) return SystemParameters.WorkArea;
        var transform = HwndSource.FromHwnd(handle)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return new Rect(transform.Transform(new Point(info.Work.Left, info.Work.Top)),
            transform.Transform(new Point(info.Work.Right, info.Work.Bottom)));
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    public static void Apply(Window window)
    {
        try
        {
            window.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/NetCat;component/Assets/logo.png"));
        }
        catch
        {
            try { window.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")); }
            catch { }
        }
        var content = window.Content; window.Content = null; window.WindowStyle = WindowStyle.None;
        var main = window is MainWindow;
        var size = new FrameSize(window.MinWidth, window.MinHeight, window.Width, window.Height, main);
        window.SetValue(FrameSizeProperty, size);
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = main ? 38 : 40, ResizeBorderThickness = new Thickness(main ? 3 : 6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        var grid = new Grid { Background = Brushes.Transparent, UseLayoutRounding = true }; if (!main) { grid.RowDefinitions.Add(new() { Height = new GridLength(40) }); grid.RowDefinitions.Add(new()); }
        grid.SetResourceReference(FrameworkElement.LayoutTransformProperty, "InterfaceScaleTransform");
        var presenter = new ContentPresenter { Content = content }; if (!main) Grid.SetRow(presenter,1); grid.Children.Add(presenter);
        var bar = new DockPanel { LastChildFill = !main, Height = 38, VerticalAlignment = VerticalAlignment.Top }; if (!main) bar.SetResourceReference(Panel.BackgroundProperty, "PanelBrush");
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(actions, Dock.Right);
        void Button(string text, string tooltip, Action action)
        {
            var b = new Button { Content = text, ToolTip = tooltip, Width = 44, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(0), BorderThickness = new Thickness(0) };
            b.SetResourceReference(Control.ForegroundProperty, "MutedBrush"); WindowChrome.SetIsHitTestVisibleInChrome(b, true); b.Click += (_,_) => action(); actions.Children.Add(b);
        }
        if (window.ResizeMode != ResizeMode.NoResize) { Button("−", "Свернуть", () => window.WindowState = WindowState.Minimized); Button("□", "Развернуть", () => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized); }
        Button("×", "Закрыть", window.Close); bar.Children.Add(actions);
        var title = new TextBlock { Margin = new(18,0,0,0), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, FontSize = 12 };
        if (main)
        {
            title.Text = "NetCat";
            // No overlay spanning the tabs: only the title and window buttons overlap the header.
            bar.Children.Remove(actions); actions.HorizontalAlignment = HorizontalAlignment.Right; actions.VerticalAlignment = VerticalAlignment.Top; actions.Height = 38; grid.Children.Add(actions);
            title.HorizontalAlignment = HorizontalAlignment.Left; title.VerticalAlignment = VerticalAlignment.Top; title.Margin = new(18,11,0,0); grid.Children.Add(title);

        }
        else { title.SetBinding(TextBlock.TextProperty, new Binding("Title") { Source = window }); bar.Children.Add(title); grid.Children.Add(bar); }
        var border = new Border { BorderThickness = new(OperatingSystem.IsWindowsVersionAtLeast(10,0,22000) ? 0 : 1), Child = grid };
        window.SourceInitialized += (_,_) => {
            if (OperatingSystem.IsWindowsVersionAtLeast(10,0,22000)) { int rounded=2; DwmSetWindowAttribute(new WindowInteropHelper(window).Handle,33,ref rounded,sizeof(int)); }
            ApplyScale(window, size, (Application.Current?.TryFindResource("InterfaceScaleTransform") as ScaleTransform)?.ScaleX ?? 1, false);
        }; border.SetResourceReference(Border.BorderBrushProperty,"LineBrush"); window.Content = border;
        ApplyScale(window, size, (Application.Current?.TryFindResource("InterfaceScaleTransform") as ScaleTransform)?.ScaleX ?? 1, true);
    }
}
