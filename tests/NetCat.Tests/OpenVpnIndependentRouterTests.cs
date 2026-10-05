using NetCat.Core;
using NetCat.Network;
using NetCat.Engine;
using System.Text.Json.Nodes;
using Xunit;

namespace NetCat.Tests;

public class OpenVpnIndependentRouterTests
{
    private static readonly NetworkSnapshot Physical = new("Ethernet", 1, "192.0.2.2", "192.0.2.1", []);

    [Fact]
    public void OpenVpnOnlyStartsLocalRouterAndTunWithoutWaitingForLink()
    {
        var plan = RuntimePlanner.CreatePlan(new() { OpenVpnEnabled = true, TunEnabled = true },
            new(ObservedComponentState.Stopped, ObservedComponentState.Stopped, ObservedComponentState.Stopped, false, Physical, true),
            new(), new Dictionary<ComponentId, int>());
        Assert.Null(plan.Actions.Single(a => a.Type == PlanActionType.StartMainRouter).Prerequisite);
        Assert.Contains(plan.Actions, a => a.Type == PlanActionType.WaitTunReady);
        Assert.Null(plan.Actions.Single(a => a.Type == PlanActionType.StartOpenVpn).Prerequisite);
    }

    [Fact]
    public void OpenVpnOnlyEffectiveSettingsDoNotSelectMainVpnProfile()
    {
        var id = Guid.NewGuid();
        var settings = new AppSettings { MainProfileId = id };
        var effective = EffectiveRuntimeConfigBuilder.Build(settings,
            new() { OpenVpnEnabled = true, SelectedVpnProfileId = id }, Physical);
        Assert.Null(effective.TargetSettings!.MainProfileId);
        Assert.False(effective.VpnEnabled);
        Assert.True(effective.TunEnabled);
        Assert.Equal(id, settings.MainProfileId);
    }

    [Fact]
    public void HealthyOpenVpnOnlyRouterIsNotStoppedByMainVpnOff()
    {
        var plan = RuntimePlanner.CreatePlan(new() { OpenVpnEnabled = true, TunEnabled = true },
            new(ObservedComponentState.RunningHealthy, ObservedComponentState.Stopped, ObservedComponentState.RunningHealthy, true,
                Physical, true, TunConfigured: true, TunObservedHealthy: true, OpenVpnRoutesInstalled: true),
            new(), new Dictionary<ComponentId, int>());
        Assert.Empty(plan.Actions);
    }

    [Theory]
    [InlineData(RoutingMode.Rules)]
    [InlineData(RoutingMode.Global)]
    [InlineData(RoutingMode.SelectiveDirect)]
    [InlineData(RoutingMode.SelectiveVpn)]
    public void OpenVpnOnlyOrdinaryTrafficIsDirectAndExplicitVpnRuleStillRejects(RoutingMode mode)
    {
        var s = new AppSettings { Mode = mode, Rules = [new() { Kind = RuleKind.ExactDomain, Value = "intentional.example", Target = RouteTarget.Vpn }] };
        var cfg = SingBoxConfig.Build(s, Physical, null, null, true);
        Assert.Equal("direct", cfg["route"]!["final"]!.ToString());
        Assert.Equal("dns-direct", cfg["dns"]!["final"]!.ToString());
        Assert.DoesNotContain(cfg["outbounds"]!.AsArray(), n => n?["tag"]?.ToString() is "vpn" or "vpn-user");
        var explicitRule = Assert.Single(cfg["route"]!["rules"]!.AsArray(), n => n?["domain"]?[0]?.ToString() == "intentional.example");
        Assert.Equal("reject", explicitRule!["action"]!.ToString());
        Assert.Single(cfg["inbounds"]!.AsArray(), n => n?["type"]?.ToString() == "tun");
    }

    [Fact]
    public void ExplicitTunOffRetainsIpOnlyOpenVpn()
    {
        var desired = new DesiredRuntimeState { OpenVpnEnabled = true, TunEnabled = false };
        Assert.False(desired.RouterEnabled);
        var plan = RuntimePlanner.CreatePlan(desired,
            new(ObservedComponentState.Stopped, ObservedComponentState.Stopped, ObservedComponentState.Stopped, false, Physical, true),
            new(), new Dictionary<ComponentId, int>());
        Assert.DoesNotContain(plan.Actions, a => a.Type is PlanActionType.StartMainRouter or PlanActionType.WaitTunReady);
        Assert.Contains(plan.Actions, a => a.Type == PlanActionType.StartOpenVpn);
    }

