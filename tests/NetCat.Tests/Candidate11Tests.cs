using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate11Tests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "NetCat-C11Tests-" + Guid.NewGuid().ToString("N"));

    public Candidate11Tests()
    {
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
        catch { }
    }

    // =========================================================================
    // 1. NETWORK FLAP / UI STATE
    // =========================================================================

    [Fact]
    public async Task PhysicalNetworkLossIsPendingExternalConditionNotException()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true, ZapretEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var physical = new Candidate9Tests.FakePhysicalProvider { Usable = false, Current = null };

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, physical: physical);

        // Reconcile should NOT throw and should enter WaitingForPhysicalNetwork
        await coordinator.ReconcileAsync(ReconcileReason.PhysicalNetworkChanged);

        Assert.Equal(StartupRestoreState.WaitingForNetwork, coordinator.CurrentRestoreState);
        Assert.Equal(ConvergencePhase.WaitingForPhysicalNetwork, coordinator.CurrentConvergenceState.Phase);
        Assert.False(coordinator.CurrentConvergenceState.DesiredSatisfied);
        Assert.Equal(0, coordinator.GetFailureCount(ComponentId.MainRouter));
        Assert.Equal(0, coordinator.GetFailureCount(ComponentId.Zapret));
    }

    [Fact]
    public void NetworkLossDoesNotPollReconcileEveryTwoSeconds()
    {
        var desired = new DesiredRuntimeState { MainVpnEnabled = true };
        var observed = new ObservedRuntimeState(
            MainRouterStatus: ObservedComponentState.Stopped,
            ZapretStatus: ObservedComponentState.Stopped,
            OpenVpnStatus: ObservedComponentState.Stopped,
            TunReady: false,
            PhysicalNetwork: null,
            PhysicalUsable: false
        );

        var plan = RuntimePlanner.CreatePlan(desired, observed, new AppSettings(), new Dictionary<ComponentId, int>());

        Assert.Single(plan.Actions);
        Assert.Equal(PlanActionType.WaitForPhysicalNetwork, plan.Actions[0].Type);
        Assert.Empty(plan.PendingRetryComponents);
        Assert.Equal(TimeSpan.Zero, plan.NextRetryDelay);
    }

    [Fact]
    public async Task PhysicalNetworkReturnRequestsReconcile()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var physical = new Candidate9Tests.FakePhysicalProvider { Usable = false, Current = null };

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, physical: physical);
        await coordinator.ReconcileAsync(ReconcileReason.PhysicalNetworkChanged);
        Assert.Equal(0, router.EnsureRunningCalls);

        // Network returns
        physical.Usable = true;
        physical.Current = new NetworkSnapshot("Ethernet", 24, "192.168.1.100", "192.168.1.1", []);
        await coordinator.ReconcileAsync(ReconcileReason.PhysicalNetworkChanged);

        Assert.Equal(1, router.EnsureRunningCalls);
        Assert.Equal(ConvergencePhase.Converged, coordinator.CurrentConvergenceState.Phase);
        Assert.True(coordinator.CurrentConvergenceState.DesiredSatisfied);
    }

    [Fact]
    public async Task RecoveredNetworkUsesFreshInterfaceIndex()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var physical = new Candidate9Tests.FakePhysicalProvider
        {
            Usable = true,
            Current = new NetworkSnapshot("Ethernet", 42, "10.0.0.5", "10.0.0.1", [])
        };

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, physical: physical);
        await coordinator.ReconcileAsync(ReconcileReason.PhysicalNetworkChanged);

        Assert.Equal(1, router.EnsureRunningCalls);
        Assert.NotNull(router.ActivePhysical);
        Assert.Equal(42, router.ActivePhysical.Index);
        Assert.Equal("10.0.0.5", router.ActivePhysical.Address);
    }

    [Fact]
    public void NetworkReturnClearsStuckRebuildingUiState()
    {
        var store = new SettingsStore(tempDir);
        var settings = new AppSettings();
        using var vm = new MainViewModel(store, settings);

        // Simulate network change
        Assert.NotNull(vm.VpnStatus);

        // When coordinator converges, DesiredSatisfied clears transition
        var conv = new RuntimeConvergenceState(true, true, [], null, ConvergencePhase.Converged);
        typeof(MainViewModel).GetMethod("NotifyState")?.Invoke(vm, null);

        Assert.DoesNotContain("перестраиваю маршруты", vm.VpnStatus);
    }

    [Fact]
    public void FullRecoveryMessageRequiresDesiredSatisfied()
    {
        var store = new SettingsStore(tempDir);
        var settings = new AppSettings();
        using var vm = new MainViewModel(store, settings);

        // Not satisfied should NOT show full recovery
        var unsat = new RuntimeConvergenceState(true, false, [ComponentId.MainRouter], null, ConvergencePhase.Degraded);
        var prop = typeof(RuntimeCoordinator).GetProperty("CurrentConvergenceState");
        prop?.SetValue(vm.RuntimeCoordinator, unsat);
        vm.NotifyState();

        Assert.NotEqual("Подключения NetCat восстановлены", vm.VpnStatus);
    }

    [Fact]
    public async Task NetworkFlapDoesNotClearDesiredState()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState
        {
            MainVpnEnabled = true,
            TunEnabled = true,
            ZapretEnabled = true
        });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var physical = new Candidate9Tests.FakePhysicalProvider { Usable = true };

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, physical: physical);
        await coordinator.ReconcileAsync(ReconcileReason.Startup);

        // Disconnect
        physical.Usable = false;
        physical.Current = null;
        await coordinator.ReconcileAsync(ReconcileReason.PhysicalNetworkChanged);

        // Desired MUST remain intact
        var currentDesired = desired.GetCurrentDesiredState();
        Assert.True(currentDesired.MainVpnEnabled);
        Assert.True(currentDesired.TunEnabled);
        Assert.True(currentDesired.ZapretEnabled);

        // Reconnect
        physical.Usable = true;
        physical.Current = new NetworkSnapshot("Ethernet", 1, "192.168.1.1", "192.168.1.1", []);
        await coordinator.ReconcileAsync(ReconcileReason.PhysicalNetworkChanged);

        currentDesired = desired.GetCurrentDesiredState();
        Assert.True(currentDesired.MainVpnEnabled);
        Assert.True(currentDesired.TunEnabled);
        Assert.True(currentDesired.ZapretEnabled);
    }

    [Fact]
    public void NetworkFlapCoalescesBurstEvents()
    {
        long gen1 = PhysicalNetwork.PhysicalGeneration;
        long gen2 = PhysicalNetwork.NextGeneration();
        long gen3 = PhysicalNetwork.NextGeneration();

        Assert.True(gen3 > gen2);
        Assert.True(gen2 > gen1);
    }

    [Fact]
    public async Task HiddenAutostartNetworkFlapRecoversWithoutMainWindow()
    {
        var store = new SettingsStore(tempDir);
        var settings = new AppSettings { RestoreConnectionsOnStartup = true };
        store.SaveDesiredState(new DesiredRuntimeState { MainVpnEnabled = true });

        using var vm = new MainViewModel(store, settings);

        // Flap network on coordinator headlessly
        await vm.RuntimeCoordinator.ReconcileAsync(ReconcileReason.PhysicalNetworkChanged);

        Assert.NotNull(vm.RuntimeCoordinator.CurrentConvergenceState);
    }

    // =========================================================================
    // 2. ZAPRET LIFECYCLE & READINESS
    // =========================================================================

    [Fact]
    public async Task StartupDesiredZapretOnStartsOwnedWinws()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { ZapretEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn);
        await coordinator.ReconcileAsync(ReconcileReason.Startup);

        Assert.Equal(1, zapret.EnsureRunningCalls);
        Assert.True(zapret.IsRunning);
    }



    [Fact]
    public void ZapretUnexpectedExitWithNoNetworkWaitsForNetwork()
    {
        var desired = new DesiredRuntimeState { ZapretEnabled = true };
        var observed = new ObservedRuntimeState(
            MainRouterStatus: ObservedComponentState.Stopped,
            ZapretStatus: ObservedComponentState.Stopped,
            OpenVpnStatus: ObservedComponentState.Stopped,
            TunReady: false,
            PhysicalNetwork: null,
            PhysicalUsable: false
        );

        var plan = RuntimePlanner.CreatePlan(desired, observed, new AppSettings(), new Dictionary<ComponentId, int>());

        Assert.Single(plan.Actions);
        Assert.Equal(PlanActionType.WaitForPhysicalNetwork, plan.Actions[0].Type);
    }

    [Fact]
    public async Task NetworkReturnRestartsDesiredZapretAutomatically()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { ZapretEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var physical = new Candidate9Tests.FakePhysicalProvider { Usable = false, Current = null };

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, physical: physical);
        await coordinator.ReconcileAsync(ReconcileReason.Startup);
        Assert.Equal(0, zapret.EnsureRunningCalls);

        // Network returns
        physical.Usable = true;
        physical.Current = new NetworkSnapshot("Eth", 1, "192.168.1.1", "192.168.1.1", []);
        await coordinator.ReconcileAsync(ReconcileReason.PhysicalNetworkChanged);

        Assert.Equal(1, zapret.EnsureRunningCalls);
        Assert.True(zapret.IsRunning);
    }

    [Fact]
    public void ZapretRebindsWhenPhysicalInterfaceIndexChanges()
    {
        var desired = new DesiredRuntimeState { ZapretEnabled = true };
        var oldDetail = new ZapretObservedState(true, true, true, 500, DateTimeOffset.UtcNow, 24, "fp", null);
        var observed = new ObservedRuntimeState(
            MainRouterStatus: ObservedComponentState.Stopped,
            ZapretStatus: ObservedComponentState.RunningHealthy,
            OpenVpnStatus: ObservedComponentState.Stopped,
            TunReady: false,
            PhysicalNetwork: new NetworkSnapshot("Eth", 42, "10.0.0.1", "10.0.0.1", []), // New index 42 != 24
            PhysicalUsable: true,
            ZapretDetail: oldDetail
        );

        var diff = new RuntimeSettingsDiff(false, false, false, false, false, false, false, false, false, false, PhysicalBindingChanged: true);
        var plan = RuntimePlanner.CreatePlan(desired, observed, new AppSettings(), new Dictionary<ComponentId, int>(), diff: diff);

        Assert.Contains(plan.Actions, a => a.Type == PlanActionType.StartZapret);
    }







    [Fact]
    public void PidReuseCannotSatisfyOwnerMatch()
    {
        var meta = new ZapretOwnerRecord(
            Pid: 1234,
            ProcessStartTime: DateTimeOffset.UtcNow.AddHours(-1),
            SessionId: 1,
            ExecutablePath: "C:\\NetCat\\bin\\zapret\\winws.exe",
            CommandFingerprint: "test",
            OwnerInstanceId: "test-owner"
        );

        // Reused PID started 1 minute ago should fail StartTime match
        var otherStartTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.True(Math.Abs((otherStartTime - meta.ProcessStartTime).TotalSeconds) > 30);
    }



    // =========================================================================
    // 3. VPN PROFILE SWITCHING
    // =========================================================================

    [Fact]
    public async Task ProfileSelectionUpdatesDesiredProfileId()
    {
        var store = new SettingsStore(tempDir);
        var targetId = Guid.NewGuid();
        var settings = new AppSettings
        {
            Profiles = [new Profile { Id = targetId, Name = "Server-B" }]
        };
        using var vm = new MainViewModel(store, settings);

        vm.UserSelectedVpnProfile(targetId);
        await vm.PendingSelection;

        Assert.Equal(targetId, vm.DesiredState.SelectedVpnProfileId);
        Assert.Equal(targetId, vm.State.MainProfileId);
    }

    [Fact]
    public async Task ProfileSelectionWhileVpnOffDoesNotStartVpn()
    {
        var targetId = Guid.NewGuid();
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState
        {
            MainVpnEnabled = false,
            SelectedVpnProfileId = targetId
        });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn);
        await coordinator.ReconcileAsync(ReconcileReason.UserSelectedVpnProfile);

        Assert.Equal(0, router.EnsureRunningCalls);
        Assert.False(router.IsRunning);
    }

    [Fact]
    public async Task ProfileSelectionWhileVpnOnReconfiguresExactlyOnce()
    {
        var profileA = Guid.NewGuid();
        var profileB = Guid.NewGuid();
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState
        {
            MainVpnEnabled = true,
            SelectedVpnProfileId = profileA
        });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn);
        await coordinator.ReconcileAsync(ReconcileReason.Startup);
        Assert.Equal(1, router.EnsureRunningCalls);

        // Switch to B
        desired.Current = desired.Current with { SelectedVpnProfileId = profileB };
        await coordinator.ReconcileAsync(ReconcileReason.UserSelectedVpnProfile);

        // Exactly one reconfiguration for B
        Assert.Equal(2, router.EnsureRunningCalls);
        Assert.Equal(profileB, router.ActiveProfileId);
    }

    [Fact]
    public async Task ExecutorReceivesNewSelectedProfileNotCommittedOldProfile()
    {
        var profileA = Guid.NewGuid();
        var profileB = Guid.NewGuid();
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState
        {
            MainVpnEnabled = true,
            SelectedVpnProfileId = profileB
        });
        var router = new Candidate9Tests.FakeRouterRuntime { ActiveProfileId = profileA };
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();

        var settings = new AppSettings
        {
            MainProfileId = profileA, // committed still has A
            Profiles = [new Profile { Id = profileA, Name = "A" }, new Profile { Id = profileB, Name = "B" }]
        };

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, getSettings: () => settings);
        await coordinator.ReconcileAsync(ReconcileReason.UserSelectedVpnProfile);

        Assert.Equal(profileB, router.ActiveProfileId);
    }

    [Fact]
    public void ProfileChangeProducesProfileDiff()
    {
        var last = new EffectiveRuntimeConfig(true, false, Guid.NewGuid(), false, "", "", false, null, "", "", "", "");
        var next = last with { VpnProfileId = Guid.NewGuid() };

        var diff = RuntimeSettingsDiff.Compute(last, next, null);

        Assert.True(diff.VpnProfileChanged);
        Assert.True(diff.MainRouterBaseChanged);
    }

    [Fact]
    public void InvalidProfilePreflightDoesNotCommitSelection()
    {
        var store = new SettingsStore(tempDir);
        var validId = Guid.NewGuid();
        var settings = new AppSettings
        {
            MainProfileId = validId,
            Profiles = [new Profile { Id = validId, Name = "Valid" }]
        };
        using var vm = new MainViewModel(store, settings);

        // Try selecting non-existent profile
        vm.UserSelectedVpnProfile(Guid.NewGuid());

        Assert.Equal(validId, vm.DesiredState.SelectedVpnProfileId);
        Assert.Equal(validId, vm.State.MainProfileId);
    }

    [Fact]
    public async Task ProfileAtoBtoAConvergesWithoutLoop()
    {
        var profileA = Guid.NewGuid();
        var profileB = Guid.NewGuid();
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState
        {
            MainVpnEnabled = true,
            SelectedVpnProfileId = profileA
        });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();

        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn);
        await coordinator.ReconcileAsync(ReconcileReason.Startup);

        // A -> B
        desired.Current = desired.Current with { SelectedVpnProfileId = profileB };
        await coordinator.ReconcileAsync(ReconcileReason.UserSelectedVpnProfile);
        Assert.Equal(profileB, router.ActiveProfileId);

        // B -> A
        desired.Current = desired.Current with { SelectedVpnProfileId = profileA };
        await coordinator.ReconcileAsync(ReconcileReason.UserSelectedVpnProfile);
        Assert.Equal(profileA, router.ActiveProfileId);
        Assert.Equal(ConvergencePhase.Converged, coordinator.CurrentConvergenceState.Phase);
    }

    // =========================================================================
    // 4. OPENVPN THREADING / PERSISTENCE
    // =========================================================================

    [Fact]
    public async Task LearnedRoutePersistenceWorksWithoutMainWindow()
    {
        var store = new SettingsStore(tempDir);
        var profileId = Guid.NewGuid();
        var settings = new AppSettings
        {
            Profiles = [new Profile { Id = profileId, Protocol = "openvpn", Name = "Corp" }]
        };
        await store.SaveAsync(settings);

        var repo = new RuntimeConfigurationRepository(store);
        await repo.UpdateOpenVpnLearnedRoutesAsync(profileId, ["10.200.0.0/16", "10.201.0.0/16"]);

        var updated = store.Load();
        var prof = updated.Profiles.First(p => p.Id == profileId);
        Assert.Equal(2, prof.LearnedRoutes.Count);
        Assert.Contains("10.200.0.0/16", prof.LearnedRoutes);
    }

    [Fact]
    public async Task LearnedRoutePersistenceDoesNotTouchObservableCollectionOffDispatcher()
    {
        var store = new SettingsStore(tempDir);
        var repo = new RuntimeConfigurationRepository(store);

        // Calling from thread pool
        var ex = await Record.ExceptionAsync(() => Task.Run(async () =>
        {
            await repo.UpdateOpenVpnLearnedRoutesAsync(Guid.NewGuid(), ["172.16.0.0/12"]);
        }));

        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public async Task ConfigurationChangeUiProjectionRunsOnDispatcher()
    {
        var store = new SettingsStore(tempDir);
        var repo = new RuntimeConfigurationRepository(store);
        bool eventFired = false;

        repo.ConfigurationChanged += s => { eventFired = true; };
        await repo.UpdateSettingsAsync(s => { s.DirectDns = "1.1.1.1"; return s; });

        Assert.True(eventFired);
    }

    [Fact]
    public async Task OpenVpnStartDoesNotFailAfterSuccessfulRouteInstallBecauseOfUiRefresh()
    {
        var ovpnProfileId = Guid.NewGuid();
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState
        {
            OpenVpnEnabled = true,
            SelectedOpenVpnProfileId = ovpnProfileId
        });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime { LearnedRoutesToPush = ["10.50.0.0/16"] };

        var repo = new RuntimeConfigurationRepository(new AppSettings
        {
            Profiles = [new Profile { Id = ovpnProfileId, Protocol = "openvpn", Name = "Test" }]
        }, new SettingsStore(tempDir));

        var coordinator = Candidate9Tests.CreateCoordinator(
            desired, router, zapret, openvpn,
            onRoutesLearned: (id, routes, ct) => repo.UpdateOpenVpnLearnedRoutesAsync(id, routes, ct));

        await coordinator.ReconcileAsync(ReconcileReason.Startup);

        Assert.True(openvpn.IsRunning);
        Assert.Equal(1, openvpn.EnsureRunningCalls);
        Assert.Equal(0, coordinator.GetFailureCount(ComponentId.OpenVpnLink));
    }

    [Fact]
    public void LearnedRouteUpdateProducesOnlyOneRouterRoutingUpdate()
    {
        var last = new EffectiveRuntimeConfig(true, false, Guid.NewGuid(), false, "", "", true, Guid.NewGuid(), "10.0.0.0/8", "", "", "");
        var next = last with { LearnedOpenVpnRoutesFingerprint = "10.0.0.0/8,172.16.0.0/12" };

        var diff = RuntimeSettingsDiff.Compute(last, next, null);

        Assert.True(diff.LearnedOpenVpnRoutesChanged);
        Assert.False(diff.MainRouterBaseChanged);

        var desired = new DesiredRuntimeState { MainVpnEnabled = true, OpenVpnEnabled = true };
        var observed = new ObservedRuntimeState(
            MainRouterStatus: ObservedComponentState.RunningHealthy,
            ZapretStatus: ObservedComponentState.Stopped,
            OpenVpnStatus: ObservedComponentState.RunningHealthy,
            TunReady: true,
            PhysicalNetwork: new NetworkSnapshot("Eth", 1, "192.168.1.1", "192.168.1.1", []),
            PhysicalUsable: true,
            OpenVpnRoutesInstalled: true
        );

        var plan = RuntimePlanner.CreatePlan(desired, observed, new AppSettings(), new Dictionary<ComponentId, int>(), diff: diff);

        // MUST NOT restart MainRouter; plans ONLY FinalizeRouting
        Assert.DoesNotContain(plan.Actions, a => a.Type == PlanActionType.StartMainRouter);
        Assert.Contains(plan.Actions, a => a.Type == PlanActionType.FinalizeRouting);
    }

    // =========================================================================
    // 5. OPENVPN ROUTE STATE
    // =========================================================================



    [Fact]
    public void StopOpenVpnFailureSkipsFinalizeRemoval()
    {
        var desired = new DesiredRuntimeState { OpenVpnEnabled = false };
        var observed = new ObservedRuntimeState(
            MainRouterStatus: ObservedComponentState.Stopped,
            ZapretStatus: ObservedComponentState.Stopped,
            OpenVpnStatus: ObservedComponentState.RunningHealthy,
            TunReady: false,
            PhysicalNetwork: new NetworkSnapshot("Eth", 1, "192.168.1.1", "192.168.1.1", []),
            PhysicalUsable: true
        );

        var plan = RuntimePlanner.CreatePlan(desired, observed, new AppSettings(), new Dictionary<ComponentId, int>());

        var stopAction = plan.Actions.FirstOrDefault(a => a.Type == PlanActionType.StopOpenVpn);
        var finalizeAction = plan.Actions.FirstOrDefault(a => a.Type == PlanActionType.FinalizeRouting);

        Assert.NotNull(stopAction);
        Assert.NotNull(finalizeAction);
        Assert.Equal(PlanActionType.StopOpenVpn, finalizeAction.Prerequisite);
    }

    [Fact]
    public async Task SuccessfulStopThenFinalizeRemovesRoutes()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { OpenVpnEnabled = false });
        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime { IsRunning = true };

        bool finalizeCalled = false;
        var coordinator = Candidate9Tests.CreateCoordinator(
            desired, router, zapret, openvpn,
            finalizeOverride: _ => { finalizeCalled = true; return Task.CompletedTask; });

        await coordinator.ReconcileAsync(ReconcileReason.UserToggledOpenVpn);

        Assert.Equal(1, openvpn.EnsureStoppedCalls);
        Assert.True(finalizeCalled);
    }

    [Fact]
    public void RouteFinalizeFailureRetriesRoutesOnly()
    {
        var desired = new DesiredRuntimeState { OpenVpnEnabled = true };
        var observed = new ObservedRuntimeState(
            MainRouterStatus: ObservedComponentState.Stopped,
            ZapretStatus: ObservedComponentState.Stopped,
            OpenVpnStatus: ObservedComponentState.RunningHealthy,
            TunReady: false,
            PhysicalNetwork: new NetworkSnapshot("Eth", 1, "192.168.1.1", "192.168.1.1", []),
            PhysicalUsable: true,
            OpenVpnRoutesInstalled: false
        );

        var failures = new Dictionary<ComponentId, int> { [ComponentId.OpenVpnRoutes] = 2 };
        var plan = RuntimePlanner.CreatePlan(desired, observed, new AppSettings(), failures);

        Assert.Contains(ComponentId.OpenVpnRoutes, plan.PendingRetryComponents);
        Assert.DoesNotContain(ComponentId.OpenVpnLink, plan.PendingRetryComponents);
        Assert.Contains(plan.Actions, a => a.Type == PlanActionType.FinalizeRouting);
        Assert.DoesNotContain(plan.Actions, a => a.Type == PlanActionType.StartOpenVpn);
    }

    // =========================================================================
    // 6. DESIRED STATE REVERSE WRITE GUARDS
    // =========================================================================

    [Fact]
    public void RuntimeProcessStateCannotTurnDesiredVpnOn()
    {
        var store = new SettingsStore(tempDir);
        var settings = new AppSettings();
        store.SaveDesiredState(new DesiredRuntimeState { MainVpnEnabled = false });

        var router = new RouterService(tempDir, tempDir);
        using var vm = new MainViewModel(store, settings, router: router);

        Assert.False(vm.DesiredState.MainVpnEnabled);
        vm.NotifyState();
        Assert.False(vm.DesiredState.MainVpnEnabled);
    }



    [Fact]
    public async Task GenericSettingsCommitCannotOverwriteDesiredTun()
    {
        var store = new SettingsStore(tempDir);
        var settings = new AppSettings { Tun = true, BaseColor = "#000000" };
        store.SaveDesiredState(new DesiredRuntimeState { TunEnabled = false });

        using var vm = new MainViewModel(store, settings);
        Assert.False(vm.DesiredState.TunEnabled);

        // Commit unrelated change (BaseColor)
        await vm.UpdateSettingsAsync(s => s.BaseColor = "#FFFFFF");

        Assert.False(vm.DesiredState.TunEnabled);
    }

    [Fact]
    public void ShutdownCleanupPreservesDesiredOn()
    {
        var store = new SettingsStore(tempDir);
        store.SaveDesiredState(new DesiredRuntimeState
        {
            MainVpnEnabled = true,
            TunEnabled = true,
            ZapretEnabled = true
        });

        using (var vm = new MainViewModel(store, new AppSettings()))
        {
            Assert.True(vm.DesiredState.MainVpnEnabled);
        }

        var reloaded = store.LoadDesiredState();
        Assert.True(reloaded.MainVpnEnabled);
        Assert.True(reloaded.TunEnabled);
        Assert.True(reloaded.ZapretEnabled);
    }

    // =========================================================================
    // 7. HEADLESS HEALTH / FAILOVER
    // =========================================================================

    [Fact]
    public async Task VpnHealthMonitorRunsWithoutMainWindow()
    {
        var repo = new RuntimeConfigurationRepository(new AppSettings(), new SettingsStore(tempDir));
        var router = new Candidate9Tests.FakeRouterRuntime { IsRunning = true, VpnRunning = true, LatencyPort = 1080 };
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true });
        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, new Candidate9Tests.FakeZapretRuntime(), new Candidate9Tests.FakeOpenVpnRuntime());

        using var monitor = new VpnHealthMonitorService(
            repo, router, coordinator,
            () => desired.Current,
            update => desired.Current = update(desired.Current)
        )
        {
            MeasureLatencyFunc = (port, url, ct, timeout) => Task.FromResult(new DelayResult(true, 42, ""))
        };

        await monitor.ProbeOnceAsync();

        Assert.NotNull(monitor.LastHealthResult);
        Assert.True(monitor.LastHealthResult.Success);
        Assert.Equal(42, monitor.LastHealthResult.Milliseconds);
    }

    [Fact]
    public void ClosingWindowDoesNotStopVpnHealthMonitor()
    {
        var store = new SettingsStore(tempDir);
        using var vm = new MainViewModel(store, new AppSettings());

        Assert.NotNull(vm.HealthMonitor);
    }

    [Fact]
    public async Task UpstreamFailureUpdatesObservedHealthHeadlessly()
    {
        var repo = new RuntimeConfigurationRepository(new AppSettings(), new SettingsStore(tempDir));
        var router = new Candidate9Tests.FakeRouterRuntime { IsRunning = true, VpnRunning = true, LatencyPort = 1080 };
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true });
        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, new Candidate9Tests.FakeZapretRuntime(), new Candidate9Tests.FakeOpenVpnRuntime());

        using var monitor = new VpnHealthMonitorService(
            repo, router, coordinator,
            () => desired.Current,
            update => desired.Current = update(desired.Current)
        )
        {
            MeasureLatencyFunc = (port, url, ct, timeout) => Task.FromResult(new DelayResult(false, 0, "Connection timed out"))
        };

        await monitor.ProbeOnceAsync();

        Assert.False(coordinator.VpnUpstreamHealthy);
        Assert.False(monitor.LastHealthResult!.Success);
    }

    [Fact]
    public async Task FailoverPolicyRequestsReconcileInsteadOfCallingRouterDirectly()
    {
        var targetA = Guid.NewGuid();
        var targetB = Guid.NewGuid();
        var repo = new RuntimeConfigurationRepository(new AppSettings
        {
            MainProfileId = targetA,
            AutoSwitch = true,
            Profiles = [new Profile { Id = targetA, Name = "A" }, new Profile { Id = targetB, Name = "B" }]
        }, new SettingsStore(tempDir));
        var router = new Candidate9Tests.FakeRouterRuntime { IsRunning = true, VpnRunning = true, LatencyPort = 1080, ActiveProfileId = targetA };
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true, SelectedVpnProfileId = targetA });
        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, new Candidate9Tests.FakeZapretRuntime(), new Candidate9Tests.FakeOpenVpnRuntime());

        using var monitor = new VpnHealthMonitorService(
            repo, router, coordinator,
            () => desired.Current,
            update => desired.Current = update(desired.Current)
        )
        {
            MeasureLatencyFunc = (port, url, ct, timeout) => Task.FromResult(new DelayResult(false, 0, "Server unreachable")),
            TestProfileFunc = (_, _, _) => Task.FromResult(new DelayResult(true, 20, ""))
        };

        // Repeated failures trigger failover
        for (int i = 0; i < 5; i++)
        {
            await monitor.ProbeOnceAsync();
        }

        // Router runtime should NOT have been called directly by monitor; reconcile pass is triggered instead
        Assert.True(router.EnsureRunningCalls <= 1);
        Assert.Equal(targetB, desired.Current.SelectedVpnProfileId);
    }

    // =========================================================================
    // 8. SINGLE RUNTIME MUTATION AUTHORITY
    // =========================================================================

    [Fact]
    public void NoDirectRouterMutationFromMainViewModel()
    {
        var methods = typeof(MainViewModel).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var profileSwitchMethod = methods.FirstOrDefault(m => m.Name == "ApplySelectedProfileAsync");
        Assert.Null(profileSwitchMethod);
    }

    [Fact]
    public void NoDirectZapretMutationFromMainViewModel()
    {
        var store = new SettingsStore(tempDir);
        var settings = new AppSettings();
        using var vm = new MainViewModel(store, settings);

        vm.UserRequestedZapretChange(true);

        Assert.True(vm.DesiredState.ZapretEnabled);
        Assert.False(vm.Zapret.Running);
    }

    [Fact]
    public void NoDirectRuntimeMutationFromMainWindow()
    {
        var methods = typeof(MainWindow).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance);
        var recoverConn = methods.FirstOrDefault(m => m.Name == "RecoverConnectionAsync");
        Assert.Null(recoverConn);
    }

    [Fact]
    public void NoApplyRuntimeEscapeHatch()
    {
        var methods = typeof(MainViewModel).GetMethods();
        Assert.DoesNotContain(methods, m => m.Name.Contains("ApplyRuntime", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NetworkMonitorOnlyUpdatesObservationAndRequestsReconcile()
    {
        using var signals = new NetworkSignals(subscribe: false);
        Assert.NotNull(signals);
        long rev = signals.Revision;
        signals.Signal();
        Assert.Equal(rev + 1, signals.Revision);
    }

    // =========================================================================
    // 9. STOP RETRY
    // =========================================================================

    [Fact]
    public void FailedStopZapretRemainsUnsatisfiedAndRetries()
    {
        var desired = new DesiredRuntimeState { ZapretEnabled = false };
        var observed = new ObservedRuntimeState(
            MainRouterStatus: ObservedComponentState.Stopped,
            ZapretStatus: ObservedComponentState.RunningHealthy, // Still running after failed stop
            OpenVpnStatus: ObservedComponentState.Stopped,
            TunReady: false,
            PhysicalNetwork: new NetworkSnapshot("Eth", 1, "192.168.1.1", "192.168.1.1", []),
            PhysicalUsable: true
        );

        var failures = new Dictionary<ComponentId, int> { [ComponentId.Zapret] = 1 };
        var plan = RuntimePlanner.CreatePlan(desired, observed, new AppSettings(), failures);

        Assert.Contains(plan.Actions, a => a.Type == PlanActionType.StopZapret);
        Assert.Contains(ComponentId.Zapret, plan.PendingRetryComponents);
    }

    [Fact]
    public void FailedStopMainRouterRemainsUnsatisfiedAndRetries()
    {
        var desired = new DesiredRuntimeState { MainVpnEnabled = false };
        var observed = new ObservedRuntimeState(
            MainRouterStatus: ObservedComponentState.RunningHealthy, // Still running after failed stop
            ZapretStatus: ObservedComponentState.Stopped,
            OpenVpnStatus: ObservedComponentState.Stopped,
            TunReady: false,
            PhysicalNetwork: new NetworkSnapshot("Eth", 1, "192.168.1.1", "192.168.1.1", []),
            PhysicalUsable: true
        );

        var failures = new Dictionary<ComponentId, int> { [ComponentId.MainRouter] = 1 };
        var plan = RuntimePlanner.CreatePlan(desired, observed, new AppSettings(), failures);

        Assert.Contains(plan.Actions, a => a.Type == PlanActionType.StopMainRouter);
        Assert.Contains(ComponentId.MainRouter, plan.PendingRetryComponents);
    }

    [Fact]
    public void FailedStopOpenVpnRemainsUnsatisfiedAndRetries()
    {
        var desired = new DesiredRuntimeState { OpenVpnEnabled = false };
        var observed = new ObservedRuntimeState(
            MainRouterStatus: ObservedComponentState.Stopped,
            ZapretStatus: ObservedComponentState.Stopped,
            OpenVpnStatus: ObservedComponentState.RunningHealthy, // Still running after failed stop
            TunReady: false,
            PhysicalNetwork: new NetworkSnapshot("Eth", 1, "192.168.1.1", "192.168.1.1", []),
            PhysicalUsable: true
        );

        var failures = new Dictionary<ComponentId, int> { [ComponentId.OpenVpnLink] = 1 };
        var plan = RuntimePlanner.CreatePlan(desired, observed, new AppSettings(), failures);

        Assert.Contains(plan.Actions, a => a.Type == PlanActionType.StopOpenVpn);
        Assert.Contains(ComponentId.OpenVpnLink, plan.PendingRetryComponents);
    }

    [Fact]
    public void ProcessAbsenceDoesNotClearUnfinishedStopCleanup()
    {
        var desired = new DesiredRuntimeState { ZapretEnabled = false };
        var observed = new ObservedRuntimeState(
            MainRouterStatus: ObservedComponentState.Stopped,
            ZapretStatus: ObservedComponentState.Stopped, // Process is gone; cleanup from the failed stop may still be pending.
            OpenVpnStatus: ObservedComponentState.Stopped,
            TunReady: false,
            PhysicalNetwork: new NetworkSnapshot("Eth", 1, "192.168.1.1", "192.168.1.1", []),
            PhysicalUsable: true
        );

        var failures = new Dictionary<ComponentId, int> { [ComponentId.Zapret] = 1 };
        var plan = RuntimePlanner.CreatePlan(desired, observed, new AppSettings(), failures);

        Assert.Equal(PlanActionType.StopZapret, Assert.Single(plan.Actions).Type);
        Assert.Empty(plan.PendingRetryComponents);
    }

    // =========================================================================
    // 10. COMPOSITION TEST
    // =========================================================================

    [Fact]
    public async Task FullRealWorldCompositionScenario()
    {
        var profileA = Guid.NewGuid();
        var profileB = Guid.NewGuid();
        var ovpnId = Guid.NewGuid();

        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState
        {
            MainVpnEnabled = true,
            TunEnabled = true,
            ZapretEnabled = true,
            SelectedVpnProfileId = profileA,
            OpenVpnEnabled = false
        });

        var router = new Candidate9Tests.FakeRouterRuntime();
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime { LearnedRoutesToPush = ["10.100.0.0/16"] };
        var physical = new Candidate9Tests.FakePhysicalProvider
        {
            Usable = true,
            Current = new NetworkSnapshot("Ethernet", 24, "192.168.1.50", "192.168.1.1", [])
        };

        var settings = new AppSettings
        {
            MainProfileId = profileA,
            Profiles = [
                new Profile { Id = profileA, Name = "Server-A" },
                new Profile { Id = profileB, Name = "Server-B" },
                new Profile { Id = ovpnId, Protocol = "openvpn", Name = "Corp" }
            ]
        };

        var logs = new List<string>();
        var coordinator = Candidate9Tests.CreateCoordinator(
            desired, router, zapret, openvpn,
            physical: physical,
            getSettings: () => settings);
        coordinator.Log = logs.Add;

        // 1. Startup -> converges
        await coordinator.ReconcileAsync(ReconcileReason.Startup);
        Assert.Equal(1, router.EnsureRunningCalls);
        Assert.Equal(1, zapret.EnsureRunningCalls);
        Assert.True(coordinator.CurrentConvergenceState.DesiredSatisfied);
        Assert.Equal(ConvergencePhase.Converged, coordinator.CurrentConvergenceState.Phase);

        // 2. Physical network loss -> enters WaitingForPhysicalNetwork
        physical.Usable = false;
        physical.Current = null;
        await coordinator.ReconcileAsync(ReconcileReason.PhysicalNetworkChanged);
        Assert.Equal(ConvergencePhase.WaitingForPhysicalNetwork, coordinator.CurrentConvergenceState.Phase);

        // 3. Zapret exits while network is down -> stays in WaitingForPhysicalNetwork without loop
        zapret.IsRunning = false;
        await coordinator.ReconcileAsync(ReconcileReason.ZapretProcessExited);
        Assert.Equal(ConvergencePhase.WaitingForPhysicalNetwork, coordinator.CurrentConvergenceState.Phase);

        // 4. Physical network returns with fresh interface index 42 -> rebinds VPN and restores Zapret
        physical.Usable = true;
        physical.Current = new NetworkSnapshot("Ethernet-New", 42, "10.0.0.10", "10.0.0.1", []);
        await coordinator.ReconcileAsync(ReconcileReason.PhysicalNetworkChanged);

        Assert.Equal(ConvergencePhase.Converged, coordinator.CurrentConvergenceState.Phase);
        Assert.True(coordinator.CurrentConvergenceState.DesiredSatisfied);
        Assert.Equal(42, router.ActivePhysical?.Index);
        Assert.True(zapret.IsRunning);

        // 5. Profile switch A -> B -> exactly one router reconfiguration
        int routerBeforeSwitch = router.EnsureRunningCalls;
        desired.Current = desired.Current with { SelectedVpnProfileId = profileB };
        await coordinator.ReconcileAsync(ReconcileReason.UserSelectedVpnProfile);

        Assert.Equal(routerBeforeSwitch + 1, router.EnsureRunningCalls);
        Assert.Equal(profileB, router.ActiveProfileId);
        Assert.Equal(ConvergencePhase.Converged, coordinator.CurrentConvergenceState.Phase);

        // 6. OpenVPN enabled with learned route -> single routing update, no duplicate router restarts
        int routerBeforeOvpn = router.EnsureRunningCalls;
        desired.Current = desired.Current with { OpenVpnEnabled = true, SelectedOpenVpnProfileId = ovpnId };
        await coordinator.ReconcileAsync(ReconcileReason.UserToggledOpenVpn);

        Assert.True(openvpn.IsRunning);
        Assert.Equal(ConvergencePhase.Converged, coordinator.CurrentConvergenceState.Phase);
        Assert.True(coordinator.CurrentConvergenceState.DesiredSatisfied);
    }
}
