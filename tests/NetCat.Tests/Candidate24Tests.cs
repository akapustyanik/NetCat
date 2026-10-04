using System.Net.NetworkInformation;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate24Tests
{
    private sealed class StubDesiredProvider(DesiredRuntimeState initial) : IDesiredRuntimeStateProvider
    {
        public DesiredRuntimeState State { get; set; } = initial;
        public DesiredRuntimeState GetCurrentDesiredState() => State;
    }

    private sealed class StubRouterRuntime : IRouterRuntime
    {
        public bool IsRunning { get; set; }
        public bool VpnRunning { get; set; }
        public bool TunActive { get; set; }
        public int ListenPort { get; set; }
        public int LatencyPort { get; set; }
        public int HealthSourcePort { get; set; }
        public NetworkSnapshot? ActivePhysical { get; set; }
        public Guid? ActiveProfileId { get; set; }
        public long SessionRevision { get; set; }
        public Task EnsureRunningAsync(AppSettings settings, NetworkSnapshot physical, string reason, CancellationToken ct) => Task.CompletedTask;
        public Task EnsureStoppedAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RefreshPhysicalAsync(AppSettings settings, NetworkSnapshot physical, string reason, CancellationToken ct) => Task.CompletedTask;
        public Task ApplyOpenVpnOverlayAsync(AppSettings settings, OpenVpnLink? link, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubZapretRuntime : IZapretRuntime
    {
        public bool IsRunning { get; set; }
        public string ActiveStrategy { get; set; } = "";
        public string ActiveScenario { get; set; } = "";
        public event Action<int, int>? ProcessExited { add { } remove { } }
        public Task EnsureRunningAsync(AppSettings settings, string? strategyFile, CancellationToken ct) => Task.CompletedTask;
        public Task EnsureStoppedAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubOpenVpnRuntime : IOpenVpnRuntime
    {
        public bool IsRunning { get; set; }
        public OpenVpnLink? Link { get; set; }
        public Guid? ActiveProfileId { get; set; }
        public int EnsureRunningCalls { get; private set; }
        public Func<Task<OpenVpnLink>>? OnEnsureRunning { get; set; }

        public Task<OpenVpnLink> EnsureRunningAsync(Profile profile, string dnsOverride, CancellationToken ct)
        {
            EnsureRunningCalls++;
            if (OnEnsureRunning != null) return OnEnsureRunning();
            var link = new OpenVpnLink("NetCat-OpenVPN", 10, "10.8.0.2", "10.8.0.1", "10.8.0.1", ["192.168.100.0/24"]);
            Link = link;
            IsRunning = true;
            ActiveProfileId = profile.Id;
            return Task.FromResult(link);
        }

        public Task EnsureStoppedAsync(CancellationToken ct)
        {
            IsRunning = false;
            Link = null;
            ActiveProfileId = null;
            return Task.CompletedTask;
        }
    }

    private sealed class StubOpenVpnRetryController(bool canAttempt) : IOpenVpnRetryController
    {
        public OpenVpnRetryState State { get; set; } = canAttempt ? OpenVpnRetryState.Idle : OpenVpnRetryState.SuspendedFatal;
        public OpenVpnFailureClass LastFailureClass { get; set; } = OpenVpnFailureClass.None;
        public string? LastError { get; set; }
        public DateTimeOffset? NextAttemptAt { get; set; }
        public int AttemptCount { get; set; }
        public OpenVpnRetryKey CurrentKey { get; set; } = OpenVpnRetryKey.Empty;
        public event Action<OpenVpnRetryState>? StateChanged { add { } remove { } }

        public bool CanAttempt(OpenVpnRetryKey key, DateTimeOffset now, ReconcileReason reason) => canAttempt;
        public void RecordAttemptStarted(OpenVpnRetryKey key, DateTimeOffset now) { }
        public void RecordSuccess(OpenVpnRetryKey key) { }
        public void RecordFailure(OpenVpnRetryKey key, OpenVpnFailureClass failureClass, string errorMessage, DateTimeOffset now) { }
        public void Reset(OpenVpnRetryKey key) { }
        public void Cancel() { }
        public void SetKey(OpenVpnRetryKey key) { }
    }

    // ==========================================
    // SECTION 27: RETRY BACKOFF & STORM PREVENTION (Tests 01-15)
    // ==========================================

    [Fact]
    public void Test_01_DeterministicLocalFatal_SingleAttemptAndSuspendedFatal()
    {
        var controller = new OpenVpnRetryController();
        var key = new OpenVpnRetryKey(Guid.NewGuid(), 1, 10, 1, 1);
        var now = DateTimeOffset.UtcNow;

        Assert.True(controller.CanAttempt(key, now, ReconcileReason.OpenVpnStateChanged));
        controller.RecordAttemptStarted(key, now);
        Assert.Equal(OpenVpnRetryState.Starting, controller.State);
        Assert.Equal(1, controller.AttemptCount);

        var error = "OpenSSL: error:0308010C:digital envelope routines::unsupported:Algorithm (RC2-40-CBC : 0)";
        var failureClass = OpenVpnFailureClassifier.Classify(error);
        Assert.Equal(OpenVpnFailureClass.DeterministicLocalFatal, failureClass);

        controller.RecordFailure(key, failureClass, error, now);

        Assert.Equal(OpenVpnRetryState.SuspendedFatal, controller.State);
        Assert.Null(controller.NextAttemptAt);
        Assert.False(controller.CanAttempt(key, now.AddSeconds(10), ReconcileReason.OpenVpnStateChanged));
        Assert.False(controller.CanAttempt(key, now.AddHours(1), ReconcileReason.OpenVpnStateChanged));
    }

    [Fact]
    public void Test_02_AuthenticationFatal_SingleAttemptAndSuspendedAuth()
    {
        var controller = new OpenVpnRetryController();
        var key = new OpenVpnRetryKey(Guid.NewGuid(), 1, 10, 1, 1);
        var now = DateTimeOffset.UtcNow;

        controller.RecordAttemptStarted(key, now);
        var failureClass = OpenVpnFailureClassifier.Classify("AUTH_FAILED: username/password verification failed");
        Assert.Equal(OpenVpnFailureClass.AuthenticationFatal, failureClass);

        controller.RecordFailure(key, failureClass, "AUTH_FAILED", now);

        Assert.Equal(OpenVpnRetryState.SuspendedAuth, controller.State);
        Assert.Null(controller.NextAttemptAt);
        Assert.False(controller.CanAttempt(key, now.AddMinutes(5), ReconcileReason.OpenVpnStateChanged));
    }

    [Fact]
    public void Test_03_TransientNetwork_BackoffScheduleAndBudget()
    {
        var controller = new OpenVpnRetryController(maxRetries: 3);
        var key = new OpenVpnRetryKey(Guid.NewGuid(), 1, 10, 1, 1);
        var now = DateTimeOffset.UtcNow;

        // Attempt 1 -> fails -> retry scheduled in 5s
        controller.RecordAttemptStarted(key, now);
        controller.RecordFailure(key, OpenVpnFailureClass.TransientNetwork, "TLS handshake failed", now);
        Assert.Equal(OpenVpnRetryState.RetryScheduled, controller.State);
        Assert.Equal(now.AddSeconds(5), controller.NextAttemptAt);

        // Attempt 2 -> fails -> retry scheduled in 15s
        var t2 = now.AddSeconds(5);
        Assert.True(controller.CanAttempt(key, t2, ReconcileReason.ExternalConditionResolved));
        controller.RecordAttemptStarted(key, t2);
        controller.RecordFailure(key, OpenVpnFailureClass.TransientNetwork, "Connection timed out", t2);
        Assert.Equal(OpenVpnRetryState.RetryScheduled, controller.State);
        Assert.Equal(t2.AddSeconds(15), controller.NextAttemptAt);

        // Attempt 3 -> fails -> budget exhausted, transitions to WaitingForRelevantChange
        var t3 = t2.AddSeconds(15);
        Assert.True(controller.CanAttempt(key, t3, ReconcileReason.ExternalConditionResolved));
        controller.RecordAttemptStarted(key, t3);
        controller.RecordFailure(key, OpenVpnFailureClass.TransientNetwork, "Connection reset", t3);
        Assert.Equal(OpenVpnRetryState.WaitingForRelevantChange, controller.State);
        Assert.Null(controller.NextAttemptAt);
        Assert.False(controller.CanAttempt(key, t3.AddMinutes(10), ReconcileReason.OpenVpnStateChanged));
    }

    [Fact]
    public void Test_04_ProcessExited_WithinBackoffWindow_DoesNotBypassGate()
    {
        var controller = new OpenVpnRetryController();
        var key = new OpenVpnRetryKey(Guid.NewGuid(), 1, 10, 1, 1);
        var now = DateTimeOffset.UtcNow;

        controller.RecordAttemptStarted(key, now);
        controller.RecordFailure(key, OpenVpnFailureClass.TransientNetwork, "network down", now);
        Assert.Equal(OpenVpnRetryState.RetryScheduled, controller.State);

        // ProcessExited fires 50ms later
        var exitTime = now.AddMilliseconds(50);
        Assert.False(controller.CanAttempt(key, exitTime, ReconcileReason.OpenVpnStateChanged));
    }

    [Fact]
    public void Test_05_RuntimePlanner_DoesNotGenerateStartOpenVpn_WhenCanAttemptFalse()
    {
        var desired = new DesiredRuntimeState { OpenVpnEnabled = true, SelectedOpenVpnProfileId = Guid.NewGuid() };
        var observed = new ObservedRuntimeState(MainRouterStatus: ObservedComponentState.Stopped, ZapretStatus: ObservedComponentState.Stopped, OpenVpnStatus: ObservedComponentState.Stopped, TunReady: false, PhysicalNetwork: null, PhysicalUsable: true);
        var failures = new Dictionary<ComponentId, int>();

        var plan = RuntimePlanner.CreatePlan(
            desired, observed, new AppSettings(), failures,
            canStartOpenVpn: false);

        Assert.DoesNotContain(plan.Actions, a => a.Type == PlanActionType.StartOpenVpn);
        Assert.Contains(ComponentId.OpenVpnLink, plan.PendingRetryComponents);
    }

    [Fact]
    public void Test_06_RuntimePlanner_CalculatesRemainingBackoffDelay()
    {
        var desired = new DesiredRuntimeState { OpenVpnEnabled = true };
        var observed = new ObservedRuntimeState(MainRouterStatus: ObservedComponentState.Stopped, ZapretStatus: ObservedComponentState.Stopped, OpenVpnStatus: ObservedComponentState.Stopped, TunReady: false, PhysicalNetwork: null, PhysicalUsable: true);
        var failures = new Dictionary<ComponentId, int>();

        var expectedDelay = TimeSpan.FromSeconds(4.5);
        var plan = RuntimePlanner.CreatePlan(
            desired, observed, new AppSettings(), failures,
            canStartOpenVpn: false,
            openVpnRetryDelay: expectedDelay);

        Assert.Equal(expectedDelay, plan.NextRetryDelay);
    }

    [Fact]
    public async Task Test_07_RuntimeCoordinator_SuppressesStartOpenVpn_WhenCanAttemptFalse()
    {
        var ovpnProfileId = Guid.NewGuid();
        var desired = new DesiredRuntimeState { OpenVpnEnabled = true, SelectedOpenVpnProfileId = ovpnProfileId };
        var settings = new AppSettings
        {
            Profiles = [new Profile { Id = ovpnProfileId, Protocol = "openvpn", Name = "Corporate" }],
            OpenVpnProfileId = ovpnProfileId
        };

        var desiredProvider = new StubDesiredProvider(desired);
        var router = new StubRouterRuntime();
        var zapret = new StubZapretRuntime();
        var ovpn = new StubOpenVpnRuntime();

        var controller = new StubOpenVpnRetryController(canAttempt: false);
        var coordinator = new RuntimeCoordinator(desiredProvider, router, zapret, ovpn, controller)
        {
            GetSettings = () => settings
        };

        // Trigger reconcile
        await coordinator.ReconcileAsync(ReconcileReason.OpenVpnStateChanged, CancellationToken.None);

        // EnsureRunningAsync must NOT be called
        Assert.Equal(0, ovpn.EnsureRunningCalls);
    }

    [Fact]
    public void Test_08_ProfileChange_ResetsRetryStateAndAllowsAttempt()
    {
        var controller = new OpenVpnRetryController();
        var key1 = new OpenVpnRetryKey(Guid.NewGuid(), 1, 10, 1, 1);
        var key2 = new OpenVpnRetryKey(Guid.NewGuid(), 1, 10, 1, 1);
        var now = DateTimeOffset.UtcNow;

        controller.RecordAttemptStarted(key1, now);
        controller.RecordFailure(key1, OpenVpnFailureClass.DeterministicLocalFatal, "fatal", now);
        Assert.False(controller.CanAttempt(key1, now, ReconcileReason.OpenVpnStateChanged));

        // When switching to key2 (different profile ID), attempt is permitted
        Assert.True(controller.CanAttempt(key2, now, ReconcileReason.UserChangedSettings));
    }

    [Fact]
    public void Test_09_CredentialsChange_ResetsSuspendedAuthAndAllowsAttempt()
    {
        var profileId = Guid.NewGuid();
        var controller = new OpenVpnRetryController();
        var keyOld = new OpenVpnRetryKey(profileId, 1, 10, 100, 1);
        var keyNew = new OpenVpnRetryKey(profileId, 1, 10, 200, 1); // credentials updated
        var now = DateTimeOffset.UtcNow;

        controller.RecordAttemptStarted(keyOld, now);
        controller.RecordFailure(keyOld, OpenVpnFailureClass.AuthenticationFatal, "AUTH_FAILED", now);
        Assert.False(controller.CanAttempt(keyOld, now, ReconcileReason.OpenVpnStateChanged));

        Assert.True(controller.CanAttempt(keyNew, now, ReconcileReason.UserChangedSettings));
    }

    [Fact]
    public void Test_10_PhysicalNetworkChange_ResetsWaitingForRelevantChange()
    {
        var profileId = Guid.NewGuid();
        var controller = new OpenVpnRetryController(maxRetries: 1);
        var keyPhys1 = new OpenVpnRetryKey(profileId, 1, 10, 1, 1);
        var keyPhys2 = new OpenVpnRetryKey(profileId, 1, 20, 1, 1); // physical adapter changed
        var now = DateTimeOffset.UtcNow;

        controller.RecordAttemptStarted(keyPhys1, now);
        controller.RecordFailure(keyPhys1, OpenVpnFailureClass.TransientNetwork, "timeout", now);
        Assert.Equal(OpenVpnRetryState.WaitingForRelevantChange, controller.State);
        Assert.False(controller.CanAttempt(keyPhys1, now, ReconcileReason.OpenVpnStateChanged));

        Assert.True(controller.CanAttempt(keyPhys2, now, ReconcileReason.PhysicalNetworkChanged));
    }

    [Fact]
    public void Test_11_ManualRetry_OverridesBackoffDeadlineAndSuspended()
    {
        var controller = new OpenVpnRetryController();
        var key = new OpenVpnRetryKey(Guid.NewGuid(), 1, 10, 1, 1);
        var now = DateTimeOffset.UtcNow;

        controller.RecordAttemptStarted(key, now);
        controller.RecordFailure(key, OpenVpnFailureClass.DeterministicLocalFatal, "RC2 error", now);
        Assert.Equal(OpenVpnRetryState.SuspendedFatal, controller.State);

        // The command grants one attempt; an eligibility query has no side effects.
        Assert.False(controller.CanAttempt(key, now, ReconcileReason.ManualRetry));
        controller.Reset(key);
        Assert.True(controller.CanAttempt(key, now, ReconcileReason.ManualRetry));
        Assert.Equal(OpenVpnRetryState.Idle, controller.State);
    }

    [Fact]
    public void Test_12_ControllerCancel_ResetsToIdleAndClearsNextAttempt()
    {
        var controller = new OpenVpnRetryController();
        var key = new OpenVpnRetryKey(Guid.NewGuid(), 1, 10, 1, 1);
        var now = DateTimeOffset.UtcNow;

        controller.RecordAttemptStarted(key, now);
        controller.RecordFailure(key, OpenVpnFailureClass.TransientNetwork, "failed", now);
        Assert.Equal(OpenVpnRetryState.RetryScheduled, controller.State);
        Assert.NotNull(controller.NextAttemptAt);

        controller.Cancel();

        Assert.Equal(OpenVpnRetryState.Idle, controller.State);
        Assert.Null(controller.NextAttemptAt);
        Assert.Equal(0, controller.AttemptCount);
    }

    [Fact]
    public void Test_13_SuccessfulConnection_ResetsFailureCountAndStateConnected()
    {
        var controller = new OpenVpnRetryController();
        var key = new OpenVpnRetryKey(Guid.NewGuid(), 1, 10, 1, 1);
        var now = DateTimeOffset.UtcNow;

        controller.RecordAttemptStarted(key, now);
        controller.RecordFailure(key, OpenVpnFailureClass.TransientNetwork, "temporary drop", now);
        Assert.Equal(1, controller.AttemptCount);

        controller.RecordAttemptStarted(key, now.AddSeconds(5));
        controller.RecordSuccess(key);

        Assert.Equal(OpenVpnRetryState.Connected, controller.State);
        Assert.Equal(0, controller.AttemptCount);
        Assert.Null(controller.NextAttemptAt);
        Assert.Null(controller.LastError);
    }

    [Fact]
    public void Test_14_FailureClassifier_ComprehensiveFatalPatterns()
    {
        Assert.Equal(OpenVpnFailureClass.DeterministicLocalFatal,
            OpenVpnFailureClassifier.Classify("OpenSSL: error:0308010C:digital envelope routines::unsupported:Algorithm (RC2-40-CBC : 0)"));

        Assert.Equal(OpenVpnFailureClass.DeterministicLocalFatal,
            OpenVpnFailureClassifier.Classify("digital envelope routines::unsupported"));

        Assert.Equal(OpenVpnFailureClass.DeterministicLocalFatal,
            OpenVpnFailureClassifier.Classify("unsupported:Algorithm"));

        Assert.Equal(OpenVpnFailureClass.DeterministicLocalFatal,
            OpenVpnFailureClassifier.Classify("wintun.dll not found in module directory"));

        Assert.Equal(OpenVpnFailureClass.DeterministicLocalFatal,
            OpenVpnFailureClassifier.Classify("", new FileNotFoundException("wintun.dll")));

        Assert.Equal(OpenVpnFailureClass.DeterministicLocalFatal,
            OpenVpnFailureClassifier.Classify("", new InvalidDataException("Invalid syntax")));
    }

    [Fact]
    public void Test_15_FailureClassifier_TransientAndAuthPatterns()
    {
        Assert.Equal(OpenVpnFailureClass.AuthenticationFatal,
            OpenVpnFailureClassifier.Classify("AUTH_FAILED: password mismatch"));

        Assert.Equal(OpenVpnFailureClass.PhysicalNetworkUnavailable,
            OpenVpnFailureClassifier.Classify("Физический сетевой адаптер недоступен"));

        Assert.Equal(OpenVpnFailureClass.TransientNetwork,
            OpenVpnFailureClassifier.Classify("TLS Error: TLS key negotiation failed to occur"));

        Assert.Equal(OpenVpnFailureClass.TransientNetwork,
            OpenVpnFailureClassifier.Classify("Connection timed out"));

        Assert.Equal(OpenVpnFailureClass.RemoteTemporary,
            OpenVpnFailureClassifier.Classify("server restart pause 30s"));
    }

    // ==========================================
    // SECTION 28: UI TOGGLE & CANCELLATION (Tests 16-30)
    // ==========================================

    [Fact]
    public void Test_16_UI_OpenVpnButton_DesiredFalse_ShowsConnect()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var settings = new AppSettings();
        using var vm = new MainViewModel(store, settings);

        vm.UpdateDesiredState(d => d with { OpenVpnEnabled = false });
        Assert.Equal("Подключить OpenVPN", vm.OpenVpnButton);
    }

    [Fact]
    public void Test_17_UI_OpenVpnButton_DesiredTrue_Starting_ShowsCancel()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var settings = new AppSettings();
        using var vm = new MainViewModel(store, settings);

        vm.UpdateDesiredState(d => d with { OpenVpnEnabled = true });
        vm.RuntimeCoordinator.OpenVpnRetryController.RecordAttemptStarted(OpenVpnRetryKey.Empty, DateTimeOffset.UtcNow);

        Assert.Equal("Отменить подключение", vm.OpenVpnButton);
    }

    [Fact]
    public void Test_18_UI_GlobalCancelAvailableWhileStarting()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var settings = new AppSettings();
        using var vm = new MainViewModel(store, settings);

        vm.UpdateDesiredState(d => d with { OpenVpnEnabled = true });
        vm.RuntimeCoordinator.OpenVpnRetryController.RecordAttemptStarted(OpenVpnRetryKey.Empty, DateTimeOffset.UtcNow);

        Assert.Equal("Отменить подключение", vm.OpenVpnButton);
        Assert.True(vm.CanCancelOperation);
    }

    [Fact]
    public void Test_19_UI_OpenVpnButton_DesiredTrue_RetryScheduled_ShowsCancel()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var settings = new AppSettings();
        using var vm = new MainViewModel(store, settings);

        vm.UpdateDesiredState(d => d with { OpenVpnEnabled = true });
        var key = new OpenVpnRetryKey(Guid.NewGuid(), 1, 10, 1, 1);
        vm.RuntimeCoordinator.OpenVpnRetryController.RecordAttemptStarted(key, DateTimeOffset.UtcNow);
        vm.RuntimeCoordinator.OpenVpnRetryController.RecordFailure(key, OpenVpnFailureClass.TransientNetwork, "drop", DateTimeOffset.UtcNow);

        Assert.Equal(OpenVpnRetryState.RetryScheduled, vm.RuntimeCoordinator.OpenVpnRetryController.State);
        Assert.Equal("Отменить подключение", vm.OpenVpnButton);
    }

    [Fact]
    public void Test_20_UI_OpenVpnButton_DesiredTrue_Connected_ShowsDisconnect()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var settings = new AppSettings();
        using var vm = new MainViewModel(store, settings);

        vm.UpdateDesiredState(d => d with { OpenVpnEnabled = true });
        var key = new OpenVpnRetryKey(Guid.NewGuid(), 1, 10, 1, 1);
        vm.RuntimeCoordinator.OpenVpnRetryController.RecordAttemptStarted(key, DateTimeOffset.UtcNow);
        vm.RuntimeCoordinator.OpenVpnRetryController.RecordSuccess(key);

        Assert.Equal(OpenVpnRetryState.Connected, vm.RuntimeCoordinator.OpenVpnRetryController.State);
        Assert.Equal("Отключить OpenVPN", vm.OpenVpnButton);
    }

    [Fact]
    public void Test_21_UI_OpenVpnButton_DesiredTrue_SuspendedFatal_ShowsDisconnect()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var settings = new AppSettings();
        using var vm = new MainViewModel(store, settings);

        vm.UpdateDesiredState(d => d with { OpenVpnEnabled = true });
        var key = new OpenVpnRetryKey(Guid.NewGuid(), 1, 10, 1, 1);
        vm.RuntimeCoordinator.OpenVpnRetryController.RecordAttemptStarted(key, DateTimeOffset.UtcNow);
        vm.RuntimeCoordinator.OpenVpnRetryController.RecordFailure(key, OpenVpnFailureClass.DeterministicLocalFatal, "RC2 fatal", DateTimeOffset.UtcNow);

        Assert.Equal(OpenVpnRetryState.SuspendedFatal, vm.RuntimeCoordinator.OpenVpnRetryController.State);
        Assert.Equal("Отключить OpenVPN", vm.OpenVpnButton);
    }

    [Fact]
    public void Test_22_UI_OpenVpnButton_DesiredTrue_SuspendedAuth_ShowsDisconnect()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var settings = new AppSettings();
        using var vm = new MainViewModel(store, settings);

        vm.UpdateDesiredState(d => d with { OpenVpnEnabled = true });
        var key = new OpenVpnRetryKey(Guid.NewGuid(), 1, 10, 1, 1);
        vm.RuntimeCoordinator.OpenVpnRetryController.RecordAttemptStarted(key, DateTimeOffset.UtcNow);
        vm.RuntimeCoordinator.OpenVpnRetryController.RecordFailure(key, OpenVpnFailureClass.AuthenticationFatal, "AUTH_FAILED", DateTimeOffset.UtcNow);

        Assert.Equal(OpenVpnRetryState.SuspendedAuth, vm.RuntimeCoordinator.OpenVpnRetryController.State);
        Assert.Equal("Отключить OpenVPN", vm.OpenVpnButton);
    }

    [Fact]
    public void Test_23_UI_OpenVpnButton_DesiredTrue_WaitingForRelevantChange_ShowsDisconnect()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var settings = new AppSettings();
        using var vm = new MainViewModel(store, settings);

        vm.UpdateDesiredState(d => d with { OpenVpnEnabled = true });
        var key = new OpenVpnRetryKey(Guid.NewGuid(), 1, 10, 1, 1);
        vm.RuntimeCoordinator.OpenVpnRetryController.RecordAttemptStarted(key, DateTimeOffset.UtcNow);
        vm.RuntimeCoordinator.OpenVpnRetryController.RecordFailure(key, OpenVpnFailureClass.PhysicalNetworkUnavailable, "no physical net", DateTimeOffset.UtcNow);

        Assert.Equal(OpenVpnRetryState.WaitingForRelevantChange, vm.RuntimeCoordinator.OpenVpnRetryController.State);
        Assert.Equal("Отключить OpenVPN", vm.OpenVpnButton);
    }

    [Fact]
    public void Test_24_UI_Click_WhenDesiredFalse_SetsDesiredTrue()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var settings = new AppSettings();
        using var vm = new MainViewModel(store, settings);

        vm.UpdateDesiredState(d => d with { OpenVpnEnabled = false });

        // Simulating OpenVpn_Click:
        vm.UserRequestedOpenVpnChange(!vm.DesiredState.OpenVpnEnabled);

        Assert.True(vm.DesiredState.OpenVpnEnabled);
    }

    [Fact]
    public void Test_25_UI_Click_WhenDesiredTrue_SetsDesiredFalse_NeverTrue()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var settings = new AppSettings();
        using var vm = new MainViewModel(store, settings);

        // Desired is true, but Router.OpenVpn.Running is false (e.g. failing/retrying)
        vm.UpdateDesiredState(d => d with { OpenVpnEnabled = true });
        Assert.False(vm.Router.OpenVpn.Running);

        // In Candidate23 this incorrectly evaluated !vm.Router.OpenVpn.Running -> !false -> true (re-requesting connection!)
        // In Candidate24 it evaluates !vm.DesiredState.OpenVpnEnabled -> !true -> false:
        vm.UserRequestedOpenVpnChange(!vm.DesiredState.OpenVpnEnabled);

        Assert.False(vm.DesiredState.OpenVpnEnabled);
    }

    [Fact]
    public void Test_26_UI_UserRequestedOpenVpnChange_False_CancelsPendingRetries()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var settings = new AppSettings();
        using var vm = new MainViewModel(store, settings);

        vm.UpdateDesiredState(d => d with { OpenVpnEnabled = true });
        var key = new OpenVpnRetryKey(Guid.NewGuid(), 1, 10, 1, 1);
        vm.RuntimeCoordinator.OpenVpnRetryController.RecordAttemptStarted(key, DateTimeOffset.UtcNow);
        vm.RuntimeCoordinator.OpenVpnRetryController.RecordFailure(key, OpenVpnFailureClass.TransientNetwork, "fail", DateTimeOffset.UtcNow);

        Assert.Equal(OpenVpnRetryState.RetryScheduled, vm.RuntimeCoordinator.OpenVpnRetryController.State);

        vm.UserRequestedOpenVpnChange(false);

        Assert.Equal(OpenVpnRetryState.Idle, vm.RuntimeCoordinator.OpenVpnRetryController.State);
        Assert.Null(vm.RuntimeCoordinator.OpenVpnRetryController.NextAttemptAt);
    }

    [Fact]
    public void Test_27_UI_GlobalCancel_CancelsPendingOpenVpnConnection()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var settings = new AppSettings();
        using var vm = new MainViewModel(store, settings);

        vm.UpdateDesiredState(d => d with { OpenVpnEnabled = true });
        var key = new OpenVpnRetryKey(Guid.NewGuid(), 1, 10, 1, 1);
        vm.RuntimeCoordinator.OpenVpnRetryController.RecordAttemptStarted(key, DateTimeOffset.UtcNow);
        vm.RuntimeCoordinator.OpenVpnRetryController.RecordFailure(key, OpenVpnFailureClass.TransientNetwork, "drop", DateTimeOffset.UtcNow);

        Assert.Equal(OpenVpnRetryState.RetryScheduled, vm.RuntimeCoordinator.OpenVpnRetryController.State);

        vm.CancelCurrentOperation();

        Assert.False(vm.DesiredState.OpenVpnEnabled);
        Assert.Equal(OpenVpnRetryState.Idle, vm.RuntimeCoordinator.OpenVpnRetryController.State);
    }

    [Fact]
    public void Test_28_UI_DisablingOpenVpn_DoesNotTouchMainVpnOrZapret()
    {
        // When user disables OpenVPN while MainVpn and Zapret are running:
        var desired = new DesiredRuntimeState
        {
            MainVpnEnabled = true,
            ZapretEnabled = true,
            OpenVpnEnabled = false
        };
        var observed = new ObservedRuntimeState(
            MainRouterStatus: ObservedComponentState.RunningHealthy,
            ZapretStatus: ObservedComponentState.RunningHealthy,
            OpenVpnStatus: ObservedComponentState.RunningHealthy,
            TunReady: true,
            PhysicalNetwork: new NetworkSnapshot("Eth", 1, "192.168.1.1", "192.168.1.1", []),
            PhysicalUsable: true
        );

        var plan = RuntimePlanner.CreatePlan(desired, observed, new AppSettings(), new Dictionary<ComponentId, int>());

        // Only OpenVPN actions are scheduled
        Assert.All(plan.Actions, a => Assert.True(a.TargetComponent is ComponentId.OpenVpnLink or ComponentId.OpenVpnRoutes));
        Assert.DoesNotContain(plan.Actions, a => a.TargetComponent == ComponentId.MainRouter);
        Assert.DoesNotContain(plan.Actions, a => a.TargetComponent == ComponentId.Zapret);
        Assert.DoesNotContain(plan.Actions, a => a.TargetComponent == ComponentId.Tun);
    }

    [Fact]
    public void Test_29_UI_OpenVpnStatus_ReflectsSuspendedFatalMessage()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var settings = new AppSettings();
        using var vm = new MainViewModel(store, settings);

        vm.UpdateDesiredState(d => d with { OpenVpnEnabled = true });
        var key = new OpenVpnRetryKey(Guid.NewGuid(), 1, 10, 1, 1);
        vm.RuntimeCoordinator.OpenVpnRetryController.RecordAttemptStarted(key, DateTimeOffset.UtcNow);
        vm.RuntimeCoordinator.OpenVpnRetryController.RecordFailure(key, OpenVpnFailureClass.DeterministicLocalFatal, "Algorithm (RC2-40-CBC : 0)", DateTimeOffset.UtcNow);

        Assert.Contains("критическая ошибка", vm.OpenVpnStatus);
    }

    [Fact]
    public void Test_30_UI_OpenVpnStatus_ReflectsCountdownWhenRetryScheduled()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var settings = new AppSettings();
        using var vm = new MainViewModel(store, settings);

        vm.UpdateDesiredState(d => d with { OpenVpnEnabled = true });
        var key = new OpenVpnRetryKey(Guid.NewGuid(), 1, 10, 1, 1);
        vm.RuntimeCoordinator.OpenVpnRetryController.RecordAttemptStarted(key, DateTimeOffset.UtcNow);
        vm.RuntimeCoordinator.OpenVpnRetryController.RecordFailure(key, OpenVpnFailureClass.TransientNetwork, "network timeout", DateTimeOffset.UtcNow);

        Assert.Contains("повтор через", vm.OpenVpnStatus);
    }
}
