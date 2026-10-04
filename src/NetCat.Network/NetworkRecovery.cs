using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using NetCat.Core;

namespace NetCat.Network;

public sealed record RouterHealth
{
    public bool CoreProcessHealthy { get; init; }
    public bool TunInterfacePresent { get; init; }
    public bool TunRoutesPresent { get; init; }
    public bool LocalDataPathHealthy { get; init; }
    public int TunIndex { get; init; }
    public bool? TunDataPathHealthy { get; init; }
    public bool? VpnUpstreamHealthy { get; init; }
    public bool? DirectPathHealthy { get; init; }
    public bool? DnsPathHealthy { get; init; }
    public TunStructuralStatus StructuralStatus { get; init; }
    public TunHealthState? HealthState { get; init; }

    public bool StructuralFailure => StructuralStatus == TunStructuralStatus.StructuralFailure;

    public RouterHealth(
        bool CoreProcessHealthy,
        bool TunInterfacePresent,
        bool TunRoutesPresent,
        bool LocalDataPathHealthy,
        int TunIndex,
        bool? TunDataPathHealthy = null,
        bool? VpnUpstreamHealthy = null,
        bool? DirectPathHealthy = null,
        bool? DnsPathHealthy = null,
        TunStructuralStatus? StructuralStatus = null,
        TunHealthState? HealthState = null)
    {
        this.CoreProcessHealthy = CoreProcessHealthy;
        this.TunInterfacePresent = TunInterfacePresent;
        this.TunRoutesPresent = TunRoutesPresent;
        this.LocalDataPathHealthy = LocalDataPathHealthy;
        this.TunIndex = TunIndex;
        this.TunDataPathHealthy = TunDataPathHealthy;
        this.VpnUpstreamHealthy = VpnUpstreamHealthy;
        this.DirectPathHealthy = DirectPathHealthy;
        this.DnsPathHealthy = DnsPathHealthy;
        this.HealthState = HealthState;

        this.StructuralStatus = StructuralStatus ?? (
            (!CoreProcessHealthy || !TunInterfacePresent || !TunRoutesPresent)
                ? TunStructuralStatus.StructuralFailure
                : (!LocalDataPathHealthy
                    ? TunStructuralStatus.TransientDegraded
                    : TunStructuralStatus.Healthy));
    }
}

public sealed class TunHealthTracker
{
    public const int WeakFailureConfirmationCount = TunHealthPolicy.WeakDiagnosticConfirmationCount;
    public const int StructuralFailureConfirmationCount = 2;

    private readonly object syncRoot = new();
    private TunStructuralStatus lastStatus = TunStructuralStatus.Healthy;
    private DateTimeOffset lastStatusChanged = DateTimeOffset.UtcNow;
    private int consecutiveWeakFailures = 0;
    private int consecutiveMissingFailures = 0;

    public Action<string>? Log { get; set; }
    public TunStructuralStatus CurrentStatus { get { lock (syncRoot) return lastStatus; } }