    [Fact]
    public async Task OpenVpnOnlyCarrierConvergesDoesNotRestartOnNoopAndStopsOnOff()
    {
        using var f = new OpenVpnBehaviorFixture();
        var router = new IndependentRouter(f.Main);
        typeof(RuntimeCoordinator).GetProperty(nameof(RuntimeCoordinator.RouterRuntime))!.SetValue(f.Coordinator, router);
        await f.Pass();
        Assert.True(router.IsRunning); Assert.False(router.VpnRunning); Assert.True(router.TunActive);
        Assert.Null(router.ActiveProfileId); Assert.Equal(MainRouterLifecycle.Running, f.Coordinator.MainRouterLifecycle);
        Assert.True(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
        for (int i = 0; i < 3; i++) await f.Pass();
        Assert.Equal(1, f.Main.Starts);
        f.Off(); await f.Pass();
        Assert.False(router.IsRunning); Assert.False(router.TunActive); Assert.False(f.OpenVpn.IsRunning);
        Assert.Equal(1, f.Main.Stops); Assert.True(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
    }

    [Fact]
    public async Task OpenVpnOnlyCarrierFailurePreservesBackoffAndDoesNotStopLink()
    {
        using var f = new OpenVpnBehaviorFixture(); int attempts = 0;
        f.Main.BeforeStart = () => { attempts++; throw new System.IO.IOException("local carrier start failed"); };
        await f.Pass();
        for (int i = 0; i < 5; i++) await f.Pass(ReconcileReason.TunStructuralFailure);
        Assert.Equal(1, attempts); Assert.True(f.OpenVpn.IsRunning);
        Assert.Equal(1, f.Coordinator.GetFailureCount(ComponentId.MainRouter));
        f.Main.BeforeStart = null; f.Clock.Advance(TimeSpan.FromHours(2));
        await f.Pass(ReconcileReason.ExternalConditionResolved);
        Assert.True(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
        Assert.Null(f.Main.ActiveProfileId);
    }

    [Fact]
    public async Task OpenVpnOnlyCarrierRecoversUnexpectedTunLoss()
    {
        using var f = new OpenVpnBehaviorFixture(); var tunnel = new Candidate12Tests.Tunnel();
        typeof(RuntimeCoordinator).GetProperty(nameof(RuntimeCoordinator.TunnelInspector))!.SetValue(f.Coordinator, tunnel);
        await f.Pass(); tunnel.Status = TunStructuralStatus.StructuralFailure;
        await f.Pass(ReconcileReason.TunStructuralFailure);
        Assert.Equal(2, f.Main.Starts); Assert.Equal(1, f.OpenVpn.Starts);
        Assert.Null(f.Main.ActiveProfileId);
    }

    [Fact]
    public async Task PhysicalRebindingOfCarrierDoesNotEnableSavedMainVpnProfile()
    {
        using var f = new OpenVpnBehaviorFixture(); var main = new Profile { Protocol = "vless" };
        f.Settings.Profiles.Add(main); f.Settings.MainProfileId = main.Id;
        f.Desired.Current = f.Desired.Current with { SelectedVpnProfileId = main.Id };
        await f.Pass();
        f.Physical.Current = new("Wi-Fi", 25, "192.0.2.3", "192.0.2.1", []);
        await f.Pass(ReconcileReason.PhysicalNetworkChanged);
        Assert.Equal(2, f.Main.Starts); Assert.Null(f.Main.ActiveProfileId);
        Assert.Equal(main.Id, f.Settings.MainProfileId);
    }

    [Fact]
    public async Task SwitchingMainVpnOnOffKeepsOpenVpnLinkAndReusesSingleRouterOwner()
    {
        using var f = new OpenVpnBehaviorFixture(); var profile = new Profile { Protocol = "vless" };
        f.Settings.Profiles.Add(profile); f.Settings.MainProfileId = profile.Id;
        await f.Pass(); Assert.Null(f.Main.ActiveProfileId);
        f.Desired.Current = f.Desired.Current with { MainVpnEnabled = true, SelectedVpnProfileId = profile.Id };
        await f.Pass(); Assert.Equal(profile.Id, f.Main.ActiveProfileId); Assert.Equal(2, f.Main.Starts);
        f.Desired.Current = f.Desired.Current with { MainVpnEnabled = false };
        await f.Pass(); Assert.Null(f.Main.ActiveProfileId); Assert.Equal(3, f.Main.Starts);
        Assert.True(f.Main.TunActive); Assert.Equal(0, f.Main.Stops); Assert.Equal(1, f.OpenVpn.Starts);
        Assert.True(f.OpenVpn.IsRunning); Assert.True(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
    }

    [Fact]
    public async Task MainVpnRequestWithoutProfileCannotSilentlyBecomeDirectCarrier()
    {
        using var router = new RouterService("unused-modules",Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => router.EnsureRunningAsync(new(),Physical,true,"missing-main-profile",CancellationToken.None));
        Assert.False(router.IsRunning); Assert.False(router.VpnRequested);
    }

    // The underlying fixture historically equates a live carrier with a VPN.
    // This boundary explicitly reproduces the production standalone state.
    private sealed class IndependentRouter(Candidate12Tests.Router inner) : IRouterRuntime
    {
        public bool IsRunning => inner.IsRunning;
        public bool VpnRunning => inner.IsRunning && inner.ActiveProfileId != null;
        public bool TunActive => inner.TunActive;
        public int ListenPort => inner.ListenPort;
        public int LatencyPort => inner.LatencyPort;
        public int HealthSourcePort => inner.HealthSourcePort;
        public NetworkSnapshot? ActivePhysical => inner.ActivePhysical;
        public Guid? ActiveProfileId => inner.ActiveProfileId;
        public long SessionRevision => inner.SessionRevision;
        public Task EnsureRunningAsync(AppSettings s, NetworkSnapshot p, string reason, CancellationToken ct) => inner.EnsureRunningAsync(s,p,reason,ct);
        public Task EnsureStoppedAsync(CancellationToken ct) => inner.EnsureStoppedAsync(ct);
        public Task RefreshPhysicalAsync(AppSettings s, NetworkSnapshot p, string reason, CancellationToken ct) => inner.RefreshPhysicalAsync(s,p,reason,ct);
        public Task ApplyOpenVpnOverlayAsync(AppSettings s, OpenVpnLink? link, CancellationToken ct) => inner.ApplyOpenVpnOverlayAsync(s,link,ct);
    }
}
