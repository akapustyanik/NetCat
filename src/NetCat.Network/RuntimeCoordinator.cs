using System.Diagnostics;
using NetCat.Core;
using NetCat.Engine;

namespace NetCat.Network;

public sealed class RuntimeCoordinator : IDisposable
{
    private readonly SemaphoreSlim reconcileGate = new(1, 1);
    private readonly SemaphoreSlim zapretTestGate = new(1, 1);
    private int zapretTesting;
    public bool IsZapretTesting => Volatile.Read(ref zapretTesting) != 0;
    private readonly object syncRoot = new();
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly Dictionary<ComponentId, ComponentRetryState> componentRetries = new();
    private readonly Queue<Action> publications = new();
    private bool publishing;
    private long healthSequence, publishedHealthSequence;
    private ObservedRuntimeState? observedState;

    private long generation;
    private long openVpnIntent;
    private long openVpnPhysicalGeneration;
    private OpenVpnRetryKey? routeRetryKey;
    private NetworkSnapshot? signaledPhysical;
    private bool physicalSignalPending;
    public void NotifyPhysicalBindingChanged(NetworkSnapshot? current)
    {
        // Invalidate in-flight probes before waiting for the serialized reconcile
        // pass, which may itself be awaiting that probe's completion.
        lock (syncRoot)
        {
            signaledPhysical = current; physicalSignalPending = true;
            Interlocked.Increment(ref openVpnPhysicalGeneration);
        }
        OpenVpnRuntime.PhysicalNetworkChanged();
    }
    private CancellationTokenSource? openVpnAttempt;
    public void CancelOpenVpnOperation()
    {
        lock (syncRoot)
        {
            openVpnIntent++;
            openVpnAttempt?.Cancel();
            OpenVpnRetryController.Cancel();
        }
        OpenVpnRuntime.CancelPendingConnection();
    }
    private bool pendingReconcile;
    private ReconcileReason pendingReason = ReconcileReason.Startup;
    private TaskCompletionSource<bool> wakeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? loopTask;
    private volatile MainRouterLifecycle mainRouterLifecycle = MainRouterLifecycle.StoppedByDesired;
    private volatile PhysicalNetworkAvailability physicalNetworkAvailability = PhysicalNetworkAvailability.Unknown;
    private long lifecycleRevision;
    private NetworkSnapshot? capturedPhysical;

    private EffectiveRuntimeConfig? lastAppliedConfig;
    private EffectiveRuntimeConfig? failedMainConfig;
    private bool dependencyRecovery;
    private DateTimeOffset? dependencyRecoveredAt;
    public EffectiveRuntimeConfig? LastAppliedConfig => lastAppliedConfig;
    public RuntimeSettingsDiff? LastDiff { get; private set; }
    public Func<Guid, IReadOnlyList<string>, CancellationToken, Task>? OnOpenVpnRoutesLearned { get; init; }

    public IDesiredRuntimeStateProvider DesiredStateProvider { get; init; }
    public IRouterRuntime RouterRuntime { get; init; }
    public IZapretRuntime ZapretRuntime { get; init; }
    public IOpenVpnRuntime OpenVpnRuntime { get; init; }
    public IPhysicalNetworkProvider PhysicalNetworkProvider { get; init; } = SystemPhysicalNetworkProvider.Instance;
    public ITunnelHealthInspector TunnelInspector { get; init; } = SystemTunnelHealthInspector.Instance;
    public IClock Clock { get; init; } = SystemClock.Instance;

    public Func<AppSettings> GetSettings { get; init; } = () => new();
    public Func<CancellationToken, Task>? FinalizeRoutingOverride { get; init; }
    public Func<IDisposable?>? CreateBatch { get; init; }
    public Action<string>? Log { get; set; }
    public event Action<ObservedRuntimeState>? ObservedStateChanged;
    public event Action<StartupRestoreState>? StateChanged;
    public event Action<RuntimeConvergenceState>? ConvergenceStateChanged;

    public TimeSpan[] BackoffIntervals { get; set; } = RuntimePlanner.DefaultBackoffIntervals;
    public TimeSpan TunReadyTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public StartupRestoreState CurrentRestoreState { get; private set; } = StartupRestoreState.Idle;
    public StartupRestoreState State => CurrentRestoreState;
    private volatile RuntimeConvergenceState convergenceState = new(false, false, [], null, ConvergencePhase.Starting);
    public RuntimeConvergenceState CurrentConvergenceState { get => convergenceState; private set => SetConvergenceState(value); }
    public ObservedRuntimeState? CurrentObservedState { get { lock (syncRoot) return observedState; } }
    public MainRouterLifecycle MainRouterLifecycle => mainRouterLifecycle;
    public bool MainRouterRetryPending
    {
        get { lock(syncRoot) return new[]{ComponentId.MainRouter,ComponentId.Tun}
            .Any(c => componentRetries.TryGetValue(c,out var retry) && retry.NextRetryAt > Clock.UtcNow); }
    }
    public PhysicalNetworkAvailability PhysicalNetworkState => physicalNetworkAvailability;
    public NetworkSnapshot? CurrentPhysicalNetwork => capturedPhysical;

    public IOpenVpnRetryController OpenVpnRetryController { get; init; } = new OpenVpnRetryController();
    public bool? VpnUpstreamHealthy { get; set; }
    public Func<int, IReadOnlyList<string>, RouteObservationResult>? VerifyOpenVpnRoutesInRouteTable { get; set; }

    public long CurrentGeneration => Interlocked.Read(ref generation);

    public int GetFailureCount(ComponentId component)
    {
        lock (syncRoot)
        {
            return componentRetries.TryGetValue(component, out var state) ? state.Failures : 0;
        }
    }

    public static RouteObservationResult DefaultVerifyRoutesInTable(int ifIndex, IReadOnlyList<string> requiredRoutes)
    {
        return RouteTable.VerifyRoutes(ifIndex, requiredRoutes);
    }

    public RuntimeCoordinator(
        IDesiredRuntimeStateProvider desiredProvider,
        IRouterRuntime router,
        IZapretRuntime zapret,
        IOpenVpnRuntime openVpn,
        IOpenVpnRetryController? openVpnRetry = null)
    {
        DesiredStateProvider = desiredProvider;
        RouterRuntime = router;
        ZapretRuntime = zapret;
        OpenVpnRuntime = openVpn;
        if (openVpnRetry != null) OpenVpnRetryController = openVpnRetry;
        openVpn.ProcessExited += (_, _) => RequestReconcile(ReconcileReason.OpenVpnStateChanged);
        openVpn.LinkChanged += () => { router.InvalidateOpenVpnOverlay(); RequestReconcile(ReconcileReason.OpenVpnStateChanged); };
        router.OpenVpnOverlayLost += () => RequestReconcile(ReconcileReason.OpenVpnStateChanged);
        router.DependencyLost += () => RequestReconcile(ReconcileReason.VpnUpstreamChanged);

        zapret.ProcessExited += (pid, exitCode) =>
        {
            var d = DesiredStateProvider.GetCurrentDesiredState();
            Log?.Invoke($"ZAPRET_PROCESS state=exited pid={pid} exitCode={exitCode} desired={(d.ZapretEnabled ? "on" : "off")}");
            if (d.ZapretEnabled)
            {
                RequestReconcile(ReconcileReason.ZapretProcessExited);
            }
        };
    }

    public void RequestReconcile(ReconcileReason reason)
    {
        var desiredNow = DesiredStateProvider.GetCurrentDesiredState();
        if (!desiredNow.OpenVpnEnabled) CancelOpenVpnOperation();
        var settingsNow = GetSettings();
        RouterRuntime.PrepareDomainOwnership(settingsNow);
        var profileNow = settingsNow.Profiles.FirstOrDefault(p => p.Id == (desiredNow.SelectedOpenVpnProfileId ?? settingsNow.OpenVpnProfileId) && p.IsOpenVpn);
        if (OpenVpnRuntime.Reconnecting && (!desiredNow.OpenVpnEnabled || profileNow == null || profileNow.Id != OpenVpnRuntime.ActiveProfileId || OpenVpnRuntime.NeedsRestart(profileNow)))
            OpenVpnRuntime.CancelPendingConnection();
        lock (syncRoot)
        {
            if (lifetimeCts.IsCancellationRequested) return;
            pendingReconcile = true;
            pendingReason = reason;
            wakeSignal.TrySetResult(true);

            if (loopTask == null || loopTask.IsCompleted)
            {
                loopTask = Task.Run(ReconciliationLoopAsync);
            }
        }
    }

    public void TriggerRetry()
    {
        lock (syncRoot)
        {
            if (lifetimeCts.IsCancellationRequested) return;
            pendingReconcile = true;
            pendingReason = ReconcileReason.ManualRetry;
            wakeSignal.TrySetResult(true);

            if (loopTask == null || loopTask.IsCompleted)
            {
                loopTask = Task.Run(ReconciliationLoopAsync);
            }
        }
    }

    private ObservedComponentState ObserveOpenVpn(Profile? target)
    {
        if (OpenVpnRuntime.IsRunning) return target == null || OpenVpnRuntime.NeedsRestart(target)
            ? ObservedComponentState.RunningDegraded : ObservedComponentState.RunningHealthy;
        if (OpenVpnRuntime.RequiresStop)
            return OpenVpnRuntime.Reconnecting && target != null && target.Id == OpenVpnRuntime.ActiveProfileId &&
                !OpenVpnRuntime.NeedsRestart(target) && OpenVpnRuntime.RuntimePhase is OpenVpnRuntimePhase.Starting or OpenVpnRuntimePhase.Reconnecting or OpenVpnRuntimePhase.LongReconnect
                ? ObservedComponentState.Starting : ObservedComponentState.RunningDegraded;
        return ObservedComponentState.Stopped;
    }

