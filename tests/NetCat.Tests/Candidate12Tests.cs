using System.Diagnostics;
using System.Reflection;
using System.Windows.Data;
using System.Windows.Threading;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

// Tests exercise production planner/coordinator/builder/repository; only OS and component boundaries are fake.
public sealed class Candidate12Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NetCat-C12-" + Guid.NewGuid().ToString("N"));
    public Candidate12Tests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch { } }
    private SettingsStore Store => new(root);
    private static Profile Profile(string name) => new() { Name = name, Protocol = "vless", Host = "example.test", Port = 443, OutboundJson = "{\"type\":\"vless\",\"server\":\"example.test\",\"server_port\":443,\"uuid\":\"11111111-1111-1111-1111-111111111111\"}" };

    internal sealed class Router : IRouterRuntime
    {
        public bool IsRunning { get; set; }
        public bool VpnRunning => IsRunning && DependenciesHealthy;
        public bool DependenciesHealthy { get; set; } = true;
        public event Action? DependencyLost;
        public void LoseXray() { DependenciesHealthy = false; DependencyLost?.Invoke(); }
        public bool TunActive { get; set; }
        public int ListenPort => 1080;
        public int LatencyPort => 1081;
        public int HealthSourcePort => 0;
        public NetworkSnapshot? ActivePhysical { get; set; }
        public Guid? ActiveProfileId { get; set; }
        public long SessionRevision { get; private set; }
        public int Starts, Stops, Overlays, GatewayUpdates;
        public bool FailStop;
        public bool ExitBeforeStopFailure;
        public Func<Task>? BeforeStart;
        public AppSettings? Applied;
        public async Task EnsureRunningAsync(AppSettings s, NetworkSnapshot physical, string reason, CancellationToken ct)
        {
            if (BeforeStart != null) await BeforeStart();
            Starts++; SessionRevision++; Applied = JsonSettings.Clone(s);
            IsRunning = true; DependenciesHealthy = true; TunActive = s.Tun; ActiveProfileId = s.MainProfileId; ActivePhysical = physical;
        }
        public Task EnsureStoppedAsync(CancellationToken ct)
        { Stops++; if (ExitBeforeStopFailure) IsRunning = false; if (FailStop) throw new IOException("stop router"); IsRunning = false; TunActive = false; return Task.CompletedTask; }
        public Task RefreshPhysicalAsync(AppSettings s, NetworkSnapshot physical, string reason, CancellationToken ct)
        { Overlays++; Applied = JsonSettings.Clone(s); return Task.CompletedTask; }
        public Task ApplyOpenVpnOverlayAsync(AppSettings s, OpenVpnLink? link, CancellationToken ct)
        { GatewayUpdates++; Applied = JsonSettings.Clone(s); return Task.CompletedTask; }
    }
    internal sealed class Zapret : IZapretRuntime
    {
        public bool IsRunning { get; set; }
        public string ActiveStrategy { get; private set; } = "";
        public string ActiveScenario { get; private set; } = "";
        public ZapretObservedState? ObservedState { get; private set; }
        public int Starts, Stops;
        public bool FailStop;
        public event Action<int, int>? ProcessExited;
        public Task EnsureRunningAsync(AppSettings s, string? file, CancellationToken ct)
        {
            Starts++; IsRunning = true; ActiveStrategy = s.ZapretStrategy; ActiveScenario = s.Scenario;
            return Task.CompletedTask;
        }
        public Task EnsureStoppedAsync(CancellationToken ct)
        { Stops++; if (FailStop) throw new IOException("stop zapret"); IsRunning = false; return Task.CompletedTask; }
        public void Crash() { IsRunning = false; ProcessExited?.Invoke(901, 1); }
    }
    internal sealed class OpenVpn : IOpenVpnRuntime
    {
        public bool IsRunning { get; set; }
        public OpenVpnLink? Link { get; set; }
        public Guid? ActiveProfileId { get; set; }
        public int Starts, Stops;
        public bool FailStop;
        public List<string> Learned = ["10.77.0.0/16"];
        public Task<OpenVpnLink> EnsureRunningAsync(Profile p, string dns, CancellationToken ct)
        {
            Starts++; IsRunning = true; ActiveProfileId = p.Id;
            Link = new("OpenVPN", 40, "10.77.0.2", "10.77.0.1", dns, Learned);
            return Task.FromResult(Link);
        }
        public Task EnsureStoppedAsync(CancellationToken ct)
        { Stops++; if (FailStop) throw new IOException("stop openvpn"); IsRunning = false; Link = null; return Task.CompletedTask; }
    }
    internal sealed class Tunnel : ITunnelHealthInspector
    {
        public TunStructuralStatus Status = TunStructuralStatus.Healthy;
        public int Waits;
        public Task<RouterHealth> InspectAsync(bool running, int port, bool tun, CancellationToken ct) =>
            Task.FromResult(new RouterHealth(running, true, true, Status == TunStructuralStatus.Healthy, 9, StructuralStatus: Status));
        public Task<RouterHealth> WaitForTunReadyAsync(Func<bool> running, Func<int> port, bool tun, TimeSpan timeout, CancellationToken ct)
        { Waits++; return InspectAsync(running(), port(), tun, ct); }
    }
    internal sealed class Fixture : IDisposable
    {
        public Profile A = Profile("A"), B = Profile("B"), O = new() { Name = "office", Protocol = "openvpn" };
        public Candidate9Tests.FakeDesiredProvider Desired;
        public Router Router = new();
        public Zapret Zapret = new();
        public OpenVpn OpenVpn = new();
        public Tunnel Tunnel = new();
        public TestClock Clock = new();
        public Candidate9Tests.FakePhysicalProvider Physical = new();
        public RuntimeConfigurationRepository Repo;
        public RuntimeCoordinator Coordinator;
        public List<string> Plans = [];
        public RouteObservationStatus Routes = RouteObservationStatus.Verified;
        public Fixture(SettingsStore store)
        {
            Repo = new(new AppSettings { Profiles = [A, B, O], MainProfileId = A.Id, OpenVpnProfileId = O.Id,
                ZapretStrategy = "general (ALT11).bat", Tun = true, AutoSwitch = true, FailureThreshold = 2 }, store);
            Desired = new(new DesiredRuntimeState { MainVpnEnabled = true, TunEnabled = true, ZapretEnabled = true,
                SelectedVpnProfileId = A.Id, SelectedOpenVpnProfileId = O.Id });
            Coordinator = new(Desired, Router, Zapret, OpenVpn)
            {
                GetSettings = () => Repo.CurrentSettings, PhysicalNetworkProvider = Physical, TunnelInspector = Tunnel, Clock = Clock,
                OnOpenVpnRoutesLearned = Repo.UpdateOpenVpnLearnedRoutesAsync,
                VerifyOpenVpnRoutesInRouteTable = (_, _) => new(Routes, []),
                Log = text => { if (text.StartsWith("PLAN ")) lock (Plans) Plans.Add(text); },
                BackoffIntervals = [TimeSpan.FromHours(1)]
            };
        }
        public Task Reconcile(ReconcileReason reason = ReconcileReason.UserChangedSettings) => Coordinator.ReconcileAsync(reason);
        public void Dispose() => Coordinator.Dispose();
    }

    internal sealed class TestClock : IClock
    {
        private readonly object gate = new();
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        private readonly List<(DateTimeOffset Due, TaskCompletionSource Source)> waits = [];
        public TaskCompletionSource DelayScheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset UtcNow { get { lock (gate) return now; } }
        public Task Delay(TimeSpan duration, CancellationToken ct)
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) waits.Add((now + duration, source));
            ct.Register(() => source.TrySetCanceled(ct));
            DelayScheduled.TrySetResult();
            return source.Task;
        }
        public void Advance(TimeSpan duration)
        {
            lock (gate)
            {
                now += duration;
                foreach (var wait in waits.Where(w => w.Due <= now).ToArray())
                { wait.Source.TrySetResult(); waits.Remove(wait); }
            }
        }
    }

    [Fact]
    public async Task StartupPreservesCandidate11ZapretRouterTunOrder()
    {
        using var f = new Fixture(Store);
        await f.Reconcile(ReconcileReason.Startup);
        Assert.Equal("PLAN [StartZapret,StartMainRouter,WaitTunReady]", Assert.Single(f.Plans));
        Assert.Equal(1, f.Router.Starts); Assert.Equal(1, f.Zapret.Starts); Assert.Equal(1, f.Tunnel.Waits);
        Assert.True(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
    }

    [Fact]
    public async Task ProfileAtoBtoAReconfiguresOnceEachWithoutTouchingOtherComponents()
    {
        using var f = new Fixture(Store); await f.Reconcile();
        foreach (var p in new[] { f.B, f.A })
        {
            var starts = f.Router.Starts;
            f.Desired.Current = f.Desired.Current with { SelectedVpnProfileId = p.Id };
            await f.Reconcile(ReconcileReason.UserSelectedVpnProfile);
            Assert.Equal(p.Id, f.Router.ActiveProfileId);
            Assert.Equal(starts + 1, f.Router.Starts);
            await f.Reconcile(); Assert.Equal(starts + 1, f.Router.Starts);
        }
        Assert.Equal(1, f.Zapret.Starts); Assert.Equal(0, f.OpenVpn.Starts); Assert.Equal(0, f.Router.Stops);
    }

    [Theory]
    [InlineData("general (ALT2).bat")]
    [InlineData("general (ALT3).bat")]
    [InlineData("general (ALT).bat")]
    public async Task StrategyChangeUsesDesiredStrategyOnce(string strategy)
    {
        using var f = new Fixture(Store); await f.Reconcile();
        await f.Repo.UpdateSettingsAsync(s => { s.ZapretStrategy = strategy; return s; });
        await f.Reconcile(ReconcileReason.ZapretConfigurationChanged); await f.Reconcile();
        Assert.Equal(strategy, f.Zapret.ActiveStrategy); Assert.Equal(2, f.Zapret.Starts); Assert.Equal(1, f.Router.Starts);
    }

    [Theory]
    [InlineData(TunStructuralStatus.TransientDegraded)]
    [InlineData(TunStructuralStatus.Unknown)]
    public async Task WeakTunObservationAndRecoveryHaveNoRuntimeActions(TunStructuralStatus status)
    {
        using var f = new Fixture(Store); await f.Reconcile();
        f.Tunnel.Status = status; await f.Reconcile();
        f.Tunnel.Status = TunStructuralStatus.Healthy; await f.Reconcile();
        Assert.Equal(1, f.Router.Starts); Assert.Equal(1, f.Zapret.Starts); Assert.Equal(0, f.OpenVpn.Starts);
        Assert.Equal(1, f.Tunnel.Waits); Assert.True(f.Desired.Current.TunEnabled);
    }

    [Fact]
    public void PersistentWeakProbesNeverInventStructuralEvidence()
    {
        var tracker = new TunHealthTracker();
        for (int i = 0; i < 10; i++)
            Assert.Equal(TunStructuralStatus.TransientDegraded, tracker.Evaluate(true, true, true, false, 9).StructuralStatus);
        Assert.Equal(TunStructuralStatus.Healthy, tracker.Evaluate(true, true, true, true, 9).StructuralStatus);
    }

    [Fact]
    public void MissingInterfaceAndRoutesRequireFreshConfirmationButProcessExitIsImmediate()
    {
        var tracker = new TunHealthTracker();
        Assert.Equal(TunStructuralStatus.TransientDegraded, tracker.Evaluate(true, false, false, false, 0).StructuralStatus);
        Assert.Equal(TunStructuralStatus.StructuralFailure, tracker.Evaluate(true, false, false, false, 0).StructuralStatus);
        Assert.Equal(TunStructuralStatus.Unknown, tracker.Evaluate(true, false, false, false, 0, routeObservationKnown: false).StructuralStatus);
        Assert.Equal(TunStructuralStatus.StructuralFailure, tracker.Evaluate(false, true, true, true, 9).StructuralStatus);
    }

    [Fact]
    public void UpstreamAndDnsFailuresAreNotTunStructuralFailure()
    {
        Assert.False(new RouterHealth(true, true, true, true, 9, VpnUpstreamHealthy: false, DnsPathHealthy: false, DirectPathHealthy: false).StructuralFailure);
    }

    [Fact]
    public async Task PhysicalLossPreservesIntentAndReturnUsesFreshIndex()
    {
        using var f = new Fixture(Store); await f.Reconcile();
        var desired = f.Desired.Current;
        f.Physical.Current = null; await f.Reconcile(ReconcileReason.PhysicalNetworkChanged);
        Assert.Equal(ConvergencePhase.WaitingForPhysicalNetwork, f.Coordinator.CurrentConvergenceState.Phase);
        Assert.Equal(desired, f.Desired.Current);
        f.Physical.Current = new("Wi-Fi", 22, "192.168.2.3", "1.1.1.1", []);
        using var monitor = new PhysicalNetworkMonitor(f.Coordinator, f.Repo);
        monitor.Signal(); await monitor.ObserveAsync();
        Assert.Equal(1, monitor.Generation);
        Assert.Equal(22, f.Router.ActivePhysical!.Index);
        Assert.Equal(2, f.Router.Starts); Assert.Equal(2, f.Zapret.Starts);
        Assert.Equal(ConvergencePhase.Converged, f.Coordinator.CurrentConvergenceState.Phase);
    }

    [Fact]
    public async Task OffCommandsStillStopComponentsWhileNetworkAbsent()
    {
        using var f = new Fixture(Store); await f.Reconcile();
        f.Physical.Current = null; f.Desired.Current = f.Desired.Current with { ZapretEnabled = false };
        await f.Reconcile(ReconcileReason.UserToggledZapret);
        Assert.Equal(1, f.Zapret.Stops); Assert.True(f.Desired.Current.MainVpnEnabled);
    }

    [Theory]
    [InlineData(RouteObservationStatus.Missing)]
    [InlineData(RouteObservationStatus.Unknown)]
    [InlineData(RouteObservationStatus.Error)]
    public async Task UnverifiedOpenVpnRoutesStayPendingWithoutReconnectingLink(RouteObservationStatus status)
    {
        using var f = new Fixture(Store); await f.Reconcile();
        f.Desired.Current = f.Desired.Current with { OpenVpnEnabled = true };
        f.Routes = status; await f.Reconcile();
        Assert.False(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
        Assert.Equal(1, f.OpenVpn.Starts);
        f.Routes = RouteObservationStatus.Verified; await f.Reconcile(ReconcileReason.ManualRetry);
        Assert.Equal(1, f.OpenVpn.Starts);
        Assert.True(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
        Assert.Equal(0, f.Coordinator.GetFailureCount(ComponentId.OpenVpnRoutes));
    }

    [Fact]
    public async Task OpenVpnLearnedRoutesPersistBeforeSingleOverlayReconfigure()
    {
        using var f = new Fixture(Store); await f.Reconcile();
        f.Desired.Current = f.Desired.Current with { OpenVpnEnabled = true };
        await f.Reconcile();
        Assert.Contains("10.77.0.0/16", Store.Load().Profiles.Single(p => p.Id == f.O.Id).LearnedRoutes);
        Assert.Contains("10.77.0.0/16", f.Router.Applied!.Profiles.Single(p => p.Id == f.O.Id).LearnedRoutes);
        Assert.Equal(1, f.Router.Starts); Assert.Equal(0, f.Router.Overlays); Assert.Equal(1, f.Router.GatewayUpdates);
        await f.Reconcile(); Assert.Equal(1, f.Router.GatewayUpdates);
    }

    [Fact]
    public async Task CombinedProfileAndOpenVpnChangePerformsOneRouterUpdate()
    {
        using var f = new Fixture(Store); await f.Reconcile();
        f.Desired.Current = f.Desired.Current with { OpenVpnEnabled = true, SelectedVpnProfileId = f.B.Id };
        await f.Reconcile();
        Assert.Equal(2, f.Router.Starts); Assert.Equal(0, f.Router.Overlays); Assert.Equal(1, f.Router.GatewayUpdates);
        Assert.Contains("10.77.0.0/16", f.Router.Applied!.Profiles.Single(p => p.Id == f.O.Id).LearnedRoutes);
    }

    [Theory]
    [InlineData(ComponentId.Zapret)]
    [InlineData(ComponentId.MainRouter)]
    [InlineData(ComponentId.OpenVpnLink)]
    public async Task StopFailureRetriesOnlyStopAndClearsFailureAfterSuccess(ComponentId component)
    {
        using var f = new Fixture(Store); f.Desired.Current = f.Desired.Current with { OpenVpnEnabled = true };
        await f.Reconcile();
        f.Zapret.FailStop = component == ComponentId.Zapret; f.Router.FailStop = component == ComponentId.MainRouter; f.OpenVpn.FailStop = component == ComponentId.OpenVpnLink;
        f.Desired.Current = component switch
        {
            ComponentId.Zapret => f.Desired.Current with { ZapretEnabled = false },
            // Turning off only the main VPN now retains the OpenVPN carrier.
            // A router stop test must also relinquish that independent owner.
            ComponentId.MainRouter => f.Desired.Current with { MainVpnEnabled = false, OpenVpnEnabled = false },
            _ => f.Desired.Current with { OpenVpnEnabled = false }
        };
        var overlays = f.Router.Overlays; var starts = f.Router.Starts;
        await f.Reconcile();
        Assert.Equal(1, f.Coordinator.GetFailureCount(component));
        if (component == ComponentId.OpenVpnLink) Assert.Equal(overlays, f.Router.Overlays);
        f.Zapret.FailStop = f.Router.FailStop = f.OpenVpn.FailStop = false;
        await f.Reconcile(ReconcileReason.ManualRetry);
        Assert.Equal(0, f.Coordinator.GetFailureCount(component)); Assert.Equal(starts, f.Router.Starts);
    }

    [Fact]
    public async Task RepositoryIsCopyOnWriteAndSubscriberFailureCannotBlockPersistence()
    {
        var repo = new RuntimeConfigurationRepository(new AppSettings(), Store);
        repo.ConfigurationChanged += _ => throw new InvalidOperationException("UI failed");
        repo.CurrentSettings.DirectDns = "mutated";
        Assert.NotEqual("mutated", repo.CurrentSettings.DirectDns);
        await Task.Run(() => repo.UpdateSettingsAsync(s => { s.DirectDns = "9.9.9.9"; return s; }));
        Assert.Equal("9.9.9.9", Store.Load().DirectDns);
        Assert.Equal("9.9.9.9", repo.CurrentSettings.DirectDns);
    }

    private sealed class FailingStore(string root) : SettingsStore(root)
    {
        public override Task SaveAsync(AppSettings settings) => throw new IOException("disk full");
    }
    [Fact]
    public async Task FailedPersistencePublishesNeitherNewStateNorEvent()
    {
        var repo = new RuntimeConfigurationRepository(new AppSettings { DirectDns = "1.1.1.1" }, new FailingStore(root));
        int events = 0; repo.ConfigurationChanged += _ => events++;
        await Assert.ThrowsAsync<IOException>(() => repo.UpdateSettingsAsync(s => { s.DirectDns = "9.9.9.9"; return s; }));
        Assert.Equal("1.1.1.1", repo.CurrentSettings.DirectDns); Assert.Equal(0, events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task WpfProfileSwitchWithRealCollectionViewAndBlockedProjection(bool preflightFails) => OnDispatcher(async () =>
    {
        using var f = new Fixture(Store);
        Store.SaveDesiredState(f.Desired.Current);
        using var native = new RouterService(root, Path.Combine(root, "runtime"))
        { PreflightOverride = (_, _, _) => Task.FromResult(new DelayResult(!preflightFails, 10, preflightFails ? "preflight failed" : "")) };
        using var vm = new MainViewModel(Store, f.Repo.CurrentSettings, native);
        // Replace component boundaries; run the actual VM command and its actual coordinator.
        typeof(RuntimeCoordinator).GetProperty(nameof(RuntimeCoordinator.RouterRuntime))!.SetValue(vm.RuntimeCoordinator, f.Router);
        typeof(RuntimeCoordinator).GetProperty(nameof(RuntimeCoordinator.ZapretRuntime))!.SetValue(vm.RuntimeCoordinator, f.Zapret);
        typeof(RuntimeCoordinator).GetProperty(nameof(RuntimeCoordinator.OpenVpnRuntime))!.SetValue(vm.RuntimeCoordinator, f.OpenVpn);
        typeof(RuntimeCoordinator).GetProperty(nameof(RuntimeCoordinator.PhysicalNetworkProvider))!.SetValue(vm.RuntimeCoordinator, f.Physical);
        typeof(RuntimeCoordinator).GetProperty(nameof(RuntimeCoordinator.TunnelInspector))!.SetValue(vm.RuntimeCoordinator, f.Tunnel);
        await vm.RuntimeCoordinator.ReconcileAsync(ReconcileReason.Startup);
        var view = CollectionViewSource.GetDefaultView(vm.Profiles);
        var uiThread = Environment.CurrentManagedThreadId;
        int projections = 0;
        vm.Profiles.CollectionChanged += (_, _) => { Assert.Equal(uiThread, Environment.CurrentManagedThreadId); projections++; };
        vm.ConfigRepository.ConfigurationChanged += _ => throw new InvalidOperationException("broken UI subscriber");
        vm.UserSelectedVpnProfile(f.B.Id);
        await vm.PendingSelection;
        await vm.RuntimeCoordinator.ReconcileAsync(ReconcileReason.UserSelectedVpnProfile);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(preflightFails ? f.A.Id : f.B.Id, vm.GetCurrentDesiredState().SelectedVpnProfileId);
        Assert.Equal(preflightFails ? f.A.Id : f.B.Id, f.Router.ActiveProfileId);
        Assert.Equal(preflightFails ? 1 : 2, f.Router.Starts);
        if (!preflightFails) Assert.True(projections > 0);
        GC.KeepAlive(view);
    });

    private static Task OnDispatcher(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task HeadlessFailoverIgnoresProjectionErrorsAndUsesCoordinatorOnly()
    {
        using var f = new Fixture(Store); await f.Reconcile();
        using var monitor = new VpnHealthMonitorService(f.Repo, f.Router, f.Coordinator, () => f.Desired.Current, update => f.Desired.Current = update(f.Desired.Current))
        {
            MeasureLatencyFunc = (_, _, _, _) => Task.FromResult(new DelayResult(false, -1, "timeout")),
            TestProfileFunc = (_, _, _) => Task.FromResult(new DelayResult(true, 20, ""))
        };
        monitor.HealthUpdated += (_, _) => throw new Exception("UI absent");
        await monitor.ProbeOnceAsync(); await monitor.ProbeOnceAsync();
        await f.Reconcile(ReconcileReason.VpnFailover);
        Assert.Equal(f.B.Id, f.Desired.Current.SelectedVpnProfileId);
        Assert.Equal(f.B.Id, f.Router.ActiveProfileId); Assert.Equal(2, f.Router.Starts);
        Assert.Equal(1, f.Zapret.Starts);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("reuse")]
    [InlineData("unknown-time")]
    [InlineData("unknown-path")]
    [InlineData("unknown-session")]
    [InlineData("other-command")]
    [InlineData("other-path")]
    public void ZapretOwnershipRequiresFullExactIdentity(string variant)
    {
        var time = DateTimeOffset.UtcNow;
        var owner = new ZapretOwnerRecord(100, time, 1, @"C:\NetCat\winws.exe", "hash", "instance");
        var process = new ZapretProcessInfo(100, owner.ExecutablePath, 1, time, false, "hash");
        process = variant switch
        {
            "reuse" => process with { StartTime = time.AddMilliseconds(1) },
            "unknown-time" => process with { StartTime = DateTimeOffset.MinValue },
            "unknown-path" => process with { ExecutablePath = null },
            "unknown-session" => process with { SessionId = -1 },
            "other-command" => process with { CommandFingerprint = "different" },
            "other-path" => process with { ExecutablePath = @"C:\Foreign\winws.exe" },
            _ => process
        };
        Assert.Equal(variant == "same", ZapretService.IsProcessOwned(process, owner, owner.ExecutablePath, 1));
    }

    [Theory]
    [InlineData("windivert initialized. capture is started.", true)]
    [InlineData("process is alive", false)]
    public async Task RealReadinessWaitRequiresCaptureMarker(string output, bool ready)
    {
        using var service = new ZapretService(root, Path.Combine(root, "zapret"));
        var host = (ProcessHost)typeof(ZapretService).GetField("process", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        host.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-Command", $"Write-Output '{output}'; Start-Sleep -Seconds 4"]);
        if (ready) await service.WaitForZapretReadyAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        else await Assert.ThrowsAsync<TimeoutException>(() => service.WaitForZapretReadyAsync(TimeSpan.FromMilliseconds(700), CancellationToken.None));
    }

    private sealed class Stats : ITrafficStatisticsProvider
    {
        public long Rx, Tx;
        public (long BytesReceived, long BytesSent)? GetInterfaceStatistics(int index) => (Rx, Tx);
        public string GetInterfaceName(int index) => "physical";
    }
    [Fact]
    public void PhysicalTrafficUsesMonotonicDeltasAndResetsOnChangeRollbackAndSleep()
    {
        var stats = new Stats();
        NetworkSnapshot? physical = new("Ethernet", 7, "192.168.1.2", "1.1.1.1", []);
        using var monitor = new TrafficMonitorService(() => physical, stats);
        long tick = Stopwatch.Frequency;
        monitor.Sample(tick); stats.Rx = 1_000_000; stats.Tx = 500_000;
        var sample = monitor.Sample(2 * tick);
        Assert.Equal(8, sample.DownloadMbps); Assert.Equal(4, sample.UploadMbps);
        physical = physical with { Index = 8 }; Assert.Equal(0, monitor.Sample(3 * tick).DownloadMbps);
        stats.Rx = stats.Tx = 0; Assert.Equal(0, monitor.Sample(4 * tick).DownloadMbps);
        stats.Rx = 1_000_000; Assert.Equal(0, monitor.Sample(10 * tick).DownloadMbps);
        physical = null; Assert.False(monitor.Sample(11 * tick).HasBaseline);
    }

    [Fact]
    public async Task TrafficSamplingContinuesWithoutAnyWindowAndStopsWithService()
    {
        var stats = new Stats();
        using var monitor = new TrafficMonitorService(() => new("Eth", 1, "192.168.1.2", "1.1.1.1", []), stats);
        var samples = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int count = 0;
        monitor.SampleUpdated += _ => { if (Interlocked.Increment(ref count) >= 3) samples.TrySetResult(); };
        monitor.StartSampling(TimeSpan.FromMilliseconds(15)); await samples.Task.WaitAsync(TimeSpan.FromSeconds(2));
        monitor.StopSampling(); Assert.True(count >= 3);
    }

    [Fact]
    public void StaticMutationAuthorityHasNoUiRuntimeEscapeHatch()
    {
        var repoRoot = RoutingTests.FindRoot();
        var vm = File.ReadAllText(Path.Combine(repoRoot, "src/NetCat.UI/MainViewModel.cs"));
        foreach (var call in new[] { "Router.ApplyAsync(", "Router.SetVpnAsync(", "Router.StopAllAsync(", "Zapret.ApplyAsync(", "Zapret.StopAsync(", "applyRuntime", "apply: async", "rollback: async" })
            Assert.DoesNotContain(call, vm);
        Assert.DoesNotContain("VM.SampleTraffic();", File.ReadAllText(Path.Combine(repoRoot, "src/NetCat.UI/MainWindow.xaml.cs")));
    }

    [Fact]
    public async Task FullCompositionCandidate12Scenario()
    {
        using var f = new Fixture(Store);
        await f.Reconcile(ReconcileReason.Startup);
        Assert.Equal("PLAN [StartZapret,StartMainRouter,WaitTunReady]", f.Plans[^1]);
        f.Desired.Current = f.Desired.Current with { SelectedVpnProfileId = f.B.Id };
        await f.Reconcile(ReconcileReason.UserSelectedVpnProfile);
        Assert.Equal("PLAN [EnsureMainRouterForProfileChange,WaitTunReady]", f.Plans[^1]);
        await f.Repo.UpdateSettingsAsync(s => { s.ZapretStrategy = "general (ALT2).bat"; return s; });
        await f.Reconcile(ReconcileReason.ZapretConfigurationChanged);
        Assert.Equal("PLAN [StartZapret]", f.Plans[^1]);
        f.Tunnel.Status = TunStructuralStatus.TransientDegraded; await f.Reconcile();
        Assert.Equal("PLAN []", f.Plans[^1]);
        f.Tunnel.Status = TunStructuralStatus.Healthy;
        f.Physical.Current = null; await f.Reconcile(ReconcileReason.PhysicalNetworkChanged);
        Assert.Equal("PLAN [WaitForPhysicalNetwork]", f.Plans[^1]);
        f.Physical.Current = new("Wi-Fi", 25, "192.168.2.7", "1.1.1.1", []);
        await f.Reconcile(ReconcileReason.PhysicalNetworkChanged);
        Assert.Equal("PLAN [StartZapret,EnsureMainRouterForPhysicalBinding,WaitTunReady]", f.Plans[^1]);
        f.Zapret.IsRunning = false; await f.Reconcile(ReconcileReason.ZapretProcessExited);
        Assert.Equal("PLAN [StartZapret]", f.Plans[^1]);
        f.Desired.Current = f.Desired.Current with { OpenVpnEnabled = true };
        await f.Reconcile(ReconcileReason.OpenVpnStateChanged);
        Assert.Equal("PLAN [StartOpenVpn,FinalizeRouting]", f.Plans[^1]);
        Assert.Contains("10.77.0.0/16", Store.Load().Profiles.Single(p => p.Id == f.O.Id).LearnedRoutes);
        using var health = new VpnHealthMonitorService(f.Repo, f.Router, f.Coordinator, () => f.Desired.Current, update => f.Desired.Current = update(f.Desired.Current))
        {
            MeasureLatencyFunc = (_, _, _, _) => Task.FromResult(new DelayResult(false, -1, "upstream timeout")),
            TestProfileFunc = (_, _, _) => Task.FromResult(new DelayResult(true, 20, ""))
        };
        await health.ProbeOnceAsync(); await health.ProbeOnceAsync();
        await f.Reconcile(ReconcileReason.VpnFailover);
        Assert.Equal(f.A.Id, f.Router.ActiveProfileId);
        await f.Reconcile(); Assert.Equal("PLAN []", f.Plans[^1]);
        Assert.True(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
        Assert.Equal(1, f.OpenVpn.Starts); Assert.Equal(0, f.Router.Overlays); Assert.Equal(1, f.Router.GatewayUpdates);
    }

    [Fact]
    public async Task RealEventWakeRestoresHeadlessWithoutWaitingForFallback()
    {
        using var f = new Fixture(Store);
        f.Physical.Current = null;
        await f.Reconcile(ReconcileReason.Startup);
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Coordinator.ConvergenceStateChanged += state => { if (state.DesiredSatisfied) restored.TrySetResult(); };
        using var monitor = new PhysicalNetworkMonitor(f.Coordinator, f.Repo);
        monitor.Start();
        f.Physical.Current = new("Wi-Fi", 29, "192.168.4.2", "1.1.1.1", []);
        monitor.Signal();
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(29, f.Router.ActivePhysical!.Index);
        Assert.Equal(1, f.Router.Starts); Assert.Equal(1, f.Zapret.Starts);
    }

    [Fact]
    public async Task PhysicalChangeDuringStartCannotPublishStaleConvergence()
    {
        using var f = new Fixture(Store);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Router.BeforeStart = async () => { entered.TrySetResult(); await release.Task; };
        var stale = f.Reconcile(ReconcileReason.Startup);
        await entered.Task;
        f.Physical.Current = new("Fresh", 31, "192.168.5.2", "1.1.1.1", []);
        release.TrySetResult(); await stale;
        f.Router.BeforeStart = null;
        await f.Reconcile(ReconcileReason.PhysicalNetworkChanged);
        Assert.Equal(31, f.Router.ActivePhysical!.Index);
        Assert.True(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
    }

    [Fact]
    public async Task ZapretUnexpectedExitEventRestoresOnlyZapret()
    {
        using var f = new Fixture(Store); await f.Reconcile();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Coordinator.ConvergenceStateChanged += state => { if (state.DesiredSatisfied && f.Zapret.Starts == 2) recovered.TrySetResult(); };
        f.Zapret.Crash();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, f.Router.Starts); Assert.Equal(0, f.OpenVpn.Starts);
    }

    [Fact]
    public Task BlockedUiDispatcherDoesNotBlockLiveProfileConvergence() => OnDispatcher(async () =>
    {
        using var f = new Fixture(Store); Store.SaveDesiredState(f.Desired.Current);
        using var native = new RouterService(root, Path.Combine(root, "runtime"))
        { PreflightOverride = (_, _, _) => Task.FromResult(new DelayResult(true, 1, "")) };
        using var vm = new MainViewModel(Store, f.Repo.CurrentSettings, native);
        foreach (var (name, value) in new (string, object)[] { (nameof(RuntimeCoordinator.RouterRuntime), f.Router),
            (nameof(RuntimeCoordinator.ZapretRuntime), f.Zapret), (nameof(RuntimeCoordinator.OpenVpnRuntime), f.OpenVpn),
            (nameof(RuntimeCoordinator.PhysicalNetworkProvider), f.Physical), (nameof(RuntimeCoordinator.TunnelInspector), f.Tunnel) })
            typeof(RuntimeCoordinator).GetProperty(name)!.SetValue(vm.RuntimeCoordinator, value);
        await vm.RuntimeCoordinator.ReconcileAsync(ReconcileReason.Startup);
        var view = CollectionViewSource.GetDefaultView(vm.Profiles);
        vm.UserSelectedVpnProfile(f.B.Id);
        // Deliberately stop pumping the owning Dispatcher. Runtime must still complete.
        vm.PendingSelection.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        Assert.Equal(f.B.Id, f.Router.ActiveProfileId);
        Assert.Equal(f.B.Id, vm.ConfigRepository.CurrentSettings.MainProfileId);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(f.B.Id, vm.State.MainProfileId);
        GC.KeepAlive(view);
    });

    [Fact]
    public async Task ProcessExitBeforeReadyIsFailureAndCancellationReleasesReadinessWait()
    {
        using var service = new ZapretService(root, Path.Combine(root, "zapret"));
        var host = (ProcessHost)typeof(ZapretService).GetField("process", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        host.Start(ProcessHost.PowerShellPath, ["-NoProfile", "-NonInteractive", "-Command", "exit 1"]);
        await Assert.ThrowsAsync<IOException>(() => service.WaitForZapretReadyAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
        await host.StopAsync();
        host.Start(ProcessHost.PowerShellPath, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 5"]);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.WaitForZapretReadyAsync(TimeSpan.FromSeconds(3), cancelled.Token));
    }

    [Fact]
    public async Task GenericConfigurationCommitDoesNotChangeDesiredTun()
    {
        Store.SaveDesiredState(new DesiredRuntimeState { TunEnabled = true });
        using var vm = new MainViewModel(Store, new AppSettings { Tun = true });
        await vm.UpdateSettingsAsync(s => s.Tun = false);
        Assert.True(vm.GetCurrentDesiredState().TunEnabled);
        Assert.True(Store.LoadDesiredState().TunEnabled);
    }

    [Fact]
    public async Task FailedCleanupAfterProcessExitIsRetriedWithClockBackoff()
    {
        using var f = new Fixture(Store); await f.Reconcile();
        f.Coordinator.BackoffIntervals = [TimeSpan.FromSeconds(1)];
        f.Desired.Current = f.Desired.Current with { MainVpnEnabled = false };
        f.Router.FailStop = true; f.Router.ExitBeforeStopFailure = true;
        await f.Reconcile();
        await f.Clock.DelayScheduled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, f.Router.Stops);
        Assert.False(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
        var converged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Coordinator.ConvergenceStateChanged += state => { if (state.DesiredSatisfied) converged.TrySetResult(); };
        f.Router.FailStop = false;
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        await converged.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, f.Router.Stops); Assert.Equal(1, f.Router.Starts);
        Assert.Equal(0, f.Coordinator.GetFailureCount(ComponentId.MainRouter));
    }
}
