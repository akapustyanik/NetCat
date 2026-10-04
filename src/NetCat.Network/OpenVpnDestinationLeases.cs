using System.Net;
using NetCat.Core;

namespace NetCat.Network;

// One instance per OpenVpnService. The journal serializes all static/dynamic
// mutations; this gate additionally serializes acquisition vs last-reference close.
public sealed class OpenVpnDestinationLeases(string journalPath,
    Func<IReadOnlyList<RouteRow>> capture,
    Func<string,CancellationToken,Task<(int Code,string Output)>> run)
{
    public sealed class Generation(OpenVpnLink link, Func<bool> ready, IReadOnlyCollection<IPAddress> transportEndpoints)
    {
        public OpenVpnLink Link { get; }=link;
        internal Func<bool> Ready { get; }=ready;
        internal IReadOnlyCollection<IPAddress> TransportEndpoints { get; }=transportEndpoints.ToArray();
        internal CancellationTokenSource Stop { get; }=new();
        public CancellationToken Token=>Stop.Token;
        internal Dictionary<IPAddress,int> References { get; }=[];
    }
    public sealed class Lease(OpenVpnDestinationLeases owner,Generation generation,IPAddress address) : IAsyncDisposable
    {
        private int released;
        public IPAddress Address {get;}=address;
        public Generation Generation {get;}=generation;
        public ValueTask DisposeAsync()=>Interlocked.Exchange(ref released,1)==0?new(owner.ReleaseAsync(Generation,Address)):ValueTask.CompletedTask;
    }
    private readonly object stateGate=new();
    private readonly SemaphoreSlim gate=new(1);
    private Generation? active;
    public Generation Capture()
    {
        lock(stateGate) return active is {} g && IsCurrent(g)?g:throw new IOException("OpenVPN datapath unavailable.");
    }
    public bool IsCurrent(Generation generation)
    {lock(stateGate)return ReferenceEquals(active,generation)&&!generation.Token.IsCancellationRequested&&generation.Ready();}
    public void Activate(OpenVpnLink link,Func<bool> ready,IReadOnlyCollection<IPAddress>? transportEndpoints=null)
    {
        Generation? old;lock(stateGate){old=active;active=new(link,ready,transportEndpoints??[]);}
        old?.Stop.Cancel();
    }
    public void Invalidate()
    {Generation? old;lock(stateGate){old=active;active=null;}old?.Stop.Cancel();}
    // Caller holds the service transition gate. First revoke authorization and
    // cancel DNS/route work, then drain acquisition/release before retiring disk
    // ownership. Late releases keep only their old Generation reference table.
    public async Task RetireAsync(Func<Task> cleanup)
    {
        Invalidate();
        await gate.WaitAsync().ConfigureAwait(false);
        try{await cleanup().ConfigureAwait(false);}
        finally{gate.Release();}
    }
    public static bool OwnedAllowed(Generation generation,IPAddress address)=>OpenVpnRouteJournal.Covers(generation.Link.LearnedRoutes,address);
    public async Task<Lease> AcquireAsync(Generation generation,IPAddress address,bool explicitRoute,CancellationToken ct)
    {
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(ct,generation.Token);
        await gate.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            if(!IsCurrent(generation))throw new OperationCanceledException("Obsolete destination generation.");
            if(address.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork || generation.TransportEndpoints.Contains(address) ||
                !explicitRoute&&!OwnedAllowed(generation,address))throw new IOException("Unauthorized OpenVPN destination.");
            await OpenVpnRouteJournal.EnsureDestinationAsync(journalPath,generation.Link,address,()=>IsCurrent(generation),capture,run,stop.Token).ConfigureAwait(false);
            if(!IsCurrent(generation))throw new OperationCanceledException("Obsolete destination lease.");
            generation.References[address]=generation.References.GetValueOrDefault(address)+1;
            return new(this,generation,address);
        }
        finally{gate.Release();}
    }
    private async Task ReleaseAsync(Generation generation,IPAddress address)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var count=generation.References.GetValueOrDefault(address);
            if(count>1){generation.References[address]=count-1;return;}
            generation.References.Remove(address);
            await OpenVpnRouteJournal.ReleaseDestinationAsync(journalPath,generation.Link,address,run,()=>IsCurrent(generation)).ConfigureAwait(false);
        }
        finally{gate.Release();}
    }
    public int ReferenceCount(Generation generation,IPAddress address)
    {gate.Wait();try{return generation.References.GetValueOrDefault(address);}finally{gate.Release();}}
}
