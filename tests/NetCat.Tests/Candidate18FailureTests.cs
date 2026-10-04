using NetCat.Core;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate18FailureTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NetCat-C18-failures-" + Guid.NewGuid().ToString("N"));
    public Candidate18FailureTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private sealed class OpenVpn : IOpenVpnRuntime
    {
        public bool IsRunning => Link != null;
        public bool Reconnecting { get; set; }
        public OpenVpnLink? Link { get; set; }
        public Guid? ActiveProfileId { get; set; }
        public bool FailStart;
        public int Starts, Stops;
        public Task<OpenVpnLink> EnsureRunningAsync(Profile profile, string dns, CancellationToken ct)
        {
            Starts++; if (FailStart) throw new IOException("controlled start failure");
            if (ActiveProfileId != null && ActiveProfileId != profile.Id) throw new InvalidOperationException("old profile still running");
            ActiveProfileId = profile.Id; Link = new("fixture", 42, "10.1.0.2", "10.1.0.1", dns, profile.LearnedRoutes) { ProfileId = profile.Id, Generation = 1 };
            return Task.FromResult(Link);
        }
        public Task EnsureStoppedAsync(CancellationToken ct) { Stops++; Link = null; ActiveProfileId = null; Reconnecting = false; return Task.CompletedTask; }
    }
    private sealed class Router : IRouterRuntime
    {
        public bool IsRunning { get; private set; }
        public bool VpnRunning => IsRunning;
        public bool TunActive => IsRunning;
        public int ListenPort => 1080; public int LatencyPort => 1081; public int HealthSourcePort => 0;
        public NetworkSnapshot? ActivePhysical { get; private set; }
        public Guid? ActiveProfileId { get; private set; }
        public long SessionRevision { get; private set; }
        public bool OpenVpnGatewayReady { get; set; } = true;
        public bool OpenVpnOverlayReady => Overlay != null;
        public OpenVpnLink? Overlay;
        public int Starts, Stops, Refreshes, Overlays;
        public void InvalidateOpenVpnOverlay() => Overlay = null;
        public Task EnsureRunningAsync(AppSettings s, NetworkSnapshot n, string reason, CancellationToken ct) { Starts++; SessionRevision++; IsRunning = true; ActiveProfileId = s.MainProfileId; ActivePhysical = n; return Task.CompletedTask; }
        public Task EnsureStoppedAsync(CancellationToken ct) { Stops++; IsRunning = false; return Task.CompletedTask; }
        public Task RefreshPhysicalAsync(AppSettings s, NetworkSnapshot n, string reason, CancellationToken ct) { Refreshes++; return Task.CompletedTask; }
        public Task ApplyOpenVpnOverlayAsync(AppSettings s, OpenVpnLink? link, CancellationToken ct) { Overlay = link; Overlays++; OpenVpnGatewayReady = true; return Task.CompletedTask; }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly Candidate12Tests.Fixture Base;
        public readonly Router Router = new(); public readonly OpenVpn O = new(); public readonly RuntimeCoordinator Coordinator;
        public Fixture(string root)
        {
            Base = new(new SettingsStore(root)); Base.Desired.Current = Base.Desired.Current with { OpenVpnEnabled = true };
            Coordinator = new(Base.Desired, Router, Base.Zapret, O) { GetSettings = () => Base.Repo.CurrentSettings,
                PhysicalNetworkProvider = Base.Physical, TunnelInspector = Base.Tunnel, Clock = Base.Clock,
                OnOpenVpnRoutesLearned = Base.Repo.UpdateOpenVpnLearnedRoutesAsync,
                VerifyOpenVpnRoutesInRouteTable = (_, _) => new(RouteObservationStatus.Verified, []), BackoffIntervals = [TimeSpan.FromHours(1)] };
        }
        public Task Pass() => Coordinator.ReconcileAsync(ReconcileReason.OpenVpnStateChanged);
        public async Task FailedSwitch()
        {
            await Pass(); var b = new Profile { Protocol = "openvpn", LearnedRoutes = ["10.2.0.0/16"] };
            await Base.Repo.UpdateSettingsAsync(s => { s.Profiles.Add(b); return s; });
            Base.Desired.Current = Base.Desired.Current with { SelectedOpenVpnProfileId = b.Id }; O.FailStart = true; await Pass();
        }
        public void Stable() { Assert.Equal(1, Router.Starts); Assert.Equal(0, Router.Stops); Assert.Equal(0, Router.Refreshes); Assert.Equal(1, Router.SessionRevision); Assert.Equal(1, Base.Zapret.Starts); Assert.Equal(MainRouterLifecycle.Running, Coordinator.MainRouterLifecycle); }
        public void Dispose() { Coordinator.Dispose(); Base.Dispose(); }
    }
    [Fact] public async Task FailedProfileSwitchDoesNotRestartMainRouter()
    { using var f = new Fixture(root); await f.FailedSwitch(); f.Stable(); Assert.Equal(1, f.O.Stops); }
    [Fact] public async Task FailedProfileSwitchRemainsFailClosed()
    { using var f = new Fixture(root); await f.FailedSwitch(); Assert.Null(f.Router.Overlay); Assert.Null(f.O.Link); Assert.False(f.Coordinator.CurrentConvergenceState.DesiredSatisfied); await f.Pass(); Assert.Null(f.Router.Overlay); f.Stable(); }
    [Fact] public async Task OpenVpnReconnectKeepsTunIdentityAndSessionStable()
    {
        using var f = new Fixture(root); await f.Pass(); var link = f.O.Link!;
        f.O.Link = null; f.O.Reconnecting = true; f.Router.InvalidateOpenVpnOverlay(); await f.Pass();
        Assert.False(f.Coordinator.CurrentConvergenceState.DesiredSatisfied); Assert.True(f.Router.TunActive); f.Stable();
        f.O.Reconnecting = false; f.O.Link = link with { Generation = 2 }; await f.Pass(); f.Stable(); Assert.Equal(1, f.Base.Tunnel.Waits);
    }
    [Fact] public async Task OpenVpnReconnectDoesNotRestartZapret()
    {
        using var f = new Fixture(root); await f.Pass(); var link = f.O.Link!;
        for (int i = 2; i < 5; i++) { f.O.Link = null; f.O.Reconnecting = true; f.Router.InvalidateOpenVpnOverlay(); await f.Pass(); f.O.Reconnecting = false; f.O.Link = link with { Generation = i }; await f.Pass(); }
        f.Stable(); Assert.Equal(0, f.Base.Zapret.Stops);
    }
    [Fact] public async Task InProcessReconnectWithChangedDnsRefreshesSidecarOnly()
    {
        using var f = new Fixture(root); await f.Pass(); f.Router.InvalidateOpenVpnOverlay(); f.O.Link = f.O.Link! with { Generation = 2, Dns = "10.2.0.53" };
        await f.Pass(); Assert.Equal("10.2.0.53", f.Router.Overlay!.Dns); Assert.Equal(2, f.Router.Overlays); f.Stable();
    }
    [Fact] public async Task SidecarRecoveryKeepsSameGatewayEndpoints()
    {
        using var f = new Fixture(root); await f.Pass(); f.Router.OpenVpnGatewayReady = false; f.Router.InvalidateOpenVpnOverlay(); await f.Pass();
        Assert.NotNull(f.Router.Overlay); f.Stable(); Assert.Equal(2, f.Router.Overlays);
        // Exact port identities additionally exercised by the native OFF crash test.
    }
    [Fact] public async Task GatewayCrashWhenOffPlansLocalRejectRecovery()
    {
        using var f = new Fixture(root); await f.Pass(); f.Base.Desired.Current = f.Base.Desired.Current with { OpenVpnEnabled = false }; await f.Pass();
        f.Router.OpenVpnGatewayReady = false; int prior = f.Router.Overlays; await f.Pass();
        Assert.Equal(prior + 1, f.Router.Overlays); Assert.Null(f.Router.Overlay); f.Stable();
    }
    [Fact] public async Task OpenVpnDomainsChangeDoesNotReconfigureMain()
    { using var f = new Fixture(root); await f.Pass(); await f.Base.Repo.UpdateSettingsAsync(s => { s.OpenVpnDomains = "new.corp.test"; return s; }); await f.Pass(); f.Stable(); Assert.Equal(2, f.Router.Overlays); }
    [Fact] public async Task DeletingActiveProfileStopsOnlyOpenVpn()
    { using var f = new Fixture(root); await f.Pass(); await f.Base.Repo.UpdateSettingsAsync(s => { s.Profiles.RemoveAll(p => p.IsOpenVpn); return s; }); await f.Pass(); Assert.Null(f.O.Link); Assert.Null(f.Router.Overlay); f.Stable(); }
    [Fact] public async Task OpenVpnUserOffStormConvergesWithoutMainRestart()
    {
        using var f = new Fixture(root); await f.Pass();
        for (int i = 0; i < 10; i++) { f.Base.Desired.Current = f.Base.Desired.Current with { OpenVpnEnabled = i % 2 == 0 }; await f.Pass(); }
        Assert.Null(f.O.Link); Assert.Null(f.Router.Overlay); f.Stable();
    }
    [Fact] public async Task RouteJournalWriteFailureHappensBeforeWindowsMutation()
    {
        var path = Path.Combine(root, "journal"); Directory.CreateDirectory(path + ".next"); int calls = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => OpenVpnRouteJournal.InstallAsync(path, new("fixture", 42, "10.1.0.2", "10.1.0.1", "10.1.0.53", ["10.1.0.0/16"]), () => [], (_, _) => { calls++; return Task.FromResult((0, "")); }, CancellationToken.None));
        Assert.Equal(0, calls);
    }
    [Fact] public async Task CorruptJournalCannotExecuteRouteCleanup()
    {
        var path = Path.Combine(root, "journal"); await File.WriteAllTextAsync(path, "{\"Schema\":2,\"InterfaceIndex\":0,\"Gateway\":\"invalid\",\"Prefixes\":[]}"); int calls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => OpenVpnRouteJournal.CleanupAsync(path, (_, _) => { calls++; return Task.FromResult((0, "")); })); Assert.Equal(0, calls); Assert.True(File.Exists(path));
    }
    [Fact] public void RouteOwnershipRejectsIpv6AndNonCanonicalInjectionInputs()
    { foreach (var prefix in new[] { "::/24", "10.1.2.3/8", "10.0.0.0/8';Remove-NetRoute", "0.0.0.0/0", "128.0.0.0/1" }) Assert.False(OpenVpnRoutes.ValidatePushedRoute(prefix, true)); }
}