    public TunHealthState Evaluate(
        bool running,
        bool interfacePresent,
        bool routesPresent,
        bool localDataPath,
        int tunIndex,
        MainRouterLifecycle lifecycle,
        long generation = 0,
        bool routeObservationKnown = true)
    {
        lock (syncRoot)
        {
            var now = DateTimeOffset.UtcNow;
            TunStructuralStatus newStatus;
            string? evidence = null;

            // The coordinator owns lifecycle state.  A missing process or
            // topology while it is starting/stopping is expected and must not
            // be interpreted as a failed router.
            var expected = TunHealthClassifier.ExpectedStatus(lifecycle, running, interfacePresent, routesPresent, routeObservationKnown);
            if (expected.HasValue)
            {
                newStatus = expected.Value;
                evidence = $"router lifecycle is {lifecycle}";
                consecutiveWeakFailures = 0;
                consecutiveMissingFailures = 0;
            }
            else if (!running)
            {
                // Once Running has been reached, process exit is immediate,
                // authoritative structural evidence.
                newStatus = TunStructuralStatus.StructuralFailure;
                evidence = "sing-box process exited";
                consecutiveWeakFailures = 0;
                consecutiveMissingFailures = 0;
            }
            else if (!routeObservationKnown)
            {
                newStatus = TunStructuralStatus.Unknown;
                evidence = "Route observation unavailable";
                consecutiveMissingFailures = consecutiveWeakFailures = 0;
            }
            else if (!interfacePresent && !routesPresent)
            {
                consecutiveMissingFailures++;
                if (consecutiveMissingFailures >= StructuralFailureConfirmationCount)
                {
                    newStatus = TunStructuralStatus.StructuralFailure;
                    evidence = "Wintun interface and routes missing";
                }
                else
                {
                    newStatus = TunStructuralStatus.TransientDegraded;
                    evidence = "Unconfirmed interface and routes disappearance";
                }
            }
            else if (!interfacePresent || !routesPresent)
            {
                consecutiveMissingFailures = 0;
                newStatus = TunStructuralStatus.TransientDegraded;
                evidence = "Incomplete TUN topology observation; awaiting confirmation";
            }
            else if (!localDataPath)
            {
                consecutiveMissingFailures = 0;
                consecutiveWeakFailures++;
                newStatus = TunStructuralStatus.TransientDegraded;
                evidence = $"Local SOCKS datapath probe failed; structural evidence absent (samples={consecutiveWeakFailures})";
            }
            else
            {
                consecutiveWeakFailures = 0;
                consecutiveMissingFailures = 0;
                newStatus = TunStructuralStatus.Healthy;
            }

            LogTransition(now, newStatus, running, interfacePresent, routesPresent, localDataPath, evidence, generation);
            return new TunHealthState(running, interfacePresent, routesPresent, localDataPath, newStatus, evidence, now);
        }
    }

    public TunHealthState Evaluate(
        bool running,
        bool interfacePresent,
        bool routesPresent,
        bool localDataPath,
        int tunIndex,
        long generation = 0,
        bool routeObservationKnown = true)
        => Evaluate(running, interfacePresent, routesPresent, localDataPath, tunIndex,
            MainRouterLifecycle.Running, generation, routeObservationKnown);

    private void LogTransition(DateTimeOffset now, TunStructuralStatus newStatus, bool running,
        bool interfacePresent, bool routesPresent, bool localDataPath, string? evidence, long generation)
    {
        if (newStatus == lastStatus) return;
        var durationMs = (long)(now - lastStatusChanged).TotalMilliseconds;
        if (lastStatus == TunStructuralStatus.TransientDegraded && newStatus == TunStructuralStatus.Healthy)
            Log?.Invoke($"TUN_HEALTH old=TransientDegraded new=Healthy durationMs={durationMs} recoveryAction=none");
        else if (lastStatus == TunStructuralStatus.TransientDegraded && newStatus == TunStructuralStatus.StructuralFailure)
            Log?.Invoke($"TUN_HEALTH old=TransientDegraded new=StructuralFailure durationMs={durationMs} evidence={evidence} reconcileRequested=true");
        else
            Log?.Invoke($"TUN_HEALTH old={lastStatus} new={newStatus} processAlive={running} interfacePresent={interfacePresent} routesPresent={routesPresent} localDataPath={localDataPath} evidence={evidence ?? "none"} generation={generation}");
        lastStatus = newStatus;
        lastStatusChanged = now;
    }

    public void Reset()
    {
        lock (syncRoot)
        {
            lastStatus = TunStructuralStatus.Healthy;
            lastStatusChanged = DateTimeOffset.UtcNow;
            consecutiveWeakFailures = 0;
            consecutiveMissingFailures = 0;
        }
    }
}

// Upstream HTTP failures are deliberately absent from the recovery decision.
public sealed class RecoveryPolicy
{
    private int failures, attempts;
    private DateTimeOffset healthySince;
    public DateTimeOffset NextAttempt { get; private set; }
    public int Attempts => attempts;
    public bool Observe(bool structuralFailure, DateTimeOffset now)
    {
        if (!structuralFailure)
        {
            failures = 0;
            if (healthySince == default) healthySince = now;
            if (now - healthySince >= TimeSpan.FromMinutes(2)) { attempts = 0; NextAttempt = default; }
            return false;
        }
        healthySince = default;
        if (++failures < 3 || now < NextAttempt) return false;
        failures = 0; attempts++;
        NextAttempt = now.AddSeconds(attempts switch { 1 => 5, 2 => 15, 3 => 30, 4 => 60, _ => 300 });
        return true;
    }
    public void Reset() { failures = attempts = 0; NextAttempt = healthySince = default; }
}

