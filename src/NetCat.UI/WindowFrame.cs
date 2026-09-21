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
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    public static void Apply(Window window)
    {
        window.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/logo.png"));
        var content = window.Content; window.Content = null; window.WindowStyle = WindowStyle.None;
        var main = window is MainWindow;
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = main ? 38 : 40, ResizeBorderThickness = new Thickness(main ? 3 : 6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        var grid = new Grid { Background = Brushes.Transparent }; if (!main) { grid.RowDefinitions.Add(new() { Height = new GridLength(40) }); grid.RowDefinitions.Add(new()); }
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
        window.SourceInitialized += (_,_) => { if (OperatingSystem.IsWindowsVersionAtLeast(10,0,22000)) { int rounded=2; DwmSetWindowAttribute(new WindowInteropHelper(window).Handle,33,ref rounded,sizeof(int)); } }; border.SetResourceReference(Border.BorderBrushProperty,"LineBrush"); window.Content = border;
    }
}
