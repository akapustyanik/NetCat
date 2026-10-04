using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NetCat.Network;

// Automatic VPN is a distinct authenticated channel from deliberate user VPN.
// It never resolves names: the ownership decision precedes handing the original
// SOCKS target to the native VPN transport. The public listener stays owned.
internal sealed class GuardedVpnGateway(CorporateDomainGuard domains) : IDisposable
{
    private readonly TcpListener listener = StartListener();
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<long, TcpClient> clients = new();
    private readonly LocalSocksAdmission admission = new();
    private long sequence;
    private volatile int returnPort;
    public string Username { get; } = "netcat-auto-vpn";
    public string Password { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public int ReturnPort => returnPort;
    public void Start(int port) { returnPort = port; _ = Accept(); }
    public void SetReturnPort(int port) => returnPort = port;
    private static TcpListener StartListener() { var t = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true }; t.Start(); return t; }
    private async Task Accept()
    {
        try { while (!lifetime.IsCancellationRequested) { var c = await listener.AcceptTcpClientAsync(lifetime.Token); var lease=admission.TryAccept(); if(lease==null){c.Dispose();continue;} _ = Serve(c,lease); } }
        catch(Exception e) when(e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private static async Task<byte[]> Read(Stream s,int n,CancellationToken ct) { var b=new byte[n]; await s.ReadExactlyAsync(b,ct);return b; }
    private static async Task<(byte[] Wire,string Host)> Target(Stream s,byte type,CancellationToken ct)
    {
        byte[] address; byte[] prefix=[type];
        if(type==3) { byte length=(await Read(s,1,ct))[0];if(length==0)throw new IOException("Empty SOCKS name");prefix=[type,length];address=await Read(s,length,ct); }
        else address=await Read(s,type==1?4:type==4?16:throw new IOException("SOCKS address type"),ct);
        var port=await Read(s,2,ct);
        return (prefix.Concat(address).Concat(port).ToArray(),type==3?Encoding.UTF8.GetString(address):new IPAddress(address).ToString());
    }
    private async Task Authenticate(Stream s,CancellationToken ct)
    {
        var head=await Read(s,2,ct);if(head[0]!=5)throw new IOException("SOCKS version");
        var methods=await Read(s,head[1],ct);if(!methods.Contains((byte)2))throw new IOException("SOCKS authentication required");
        await s.WriteAsync(new byte[]{5,2},ct);if((await Read(s,1,ct))[0]!=1)throw new IOException("SOCKS auth version");
        var u=await Read(s,(await Read(s,1,ct))[0],ct);var p=await Read(s,(await Read(s,1,ct))[0],ct);
        if(!CryptographicOperations.FixedTimeEquals(u,Encoding.ASCII.GetBytes(Username)) || !CryptographicOperations.FixedTimeEquals(p,Encoding.ASCII.GetBytes(Password)))throw new IOException("SOCKS authentication failed");
        await s.WriteAsync(new byte[]{1,0},ct);
    }
    private async Task<TcpClient> Upstream(CancellationToken ct)
    {
        var c=new TcpClient();
        try {
            await c.ConnectAsync(IPAddress.Loopback,returnPort,ct);var s=c.GetStream();
            await s.WriteAsync(new byte[]{5,1,2},ct);var method=await Read(s,2,ct);if(method[0]!=5||method[1]!=2)throw new IOException("VPN return authentication required");
            var u=Encoding.ASCII.GetBytes(Username);var p=Encoding.ASCII.GetBytes(Password);
            await s.WriteAsync(new byte[]{1,(byte)u.Length}.Concat(u).Concat(new[]{(byte)p.Length}).Concat(p).ToArray(),ct);
            var auth=await Read(s,2,ct);if(auth[0]!=1||auth[1]!=0)throw new IOException("VPN return authentication failed");return c;
        }catch { c.Dispose();throw; }
    }
    private async Task Serve(TcpClient client,LocalSocksAdmission.Lease lease)
    {
        long id=Interlocked.Increment(ref sequence);clients[id]=client;
        using(lease) using(client) using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        try {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));var ct=timeout.Token;var s=client.GetStream();await Authenticate(s,ct);
            var h=await Read(s,4,ct);if(h[0]!=5||h[2]!=0)throw new IOException("SOCKS request");var target=await Target(s,h[3],ct);
            if(!lease.Promote(h[1]==3))throw new IOException("VPN gateway transport capacity reached");
            if(h[1]==3){timeout.CancelAfter(Timeout.InfiniteTimeSpan);await Udp(s,ct);return;}
            if(h[1]!=1||domains.IsCorporate(target.Host))throw new IOException("Corporate target cannot use automatic VPN");
            using var upstream=await Upstream(ct);var remote=upstream.GetStream();
            await domains.DispatchAutomatic(target.Host,()=>remote.WriteAsync(new byte[]{5,1,0}.Concat(target.Wire).ToArray(),ct).AsTask());
            var reply=await Read(remote,4,ct);if(reply[0]!=5||reply[1]!=0)throw new IOException("VPN return refused");
            var bound=await Target(remote,reply[3],ct);await s.WriteAsync(reply.Take(3).Concat(bound.Wire).ToArray(),ct);
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);await TcpRelay.CopyAsync(client,upstream,ct);
        }catch(Exception e)when(e is IOException or SocketException or OperationCanceledException or ObjectDisposedException){ }
        finally { clients.TryRemove(id,out _); }
    }
    private async Task Udp(NetworkStream control,CancellationToken ct)
    {
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var setup=CancellationTokenSource.CreateLinkedTokenSource(ct);setup.CancelAfter(TimeSpan.FromSeconds(10));
        using var upstream=await Upstream(setup.Token);var remote=upstream.GetStream();
        await remote.WriteAsync(new byte[]{5,3,0,1,127,0,0,1,0,0},setup.Token);
        var reply=await Read(remote,4,setup.Token);if(reply[0]!=5||reply[1]!=0)throw new IOException("VPN UDP refused");
        var target=await Target(remote,reply[3],setup.Token);
        if(!IPAddress.TryParse(target.Host,out var address)||!IPAddress.IsLoopback(address))throw new IOException("Nonlocal VPN UDP return");
        int upstreamPort=target.Wire[^2]*256+target.Wire[^1];
        using var outgoing=new UdpClient(AddressFamily.InterNetwork);outgoing.Connect(address,upstreamPort);
        using var incoming=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));int port=((IPEndPoint)incoming.Client.LocalEndPoint!).Port;
        await control.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,(byte)(port>>8),(byte)port},ct);
        IPEndPoint? peer=null;
        async Task Watch(Stream stream){try{await stream.ReadAsync(new byte[1],stop.Token);}finally{stop.Cancel();}}
        async Task Send()
        {
            while(!stop.IsCancellationRequested){var packet=await incoming.ReceiveAsync(stop.Token);
                if(!IPAddress.IsLoopback(packet.RemoteEndPoint.Address))continue;peer??=packet.RemoteEndPoint;if(!peer.Equals(packet.RemoteEndPoint))continue;
                var b=packet.Buffer;if(b.Length<4||b[0]!=0||b[1]!=0||b[2]!=0)continue;
                using var data=new MemoryStream(b,4,b.Length-4,false);var destination=await Target(data,b[3],stop.Token);
                if(domains.IsCorporate(destination.Host))continue;
                await domains.DispatchAutomatic(destination.Host,async()=>{await outgoing.SendAsync(b,stop.Token);});
            }
        }
        async Task Receive(){while(!stop.IsCancellationRequested){var packet=await outgoing.ReceiveAsync(stop.Token);if(peer!=null)await incoming.SendAsync(packet.Buffer,peer,stop.Token);}}
        var tasks=new[]{Watch(control),Watch(remote),Send(),Receive()};
        try{await await Task.WhenAny(tasks);}finally{stop.Cancel();try{await Task.WhenAll(tasks);}catch(Exception e)when(e is IOException or SocketException or OperationCanceledException or ObjectDisposedException){}}
    }
    public void Dispose(){lifetime.Cancel();listener.Stop();foreach(var client in clients.Values)client.Dispose();}
}