public static class TunHealthPolicy
{
    // Named policy used by both the authoritative tracker and the UI
    // projection; this avoids scattered magic thresholds.
    public const int WeakDiagnosticConfirmationCount = 3;
}

public static class TunHealthClassifier
{
    public static TunStructuralStatus? ExpectedStatus(MainRouterLifecycle lifecycle, bool running,
        bool interfacePresent, bool routesPresent, bool routeObservationKnown = true) => lifecycle switch
    {
        MainRouterLifecycle.StoppedByDesired or MainRouterLifecycle.Stopping => TunStructuralStatus.Unknown,
        MainRouterLifecycle.Starting when !running || !interfacePresent || !routesPresent || !routeObservationKnown => TunStructuralStatus.Starting,
        _ => null
    };
    // Compatibility boundary for inspectors that only provide raw topology.
    // The lifecycle-aware tracker is the normal production path; this keeps
    // every consumer from independently treating a startup miss as failure.
    public static RouterHealth ApplyLifecycle(RouterHealth raw, MainRouterLifecycle lifecycle)
    {
        var expected = ExpectedStatus(lifecycle, raw.CoreProcessHealthy, raw.TunInterfacePresent, raw.TunRoutesPresent);
        if (!expected.HasValue && !raw.CoreProcessHealthy)
            expected = TunStructuralStatus.StructuralFailure;
        if (expected.HasValue)
            return raw with
            {
                StructuralStatus = expected.Value,
                HealthState = raw.HealthState is { } state
                    ? state with { StructuralStatus = expected.Value, FailureEvidence = $"router lifecycle is {lifecycle}" }
                    : null
            };

        return raw;
    }
}

public sealed class NetworkSignals : IDisposable
{
    private long revision, dueTicks;
    public long Revision => Interlocked.Read(ref revision);
    public NetworkSignals(bool subscribe = true)
    {
        if (subscribe) { NetworkChange.NetworkAddressChanged += Changed; NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged; }
    }
    public void Signal()
    {
        // First event opens a bounded coalescing window; continuous errors must
        // not postpone recovery forever by continually moving its deadline.
        var now = DateTimeOffset.UtcNow;
        var previous = Interlocked.Read(ref dueTicks);
        if (previous <= now.UtcTicks) Interlocked.CompareExchange(ref dueTicks, now.AddSeconds(2).UtcTicks, previous);
        Interlocked.Increment(ref revision);
    }
    public bool Ready(long handled, DateTimeOffset now) => Revision != handled && now.UtcTicks >= Interlocked.Read(ref dueTicks);
    private void Changed(object? sender, EventArgs args) => Signal();
    private void AvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) => Signal();
    public void Dispose() { NetworkChange.NetworkAddressChanged -= Changed; NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged; }
}

