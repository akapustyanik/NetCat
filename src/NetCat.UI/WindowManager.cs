using System.Windows;
using NetCat.Core;

namespace NetCat.UI;

public sealed class WindowManager
{
    private MainWindow? window;

    public MainWindow? CurrentWindow => window;
    public WindowPresentationSession? Presentation { get; set; }

    public MainWindow GetOrCreateMainWindow(MainViewModel vm)
    {
        if (window == null)
        {
            window = new MainWindow(vm, presentation: Presentation);
            if (Application.Current != null)
            {
                Application.Current.MainWindow = window;
            }
            window.Closed += (_, _) =>
            {
                if (Application.Current != null && window == Application.Current.MainWindow)
                {
                    Application.Current.MainWindow = null;
                }
                window = null;
            };
        }
        return window;
    }

    public void ShowMainWindow(MainViewModel vm)
        => ShowOrActivate(vm);

    public void ShowOrActivate(MainViewModel vm)
    {
        Presentation?.UserActivated();
        ShowWindow(vm);
    }

    public void ShowForTrayRecovery(MainViewModel vm)
    {
        if (window is { IsVisible: true, ShowInTaskbar: true } && window.WindowState != WindowState.Minimized) return;
        Presentation?.BeginTrayRecovery();
        ShowWindow(vm);
    }

    private void ShowWindow(MainViewModel vm)
    {
        var win = GetOrCreateMainWindow(vm);
        win.ShowInTaskbar = true;
        if (!win.IsVisible) win.Show();
        if (win.WindowState == WindowState.Minimized)
        {
            win.WindowState = WindowState.Normal;
        }
        win.Activate();
        win.Focus();
        vm.WriteLog($"WINDOW state=visible reason={(Presentation?.RecoveryWindow == true ? "tray-recovery" : "activation")}");
        Presentation?.SaveActualState(win.WindowState == WindowState.Maximized
            ? WindowPresentationState.VisibleMaximized : WindowPresentationState.VisibleNormal);
    }
}
