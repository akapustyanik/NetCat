using NetCat.Core;
using NetCat.Engine;

namespace NetCat.Network;

public interface IDesiredRuntimeStateProvider
{
    DesiredRuntimeState GetCurrentDesiredState();
}

public interface IRouterRuntime
{
    bool DependenciesHealthy => true;
    event Action? DependencyLost { add { } remove { } }
    bool OpenVpnGatewayReady => true;
    void InvalidateOpenVpnOverlay() { }
    bool OpenVpnOverlayReady => true;
    event Action? OpenVpnOverlayLost { add { } remove { } }
    bool IsRunning { get; }
    bool VpnRunning { get; }
    bool TunActive { get; }
    int ListenPort { get; }
    int LatencyPort { get; }
    int HealthSourcePort { get; }
    NetworkSnapshot? ActivePhysical { get; }
    Guid? ActiveProfileId { get; }
    long SessionRevision { get; }
    Task EnsureRunningAsync(AppSettings settings, NetworkSnapshot physical, string reason, CancellationToken ct);
    Task EnsureStoppedAsync(CancellationToken ct);
    Task SuspendModuleAsync(string key,CancellationToken ct) => EnsureStoppedAsync(ct);
    Task RefreshPhysicalAsync(AppSettings settings, NetworkSnapshot physical, string reason, CancellationToken ct);
    Task ApplyOpenVpnOverlayAsync(AppSettings settings, OpenVpnLink? link, CancellationToken ct);
    Task ApplyOpenVpnOverlayAsync(AppSettings settings, OpenVpnLink? link, Func<bool> stillCurrent, CancellationToken ct)
        => stillCurrent() ? ApplyOpenVpnOverlayAsync(settings, link, ct) : Task.FromCanceled(new CancellationToken(true));
    void PrepareDomainOwnership(AppSettings settings) { }
    void PrepareDomainOwnership(AppSettings settings, bool openVpnRequested) => PrepareDomainOwnership(settings);
    void PrepareDomainOwnership(AppSettings settings, Func<bool> openVpnRequested) => PrepareDomainOwnership(settings, openVpnRequested());
}

public interface IZapretRuntime
{
    bool IsRunning { get; }
    string ActiveStrategy { get; }
    string ActiveScenario { get; }
    ZapretObservedState? ObservedState => null;
    string? ConfigurationFingerprint(AppSettings settings, NetworkSnapshot physical) => null;
    event Action<int, int>? ProcessExited { add { } remove { } }
    Task EnsureRunningAsync(AppSettings settings, string? strategyFile, CancellationToken ct);
    Task EnsureStoppedAsync(CancellationToken ct);
}

public enum OpenVpnRuntimePhase { NoProcess, Starting, Connected, Reconnecting, LongReconnect, Stopping, CleanupPending, FailedCleanup, FullyStopped }

public interface IOpenVpnRuntime
{
    bool DataPathRecoveryRequired => false;
    void PhysicalNetworkChanged() { }
    int NativeModuleRevision => 0;
    OpenVpnRuntimePhase RuntimePhase => IsRunning ? OpenVpnRuntimePhase.Connected : Reconnecting ? OpenVpnRuntimePhase.Reconnecting : OpenVpnRuntimePhase.FullyStopped;
    bool RequiresStop => RuntimePhase is not (OpenVpnRuntimePhase.NoProcess or OpenVpnRuntimePhase.FullyStopped);
    bool Reconnecting => false;
    void CancelPendingConnection() { }
    bool NeedsRestart(Profile profile) => false;
    event Action? LinkChanged { add { } remove { } }
    event Action<int, int>? ProcessExited { add { } remove { } }
    bool IsRunning { get; }
    OpenVpnLink? Link { get; }
    Guid? ActiveProfileId { get; }
    Task<OpenVpnLink> EnsureRunningAsync(Profile profile, string dnsOverride, CancellationToken ct);
    Task EnsureRoutesAsync(CancellationToken ct) => Task.CompletedTask;
    RouteObservationResult VerifyRoutes() => Link is {} link ? RouteTable.VerifyOpenVpn(link, RouteTable.CaptureIpv4()) : new(RouteObservationStatus.Missing, []);
    Task EnsureStoppedAsync(CancellationToken ct);
}

public interface IPhysicalNetworkProvider
{
    NetworkSnapshot? ResolveCurrentBinding(string preferred = "");
    NetworkSnapshot? ResolveCurrentBinding(string preferred, NetworkSnapshot? active) => ResolveCurrentBinding(preferred);
    bool IsUsable(NetworkSnapshot? snapshot);
    bool HasChanged(NetworkSnapshot? active, NetworkSnapshot? current, bool captureFailed);
}

public interface ITunnelHealthInspector
{
    Task<RouterHealth> InspectAsync(bool running, int port, bool tun, CancellationToken ct);
    Task<RouterHealth> InspectAsync(bool running, int port, bool tun, MainRouterLifecycle lifecycle, CancellationToken ct)
        => InspectAsync(running, port, tun, ct);
    Task<RouterHealth> WaitForTunReadyAsync(Func<bool> isRunning, Func<int> getPort, bool tun, TimeSpan timeout, CancellationToken ct);
    Task<RouterHealth> WaitForTunReadyAsync(Func<bool> isRunning, Func<int> getPort, bool tun, MainRouterLifecycle lifecycle, TimeSpan timeout, CancellationToken ct)
        => WaitForTunReadyAsync(isRunning, getPort, tun, timeout, ct);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
    Task Delay(TimeSpan duration, CancellationToken ct);
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task Delay(TimeSpan duration, CancellationToken ct) => Task.Delay(duration, ct);
}
