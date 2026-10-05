using System.Collections.Concurrent;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate16Tests
{
    private sealed class OpenVpn : IOpenVpnRuntime
    {
        public bool IsRunning { get; private set; }
        public OpenVpnLink? Link { get; private set; }
        public Guid? ActiveProfileId { get; private set; }
        public Exception? Error = new TimeoutException("OpenVPN readiness timeout");
        public int Attempts, RouteApplications;
        public Func<Task>? BeforeStart;
        public async Task<OpenVpnLink> EnsureRunningAsync(Profile profile, string dns, CancellationToken ct)
        {
            Interlocked.Increment(ref Attempts);
            if (BeforeStart != null) await BeforeStart();
            if (Error != null) throw Error;
            IsRunning = true;
            ActiveProfileId = profile.Id;
            return Link = new("NetCat-OpenVPN", 40, "10.77.0.2", "10.77.0.1", dns, ["10.77.0.0/16"]);
        }
        public Task EnsureRoutesAsync(CancellationToken ct)
        {
            Assert.True(IsRunning);
            RouteApplications++;
            return Task.CompletedTask;
        }
        public Task EnsureStoppedAsync(CancellationToken ct)
        { IsRunning = false; Link = null; return Task.CompletedTask; }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "NetCat-C16-" + Guid.NewGuid().ToString("N"));
        public readonly Candidate12Tests.Router Router = new();
        public readonly Candidate9Tests.FakeZapretRuntime Zapret = new();
        public readonly Candidate12Tests.Tunnel Tunnel = new();
        public readonly OpenVpn OpenVpn = new();
        public readonly Candidate12Tests.TestClock Clock = new();
        public readonly Candidate9Tests.FakeDesiredProvider Desired;
        public readonly RuntimeConfigurationRepository Repo;
        public readonly RuntimeCoordinator Coordinator;
        public readonly ConcurrentQueue<string> Logs = new();

        public Fixture(ITunnelHealthInspector? inspector = null)
        {
            var vpn = new Profile { Name = "VPN", Protocol = "vless", Host = "example.test", Port = 443 };
            var ovpn = new Profile { Name = "Office", Protocol = "openvpn" };
            Repo = new(new AppSettings { Profiles = [vpn, ovpn], MainProfileId = vpn.Id,
                OpenVpnProfileId = ovpn.Id, Tun = true, ZapretStrategy = "general.bat" }, new SettingsStore(root));
            Desired = new(new DesiredRuntimeState { MainVpnEnabled = true, TunEnabled = true,
                ZapretEnabled = true, OpenVpnEnabled = true, SelectedVpnProfileId = vpn.Id, SelectedOpenVpnProfileId = ovpn.Id });
            Coordinator = new(Desired, Router, Zapret, OpenVpn)
            {
                GetSettings = () => Repo.CurrentSettings, Clock = Clock, Log = Logs.Enqueue,
                PhysicalNetworkProvider = new Candidate9Tests.FakePhysicalProvider(), TunnelInspector = inspector ?? Tunnel,
                OnOpenVpnRoutesLearned = Repo.UpdateOpenVpnLearnedRoutesAsync,
                VerifyOpenVpnRoutesInRouteTable = (_, _) => new(RouteObservationStatus.Verified, []),
                BackoffIntervals = [TimeSpan.FromHours(1)]
            };
        }
        public async Task FailOnce()
        {
            Coordinator.RequestReconcile(ReconcileReason.Startup);
            await Clock.DelayScheduled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, OpenVpn.Attempts);
        }
        public async Task Retry(bool success = false)
        {
            if (success) OpenVpn.Error = null;
            var attempts = OpenVpn.Attempts;
            Clock.Advance(TimeSpan.FromHours(1));
            await Until(() => OpenVpn.Attempts > attempts &&
                (success ? Coordinator.CurrentConvergenceState.DesiredSatisfied : Coordinator.GetFailureCount(ComponentId.OpenVpnLink) > attempts));
        }
        public void Dispose()
        { Coordinator.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(predicate(), "Expected runtime transition did not complete.");
    }

    [Fact]
    public async Task OpenVpnTimeoutDoesNotBlockMainRouterStart()
    {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.OpenVpn.BeforeStart = async () => { entered.TrySetResult(); await release.Task; };
        f.Coordinator.RequestReconcile(ReconcileReason.Startup);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(f.Router.VpnRunning); // While OpenVPN is still waiting, not just after timeout.
            Assert.Equal(MainRouterLifecycle.Running, f.Coordinator.MainRouterLifecycle);
            Assert.Equal(1, f.Tunnel.Waits);
            Assert.Equal(ObservedComponentState.RunningHealthy, f.Coordinator.CurrentObservedState!.MainRouterStatus);
        }
        finally { release.TrySetResult(); }
        await f.Clock.DelayScheduled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, f.Router.Starts);
    }

    [Fact]
    public async Task OpenVpnTimeoutDoesNotBlockWaitTunReady()
    {
        using var f = new Fixture(); await f.FailOnce();
        Assert.Equal(1, f.Tunnel.Waits);
        Assert.Equal(TunStructuralStatus.Healthy, f.Coordinator.CurrentObservedState!.TunStatus);
        Assert.DoesNotContain(f.Logs, x => x.Contains("WaitTunReady result=skipped"));
    }

    [Fact]
    public async Task OpenVpnFailureDoesNotLeaveMainRouterLifecycleStarting()
    {
        using var f = new Fixture(); await f.FailOnce();
        Assert.Equal(MainRouterLifecycle.Running, f.Coordinator.MainRouterLifecycle);
    }

    [Fact]
    public async Task OpenVpnFailureKeepsMainVpnSatisfiedWhenRouterStarted()
    {
        using var f = new Fixture(); await f.FailOnce();
        Assert.Equal(ObservedComponentState.RunningHealthy, f.Coordinator.CurrentObservedState!.MainRouterStatus);
        Assert.Equal(0, f.Coordinator.GetFailureCount(ComponentId.MainRouter));
        Assert.False(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
        Assert.Equal(new[] { ComponentId.OpenVpnLink }, f.Coordinator.CurrentConvergenceState.PendingComponents);
    }

    [Fact]
    public async Task OpenVpnFailureKeepsTunSatisfiedWhenTunReady()
    {
        using var f = new Fixture(); await f.FailOnce();
        Assert.True(f.Coordinator.CurrentObservedState!.TunReady);
        Assert.False(f.Coordinator.CurrentObservedState.StructuralTunFailure);
        Assert.Equal(0, f.Coordinator.GetFailureCount(ComponentId.Tun));
    }

    [Fact]
    public async Task OpenVpnFailureDoesNotRestartHealthyMainRouterOnRetry()
    {
        using var f = new Fixture(); await f.FailOnce(); await f.Retry();
        Assert.Equal(1, f.Router.Starts); Assert.Equal(0, f.Router.Overlays); Assert.Equal(0, f.Router.Stops);
        Assert.Equal(1, f.Tunnel.Waits);
    }

    [Fact]
    public async Task OpenVpnFailureDoesNotRestartHealthyZapretOnRetry()
    {
        using var f = new Fixture(); await f.FailOnce(); await f.Retry();
        Assert.Equal(1, f.Zapret.EnsureRunningCalls); Assert.Equal(0, f.Zapret.EnsureStoppedCalls);
    }

    [Fact]
    public async Task OpenVpnRetryPlanContainsOnlyOpenVpnSpecificActionsWhenOthersHealthy()
    {
        using var f = new Fixture(); await f.FailOnce(); await f.Retry();
        Assert.Equal("PLAN [StartOpenVpn,FinalizeRouting]", f.Logs.Last(x => x.StartsWith("PLAN ")));
        Assert.Equal(0, f.OpenVpn.RouteApplications);
        Assert.Equal(false, f.Coordinator.CurrentObservedState!.OpenVpnRoutesInstalled);
    }

    [Fact]
    public async Task MainVpnCanStartWhenOpenVpnEnabledButUnavailable()
    {
        using var f = new Fixture(); f.OpenVpn.Error = new IOException("server unavailable"); await f.FailOnce();
        Assert.True(f.Router.VpnRunning); Assert.True(f.Zapret.IsRunning);
        Assert.True(f.Desired.Current.OpenVpnEnabled); // Failure never changes intent.
    }

    [Fact]
    public async Task MainVpnCanStartWhenOpenVpnEnabledAndTimesOut()
    {
        using var f = new Fixture(); await f.FailOnce();
        Assert.True(f.Router.VpnRunning);
        Assert.Contains("COMPONENT_FAILURE component=OpenVpn scope=local", f.Logs);
        Assert.Contains("DEPENDENCY_EFFECT blocked=[OpenVpnRoutes] unaffected=[MainVpn,Tun,Zapret]", f.Logs);
    }

    [Fact]
    public async Task OpenVpnCanStartWhenMainVpnDisabled()
    {
        using var f = new Fixture(); f.OpenVpn.Error = null;
        f.Desired.Current = f.Desired.Current with { MainVpnEnabled = false };
        f.Coordinator.RequestReconcile(ReconcileReason.Startup);
        await Until(() => f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
        Assert.True(f.OpenVpn.IsRunning); Assert.Equal(1, f.OpenVpn.RouteApplications);
        // Independent OpenVPN now owns the local carrier, never a main VPN profile.
        Assert.Equal(1, f.Router.Starts); Assert.Equal(1, f.Tunnel.Waits);
        Assert.Null(f.Router.ActiveProfileId);
        Assert.Equal(MainRouterLifecycle.Running, f.Coordinator.MainRouterLifecycle);
    }

    [Fact]
    public async Task OpenVpnSuccessAfterMainVpnHealthyFinalizesRoutesWithoutFullRouterRestart()
    {
        using var f = new Fixture(); await f.FailOnce(); await f.Retry(success: true);
        // Candidate17 applies the gateway independently; no main refresh remains.
        Assert.Equal(1, f.Router.Starts); Assert.Equal(0, f.Router.Overlays); Assert.Equal(1, f.Router.GatewayUpdates);
        Assert.Equal(1, f.OpenVpn.RouteApplications);
        Assert.Equal(1, f.Zapret.EnsureRunningCalls);
        Assert.Contains("10.77.0.0/16", f.Router.Applied!.Profiles.Single(p => p.IsOpenVpn).LearnedRoutes);
        Assert.True(f.Coordinator.CurrentObservedState!.OpenVpnRoutesInstalled);
        Assert.Equal(MainRouterLifecycle.Running, f.Coordinator.MainRouterLifecycle);
        await f.Coordinator.ReconcileAsync(ReconcileReason.UserChangedSettings);
        Assert.Equal(1, f.Router.GatewayUpdates); Assert.Equal(0, f.Router.Overlays); Assert.Equal(1, f.Router.Starts);
    }

    [Fact]
    public void OpenVpnRouteFinalizationWaitsForOpenVpnLinkOnly()
    {
        var plan = StartupPlan();
        Assert.Equal(PlanActionType.StartOpenVpn, plan.Actions.Single(x => x.Type == PlanActionType.FinalizeRouting).Prerequisite);
        Assert.Null(plan.Actions.Single(x => x.Type == PlanActionType.StartOpenVpn).Prerequisite);
    }

    private static RuntimePlan StartupPlan() => RuntimePlanner.CreatePlan(
        new() { MainVpnEnabled = true, TunEnabled = true, ZapretEnabled = true, OpenVpnEnabled = true },
        new(ObservedComponentState.Stopped, ObservedComponentState.Stopped, ObservedComponentState.Stopped, false,
            new("Ethernet", 1, "192.168.1.2", "192.168.1.1", []), true), new(), new Dictionary<ComponentId, int>());

    [Fact]
    public void StartMainRouterNeverHasStartOpenVpnAsPrerequisite()
    {
        var plan = StartupPlan();
        Assert.Null(plan.Actions.Single(x => x.Type == PlanActionType.StartMainRouter).Prerequisite);
        Assert.Equal(new[] { PlanActionType.StartZapret, PlanActionType.StartMainRouter, PlanActionType.WaitTunReady,
            PlanActionType.StartOpenVpn, PlanActionType.FinalizeRouting }, plan.Actions.Select(x => x.Type));
    }

    [Fact]
    public void WaitTunReadyNeverDependsOnStartOpenVpn()
        => Assert.Equal(PlanActionType.StartMainRouter, StartupPlan().Actions.Single(x => x.Type == PlanActionType.WaitTunReady).Prerequisite);

    [Fact]
    public void NoPlannerBranchCreatesCrossComponentStartupPrerequisites()
    {
        foreach (bool main in new[] { false, true })
        foreach (bool openVpn in new[] { false, true })
        foreach (var router in Enum.GetValues<ObservedComponentState>())
        foreach (var link in Enum.GetValues<ObservedComponentState>())
        foreach (bool structural in new[] { false, true })
        {
            var plan = RuntimePlanner.CreatePlan(new() { MainVpnEnabled = main, TunEnabled = true, OpenVpnEnabled = openVpn },
                new(router, ObservedComponentState.Stopped, link, !structural, null, true, StructuralTunFailure: structural),
                new(), new Dictionary<ComponentId, int>());
            Assert.DoesNotContain(plan.Actions, a => a.TargetComponent is ComponentId.MainRouter or ComponentId.Tun &&
                a.Prerequisite is PlanActionType.StartOpenVpn or PlanActionType.StopOpenVpn or PlanActionType.FinalizeRouting);
            Assert.DoesNotContain(plan.Actions, a => a.Type == PlanActionType.StartOpenVpn && a.Prerequisite.HasValue);
        }
    }

    [Fact]
    public void LateOpenVpnLinkRequiresNewStaticOutboundAndDnsConfiguration()
    {
        var settings = new AppSettings();
        var physical = new NetworkSnapshot("Ethernet", 1, "192.168.1.2", "192.168.1.1", []);
        var before = SingBoxConfig.Build(settings, physical, null, null, false);
        var after = SingBoxConfig.Build(settings, physical, null,
            new("NetCat-OpenVPN", 40, "10.77.0.2", "10.77.0.1", "10.77.0.1", ["10.77.0.0/16"]), false);
        Assert.DoesNotContain(before["outbounds"]!.AsArray(), n => n?["tag"]?.ToString() == "openvpn");
        var outbound = Assert.Single(after["outbounds"]!.AsArray(), n => n?["tag"]?.ToString() == "openvpn");
        Assert.Equal("NetCat-OpenVPN", outbound!["bind_interface"]!.ToString());
        Assert.Equal("10.77.0.2", outbound["inet4_bind_address"]!.ToString());
        Assert.Contains(after["dns"]!["servers"]!.AsArray(), n => n?["tag"]?.ToString() == "dns-openvpn");
    }

    [Fact]
    public async Task MainRouterFailureDoesNotBlockOpenVpnLinkEstablishment()
    {
        using var f = new Fixture(); f.OpenVpn.Error = null;
        f.Router.BeforeStart = () => throw new IOException("main startup failure");
        f.Coordinator.RequestReconcile(ReconcileReason.Startup);
        await f.Clock.DelayScheduled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(f.OpenVpn.IsRunning);
        Assert.Equal(1, f.OpenVpn.RouteApplications);
        Assert.Equal(MainRouterLifecycle.FailedUnexpectedly, f.Coordinator.MainRouterLifecycle);
    }

    private sealed class ThrowingReadiness : ITunnelHealthInspector
    {
        public Task<RouterHealth> InspectAsync(bool running, int port, bool tun, CancellationToken ct)
            => Task.FromResult(new RouterHealth(running, running, running, running, 10));
        public Task<RouterHealth> WaitForTunReadyAsync(Func<bool> running, Func<int> port, bool tun, TimeSpan timeout, CancellationToken ct)
            => throw new IOException("readiness inspection failed");
    }

    [Fact]
    public async Task ReadinessExceptionLeavesStartingAndSchedulesItsOwnRetry()
    {
        using var f = new Fixture(new ThrowingReadiness()); await f.FailOnce();
        Assert.Equal(MainRouterLifecycle.FailedUnexpectedly, f.Coordinator.MainRouterLifecycle);
        Assert.Equal(1, f.Coordinator.GetFailureCount(ComponentId.Tun));
        Assert.Equal(1, f.Coordinator.GetFailureCount(ComponentId.OpenVpnLink));
    }

    [Fact]
    public async Task ZapretFailureDoesNotBlockMainVpnUnlessModeExplicitlyRequiresIt()
    {
        using var f = new Fixture(); f.Zapret.FailStart = true; await f.FailOnce();
        Assert.True(f.Router.VpnRunning); Assert.True(f.Coordinator.CurrentObservedState!.TunReady);
        Assert.Equal(MainRouterLifecycle.Running, f.Coordinator.MainRouterLifecycle);
    }

    [Fact]
    public async Task PrerequisiteSkipCannotLeaveUnrelatedLifecycleInStarting()
    {
        using var f = new Fixture(); await f.FailOnce();
        Assert.Contains(f.Logs, x => x == "ACTION FinalizeRouting result=skipped prerequisite=StartOpenVpn");
        Assert.Equal(MainRouterLifecycle.Running, f.Coordinator.MainRouterLifecycle);
        using var brokenMain = new Fixture();
        brokenMain.Router.BeforeStart = () => throw new IOException("main startup failed");
        await brokenMain.FailOnce();
        Assert.Contains(brokenMain.Logs, x => x == "ACTION WaitTunReady result=skipped prerequisite=StartMainRouter");
        Assert.Equal(MainRouterLifecycle.FailedUnexpectedly, brokenMain.Coordinator.MainRouterLifecycle);
    }
}
