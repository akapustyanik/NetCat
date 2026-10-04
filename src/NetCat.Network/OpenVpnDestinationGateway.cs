using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NetCat.Core;

namespace NetCat.Network;

public sealed record OpenVpnDestinationEndpoint(int Port,string OwnedPassword,string ExplicitPassword);

// The native sidecar terminates the encrypted transport and selects one of two
// independently authenticated SOCKS channels. Only this class performs the final
// corporate connect/send, after the concrete address has an acknowledged route.
public sealed class OpenVpnDestinationGateway : IDisposable
{
    private readonly OpenVpnDestinationLeases leases;
    private readonly Func<int> peerProcess;
    private readonly TcpListener listener=new(IPAddress.Loopback,0);
    private readonly CancellationTokenSource lifetime=new();
    private readonly SemaphoreSlim capacity=new(512);
    private readonly object activationGate=new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long,Task> requests=new();
    private readonly Task acceptTask;
    private long requestId;
    private int disposed;
    private CancellationTokenSource? activation;
    public OpenVpnDestinationEndpoint Endpoint {get;}
    public TimeSpan UdpIdleTimeout {get;init;}=TimeSpan.FromSeconds(30);
    public Action<string>? Diagnostic {get;set;}
    public Func<string,OpenVpnLink,Func<bool>,CancellationToken,Task<IReadOnlyList<OpenVpnResolvedAddress>>> Resolve {get;init;}
        =(name,link,current,ct)=>OpenVpnDestinationDns.ResolveAsync(name,link,current,ct);

