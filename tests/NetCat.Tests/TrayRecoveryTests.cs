using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using NetCat.Core;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

[Collection("Tray interaction")]
public sealed class TrayRecoveryTests
{
    [Fact]
    public void ShellNotifyIconFailureKeepsListenerAndUserPreference() => OnUi(() =>
    {
        using var f = new Fixture();
        using var tray = f.CreateTray();
        Assert.True(tray.TrayInitialized);
        Assert.False(tray.TrayVisible);
        Assert.NotEqual(IntPtr.Zero, Source(tray).Handle);
        Assert.Equal(IntPtr.Zero, GetParent(Source(tray).Handle));
        f.Manager.ShowForTrayRecovery(f.Vm);
        f.Vm.SaveAsync().GetAwaiter().GetResult();
        Assert.True(f.Store.Load().MinimizeToTray);
        Assert.True(f.Manager.CurrentWindow!.IsVisible);
        Assert.True(f.Manager.CurrentWindow.ShowInTaskbar);
        Assert.Equal(WindowPresentationState.HiddenToTray, f.Store.LoadPresentationState(true));
    });

    [Fact]
    public void LateExplorerRegistrationRunsOnDispatcherAndStopsOnSuccess() => OnUi(() =>
    {
        using var f = new Fixture();
        using var tray = f.CreateTray([TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)]);
        f.ShellReady = true;
        Pump(TimeSpan.FromMilliseconds(60));
        Assert.True(tray.TrayVisible);
        Assert.Equal(2, f.Adds);
        Assert.Equal(1, f.Icons);
        Assert.Equal([true], f.Availability);
        Assert.All(f.Threads, t => Assert.Equal(Environment.CurrentManagedThreadId, t));
        Pump(TimeSpan.FromMilliseconds(40));
        Assert.Equal(2, f.Adds);
    });

    [Fact]
    public void RegistrationRetriesAreBoundedAndDoNotBlockStartup() => OnUi(() =>
    {
        using var f = new Fixture();
        using var tray = f.CreateTray([TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)]);
        Assert.Equal(1, f.Adds); // Constructor returns before either scheduled retry.
        Pump(TimeSpan.FromMilliseconds(80));
        Assert.False(tray.TrayVisible);
        Assert.Equal(3, f.Adds);
        Pump(TimeSpan.FromMilliseconds(80));
        Assert.Equal(3, f.Adds);
        Assert.Contains(f.Log, line => line.Contains("retries=exhausted"));
    });

    [Fact]
    public void TaskbarCreatedRecoversAfterRetriesExhaustedWithoutDuplicateIcon() => OnUi(() =>
    {
        using var f = new Fixture();
        using var tray = f.CreateTray([]);
        f.ShellReady = true;
        TaskbarCreated(tray);
        Assert.True(tray.TrayVisible);
        Assert.Equal(1, f.Icons);
        var adds = f.Adds;
        TaskbarCreated(tray);
        TaskbarCreated(tray);
        Assert.Equal(adds, f.Adds);
        Assert.Equal(1, f.Icons);
    });

    [Fact]
    public void ExplorerRestartFailureShowsRecoveryWindowAndKeepsHiddenPreference() => OnUi(() =>
    {
        using var f = new Fixture { ShellReady = true };
        using var tray = f.CreateTray([]);
        Assert.True(f.Session.StartHidden);
        Assert.Null(f.Manager.CurrentWindow);
        f.ShellReady = false;
        f.Icons = 0;
        TaskbarCreated(tray);
        Assert.False(tray.TrayVisible);
        Assert.True(f.Manager.CurrentWindow!.IsVisible);
        Assert.True(f.Session.RecoveryWindow);
        Assert.True(f.Vm.State.MinimizeToTray);
        Assert.Equal(WindowPresentationState.HiddenToTray, f.Store.LoadPresentationState(true));
        f.ShellReady = true;
        TaskbarCreated(tray);
        Assert.True(tray.TrayVisible);
        Assert.Equal(1, f.Icons);
        Assert.True(f.Manager.CurrentWindow.IsVisible);
        f.Session.EndSession(WindowPresentationState.VisibleNormal);
        Assert.Equal(WindowPresentationState.HiddenToTray, f.Store.LoadPresentationState(true));
    });

    [Fact]
    public void DisposeCancelsRetryAndDeletesIconOnlyOnce() => OnUi(() =>
    {
        using var f = new Fixture();
        var tray = f.CreateTray([TimeSpan.FromMilliseconds(10)]);
        tray.Dispose();
        tray.Dispose();
        f.ShellReady = true;
        Pump(TimeSpan.FromMilliseconds(50));
        Assert.Equal(1, f.Adds);
        Assert.Equal(1, f.Deletes);
        Assert.False(tray.TrayVisible);
    });

    [Fact]
    public void CloseWithoutTrayKeepsWindowReachableAndPreferenceEnabled() => OnUi(() =>
    {
        using var f = new Fixture();
        f.Manager.ShowForTrayRecovery(f.Vm);
        var win = f.Manager.CurrentWindow!;
        win.Close();
        Assert.Same(win, f.Manager.CurrentWindow);
        Assert.True(win.IsVisible);
        Assert.True(win.ShowInTaskbar);
        Assert.True(f.Vm.State.MinimizeToTray);
        Assert.Equal(WindowPresentationState.HiddenToTray, f.Store.LoadPresentationState(true));
        f.Session.TrayAvailable = true;
        win.Close();
        Assert.False(win.IsVisible);
        Assert.Same(win, f.Manager.CurrentWindow);
    });

    [Fact]
    public void RecoveryWindowDoesNotOverwriteHiddenPreferenceAcrossTwoReboots() => OnUi(() =>
    {
        using var f = new Fixture();
        for (var reboot = 0; reboot < 2; reboot++)
        {
            var session = new WindowPresentationSession(f.Store, true, f.Store.Load().MinimizeToTray);
            session.BeginTrayRecovery();
            session.SaveActualState(WindowPresentationState.VisibleNormal);
            session.SaveActualState(WindowPresentationState.VisibleMaximized);
            session.EndSession(WindowPresentationState.VisibleNormal);
            // WPF hides/closes windows after SessionEnding; that is not user intent.
            session.SaveActualState(WindowPresentationState.VisibleNormal);
            f.Vm.SaveAsync().GetAwaiter().GetResult();
            Assert.True(f.Store.Load().MinimizeToTray);
            Assert.Equal(WindowPresentationState.HiddenToTray, f.Store.LoadPresentationState(true));
        }
        var recovered = new WindowPresentationSession(f.Store, true, true) { TrayAvailable = true };
        Assert.True(recovered.StartHidden);
    });

    [Fact]
    public void ExplicitActivationOfRecoveryWindowIsPersistedWithoutReconcile() => OnUi(() =>
    {
        using var f = new Fixture();
        f.Manager.ShowForTrayRecovery(f.Vm);
        var win = f.Manager.CurrentWindow;
        var generation = f.Vm.RuntimeCoordinator.CurrentGeneration;
        var revision = f.Vm.Router.SessionRevision;
        f.Manager.ShowOrActivate(f.Vm); // Same path as second-instance SHOW.
        f.Manager.ShowOrActivate(f.Vm);
        Assert.Same(win, f.Manager.CurrentWindow);
        Assert.Single(Application.Current.Windows.OfType<MainWindow>(), w => ReferenceEquals(w.VM, f.Vm));
        Assert.False(f.Session.RecoveryWindow);
        Assert.Equal(WindowPresentationState.VisibleNormal, f.Store.LoadPresentationState(true));
        Assert.Equal(generation, f.Vm.RuntimeCoordinator.CurrentGeneration);
        Assert.Equal(revision, f.Vm.Router.SessionRevision);
        Assert.False(f.Vm.Router.IsRunning);
    });

    [Fact]
    public void SecondInstanceShowStillWorksWithoutTray() => OnUi(() =>
    {
        using var f = new Fixture();
        using var owner = new NetworkOwner("Local\\NetCat.TrayRecoveryTest." + Guid.NewGuid().ToString("N"));
        var pipe = "NetCat.TrayRecoveryTest." + Guid.NewGuid().ToString("N");
        var dispatcher = Dispatcher.CurrentDispatcher;
        owner.Listen(() => dispatcher.InvokeAsync(() => f.Manager.ShowOrActivate(f.Vm)).Task, pipe);
        f.Manager.ShowForTrayRecovery(f.Vm);
        var existing = f.Manager.CurrentWindow;
        var show = Task.Run(() => NetworkOwner.ShowExistingAsync(pipe));
        while (!show.IsCompleted) Pump(TimeSpan.FromMilliseconds(20));
        Assert.True(show.GetAwaiter().GetResult());
        Assert.Same(existing, f.Manager.CurrentWindow);
        Assert.True(existing!.IsVisible);
        Assert.True(existing.ShowInTaskbar);
        Assert.False(f.Session.TrayAvailable);
        Assert.True(f.Store.Load().MinimizeToTray);
    });

    [Fact]
    public void SessionEndingIgnoresSubsequentWpfWindowHide() => OnUi(() =>
    {
        using var f = new Fixture();
        f.Manager.ShowOrActivate(f.Vm);
        var win = f.Manager.CurrentWindow!;
        f.Session.EndSession(WindowPresentationState.VisibleNormal);
        win.Hide();
        Assert.Equal(WindowPresentationState.VisibleNormal, f.Store.LoadPresentationState(true));
        Assert.True(f.Vm.State.MinimizeToTray);
    });

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void AutostartAndClosePreferenceHaveSeparatePresentationPolicy(bool autostart, bool minimize, bool hidden)
    {
        var root = Path.Combine(Path.GetTempPath(), "NetCat-Presentation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(root);
            store.SavePresentationState(WindowPresentationState.HiddenToTray);
            var session = new WindowPresentationSession(store, autostart, minimize) { TrayAvailable = true };
            Assert.Equal(hidden, session.StartHidden);
            session.TrayAvailable = false;
            Assert.False(session.StartHidden);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AppDoesNotModifyMinimizeToTrayDuringTrayFailure()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "NetCat.sln"))) root = root.Parent;
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "NetCat.UI", "App.xaml.cs"));
        Assert.DoesNotContain("vm.State.MinimizeToTray = false", source);
        Assert.Contains("availabilityChanged:", source);
        Assert.Contains("Presentation?.EndSession(finalState)", source);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "NetCat-TrayRecovery-" + Guid.NewGuid().ToString("N"));
        public SettingsStore Store { get; }
        public MainViewModel Vm { get; }
        public WindowPresentationSession Session { get; }
        public WindowManager Manager { get; }
        public bool ShellReady;
        public int Icons, Adds, Deletes;
        public List<int> Threads { get; } = [];
        public List<bool> Availability { get; } = [];
        public List<string> Log { get; } = [];
        public Fixture()
        {
            Store = new SettingsStore(root);
            Store.SaveAsync(new AppSettings { MinimizeToTray = true }).GetAwaiter().GetResult();
            Store.SavePresentationState(WindowPresentationState.HiddenToTray);
            Session = new WindowPresentationSession(Store, true, true);
            Vm = new MainViewModel(Store, Store.Load());
            Manager = new WindowManager { Presentation = Session };
        }
        public TrayIconService CreateTray(TimeSpan[]? delays = null) => new(
            () => Manager.ShowOrActivate(Vm), () => { }, () => [], Log.Add,
            available =>
            {
                Availability.Add(available);
                Session.TrayAvailable = available;
                if (!available) Manager.ShowForTrayRecovery(Vm);
            }, message =>
            {
                Threads.Add(Environment.CurrentManagedThreadId);
                if (message == 2) { Deletes++; Icons = 0; return true; }
                if (message == 1) return ShellReady && Icons == 1;
                Adds++;
                if (!ShellReady) return false;
                Icons++;
                return true;
            }, delays ?? []);
        public void Dispose()
        {
            if (Manager.CurrentWindow is { } win)
            {
                typeof(MainWindow).GetField("exiting", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(win, true);
                ((DispatcherTimer)typeof(MainWindow).GetField("timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(win)!).Stop();
                win.Close();
            }
            Vm.Dispose();
            Directory.Delete(root, true);
        }
    }
    private static HwndSource Source(TrayIconService tray) =>
        (HwndSource)typeof(TrayIconService).GetField("source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
    private static void TaskbarCreated(TrayIconService tray) =>
        SendMessage(Source(tray).Handle, (int)RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero);
    private static void Pump(TimeSpan interval)
    {
        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = interval };
        stop.Tick += (_, _) => { stop.Stop(); frame.Continue = false; };
        stop.Start();
        Dispatcher.PushFrame(frame);
    }
    private static void OnUi(Action action)
    {
        if (Application.Current == null)
        {
            var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/NetCat;component/Theme.xaml") });
                ready.SetResult(app.Dispatcher);
                Dispatcher.Run();
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Task.GetAwaiter().GetResult().Invoke(action);
        }
        else Application.Current.Dispatcher.Invoke(action);
    }
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
}
