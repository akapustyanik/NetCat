using NetCat.Core;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;
public sealed class Candidate29MaintenanceTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"NetCat-C29-Maintenance-"+Guid.NewGuid().ToString("N"));
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
    [Fact] public async Task ZapretUpdatePreservesMainVpnAndDesiredState()
    {
        using var f=new Candidate12Tests.Fixture(new SettingsStore(root));await f.Reconcile();
        var desired=f.Desired.Current;int mainStarts=f.Router.Starts,mainStops=f.Router.Stops,openStops=f.OpenVpn.Stops;
        await f.Coordinator.RunModuleUpdateAsync("zapret",async(health,stop,ct)=>
        {
            Assert.False(f.Zapret.IsRunning);Assert.True(f.Router.IsRunning);
            await health(ct);Assert.True(f.Zapret.IsRunning);
        },default);
        Assert.Equal(desired,f.Desired.Current);Assert.Equal(mainStarts,f.Router.Starts);Assert.Equal(mainStops,f.Router.Stops);Assert.Equal(openStops,f.OpenVpn.Stops);
    }
    [Fact] public async Task FailedNewRouterStartStopsNewCodeBeforeRollbackAndRestoresOld()
    {
        using var f=new Candidate12Tests.Fixture(new SettingsStore(root));await f.Reconcile();var desired=f.Desired.Current;bool rollback=false;
        await Assert.ThrowsAsync<IOException>(()=>f.Coordinator.RunModuleUpdateAsync("sing-box",async(health,stop,ct)=>
        {
            Assert.False(f.Router.IsRunning);
            f.Router.BeforeStart=()=>throw new IOException("new binary failed");
            try{await health(ct);}catch{Assert.False(f.Router.IsRunning);rollback=true;f.Router.BeforeStart=null;throw;}
        },default));
        Assert.True(rollback);Assert.True(f.Router.IsRunning);Assert.Equal(desired,f.Desired.Current);
    }
    [Fact] public async Task UserOffDuringUpdateIsNotOverwrittenBySavedOnState()
    {
        using var f=new Candidate12Tests.Fixture(new SettingsStore(root));await f.Reconcile();
        await f.Coordinator.RunModuleUpdateAsync("xray",async(health,stop,ct)=>
        {
            f.Desired.Current=f.Desired.Current with {MainVpnEnabled=false};await health(ct);
        },default);
        Assert.False(f.Desired.Current.MainVpnEnabled);Assert.False(f.Router.IsRunning);
    }
}
