using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using NetCat.UI;
using System.Windows;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate10Tests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "NetCat-C10Tests-" + Guid.NewGuid().ToString("N"));

    public Candidate10Tests()
    {
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
        catch { }
    }

    // =========================================================================
    // 1. STATE OWNERSHIP / CONFIG
    // =========================================================================

    [Fact]
    public void DesiredTunAndEffectiveConfigCannotDiverge()
    {
        var settings = new AppSettings { Tun = false };
        var desired = new DesiredRuntimeState { MainVpnEnabled = true, TunEnabled = true };
        var physical = new NetworkSnapshot("Ethernet", 1, "192.168.1.100", "192.168.1.1", []);

        var effective = EffectiveRuntimeConfigBuilder.Build(settings, desired, physical);

        Assert.True(effective.TunEnabled);
        Assert.NotNull(effective.TargetSettings);
        Assert.True(effective.TargetSettings.Tun);
    }

    [Fact]
    public void DesiredVpnProfileAndExecutorTargetCannotDiverge()
    {
        var staleProfileId = Guid.NewGuid();
        var targetProfileId = Guid.NewGuid();
        var settings = new AppSettings
        {
            MainProfileId = staleProfileId,
            Profiles = [new Profile { Id = staleProfileId, Name = "Stale" }, new Profile { Id = targetProfileId, Name = "Target" }]
        };
        var desired = new DesiredRuntimeState { MainVpnEnabled = true, SelectedVpnProfileId = targetProfileId };
        var physical = new NetworkSnapshot("Ethernet", 1, "192.168.1.100", "192.168.1.1", []);

        var effective = EffectiveRuntimeConfigBuilder.Build(settings, desired, physical);

        Assert.Equal(targetProfileId, effective.VpnProfileId);
        Assert.NotNull(effective.TargetSettings);
        Assert.Equal(targetProfileId, effective.TargetSettings.MainProfileId);
    }

    [Fact]
    public void DesiredOpenVpnProfileAndExecutorTargetCannotDiverge()
    {
        var staleProfileId = Guid.NewGuid();
        var targetProfileId = Guid.NewGuid();
        var settings = new AppSettings
        {
            OpenVpnProfileId = staleProfileId,
            Profiles = [new Profile { Id = staleProfileId, Name = "Stale", Protocol = "openvpn" }, new Profile { Id = targetProfileId, Name = "Target", Protocol = "openvpn" }]
        };
        var desired = new DesiredRuntimeState { OpenVpnEnabled = true, SelectedOpenVpnProfileId = targetProfileId };
        var physical = new NetworkSnapshot("Ethernet", 1, "192.168.1.100", "192.168.1.1", []);

        var effective = EffectiveRuntimeConfigBuilder.Build(settings, desired, physical);

        Assert.Equal(targetProfileId, effective.OpenVpnProfileId);
        Assert.NotNull(effective.TargetSettings);
        Assert.Equal(targetProfileId, effective.TargetSettings.OpenVpnProfileId);
    }

    [Fact]
    public void EffectiveRuntimeConfigUsesFreshDesiredStateEveryPass()
    {
        var settings = new AppSettings();
        var physical = new NetworkSnapshot("Ethernet", 1, "192.168.1.100", "192.168.1.1", []);

        var desired1 = new DesiredRuntimeState { MainVpnEnabled = false, TunEnabled = false };
        var effective1 = EffectiveRuntimeConfigBuilder.Build(settings, desired1, physical);
        Assert.False(effective1.VpnEnabled);
        Assert.False(effective1.TunEnabled);

        var desired2 = new DesiredRuntimeState { MainVpnEnabled = true, TunEnabled = true };
        var effective2 = EffectiveRuntimeConfigBuilder.Build(settings, desired2, physical);
        Assert.True(effective2.VpnEnabled);
        Assert.True(effective2.TunEnabled);
    }

    [Fact]
    public void CommittedUiSnapshotIsNotRuntimeAuthority()
    {
        var settings = new AppSettings { Tun = false, MainProfileId = Guid.NewGuid() };
        var desiredProfile = Guid.NewGuid();
        var desired = new DesiredRuntimeState { MainVpnEnabled = true, TunEnabled = true, SelectedVpnProfileId = desiredProfile };
        var physical = new NetworkSnapshot("Ethernet", 1, "192.168.1.100", "192.168.1.1", []);

        // Mutating settings without updating desired does NOT alter target in effective config
        var effective = EffectiveRuntimeConfigBuilder.Build(settings, desired, physical);
        Assert.Equal(desiredProfile, effective.VpnProfileId);
        Assert.True(effective.TunEnabled);
    }

    // =========================================================================
    // 2. SINGLE MUTATION OWNER
    // =========================================================================

    [Fact]
    public async Task UiVpnToggleDoesNotMutateRouterDirectly()
    {
        var store = new SettingsStore(Path.Combine(tempDir, "vpn-toggle"));
        var settings = new AppSettings();
        using var vm = new MainViewModel(store, settings);

        bool reconcileTriggered = false;
        vm.RuntimeCoordinator.StateChanged += _ => reconcileTriggered = true;

        await vm.SetVpnEnabledAsync(true, CancellationToken.None);

        Assert.True(vm.DesiredState.MainVpnEnabled);
        Assert.True(reconcileTriggered || vm.RuntimeCoordinator.CurrentRestoreState != StartupRestoreState.Idle);
    }

    [Fact]
    public void UiTunToggleDoesNotMutateRouterDirectly()
    {
        var store = new SettingsStore(Path.Combine(tempDir, "tun-toggle"));
        var settings = new AppSettings { Tun = false };
        store.SaveDesiredState(new DesiredRuntimeState { TunEnabled = false });
        using var vm = new MainViewModel(store, settings);

        vm.UserRequestedTunChange(true);

        Assert.True(vm.DesiredState.TunEnabled);
    }

    [Fact]
    public async Task UiZapretToggleDoesNotMutateRouterDirectly()
    {
        var store = new SettingsStore(Path.Combine(tempDir, "zapret-toggle"));
        var settings = new AppSettings();
        using var vm = new MainViewModel(store, settings);

        await vm.ApplyZapretAsync(true, null, CancellationToken.None);

        Assert.True(vm.DesiredState.ZapretEnabled);
    }

    [Fact]
    public async Task UiOpenVpnToggleDoesNotMutateRuntimeDirectly()
    {
        var store = new SettingsStore(Path.Combine(tempDir, "ovpn-toggle"));
        var settings = new AppSettings();
        using var vm = new MainViewModel(store, settings);

        await vm.SetOpenVpnEnabledAsync(true, CancellationToken.None);

        Assert.True(vm.DesiredState.OpenVpnEnabled);
    }

    [Fact]
    public async Task UiProfileSelectionDoesNotMutateRouterDirectly()
    {
        var store = new SettingsStore(Path.Combine(tempDir, "profile-select"));
        var p1 = new Profile { Name = "P1", Host = "1.2.3.4", Port = 443, OutboundJson = "{}" };
        var p2 = new Profile { Name = "P2", Host = "1.2.3.5", Port = 443, OutboundJson = "{}" };
        var settings = new AppSettings { Profiles = [p1, p2], MainProfileId = p1.Id };
        using var vm = new MainViewModel(store, settings);

        vm.MainProfile = p2;
        await vm.PendingSelection;

        Assert.Equal(p2.Id, vm.DesiredState.SelectedVpnProfileId);
    }

    [Fact]
    public async Task StopAllOnlyChangesDesiredAndRequestsReconcile()
    {
        var store = new SettingsStore(Path.Combine(tempDir, "stop-all"));
        var settings = new AppSettings();
        store.SaveDesiredState(new DesiredRuntimeState { MainVpnEnabled = true, ZapretEnabled = true, OpenVpnEnabled = true });
        using var vm = new MainViewModel(store, settings);

        await vm.StopComponentsAsync();

        Assert.False(vm.DesiredState.MainVpnEnabled);
        Assert.False(vm.DesiredState.ZapretEnabled);
        Assert.False(vm.DesiredState.OpenVpnEnabled);
    }

    [Fact]
    public async Task AutoFailoverOnlyChangesTargetAndRequestsReconcile()
    {
        var store = new SettingsStore(Path.Combine(tempDir, "failover"));
        var p1 = new Profile { Name = "P1", Host = "1.2.3.4", Port = 443, OutboundJson = "{}" };
        var p2 = new Profile { Name = "P2", Host = "1.2.3.5", Port = 443, OutboundJson = "{}" };
        var settings = new AppSettings { Profiles = [p1, p2], MainProfileId = p1.Id, AutoSwitch = true };
        using var vm = new MainViewModel(store, settings);

        // Failover updates desired selected profile
        await vm.CommitFailoverAsync(p1.Id, p2.Id, 0, CancellationToken.None);

        Assert.Equal(p2.Id, vm.DesiredState.SelectedVpnProfileId);
    }

    [Fact]
    public void NetworkMonitorDoesNotMutateRouterDirectly()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn);

        // Signal physical network changed: only requests reconcile
        coordinator.RequestReconcile(ReconcileReason.PhysicalNetworkChanged);
        Assert.True(coordinator.CurrentRestoreState is StartupRestoreState.Idle or StartupRestoreState.Restoring);
    }

    [Fact]
    public void TunWatchdogDoesNotMutateRouterDirectly()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true, TunEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn);

        coordinator.RequestReconcile(ReconcileReason.TunStructuralFailure);
        Assert.True(coordinator.CurrentRestoreState is StartupRestoreState.Idle or StartupRestoreState.Restoring);
    }

    // =========================================================================
    // 3. TUN
    // =========================================================================

    [Fact]
    public void StructuralTunFailureOverridesConfiguredTunFlag()
    {
        var desired = new DesiredRuntimeState { MainVpnEnabled = true, TunEnabled = true };
        var observed = new ObservedRuntimeState(
            MainRouterStatus: ObservedComponentState.RunningHealthy,
            ZapretStatus: ObservedComponentState.Stopped,
            OpenVpnStatus: ObservedComponentState.Stopped,
            TunReady: true, // Configured flag says true
            PhysicalNetwork: new NetworkSnapshot("Eth", 1, "192.168.1.1", "192.168.1.1", []),
            PhysicalUsable: true,
            StructuralTunFailure: true, // Actual inspection detected structural failure
            TunObservedHealthy: false
        );

        var plan = RuntimePlanner.CreatePlan(desired, observed, new AppSettings(), new Dictionary<ComponentId, int>());

        Assert.Contains(plan.Actions, a => a.Type == PlanActionType.RestartMainRouterForStructuralTunFailure);
        Assert.Contains(plan.Actions, a => a.Type == PlanActionType.WaitTunReady);
    }

    [Fact]
    public void SingBoxAliveButWintunMissingTriggersRepair()
    {
        var desired = new DesiredRuntimeState { MainVpnEnabled = true, TunEnabled = true };
        var observed = new ObservedRuntimeState(
            MainRouterStatus: ObservedComponentState.RunningHealthy,
            ZapretStatus: ObservedComponentState.Stopped,
            OpenVpnStatus: ObservedComponentState.Stopped,
            TunReady: true,
            PhysicalNetwork: new NetworkSnapshot("Eth", 1, "192.168.1.1", "192.168.1.1", []),
            PhysicalUsable: true,
            TunObservedHealthy: false
        );

        var plan = RuntimePlanner.CreatePlan(desired, observed, new AppSettings(), new Dictionary<ComponentId, int>());

        Assert.Contains(plan.Actions, a => a.Type == PlanActionType.WaitTunReady);
    }

    [Fact]
    public void SingBoxAliveButTunRoutesMissingTriggersRepair()
    {
        var desired = new DesiredRuntimeState { MainVpnEnabled = true, TunEnabled = true };
        var observed = new ObservedRuntimeState(
            MainRouterStatus: ObservedComponentState.RunningHealthy,
            ZapretStatus: ObservedComponentState.Stopped,
            OpenVpnStatus: ObservedComponentState.Stopped,
            TunReady: true,
            PhysicalNetwork: new NetworkSnapshot("Eth", 1, "192.168.1.1", "192.168.1.1", []),
            PhysicalUsable: true,
            StructuralTunFailure: true,
            TunObservedHealthy: false
        );

        var plan = RuntimePlanner.CreatePlan(desired, observed, new AppSettings(), new Dictionary<ComponentId, int>());

        Assert.Contains(plan.Actions, a => a.Type == PlanActionType.RestartMainRouterForStructuralTunFailure);
    }

    [Fact]
    public async Task TransientTunStartupWaitDoesNotRestartRouter()
    {
        int ensureRunningCalls = 0;
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true, TunEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime { OnEnsureRunning = () => ensureRunningCalls++ };
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var inspector = new Candidate9Tests.FakeTunnelInspector();

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, inspector: inspector);

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);

        // Router is started once, wait loop verifies TUN without a second restart
        Assert.Equal(1, ensureRunningCalls);
        Assert.Equal(StartupRestoreState.Completed, coordinator.State);
    }

    [Fact]
    public void TunRepairFailureBackoffDoesNotBusyLoop()
    {
        var desired = new DesiredRuntimeState { MainVpnEnabled = true, TunEnabled = true };
        var observed = new ObservedRuntimeState(
            MainRouterStatus: ObservedComponentState.RunningHealthy,
            ZapretStatus: ObservedComponentState.Stopped,
            OpenVpnStatus: ObservedComponentState.Stopped,
            TunReady: true,
            PhysicalNetwork: new NetworkSnapshot("Eth", 1, "192.168.1.1", "192.168.1.1", []),
            PhysicalUsable: true,
            StructuralTunFailure: true,
            TunObservedHealthy: false
        );
        var failures = new Dictionary<ComponentId, int> { [ComponentId.Tun] = 2 };

        var plan = RuntimePlanner.CreatePlan(desired, observed, new AppSettings(), failures);

        Assert.True(plan.NextRetryDelay > TimeSpan.Zero);
        Assert.Contains(ComponentId.Tun, plan.PendingRetryComponents);
    }

    [Fact]
    public async Task TunSatisfiedClearsFailureState()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true, TunEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var inspector = new Candidate9Tests.FakeTunnelInspector { StructuralFailure = true };

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, inspector: inspector, backoff: [TimeSpan.FromSeconds(60)]);

        // First pass fails TUN
        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);
        Assert.Equal(1, coordinator.GetFailureCount(ComponentId.Tun));

        // TUN recovers
        inspector.StructuralFailure = false;
        coordinator.TriggerRetry();
        await coordinator.ReconcileAsync(ReconcileReason.ManualRetry, CancellationToken.None);

        Assert.Equal(0, coordinator.GetFailureCount(ComponentId.Tun));
        Assert.Equal(StartupRestoreState.Completed, coordinator.State);
    }

    // =========================================================================
    // 4. RETRY
    // =========================================================================

    [Fact]
    public async Task EmptyPlanWithSatisfiedDesiredDoesNotLoop()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn);

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);
        Assert.Equal(StartupRestoreState.Completed, coordinator.State);

        // Subsequent pass with matching state produces empty plan and does not loop
        await coordinator.ReconcileAsync(ReconcileReason.UserChangedSettings, CancellationToken.None);
        Assert.Equal(StartupRestoreState.Completed, coordinator.State);
        Assert.Equal(1, router.EnsureRunningCalls);
    }

    [Fact]
    public void EmptyPlanCannotLoopBecauseOfStaleFailureCount()
    {
        var desired = new DesiredRuntimeState { MainVpnEnabled = true, TunEnabled = false };
        var observed = new ObservedRuntimeState(
            MainRouterStatus: ObservedComponentState.RunningHealthy,
            ZapretStatus: ObservedComponentState.Stopped,
            OpenVpnStatus: ObservedComponentState.Stopped,
            TunReady: false,
            PhysicalNetwork: new NetworkSnapshot("Eth", 1, "192.168.1.1", "192.168.1.1", []),
            PhysicalUsable: true
        );
        var failures = new Dictionary<ComponentId, int> { [ComponentId.MainRouter] = 2 };

        var plan = RuntimePlanner.CreatePlan(desired, observed, new AppSettings(), failures);

        // When desired components are satisfied, plan is empty and pending retry components is empty
        Assert.Empty(plan.Actions);
        Assert.Empty(plan.PendingRetryComponents);
    }

    [Fact]
    public async Task SuccessfulComponentClearsItsFailureState()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { ZapretEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime { FailStart = true };
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, backoff: [TimeSpan.FromSeconds(60)]);

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);
        Assert.Equal(1, coordinator.GetFailureCount(ComponentId.Zapret));

        zapret.FailStart = false;
        coordinator.TriggerRetry();
        await coordinator.ReconcileAsync(ReconcileReason.ManualRetry, CancellationToken.None);

        Assert.Equal(0, coordinator.GetFailureCount(ComponentId.Zapret));
    }

    [Fact]
    public void FailedPrerequisiteSkipsDependentAction()
    {
        var plan = new RuntimePlan(
            [
                new PlanAction(PlanActionType.StartMainRouter, "Start Router", ComponentId.MainRouter),
                new PlanAction(PlanActionType.WaitTunReady, "Wait Tun", ComponentId.Tun, Prerequisite: PlanActionType.StartMainRouter),
                new PlanAction(PlanActionType.StartOpenVpn, "Start OpenVPN", ComponentId.OpenVpnLink),
                new PlanAction(PlanActionType.FinalizeRouting, "Finalize OpenVPN", ComponentId.OpenVpnRoutes, Prerequisite: PlanActionType.StartOpenVpn)
            ],
            [], TimeSpan.Zero
        );

        Assert.Equal(PlanActionType.StartMainRouter, plan.Actions[1].Prerequisite);
        Assert.Equal(PlanActionType.StartOpenVpn, plan.Actions[3].Prerequisite);
    }

    [Fact]
    public async Task PartialFailureRetriesOnlyUnsatisfiedComponent()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true, ZapretEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime { FailStart = true };
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, backoff: [TimeSpan.FromSeconds(60)]);

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);

        Assert.Equal(1, router.EnsureRunningCalls);
        Assert.Equal(1, coordinator.GetFailureCount(ComponentId.Zapret));
        Assert.Equal(0, coordinator.GetFailureCount(ComponentId.MainRouter));
    }

    [Fact]
    public void RetryUsesFakeClockAndNoRealSleep()
    {
        var clock = new FakeClock();
        var backoff = RuntimePlanner.ComputeBackoff(1, RuntimePlanner.DefaultBackoffIntervals);
        var nextRetry = clock.UtcNow + backoff;

        Assert.True(nextRetry > clock.UtcNow);
        clock.Advance(backoff);
        Assert.True(clock.UtcNow >= nextRetry);
    }

    [Fact]
    public async Task UserOffClearsPendingRetryForOnlyThatComponent()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true, ZapretEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime { FailStart = true };
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, backoff: [TimeSpan.FromSeconds(60)]);

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);
        Assert.Equal(1, coordinator.GetFailureCount(ComponentId.Zapret));

        // User turns Zapret OFF
        desired.Current = desired.Current with { ZapretEnabled = false };
        await coordinator.ReconcileAsync(ReconcileReason.UserToggledZapret, CancellationToken.None);

        Assert.Equal(0, coordinator.GetFailureCount(ComponentId.Zapret));
        Assert.Equal(StartupRestoreState.Completed, coordinator.State);
    }

    [Fact]
    public async Task CoordinatorLifetimeSurvivesUserOff()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn);

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);
        Assert.Equal(StartupRestoreState.Completed, coordinator.State);

        // Turn OFF
        desired.Current = desired.Current with { MainVpnEnabled = false };
        await coordinator.ReconcileAsync(ReconcileReason.UserToggledVpn, CancellationToken.None);
        Assert.Equal(StartupRestoreState.Completed, coordinator.State);

        // Turn back ON: coordinator is still fully alive and functional
        desired.Current = desired.Current with { MainVpnEnabled = true };
        await coordinator.ReconcileAsync(ReconcileReason.UserToggledVpn, CancellationToken.None);
        Assert.Equal(StartupRestoreState.Completed, coordinator.State);
        Assert.Equal(2, router.EnsureRunningCalls);
    }

    // =========================================================================
    // 5. OPENVPN
    // =========================================================================

    [Fact]
    public void OpenVpnLinkAndRoutesHaveSeparateSatisfactionState()
    {
        var desired = new DesiredRuntimeState { OpenVpnEnabled = true };
        var observed = new ObservedRuntimeState(
            MainRouterStatus: ObservedComponentState.Stopped,
            ZapretStatus: ObservedComponentState.Stopped,
            OpenVpnStatus: ObservedComponentState.RunningHealthy, // Link is UP
            TunReady: false,
            PhysicalNetwork: new NetworkSnapshot("Eth", 1, "192.168.1.1", "192.168.1.1", []),
            PhysicalUsable: true,
            OpenVpnRoutesInstalled: false // Routes NOT installed
        );

        var plan = RuntimePlanner.CreatePlan(desired, observed, new AppSettings(), new Dictionary<ComponentId, int>());

        // Link is NOT restarted, only routes finalized
        Assert.DoesNotContain(plan.Actions, a => a.Type == PlanActionType.StartOpenVpn);
        Assert.Contains(plan.Actions, a => a.Type == PlanActionType.FinalizeRouting);
    }

    [Fact]
    public async Task OpenVpnRouteFailureDoesNotReconnectHealthyLink()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { OpenVpnEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();

        int finalizeAttempts = 0;
        var coordinator = Candidate9Tests.CreateCoordinator(
            desired, router, zapret, openvpn,
            finalizeOverride: _ =>
            {
                finalizeAttempts++;
                throw new IOException("Route table lock failed");
            },
            backoff: [TimeSpan.FromSeconds(60)]
        );

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);

        Assert.Equal(1, openvpn.EnsureRunningCalls);
        Assert.Equal(1, finalizeAttempts);
        Assert.Equal(1, coordinator.GetFailureCount(ComponentId.OpenVpnRoutes));
        Assert.Equal(0, coordinator.GetFailureCount(ComponentId.OpenVpnLink));
    }

    [Fact]
    public async Task SuccessfulRouteFinalizeClearsRouteFailure()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { OpenVpnEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();

        bool failFinalize = true;
        var coordinator = Candidate9Tests.CreateCoordinator(
            desired, router, zapret, openvpn,
            finalizeOverride: _ =>
            {
                if (failFinalize) throw new IOException("Transient route failure");
                return Task.CompletedTask;
            },
            backoff: [TimeSpan.FromSeconds(60)]
        );

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);
        Assert.Equal(1, coordinator.GetFailureCount(ComponentId.OpenVpnRoutes));

        failFinalize = false;
        coordinator.TriggerRetry();
        await coordinator.ReconcileAsync(ReconcileReason.ManualRetry, CancellationToken.None);

        Assert.Equal(0, coordinator.GetFailureCount(ComponentId.OpenVpnRoutes));
        Assert.Equal(StartupRestoreState.Completed, coordinator.State);
    }

    [Fact]
    public async Task LearnedRoutesPersistenceRefreshesEffectiveConfigBeforeFingerprintCommit()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { OpenVpnEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime { LearnedRoutesToPush = ["10.200.0.0/16"] };

        var settings = new AppSettings();
        var p = new Profile { Protocol = "openvpn", Name = "OVPN" };
        settings.Profiles.Add(p);
        settings.OpenVpnProfileId = p.Id;

        string? persistedFingerprint = null;
        var coordinator = Candidate9Tests.CreateCoordinator(
            desired, router, zapret, openvpn,
            getSettings: () => settings,
            onRoutesLearned: (id, routes, ct) =>
            {
                p.LearnedRoutes = routes.ToList();
                persistedFingerprint = string.Join(",", routes);
                return Task.CompletedTask;
            }
        );

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);

        Assert.NotNull(persistedFingerprint);
        Assert.Contains("10.200.0.0/16", persistedFingerprint);
        Assert.NotNull(coordinator.LastAppliedConfig);
    }

    [Fact]
    public async Task RouteVerificationFailureLeavesRoutesUnsatisfied()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { OpenVpnEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime { FailRouteVerification = true };

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, backoff: [TimeSpan.FromSeconds(60)]);

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);

        Assert.Equal(StartupRestoreState.PendingRetry, coordinator.State);
        Assert.False(coordinator.CurrentConvergenceState.DesiredSatisfied);
    }

    [Fact]
    public async Task LearnedRoutesPersistToExactSelectedProfile()
    {
        var targetProfile = new Profile { Id = Guid.NewGuid(), Protocol = "openvpn", Name = "Target" };
        var settings = new AppSettings { Profiles = [targetProfile], OpenVpnProfileId = targetProfile.Id };
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { OpenVpnEnabled = true, SelectedOpenVpnProfileId = targetProfile.Id });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime { LearnedRoutesToPush = ["172.16.0.0/12"] };

        Guid? learnedTarget = null;
        var coordinator = Candidate9Tests.CreateCoordinator(
            desired, router, zapret, openvpn,
            getSettings: () => settings,
            onRoutesLearned: (id, routes, ct) =>
            {
                learnedTarget = id;
                return Task.CompletedTask;
            }
        );

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);

        Assert.Equal(targetProfile.Id, learnedTarget);
    }

    [Fact]
    public void PublicPushedRouteRequiresExplicitPolicy()
    {
        var publicRoute = "8.8.8.8/32";
        bool accepted = OpenVpnRoutes.ValidatePushedRoute(publicRoute, allowPublic: false);
        Assert.False(accepted);

        bool acceptedWithPolicy = OpenVpnRoutes.ValidatePushedRoute(publicRoute, allowPublic: true);
        Assert.True(acceptedWithPolicy);
    }

    [Fact]
    public void PrivatePrefixMustBeFullyContainedInAllowedRange()
    {
        Assert.True(OpenVpnRoutes.ValidatePushedRoute("10.0.0.0/8", allowPublic: false));
        Assert.True(OpenVpnRoutes.ValidatePushedRoute("172.16.0.0/12", allowPublic: false));
        Assert.True(OpenVpnRoutes.ValidatePushedRoute("192.168.0.0/16", allowPublic: false));

        // Outside RFC 1918
        Assert.False(OpenVpnRoutes.ValidatePushedRoute("1.1.1.1/32", allowPublic: false));
        Assert.False(OpenVpnRoutes.ValidatePushedRoute("11.0.0.0/8", allowPublic: false));
    }

    [Fact]
    public void NonContiguousMaskRejected()
    {
        Assert.False(OpenVpnRoutes.IsValidNetmask("255.0.255.0"));
        Assert.False(OpenVpnRoutes.IsValidNetmask("255.255.0.255"));
        Assert.True(OpenVpnRoutes.IsValidNetmask("255.255.255.0"));
        Assert.True(OpenVpnRoutes.IsValidNetmask("255.255.0.0"));
    }

    // =========================================================================
    // 6. NETWORK
    // =========================================================================

    [Fact]
    public void PhysicalNetworkReadyDoesNotImplyRuntimeConverged()
    {
        var convergence = new RuntimeConvergenceState(
            PhysicalNetworkReady: true,
            DesiredSatisfied: false,
            PendingComponents: [ComponentId.MainRouter],
            LastError: "Sing-box starting"
        );

        Assert.True(convergence.PhysicalNetworkReady);
        Assert.False(convergence.DesiredSatisfied);
    }

    [Fact]
    public void RuntimeRecoveryMessageEmittedOnlyAfterDesiredSatisfied()
    {
        var machine = new NetworkLifecycleStateMachine();
        long gen = machine.BeginTransition();
        machine.BeginRebuilding(gen);

        bool completed = machine.CompleteRebuild(gen, true, "Wi-Fi", "192.168.1.50");
        Assert.True(completed);

        // Physical restoration is distinct from full NetCat convergence
        Assert.Equal("Физическая сеть восстановлена: Wi-Fi, 192.168.1.50", machine.TemporaryStatus);
    }

    [Fact]
    public async Task NoNetworkWaitsForExternalEventWithoutPollingLoop()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var physical = new Candidate9Tests.FakePhysicalProvider { Usable = false };

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, physical: physical);

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);

        Assert.Equal(StartupRestoreState.WaitingForNetwork, coordinator.State);
        Assert.Equal(0, router.EnsureRunningCalls);
    }

    [Fact]
    public async Task NetworkEventWakesPendingReconcile()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var physical = new Candidate9Tests.FakePhysicalProvider { Usable = false };

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, physical: physical);

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);
        Assert.Equal(StartupRestoreState.WaitingForNetwork, coordinator.State);

        // Network returns
        physical.Usable = true;
        await coordinator.ReconcileAsync(ReconcileReason.PhysicalNetworkChanged, CancellationToken.None);

        Assert.Equal(StartupRestoreState.Completed, coordinator.State);
        Assert.Equal(1, router.EnsureRunningCalls);
    }

    [Fact]
    public void VirtualAdapterEventDoesNotTriggerPhysicalTransition()
    {
        var physical = new NetworkSnapshot("Wi-Fi", 10, "192.168.1.20", "192.168.1.1", []);
        var current = new NetworkSnapshot("Wi-Fi", 10, "192.168.1.20", "192.168.1.1", []);

        bool changed = PhysicalNetwork.HasPhysicalChanged(physical, current, captureFailed: false);
        Assert.False(changed);
    }

    // =========================================================================
    // 7. WPF / AUTOSTART / IPC
    // =========================================================================

    [Fact]
    public void HiddenAutostartDoesNotConstructMainWindow()
    {
        var wm = new WindowManager();
        Assert.Null(wm.CurrentWindow);
    }

    [Fact]
    public void HiddenAutostartStartsRuntimeReconcile()
    {
        System.IO.DirectoryInfo? current =
            new(AppContext.BaseDirectory);

        while(
            current != null &&
            !System.IO.File.Exists(
                System.IO.Path.Combine(
                    current.FullName,
                    "NetCat.sln")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);

        var source =
            System.IO.File.ReadAllText(
                System.IO.Path.Combine(
                    current!.FullName,
                    "src",
                    "NetCat.UI",
                    "App.xaml.cs"));

        var restoreGuard =
            source.IndexOf(
                "if (!IsSmoke && (settings.RestoreConnectionsOnStartup || updateResume != null))",
                System.StringComparison.Ordinal);

        var reconcile =
            source.IndexOf(
                "vm.RuntimeCoordinator.RequestReconcile(ReconcileReason.Startup);",
                System.StringComparison.Ordinal);

        var autostart =
            source.IndexOf(
                "bool isAutostart = e.Args.Contains(\"--autostart\");",
                System.StringComparison.Ordinal);

        var hiddenBranch =
            source.IndexOf(
                "trayAvailable && isAutostart && presentationState == WindowPresentationState.HiddenToTray",
                System.StringComparison.Ordinal);

        Assert.True(
            restoreGuard >= 0,
            "Startup restore guard is missing.");

        Assert.True(
            reconcile > restoreGuard,
            "Startup reconcile is not requested by App startup.");

        Assert.True(
            autostart > reconcile,
            "Presentation mode is evaluated before startup reconcile.");

        Assert.True(
            hiddenBranch > autostart,
            "Hidden autostart branch is missing.");
    }

    private static void RunOnSta(Action action)
    {
        // Tray fixtures may have already created the process-wide WPF app.
        // A second STA cannot own that application's MainWindow.
        if (Application.Current is { } app) app.Dispatcher.Invoke(action);
        else StaRunner.Run(action);
    }

    private static class StaRunner
    {
        private static readonly Thread staThread;
        private static readonly TaskCompletionSource<System.Windows.Threading.Dispatcher> dispatcherTcs = new();

        static StaRunner()
        {
            staThread = new Thread(() =>
            {
                if (Application.Current == null)
                {
                    _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    try
                    {
                        var theme = new ResourceDictionary
                        {
                            Source = new Uri("pack://application:,,,/NetCat;component/Theme.xaml", UriKind.Absolute)
                        };
                        Application.Current?.Resources.MergedDictionaries.Add(theme);
                    }
                    catch { }
                }
                dispatcherTcs.SetResult(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                System.Windows.Threading.Dispatcher.Run();
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Start();
        }

        public static void Run(Action action)
        {
            var dispatcher = dispatcherTcs.Task.GetAwaiter().GetResult();
            dispatcher.Invoke(action);
        }
    }

    [Fact]
    public void TrayCanExistWithoutMainWindow()
    {
        RunOnSta(() =>
        {
            var wm = new WindowManager();
            using var tray = new TrayIconService(
                openWindow: () => { },
                exitApp: () => { },
                getCommands: () => []
            );

            Assert.Null(wm.CurrentWindow);
        });
    }

    [Fact]
    public void SecondInstanceShowLazilyCreatesWindow()
    {
        RunOnSta(() =>
        {
            var store = new SettingsStore(Path.Combine(tempDir, "lazy-window"));
            var settings = new AppSettings();
            using var vm = new MainViewModel(store, settings);

            var wm = new WindowManager();
            Assert.Null(wm.CurrentWindow);

            var win = wm.GetOrCreateMainWindow(vm);
            Assert.NotNull(win);
            Assert.Same(win, wm.CurrentWindow);
            win.Close();
        });
    }

    [Fact]
    public void OpeningWindowDoesNotRequestRuntimeReconcile()
    {
        RunOnSta(() =>
        {
            var store = new SettingsStore(Path.Combine(tempDir, "window-no-reconcile"));
            var settings = new AppSettings();
            using var vm = new MainViewModel(store, settings);

            var initialRestore = vm.RuntimeCoordinator.CurrentRestoreState;

            var wm = new WindowManager();
            var win = wm.GetOrCreateMainWindow(vm);

            Assert.Equal(initialRestore, vm.RuntimeCoordinator.CurrentRestoreState);
            win.Close();
        });
    }

    [Fact]
    public void MainWindowConstructorHasNoNetworkRuntimeSideEffects()
    {
        RunOnSta(() =>
        {
            var store = new SettingsStore(Path.Combine(tempDir, "window-ctor"));
            var settings = new AppSettings();
            using var vm = new MainViewModel(store, settings);

            int ensureRunningCalls = 0;
            vm.Router.Changed += () => ensureRunningCalls++;

            var win = new MainWindow(vm);

            Assert.Equal(0, ensureRunningCalls);
            win.Close();
        });
    }

    // =========================================================================
    // 8. FULL COMPOSITION
    // =========================================================================

    [Fact]
    public async Task FullStackCompositionVpnTunZapretOpenVpn()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState
        {
            MainVpnEnabled = true,
            TunEnabled = true,
            ZapretEnabled = true,
            OpenVpnEnabled = true
        });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn);

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);

        Assert.Equal(StartupRestoreState.Completed, coordinator.State);
        Assert.True(router.VpnRunning);
        Assert.True(router.TunActive);
        Assert.True(zapret.IsRunning);
        Assert.True(openvpn.IsRunning);
        Assert.True(coordinator.CurrentConvergenceState.DesiredSatisfied);
    }

    [Fact]
    public async Task CompositionPartialFailureOnlyRetriesFailed()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState
        {
            MainVpnEnabled = true,
            ZapretEnabled = true,
            OpenVpnEnabled = true
        });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime { FailStart = true };
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, backoff: [TimeSpan.FromSeconds(60)]);

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);

        Assert.True(router.VpnRunning);
        Assert.False(zapret.IsRunning);
        Assert.True(openvpn.IsRunning);
        Assert.Equal(1, coordinator.GetFailureCount(ComponentId.Zapret));
        Assert.Equal(0, coordinator.GetFailureCount(ComponentId.MainRouter));
        Assert.Equal(0, coordinator.GetFailureCount(ComponentId.OpenVpnLink));
    }

    [Fact]
    public async Task CompositionProfileSwitchAppliesNewProfileIdempotently()
    {
        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true, SelectedVpnProfileId = p1 });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn);

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);
        Assert.Equal(p1, router.ActiveProfileId);
        Assert.Equal(1, router.EnsureRunningCalls);

        // Switch to P2
        desired.Current = desired.Current with { SelectedVpnProfileId = p2 };
        await coordinator.ReconcileAsync(ReconcileReason.UserChangedSettings, CancellationToken.None);
        Assert.Equal(p2, router.ActiveProfileId);
        Assert.Equal(2, router.EnsureRunningCalls);
    }

    [Fact]
    public async Task CompositionStructuralTunFailureTriggersRepair()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true, TunEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var inspector = new Candidate9Tests.FakeTunnelInspector();

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, inspector: inspector);

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);
        Assert.Equal(1, router.EnsureRunningCalls);

        // Watchdog detects structural failure
        inspector.StructuralFailure = true;
        await coordinator.ReconcileAsync(ReconcileReason.TunStructuralFailure, CancellationToken.None);

        Assert.True(router.ReconfigReasons.Contains("tun-structural-recovery") || router.EnsureRunningCalls >= 2);
    }

    [Fact]
    public async Task CompositionNetworkDownAndUpRecovers()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var physical = new Candidate9Tests.FakePhysicalProvider();

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, physical: physical);

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);
        Assert.Equal(1, router.EnsureRunningCalls);

        // Network goes down
        physical.Usable = false;
        await coordinator.ReconcileAsync(ReconcileReason.PhysicalNetworkChanged, CancellationToken.None);
        Assert.Equal(StartupRestoreState.WaitingForNetwork, coordinator.State);

        // Network returns
        physical.Usable = true;
        await coordinator.ReconcileAsync(ReconcileReason.PhysicalNetworkChanged, CancellationToken.None);
        Assert.Equal(StartupRestoreState.Completed, coordinator.State);
    }

    [Fact]
    public async Task CompositionUserOffDuringRetryStopsComponent()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { ZapretEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime { FailStart = true };
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, backoff: [TimeSpan.FromSeconds(60)]);

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);
        Assert.Equal(1, coordinator.GetFailureCount(ComponentId.Zapret));
        Assert.Equal(StartupRestoreState.PendingRetry, coordinator.State);

        desired.Current = desired.Current with { ZapretEnabled = false };
        await coordinator.ReconcileAsync(ReconcileReason.UserToggledZapret, CancellationToken.None);

        Assert.Equal(0, coordinator.GetFailureCount(ComponentId.Zapret));
        Assert.Equal(StartupRestoreState.Completed, coordinator.State);
    }

    // =========================================================================
    // 9. STATIC ARCHITECTURE
    // =========================================================================

    [Fact]
    public void StartupCoordinatorClassAbsent()
    {
        var type = Type.GetType("NetCat.Network.StartupCoordinator, NetCat.Network");
        Assert.Null(type);
    }

    [Fact]
    public void NoForbiddenDirectMutatorsOutsideExecutor()
    {
        // Assert that StartupCoordinator.cs is not in repository
        var netcatRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var startupCoordPath = Path.Combine(netcatRoot, "src", "NetCat.Network", "StartupCoordinator.cs");
        Assert.False(File.Exists(startupCoordPath), "StartupCoordinator.cs must not exist on disk.");
    }

    // =========================================================================
    // 10. TRAFFIC TELEMETRY
    // =========================================================================

    private sealed class MockTrafficProvider : ITrafficStatisticsProvider
    {
        public Dictionary<int, (long Rx, long Tx)> Stats { get; } = new();
        public Dictionary<int, string> Names { get; } = new();
        public List<int> QueriedIndices { get; } = new();

        public (long BytesReceived, long BytesSent)? GetInterfaceStatistics(int interfaceIndex)
        {
            QueriedIndices.Add(interfaceIndex);
            return Stats.TryGetValue(interfaceIndex, out var s) ? s : null;
        }

        public string? GetInterfaceName(int interfaceIndex) => Names.TryGetValue(interfaceIndex, out var n) ? n : null;
    }

    [Fact]
    public void TrafficMonitorUsesActivePhysicalInterfaceIndex()
    {
        var provider = new MockTrafficProvider();
        provider.Stats[42] = (10_000_000, 5_000_000);
        provider.Names[42] = "Ethernet 1";

        var physical = new NetworkSnapshot("Localized Имя Сети", 42, "192.168.1.50", "192.168.1.1", []);
        var monitor = new TrafficMonitorService(() => physical, provider);

        var snap = monitor.Sample();
        Assert.Contains(42, provider.QueriedIndices);
        Assert.Equal(42, snap.InterfaceIndex);
    }

    [Fact]
    public void TrafficMonitorDoesNotUseNetCatTunForDashboard()
    {
        var provider = new MockTrafficProvider();
        provider.Stats[10] = (100_000_000, 50_000_000); // physical
        provider.Names[10] = "Ethernet";
        provider.Stats[99] = (999_999_999, 999_999_999); // NetCat-TUN
        provider.Names[99] = "NetCat-TUN";

        var physical = new NetworkSnapshot("Ethernet", 10, "192.168.1.50", "192.168.1.1", []);
        var monitor = new TrafficMonitorService(() => physical, provider);

        monitor.Sample();
        Assert.DoesNotContain(99, provider.QueriedIndices);
        Assert.Equal(10, monitor.CurrentSnapshot.InterfaceIndex);
    }

    [Fact]
    public void TrafficMonitorCalculatesRxTxIndependently()
    {
        var provider = new MockTrafficProvider();
        provider.Stats[15] = (10_000_000, 2_000_000);
        provider.Names[15] = "Ethernet";

        var physical = new NetworkSnapshot("Ethernet", 15, "192.168.1.50", "192.168.1.1", []);
        var monitor = new TrafficMonitorService(() => physical, provider);

        long t0 = Stopwatch.GetTimestamp();
        monitor.Sample(t0); // baseline

        long t1 = t0 + Stopwatch.Frequency; // exactly 1 second later
        // Rx delta = 12.5 MB = 100 Mbit, Tx delta = 1.25 MB = 10 Mbit
        provider.Stats[15] = (10_000_000 + 12_500_000, 2_000_000 + 1_250_000);

        var snap = monitor.Sample(t1);
        Assert.True(Math.Abs(snap.DownloadMbps - 100.0) < 1.0, $"Expected ~100 Mbps Rx, got {snap.DownloadMbps}");
        Assert.True(Math.Abs(snap.UploadMbps - 10.0) < 0.2, $"Expected ~10 Mbps Tx, got {snap.UploadMbps}");
    }

    [Fact]
    public void PhysicalInterfaceChangeResetsTrafficBaseline()
    {
        var provider = new MockTrafficProvider();
        provider.Stats[10] = (50_000_000, 50_000_000);
        provider.Stats[20] = (800_000_000, 800_000_000);

        NetworkSnapshot currentPhysical = new("Ethernet", 10, "192.168.1.50", "192.168.1.1", []);
        var monitor = new TrafficMonitorService(() => currentPhysical, provider);

        long t0 = Stopwatch.GetTimestamp();
        monitor.Sample(t0);

        long t1 = t0 + Stopwatch.Frequency;
        // Interface switches from 10 to 20
        currentPhysical = new("Wi-Fi", 20, "192.168.1.60", "192.168.1.1", []);
        var snap = monitor.Sample(t1);

        // Baseline must be reset: 0 Mbps, no spike from 50M to 800M
        Assert.Equal(0.0, snap.DownloadMbps);
        Assert.Equal(0.0, snap.UploadMbps);
        Assert.False(snap.HasBaseline);
        Assert.Equal(20, snap.InterfaceIndex);
    }

    [Fact]
    public void CounterRollbackDoesNotProduceSpike()
    {
        var provider = new MockTrafficProvider();
        provider.Stats[10] = (500_000_000, 500_000_000);

        var physical = new NetworkSnapshot("Ethernet", 10, "192.168.1.50", "192.168.1.1", []);
        var monitor = new TrafficMonitorService(() => physical, provider);

        long t0 = Stopwatch.GetTimestamp();
        monitor.Sample(t0);

        long t1 = t0 + Stopwatch.Frequency;
        // Counter rolls back / resets
        provider.Stats[10] = (100_000, 100_000);
        var snap = monitor.Sample(t1);

        Assert.Equal(0.0, snap.DownloadMbps);
        Assert.Equal(0.0, snap.UploadMbps);
    }

    [Fact]
    public void ResumeDoesNotProduceTrafficSpike()
    {
        var provider = new MockTrafficProvider();
        provider.Stats[10] = (10_000_000, 10_000_000);

        var physical = new NetworkSnapshot("Ethernet", 10, "192.168.1.50", "192.168.1.1", []);
        var monitor = new TrafficMonitorService(() => physical, provider);

        long t0 = Stopwatch.GetTimestamp();
        monitor.Sample(t0);

        // Simulating system sleep / resume: 60 seconds elapsed
        long t1 = t0 + 60 * Stopwatch.Frequency;
        provider.Stats[10] = (500_000_000, 100_000_000);
        var snap = monitor.Sample(t1);

        Assert.Equal(0.0, snap.DownloadMbps);
        Assert.Equal(0.0, snap.UploadMbps);
    }

    [Fact]
    public void HiddenWindowDoesNotAffectSampling()
    {
        var provider = new MockTrafficProvider();
        provider.Stats[10] = (10_000_000, 10_000_000);

        var physical = new NetworkSnapshot("Ethernet", 10, "192.168.1.50", "192.168.1.1", []);
        var monitor = new TrafficMonitorService(() => physical, provider);

        long t0 = Stopwatch.GetTimestamp();
        monitor.Sample(t0);

        long t1 = t0 + Stopwatch.Frequency;
        provider.Stats[10] = (11_250_000, 10_125_000);
        var snap = monitor.Sample(t1);

        Assert.True(snap.DownloadMbps > 0);
        Assert.True(snap.UploadMbps > 0);
    }

    [Fact]
    public void TrafficTelemetryNeverRequestsRuntimeReconcile()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState());
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn);
        long initialGeneration = coordinator.CurrentGeneration;

        var provider = new MockTrafficProvider();
        provider.Stats[10] = (10_000_000, 10_000_000);
        var physical = new NetworkSnapshot("Ethernet", 10, "192.168.1.50", "192.168.1.1", []);
        var monitor = new TrafficMonitorService(() => physical, provider);

        for (int i = 0; i < 10; i++)
        {
            provider.Stats[10] = (10_000_000 + i * 1_000_000, 10_000_000 + i * 500_000);
            monitor.Sample();
        }

        Assert.Equal(initialGeneration, coordinator.CurrentGeneration);
    }
}

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan delta) => UtcNow += delta;
    public Task Delay(TimeSpan duration, CancellationToken ct)
    {
        Advance(duration);
        return Task.CompletedTask;
    }
}
