using NetCat.Core;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32RealAcceptanceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NetCat-RealAcceptance-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public async Task RecoveredXrayValidatesTunImmediatelyWithoutDiscardingFailureBudget()
    {
        using var f = new Candidate12Tests.Fixture(new SettingsStore(root));
        await f.Reconcile();
        f.Router.LoseXray();
        await f.Reconcile();
        Assert.Equal(1, f.Tunnel.Waits);
        f.Clock.Advance(TimeSpan.FromHours(1));
        await f.Reconcile();
        Assert.Equal(2, f.Router.Starts);
        Assert.Equal(2, f.Tunnel.Waits);
        Assert.Equal(MainRouterLifecycle.Running, f.Coordinator.MainRouterLifecycle);
        Assert.Equal(1, f.Coordinator.GetFailureCount(ComponentId.MainRouter));
        f.Router.LoseXray();
        await f.Reconcile();
        Assert.Equal(2, f.Coordinator.GetFailureCount(ComponentId.MainRouter));
        for (var i = 0; i < 10; i++) await f.Reconcile(ReconcileReason.TunStructuralFailure);
        Assert.Equal(2, f.Router.Starts);
    }

    [Fact]
    public async Task StabilizedXrayRecoveryKeepsRunningLifecycleWithoutAdditionalRestart()
    {
        using var f = new Candidate12Tests.Fixture(new SettingsStore(root));
        await f.Reconcile();
        f.Router.LoseXray();
        await f.Reconcile();
        f.Clock.Advance(TimeSpan.FromHours(1));
        await f.Reconcile();
        f.Clock.Advance(TimeSpan.FromSeconds(31));
        await f.Reconcile();
        Assert.Equal(2, f.Router.Starts);
        Assert.Equal(MainRouterLifecycle.Running, f.Coordinator.MainRouterLifecycle);
        Assert.Equal(0, f.Coordinator.GetFailureCount(ComponentId.MainRouter));
        Assert.True(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
    }
}