    public async Task ReconcileAsync(ReconcileReason reason, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetimeCts.Token);
        await reconcileGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var delay = await ExecuteReconcilePassAsync(reason, linked.Token).ConfigureAwait(false);
            if (delay > TimeSpan.Zero) RequestReconcile(ReconcileReason.ExternalConditionResolved);
        }
        finally
        {
            reconcileGate.Release();
        }
    }

    public void Cancel()
    {
        lifetimeCts.Cancel();
        lock (syncRoot)
        {
            wakeSignal.TrySetCanceled();
        }
        SetRestoreState(StartupRestoreState.Cancelled);
    }

    private void SetRestoreState(StartupRestoreState state)
    {
        lock (syncRoot)
        {
            if (CurrentRestoreState == state) return;
            CurrentRestoreState = state;
            publications.Enqueue(() => Publish(StateChanged, state));
        }
        DrainPublications();
    }

    private void SetConvergenceState(RuntimeConvergenceState state)
    {
        lock (syncRoot)
        {
            var snapshot = state with { PhysicalNetworkState = physicalNetworkAvailability };
            convergenceState = snapshot;
            publications.Enqueue(() => Publish(ConvergenceStateChanged, snapshot));
        }
        DrainPublications();
    }
    private void MutateObserved(Func<ObservedRuntimeState?, ObservedRuntimeState?> change)
    {
        lock (syncRoot)
        {
            var next = change(observedState); if (next == null) return;
            observedState = next;
            publications.Enqueue(() => Publish(ObservedStateChanged, next));
        }
        DrainPublications();
    }
    private void DrainPublications()
    {
        lock (syncRoot) { if (publishing) return; publishing = true; }
        while (true)
        {
            Action next;
            lock (syncRoot) { if (!publications.TryDequeue(out next!)) { publishing = false; return; } }
            try { next(); } catch { /* Projection failure cannot break ordered delivery. */ }
        }
    }

    private void SetRouterLifecycle(MainRouterLifecycle state, string reason)
    {
        lock (syncRoot)
        {
            if (mainRouterLifecycle == state) return;
            var previous = mainRouterLifecycle;
            mainRouterLifecycle = state;
            lifecycleRevision++;
            Log?.Invoke($"MAIN_ROUTER lifecycle={previous} -> {state} reason={reason}");
        }
    }

    // Initial observation must not implicitly enable startup restore. It only
    // publishes network availability and does not mutate any component.
    public async Task CaptureInitialPhysicalNetworkAsync(CancellationToken ct = default)
    {
        await reconcileGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (physicalNetworkAvailability != PhysicalNetworkAvailability.Unknown) return;
            Log?.Invoke("PHYSICAL_NETWORK state=unknown phase=initializing");
            NetworkSnapshot? physical;
            try { physical = PhysicalNetworkProvider.ResolveCurrentBinding(GetSettings().PhysicalInterface, capturedPhysical); }
            catch (InvalidOperationException) { physical = null; }
            capturedPhysical = physical;
            physicalNetworkAvailability = PhysicalNetworkProvider.IsUsable(physical)
                ? PhysicalNetworkAvailability.Ready : PhysicalNetworkAvailability.Unavailable;
            Log?.Invoke($"PHYSICAL_NETWORK state={physicalNetworkAvailability.ToString().ToLowerInvariant()} ifIndex={physical?.Index ?? 0} phase=initial-capture");
            SetConvergenceState(CurrentConvergenceState with { PhysicalNetworkReady = physicalNetworkAvailability == PhysicalNetworkAvailability.Ready });
        }
        finally { reconcileGate.Release(); }
    }

    // All watchdog observations pass through the coordinator's lifecycle. An
    // observation from a previous start/stop epoch is discarded after await.
    public async Task<RouterHealth?> ObserveTunnelAsync(string source, CancellationToken ct = default)
    {
        var sequence = Interlocked.Increment(ref healthSequence);
        MainRouterLifecycle lifecycle;
        long revision;
        var desired = DesiredStateProvider.GetCurrentDesiredState();
        RouterHealth health;
        lock (syncRoot)
        {
            lifecycle = mainRouterLifecycle;
            if (!desired.MainVpnEnabled || !desired.TunEnabled) lifecycle = MainRouterLifecycle.StoppedByDesired;
            revision = lifecycleRevision;
        }
        var raw = await TunnelInspector.InspectAsync(RouterRuntime.IsRunning, RouterRuntime.ListenPort,
            desired.TunEnabled, lifecycle, ct).ConfigureAwait(false);
        lock (syncRoot)
        {
            if (revision != lifecycleRevision || desired != DesiredStateProvider.GetCurrentDesiredState())
            {
                Log?.Invoke($"TUN_OBSERVE source={source} discarded=stale-lifecycle");
                return null;
            }
            health = TunHealthClassifier.ApplyLifecycle(raw, lifecycle);
            Log?.Invoke($"TUN_OBSERVE source={source} processAlive={raw.CoreProcessHealthy} interfacePresent={raw.TunInterfacePresent} routesPresent={raw.TunRoutesPresent} rawClassification={(raw.CoreProcessHealthy && raw.TunInterfacePresent && raw.TunRoutesPresent ? "present" : "missing")} runtimeLifecycle={lifecycle} finalStatus={health.StructuralStatus}");
            if (lifecycle == MainRouterLifecycle.Starting && (!raw.CoreProcessHealthy || !raw.TunInterfacePresent || !raw.TunRoutesPresent))
                Log?.Invoke("TUN_SIGNAL suppressed=StructuralFailure reason=runtime-is-starting");
            if (lifecycle == MainRouterLifecycle.Running && !health.CoreProcessHealthy)
                SetRouterLifecycle(MainRouterLifecycle.FailedUnexpectedly, "unexpected-process-exit");
        }
        PublishTunHealth(health, sequence, revision);
        return health;
    }

    private void PublishTunHealth(RouterHealth health, long sequence = 0, long? expectedLifecycle = null)
    {
        if (sequence == 0) sequence = Interlocked.Increment(ref healthSequence);
        MutateObserved(observed =>
        {
        if (observed == null || sequence < publishedHealthSequence || expectedLifecycle.HasValue && expectedLifecycle != lifecycleRevision) return null;
        publishedHealthSequence = sequence;
        return observed with
        {
            MainRouterStatus = RouterRuntime.VpnRunning
                ? (health.StructuralFailure ? ObservedComponentState.RunningDegraded : ObservedComponentState.RunningHealthy)
                : ObservedComponentState.Stopped,
            TunHealth = health.HealthState, TunStatus = health.StructuralStatus,
            TunReady = health.StructuralStatus == TunStructuralStatus.Healthy,
            TunObservedHealthy = health.StructuralStatus == TunStructuralStatus.Healthy,
            StructuralTunFailure = health.StructuralFailure
        };
        });
    }

    private void ObserveRouterLifecycle(DesiredRuntimeState desired)
    {
        if (!desired.MainVpnEnabled)
        {
            SetRouterLifecycle(RouterRuntime.IsRunning || RouterRuntime.VpnRunning
                ? MainRouterLifecycle.Stopping
                : MainRouterLifecycle.StoppedByDesired, "desired-off");
            return;
        }

        if (RouterRuntime.VpnRunning)
        {
            // Keep Starting until WaitTunReady has positively observed the
            // tunnel.  A non-TUN router can be considered running directly.
            if (!desired.TunEnabled || mainRouterLifecycle is not (MainRouterLifecycle.Starting or MainRouterLifecycle.FailedUnexpectedly))
                SetRouterLifecycle(MainRouterLifecycle.Running, "observed-running");
            return;
        }

        if (mainRouterLifecycle is MainRouterLifecycle.Running or MainRouterLifecycle.FailedUnexpectedly)
            SetRouterLifecycle(MainRouterLifecycle.FailedUnexpectedly, "process-missing");
        else
            SetRouterLifecycle(MainRouterLifecycle.Starting, "desired-on");
    }

    private async Task ReconciliationLoopAsync()
    {
        try { await ReconciliationLoopCoreAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested) { SetRestoreState(StartupRestoreState.Cancelled); }
    }

    private async Task ReconciliationLoopCoreAsync()
    {
        var token = lifetimeCts.Token;

        while (!token.IsCancellationRequested)
        {
            ReconcileReason reason;
            lock (syncRoot)
            {
                if (!pendingReconcile)
                {
                    var hasPendingFailures = NextAutomaticRetryAt() is {} due && Clock.UtcNow >= due;
                    if (!hasPendingFailures && CurrentRestoreState != StartupRestoreState.WaitingForNetwork)
                    {
                        loopTask = null;
                        return;
                    }
                }

                pendingReconcile = false;
                reason = pendingReason;
                pendingReason = ReconcileReason.ExternalConditionResolved;
                wakeSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            TimeSpan waitDelay = TimeSpan.Zero;
            if (reason == ReconcileReason.ExternalConditionResolved)
            {
                DateTimeOffset? nextRetry;
                lock (syncRoot) nextRetry = NextAutomaticRetryAt();
                if (nextRetry > Clock.UtcNow)
                {
                    await WaitForSignalOrDelayAsync(nextRetry.Value - Clock.UtcNow, token).ConfigureAwait(false);
                    lock (syncRoot)
                    {
                        if (pendingReconcile) { reason = pendingReason; pendingReconcile = false; }
                    }
                }
            }
            await reconcileGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                waitDelay = await ExecuteReconcilePassAsync(reason, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"RECONCILE error={ex.Message}");
            }
            finally
            {
                reconcileGate.Release();
            }

            if (token.IsCancellationRequested) break;

            if (waitDelay > TimeSpan.Zero)
            {
                SetRestoreState(StartupRestoreState.PendingRetry);
                await WaitForSignalOrDelayAsync(waitDelay, token).ConfigureAwait(false);
            }
            else if (CurrentRestoreState == StartupRestoreState.WaitingForNetwork)
            {
                await WaitForSignalOrDelayAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            }
            else
            {
                lock (syncRoot)
                {
                    if (!pendingReconcile)
                    {
                        var hasPendingFailures = NextAutomaticRetryAt() is {} due && Clock.UtcNow >= due;
                        if (!hasPendingFailures)
                        {
                            loopTask = null;
                            return;
                        }
                    }
                }
            }
        }

        if (lifetimeCts.IsCancellationRequested)
        {
            SetRestoreState(StartupRestoreState.Cancelled);
        }
    }

    public OpenVpnRetryKey GetOpenVpnRetryKey(DesiredRuntimeState desired, AppSettings settings, NetworkSnapshot? physical)
    {
        var targetOvpnId = desired.SelectedOpenVpnProfileId ?? settings.OpenVpnProfileId;
        var profile = settings.Profiles.FirstOrDefault(p => p.Id == targetOvpnId && p.IsOpenVpn);
        int profileRev = profile != null ? HashCode.Combine(profile.OpenVpnConfig, profile.AllowPublicPushedRoutes, settings.OpenVpnDns, profile.OpenVpnLegacyProviderRequired) : 0;
        int credRev = profile != null ? HashCode.Combine(profile.Username, profile.Password) : 0;
        long physGen = physical == null ? 0 : HashCode.Combine(physical.Index, physical.Address, physical.Dns, physical.DefaultRoute, Interlocked.Read(ref openVpnPhysicalGeneration));
        return new OpenVpnRetryKey(targetOvpnId, profileRev, physGen, credRev, OpenVpnRuntime.NativeModuleRevision);
    }

    private async Task<TimeSpan> ExecuteReconcilePassAsync(ReconcileReason reason, CancellationToken token)
    {
        long gen = Interlocked.Increment(ref generation);
        Log?.Invoke($"RECONCILE gen={gen} trigger={reason} begin reconcileRevision={gen}");
        var status = "completed";
        try { return await ExecuteReconcileCoreAsync(gen, reason, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { status = "cancelled"; throw; }
        catch { status = "failed"; throw; }
        finally { Log?.Invoke($"RECONCILE gen={gen} trigger={reason} complete status={status} phase={CurrentConvergenceState.Phase}"); }
    }
    private async Task<TimeSpan> ExecuteReconcileCoreAsync(long gen, ReconcileReason reason, CancellationToken token)
    {

        // Always reread fresh desired state
        var desired = DesiredStateProvider.GetCurrentDesiredState();
        var settings = GetSettings();

        ObserveRouterLifecycle(desired);
        if (physicalNetworkAvailability == PhysicalNetworkAvailability.Unknown)
            Log?.Invoke("PHYSICAL_NETWORK state=unknown phase=initializing");

        NetworkSnapshot? physical;
        try { physical = PhysicalNetworkProvider.ResolveCurrentBinding(settings.PhysicalInterface, CurrentObservedState?.PhysicalNetwork); }
        catch (InvalidOperationException) { physical = null; }
        bool physicalUsable = PhysicalNetworkProvider.IsUsable(physical);
        physicalNetworkAvailability = physicalUsable ? PhysicalNetworkAvailability.Ready : PhysicalNetworkAvailability.Unavailable;
        var previousPhysical = CurrentObservedState?.PhysicalNetwork ?? capturedPhysical;
        capturedPhysical = physical;
        if (!PhysicalNetwork.SameBinding(previousPhysical, physical))
        {
            var reasonText = previousPhysical != null && physical != null && !PhysicalNetwork.IsUsablePhysicalBinding(previousPhysical)
                ? "current-unusable"
                : reason.ToString();
            bool alreadySignaled;
            lock (syncRoot)
            {
                alreadySignaled = physicalSignalPending && PhysicalNetwork.SameBinding(signaledPhysical, physical);
                physicalSignalPending = false;
                if (!alreadySignaled) Interlocked.Increment(ref openVpnPhysicalGeneration);
            }
            if (!alreadySignaled) OpenVpnRuntime.PhysicalNetworkChanged();
            var physicalGeneration = PhysicalNetwork.NextGeneration();
            Log?.Invoke($"PHYSICAL_NETWORK_SWITCH oldIfIndex={previousPhysical?.Index ?? 0} newIfIndex={physical?.Index ?? 0} oldName={previousPhysical?.Name ?? "none"} newName={physical?.Name ?? "none"} reason={reasonText} generation={physicalGeneration}");
        }

        // Real observation of TUN health
        var inspectedSequence = Interlocked.Increment(ref healthSequence);
        var tunHealth = TunHealthClassifier.ApplyLifecycle(
            await TunnelInspector.InspectAsync(RouterRuntime.IsRunning, RouterRuntime.ListenPort, desired.TunEnabled, mainRouterLifecycle, token).ConfigureAwait(false),
            mainRouterLifecycle);
        bool tunConfigured = RouterRuntime.TunActive;
        TunStructuralStatus tunStatus = tunHealth.StructuralStatus;
        bool tunObservedHealthy = tunStatus == TunStructuralStatus.Healthy && !tunHealth.StructuralFailure;
        bool structuralTunFailure = desired.MainVpnEnabled && desired.TunEnabled && tunHealth.StructuralFailure;
        Log?.Invoke($"TUN_OBSERVE processAlive={tunHealth.CoreProcessHealthy} interfacePresent={tunHealth.TunInterfacePresent} routesPresent={tunHealth.TunRoutesPresent} localDataPath={tunHealth.LocalDataPathHealthy} rawClassification={tunHealth.StructuralStatus} runtimeLifecycle={mainRouterLifecycle} finalStatus={tunStatus} generation={gen}");
        if (mainRouterLifecycle == MainRouterLifecycle.Starting && tunHealth.StructuralFailure)
            Log?.Invoke("TUN_SIGNAL suppressed=StructuralFailure reason=runtime-is-starting");
        Log?.Invoke($"TUN_SIGNAL source=coordinator classification={(structuralTunFailure ? "structural" : tunStatus is TunStructuralStatus.TransientDegraded or TunStructuralStatus.Unknown ? "weak" : "healthy")}");

        var zapretDetail = ZapretRuntime.ObservedState;
        var observedZapret = ZapretRuntime.IsRunning
            ? (zapretDetail == null || zapretDetail.IsReady && zapretDetail.Owned ? ObservedComponentState.RunningHealthy : ObservedComponentState.RunningDegraded)
            : ObservedComponentState.Stopped;
        var observedRouter = RouterRuntime.VpnRunning ? (structuralTunFailure ? ObservedComponentState.RunningDegraded : ObservedComponentState.RunningHealthy) :
                             RouterRuntime.IsRunning ? ObservedComponentState.RunningDegraded : ObservedComponentState.Stopped;
        var dependenciesHealthy = RouterRuntime.DependenciesHealthy;
        lock (syncRoot)
        {
            if (desired.MainVpnEnabled && !dependenciesHealthy &&
                (!dependencyRecovery || dependencyRecoveredAt.HasValue))
            {
                dependencyRecovery = true; dependencyRecoveredAt = null;
                RecordFailure(ComponentId.MainRouter, "owned-xray-exit", gen);
                Log?.Invoke("XRAY_RECOVERY requested");
            }
            // A child that repeatedly exits shortly after startup must not reset
            // the retry budget on every successful local listener handshake.
            if (dependencyRecovery && dependenciesHealthy && dependencyRecoveredAt.HasValue &&
                Clock.UtcNow - dependencyRecoveredAt.Value >= TimeSpan.FromSeconds(30))
            { dependencyRecovery = false; dependencyRecoveredAt = null; componentRetries.Remove(ComponentId.MainRouter); Log?.Invoke("XRAY_RECOVERY result=stable"); }
        }
        var targetOpenVpn = settings.Profiles.FirstOrDefault(p => p.Id == (desired.SelectedOpenVpnProfileId ?? settings.OpenVpnProfileId) && p.IsOpenVpn);
        var observedOpenVpn = ObserveOpenVpn(targetOpenVpn);

        bool openVpnRoutesPendingRetry;
        lock (syncRoot)
        {
            // A failed stop may have terminated the process but left cleanup unfinished.
            // Only successful execution clears its retry; process absence alone does not.
            if (desired.MainVpnEnabled && desired.TunEnabled && tunObservedHealthy && !structuralTunFailure)
                componentRetries.Remove(ComponentId.Tun);
            if (!desired.MainVpnEnabled || !desired.TunEnabled) componentRetries.Remove(ComponentId.Tun);

            openVpnRoutesPendingRetry = componentRetries.ContainsKey(ComponentId.OpenVpnRoutes);
        }

        bool routesPresentInTable = true;
        if (OpenVpnRuntime.IsRunning && OpenVpnRuntime.Link is {} observedLink)
        {
            var obsResult = VerifyOpenVpnRoutesInRouteTable != null
                ? VerifyOpenVpnRoutesInRouteTable(observedLink.Index, observedLink.LearnedRoutes)
                : OpenVpnRuntime.VerifyRoutes();
            routesPresentInTable = obsResult.Status == RouteObservationStatus.Verified;
        }

        bool openVpnRoutesInstalled = OpenVpnRuntime.IsRunning && OpenVpnRuntime.Link != null && RouterRuntime.OpenVpnOverlayReady && !openVpnRoutesPendingRetry && routesPresentInTable;

        var observed = new ObservedRuntimeState(
            MainRouterStatus: observedRouter,
            ZapretStatus: observedZapret,
            OpenVpnStatus: observedOpenVpn,
            TunReady: tunObservedHealthy,
            PhysicalNetwork: physical,
            PhysicalUsable: physicalUsable,
            ActiveVpnProfileId: RouterRuntime.ActiveProfileId,
            ActiveOpenVpnProfileId: OpenVpnRuntime.ActiveProfileId,
            ActiveZapretStrategy: ZapretRuntime.ActiveStrategy,
            TunConfigured: tunConfigured,
            TunObservedHealthy: tunObservedHealthy,
            StructuralTunFailure: structuralTunFailure,
            OpenVpnRoutesInstalled: openVpnRoutesInstalled,
            ZapretDetail: zapretDetail,
            VpnUpstreamHealthy: VpnUpstreamHealthy,
            TunHealth: tunHealth.HealthState,
            TunStatus: tunStatus,
            PhysicalNetworkState: physicalNetworkAvailability,
            OpenVpnGatewayReady: RouterRuntime.OpenVpnGatewayReady
        );

        MutateObserved(current =>
        {
            if (current != null && publishedHealthSequence > inspectedSequence)
                observed = observed with { TunReady=current.TunReady, TunHealth=current.TunHealth, TunStatus=current.TunStatus,
                    TunObservedHealthy=current.TunObservedHealthy, StructuralTunFailure=current.StructuralTunFailure, MainRouterStatus=current.MainRouterStatus };
            else publishedHealthSequence = inspectedSequence;
            return observed;
        });
        structuralTunFailure = observed.StructuralTunFailure;
        tunStatus = observed.TunStatus;

        bool suppressed = reason == ReconcileReason.TunStructuralFailure && !structuralTunFailure;
        if (suppressed)
        {
            Log?.Invoke("TUN_SIGNAL source=local-probe classification=weak");
            Log?.Invoke($"TUN_OBSERVE status={tunStatus}");
            Log?.Invoke($"TUN_EVENT suppressed=TunStructuralFailure reason=fresh-health-is-{(tunStatus == TunStructuralStatus.Healthy ? "healthy" : "transient")}");
        }

        // Build canonical EffectiveRuntimeConfig
        var effectiveConfig = EffectiveRuntimeConfigBuilder.Build(
            settings, desired, physical, CurrentOpenVpnRoutes(desired));
        var diff = RuntimeSettingsDiff.Compute(lastAppliedConfig, effectiveConfig, physical);
        if (physical != null && desired.ZapretEnabled && ZapretRuntime.ConfigurationFingerprint(effectiveConfig.TargetSettings!, physical) is {} expectedFingerprint &&
            zapretDetail?.ConfigFingerprint != expectedFingerprint)
            diff = diff with { ZapretProfileChanged = true };
        LastDiff = diff;

        Dictionary<ComponentId, int> countsSnapshot;
        lock (syncRoot)
        {
            countsSnapshot = componentRetries.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Failures);
        }

        var ovpnKey = GetOpenVpnRetryKey(desired, settings, physical);
        lock(syncRoot)
        {
            if(routeRetryKey!=ovpnKey || reason==ReconcileReason.ManualRetry)
            {componentRetries.Remove(ComponentId.OpenVpnRoutes);routeRetryKey=ovpnKey;}
        }
        if (!desired.OpenVpnEnabled)
        {
            CancelOpenVpnOperation();
            if (!OpenVpnRuntime.RequiresStop)
            {
                lock (syncRoot)
                {
                    componentRetries.Remove(ComponentId.OpenVpnLink);
                    componentRetries.Remove(ComponentId.OpenVpnRoutes);
                }
            }
        }
        else if (reason == ReconcileReason.ManualRetry) OpenVpnRetryController.GrantManualRetry(ovpnKey);
        else if (desired.OpenVpnEnabled && OpenVpnRuntime.DataPathRecoveryRequired &&
            OpenVpnRetryController.State == OpenVpnRetryState.Connected)
        {
            // A previously established process has failed its reconnect probe.
            // Charge that failure once before allowing a full owned restart.
            OpenVpnRetryController.RecordAttemptStarted(ovpnKey, Clock.UtcNow);
            OpenVpnRetryController.RecordFailure(ovpnKey, OpenVpnFailureClass.TransientNetwork, "OpenVPN datapath probe timed out.", Clock.UtcNow);
        }
        bool canStartOpenVpn = true;
        TimeSpan? ovpnRetryDelay = null;
        if (desired.OpenVpnEnabled)
        {
            canStartOpenVpn = OpenVpnRetryController.CanAttempt(ovpnKey, Clock.UtcNow, reason);
            if (!canStartOpenVpn && OpenVpnRetryController.NextAttemptAt.HasValue)
            {
                var remaining = OpenVpnRetryController.NextAttemptAt.Value - Clock.UtcNow;
                if (remaining > TimeSpan.Zero) ovpnRetryDelay = remaining;
            }
        }

        var plan = RuntimePlanner.CreatePlan(
            desired, observed, effectiveConfig.TargetSettings ?? settings, countsSnapshot, BackoffIntervals, diff,
            canStartOpenVpn: canStartOpenVpn,
            openVpnRetryDelay: ovpnRetryDelay);
        // Trigger reasons are diagnostic; the canonical plan uses fresh health.
        // A stale TUN signal cannot veto independent desired-state actions.
        if (physicalUsable && physical != null)
        {
            Log?.Invoke($"PHYSICAL_NETWORK generation={gen} state=ready\n  ifIndex={physical.Index}\n  name={physical.Name}\n  ipv4={physical.Address}\n  gateway={physical.DefaultRoute}");
        }
        else
        {
            Log?.Invoke($"PHYSICAL_NETWORK generation={gen} state=unavailable");
            if (desired.ZapretEnabled)
            {
                Log?.Invoke("ZAPRET_RECONCILE pending=network");
            }
        }
        Log?.Invoke($"DESIRED vpn={(desired.MainVpnEnabled ? "on" : "off")} tun={(desired.TunEnabled ? "on" : "off")} zapret={(desired.ZapretEnabled ? "on" : "off")} openvpn={(desired.OpenVpnEnabled ? "on" : "off")} vpnProfile={desired.SelectedVpnProfileId} openVpnProfile={desired.SelectedOpenVpnProfileId}");
        Log?.Invoke($"OBSERVED physical={(observed.PhysicalUsable ? "ready" : "unavailable")} mainVpn={(observedRouter == ObservedComponentState.RunningHealthy ? "on" : "off")} activeVpnProfile={observed.ActiveVpnProfileId} tun={(observed.TunObservedHealthy == true ? "ready" : (observed.StructuralTunFailure ? "structural_failure" : "missing"))} zapret={(observedZapret == ObservedComponentState.RunningHealthy ? "running" : "stopped")} openVpnLink={(observedOpenVpn == ObservedComponentState.RunningHealthy ? "on" : "off")} openVpnRoutes={(openVpnRoutesInstalled ? "installed" : "unsatisfied")}");
        Log?.Invoke($"DIFF mainVpn={diff.VpnChanged.ToString().ToLowerInvariant()} tun={diff.TunChanged.ToString().ToLowerInvariant()} vpnProfile={diff.VpnProfileChanged.ToString().ToLowerInvariant()} zapret={diff.ZapretChanged.ToString().ToLowerInvariant()} openVpn={diff.OpenVpnChanged.ToString().ToLowerInvariant()} physical={diff.PhysicalBindingChanged.ToString().ToLowerInvariant()}");
        Log?.Invoke($"PLAN [{string.Join(",", plan.Actions.Select(a => a.Type))}]");

        if (plan.Actions.Count == 0)
        {
            if (desired.OpenVpnEnabled && OpenVpnRuntime.Reconnecting)
            {
                SetConvergenceState(new(physicalUsable, false, [ComponentId.OpenVpnLink], "OpenVPN переподключается; корпоративные маршруты закрыты", Phase: OpenVpnRuntime.RuntimePhase == OpenVpnRuntimePhase.LongReconnect ? ConvergencePhase.Degraded : ConvergencePhase.Reconciling));
                if(dependencyRecovery && dependencyRecoveredAt.HasValue)
                    return TimeSpan.FromSeconds(30)-(Clock.UtcNow-dependencyRecoveredAt.Value);
                return TimeSpan.Zero;
            }
            if (plan.PendingRetryComponents.Count == 0)
            {
                SetRestoreState(StartupRestoreState.Completed);
                SetConvergenceState(new RuntimeConvergenceState(physicalUsable, DesiredSatisfied: true, [], null, Phase: ConvergencePhase.Converged));
                Log?.Invoke($"SATISFACTION pending=[]");
                if(dependencyRecovery && dependencyRecoveredAt.HasValue)
                    return TimeSpan.FromSeconds(30)-(Clock.UtcNow-dependencyRecoveredAt.Value);
                return TimeSpan.Zero;
            }
            else
            {
                SetConvergenceState(new RuntimeConvergenceState(physicalUsable, DesiredSatisfied: false, plan.PendingRetryComponents, null, Phase: ConvergencePhase.Degraded));
                Log?.Invoke($"SATISFACTION pending=[{string.Join(",", plan.PendingRetryComponents)}]");
                return AutomaticRetryDelay();
            }
        }

        SetConvergenceState(new(physicalUsable, false, plan.Actions.Where(a => a.TargetComponent.HasValue).Select(a => a.TargetComponent!.Value).Distinct().ToArray(), null, ConvergencePhase.Reconciling));
        var failedActions = new HashSet<PlanActionType>();
        bool mainRouterStartedInBatch = false;
        using (CreateBatch?.Invoke())
        {
            foreach (var action in plan.Actions)
            {
                token.ThrowIfCancellationRequested();
                if (DesiredStateProvider.GetCurrentDesiredState() != desired)
                { RequestReconcile(ReconcileReason.UserChangedSettings); return TimeSpan.Zero; }

                if (action.Prerequisite.HasValue && failedActions.Contains(action.Prerequisite.Value))
                {
                    failedActions.Add(action.Type);
                    Log?.Invoke($"ACTION {action.Type} result=skipped prerequisite={action.Prerequisite.Value}");
                    continue;
                }

                settings = GetSettings();
                effectiveConfig = EffectiveRuntimeConfigBuilder.Build(settings, desired, physical, CurrentOpenVpnRoutes(desired));
                if (desired.MainVpnEnabled && action.Type is PlanActionType.StartMainRouter or PlanActionType.RestartMainRouterForStructuralTunFailure
                    or PlanActionType.EnsureMainRouterForProfileChange or PlanActionType.EnsureMainRouterForPhysicalBinding or PlanActionType.WaitTunReady)
                {
                    var startedAndHealthy = mainRouterStartedInBatch && RouterRuntime.VpnRunning && RouterRuntime.DependenciesHealthy;
                    lock(syncRoot)
                    {
                        // Suppression belongs to the component/configuration, not
                        // to the event that happened to wake the reconcile loop.
                        if (reason == ReconcileReason.ManualRetry || failedMainConfig != null &&
                            RuntimeSettingsDiff.Compute(failedMainConfig,effectiveConfig,physical).HasRouterChanges)
                        { componentRetries.Remove(ComponentId.MainRouter); componentRetries.Remove(ComponentId.Tun); }
                        else if (MainRouterRetryPending && !(action.Type == PlanActionType.WaitTunReady && startedAndHealthy))
                        {
                            failedActions.Add(action.Type);
                            Log?.Invoke($"ACTION {action.Type} result=deferred reason=component-backoff");
                            continue;
                        }
                    }
                }
                switch (action.Type)
                {
                    case PlanActionType.WaitForPhysicalNetwork:
                        SetRestoreState(StartupRestoreState.WaitingForNetwork);
                        SetConvergenceState(new RuntimeConvergenceState(false, false, new[] { desired.MainVpnEnabled ? ComponentId.MainRouter : (ComponentId?)null, desired.ZapretEnabled ? ComponentId.Zapret : null, desired.OpenVpnEnabled ? ComponentId.OpenVpnLink : null }.Where(c => c.HasValue).Select(c => c!.Value).ToArray(), "Ожидание физической сети", Phase: ConvergencePhase.WaitingForPhysicalNetwork, LastBlockingCondition: "Физическая сеть недоступна"));
                        Log?.Invoke($"PHYSICAL_NETWORK generation={PhysicalNetwork.PhysicalGeneration} state=unavailable");
                        Log?.Invoke("ACTION WaitForPhysicalNetwork result=waiting");
                        return TimeSpan.Zero;

                    case PlanActionType.StartZapret:
                        if (IsZapretTesting)
                        {
                            Log?.Invoke("ACTION StartZapret result=skipped reason=zapret-testing-active");
                            break;
                        }
                        SetRestoreState(StartupRestoreState.Restoring);
                        try
                        {
                            await ZapretRuntime.EnsureRunningAsync(effectiveConfig.TargetSettings ?? settings, null, token).ConfigureAwait(false);
                            var activeObs = ZapretRuntime.ObservedState;
                            if (!ZapretRuntime.IsRunning || activeObs is { IsReady: false } || activeObs is { Owned: false })
                                throw new IOException("Zapret не подтвердил готовность захвата.");
                            Log?.Invoke($"ZAPRET_PROCESS state=started pid={activeObs?.ProcessId ?? 0} ifIndex={activeObs?.BoundPhysicalInterfaceIndex ?? 0} fingerprint={activeObs?.ConfigFingerprint ?? ""}");
                            lastAppliedConfig = (lastAppliedConfig ?? EffectiveRuntimeConfig.Empty) with
                            {
                                ZapretEnabled = true,
                                ZapretStrategy = effectiveConfig.ZapretStrategy,
                                Scenario = effectiveConfig.Scenario
                            };
                            lock (syncRoot) componentRetries.Remove(ComponentId.Zapret);
                            Log?.Invoke("ACTION StartZapret result=ok");
                        }
                        catch (Exception ex)
                        {
                            failedActions.Add(PlanActionType.StartZapret);
                            RecordFailure(ComponentId.Zapret, ProcessHost.Redact(ex.Message), gen);
                            Log?.Invoke($"ACTION StartZapret result=failed error={ex.Message}");
                        }
                        break;

                    case PlanActionType.StopZapret:
                        if (IsZapretTesting)
                        {
                            Log?.Invoke("ACTION StopZapret result=skipped reason=zapret-testing-active");
                            break;
                        }
                        try
                        {
                            await ZapretRuntime.EnsureStoppedAsync(token).ConfigureAwait(false);
                            if (ZapretRuntime.IsRunning) throw new IOException("Zapret продолжает работать после остановки.");
                            lastAppliedConfig = (lastAppliedConfig ?? EffectiveRuntimeConfig.Empty) with
                            {
                                ZapretEnabled = false
                            };
                            lock (syncRoot) componentRetries.Remove(ComponentId.Zapret);
                            Log?.Invoke("ACTION StopZapret result=ok");
                        }
                        catch (Exception ex)
                        {
                            failedActions.Add(PlanActionType.StopZapret);
                            RecordFailure(ComponentId.Zapret, ProcessHost.Redact(ex.Message), gen);
                            Log?.Invoke($"ACTION StopZapret result=failed error={ex.Message}");
                        }
                        break;

                    case PlanActionType.StartMainRouter:
                    case PlanActionType.RestartMainRouterForStructuralTunFailure:
                        SetRestoreState(StartupRestoreState.Restoring);
                        SetRouterLifecycle(MainRouterLifecycle.Starting, $"action-{action.Type}");
                        try
                        {
                            if (physical != null)
                            {
                                var reasonStr = action.Type == PlanActionType.RestartMainRouterForStructuralTunFailure
                                    ? "restart-structural-tun"
                                    : "reconcile-main-vpn";
                                await RouterRuntime.EnsureRunningAsync(effectiveConfig.TargetSettings ?? settings, physical, reasonStr, token).ConfigureAwait(false);
                                // Successful local startup still requires authoritative TUN
                                // readiness in this batch. Retaining the Xray failure budget
                                // must suppress new launches, not this validation step.
                                mainRouterStartedInBatch = true;
                                lastAppliedConfig = (lastAppliedConfig ?? EffectiveRuntimeConfig.Empty) with
                                {
                                    VpnEnabled = true,
                                    TunEnabled = desired.TunEnabled,
                                    VpnProfileId = effectiveConfig.VpnProfileId,
                                    RoutingRulesFingerprint = effectiveConfig.RoutingRulesFingerprint,
                                    DnsPolicyFingerprint = effectiveConfig.DnsPolicyFingerprint,
                                    PhysicalBindingFingerprint = effectiveConfig.PhysicalBindingFingerprint
                                };
                                lock (syncRoot)
                                {
                                    if (dependencyRecovery)
                                    {
                                        dependencyRecoveredAt = Clock.UtcNow;
                                        // Retain the failure budget during stabilization without
                                        // leaving an already-due retry that spins PLAN [].
                                        if(componentRetries.TryGetValue(ComponentId.MainRouter,out var stabilizing))stabilizing.NextRetryAt=Clock.UtcNow+TimeSpan.FromSeconds(30);
                                        Log?.Invoke("XRAY_RECOVERY result=started");
                                    }
                                    else componentRetries.Remove(ComponentId.MainRouter);
                                    if (action.Type == PlanActionType.RestartMainRouterForStructuralTunFailure)
                                    {
                                        componentRetries.Remove(ComponentId.Tun);
                                    }
                                }
                                if (!desired.TunEnabled) SetRouterLifecycle(MainRouterLifecycle.Running, "router-ready-without-tun");
                                if (action.Type == PlanActionType.EnsureMainRouterForProfileChange)
                                {
                                    var prof = settings.Profiles.FirstOrDefault(p => p.Id == effectiveConfig.VpnProfileId);
                                    if (prof != null)
                                    {
                                        Log?.Invoke($"VPN_PROFILE applied={prof.Name}");
                                    }
                                }
                                Log?.Invoke($"ACTION {action.Type} result=ok");
                            }
                            else
                            {
                                throw new InvalidOperationException("Физический сетевой адаптер не готов.");
                            }
                        }
                        catch (Exception ex)
                        {
                            failedActions.Add(action.Type);
                            SetRouterLifecycle(MainRouterLifecycle.FailedUnexpectedly, $"action-{action.Type}-failed");
                            var targetComp = action.Type == PlanActionType.RestartMainRouterForStructuralTunFailure
                                ? ComponentId.Tun
                                : ComponentId.MainRouter;
                            RecordFailure(targetComp, ProcessHost.Redact(ex.Message), gen);
                            if (dependencyRecovery) Log?.Invoke("XRAY_RECOVERY result=failed");
                            Log?.Invoke($"ACTION {action.Type} result=failed error={ex.Message}");
                        }
                        break;

                    case PlanActionType.EnsureMainRouterForProfileChange:
                    case PlanActionType.EnsureMainRouterForPhysicalBinding:
                        SetRestoreState(StartupRestoreState.Restoring);
                        SetRouterLifecycle(MainRouterLifecycle.Starting, $"action-{action.Type}");
                        try
                        {
                            if (physical != null)
                            {
                                var targetProfileId = desired.SelectedVpnProfileId ?? settings.MainProfileId;
                                var targetSettings = effectiveConfig.TargetSettings ?? settings;
                                if (targetProfileId.HasValue && targetSettings.MainProfileId != targetProfileId)
                                {
                                    targetSettings = JsonSettings.Clone(targetSettings);
                                    targetSettings.MainProfileId = targetProfileId;
                                }

                                var reasonStr = action.Type == PlanActionType.EnsureMainRouterForProfileChange
                                    ? "reconcile-profile-change"
                                    : "reconcile-physical-binding";

                                Log?.Invoke($"VPN_PROFILE intent old={observed.ActiveVpnProfileId} new={targetProfileId}");
                                await RouterRuntime.EnsureRunningAsync(targetSettings, physical, reasonStr, token).ConfigureAwait(false);
                                Log?.Invoke($"VPN_PROFILE applied={targetProfileId}");

                                lastAppliedConfig = (lastAppliedConfig ?? EffectiveRuntimeConfig.Empty) with
                                {
                                    VpnEnabled = true,
                                    TunEnabled = desired.TunEnabled,
                                    VpnProfileId = targetProfileId,
                                    RoutingRulesFingerprint = effectiveConfig.RoutingRulesFingerprint,
                                    DnsPolicyFingerprint = effectiveConfig.DnsPolicyFingerprint,
                                    PhysicalBindingFingerprint = effectiveConfig.PhysicalBindingFingerprint
                                };

                                lock (syncRoot)
                                {
                                    componentRetries.Remove(ComponentId.MainRouter);
                                }
                                if (!desired.TunEnabled) SetRouterLifecycle(MainRouterLifecycle.Running, "router-ready-without-tun");
                                Log?.Invoke($"ACTION {action.Type} targetProfile={targetProfileId} result=ok");
                            }
                            else
                            {
                                throw new InvalidOperationException("Физический сетевой адаптер не готов.");
                            }
                        }
                        catch (Exception ex)
                        {
                            failedActions.Add(action.Type);
                            SetRouterLifecycle(MainRouterLifecycle.FailedUnexpectedly, $"action-{action.Type}-failed");
                            RecordFailure(ComponentId.MainRouter, ProcessHost.Redact(ex.Message), gen);
                            Log?.Invoke($"ACTION {action.Type} result=failed error={ex.Message}");
                        }
                        break;

                    case PlanActionType.WaitTunReady:
                        SetRestoreState(StartupRestoreState.Restoring);
                        SetRouterLifecycle(MainRouterLifecycle.Starting, "wait-tun-ready");
                        var sw = Stopwatch.StartNew();
                        try
                        {
                            var health = TunHealthClassifier.ApplyLifecycle(await TunnelInspector.WaitForTunReadyAsync(
                                () => RouterRuntime.IsRunning,
                                () => RouterRuntime.ListenPort,
                                desired.TunEnabled,
                                mainRouterLifecycle,
                                TunReadyTimeout,
                                token).ConfigureAwait(false), mainRouterLifecycle);

                            if (health.StructuralStatus == TunStructuralStatus.Healthy)
                            {
                                SetRouterLifecycle(MainRouterLifecycle.Running, "tun-ready");
                                PublishTunHealth(health);
                                lock (syncRoot) componentRetries.Remove(ComponentId.Tun);
                                Log?.Invoke($"ACTION WaitTunReady result=ready duration={sw.Elapsed.TotalSeconds:F1}s");
                            }
                            else
                            {
                                SetRouterLifecycle(MainRouterLifecycle.FailedUnexpectedly, "tun-ready-timeout");
                                PublishTunHealth(health);
                                failedActions.Add(PlanActionType.WaitTunReady);
                                RecordFailure(ComponentId.Tun, health.StructuralStatus.ToString(), gen);
                                Log?.Invoke($"ACTION WaitTunReady result=failed duration={sw.Elapsed.TotalSeconds:F1}s");
                            }
                        }
                        catch (Exception ex)
                        {
                            SetRouterLifecycle(MainRouterLifecycle.FailedUnexpectedly, "tun-ready-error");
                            failedActions.Add(PlanActionType.WaitTunReady);
                            RecordFailure(ComponentId.Tun, ProcessHost.Redact(ex.Message), gen);
                            Log?.Invoke($"ACTION WaitTunReady result=failed error={ex.Message}");
                        }
                        break;

                    case PlanActionType.StopMainRouter:
                        SetRouterLifecycle(MainRouterLifecycle.Stopping, "action-stop");
                        try
                        {
                            await RouterRuntime.EnsureStoppedAsync(token).ConfigureAwait(false);
                            if (RouterRuntime.VpnRunning) throw new IOException("Основной VPN продолжает работать после остановки.");
                            lastAppliedConfig = (lastAppliedConfig ?? EffectiveRuntimeConfig.Empty) with
                            {
                                VpnEnabled = false
                            };
                            lock (syncRoot)
                            {
                                componentRetries.Remove(ComponentId.MainRouter);
                                componentRetries.Remove(ComponentId.Tun);
                                dependencyRecovery = false; dependencyRecoveredAt = null;
                            }
                            SetRouterLifecycle(MainRouterLifecycle.StoppedByDesired, "stopped");
                            Log?.Invoke("ACTION StopMainRouter result=ok");
                        }
                        catch (Exception ex)
                        {
                            failedActions.Add(PlanActionType.StopMainRouter);
                            RecordFailure(ComponentId.MainRouter, ProcessHost.Redact(ex.Message), gen);
                            Log?.Invoke($"ACTION StopMainRouter result=failed error={ex.Message}");
                        }
                        break;

                    case PlanActionType.StartOpenVpn:
                    {
                        SetRestoreState(StartupRestoreState.Restoring);
                        if (!OpenVpnRetryController.CanAttempt(ovpnKey, Clock.UtcNow, reason))
                        {
                            failedActions.Add(PlanActionType.StartOpenVpn);
                            Log?.Invoke($"ACTION StartOpenVpn result=deferred reason=openvpn-retry-gate state={OpenVpnRetryController.State}");
                            continue;
                        }
                        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                        long intent;
                        lock (syncRoot)
                        {
                            if (DesiredStateProvider.GetCurrentDesiredState() != desired ||
                                GetOpenVpnRetryKey(desired, GetSettings(), capturedPhysical) != ovpnKey ||
                                !OpenVpnRetryController.CanAttempt(ovpnKey, Clock.UtcNow, ReconcileReason.OpenVpnStateChanged))
                            { failedActions.Add(action.Type); continue; }
                            intent = openVpnIntent;
                            openVpnAttempt = attempt;
                            OpenVpnRetryController.RecordAttemptStarted(ovpnKey, Clock.UtcNow);
                        }
                        bool CurrentAttempt() => !attempt.IsCancellationRequested && intent == Interlocked.Read(ref openVpnIntent) &&
                            DesiredStateProvider.GetCurrentDesiredState() == desired && GetOpenVpnRetryKey(desired, GetSettings(), capturedPhysical) == ovpnKey;
                        try
                        {
                            var targetOvpnId = desired.SelectedOpenVpnProfileId ?? settings.OpenVpnProfileId;
                            var ovpnProfile = settings.Profiles.FirstOrDefault(p => p.Id == targetOvpnId && p.IsOpenVpn);
                            if (ovpnProfile == null)
                            {
                                RouterRuntime.InvalidateOpenVpnOverlay();
                                await RouterRuntime.ApplyOpenVpnOverlayAsync(settings, null, token).ConfigureAwait(false);
                                await OpenVpnRuntime.EnsureStoppedAsync(token).ConfigureAwait(false);
                                throw new OpenVpnFailureException(OpenVpnFailureClass.DeterministicLocalFatal, "Выберите существующий профиль OpenVPN.");
                            }
                            if ((OpenVpnRuntime.IsRunning || OpenVpnRuntime.Reconnecting) && OpenVpnRuntime.ActiveProfileId.HasValue && (OpenVpnRuntime.ActiveProfileId != ovpnProfile.Id || OpenVpnRuntime.NeedsRestart(ovpnProfile)))
                            {
                                RouterRuntime.InvalidateOpenVpnOverlay();
                                await RouterRuntime.ApplyOpenVpnOverlayAsync(settings, null, token).ConfigureAwait(false);
                                await OpenVpnRuntime.EnsureStoppedAsync(token).ConfigureAwait(false);
                            }
                            if (!CurrentAttempt()) throw new OperationCanceledException(attempt.Token);
                            var link = await OpenVpnRuntime.EnsureRunningAsync(ovpnProfile, settings.OpenVpnDns, attempt.Token).ConfigureAwait(false);
                            if (!CurrentAttempt()) throw new OperationCanceledException(attempt.Token);
                            if (link != null)
                            {
                                if (OnOpenVpnRoutesLearned != null)
                                {
                                    await OnOpenVpnRoutesLearned(ovpnProfile.Id, link.LearnedRoutes, attempt.Token).ConfigureAwait(false);
                                }
                                else
                                {
                                    ovpnProfile.LearnedRoutes = link.LearnedRoutes.ToList();
                                }
                            }
                            lastAppliedConfig = (lastAppliedConfig ?? EffectiveRuntimeConfig.Empty) with
                            {
                                OpenVpnEnabled = true,
                                OpenVpnProfileId = ovpnProfile.Id,
                                LearnedOpenVpnRoutesFingerprint = (link != null ? string.Join(",", link.LearnedRoutes.OrderBy(r => r)) : "") + "|dns=" + settings.OpenVpnDns
                            };
                            if (!CurrentAttempt()) throw new OperationCanceledException(attempt.Token);
                            OpenVpnRetryController.RecordSuccess(ovpnKey);
                            lock (syncRoot) componentRetries.Remove(ComponentId.OpenVpnLink);
                            Log?.Invoke("ACTION StartOpenVpn result=ok");
                        }
                        catch (Exception ex)
                        {
                            failedActions.Add(PlanActionType.StartOpenVpn);
                            if (!CurrentAttempt() || ex is OperationCanceledException)
                            {
                                try { await RouterRuntime.ApplyOpenVpnOverlayAsync(GetSettings(), null, CancellationToken.None).ConfigureAwait(false); }
                                finally { await OpenVpnRuntime.EnsureStoppedAsync(CancellationToken.None).ConfigureAwait(false); }
                                SetConvergenceState(new(physicalUsable, false, [ComponentId.OpenVpnLink], null, Phase: ConvergencePhase.Reconciling));
                                return TimeSpan.Zero;
                            }
                            var failureClass = OpenVpnFailureClassifier.Classify(ex.Message, ex);
                            OpenVpnRetryController.RecordFailure(ovpnKey, failureClass, ProcessHost.Redact(ex.Message), Clock.UtcNow);
                            RecordFailure(ComponentId.OpenVpnLink, ProcessHost.Redact(ex.Message), gen);
                            Log?.Invoke($"ACTION StartOpenVpn result=failed error={ProcessHost.Redact(ex.Message)} failureClass={failureClass} retryState={OpenVpnRetryController.State}");
                            Log?.Invoke("COMPONENT_FAILURE component=OpenVpn scope=local");
                            Log?.Invoke("DEPENDENCY_EFFECT blocked=[OpenVpnRoutes] unaffected=[MainVpn,Tun,Zapret]");
                        }
                        finally { lock (syncRoot) { if (ReferenceEquals(openVpnAttempt, attempt)) openVpnAttempt = null; } }
                        break;

                    }
                    case PlanActionType.StopOpenVpn:
                        try
                        {
                            try { await RouterRuntime.ApplyOpenVpnOverlayAsync(settings, null, token).ConfigureAwait(false); }
                            finally { await OpenVpnRuntime.EnsureStoppedAsync(token).ConfigureAwait(false); }
                            if (OpenVpnRuntime.RequiresStop) throw new IOException("Остановка/очистка OpenVPN ещё не завершена.");
                            lastAppliedConfig = (lastAppliedConfig ?? EffectiveRuntimeConfig.Empty) with
                            {
                                OpenVpnEnabled = false
                            };
                            if (!desired.OpenVpnEnabled)
                            {
                                OpenVpnRetryController.Cancel();
                            }
                            lock (syncRoot)
                            {
                                componentRetries.Remove(ComponentId.OpenVpnLink);
                                componentRetries.Remove(ComponentId.OpenVpnRoutes);
                            }
                            Log?.Invoke("ACTION StopOpenVpn result=ok");
                        }
                        catch (Exception ex)
                        {
                            failedActions.Add(PlanActionType.StopOpenVpn);
                            RecordFailure(ComponentId.OpenVpnLink, ProcessHost.Redact(ex.Message), gen);
                            Log?.Invoke($"ACTION StopOpenVpn result=failed error={ex.Message}");
                        }
                        break;

                    case PlanActionType.FinalizeRouting:
                    case PlanActionType.FinalizeOpenVpnRoutes:
                        lock(syncRoot)
                        {
                            var currentOwnerId = OpenVpnRuntime.Link?.RouteOwnerId;
                            if(desired.OpenVpnEnabled && componentRetries.TryGetValue(ComponentId.OpenVpnRoutes,out var retry))
                            {
                                if (retry.RouteOwnerId.HasValue && currentOwnerId.HasValue && retry.RouteOwnerId.Value != currentOwnerId.Value)
                                    componentRetries.Remove(ComponentId.OpenVpnRoutes);
                                else if (retry.NextRetryAt > Clock.UtcNow)
                                {
                                    failedActions.Add(action.Type);
                                    Log?.Invoke("ACTION FinalizeRouting result=deferred reason=component-backoff");
                                    continue;
                                }
                            }


                        }
                        var routeAttemptLink=OpenVpnRuntime.Link;
                        Log?.Invoke("OPENVPN_OVERLAY_UPDATE mainRouterRestart=false tunRestart=false");
                        if (FinalizeRoutingOverride != null)
                        {
                            try
                            {
                                await FinalizeRoutingOverride(token).ConfigureAwait(false);
                                lock (syncRoot) componentRetries.Remove(ComponentId.OpenVpnRoutes);
                                Log?.Invoke("ACTION FinalizeRouting result=ok");
                            }
                            catch (OperationCanceledException) when(token.IsCancellationRequested || DesiredStateProvider.GetCurrentDesiredState()!=desired || !ReferenceEquals(routeAttemptLink,OpenVpnRuntime.Link))
                            {failedActions.Add(action.Type);Log?.Invoke("ACTION FinalizeRouting result=obsolete");}
                            catch (Exception ex)
                            {
                                RouterRuntime.InvalidateOpenVpnOverlay();
                                failedActions.Add(action.Type);
                                RecordFailure(ComponentId.OpenVpnRoutes, ProcessHost.Redact(ex.Message), gen);
                                Log?.Invoke($"ACTION FinalizeRouting result=failed error={ex.Message}");
                            }
                        }
                        else
                        {
                            try
                            {
                                if (desired.OpenVpnEnabled) await OpenVpnRuntime.EnsureRoutesAsync(token).ConfigureAwait(false);
                                if (OpenVpnRuntime.Link is { ProfileId: not null } currentLink && OnOpenVpnRoutesLearned != null &&
                                    settings.Profiles.FirstOrDefault(p => p.Id == currentLink.ProfileId) is {} currentProfile &&
                                    !currentProfile.LearnedRoutes.SequenceEqual(currentLink.LearnedRoutes))
                                {
                                    await OnOpenVpnRoutesLearned(currentProfile.Id, currentLink.LearnedRoutes, token).ConfigureAwait(false);
                                    settings = GetSettings();
                                    effectiveConfig = EffectiveRuntimeConfigBuilder.Build(settings, desired, physical, CurrentOpenVpnRoutes(desired));
                                }
                                var overlayLink = desired.OpenVpnEnabled && OpenVpnRuntime.IsRunning ? OpenVpnRuntime.Link : null;
                                await RouterRuntime.ApplyOpenVpnOverlayAsync(effectiveConfig.TargetSettings ?? settings, overlayLink,
                                    () => DesiredStateProvider.GetCurrentDesiredState() == desired && (overlayLink == null || ReferenceEquals(overlayLink, OpenVpnRuntime.Link)), token).ConfigureAwait(false);
                                lock (syncRoot) componentRetries.Remove(ComponentId.OpenVpnRoutes);
                                Log?.Invoke("ACTION FinalizeRouting result=ok");
                            }
                            catch (OperationCanceledException) when(token.IsCancellationRequested || DesiredStateProvider.GetCurrentDesiredState()!=desired || !ReferenceEquals(routeAttemptLink,OpenVpnRuntime.Link))
                            {failedActions.Add(action.Type);Log?.Invoke("ACTION FinalizeRouting result=obsolete");}
                            catch (Exception ex)
                            {
                                RouterRuntime.InvalidateOpenVpnOverlay();
                                failedActions.Add(action.Type);
                                RecordFailure(ComponentId.OpenVpnRoutes, ProcessHost.Redact(ex.Message), gen);
                                Log?.Invoke($"ACTION FinalizeRouting result=failed error={ex.Message}");
                            }
                        }
                        break;
                }
            }
        }

        bool finalOpenVpnRoutesInstalled = false;
        if (desired.OpenVpnEnabled && OpenVpnRuntime.IsRunning && OpenVpnRuntime.Link is {} installedLink)
        {
            var routeCheck = VerifyOpenVpnRoutesInRouteTable?.Invoke(installedLink.Index, installedLink.LearnedRoutes)
                ?? OpenVpnRuntime.VerifyRoutes();
            if (!routeCheck.IsVerified)
            {
                RecordFailure(ComponentId.OpenVpnRoutes, routeCheck.Error ?? routeCheck.Status.ToString(), gen);
                Log?.Invoke($"OPENVPN_ROUTES observation={routeCheck.Status} error={routeCheck.Error}");
            }
            lock (syncRoot) finalOpenVpnRoutesInstalled = routeCheck.IsVerified && RouterRuntime.OpenVpnOverlayReady && !componentRetries.ContainsKey(ComponentId.OpenVpnRoutes);
        }
        token.ThrowIfCancellationRequested();
        // Publish successful components even when a different component will
        // remain in backoff. A local OpenVPN failure must not leave the UI's
        // observed Main VPN/Zapret snapshot at its pre-start values.
        MutateObserved(current => (current ?? observed) with
        {
            ActiveVpnProfileId = RouterRuntime.ActiveProfileId,
            ActiveOpenVpnProfileId = OpenVpnRuntime.ActiveProfileId,
            ActiveZapretStrategy = ZapretRuntime.ActiveStrategy,
            MainRouterStatus = RouterRuntime.VpnRunning ? ((current ?? observed).StructuralTunFailure ? ObservedComponentState.RunningDegraded : ObservedComponentState.RunningHealthy) : ObservedComponentState.Stopped,
            ZapretStatus = ZapretRuntime.IsRunning ? ObservedComponentState.RunningHealthy : ObservedComponentState.Stopped,
            OpenVpnStatus = ObserveOpenVpn(targetOpenVpn),
            OpenVpnRoutesInstalled = finalOpenVpnRoutesInstalled,
            ZapretDetail = ZapretRuntime.ObservedState
        });
        // Post-execution evaluation
        List<ComponentId> activeFailures;
        lock (syncRoot)
        {
            activeFailures = componentRetries.Where(kvp => kvp.Value.Failures > 0).Select(kvp => kvp.Key).ToList();
        }

        if (activeFailures.Count > 0)
        {
            Log?.Invoke($"PENDING [{string.Join(",", activeFailures)}]");
            var delay = AutomaticRetryDelay();
            Log?.Invoke($"RETRY in={(int)delay.TotalSeconds}s");
            SetRestoreState(StartupRestoreState.PendingRetry);
            SetConvergenceState(new RuntimeConvergenceState(physicalUsable, DesiredSatisfied: false, activeFailures, string.Join("; ", activeFailures.Select(c => $"{c}: {componentRetries[c].LastErrorClass}")), Phase: ConvergencePhase.Degraded));
            return delay;
        }

        if (desired.OpenVpnEnabled && OpenVpnRetryController.State is OpenVpnRetryState.RetryScheduled or OpenVpnRetryState.WaitingForRelevantChange or OpenVpnRetryState.SuspendedFatal or OpenVpnRetryState.SuspendedAuth)
        {
            SetConvergenceState(new(physicalUsable, false, [ComponentId.OpenVpnLink], OpenVpnRetryController.LastError, Phase: ConvergencePhase.Degraded));
            var remaining = OpenVpnRetryController.NextAttemptAt - Clock.UtcNow;
            return remaining is { } next && next > TimeSpan.Zero ? next : TimeSpan.Zero;
        }
        NetworkSnapshot? latestPhysical;
        try { latestPhysical = PhysicalNetworkProvider.ResolveCurrentBinding(GetSettings().PhysicalInterface); }
        catch (InvalidOperationException) { latestPhysical = null; }
        if (PhysicalNetworkProvider.HasChanged(physical, latestPhysical, false) &&
            (physical != null || latestPhysical != null))
        {
            RequestReconcile(ReconcileReason.PhysicalNetworkChanged);
            SetConvergenceState(new(PhysicalNetworkProvider.IsUsable(latestPhysical), false, [], null,
                latestPhysical == null ? ConvergencePhase.WaitingForPhysicalNetwork : ConvergencePhase.Reconciling));
            return TimeSpan.Zero;
        }
        if (DesiredStateProvider.GetCurrentDesiredState() != desired)
        {
            RequestReconcile(ReconcileReason.UserChangedSettings);
            SetConvergenceState(new(physicalUsable, false, [], null, ConvergencePhase.Reconciling));
            return TimeSpan.Zero;
        }
        // Finalize last applied config fingerprint
        if (desired.OpenVpnEnabled && OpenVpnRuntime.Reconnecting)
        {
            SetConvergenceState(new(physicalUsable, false, [ComponentId.OpenVpnLink], "OpenVPN переподключается; корпоративные маршруты закрыты", Phase: OpenVpnRuntime.RuntimePhase == OpenVpnRuntimePhase.LongReconnect ? ConvergencePhase.Degraded : ConvergencePhase.Reconciling));
            return TimeSpan.Zero;
        }
        lastAppliedConfig = EffectiveRuntimeConfigBuilder.Build(
            settings, desired, physical, CurrentOpenVpnRoutes(desired));

        SetRestoreState(StartupRestoreState.Completed);
        SetConvergenceState(new RuntimeConvergenceState(physicalUsable, DesiredSatisfied: true, [], null, Phase: ConvergencePhase.Converged));
        Log?.Invoke("SATISFACTION pending=[]");
        return TimeSpan.Zero;
    }

    private IReadOnlyList<string>? CurrentOpenVpnRoutes(DesiredRuntimeState desired)
    {
        var link = OpenVpnRuntime.Link;
        return link != null && (!link.ProfileId.HasValue || link.ProfileId == (desired.SelectedOpenVpnProfileId ?? GetSettings().OpenVpnProfileId)) ? link.LearnedRoutes : null;
    }

    private void RecordFailure(ComponentId component, string errorClass, long gen)
    {
        lock (syncRoot)
        {
            if (!componentRetries.TryGetValue(component, out var state))
            {
                state = new ComponentRetryState();
                componentRetries[component] = state;
            }
            else if(component==ComponentId.OpenVpnRoutes && (state.Generation==gen || state.NextRetryAt>Clock.UtcNow))return;
            state.Failures++;
            state.LastErrorClass = errorClass;
            state.Generation = gen;
            if (component == ComponentId.OpenVpnRoutes) state.RouteOwnerId = OpenVpnRuntime.Link?.RouteOwnerId;
            var delay = RuntimePlanner.GetBackoff(state.Failures, BackoffIntervals);
            state.NextRetryAt = Clock.UtcNow + delay;
            if(component==ComponentId.OpenVpnRoutes)
                Log?.Invoke($"OPENVPN_ROUTE_RETRY reconcileRevision={gen} retryOwner={RuntimeIdentityDiagnostic.Alias(state.RouteOwnerId)} retryInMs={delay.TotalMilliseconds:0} {RuntimeIdentityDiagnostic.Owner(OpenVpnRuntime.Link)}");
            if(component is ComponentId.MainRouter or ComponentId.Tun)
                failedMainConfig = EffectiveRuntimeConfigBuilder.Build(GetSettings(), DesiredStateProvider.GetCurrentDesiredState(), capturedPhysical,
                    CurrentOpenVpnRoutes(DesiredStateProvider.GetCurrentDesiredState()));
        }
    }

    private DateTimeOffset? NextAutomaticRetryAt()
    {
        lock (syncRoot)
        {
            var openVpnEnabled = DesiredStateProvider.GetCurrentDesiredState().OpenVpnEnabled;
            // Link failure records remain for UI diagnostics and OFF cleanup.
            // While ON, the typed controller alone owns retry/exhaustion. Its
            // suspended state has no deadline; an old generic deadline must
            // neither spin the loop nor shorten a different component's wait.
            var deadlines = componentRetries
                .Where(entry => entry.Value.Failures > 0 && !(openVpnEnabled && entry.Key == ComponentId.OpenVpnLink))
                .Select(entry => (DateTimeOffset?)entry.Value.NextRetryAt);
            if (openVpnEnabled && OpenVpnRetryController.State == OpenVpnRetryState.RetryScheduled)
                deadlines = deadlines.Append(OpenVpnRetryController.NextAttemptAt);
            return deadlines.Where(deadline => deadline.HasValue).Min();
        }
    }

    private TimeSpan AutomaticRetryDelay()
    {
        var remaining = NextAutomaticRetryAt() - Clock.UtcNow;
        return remaining is {} delay && delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    private async Task WaitForSignalOrDelayAsync(TimeSpan delay, CancellationToken token)
    {
        TaskCompletionSource<bool> tcs;
        lock (syncRoot)
        {
            tcs = wakeSignal;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var delayTask = Clock.Delay(delay, cts.Token);
        var finished = await Task.WhenAny(delayTask, tcs.Task).ConfigureAwait(false);

        cts.Cancel();

        lock (syncRoot)
        {
            if (wakeSignal.Task.IsCompleted)
            {
                wakeSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private void Publish<T>(Action<T>? handlers, T value)
    {
        foreach (var handler in handlers?.GetInvocationList() ?? [])
            try { ((Action<T>)handler)(value); }
            catch (Exception ex) { Log?.Invoke("RUNTIME_PROJECTION error=" + ex.Message); }
    }

    public async Task<T> RunZapretTestAsync<T>(Func<CancellationToken, Task<T>> test, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetimeCts.Token);
        await zapretTestGate.WaitAsync(linked.Token).ConfigureAwait(false);
        Interlocked.Exchange(ref zapretTesting, 1);
        try { return await test(linked.Token).ConfigureAwait(false); }
        finally
        {
            Interlocked.Exchange(ref zapretTesting, 0);
            zapretTestGate.Release();
            RequestReconcile(ReconcileReason.ZapretStateChanged);
        }
    }

    public async Task StopForShutdownAsync()
    {
        Cancel();
        await reconcileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            SetRouterLifecycle(MainRouterLifecycle.Stopping, "shutdown");
            try { await ZapretRuntime.EnsureStoppedAsync(CancellationToken.None).ConfigureAwait(false); }
            finally
            {
                try { await OpenVpnRuntime.EnsureStoppedAsync(CancellationToken.None).ConfigureAwait(false); }
                finally { await RouterRuntime.EnsureStoppedAsync(CancellationToken.None).ConfigureAwait(false); }
            }
        }
        finally { reconcileGate.Release(); }
    }

    public void Dispose() => Cancel();

    public async Task RunModuleUpdateAsync(string key,Func<Func<CancellationToken,Task>,Func<Task>,CancellationToken,Task> install,CancellationToken ct)
    {
        if(key is not ("sing-box" or "xray" or "zapret" or "geoip" or "geosite" or "tg-ws-proxy"))throw new ArgumentException("Unsupported component maintenance",nameof(key));
        bool routerAffected=key is "sing-box" or "xray" or "geoip" or "geosite";
        bool zapretAffected=key is "zapret";
        using var zapretLock = zapretAffected ? await AcquireZapretGateAsync(ct).ConfigureAwait(false) : null;
        await reconcileGate.WaitAsync(ct).ConfigureAwait(false);
        bool restored=false;
        async Task Stop(CancellationToken token)
        {
            if(key=="zapret")await ZapretRuntime.EnsureStoppedAsync(token).ConfigureAwait(false);
            if(routerAffected){SetRouterLifecycle(MainRouterLifecycle.Stopping,"module-update");await RouterRuntime.SuspendModuleAsync(key,token).ConfigureAwait(false);}
        }
        async Task Restore(CancellationToken token)
        {
            // Do not restore a stale snapshot over a newer user OFF/profile choice.
            var desired=DesiredStateProvider.GetCurrentDesiredState();var settings=GetSettings();
            var physical=PhysicalNetworkProvider.ResolveCurrentBinding(settings.PhysicalInterface);
            var target=EffectiveRuntimeConfigBuilder.Build(settings,desired,physical,CurrentOpenVpnRoutes(desired)).TargetSettings??settings;
            if(key=="zapret"&&desired.ZapretEnabled)
            {
                await ZapretRuntime.EnsureRunningAsync(target,null,token).ConfigureAwait(false);
                if(!ZapretRuntime.IsRunning||ZapretRuntime.ObservedState is {IsReady:false})throw new IOException("Zapret update startup failed.");
            }
            if(routerAffected&&desired.MainVpnEnabled)
            {
                if(physical==null||!PhysicalNetworkProvider.IsUsable(physical))throw new IOException("Physical network unavailable during update validation.");
                SetRouterLifecycle(MainRouterLifecycle.Starting,"module-update");
                await RouterRuntime.EnsureRunningAsync(target,physical,"module-update",token).ConfigureAwait(false);
                if(!RouterRuntime.VpnRunning||!RouterRuntime.DependenciesHealthy)throw new IOException("Router update startup failed.");
                var health=await TunnelInspector.WaitForTunReadyAsync(()=>RouterRuntime.IsRunning,()=>RouterRuntime.ListenPort,desired.TunEnabled,MainRouterLifecycle.Starting,TunReadyTimeout,token).ConfigureAwait(false);
                if(health.StructuralFailure)throw new IOException("Router update datapath validation failed.");
                SetRouterLifecycle(MainRouterLifecycle.Running,"module-update");
            }
            if(key=="sing-box")await RouterRuntime.ApplyOpenVpnOverlayAsync(target,desired.OpenVpnEnabled?OpenVpnRuntime.Link:null,token).ConfigureAwait(false);
            Log?.Invoke($"UPDATE_RESTART module={key} result=ready");
        }
        try
        {
            await Stop(ct).ConfigureAwait(false);
            await install(async token=>
            {
                try{await Restore(token).ConfigureAwait(false);restored=true;}
                catch{await Stop(CancellationToken.None).ConfigureAwait(false);throw;}
            },async()=>{restored=false;await Stop(CancellationToken.None).ConfigureAwait(false);},ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if(!restored)
                {
                    using var recovery=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try{await Restore(recovery.Token).ConfigureAwait(false);}
                    catch(Exception error){Log?.Invoke($"UPDATE_RESTART module={key} result=deferred errorClass={error.GetType().Name}");}
                }
            }
            finally
            {
                reconcileGate.Release();
                RequestReconcile(ReconcileReason.UserChangedSettings);
            }
        }
    }
    private async Task<IDisposable> AcquireZapretGateAsync(CancellationToken ct) { await zapretTestGate.WaitAsync(ct).ConfigureAwait(false); return new ActionDisposable(() => zapretTestGate.Release()); }
    private sealed class ActionDisposable(Action action) : IDisposable { public void Dispose() => action(); }
}
