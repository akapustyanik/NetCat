using NetCat.Core;
using NetCat.Network;
using NetCat.Engine;
using System.Reflection;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate26XrayTests : IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"NetCat-C26-"+Guid.NewGuid().ToString("N"));
    Candidate12Tests.Fixture Create() => new(new SettingsStore(root));
    public void Dispose() { if(Directory.Exists(root)) Directory.Delete(root,true); }
    [Fact] public async Task OwnedXrayExitRequestsReconcileAndRecovers()
    {
        using var f=Create(); await f.Reconcile();
        f.Router.LoseXray(); await f.Reconcile();
        Assert.Equal(1,f.Coordinator.GetFailureCount(ComponentId.MainRouter));
        Assert.Equal(1,f.Router.Starts);
        f.Clock.Advance(TimeSpan.FromHours(1)); await f.Reconcile();
        Assert.True(f.Router.VpnRunning); Assert.Equal(2,f.Router.Starts);
    }
    [Fact] public async Task XrayCannotResurrectAfterDesiredOff()
    {
        using var f=Create(); await f.Reconcile();
        f.Router.LoseXray(); await f.Reconcile();
        f.Desired.Current=f.Desired.Current with{MainVpnEnabled=false};
        await f.Reconcile(); f.Clock.Advance(TimeSpan.FromHours(2)); await f.Reconcile();
        Assert.False(f.Router.IsRunning); Assert.Equal(1,f.Router.Starts);
    }
    [Fact] public async Task XrayRecoveryUsesCurrentProfile()
    {
        using var f=Create(); await f.Reconcile(); f.Router.LoseXray(); await f.Reconcile();
        f.Desired.Current=f.Desired.Current with{SelectedVpnProfileId=f.B.Id};
        await f.Reconcile(ReconcileReason.UserSelectedVpnProfile);
        Assert.Equal(f.B.Id,f.Router.ActiveProfileId); Assert.Equal(2,f.Router.Starts);
    }
    [Fact] public async Task RepeatedXrayExitRetainsBackoffAcrossListenerReadiness()
    {
        using var f=Create(); await f.Reconcile();
        for(int n=1;n<=3;n++)
        {
            f.Router.LoseXray(); await f.Reconcile();
            for(int i=0;i<10;i++) await f.Reconcile(ReconcileReason.TunStructuralFailure);
            Assert.Equal(n,f.Router.Starts); Assert.Equal(n,f.Coordinator.GetFailureCount(ComponentId.MainRouter));
            f.Clock.Advance(TimeSpan.FromHours(1)); await f.Reconcile();
        }
        Assert.Equal(4,f.Router.Starts);
    }
    [Fact] public async Task HealthyTunWithDeadRequiredXrayIsDegradedDependency()
    {
        using var f=Create(); await f.Reconcile(); f.Router.LoseXray(); await f.Reconcile();
        Assert.True(f.Coordinator.CurrentObservedState!.TunObservedHealthy);
        Assert.NotEqual(ObservedComponentState.RunningHealthy,f.Coordinator.CurrentObservedState.MainRouterStatus);
        Assert.False(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
    }
    [Fact] public async Task NonXrayProfileKeepsHealthyDependency()
    {
        using var f=Create(); await f.Reconcile(); await f.Reconcile();
        Assert.True(f.Coordinator.CurrentConvergenceState.DesiredSatisfied); Assert.Equal(1,f.Router.Starts);
    }
    [Fact] public async Task OwnedXrayProcessExitSignalsDependencyAndLeavesForeignProcessUntouched()
    {
        using var router=new RouterService("unused",root);
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var owned=(ProcessHost)typeof(RouterService).GetField("xray",flags)!.GetValue(router)!;
        typeof(RouterService).GetField("requiresXray",flags)!.SetValue(router,true);
        typeof(RouterService).GetProperty("VpnRequested")!.SetValue(router,true);
        using var foreign=new ProcessHost();
        foreign.Start(ProcessHost.PowerShellPath,["-NoProfile","-NonInteractive","-Command","Start-Sleep -Seconds 60"]);
        var foreignPid=foreign.Id;
        var lost=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.DependencyLost+=()=>lost.TrySetResult();
        owned.Start(ProcessHost.PowerShellPath,["-NoProfile","-NonInteractive","-Command","Start-Sleep -Seconds 60"]);
        Assert.True(router.DependenciesHealthy);
        using(var process=System.Diagnostics.Process.GetProcessById(owned.Id)) process.Kill();
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(router.DependenciesHealthy);Assert.True(foreign.Running);Assert.Equal(foreignPid,foreign.Id);
        await foreign.StopAsync();
    }
}
