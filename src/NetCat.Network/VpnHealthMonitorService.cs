using NetCat.Core;

namespace NetCat.Network;

public interface IVpnHealthMonitorService : IDisposable
{
    bool IsRunning { get; }
    DelayResult? LastHealthResult { get; }
    DelayResult? LastSystemHealthResult { get; }
    event Action<DelayResult?, DelayResult?>? HealthUpdated;
    void Start();
    void Stop();
    Task ProbeOnceAsync(CancellationToken ct = default);
}

public class VpnHealthMonitorService : IVpnHealthMonitorService
{
    private readonly IRuntimeConfigurationRepository configRepo;
    private readonly IRouterRuntime router;
    private readonly RuntimeCoordinator coordinator;
    private readonly Func<DesiredRuntimeState> getDesiredState;
    private readonly Action<Func<DesiredRuntimeState, DesiredRuntimeState>> updateDesiredState;
    private readonly FailoverPolicy failoverPolicy = new();
    private readonly object lifecycleGate = new();
    private CancellationTokenSource? cts;
    private Task? loopTask;
    private volatile bool isRunning;
    private bool disposed;
    private int isProbing;
    private long lastObservedRevision = -1;

    public bool IsRunning => isRunning;
    public DelayResult? LastHealthResult { get; private set; }
    public DelayResult? LastSystemHealthResult { get; private set; }
    public event Action<DelayResult?, DelayResult?>? HealthUpdated;

    public Func<int, string, CancellationToken, int, Task<DelayResult>> MeasureLatencyFunc { get; set; } =
        (port, url, ct, timeout) => ConnectionLatency.MeasureAsync(port, url, ct, timeout);

    public Func<Profile, AppSettings, CancellationToken, Task<DelayResult>>? TestProfileFunc { get; set; }
    public Func<int, CancellationToken, Task<DelayResult>>? CheckTunnelHealthFunc { get; set; }
    public Action<string>? Log { get; set; }
    public IClock Clock { get; init; } = SystemClock.Instance;

    public VpnHealthMonitorService(
        IRuntimeConfigurationRepository configRepo,
        IRouterRuntime router,
        RuntimeCoordinator coordinator,
        Func<DesiredRuntimeState> getDesiredState,
        Action<Func<DesiredRuntimeState, DesiredRuntimeState>> updateDesiredState)
    {
        this.configRepo = configRepo;
        this.router = router;
        this.coordinator = coordinator;
        this.getDesiredState = getDesiredState;
        this.updateDesiredState = updateDesiredState;
    }

