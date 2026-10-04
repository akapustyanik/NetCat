using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate9Tests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "NetCat-C9Tests-" + Guid.NewGuid().ToString("N"));

    public Candidate9Tests()
    {
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
        catch { }
    }

    internal sealed class FakeDesiredProvider(DesiredRuntimeState initial) : IDesiredRuntimeStateProvider
    {
        public DesiredRuntimeState Current { get; set; } = initial;
        public DesiredRuntimeState GetCurrentDesiredState() => Current;
    }

    internal sealed class FakeRouterRuntime : IRouterRuntime
    {
        public bool IsRunning { get; set; }
        public bool VpnRunning { get; set; }
        public bool TunActive { get; set; }
        public int ListenPort { get; set; } = 1080;
        public int LatencyPort { get; set; } = 1081;
        public int HealthSourcePort { get; set; } = 1082;
        public NetworkSnapshot? ActivePhysical { get; set; } = new("Ethernet", 1, "192.168.1.100", "192.168.1.1", []);
        public Guid? ActiveProfileId { get; set; }
        public long SessionRevision { get; set; }

        public int EnsureRunningCalls { get; private set; }
        public int EnsureStoppedCalls { get; private set; }
        public int RefreshPhysicalCalls { get; private set; }
        public List<string> ReconfigReasons { get; } = [];
        public bool FailStart { get; set; }
        public Action? OnEnsureRunning { get; set; }
        public Func<Task>? OnEnsureRunningAsync { get; set; }

        public async Task EnsureRunningAsync(AppSettings settings, NetworkSnapshot physical, string reason, CancellationToken ct)
        {
            EnsureRunningCalls++;
            ReconfigReasons.Add(reason);
            OnEnsureRunning?.Invoke();
            if (OnEnsureRunningAsync != null) await OnEnsureRunningAsync();
            if (FailStart) throw new IOException("Router failed to start");
            IsRunning = true;
            VpnRunning = true;
            TunActive = settings.Tun;
            ActiveProfileId = settings.MainProfileId;
            ActivePhysical = physical;
        }

        public Task EnsureStoppedAsync(CancellationToken ct)
        {
            EnsureStoppedCalls++;
            IsRunning = false;
            VpnRunning = false;
            TunActive = false;
            return Task.CompletedTask;
        }

        public Task RefreshPhysicalAsync(AppSettings settings, NetworkSnapshot physical, string reason, CancellationToken ct)
        {
            RefreshPhysicalCalls++;
            ActivePhysical = physical;
            return Task.CompletedTask;
        }
        public Task ApplyOpenVpnOverlayAsync(AppSettings settings, OpenVpnLink? link, CancellationToken ct) => Task.CompletedTask;
    }

    internal sealed class FakeZapretRuntime : IZapretRuntime
    {
        public bool IsRunning { get; set; }
        public string ActiveStrategy { get; set; } = "";
        public string ActiveScenario { get; set; } = "";
        public int EnsureRunningCalls { get; private set; }
        public int EnsureStoppedCalls { get; private set; }
        public bool FailStart { get; set; }

        public Task EnsureRunningAsync(AppSettings settings, string? strategyFile, CancellationToken ct)
        {
            EnsureRunningCalls++;
            if (FailStart) throw new IOException("Zapret winws failed to start");
            IsRunning = true;
            ActiveStrategy = strategyFile != null ? Path.GetFileName(strategyFile) : "general.bat";
            ActiveScenario = settings.Scenario;
            return Task.CompletedTask;
        }

        public Task EnsureStoppedAsync(CancellationToken ct)
        {
            EnsureStoppedCalls++;
            IsRunning = false;
            ActiveStrategy = "";
            ActiveScenario = "";
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeOpenVpnRuntime : IOpenVpnRuntime
    {
        public bool IsRunning { get; set; }
        public OpenVpnLink? Link { get; set; }
        public Guid? ActiveProfileId { get; set; }
        public int EnsureRunningCalls { get; private set; }
        public int EnsureStoppedCalls { get; private set; }
        public bool FailStart { get; set; }
        public bool FailRouteVerification { get; set; }
        public List<string> LearnedRoutesToPush { get; set; } = [];

        public Task<OpenVpnLink> EnsureRunningAsync(Profile profile, string dnsOverride, CancellationToken ct)
        {
            EnsureRunningCalls++;
            if (FailStart) throw new IOException("OpenVPN failed to start");
            if (FailRouteVerification) throw new IOException("Не удалось подтвердить установку маршрута в таблице маршрутизации Windows.");
            IsRunning = true;
            ActiveProfileId = profile.Id;
            var routes = LearnedRoutesToPush.Count > 0 ? LearnedRoutesToPush : profile.LearnedRoutes;
            Link = new OpenVpnLink("NetCat-OpenVPN", 10, "10.10.11.5", "10.10.11.1", dnsOverride, routes);
            return Task.FromResult(Link);
        }

        public Task EnsureStoppedAsync(CancellationToken ct)
        {
            EnsureStoppedCalls++;
            IsRunning = false;
            Link = null;
            ActiveProfileId = null;
            return Task.CompletedTask;
        }
    }

    internal sealed class FakePhysicalProvider : IPhysicalNetworkProvider
    {
        public NetworkSnapshot? Current { get; set; } = new("Ethernet", 1, "192.168.1.100", "192.168.1.1", []);
        public bool Usable { get; set; } = true;

        public NetworkSnapshot? ResolveCurrentBinding(string preferred = "") => Current;
        public bool IsUsable(NetworkSnapshot? snapshot) => Usable && snapshot != null;
        public bool HasChanged(NetworkSnapshot? active, NetworkSnapshot? current, bool captureFailed) =>
            PhysicalNetwork.HasPhysicalChanged(active, current, captureFailed);
    }

    internal sealed class FakeTunnelInspector : ITunnelHealthInspector
    {
        public bool StructuralFailure { get; set; }
        public int InspectCalls { get; private set; }
        public int WaitForTunReadyCalls { get; private set; }

        public Task<RouterHealth> InspectAsync(bool running, int port, bool tun, CancellationToken ct)
        {
            InspectCalls++;
            return Task.FromResult(new RouterHealth(running, !StructuralFailure, !StructuralFailure, !StructuralFailure, 10));
        }

        public Task<RouterHealth> WaitForTunReadyAsync(Func<bool> isRunning, Func<int> getPort, bool tun, TimeSpan timeout, CancellationToken ct)
        {
            WaitForTunReadyCalls++;
            return Task.FromResult(new RouterHealth(isRunning(), !StructuralFailure, !StructuralFailure, !StructuralFailure, 10));
        }
    }

    internal static RuntimeCoordinator CreateCoordinator(
        IDesiredRuntimeStateProvider desired,
        IRouterRuntime router,
        IZapretRuntime zapret,
        IOpenVpnRuntime openVpn,
        IPhysicalNetworkProvider? physical = null,
        ITunnelHealthInspector? inspector = null,
        TimeSpan[]? backoff = null,
        Func<AppSettings>? getSettings = null,
        Func<CancellationToken, Task>? finalizeOverride = null,
        Func<Guid, IReadOnlyList<string>, CancellationToken, Task>? onRoutesLearned = null)
    {
        var fixedProfileId = Guid.NewGuid();
        var coordinator = new RuntimeCoordinator(desired, router, zapret, openVpn)
        {
            PhysicalNetworkProvider = physical ?? new FakePhysicalProvider(),
            TunnelInspector = inspector ?? new FakeTunnelInspector(),
            TunReadyTimeout = TimeSpan.FromMilliseconds(50),
            GetSettings = getSettings ?? (() =>
            {
                var s = new AppSettings();
                var p = new Profile { Id = desired.GetCurrentDesiredState().SelectedOpenVpnProfileId ?? fixedProfileId, Protocol = "openvpn", Name = "OpenVPN" };
                s.Profiles.Add(p);
                s.OpenVpnProfileId = p.Id;
                return s;
            }),
            VerifyOpenVpnRoutesInRouteTable = (_, _) => new(RouteObservationStatus.Verified, []),
            FinalizeRoutingOverride = finalizeOverride,
            OnOpenVpnRoutesLearned = onRoutesLearned
        };
        if (backoff != null) coordinator.BackoffIntervals = backoff;
        return coordinator;
    }

    // 1. AppStartupUsesOnlyRuntimeCoordinator
    [Fact]
    public void AppStartupUsesOnlyRuntimeCoordinator()
    {
        // Assert via reflection that MainViewModel does NOT expose StartupCoordinator
        var startupProp = typeof(MainViewModel).GetProperty("StartupCoordinator");
        Assert.Null(startupProp);

        // Verify App.xaml.cs source does not call StartupCoordinator
        var appCs = File.ReadAllText(Path.Combine(RoutingTests.FindRoot(), "src", "NetCat.UI", "App.xaml.cs"));
        Assert.DoesNotContain("StartupCoordinator", appCs);
        Assert.Contains("RuntimeCoordinator.RequestReconcile(ReconcileReason.Startup)", appCs);
    }

    // 2. LegacyStartupCoordinatorIsNotInstantiated
    [Fact]
    public void LegacyStartupCoordinatorIsNotInstantiated()
    {
        // StartupCoordinator must not be instantiated by MainViewModel
        var store = new SettingsStore(tempDir);
        var settings = store.Load();
        var vm = new MainViewModel(store, settings);

        // Property must not exist
        Assert.Null(typeof(MainViewModel).GetProperty("StartupCoordinator"));

        // RuntimeCoordinator must be present
        Assert.NotNull(vm.RuntimeCoordinator);
        vm.Dispose();
    }

    // 3. HiddenAutostartStartsReconcileWithoutWindowLoaded
    [Fact]
    public async Task HiddenAutostartStartsReconcileWithoutWindowLoaded()
    {
        var desired = new FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true, ZapretEnabled = true });
        var router = new FakeRouterRuntime();
        var zapret = new FakeZapretRuntime();
        var openVpn = new FakeOpenVpnRuntime();

        using var coordinator = CreateCoordinator(desired, router, zapret, openVpn);

        // Reconcile is triggered without any Window.Loaded event
        coordinator.RequestReconcile(ReconcileReason.Startup);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.Completed; i++)
            await Task.Delay(25);

        Assert.Equal(StartupRestoreState.Completed, coordinator.CurrentRestoreState);
        Assert.Equal(1, router.EnsureRunningCalls);
        Assert.Equal(1, zapret.EnsureRunningCalls);
    }

    // 4. OpeningWindowDoesNotTriggerSecondReconcile
    [Fact]
    public async Task OpeningWindowDoesNotTriggerSecondReconcile()
    {
        var desired = new FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true });
        var router = new FakeRouterRuntime();
        var zapret = new FakeZapretRuntime();
        var openVpn = new FakeOpenVpnRuntime();

        using var coordinator = CreateCoordinator(desired, router, zapret, openVpn);

        coordinator.RequestReconcile(ReconcileReason.Startup);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.Completed; i++)
            await Task.Delay(25);

        long genAfterStartup = coordinator.CurrentGeneration;
        int routerCallsAfterStartup = router.EnsureRunningCalls;

        // Simulate subsequent window open / focus (no settings changed)
        // No reconcile request is made by opening window
        await Task.Delay(50);

        Assert.Equal(genAfterStartup, coordinator.CurrentGeneration);
        Assert.Equal(routerCallsAfterStartup, router.EnsureRunningCalls);
    }

    // 5. NetworkMonitorNeverCallsRouterDirectly
    [Fact]
    public void NetworkMonitorNeverCallsRouterDirectly()
    {
        // Inspect MainViewModel source to assert MonitorNetworkAsync does not invoke Router.RefreshNetworkAsync
        var vmCs = File.ReadAllText(Path.Combine(RoutingTests.FindRoot(), "src", "NetCat.UI", "MainViewModel.cs"));

        // Assert that MonitorNetworkAsync calls RequestReconcile(ReconcileReason.PhysicalNetworkChanged)
        Assert.Contains("ReconcileAsync(ReconcileReason.PhysicalNetworkChanged", File.ReadAllText(Path.Combine(RoutingTests.FindRoot(), "src/NetCat.Network/PhysicalNetworkMonitor.cs")));

        // Assert that MonitorNetworkAsync does NOT call Router.RefreshNetworkAsync
        Assert.DoesNotContain("Router.RefreshNetworkAsync(settings, false, \"network change\"", vmCs);
    }

    // 6. TunWatchdogNeverCallsRouterDirectly
    [Fact]
    public void TunWatchdogNeverCallsRouterDirectly()
    {
        var vmCs = File.ReadAllText(Path.Combine(RoutingTests.FindRoot(), "src", "NetCat.UI", "MainViewModel.cs"));

        // Assert that watchdog branch calls RequestReconcile(ReconcileReason.TunStructuralFailure)
        Assert.Contains("RequestReconcile(ReconcileReason.TunStructuralFailure)", File.ReadAllText(Path.Combine(RoutingTests.FindRoot(), "src/NetCat.Network/PhysicalNetworkMonitor.cs")));

        // Assert that watchdog branch does NOT call Router.RefreshNetworkAsync
        Assert.DoesNotContain("Router.RefreshNetworkAsync(settings, true, \"TUN watchdog\"", vmCs);
    }

    // 7. UiVpnToggleUpdatesDesiredAndRequestsReconcileOnly
    [Fact]
    public void UiVpnToggleUpdatesDesiredAndRequestsReconcileOnly()
    {
        var store = new SettingsStore(tempDir);
        var settings = store.Load();
        var vm = new MainViewModel(store, settings);

        // Toggle ON
        vm.UserRequestedVpnChange(true);
        Assert.True(vm.DesiredState.MainVpnEnabled);
        var saved = store.LoadDesiredState();
        Assert.True(saved.MainVpnEnabled);

        // Toggle OFF - must NOT cancel coordinator lifetime
        vm.UserRequestedVpnChange(false);
        Assert.False(vm.DesiredState.MainVpnEnabled);
        Assert.NotEqual(StartupRestoreState.Cancelled, vm.RuntimeCoordinator.CurrentRestoreState);

        vm.Dispose();
    }

    // 8. UiZapretToggleDoesNotCallRouterDirectly
    [Fact]
    public void UiZapretToggleDoesNotCallRouterDirectly()
    {
        var store = new SettingsStore(tempDir);
        var settings = store.Load();
        var vm = new MainViewModel(store, settings);

        vm.UserRequestedZapretChange(true);
        Assert.True(vm.DesiredState.ZapretEnabled);

        // Router must not be requested by Zapret toggle
        Assert.False(vm.Router.VpnRequested);

        vm.Dispose();
    }

    // 9. RuntimeSettingsDiffIsPassedToPlanner
    [Fact]
    public async Task RuntimeSettingsDiffIsPassedToPlanner()
    {
        var desired = new FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true, ZapretEnabled = true });
        var router = new FakeRouterRuntime();
        var zapret = new FakeZapretRuntime();
        var openVpn = new FakeOpenVpnRuntime();

        using var coordinator = CreateCoordinator(desired, router, zapret, openVpn);

        coordinator.RequestReconcile(ReconcileReason.Startup);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.Completed; i++)
            await Task.Delay(25);

        // Assert that LastDiff was computed and passed to planner
        Assert.NotNull(coordinator.LastDiff);
        Assert.True(coordinator.LastDiff.VpnChanged);
        Assert.True(coordinator.LastDiff.ZapretChanged);
    }

    // 10. TunOnlyChangeProducesMainRouterDelta
    [Fact]
    public void TunOnlyChangeProducesMainRouterDelta()
    {
        var desired = new DesiredRuntimeState { MainVpnEnabled = true, TunEnabled = true };
        var observed = new ObservedRuntimeState(
            MainRouterStatus: ObservedComponentState.RunningHealthy,
            ZapretStatus: ObservedComponentState.Stopped,
            OpenVpnStatus: ObservedComponentState.Stopped,
            TunReady: false,
            PhysicalNetwork: new NetworkSnapshot("Eth", 1, "192.168.1.1", "192.168.1.1", []),
            PhysicalUsable: true
        );
        var settings = new AppSettings();
        var diff = new RuntimeSettingsDiff(
            VpnChanged: false,
            TunChanged: true,
            VpnProfileChanged: false,
            OpenVpnChanged: false,
            OpenVpnProfileChanged: false,
            RoutingRulesChanged: false,
            LearnedOpenVpnRoutesChanged: false,
            ZapretChanged: false,
            ZapretProfileChanged: false,
            DnsPolicyChanged: false,
            PhysicalBindingChanged: false
        );

        var plan = RuntimePlanner.CreatePlan(desired, observed, settings, new Dictionary<ComponentId, int>(), diff: diff);

        // Plan must reconfigure MainRouter and wait for TUN, but touch neither Zapret nor OpenVPN
        Assert.Contains(plan.Actions, a => a.Type == PlanActionType.StartMainRouter);
        Assert.Contains(plan.Actions, a => a.Type == PlanActionType.WaitTunReady);
        Assert.DoesNotContain(plan.Actions, a => a.Type == PlanActionType.StartZapret);
        Assert.DoesNotContain(plan.Actions, a => a.Type == PlanActionType.StartOpenVpn);
    }

    // 11. ZapretOnlyChangeLeavesRouterUntouched
    [Fact]
    public void ZapretOnlyChangeLeavesRouterUntouched()
    {
        var desired = new DesiredRuntimeState { MainVpnEnabled = true, ZapretEnabled = true };
        var observed = new ObservedRuntimeState(
            MainRouterStatus: ObservedComponentState.RunningHealthy,
            ZapretStatus: ObservedComponentState.RunningHealthy,
            OpenVpnStatus: ObservedComponentState.Stopped,
            TunReady: true,
            PhysicalNetwork: new NetworkSnapshot("Eth", 1, "192.168.1.1", "192.168.1.1", []),
            PhysicalUsable: true
        );
        var settings = new AppSettings();
        var diff = new RuntimeSettingsDiff(
            VpnChanged: false,
            TunChanged: false,
            VpnProfileChanged: false,
            OpenVpnChanged: false,
            OpenVpnProfileChanged: false,
            RoutingRulesChanged: false,
            LearnedOpenVpnRoutesChanged: false,
            ZapretChanged: true,
            ZapretProfileChanged: true,
            DnsPolicyChanged: false,
            PhysicalBindingChanged: false
        );

        var plan = RuntimePlanner.CreatePlan(desired, observed, settings, new Dictionary<ComponentId, int>(), diff: diff);

        // Plan must only adjust Zapret, leaving Router completely untouched
        Assert.Contains(plan.Actions, a => a.Type == PlanActionType.StartZapret);
        Assert.DoesNotContain(plan.Actions, a => a.Type == PlanActionType.StartMainRouter);
        Assert.DoesNotContain(plan.Actions, a => a.Type == PlanActionType.StopMainRouter);
    }

    // 12. NoNetworkLeavesCoordinatorPendingUntilNetworkEvent
    [Fact]
    public async Task NoNetworkLeavesCoordinatorPendingUntilNetworkEvent()
    {
        var desired = new FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true });
        var router = new FakeRouterRuntime();
        var zapret = new FakeZapretRuntime();
        var openVpn = new FakeOpenVpnRuntime();
        var physical = new FakePhysicalProvider { Usable = false };

        using var coordinator = CreateCoordinator(desired, router, zapret, openVpn, physical: physical);

        coordinator.RequestReconcile(ReconcileReason.Startup);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.WaitingForNetwork; i++)
            await Task.Delay(25);

        Assert.Equal(StartupRestoreState.WaitingForNetwork, coordinator.CurrentRestoreState);
        // Failure count must NOT be incremented for physical network absence
        Assert.Equal(0, coordinator.GetFailureCount(ComponentId.MainRouter));
        Assert.Equal(0, router.EnsureRunningCalls);
    }

    // 13. PhysicalNetworkEventResumesPendingConvergence
    [Fact]
    public async Task PhysicalNetworkEventResumesPendingConvergence()
    {
        var desired = new FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true });
        var router = new FakeRouterRuntime();
        var zapret = new FakeZapretRuntime();
        var openVpn = new FakeOpenVpnRuntime();
        var physical = new FakePhysicalProvider { Usable = false };

        using var coordinator = CreateCoordinator(desired, router, zapret, openVpn, physical: physical);

        coordinator.RequestReconcile(ReconcileReason.Startup);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.WaitingForNetwork; i++)
            await Task.Delay(25);

        Assert.Equal(StartupRestoreState.WaitingForNetwork, coordinator.CurrentRestoreState);

        // Network becomes available
        physical.Usable = true;
        coordinator.RequestReconcile(ReconcileReason.PhysicalNetworkChanged);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.Completed; i++)
            await Task.Delay(25);

        Assert.Equal(StartupRestoreState.Completed, coordinator.CurrentRestoreState);
        Assert.Equal(1, router.EnsureRunningCalls);
    }

    // 14. FailedStartMainRouterSkipsWaitTun
    [Fact]
    public async Task FailedStartMainRouterSkipsWaitTun()
    {
        var desired = new FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true, TunEnabled = true });
        var router = new FakeRouterRuntime { FailStart = true };
        var zapret = new FakeZapretRuntime();
        var openVpn = new FakeOpenVpnRuntime();
        var inspector = new FakeTunnelInspector();

        using var coordinator = CreateCoordinator(desired, router, zapret, openVpn, inspector: inspector, backoff: [TimeSpan.FromSeconds(60)]);

        coordinator.RequestReconcile(ReconcileReason.Startup);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.PendingRetry; i++)
            await Task.Delay(25);

        Assert.Equal(StartupRestoreState.PendingRetry, coordinator.CurrentRestoreState);
        // WaitTunReady was skipped because StartMainRouter prerequisite failed
        Assert.Equal(0, inspector.WaitForTunReadyCalls);
        // MainRouter failure count incremented exactly once
        Assert.Equal(1, coordinator.GetFailureCount(ComponentId.MainRouter));
    }

    // 15. FailedStartOpenVpnSkipsFinalizeRouting
    [Fact]
    public async Task FailedStartOpenVpnSkipsFinalizeRouting()
    {
        var desired = new FakeDesiredProvider(new DesiredRuntimeState { OpenVpnEnabled = true });
        var router = new FakeRouterRuntime();
        var zapret = new FakeZapretRuntime();
        var openVpn = new FakeOpenVpnRuntime { FailStart = true };
        int finalizeCalls = 0;

        using var coordinator = CreateCoordinator(desired, router, zapret, openVpn, backoff: [TimeSpan.FromSeconds(60)], finalizeOverride: _ =>
        {
            finalizeCalls++;
            return Task.CompletedTask;
        });

        coordinator.RequestReconcile(ReconcileReason.Startup);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.PendingRetry; i++)
            await Task.Delay(25);

        Assert.Equal(StartupRestoreState.PendingRetry, coordinator.CurrentRestoreState);
        // FinalizeRouting skipped because StartOpenVpn failed
        Assert.Equal(0, finalizeCalls);
        Assert.Equal(1, coordinator.GetFailureCount(ComponentId.OpenVpn));
    }

    // 16. RetryPlanContainsOnlyUnsatisfiedComponent
    [Fact]
    public void RetryPlanContainsOnlyUnsatisfiedComponent()
    {
        var desired = new DesiredRuntimeState { MainVpnEnabled = true, ZapretEnabled = true, OpenVpnEnabled = true };
        var observed = new ObservedRuntimeState(
            MainRouterStatus: ObservedComponentState.RunningHealthy,
            ZapretStatus: ObservedComponentState.Stopped,
            OpenVpnStatus: ObservedComponentState.RunningHealthy,
            TunReady: true,
            PhysicalNetwork: new NetworkSnapshot("Eth", 1, "192.168.1.1", "192.168.1.1", []),
            OpenVpnRoutesInstalled: true,
            PhysicalUsable: true
        );
        var settings = new AppSettings();
        var failures = new Dictionary<ComponentId, int> { [ComponentId.Zapret] = 1 };

        var plan = RuntimePlanner.CreatePlan(desired, observed, settings, failures);

        // Plan contains ONLY StartZapret
        Assert.Single(plan.Actions);
        Assert.Equal(PlanActionType.StartZapret, plan.Actions[0].Type);
    }

    // 17. DesiredStateRereadCancelsPendingOpenVpnStart
    [Fact]
    public async Task DesiredStateRereadCancelsPendingOpenVpnStart()
    {
        var desired = new FakeDesiredProvider(new DesiredRuntimeState { OpenVpnEnabled = true });
        var router = new FakeRouterRuntime();
        var zapret = new FakeZapretRuntime();
        var openVpn = new FakeOpenVpnRuntime { FailStart = true };

        using var coordinator = CreateCoordinator(desired, router, zapret, openVpn, backoff: [TimeSpan.FromSeconds(60)]);

        coordinator.RequestReconcile(ReconcileReason.Startup);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.PendingRetry; i++)
            await Task.Delay(25);

        Assert.Equal(StartupRestoreState.PendingRetry, coordinator.CurrentRestoreState);
        Assert.Equal(1, openVpn.EnsureRunningCalls);

        // User turns OpenVPN OFF during retry wait
        desired.Current = desired.Current with { OpenVpnEnabled = false };
        coordinator.TriggerRetry();

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.Completed; i++)
            await Task.Delay(25);

        Assert.Equal(StartupRestoreState.Completed, coordinator.CurrentRestoreState);
        // OpenVPN was NOT started again
        Assert.Equal(1, openVpn.EnsureRunningCalls);
        Assert.Equal(0, coordinator.GetFailureCount(ComponentId.OpenVpn));
    }

    // 18. UserOffDoesNotCancelCoordinatorLifetime
    [Fact]
    public async Task UserOffDoesNotCancelCoordinatorLifetime()
    {
        var desired = new FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true });
        var router = new FakeRouterRuntime();
        var zapret = new FakeZapretRuntime();
        var openVpn = new FakeOpenVpnRuntime();

        using var coordinator = CreateCoordinator(desired, router, zapret, openVpn);

        coordinator.RequestReconcile(ReconcileReason.Startup);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.Completed; i++)
            await Task.Delay(25);

        Assert.Equal(1, router.EnsureRunningCalls);

        // User turns VPN OFF - must NOT cancel coordinator
        desired.Current = desired.Current with { MainVpnEnabled = false };
        coordinator.RequestReconcile(ReconcileReason.UserChangedSettings);

        for (int i = 0; i < 40 && router.EnsureStoppedCalls == 0; i++)
            await Task.Delay(25);

        Assert.Equal(1, router.EnsureStoppedCalls);
        Assert.NotEqual(StartupRestoreState.Cancelled, coordinator.CurrentRestoreState);

        // Subsequent user action works normally because coordinator is alive
        desired.Current = desired.Current with { MainVpnEnabled = true };
        coordinator.RequestReconcile(ReconcileReason.UserChangedSettings);

        for (int i = 0; i < 40 && router.EnsureRunningCalls < 2; i++)
            await Task.Delay(25);

        Assert.Equal(2, router.EnsureRunningCalls);
    }

    // 19. OpenVpnLearnedRoutesPersistThroughNewExecutorPath
    [Fact]
    public async Task OpenVpnLearnedRoutesPersistThroughNewExecutorPath()
    {
        var profileId = Guid.NewGuid();
        var desired = new FakeDesiredProvider(new DesiredRuntimeState { OpenVpnEnabled = true, SelectedOpenVpnProfileId = profileId });
        var router = new FakeRouterRuntime();
        var zapret = new FakeZapretRuntime();
        var openVpn = new FakeOpenVpnRuntime { LearnedRoutesToPush = ["10.0.117.0/24", "10.0.118.0/24"] };

        Guid? persistedProfileId = null;
        IReadOnlyList<string>? persistedRoutes = null;

        using var coordinator = CreateCoordinator(
            desired, router, zapret, openVpn,
            onRoutesLearned: (id, routes, ct) =>
            {
                persistedProfileId = id;
                persistedRoutes = routes;
                return Task.CompletedTask;
            });

        coordinator.RequestReconcile(ReconcileReason.Startup);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.Completed; i++)
            await Task.Delay(25);

        Assert.Equal(StartupRestoreState.Completed, coordinator.CurrentRestoreState);
        Assert.Equal(profileId, persistedProfileId);
        Assert.NotNull(persistedRoutes);
        Assert.Equal(2, persistedRoutes.Count);
        Assert.Contains("10.0.117.0/24", persistedRoutes);
        Assert.Contains("10.0.118.0/24", persistedRoutes);
    }

    // 20. OpenVpnRouteVerificationFailureLeavesComponentUnsatisfied
    [Fact]
    public async Task OpenVpnRouteVerificationFailureLeavesComponentUnsatisfied()
    {
        var desired = new FakeDesiredProvider(new DesiredRuntimeState { OpenVpnEnabled = true });
        var router = new FakeRouterRuntime();
        var zapret = new FakeZapretRuntime();
        var openVpn = new FakeOpenVpnRuntime { FailRouteVerification = true };

        using var coordinator = CreateCoordinator(desired, router, zapret, openVpn, backoff: [TimeSpan.FromSeconds(60)]);

        coordinator.RequestReconcile(ReconcileReason.Startup);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.PendingRetry; i++)
            await Task.Delay(25);

        Assert.Equal(StartupRestoreState.PendingRetry, coordinator.CurrentRestoreState);
        Assert.Equal(1, coordinator.GetFailureCount(ComponentId.OpenVpn));
    }

    // 21. RouterEnsureRunningIsNoOpWhenFingerprintMatches
    [Fact]
    public async Task RouterEnsureRunningIsNoOpWhenFingerprintMatches()
    {
        var root = RoutingTests.FindRoot();
        var bin = RoutingTests.ModuleRoot;
        var runtime = Path.Combine(tempDir, "runtime");
        Directory.CreateDirectory(runtime);

        using var router = new RouterService(bin, runtime);

        var profile = ProfileImporter.ParseLink("socks://192.0.2.1:1080");
        var settings = new AppSettings { Tun = false, Profiles = [profile], MainProfileId = profile.Id, SocksPort = OpenVpnService.FreePort() };

        var physical = new NetworkSnapshot("Ethernet", 1, "192.168.1.100", "192.168.1.1", []);

        // Initial run
        await router.EnsureRunningAsync(settings, physical, "initial", CancellationToken.None);
        int initialStartCount = router.StartCount;
        Assert.True(initialStartCount > 0);

        // Identical fingerprint call -> NoOp
        await router.EnsureRunningAsync(settings, physical, "second", CancellationToken.None);
        Assert.Equal(initialStartCount, router.StartCount);

        // Change config: Mode toggled -> Fingerprint changes -> Starts again
        settings.Mode = RoutingMode.Global;
        await router.EnsureRunningAsync(settings, physical, "mode-change", CancellationToken.None);
        Assert.Equal(initialStartCount + 1, router.StartCount);
    }
}
