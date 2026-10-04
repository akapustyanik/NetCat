using System.Reflection;
using NetCat.Core;
using NetCat.Network;
using Xunit;
namespace NetCat.Tests;
public sealed class Candidate20ReconcileTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"NetCat-C20-reconcile-"+Guid.NewGuid().ToString("N"));
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
    private Candidate12Tests.Fixture Create()=>new(new SettingsStore(root));
    [Fact] public async Task SuppressedTunStructuralFailureStillStopsOpenVpnWhenDesiredOff()
    {
        await using var f=new Candidate19StateTests.Fixture();await f.Start();
        f.Base.Desired.Current=f.Base.Desired.Current with{OpenVpnEnabled=false};
        await f.Coordinator.ReconcileAsync(ReconcileReason.TunStructuralFailure);
        Assert.Equal(0,f.Service.ProcessId);Assert.True(f.AdapterReleased);f.Stable();
        Assert.True(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
    }
    [Fact] public async Task SuppressedTunStructuralFailureStillStopsMainVpnWhenDesiredOff()
    {
        using var f=Create();await f.Reconcile();f.Desired.Current=f.Desired.Current with{MainVpnEnabled=false};
        await f.Reconcile(ReconcileReason.TunStructuralFailure);Assert.False(f.Router.IsRunning);Assert.Equal(1,f.Router.Stops);Assert.Equal(1,f.Zapret.Starts);
    }
    [Fact] public async Task SuppressedTunStructuralFailureStillAppliesProfileSwitch()
    {
        using var f=Create();await f.Reconcile();f.Desired.Current=f.Desired.Current with{SelectedVpnProfileId=f.B.Id};
        await f.Reconcile(ReconcileReason.TunStructuralFailure);Assert.Equal(f.B.Id,f.Router.ActiveProfileId);Assert.Equal(2,f.Router.Starts);Assert.Equal(1,f.Zapret.Starts);
    }
    [Fact] public async Task SuppressedTunStructuralFailureStillAppliesZapretChange()
    {
        using var f=Create();await f.Reconcile();f.Desired.Current=f.Desired.Current with{ZapretEnabled=false};
        await f.Reconcile(ReconcileReason.TunStructuralFailure);Assert.False(f.Zapret.IsRunning);Assert.Equal(1,f.Router.Starts);Assert.Equal(1,f.Router.SessionRevision);
    }
    [Fact] public async Task CoalescedTunSignalCannotConsumeUserSettingsReason()
    {
        using var f=Create();await f.Reconcile();
        // Hold the production reconcile gate so both requests are pending before
        // a pass may observe them. No scheduler timing assumption is required.
        var gate=(SemaphoreSlim)typeof(RuntimeCoordinator).GetField("reconcileGate",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(f.Coordinator)!;
        await gate.WaitAsync();
        try{f.Desired.Current=f.Desired.Current with{ZapretEnabled=false};f.Coordinator.RequestReconcile(ReconcileReason.UserChangedSettings);f.Coordinator.RequestReconcile(ReconcileReason.TunStructuralFailure);}
        finally{gate.Release();}
        // ReconcileAsync itself queues the same stale reason and joins the loop.
        await f.Coordinator.ReconcileAsync(ReconcileReason.TunStructuralFailure).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(f.Zapret.IsRunning);Assert.True(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);Assert.Equal(1,f.Router.Starts);
    }
    [Fact] public async Task SuppressedTunSignalExecutesIndependentPlanActions()
    {
        using var f=Create();await f.Reconcile();f.Desired.Current=f.Desired.Current with{MainVpnEnabled=false,ZapretEnabled=false};
        await f.Reconcile(ReconcileReason.TunStructuralFailure);Assert.False(f.Router.IsRunning);Assert.False(f.Zapret.IsRunning);Assert.True(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
    }
    [Fact] public async Task SuppressedTunSignalDoesNotCreateInfiniteRetry()
    {
        using var f=Create();await f.Reconcile();f.Desired.Current=f.Desired.Current with{ZapretEnabled=false};
        await f.Reconcile(ReconcileReason.TunStructuralFailure).WaitAsync(TimeSpan.FromSeconds(10));
        await f.Reconcile(ReconcileReason.TunStructuralFailure).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(f.Zapret.IsRunning);Assert.Equal(1,f.Zapret.Stops);Assert.Equal(1,f.Router.Starts);Assert.True(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
    }
    [Fact] public async Task SuppressedTunSignalStillAppliesPhysicalSettingChange()
    {
        using var f=Create();await f.Reconcile();f.Physical.Current=new("new-physical",28,"192.168.3.3","192.168.3.1",[]);
        await f.Reconcile(ReconcileReason.TunStructuralFailure);Assert.Equal(28,f.Router.ActivePhysical!.Index);Assert.Equal(2,f.Router.Starts);
    }
    [Fact] public async Task SameProfileNeedsRestartUsesLocalStopThenStart()
    {
        await using var f=new Candidate19StateTests.Fixture();await f.Start();var old=f.Service.ProcessId;
        await f.Base.Repo.UpdateSettingsAsync(s=>{s.Profiles.First(p=>p.Id==f.Base.O.Id).AllowPublicPushedRoutes=true;return s;});
        await f.Coordinator.ReconcileAsync(ReconcileReason.UserChangedSettings);Assert.True(f.Service.IsRunning);Assert.NotEqual(old,f.Service.ProcessId);f.Stable();
    }
    [Fact] public async Task SuppressedTunSignalStillAppliesOpenVpnPolicyChange()
    {
        await using var f=new Candidate19StateTests.Fixture();await f.Start();var old=f.Service.ProcessId;
        await f.Base.Repo.UpdateSettingsAsync(s=>{s.Profiles.First(p=>p.Id==f.Base.O.Id).AllowPublicPushedRoutes=true;return s;});
        await f.Coordinator.ReconcileAsync(ReconcileReason.TunStructuralFailure);Assert.True(f.Service.IsRunning);Assert.NotEqual(old,f.Service.ProcessId);f.Stable();
    }
    [Fact] public async Task UserDesiredChangeBypassesExternalConditionRetryDelay()
    {
        using var f=Create();await f.Reconcile();
        f.Desired.Current=f.Desired.Current with{ZapretEnabled=false};
        f.Coordinator.RequestReconcile(ReconcileReason.UserChangedSettings);
        await f.Coordinator.ReconcileAsync(ReconcileReason.UserChangedSettings).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(f.Zapret.IsRunning);Assert.True(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
    }
    [Fact] public async Task ReasonCoalescingCannotDelayExplicitUserOff()
    {
        using var f=Create();await f.Reconcile();
        var gate=(SemaphoreSlim)typeof(RuntimeCoordinator).GetField("reconcileGate",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(f.Coordinator)!;
        await gate.WaitAsync();
        try
        {
            f.Desired.Current=f.Desired.Current with{MainVpnEnabled=false};
            f.Coordinator.RequestReconcile(ReconcileReason.PhysicalNetworkChanged);
            f.Coordinator.RequestReconcile(ReconcileReason.UserChangedSettings);
        }
        finally{gate.Release();}
        await f.Coordinator.ReconcileAsync(ReconcileReason.PhysicalNetworkChanged).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(f.Router.IsRunning);Assert.True(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
    }
}
