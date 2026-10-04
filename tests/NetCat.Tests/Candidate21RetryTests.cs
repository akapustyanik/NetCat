using NetCat.Core;
using NetCat.Network;
using Xunit;
namespace NetCat.Tests;

public sealed class Candidate21RetryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NetCat-C21-retry-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private Candidate12Tests.Fixture Create() => new(new SettingsStore(root));
    [Fact] public async Task TunStructuralFailureDoesNotBypassMainRouterBackoff()
    {
        using var f = Create(); int attempts = 0;
        f.Router.BeforeStart = () => { attempts++; throw new IOException("start failed"); };
        await f.Reconcile(ReconcileReason.Startup);
        for (int i = 0; i < 5; i++) await f.Reconcile(ReconcileReason.TunStructuralFailure);
        Assert.Equal(1, attempts); Assert.Equal(1, f.Coordinator.GetFailureCount(ComponentId.MainRouter));
        f.Router.BeforeStart = null; f.Clock.Advance(TimeSpan.FromHours(2));
        await f.Reconcile(ReconcileReason.ExternalConditionResolved);
        Assert.True(f.Router.IsRunning); Assert.True(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
    }
    [Fact] public async Task TunMonitorDoesNotRestartStormDuringKnownStartFailure()
    {
        using var f = Create(); var events = new System.Collections.Concurrent.ConcurrentQueue<string>(); f.Coordinator.Log = events.Enqueue;
        f.Router.BeforeStart = () => throw new IOException("start failed"); await f.Reconcile();
        using var monitor = new PhysicalNetworkMonitor(f.Coordinator, f.Repo);
        for (int i = 0; i < 8; i++) await monitor.ObserveTunnelOnceAsync();
        Assert.DoesNotContain(events, s => s == "TUN_EVENT emitted=TunStructuralFailure");
    }
    [Fact] public async Task UnexpectedTunLossWhileRunningStillTriggersRecovery()
    {
        using var f = Create(); await f.Reconcile();
        f.Tunnel.Status = TunStructuralStatus.StructuralFailure;
        await f.Reconcile(ReconcileReason.TunStructuralFailure);
        Assert.Equal(2, f.Router.Starts);
    }
    [Fact] public async Task PendingMainRetryDoesNotSuppressIndependentOff()
    {
        using var f = Create(); int attempts = 0;
        f.Router.BeforeStart = () => { attempts++; throw new IOException("start failed"); }; await f.Reconcile();
        f.Desired.Current = f.Desired.Current with { ZapretEnabled = false };
        await f.Reconcile(ReconcileReason.TunStructuralFailure);
        Assert.False(f.Zapret.IsRunning); Assert.Equal(1, attempts);
    }
}
