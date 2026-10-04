using System.Net.NetworkInformation;
using NetCat.Core;

namespace NetCat.Network;

// Application lifetime observer. Events wake immediately; polling only catches missed OS events.
public sealed class PhysicalNetworkMonitor : IDisposable
{
    private readonly RuntimeCoordinator coordinator;
    private readonly IRuntimeConfigurationRepository repository;
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly CancellationTokenSource lifetime = new();
    private Task? loop;
    private bool subscribed;
    private long generation;
    private long lastStructuralRevision = -1;
    public Task InitialCapture { get; private set; } = Task.CompletedTask;
    public long Generation => Interlocked.Read(ref generation);
    public PhysicalNetworkMonitor(RuntimeCoordinator coordinator, IRuntimeConfigurationRepository repository)
    { this.coordinator = coordinator; this.repository = repository; }
    public void Signal()
    {
        try { signal.Release(); } catch (SemaphoreFullException) { }
    }
    private void AddressChanged(object? sender, EventArgs e) => Signal();
    private void AvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Signal();
    public void Start()
    {
        if (loop != null) return;
        NetworkChange.NetworkAddressChanged += AddressChanged;
        NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged;
        subscribed = true;
        InitialCapture = Task.Run(() => coordinator.CaptureInitialPhysicalNetworkAsync(lifetime.Token));
        loop = Task.Run(RunAsync);
    }
    public async Task ObserveAsync(CancellationToken ct = default)
    {
        var previous = coordinator.CurrentObservedState?.PhysicalNetwork ?? coordinator.CurrentPhysicalNetwork;
        NetworkSnapshot? current;
        try { current = coordinator.PhysicalNetworkProvider.ResolveCurrentBinding(repository.CurrentSettings.PhysicalInterface, previous); }
        catch (InvalidOperationException) { current = null; }
        // OS notifications do not identify their adapter. Compare the complete
        // physical binding, not the number of virtual-adapter notifications.
        if (coordinator.PhysicalNetworkState != PhysicalNetworkAvailability.Unknown && PhysicalNetwork.SameBinding(previous, current)) return;
        Interlocked.Increment(ref generation);
        coordinator.Log?.Invoke($"NETWORK_EVENT generation={Generation}");
        coordinator.NotifyPhysicalBindingChanged(current);
        await coordinator.ReconcileAsync(ReconcileReason.PhysicalNetworkChanged, ct).ConfigureAwait(false);
    }
    private async Task RunAsync()
    {
        var ct = lifetime.Token;
        var nextPhysicalCheck = DateTimeOffset.UtcNow.AddSeconds(60);
        try
        {
            try { await InitialCapture.ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { coordinator.Log?.Invoke($"PHYSICAL_NETWORK state=unknown capture_error={ex.Message}"); }
            while (!ct.IsCancellationRequested)
            {
                bool changed = await signal.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                try
                {
                    if (changed || DateTimeOffset.UtcNow >= nextPhysicalCheck)
                    {
                        nextPhysicalCheck = DateTimeOffset.UtcNow.AddSeconds(60);
                        await ObserveAsync(ct).ConfigureAwait(false);
                    }
                    await ObserveTunnelOnceAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { coordinator.Log?.Invoke($"NETWORK_OBSERVATION unknown error={ex.Message}"); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public async Task ObserveTunnelOnceAsync(CancellationToken ct = default)
    {
        var desired = coordinator.DesiredStateProvider.GetCurrentDesiredState();
        if (!desired.MainVpnEnabled || !desired.TunEnabled) { lastStructuralRevision = -1; return; }
        if (coordinator.MainRouterLifecycle is MainRouterLifecycle.Starting or MainRouterLifecycle.Stopping or MainRouterLifecycle.StoppedByDesired
            || coordinator.MainRouterRetryPending) { lastStructuralRevision = -1; return; }
        var health = await coordinator.ObserveTunnelAsync("network-monitor", ct).ConfigureAwait(false);
        if (health == null) return;
        if (!health.StructuralFailure) { lastStructuralRevision = -1; return; }
        var revision = coordinator.RouterRuntime.SessionRevision;
        if (lastStructuralRevision == revision) return;
        lastStructuralRevision = revision;
        coordinator.Log?.Invoke("TUN_SIGNAL source=network-monitor classification=structural evidence=fresh-observation");
        coordinator.Log?.Invoke("TUN_EVENT emitted=TunStructuralFailure");
        coordinator.RequestReconcile(ReconcileReason.TunStructuralFailure);
    }
    public void Dispose()
    {
        if (subscribed)
        {
            NetworkChange.NetworkAddressChanged -= AddressChanged;
            NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged;
            subscribed = false;
        }
        lifetime.Cancel();
    }
}