    public void Start()
    {
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (isRunning) return;
            var previous = loopTask;
            var source = new CancellationTokenSource();
            cts = source;
            isRunning = true;
            loopTask = Task.Run(async () =>
            {
                try
                {
                    // Stop cancels promptly. A restart must still drain a probe
                    // whose implementation has not yet observed cancellation.
                    if (previous != null) await previous.ConfigureAwait(false);
                    await MonitorLoopAsync(source.Token).ConfigureAwait(false);
                }
                finally
                {
                    lock (lifecycleGate)
                    {
                        if (ReferenceEquals(cts, source)) { cts = null; isRunning = false; }
                        source.Dispose();
                    }
                }
            });
        }
    }

    public void Stop()
    {
        lock (lifecycleGate)
        {
            isRunning = false;
            cts?.Cancel();
        }
    }

    private async Task MonitorLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && isRunning)
        {
            var settings = configRepo.CurrentSettings;
            var intervalSeconds = Math.Max(1, settings.TestIntervalSeconds);

            try
            {
                // Await the complete probe before scheduling the next interval.
                // Chunk very large stored intervals to stay within Task.Delay's
                // timer limit without silently shortening the selected interval.
                while (intervalSeconds > 0)
                {
                    var seconds = Math.Min(intervalSeconds, 86400);
                    await Clock.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
                    intervalSeconds -= seconds;
                }
                if (token.IsCancellationRequested || !isRunning) break;

                await ProbeOnceAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"VPN_HEALTH probe_error={ex.Message}");
            }
        }
    }

    public async Task ProbeOnceAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref isProbing, 1) != 0) return;
        try
        {
            if (!router.VpnRunning || router.LatencyPort == 0)
            {
                if (!router.DependenciesHealthy && getDesiredState().MainVpnEnabled)
                    coordinator.RequestReconcile(ReconcileReason.VpnUpstreamChanged);
                coordinator.VpnUpstreamHealthy = null;
                LastHealthResult = null;
                LastSystemHealthResult = null;
                PublishHealth(null, null);
                return;
            }

            var revision = router.SessionRevision;
            if (lastObservedRevision != revision)
            {
                failoverPolicy.ObserveSession(revision);
                lastObservedRevision = revision;
            }

            var activeId = router.ActiveProfileId;
            var settings = configRepo.CurrentSettings;
            int timeoutSec = settings.AutoSwitch ? 4 : settings.TestTimeoutSeconds;

            var result = await MeasureLatencyFunc(router.LatencyPort, settings.TestUrl, ct, timeoutSec).ConfigureAwait(false);
            if (router.SessionRevision != revision) return;

            coordinator.VpnUpstreamHealthy = result.Success;
            LastHealthResult = result;

            DelayResult? systemResult = null;
            if (router.TunActive && (result.Success || !settings.AutoSwitch) && CheckTunnelHealthFunc != null && router.HealthSourcePort > 0)
            {
                systemResult = await CheckTunnelHealthFunc(router.HealthSourcePort, ct).ConfigureAwait(false);
                LastSystemHealthResult = systemResult;
            }

            if (router.SessionRevision != revision || !getDesiredState().MainVpnEnabled) return;
            PublishHealth(result, systemResult);

            if (activeId.HasValue)
            {
                failoverPolicy.Record(activeId.Value, result, DateTimeOffset.UtcNow, activeConnection: true);

                var physical = coordinator.PhysicalNetworkProvider.ResolveCurrentBinding(settings.PhysicalInterface);
                bool physicalReady = coordinator.PhysicalNetworkProvider.IsUsable(physical) &&
                    coordinator.CurrentConvergenceState.Phase != ConvergencePhase.WaitingForPhysicalNetwork;

                if (!result.Success && settings.AutoSwitch &&
                    failoverPolicy.ShouldRecover(activeId.Value, settings.FailureThreshold, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), physicalNetworkAvailable: physicalReady))
                {
                    await TriggerFailoverAsync(activeId.Value, revision, settings, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Volatile.Write(ref isProbing, 0);
        }
    }

    private async Task TriggerFailoverAsync(Guid failedProfileId, long revision, AppSettings settings, CancellationToken ct)
    {
        Log?.Invoke($"VPN_FAILOVER triggered for profile={failedProfileId}");

        var candidates = settings.Profiles.Where(p => p.Id != failedProfileId && !p.IsOpenVpn && p.Candidate && !p.SubscriptionRemoved).ToList();
        if (candidates.Count == 0)
        {
            Log?.Invoke("VPN_FAILOVER no candidate profiles available");
            return;
        }

        Profile? winner = null;
        if (TestProfileFunc != null)
        {
            foreach (var candidate in candidates)
            {
                // Confirm the same candidate twice within this recovery attempt.
                // A failure discards its first success; results from other
                // candidates or earlier attempts must not complete this pair.
                int successes = 0;
                for (int confirmation = 0; confirmation < 2; confirmation++)
                {
                    var desired = getDesiredState();
                    if (ct.IsCancellationRequested || router.SessionRevision != revision ||
                        !desired.MainVpnEnabled || desired.SelectedVpnProfileId != failedProfileId ||
                        !configRepo.CurrentSettings.AutoSwitch) return;
                    var res = await TestProfileFunc(candidate, settings, ct).ConfigureAwait(false);
                    Log?.Invoke($"VPN_FAILOVER candidate={candidate.Id} confirmation={confirmation + 1}/2 success={res.Success}");
                    if (!res.Success) break;
                    successes++;
                }
                if (successes == 2)
                {
                    winner = candidate;
                    break;
                }
            }
        }

        if (winner == null || ct.IsCancellationRequested || router.SessionRevision != revision) return;

        Log?.Invoke($"VPN_FAILOVER selected winner={winner.Name} id={winner.Id}");

        // Update persistent settings
        await configRepo.UpdateSettingsAsync(s =>
        {
            var desired = getDesiredState();
            if (!s.AutoSwitch || !desired.MainVpnEnabled || desired.SelectedVpnProfileId != failedProfileId || router.SessionRevision != revision)
                throw new OperationCanceledException("Failover superseded by user intent.");
            if (!s.Profiles.Any(p => p.Id == winner.Id && p.Candidate && !p.IsOpenVpn && !p.SubscriptionRemoved))
                throw new OperationCanceledException("Failover candidate is no longer available.");
            s.MainProfileId = winner.Id;
            return s;
        }, ct).ConfigureAwait(false);

        updateDesiredState(d => d.SelectedVpnProfileId == failedProfileId && d.MainVpnEnabled
            ? d with { SelectedVpnProfileId = winner.Id } : d);
        failoverPolicy.Switched(DateTimeOffset.UtcNow);
        // Coordinator is the single mutation authority — request reconcile
        coordinator.RequestReconcile(ReconcileReason.VpnFailover);
    }

    private void PublishHealth(DelayResult? profile, DelayResult? system)
    {
        foreach (var handler in HealthUpdated?.GetInvocationList() ?? [])
            try { ((Action<DelayResult?, DelayResult?>)handler)(profile, system); }
            catch (Exception ex) { Log?.Invoke("VPN_HEALTH projection_error=" + ex.Message); }
    }

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            if (disposed) return;
            disposed = true;
            Stop();
        }
    }
}