public static class TunnelInspection
{
    [DllImport("iphlpapi.dll")] private static extern uint GetBestInterface(uint destination, out uint index);
    public static NetworkInterface? Adapter() => NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == "NetCat-TUN" && n.OperationalStatus == OperationalStatus.Up);
    public static TunHealthTracker Tracker { get; } = new();

    public static Task<RouterHealth> ReadAsync(bool running, int port, bool tun, CancellationToken ct) =>
        ReadAsync(running, port, tun, MainRouterLifecycle.Running, ct);

    public static async Task<RouterHealth> ReadAsync(bool running, int port, bool tun, MainRouterLifecycle lifecycle, CancellationToken ct)
    {
        NetworkInterface? nic;
        int index;
        try { nic = tun ? Adapter() : null; index = nic?.GetIPProperties().GetIPv4Properties()?.Index ?? 0; }
        catch (NetworkInformationException)
        {
            var unknown = Tracker.Evaluate(running, false, false, false, 0, lifecycle, routeObservationKnown: false);
            return new(running, false, false, false, 0, StructuralStatus: unknown.StructuralStatus, HealthState: unknown);
        }
        bool routes = !tun, known = true;
        if (tun)
        {
            // No adapter is a positive absence observation; API errors remain unknown.
            if (index > 0)
            {
                var firstCode = GetBestInterface(0x01010101, out var first);
                var secondCode = GetBestInterface(0x08080808, out var second);
                known = firstCode == 0 && secondCode == 0;
                routes = known && first == index && second == index;
            }
        }
        var local = running && port > 0 && await RouterService.SocksAvailableAsync("127.0.0.1", port, ct);
        bool ifacePresent = !tun || index > 0;
        var healthState = Tracker.Evaluate(running, ifacePresent, routes, local, index, lifecycle, routeObservationKnown: known);
        return new(running, ifacePresent, routes, local, index, StructuralStatus: healthState.StructuralStatus, HealthState: healthState);
    }
    public static async Task WaitReleasedAsync(CancellationToken ct)
    {
        for (int i = 0; i < 40; i++) { if (Adapter() == null) return; await Task.Delay(150, ct); }
        throw new IOException("Прежний NetCat-TUN ещё не освобождён. Повторная попытка будет отложена.");
    }
    public static async Task<RouterHealth> WaitForTunReadyAsync(
        Func<bool> isRunning,
        Func<int> getPort,
        bool tun,
        TimeSpan timeout,
        CancellationToken ct,
        TimeSpan? pollInterval = null,
        Func<CancellationToken, Task<RouterHealth>>? inspectOverride = null)
        => await WaitForTunReadyAsync(isRunning, getPort, tun, MainRouterLifecycle.Starting, timeout, ct, pollInterval, inspectOverride).ConfigureAwait(false);

    public static async Task<RouterHealth> WaitForTunReadyAsync(
        Func<bool> isRunning,
        Func<int> getPort,
        bool tun,
        MainRouterLifecycle lifecycle,
        TimeSpan timeout,
        CancellationToken ct,
        TimeSpan? pollInterval = null,
        Func<CancellationToken, Task<RouterHealth>>? inspectOverride = null)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(300);
        var deadline = DateTimeOffset.UtcNow + timeout;
        RouterHealth lastHealth = new(isRunning(), false, false, false, 0);

        while (!ct.IsCancellationRequested)
        {
            lastHealth = inspectOverride != null
                ? TunHealthClassifier.ApplyLifecycle(await inspectOverride(ct), lifecycle)
                : await ReadAsync(isRunning(), getPort(), tun, lifecycle, ct);

            if (lastHealth.StructuralStatus == TunStructuralStatus.Healthy)
            {
                return lastHealth;
            }

            if (DateTimeOffset.UtcNow >= deadline)
                break;

            await Task.Delay(interval, ct);
        }
        ct.ThrowIfCancellationRequested();
        return lastHealth;
    }
}

public sealed class SystemTunnelHealthInspector : ITunnelHealthInspector
{
    public static readonly SystemTunnelHealthInspector Instance = new();
    public Task<RouterHealth> InspectAsync(bool running, int port, bool tun, CancellationToken ct) =>
        TunnelInspection.ReadAsync(running, port, tun, ct);
    public Task<RouterHealth> InspectAsync(bool running, int port, bool tun, MainRouterLifecycle lifecycle, CancellationToken ct) =>
        TunnelInspection.ReadAsync(running, port, tun, lifecycle, ct);
    public Task<RouterHealth> WaitForTunReadyAsync(Func<bool> isRunning, Func<int> getPort, bool tun, TimeSpan timeout, CancellationToken ct) =>
        TunnelInspection.WaitForTunReadyAsync(isRunning, getPort, tun, timeout, ct);
    public Task<RouterHealth> WaitForTunReadyAsync(Func<bool> isRunning, Func<int> getPort, bool tun, MainRouterLifecycle lifecycle, TimeSpan timeout, CancellationToken ct) =>
        TunnelInspection.WaitForTunReadyAsync(isRunning, getPort, tun, lifecycle, timeout, ct);
}
