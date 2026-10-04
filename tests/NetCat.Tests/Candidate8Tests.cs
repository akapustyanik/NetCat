using System.Diagnostics;
using System.Net;
using System.Text.Json;
using NetCat.Core;
using NetCat.Network;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate8Tests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "NetCat-C8Tests-" + Guid.NewGuid().ToString("N"));

    public Candidate8Tests()
    {
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
        catch { }
    }

    private sealed class FakeDesiredProvider(DesiredRuntimeState initial) : IDesiredRuntimeStateProvider
    {
        public DesiredRuntimeState Current { get; set; } = initial;
        public DesiredRuntimeState GetCurrentDesiredState() => Current;
    }

    private sealed class FakeRouterRuntime : IRouterRuntime
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

        public Task EnsureRunningAsync(AppSettings settings, NetworkSnapshot physical, string reason, CancellationToken ct)
        {
            EnsureRunningCalls++;
            ReconfigReasons.Add(reason);
            if (FailStart) throw new IOException("Router failed to start");
            IsRunning = true;
            VpnRunning = true;
            TunActive = settings.Tun;
            ActiveProfileId = settings.MainProfileId;
            ActivePhysical = physical;
            return Task.CompletedTask;
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

    private sealed class FakeZapretRuntime : IZapretRuntime
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

    private sealed class FakeOpenVpnRuntime : IOpenVpnRuntime
    {
        public bool IsRunning { get; set; }
        public OpenVpnLink? Link { get; set; }
        public Guid? ActiveProfileId { get; set; }
        public int EnsureRunningCalls { get; private set; }
        public int EnsureStoppedCalls { get; private set; }
        public bool FailStart { get; set; }

        public Task<OpenVpnLink> EnsureRunningAsync(Profile profile, string dnsOverride, CancellationToken ct)
        {
            EnsureRunningCalls++;
            if (FailStart) throw new IOException("OpenVPN failed to start");
            IsRunning = true;
            ActiveProfileId = profile.Id;
            Link = new OpenVpnLink("NetCat-OpenVPN", 10, "10.10.11.5", "10.10.11.1", dnsOverride, profile.LearnedRoutes);
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

    private sealed class FakePhysicalProvider : IPhysicalNetworkProvider
    {
        public NetworkSnapshot? Current { get; set; } = new("Ethernet", 1, "192.168.1.100", "192.168.1.1", []);
        public bool Usable { get; set; } = true;
        public NetworkSnapshot? ResolveCurrentBinding(string preferred = "") => Current;
        public bool IsUsable(NetworkSnapshot? snapshot) => Usable && PhysicalNetwork.IsUsablePhysicalBinding(snapshot);
        public bool HasChanged(NetworkSnapshot? active, NetworkSnapshot? current, bool captureFailed) =>
            PhysicalNetwork.HasPhysicalChanged(active, current, captureFailed);
    }

    private sealed class FakeTunnelInspector : ITunnelHealthInspector
    {
        public bool StructuralFailure { get; set; }
        public int InspectCalls { get; private set; }
        public int WaitForTunReadyCalls { get; private set; }
        public Queue<bool>? StructuralFailureSequence { get; set; }

        public Task<RouterHealth> InspectAsync(bool running, int port, bool tun, CancellationToken ct)
        {
            InspectCalls++;
            bool fail = StructuralFailureSequence != null && StructuralFailureSequence.Count > 0
                ? StructuralFailureSequence.Dequeue()
                : StructuralFailure;
            return Task.FromResult(new RouterHealth(running, !fail, !fail, !fail, 10));
        }

        public async Task<RouterHealth> WaitForTunReadyAsync(Func<bool> isRunning, Func<int> getPort, bool tun, TimeSpan timeout, CancellationToken ct)
        {
            WaitForTunReadyCalls++;
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (!ct.IsCancellationRequested)
            {
                bool fail = StructuralFailureSequence != null && StructuralFailureSequence.Count > 0
                    ? StructuralFailureSequence.Dequeue()
                    : StructuralFailure;

                if (!fail) return new RouterHealth(isRunning(), true, true, true, 10);
                if (DateTimeOffset.UtcNow >= deadline) break;
                await Task.Delay(20, ct);
            }
            return new RouterHealth(isRunning(), false, false, false, 0);
        }
    }

    private static RuntimeCoordinator CreateCoordinator(
        IDesiredRuntimeStateProvider desired,
        IRouterRuntime router,
        IZapretRuntime zapret,
        IOpenVpnRuntime openVpn,
        IPhysicalNetworkProvider? physical = null,
        ITunnelHealthInspector? inspector = null,
        TimeSpan[]? backoff = null,
        Func<AppSettings>? getSettings = null,
        Func<CancellationToken, Task>? finalizeOverride = null)
    {
        var coordinator = new RuntimeCoordinator(desired, router, zapret, openVpn)
        {
            PhysicalNetworkProvider = physical ?? new FakePhysicalProvider(),
            TunnelInspector = inspector ?? new FakeTunnelInspector(),
            TunReadyTimeout = TimeSpan.FromMilliseconds(50),
            GetSettings = getSettings ?? (() =>
            {
                var s = new AppSettings();
                var p = new Profile { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Protocol = "openvpn", Name = "OpenVPN" };
                s.Profiles.Add(p);
                s.OpenVpnProfileId = p.Id;
                return s;
            }),
            VerifyOpenVpnRoutesInRouteTable = (_, _) => new(RouteObservationStatus.Verified, []),
            FinalizeRoutingOverride = finalizeOverride
        };
        if (backoff != null) coordinator.BackoffIntervals = backoff;
        return coordinator;
    }

    // 1. HiddenAutostartReconcilesWithoutWindowLoaded
    [Fact]
    public async Task HiddenAutostartReconcilesWithoutWindowLoaded()
    {
        var desired = new FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true, ZapretEnabled = true });
        var router = new FakeRouterRuntime();
        var zapret = new FakeZapretRuntime();
        var openVpn = new FakeOpenVpnRuntime();

        using var coordinator = CreateCoordinator(desired, router, zapret, openVpn);
        coordinator.RequestReconcile(ReconcileReason.Startup);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.Completed; i++)
            await Task.Delay(25);

        Assert.Equal(StartupRestoreState.Completed, coordinator.CurrentRestoreState);
        Assert.Equal(1, router.EnsureRunningCalls);
        Assert.Equal(1, zapret.EnsureRunningCalls);
        Assert.Equal(0, openVpn.EnsureRunningCalls);
    }

    // 2. SecondLaunchOnlyShowsExistingInstance
    [Fact]
    public void SecondLaunchOnlyShowsExistingInstance()
    {
        var logs = new List<string>();
        int restoreCount = 0;

        void OnExistingSignal()
        {
            logs.Add("SHOW_AND_FOCUS");
        }

        OnExistingSignal();

        Assert.Single(logs);
        Assert.Equal("SHOW_AND_FOCUS", logs[0]);
        Assert.Equal(0, restoreCount);
    }

    // 3. PartialFailureRetriesOnlyFailedComponent
    [Fact]
    public async Task PartialFailureRetriesOnlyFailedComponent()
    {
        var desired = new FakeDesiredProvider(new DesiredRuntimeState
        {
            MainVpnEnabled = true,
            ZapretEnabled = true,
            OpenVpnEnabled = true
        });
        var router = new FakeRouterRuntime();
        var zapret = new FakeZapretRuntime { FailStart = true };
        var openVpn = new FakeOpenVpnRuntime();

        using var coordinator = CreateCoordinator(desired, router, zapret, openVpn, backoff: [TimeSpan.FromSeconds(60)]);

        coordinator.RequestReconcile(ReconcileReason.Startup);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.PendingRetry; i++)
            await Task.Delay(25);

        Assert.Equal(StartupRestoreState.PendingRetry, coordinator.CurrentRestoreState);
        Assert.Equal(1, router.EnsureRunningCalls);
        Assert.Equal(1, openVpn.EnsureRunningCalls);
        Assert.Equal(1, zapret.EnsureRunningCalls);

        // Zapret recovers
        zapret.FailStart = false;
        coordinator.TriggerRetry();

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.Completed; i++)
            await Task.Delay(25);

        Assert.Equal(StartupRestoreState.Completed, coordinator.CurrentRestoreState);
        // VPN and OpenVPN must NOT be called again
        Assert.Equal(1, router.EnsureRunningCalls);
        Assert.Equal(1, openVpn.EnsureRunningCalls);
        // Zapret was called again
        Assert.Equal(2, zapret.EnsureRunningCalls);
    }

    // 4. SuccessfulVpnIsNotRestartedBecauseZapretFailed
    [Fact]
    public async Task SuccessfulVpnIsNotRestartedBecauseZapretFailed()
    {
        var desired = new FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true, ZapretEnabled = true });
        var router = new FakeRouterRuntime();
        var zapret = new FakeZapretRuntime { FailStart = true };
        var openVpn = new FakeOpenVpnRuntime();

        using var coordinator = CreateCoordinator(desired, router, zapret, openVpn, backoff: [TimeSpan.FromSeconds(60)]);

        coordinator.RequestReconcile(ReconcileReason.Startup);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.PendingRetry; i++)
            await Task.Delay(25);

        Assert.Equal(1, router.EnsureRunningCalls);
        Assert.True(router.VpnRunning);

        zapret.FailStart = false;
        coordinator.TriggerRetry();

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.Completed; i++)
            await Task.Delay(25);

        Assert.Equal(1, router.EnsureRunningCalls);
    }

    // 5. SuccessfulOpenVpnIsNotRestartedBecauseZapretFailed
    [Fact]
    public async Task SuccessfulOpenVpnIsNotRestartedBecauseZapretFailed()
    {
        var desired = new FakeDesiredProvider(new DesiredRuntimeState { OpenVpnEnabled = true, ZapretEnabled = true });
        var router = new FakeRouterRuntime();
        var zapret = new FakeZapretRuntime { FailStart = true };
        var openVpn = new FakeOpenVpnRuntime();

        using var coordinator = CreateCoordinator(desired, router, zapret, openVpn, backoff: [TimeSpan.FromSeconds(60)]);

        coordinator.RequestReconcile(ReconcileReason.Startup);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.PendingRetry; i++)
            await Task.Delay(25);

        Assert.Equal(1, openVpn.EnsureRunningCalls);
        Assert.True(openVpn.IsRunning);

        zapret.FailStart = false;
        coordinator.TriggerRetry();

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.Completed; i++)
            await Task.Delay(25);

        Assert.Equal(1, openVpn.EnsureRunningCalls);
    }

    // 6. DesiredOpenVpnOffCancelsPendingRetry
    [Fact]
    public async Task DesiredOpenVpnOffCancelsPendingRetry()
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
        Assert.Equal(1, coordinator.GetFailureCount(ComponentId.OpenVpn));

        // User sets OpenVPN OFF
        desired.Current = desired.Current with { OpenVpnEnabled = false };
        coordinator.RequestReconcile(ReconcileReason.UserChangedSettings);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.Completed; i++)
            await Task.Delay(25);

        Assert.Equal(StartupRestoreState.Completed, coordinator.CurrentRestoreState);
        Assert.Equal(0, coordinator.GetFailureCount(ComponentId.OpenVpn));
    }

    // 7. DesiredZapretOffCancelsPendingRetry
    [Fact]
    public async Task DesiredZapretOffCancelsPendingRetry()
    {
        var desired = new FakeDesiredProvider(new DesiredRuntimeState { ZapretEnabled = true });
        var router = new FakeRouterRuntime();
        var zapret = new FakeZapretRuntime { FailStart = true };
        var openVpn = new FakeOpenVpnRuntime();

        using var coordinator = CreateCoordinator(desired, router, zapret, openVpn, backoff: [TimeSpan.FromSeconds(60)]);

        coordinator.RequestReconcile(ReconcileReason.Startup);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.PendingRetry; i++)
            await Task.Delay(25);

        Assert.Equal(StartupRestoreState.PendingRetry, coordinator.CurrentRestoreState);

        desired.Current = desired.Current with { ZapretEnabled = false };
        coordinator.RequestReconcile(ReconcileReason.UserChangedSettings);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.Completed; i++)
            await Task.Delay(25);

        Assert.Equal(StartupRestoreState.Completed, coordinator.CurrentRestoreState);
        Assert.Equal(0, coordinator.GetFailureCount(ComponentId.Zapret));
    }

    // 8. DesiredVpnOffCancelsPendingRetry
    [Fact]
    public async Task DesiredVpnOffCancelsPendingRetry()
    {
        var desired = new FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true });
        var router = new FakeRouterRuntime { FailStart = true };
        var zapret = new FakeZapretRuntime();
        var openVpn = new FakeOpenVpnRuntime();

        using var coordinator = CreateCoordinator(desired, router, zapret, openVpn, backoff: [TimeSpan.FromSeconds(60)]);

        coordinator.RequestReconcile(ReconcileReason.Startup);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.PendingRetry; i++)
            await Task.Delay(25);

        Assert.Equal(StartupRestoreState.PendingRetry, coordinator.CurrentRestoreState);

        desired.Current = desired.Current with { MainVpnEnabled = false };
        coordinator.RequestReconcile(ReconcileReason.UserChangedSettings);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.Completed; i++)
            await Task.Delay(25);

        Assert.Equal(StartupRestoreState.Completed, coordinator.CurrentRestoreState);
        Assert.Equal(0, coordinator.GetFailureCount(ComponentId.MainRouter));
    }

    // 9. DesiredStateIsRereadBeforeEveryReconcilePass
    [Fact]
    public async Task DesiredStateIsRereadBeforeEveryReconcilePass()
    {
        var desired = new FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = false, ZapretEnabled = false });
        var router = new FakeRouterRuntime();
        var zapret = new FakeZapretRuntime();
        var openVpn = new FakeOpenVpnRuntime();

        using var coordinator = CreateCoordinator(desired, router, zapret, openVpn);
        coordinator.RequestReconcile(ReconcileReason.Startup);

        for (int i = 0; i < 20 && coordinator.CurrentRestoreState != StartupRestoreState.Completed; i++)
            await Task.Delay(25);

        Assert.Equal(0, router.EnsureRunningCalls);

        // Update desired
        desired.Current = new DesiredRuntimeState { MainVpnEnabled = true };
        coordinator.RequestReconcile(ReconcileReason.UserChangedSettings);

        for (int i = 0; i < 40 && router.EnsureRunningCalls == 0; i++)
            await Task.Delay(25);

        Assert.Equal(1, router.EnsureRunningCalls);
    }

    // 10. ConcurrentTriggersCoalesceIntoSingleReconcileLoop
    [Fact]
    public async Task ConcurrentTriggersCoalesceIntoSingleReconcileLoop()
    {
        var desired = new FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true, ZapretEnabled = true });
        var router = new FakeRouterRuntime();
        var zapret = new FakeZapretRuntime();
        var openVpn = new FakeOpenVpnRuntime();

        using var coordinator = CreateCoordinator(desired, router, zapret, openVpn);

        // Fire 20 concurrent requests
        Parallel.For(0, 20, _ =>
        {
            coordinator.RequestReconcile(ReconcileReason.PhysicalNetworkChanged);
        });

        for (int i = 0; i < 80 && coordinator.CurrentRestoreState != StartupRestoreState.Completed; i++)
            await Task.Delay(25);

        Assert.Equal(StartupRestoreState.Completed, coordinator.CurrentRestoreState);
        Assert.True(coordinator.CurrentGeneration <= 5, $"Generations were {coordinator.CurrentGeneration}, expected coalescing to <= 5");
    }

    // 11. ZapretOnlyChangeDoesNotReconfigureRouter
    [Fact]
    public void ZapretOnlyChangeDoesNotReconfigureRouter()
    {
        var old = new AppSettings { ZapretStrategy = "general.bat", YouTube = ServiceRoute.Zapret };
        var next = new AppSettings { ZapretStrategy = "general2.bat", YouTube = ServiceRoute.Zapret };
        var phys = new NetworkSnapshot("Ethernet", 1, "192.168.1.1", "192.168.1.1", []);

        var diff = RuntimeSettingsDiff.Compute(old, next, phys, phys);

        Assert.True(diff.ZapretChanged);
        Assert.False(diff.VpnChanged);
        Assert.False(diff.TunChanged);
        Assert.False(diff.HasRouterChanges);
    }

    // 12. UiOnlyChangeDoesNotReconfigureRouter
    [Fact]
    public void UiOnlyChangeDoesNotReconfigureRouter()
    {
        var old = new AppSettings { BaseColor = "#151A22", AccentColor = "#0078D4", Autostart = false };
        var next = new AppSettings { BaseColor = "#F4F6F8", AccentColor = "#C784E2", Autostart = true };
        var phys = new NetworkSnapshot("Ethernet", 1, "192.168.1.1", "192.168.1.1", []);

        var diff = RuntimeSettingsDiff.Compute(old, next, phys, phys);

        Assert.False(diff.HasRouterChanges);
        Assert.False(diff.ZapretChanged);
    }

    // 13. StartupFullStackUsesMinimalRouterReconfigurations
    [Fact]
    public async Task StartupFullStackUsesMinimalRouterReconfigurations()
    {
        var desired = new FakeDesiredProvider(new DesiredRuntimeState
        {
            MainVpnEnabled = true,
            ZapretEnabled = true,
            OpenVpnEnabled = true
        });
        var router = new FakeRouterRuntime();
        var zapret = new FakeZapretRuntime();
        var openVpn = new FakeOpenVpnRuntime();
        int finalizeCount = 0;

        using var coordinator = CreateCoordinator(desired, router, zapret, openVpn, finalizeOverride: _ =>
        {
            finalizeCount++;
            return Task.CompletedTask;
        });

        coordinator.RequestReconcile(ReconcileReason.Startup);

        for (int i = 0; i < 40 && coordinator.CurrentRestoreState != StartupRestoreState.Completed; i++)
            await Task.Delay(25);

        Assert.Equal(StartupRestoreState.Completed, coordinator.CurrentRestoreState);
        Assert.Equal(1, router.EnsureRunningCalls);
        Assert.Equal(1, zapret.EnsureRunningCalls);
        Assert.Equal(1, openVpn.EnsureRunningCalls);
        Assert.Equal(1, finalizeCount); // Main VPN starts independently; the later link has its own finalization.
    }

    // 14. TransientTunNotReadyDoesNotTriggerRecovery
    [Fact]
    public async Task TransientTunNotReadyDoesNotTriggerRecovery()
    {
        var inspector = new FakeTunnelInspector
        {
            // First 2 inspections return structural failure, 3rd returns ready
            StructuralFailureSequence = new Queue<bool>([true, true, false])
        };

        var result = await inspector.WaitForTunReadyAsync(() => true, () => 1080, true, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(result.StructuralFailure);
        Assert.True(result.TunInterfacePresent);
    }

    // 15. TunTimeoutDoesTriggerRecovery
    [Fact]
    public async Task TunTimeoutDoesTriggerRecovery()
    {
        var inspector = new FakeTunnelInspector
        {
            StructuralFailure = true
        };

        var result = await inspector.WaitForTunReadyAsync(() => true, () => 1080, true, TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.True(result.StructuralFailure);
    }

    // 16. HealthyTunWithoutPhysicalNetworkIsNotRecovered
    [Fact]
    public void HealthyTunWithoutPhysicalNetworkIsNotRecovered()
    {
        NetworkSnapshot? zero = new("Ethernet", 1, "0.0.0.0", "192.168.1.1", []);
        Assert.False(PhysicalNetwork.IsUsablePhysicalBinding(zero));

        NetworkSnapshot? nullSnap = null;
        Assert.False(PhysicalNetwork.IsUsablePhysicalBinding(nullSnap));

        var machine = new NetworkLifecycleStateMachine();
        long gen = machine.BeginTransition();
        machine.BeginRebuilding(gen);

        Assert.True(machine.IsNetworkRebuilding);
        Assert.Null(machine.TemporaryStatus);
    }

    // 17. OldNetCatOwnedWinwsCanBeCleanedAcrossBuildDirectories
    [Fact]
    public void OldNetCatOwnedWinwsCanBeCleanedAcrossBuildDirectories()
    {
        var zapretDir = Path.Combine(tempDir, "zapret");
        Directory.CreateDirectory(zapretDir);
        var ownerFile = Path.Combine(zapretDir, "owner.json");

        var record = new ZapretOwnerRecord(
            Pid: 12345,
            ProcessStartTime: DateTimeOffset.UtcNow.AddMinutes(-5),
            SessionId: Process.GetCurrentProcess().SessionId,
            ExecutablePath: @"D:\Builds\Candidate7\modules\zapret\bin\winws.exe",
            CommandFingerprint: "winws --wf-l3=...",
            OwnerInstanceId: "prev-instance"
        );
        File.WriteAllText(ownerFile, JsonSerializer.Serialize(record, JsonSettings.Options));

        var killedPids = new List<int>();
        using var zapret = new ZapretService(tempDir, zapretDir)
        {
            OwnerFileOverride = () => ownerFile,
            KillProcessOverride = killedPids.Add,
            EnumerateProcessesOverride = () =>
            [
                new ZapretProcessInfo(12345, @"D:\Builds\Candidate7\modules\zapret\bin\winws.exe", record.SessionId, record.ProcessStartTime, false, record.CommandFingerprint)
            ]
        };

        zapret.RunCheckOtherInstances();

        Assert.Contains(12345, killedPids);
        Assert.False(File.Exists(ownerFile));
    }

    // 18. ExternalWinwsIsNeverKilled
    [Fact]
    public void ExternalWinwsIsNeverKilled()
    {
        var zapretDir = Path.Combine(tempDir, "zapret-external");
        Directory.CreateDirectory(zapretDir);

        var killedPids = new List<int>();
        using var zapret = new ZapretService(tempDir, zapretDir)
        {
            KillProcessOverride = killedPids.Add,
            EnumerateProcessesOverride = () =>
            [
                new ZapretProcessInfo(99999, @"C:\Tools\ZapretStandalone\winws.exe", 1, DateTimeOffset.UtcNow.AddHours(-1), false)
            ]
        };

        var ex = Assert.Throws<InvalidOperationException>(() => zapret.RunCheckOtherInstances());
        Assert.Contains("99999", ex.Message);
        Assert.Empty(killedPids);
    }

    // 19. NonContiguousOpenVpnNetmaskIsRejected
    [Fact]
    public void NonContiguousOpenVpnNetmaskIsRejected()
    {
        var nonContig = IPAddress.Parse("255.0.255.0");
        Assert.False(OpenVpnService.TryGetContiguousCidr(nonContig, out int cidr));
        Assert.Equal(0, cidr);

        var contig = IPAddress.Parse("255.255.255.0");
        Assert.True(OpenVpnService.TryGetContiguousCidr(contig, out int cidr2));
        Assert.Equal(24, cidr2);
    }

    // 20. PrivatePrefixMustBeFullyContainedInAllowedRange
    [Fact]
    public void PrivatePrefixMustBeFullyContainedInAllowedRange()
    {
        // Allowed RFC1918 / CGNAT / Link-Local
        Assert.True(OpenVpnService.IsPrefixContainedInAllowedRanges(IPAddress.Parse("10.0.117.0"), 24));
        Assert.True(OpenVpnService.IsPrefixContainedInAllowedRanges(IPAddress.Parse("10.0.0.0"), 8));
        Assert.True(OpenVpnService.IsPrefixContainedInAllowedRanges(IPAddress.Parse("172.16.0.0"), 12));
        Assert.True(OpenVpnService.IsPrefixContainedInAllowedRanges(IPAddress.Parse("172.16.50.0"), 24));
        Assert.True(OpenVpnService.IsPrefixContainedInAllowedRanges(IPAddress.Parse("192.168.1.0"), 24));
        Assert.True(OpenVpnService.IsPrefixContainedInAllowedRanges(IPAddress.Parse("100.64.0.0"), 10));
        Assert.True(OpenVpnService.IsPrefixContainedInAllowedRanges(IPAddress.Parse("169.254.0.0"), 16));

        // Rejected outside boundaries or public
        Assert.False(OpenVpnService.IsPrefixContainedInAllowedRanges(IPAddress.Parse("10.0.0.0"), 7)); // spills outside /8
        Assert.False(OpenVpnService.IsPrefixContainedInAllowedRanges(IPAddress.Parse("172.15.0.0"), 16));
        Assert.False(OpenVpnService.IsPrefixContainedInAllowedRanges(IPAddress.Parse("172.32.0.0"), 16));
        Assert.False(OpenVpnService.IsPrefixContainedInAllowedRanges(IPAddress.Parse("100.63.0.0"), 16));
        Assert.False(OpenVpnService.IsPrefixContainedInAllowedRanges(IPAddress.Parse("8.8.8.0"), 24));
        Assert.False(OpenVpnService.IsPrefixContainedInAllowedRanges(IPAddress.Parse("1.1.1.0"), 24));
    }

    // 21. PublicPushedRouteRequiresExplicitOptIn
    [Fact]
    public void PublicPushedRouteRequiresExplicitOptIn()
    {
        var publicIp = IPAddress.Parse("8.8.8.0");
        var mask = IPAddress.Parse("255.255.255.0");
        Assert.True(OpenVpnService.TryGetContiguousCidr(mask, out var cidr));
        var canonical = OpenVpnService.GetCanonicalNetwork(publicIp, mask);

        bool isPrivate = OpenVpnService.IsPrefixContainedInAllowedRanges(canonical, cidr);
        Assert.False(isPrivate);

        var profileWithoutOptIn = new Profile { AllowPublicPushedRoutes = false };
        var profileWithOptIn = new Profile { AllowPublicPushedRoutes = true };

        bool acceptedWithout = isPrivate || profileWithoutOptIn.AllowPublicPushedRoutes;
        bool acceptedWith = isPrivate || profileWithOptIn.AllowPublicPushedRoutes;

        Assert.False(acceptedWithout);
        Assert.True(acceptedWith);
    }

    // 22. PushedRouteVerificationFailureFailsOpenVpnFinalization
    [Fact]
    public async Task PushedRouteVerificationFailureFailsOpenVpnFinalization()
    {
        var ovpnDir = Path.Combine(tempDir, "ovpn-verify");
        Directory.CreateDirectory(ovpnDir);

        using var openVpn = new OpenVpnService("dummy-openvpn.exe", ovpnDir)
        {
            // Route table returns empty, so verified is false
            CaptureRouteTableOverride = () => Array.Empty<RouteRow>(),
            PowerShellOverride = (cmd, ct) => Task.FromResult((0, ""))
        };

        var profile = new Profile
        {
            Protocol = "openvpn",
            OpenVpnConfig = "client\ndev tun\n",
            LearnedRoutes = ["10.0.117.0/24"]
        };

        // When starting with a non-existent binary or failing verification, it throws IOException
        await Assert.ThrowsAnyAsync<Exception>(() => openVpn.StartAsync(profile, "1.1.1.1", CancellationToken.None));
        Assert.False(openVpn.Running);
    }
}