    public OpenVpnDestinationGateway(OpenVpnDestinationLeases leases,Func<int> peerProcess)
    {
        this.leases=leases;this.peerProcess=peerProcess;
        listener.Server.ExclusiveAddressUse=true;listener.Start();
        Endpoint=new(((IPEndPoint)listener.LocalEndpoint).Port,Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        VerifyOwnership();acceptTask=AcceptAsync();
    }
    public void VerifyOwnership()
    {if(LocalListener.Owner(Endpoint.Port)!=Environment.ProcessId)throw new IOException("OpenVPN egress listener ownership mismatch.");}
    public void Activate()
    {lock(activationGate){activation?.Cancel();activation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);}}
    public void Invalidate()
    {lock(activationGate){activation?.Cancel();activation=null;}}
    private CancellationToken ActiveToken()
    {lock(activationGate)return activation?.Token??throw new IOException("OpenVPN egress inactive.");}
    private async Task AcceptAsync()
    {
        try
        {
            while(true)
            {
                var client=await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                if(!capacity.Wait(0)){client.Dispose();continue;}
                long id=Interlocked.Increment(ref requestId);
                var task=HandleAsync(client);requests[id]=task;
                _=task.ContinueWith(completed=>{requests.TryRemove(id,out _);},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            }
        }
        catch(Exception e)when(e is OperationCanceledException or SocketException or ObjectDisposedException){}
    }
    private static bool PasswordEquals(string expected,byte[] supplied)=>CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected),supplied);
    private async Task HandleAsync(TcpClient client)
    {
        using(client)
        try
        {
            int pid=peerProcess();
            if(pid<=0 || client.Client.RemoteEndPoint is not IPEndPoint peer || !IPAddress.IsLoopback(peer.Address) || LocalListener.ConnectedOwner(peer.Port,Endpoint.Port)!=pid)return;
            var generation=leases.Capture();
            using var stop=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,ActiveToken(),generation.Token);
            var ct=stop.Token;var stream=client.GetStream();
            using var handshake=CancellationTokenSource.CreateLinkedTokenSource(ct);handshake.CancelAfter(TimeSpan.FromSeconds(5));
            var header=new byte[2];await stream.ReadExactlyAsync(header,handshake.Token).ConfigureAwait(false);
            if(header[0]!=5 || header[1]==0)return;
            var methods=new byte[header[1]];await stream.ReadExactlyAsync(methods,handshake.Token).ConfigureAwait(false);
            if(!methods.Contains((byte)2)){await stream.WriteAsync(new byte[]{5,255},handshake.Token).ConfigureAwait(false);return;}
            await stream.WriteAsync(new byte[]{5,2},handshake.Token).ConfigureAwait(false);
            await stream.ReadExactlyAsync(header,handshake.Token).ConfigureAwait(false);if(header[0]!=1 || header[1]==0)return;
            var user=new byte[header[1]];await stream.ReadExactlyAsync(user,handshake.Token).ConfigureAwait(false);
            var size=new byte[1];await stream.ReadExactlyAsync(size,handshake.Token).ConfigureAwait(false);
            var password=new byte[size[0]];await stream.ReadExactlyAsync(password,handshake.Token).ConfigureAwait(false);
            string role=Encoding.ASCII.GetString(user);
            bool explicitRoute=role=="explicit" && PasswordEquals(Endpoint.ExplicitPassword,password);
            bool owned=role=="owned" && PasswordEquals(Endpoint.OwnedPassword,password);
            await stream.WriteAsync(new byte[]{1,(byte)(explicitRoute||owned?0:1)},handshake.Token).ConfigureAwait(false);if(!explicitRoute&&!owned)return;
            var request=new byte[4];await stream.ReadExactlyAsync(request,handshake.Token).ConfigureAwait(false);
            if(request[0]!=5 || request[2]!=0)return;
            var target=await ReadTargetAsync(stream,request[3],handshake.Token).ConfigureAwait(false);
            if(request[1]==1)await TcpAsync(client,generation,target,explicitRoute,ct).ConfigureAwait(false);
            else if(request[1]==3)await UdpAsync(client,generation,target,explicitRoute,pid,ct).ConfigureAwait(false);
        }
        catch(Exception e)when(e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {if(e is not OperationCanceledException)Diagnostic?.Invoke("OPENVPN_EGRESS request_failed type="+e.GetType().Name);}
        finally{capacity.Release();}
    }
    private sealed record Target(string Host,int Port);
    private static async Task<Target> ReadTargetAsync(Stream stream,byte type,CancellationToken ct)
    {
        byte[] address;
        if(type==1){address=new byte[4];await stream.ReadExactlyAsync(address,ct).ConfigureAwait(false);}
        else if(type==4){address=new byte[16];await stream.ReadExactlyAsync(address,ct).ConfigureAwait(false);}
        else if(type==3){var n=new byte[1];await stream.ReadExactlyAsync(n,ct).ConfigureAwait(false);if(n[0]==0)throw new IOException("Empty destination.");address=new byte[n[0]];await stream.ReadExactlyAsync(address,ct).ConfigureAwait(false);}
        else throw new IOException("Only IPv4 and corporate hostnames are supported.");
        var port=new byte[2];await stream.ReadExactlyAsync(port,ct).ConfigureAwait(false);
        return new(type is 1 or 4?new IPAddress(address).ToString():Encoding.ASCII.GetString(address),BinaryPrimitives.ReadUInt16BigEndian(port));
    }
    private async Task<IReadOnlyList<OpenVpnResolvedAddress>> AddressesAsync(OpenVpnDestinationLeases.Generation generation,Target target,bool explicitRoute,CancellationToken ct)
    {
        if(!leases.IsCurrent(generation))throw new OperationCanceledException();
        if(IPAddress.TryParse(target.Host,out var address))return [new(address,uint.MaxValue)];
        // Owned traffic is CIDR based. Hostname authority belongs only to the
        // independent explicit route channel, before any DNS request is sent.
        if(!explicitRoute)throw new IOException("Owned hostname not authorized.");
        await using var dns=await leases.AcquireAsync(generation,IPAddress.Parse(generation.Link.Dns),true,ct).ConfigureAwait(false);
        var result=await Resolve(target.Host,generation.Link,()=>leases.IsCurrent(generation),ct).ConfigureAwait(false);
        if(!leases.IsCurrent(generation))throw new OperationCanceledException();
        return result;
    }
    private static byte[] Reply(int port=0)=>[5,0,0,1,127,0,0,1,(byte)(port>>8),(byte)port];
    private async Task TcpAsync(TcpClient client,OpenVpnDestinationLeases.Generation generation,Target target,bool explicitRoute,CancellationToken ct)
    {
        if(target.Port==0)throw new IOException("Invalid TCP destination port.");
        var addresses=await AddressesAsync(generation,target,explicitRoute,ct).ConfigureAwait(false);
        foreach(var answer in addresses)
        {
            await using var route=await leases.AcquireAsync(generation,answer.Address,explicitRoute,ct).ConfigureAwait(false);
            using var remote=new TcpClient(AddressFamily.InterNetwork);
            OpenVpnDestinationDns.Bind(remote.Client,generation.Link);
            using var dial=CancellationTokenSource.CreateLinkedTokenSource(ct);dial.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                ct.ThrowIfCancellationRequested();if(!leases.IsCurrent(generation))throw new OperationCanceledException();
                await remote.ConnectAsync(answer.Address,target.Port,dial.Token).ConfigureAwait(false);
            }
            catch(Exception e)when(!ct.IsCancellationRequested && e is SocketException or OperationCanceledException){continue;}
            await client.GetStream().WriteAsync(Reply(),ct).ConfigureAwait(false);
            await TcpRelay.CopyAsync(client,remote,ct).ConfigureAwait(false);return;
        }
        throw new IOException("Corporate TCP destination unavailable.");
    }
    private sealed class UdpSession(UdpClient socket,OpenVpnDestinationLeases.Lease lease,IPEndPoint destination) : IAsyncDisposable
    {
        public UdpClient Socket {get;}=socket;
        public IPEndPoint Destination {get;}=destination;
        public DateTimeOffset Used {get;set;}=DateTimeOffset.UtcNow;
        public Task? Receive {get;set;}
        public async ValueTask DisposeAsync(){Socket.Dispose();if(Receive!=null)try{await Receive.ConfigureAwait(false);}catch{}await lease.DisposeAsync().ConfigureAwait(false);}
    }
    private async Task UdpAsync(TcpClient client,OpenVpnDestinationLeases.Generation generation,Target requested,bool explicitRoute,int pid,CancellationToken ct)
    {
        if(!IPAddress.TryParse(requested.Host,out var requestedIp) || !(IPAddress.IsLoopback(requestedIp)||requestedIp.Equals(IPAddress.Any)||requestedIp.Equals(IPAddress.IPv6Any)))throw new IOException("Invalid UDP association.");
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(ct);var token=stop.Token;
        using var relay=new UdpClient(AddressFamily.InterNetwork);relay.Client.ExclusiveAddressUse=true;relay.Client.Bind(new IPEndPoint(IPAddress.Loopback,0));
        await client.GetStream().WriteAsync(Reply(((IPEndPoint)relay.Client.LocalEndPoint!).Port),token).ConfigureAwait(false);
        var sessions=new Dictionary<IPEndPoint,UdpSession>();using var sessionGate=new SemaphoreSlim(1);
        var answers=new Dictionary<string,(IReadOnlyList<OpenVpnResolvedAddress> Addresses,DateTimeOffset Expiry)>();
        IPEndPoint? peer=null;
        async Task Control(){try{var b=new byte[1];while(await client.GetStream().ReadAsync(b,token).ConfigureAwait(false)>0){}}finally{stop.Cancel();}}
        async Task Expire()
        {
            try
            {
            using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Clamp(UdpIdleTimeout.TotalMilliseconds/2,10,1000)));
            while(await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await sessionGate.WaitAsync(token).ConfigureAwait(false);
                try{foreach(var pair in sessions.Where(p=>DateTimeOffset.UtcNow-p.Value.Used>=UdpIdleTimeout).ToArray()){sessions.Remove(pair.Key);await pair.Value.DisposeAsync().ConfigureAwait(false);}}
                finally{sessionGate.Release();}
            }
            }
            catch(IOException){Diagnostic?.Invoke("OPENVPN_EGRESS lease_cleanup_failed journal_retained=true");stop.Cancel();throw;}
            catch{stop.Cancel();throw;}
        }
        var control=Control();var expiry=Expire();
        try
        {
            while(true)
            {
                var packet=await relay.ReceiveAsync(token).ConfigureAwait(false);
                if(!IPAddress.IsLoopback(packet.RemoteEndPoint.Address) || requested.Port!=0&&packet.RemoteEndPoint.Port!=requested.Port || peer!=null&&!peer.Equals(packet.RemoteEndPoint))continue;
                if(LocalListener.UdpOwner(packet.RemoteEndPoint)!=pid){Diagnostic?.Invoke("OPENVPN_EGRESS udp_peer_owner_mismatch");continue;}
                peer??=packet.RemoteEndPoint;
                var data=packet.Buffer;if(data.Length<7 || data[0]!=0 || data[1]!=0 || data[2]!=0)continue;
                using var input=new MemoryStream(data,4,data.Length-4,false);
                Target target;try{target=await ReadTargetAsync(input,data[3],token).ConfigureAwait(false);}catch(IOException){continue;}
                if(target.Port==0)continue;var payload=new byte[input.Length-input.Position];await input.ReadExactlyAsync(payload,token).ConfigureAwait(false);
                if(!answers.TryGetValue(target.Host,out var cached) || cached.Expiry<=DateTimeOffset.UtcNow)
                {
                    var resolved=await AddressesAsync(generation,target,explicitRoute,token).ConfigureAwait(false);if(resolved.Count==0)continue;
                    if(answers.Count>=256)answers.Clear();
                    cached=(resolved,DateTimeOffset.UtcNow.AddSeconds(Math.Min(300,resolved.Min(a=>a.Ttl))));answers[target.Host]=cached;
                }
                var destination=new IPEndPoint(cached.Addresses[0].Address,target.Port);
                await sessionGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if(!sessions.TryGetValue(destination,out var session))
                    {
                        if(sessions.Count>=256)continue;
                        var route=await leases.AcquireAsync(generation,destination.Address,explicitRoute,token).ConfigureAwait(false);
                        UdpClient? remote=null;
                        try
                        {
                            remote=new UdpClient(AddressFamily.InterNetwork);OpenVpnDestinationDns.Bind(remote.Client,generation.Link);remote.Connect(destination);
                            session=new(remote,route,destination);sessions.Add(destination,session);
                            session.Receive=ReceiveUdpAsync(session,relay,peer,token);
                        }
                        catch{remote?.Dispose();await route.DisposeAsync().ConfigureAwait(false);throw;}
                    }
                    // Reverify on send so a removed/replaced route cannot silently
                    // change the path of a still-live UDP association.
                    await using var verified=await leases.AcquireAsync(generation,destination.Address,explicitRoute,token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();if(!leases.IsCurrent(generation))throw new OperationCanceledException();
                    await session.Socket.SendAsync(payload,token).ConfigureAwait(false);session.Used=DateTimeOffset.UtcNow;
                }
                finally{sessionGate.Release();}
            }
        }
        finally
        {
            stop.Cancel();try{await Task.WhenAll(control,expiry).ConfigureAwait(false);}catch{}
            foreach(var session in sessions.Values)
                try{await session.DisposeAsync().ConfigureAwait(false);}
                catch(IOException){Diagnostic?.Invoke("OPENVPN_EGRESS lease_cleanup_failed journal_retained=true");}
        }
    }
    private static async Task ReceiveUdpAsync(UdpSession session,UdpClient relay,IPEndPoint peer,CancellationToken ct)
    {
        try
        {
            while(true)
            {
                var reply=await session.Socket.ReceiveAsync(ct).ConfigureAwait(false);
                var data=new byte[10+reply.Buffer.Length];data[3]=1;session.Destination.Address.GetAddressBytes().CopyTo(data,4);
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8,2),(ushort)session.Destination.Port);reply.Buffer.CopyTo(data,10);
                await relay.SendAsync(data,peer,ct).ConfigureAwait(false);
            }
        }
        catch(Exception e)when(e is SocketException or OperationCanceledException or ObjectDisposedException){}
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        Invalidate();lifetime.Cancel();listener.Stop();
        // Stop accepting before taking the snapshot. Completing a request includes
        // its await-using lease cleanup; returning earlier races journal retirement.
        async Task Drain()
        {
            await acceptTask.ConfigureAwait(false);
            await Task.WhenAll(requests.Values).ConfigureAwait(false);
        }
        Drain().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    }
}
