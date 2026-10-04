using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using NetCat.Core;
using NetCat.Network;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

[CollectionDefinition("Tray interaction", DisableParallelization = true)]
public sealed class TrayInteractionCollection { }

[Collection("Tray interaction")]
public sealed class TrayInteractionTests
{
    [Fact]
    public void TraySingleLeftClickShowsWindow() => OnUi(() =>
    {
        using var f = new Fixture();
        var window = f.Manager.GetOrCreateMainWindow(f.Vm);
        Assert.False(window.IsVisible);
        f.LeftClick();
        Assert.Same(window, f.Manager.CurrentWindow);
        Assert.True(window.IsVisible);
        Assert.True(window.ShowInTaskbar);

        window.WindowState = WindowState.Minimized;
        f.LeftClick();
        Assert.Equal(WindowState.Normal, window.WindowState);
        window.Hide();
        f.LeftClick();
        Assert.True(window.IsVisible);

        window.WindowState = WindowState.Maximized;
        f.LeftClick();
        Assert.Equal(WindowState.Maximized, window.WindowState);
        Assert.Same(window, f.Manager.CurrentWindow);
        Assert.True(window.IsActive);
        Assert.Equal(4, f.Activations);
    });

    [Fact]
    public void TraySingleLeftClickLazilyCreatesWindow() => OnUi(() =>
    {
        using var f = new Fixture();
        Assert.Null(f.Manager.CurrentWindow);
        f.LeftClick();
        var window = Assert.IsType<MainWindow>(f.Manager.CurrentWindow);
        Assert.True(window.IsVisible);
        Assert.Same(f.Vm, window.VM);
        f.LeftClick();
        Assert.Same(window, f.Manager.CurrentWindow);
        Assert.Single(Application.Current.Windows.OfType<MainWindow>(), w => ReferenceEquals(w.VM, f.Vm));
    });

    [Fact]
    public void TraySingleLeftClickDoesNotRequestReconcile() => OnUi(() =>
    {
        using var f = new Fixture();
        var coordinator = f.Vm.RuntimeCoordinator;
        var generation = coordinator.CurrentGeneration;
        var desired = JsonSettings.Clone(f.Vm.DesiredState);
        var loop = typeof(RuntimeCoordinator).GetField("loopTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Null(loop.GetValue(coordinator));
        f.LeftClick();
        f.Manager.CurrentWindow!.Hide();
        f.LeftClick();
        Assert.Null(loop.GetValue(coordinator));
        Assert.Equal(generation, coordinator.CurrentGeneration);
        Assert.Equal(desired.MainVpnEnabled, f.Vm.DesiredState.MainVpnEnabled);
        Assert.Equal(desired.ZapretEnabled, f.Vm.DesiredState.ZapretEnabled);
        Assert.Equal(desired.OpenVpnEnabled, f.Vm.DesiredState.OpenVpnEnabled);
        Assert.False(f.Vm.Router.IsRunning);
        Assert.False(f.Vm.Zapret.Running);
        Assert.False(f.Vm.Router.OpenVpn.Running);
    });

    [Fact]
    public void TrayRightClickStillOpensContextMenu() => OnUi(() =>
    {
        using var f = new Fixture();
        f.Send(0x204); // WM_RBUTTONDOWN
        f.Send(0x205); // WM_RBUTTONUP
        var menu = Assert.IsType<ContextMenu>(typeof(TrayIconService)
            .GetField("menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Tray));
        Assert.True(menu.IsOpen);
        Assert.Null(f.Manager.CurrentWindow);
        Assert.Equal(0, f.Activations);
        var open = Assert.IsType<MenuItem>(menu.Items[0]);
        Assert.Equal("Открыть NetCat", open.Header);
        open.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(f.Manager.CurrentWindow!.IsVisible);
        Assert.Equal(1, f.Activations);
    });

    [Fact]
    public void TrayDoubleClickDoesNotCauseDuplicateWindowActivation() => OnUi(() =>
    {
        using var f = new Fixture();
        // Shell's complete double-click sequence: DOWN, UP, DBLCLK, UP.
        f.Send(0x201);
        f.Send(0x202);
        var window = f.Manager.CurrentWindow;
        Assert.NotNull(window);
        Assert.Equal(1, f.Activations);
        f.Send(0x203);
        f.Send(0x202);
        Assert.Equal(1, f.Activations);
        Assert.Same(window, f.Manager.CurrentWindow);
        f.LeftClick(); // Suppression must not swallow the next independent click.
        Assert.Equal(2, f.Activations);
        Assert.Same(window, f.Manager.CurrentWindow);
    });

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "NetCat-Tray-" + Guid.NewGuid().ToString("N"));
        public MainViewModel Vm { get; }
        public WindowManager Manager { get; } = new();
        public TrayIconService Tray { get; }
        public int Activations { get; private set; }
        public Fixture()
        {
            Vm = new MainViewModel(new SettingsStore(root), new AppSettings());
            Tray = new TrayIconService(() => { Activations++; Manager.ShowOrActivate(Vm); }, () => { }, () => []);
        }
        public void Send(int mouseMessage)
        {
            var source = (HwndSource)typeof(TrayIconService).GetField("source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Tray)!;
            SendMessage(source.Handle, 0x8001, (IntPtr)1, (IntPtr)mouseMessage);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        public void LeftClick() { Send(0x201); Send(0x202); }
        public void Dispose()
        {
            Tray.Dispose();
            if (Manager.CurrentWindow is { } window)
            {
                // Close only this test window, without the application's exit/runtime path.
                typeof(MainWindow).GetField("exiting", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                ((DispatcherTimer)typeof(MainWindow).GetField("timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Stop();
                window.Close();
            }
            Vm.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    { Source = new Uri("pack://application:,,,/NetCat;component/Theme.xaml") });
                    ready.SetResult(app.Dispatcher);
                    Dispatcher.Run();
                }
                catch (Exception ex) { ready.TrySetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            dispatcher = ready.Task.GetAwaiter().GetResult();
        }
        dispatcher.Invoke(action);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
