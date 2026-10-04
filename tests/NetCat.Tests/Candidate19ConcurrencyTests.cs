using NetCat.Core;
using NetCat.Network;
using Xunit;
namespace NetCat.Tests;
public sealed class Candidate19ConcurrencyTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"NetCat-C19-statepublish-"+Guid.NewGuid().ToString("N"));
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
    [Fact] public async Task SuppressedTunSignalCanReturnToConverged()
    {
        using var f=new Candidate12Tests.Fixture(new SettingsStore(root));await f.Reconcile();
        f.Physical.Usable=false;await f.Reconcile(ReconcileReason.PhysicalNetworkChanged);
        Assert.NotEqual(ConvergencePhase.Converged,f.Coordinator.CurrentConvergenceState.Phase);
        f.Physical.Usable=true;await f.Reconcile(ReconcileReason.TunStructuralFailure);
        Assert.Equal(ConvergencePhase.Converged,f.Coordinator.CurrentConvergenceState.Phase);Assert.Equal(1,f.Router.Starts);
    }
    [Fact] public async Task SuppressedReconcileHasBeginAndComplete()
    {
        using var f=new Candidate12Tests.Fixture(new SettingsStore(root));await f.Reconcile();var logs=new List<string>();f.Coordinator.Log=logs.Add;
        await f.Reconcile(ReconcileReason.TunStructuralFailure);
        Assert.Contains(logs,s=>s.StartsWith("RECONCILE ")&&s.Contains("begin"));
        Assert.Contains(logs,s=>s.StartsWith("RECONCILE ")&&s.Contains("complete"));
    }
    [Fact] public async Task ConcurrentTunHealthAndReconcileCannotLoseTunUpdate()
    {
        using var f=new Candidate12Tests.Fixture(new SettingsStore(root));await f.Reconcile();
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();int armed=1;
        f.Coordinator.Log=s=>{if(s.StartsWith("TUN_OBSERVE processAlive=")&&Interlocked.Exchange(ref armed,0)==1){entered.Set();if(!release.Wait(TimeSpan.FromSeconds(5)))throw new TimeoutException();}};
        var pass=Task.Run(()=>f.Reconcile());Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {f.Tunnel.Status=TunStructuralStatus.TransientDegraded;await f.Coordinator.ObserveTunnelAsync("deterministic-race");}
        finally{release.Set();}
        await pass;
        Assert.Equal(TunStructuralStatus.TransientDegraded,f.Coordinator.CurrentObservedState!.TunStatus);
    }
}
