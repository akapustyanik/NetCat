using NetCat.Core;
using NetCat.Network;
using NetCat.UI;
using System.Collections.Concurrent;
using Xunit;

namespace NetCat.Tests;

// Candidate15 tests cover the four focused startup/TUN semantics fixes while
// retaining the Candidate14 production seams.
public sealed class Candidate15Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NetCat-C15Tests-" + Guid.NewGuid().ToString("N"));

    public Candidate15Tests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch { } }

    private string Source(string relative) => File.ReadAllText(Path.Combine(RoutingTests.FindRoot(), relative));

    private RuntimeCoordinator Coordinator(DesiredRuntimeState desired, Candidate9Tests.FakePhysicalProvider? physical = null)
        => Candidate9Tests.CreateCoordinator(new Candidate9Tests.FakeDesiredProvider(desired), new Candidate9Tests.FakeRouterRuntime(),
            new Candidate9Tests.FakeZapretRuntime(), new Candidate9Tests.FakeOpenVpnRuntime(), physical: physical);

    [Fact]
    public void InitialPhysicalStateIsUnknownBeforeFirstCapture()
    {
        using var coordinator = Coordinator(new DesiredRuntimeState());
        Assert.Equal(PhysicalNetworkAvailability.Unknown, coordinator.PhysicalNetworkState);
        Assert.Equal(PhysicalNetworkAvailability.Unknown, coordinator.CurrentConvergenceState.PhysicalNetworkState);
    }

    [Fact]
    public void UnknownPhysicalStateDoesNotShowNetworkUnavailable()
    {
        Assert.Equal("Определяю состояние сети…", MainViewModel.VpnStatusFor(PhysicalNetworkAvailability.Unknown,
            MainRouterLifecycle.Starting, null, true, true, false, false, false));
        var observed = new ObservedRuntimeState(ObservedComponentState.Stopped, ObservedComponentState.Stopped,
            ObservedComponentState.Stopped, false, null, false, PhysicalNetworkState: PhysicalNetworkAvailability.Unknown);
        var plan = RuntimePlanner.CreatePlan(new() { MainVpnEnabled = true }, observed, new(), new Dictionary<ComponentId, int>());
        Assert.DoesNotContain(plan.Actions, a => a.Type == PlanActionType.WaitForPhysicalNetwork);
    }

    [Fact]
    public async Task InitialCapturePublishesReadyWhenEthernetExists()
    {
        using var coordinator = Coordinator(new DesiredRuntimeState(), new Candidate9Tests.FakePhysicalProvider { Usable = true });
        await coordinator.CaptureInitialPhysicalNetworkAsync();
        Assert.Equal(PhysicalNetworkAvailability.Ready, coordinator.PhysicalNetworkState);
        Assert.True(coordinator.CurrentConvergenceState.PhysicalNetworkReady);
    }

    [Fact]
    public async Task InitialCapturePublishesUnavailableOnlyAfterAuthoritativeFailure()
    {
        using var coordinator = Coordinator(new DesiredRuntimeState(), new Candidate9Tests.FakePhysicalProvider { Current = null, Usable = false });
        await coordinator.CaptureInitialPhysicalNetworkAsync();
        Assert.Equal(PhysicalNetworkAvailability.Unavailable, coordinator.PhysicalNetworkState);
        Assert.False(coordinator.CurrentConvergenceState.PhysicalNetworkReady);
    }

    [Fact]
    public async Task PhysicalMonitorPerformsInitialCaptureWithoutMainWindow()
    {
        using var coordinator = Coordinator(new DesiredRuntimeState(), new Candidate9Tests.FakePhysicalProvider { Usable = true });
        var repository = new RuntimeConfigurationRepository(new AppSettings(), new SettingsStore(root));
        using var monitor = new PhysicalNetworkMonitor(coordinator, repository);
        monitor.Start();
        await monitor.InitialCapture.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(PhysicalNetworkAvailability.Ready, coordinator.PhysicalNetworkState);
    }

    [Fact]
    public async Task PhysicalMonitorPerformsInitialCaptureWhenVpnDesiredOff()
    {
        using var coordinator = Coordinator(new DesiredRuntimeState { MainVpnEnabled = false, TunEnabled = false }, new Candidate9Tests.FakePhysicalProvider { Usable = true });
        var repository = new RuntimeConfigurationRepository(new AppSettings(), new SettingsStore(root));
        using var monitor = new PhysicalNetworkMonitor(coordinator, repository);
        monitor.Start();
        await monitor.InitialCapture.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(PhysicalNetworkAvailability.Ready, coordinator.PhysicalNetworkState);
    }

    [Fact]
    public void DesiredVpnOnButRouterStartingIsNotStructuralFailure()
    {
        var state = new TunHealthTracker().Evaluate(false, false, false, false, 0, MainRouterLifecycle.Starting);
        Assert.Equal(TunStructuralStatus.Starting, state.StructuralStatus);
        Assert.False(state.StructuralStatus == TunStructuralStatus.StructuralFailure);
    }

    [Fact]
    public void RouterStartingWithNoProcessYetReportsStarting()
        => Assert.Equal(TunStructuralStatus.Starting, new TunHealthTracker().Evaluate(false, false, false, false, 0, MainRouterLifecycle.Starting).StructuralStatus);

    [Fact]
    public void RouterStartingWithProcessButNoInterfaceYetReportsStarting()
        => Assert.Equal(TunStructuralStatus.Starting, new TunHealthTracker().Evaluate(true, false, false, false, 10, MainRouterLifecycle.Starting).StructuralStatus);

    [Fact]
    public async Task WaitTunReadyStartupWindowDoesNotEmitTunStructuralFailure()
    {
        using var fixture = new Candidate12Tests.Fixture(new SettingsStore(root));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Router.BeforeStart = () => { entered.TrySetResult(); return release.Task; };
        var logs = new ConcurrentQueue<string>();
        fixture.Coordinator.Log = logs.Enqueue;
        var startup = fixture.Reconcile(ReconcileReason.Startup);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var monitor = new PhysicalNetworkMonitor(fixture.Coordinator, fixture.Repo);
            await monitor.ObserveTunnelOnceAsync();
            Assert.Equal(MainRouterLifecycle.Starting, fixture.Coordinator.MainRouterLifecycle);
            Assert.Equal(TunStructuralStatus.Starting, fixture.Coordinator.CurrentObservedState!.TunStatus);
            Assert.DoesNotContain(logs, l => l.Contains("emitted=TunStructuralFailure"));
        }
        finally { release.TrySetResult(); await startup; }
        Assert.Equal(TunStructuralStatus.Healthy, fixture.Coordinator.CurrentObservedState!.TunStatus);
        Assert.Equal(1, fixture.Router.Starts);
    }

    [Fact]
    public async Task WaitTunReadyWaitsForProcessThatAppearsAfterFirstSample()
    {
        int sample = 0;
        var health = await TunnelInspection.WaitForTunReadyAsync(
            () => sample > 1, () => 1080, true, MainRouterLifecycle.Starting,
            TimeSpan.FromSeconds(2), CancellationToken.None, pollInterval: TimeSpan.FromMilliseconds(1),
            inspectOverride: _ => Task.FromResult(++sample < 3
                ? new RouterHealth(false, false, false, false, 0) : new RouterHealth(true, true, true, true, 9)));
        Assert.Equal(3, sample);
        Assert.Equal(TunStructuralStatus.Healthy, health.StructuralStatus);
    }

    [Fact]
    public void HealthyRouterUnexpectedProcessExitIsStructuralFailure()
    {
        var tracker = new TunHealthTracker();
        Assert.Equal(TunStructuralStatus.Healthy, tracker.Evaluate(true, true, true, true, 10, MainRouterLifecycle.Running).StructuralStatus);
        Assert.Equal(TunStructuralStatus.StructuralFailure, tracker.Evaluate(false, false, false, false, 0, MainRouterLifecycle.Running).StructuralStatus);
    }

    [Fact]
    public void StoppingRouterProcessExitIsNotStructuralFailure()
        => Assert.False(new TunHealthTracker().Evaluate(false, false, false, false, 0, MainRouterLifecycle.Stopping).StructuralStatus == TunStructuralStatus.StructuralFailure);

    [Fact]
    public void VpnDesiredOffMissingTunIsNormal()
        => Assert.Equal(TunStructuralStatus.Unknown, new TunHealthTracker().Evaluate(false, false, false, false, 0, MainRouterLifecycle.StoppedByDesired).StructuralStatus);

    [Fact]
    public void FreshHealthyObservationClearsStartupTransientState()
    {
        var tracker = new TunHealthTracker();
        tracker.Evaluate(false, false, false, false, 0, MainRouterLifecycle.Starting);
        var healthy = tracker.Evaluate(true, true, true, true, 10, MainRouterLifecycle.Starting);
        Assert.Equal(TunStructuralStatus.Healthy, healthy.StructuralStatus);
    }

    [Fact]
    public void SingleWeakLocalProbeFailureHiddenWhenTunHealthy()
    {
        var policy = new TunDiagnosticPolicy();
        policy.Observe(new(false, -1, "local port busy"), true, 1);
        Assert.Equal("", policy.Detail(TunStructuralStatus.Healthy));
    }

    [Fact]
    public void RepeatedWeakProbeFailureUsesNeutralSecondaryText()
    {
        var policy = new TunDiagnosticPolicy();
        for (int i = 0; i < policy.ConfirmationCount; i++) policy.Observe(new(false, -1, "port busy"), true, 1);
        Assert.Equal("Дополнительная проверка TUN временно недоступна", policy.Detail(TunStructuralStatus.Healthy));
        policy.Observe(new(true, 3), true, 1);
        Assert.Equal("", policy.Detail(TunStructuralStatus.Healthy));
    }

    [Fact]
    public void WeakProbeFailureNeverChangesPrimaryHealthyStatus()
    {
        var policy = new TunDiagnosticPolicy();
        for (int i = 0; i < 10; i++)
        {
            policy.Observe(new(false, -1, "port busy"), true, 1);
            Assert.Equal("VPN подключён", MainViewModel.VpnStatusFor(PhysicalNetworkAvailability.Ready,
                MainRouterLifecycle.Running, TunStructuralStatus.Healthy, true, true, true, false, false));
        }
    }

    [Fact]
    public void StructuralFailureStillShowsRecoveryUi()
        => Assert.Equal("TUN недоступен · восстанавливаю…", MainViewModel.VpnStatusFor(PhysicalNetworkAvailability.Ready,
            MainRouterLifecycle.FailedUnexpectedly, TunStructuralStatus.StructuralFailure, true, true, false, false, false));

    [Fact]
    public void DiagnosticProbeStateDoesNotChangeRuntimeConvergence()
    {
        using var vm = new MainViewModel(new SettingsStore(root), new AppSettings());
        var convergence = vm.RuntimeCoordinator.CurrentConvergenceState;
        var generation = vm.RuntimeCoordinator.CurrentGeneration;
        for (int i = 0; i < 10; i++) vm.SetHealth(new(true, 42), new(false, -1, "port busy"));
        Assert.Same(convergence, vm.RuntimeCoordinator.CurrentConvergenceState);
        Assert.Equal(generation, vm.RuntimeCoordinator.CurrentGeneration);
    }

    [Fact]
    public void AppStartCandidateIdentityMatchesBuiltArtifactName()
    {
        var props = System.Xml.Linq.XDocument.Parse(Source("Directory.Build.props"));
        Assert.Equal(props.Descendants("CandidateName").Single().Value, BuildIdentity.Candidate);
        Assert.Contains("candidate={BuildIdentity.Candidate}", Source("src/NetCat.UI/App.xaml.cs"));
    }

    [Fact]
    public void BuildIdentityUsesSingleAuthoritativeSource()
    {
        var props = Source("Directory.Build.props");
        Assert.Contains("NetCatCandidate", props);
        Assert.DoesNotContain("candidate=Candidate13", Source("src/NetCat.UI/App.xaml.cs"));
        Assert.DoesNotContain("candidate=Candidate14", Source("src/NetCat.UI/App.xaml.cs"));
    }

    [Fact]
    public void StartupAwareClassificationHasOneLifecycleOwner()
        => Assert.Contains("TunHealthClassifier.ApplyLifecycle", Source("src/NetCat.Network/RuntimeCoordinator.cs"));

    [Fact]
    public async Task PhysicalMonitorInitialCaptureIsIndependentOfDesiredState()
    {
        using var fixture = new Candidate12Tests.Fixture(new SettingsStore(root));
        using var monitor = new PhysicalNetworkMonitor(fixture.Coordinator, fixture.Repo);
        monitor.Start();
        await monitor.InitialCapture.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(PhysicalNetworkAvailability.Ready, fixture.Coordinator.PhysicalNetworkState);
        Assert.Equal(0, fixture.Router.Starts);
        Assert.Equal(0, fixture.Zapret.Starts);
        Assert.Equal(0, fixture.Coordinator.CurrentGeneration);
    }

    [Fact]
    public async Task UnexpectedExitWithVpnRunningFalseRequestsAndCompletesRecovery()
    {
        using var fixture = new Candidate12Tests.Fixture(new SettingsStore(root));
        await fixture.Reconcile(ReconcileReason.Startup);
        var logs = new ConcurrentQueue<string>();
        fixture.Coordinator.Log = logs.Enqueue;
        fixture.Router.IsRunning = false;
        using var monitor = new PhysicalNetworkMonitor(fixture.Coordinator, fixture.Repo);
        await monitor.ObserveTunnelOnceAsync();
        await WaitUntil(() => fixture.Router.Starts == 2 && fixture.Coordinator.CurrentConvergenceState.DesiredSatisfied);
        Assert.Contains(logs, l => l.Contains("Running -> FailedUnexpectedly"));
        Assert.Contains(logs, l => l.Contains("emitted=TunStructuralFailure"));
        Assert.Equal(TunStructuralStatus.Healthy, fixture.Coordinator.CurrentObservedState!.TunStatus);
        Assert.Equal(1, fixture.Zapret.Starts);
    }

    [Fact]
    public async Task RouterStartExceptionLeavesStartingAndSchedulesRetry()
    {
        using var fixture = new Candidate12Tests.Fixture(new SettingsStore(root));
        fixture.Router.BeforeStart = () => throw new IOException("test startup failure");
        fixture.Coordinator.RequestReconcile(ReconcileReason.Startup);
        await fixture.Clock.DelayScheduled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(MainRouterLifecycle.FailedUnexpectedly, fixture.Coordinator.MainRouterLifecycle);
        Assert.Equal(1, fixture.Coordinator.GetFailureCount(ComponentId.MainRouter));
        Assert.Equal(0, fixture.Tunnel.Waits);
        Assert.False(fixture.Coordinator.CurrentConvergenceState.DesiredSatisfied);
    }



    private static async Task WaitUntil(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(predicate(), "condition was not observed before timeout");
    }
}
