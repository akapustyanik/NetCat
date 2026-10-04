using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate26EgressTests
{
    internal static async Task<TcpClient> Authenticate(OpenVpnDestinationGateway gateway,CancellationToken ct,bool owned=false,string? wrongPassword=null)
    {
        var client=new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback,gateway.Endpoint.Port,ct);var s=client.GetStream();var b=new byte[2];
            await s.WriteAsync(new byte[]{5,1,2},ct);await s.ReadExactlyAsync(b,ct);Assert.Equal(new byte[]{5,2},b);
            var user=Encoding.ASCII.GetBytes(owned?"owned":"explicit");var pass=Encoding.ASCII.GetBytes(wrongPassword??(owned?gateway.Endpoint.OwnedPassword:gateway.Endpoint.ExplicitPassword));
            await s.WriteAsync(new byte[]{1,(byte)user.Length}.Concat(user).Concat(new[]{(byte)pass.Length}).Concat(pass).ToArray(),ct);
            await s.ReadExactlyAsync(b,ct);Assert.Equal(1,b[0]);if(wrongPassword==null)Assert.Equal(0,b[1]);else Assert.Equal(1,b[1]);return client;
        }
        catch{client.Dispose();throw;}
    }
    internal static byte[] Address(string host,int port)
    {
        byte[] bytes=IPAddress.TryParse(host,out var ip)?new byte[]{1}.Concat(ip.GetAddressBytes()).ToArray():new byte[]{3,(byte)host.Length}.Concat(Encoding.ASCII.GetBytes(host)).ToArray();
        return bytes.Concat(new[]{(byte)(port>>8),(byte)port}).ToArray();
    }
    private static Task Request(TcpClient client,string host,int port,byte command,CancellationToken ct)
        =>client.GetStream().WriteAsync(new byte[]{5,command,0}.Concat(Address(host,port)).ToArray(),ct).AsTask();
    private static async Task Wait(Func<bool> condition)
    {using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!condition())await Task.Delay(10,deadline.Token);}
    private static OpenVpnDestinationGateway Gateway(Candidate26RouteFixture f)=>new(f.Leases,()=>Environment.ProcessId);
    [Fact] public async Task TcpLeaseCreatedBeforeDial()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();using var gateway=Gateway(f);gateway.Activate();
        using var echo=new TcpListener(IPAddress.Parse("127.0.0.3"),0);echo.Start();int port=((IPEndPoint)echo.LocalEndpoint).Port;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.BeforeCreate=async(_,ct)=>{entered.TrySetResult();await release.Task.WaitAsync(ct);};
        using var client=await Authenticate(gateway,deadline.Token);await Request(client,"127.0.0.3",port,1,deadline.Token);
        try{await entered.Task.WaitAsync(deadline.Token);Assert.False(echo.Pending());Assert.DoesNotContain(f.Capture(),r=>r.DestinationPrefix=="127.0.0.3/32");}
        finally{release.TrySetResult();}
        var b=new byte[10];await client.GetStream().ReadExactlyAsync(b,deadline.Token);Assert.Equal(0,b[1]);
        using var server=await echo.AcceptTcpClientAsync(deadline.Token);Assert.Contains(f.Capture(),r=>r.DestinationPrefix=="127.0.0.3/32");
    }
    [Fact] public async Task TcpCloseReleasesReference()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();using var gateway=Gateway(f);gateway.Activate();
        using var echo=new TcpListener(IPAddress.Parse("127.0.0.3"),0);echo.Start();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var client=await Authenticate(gateway,deadline.Token);await Request(client,"127.0.0.3",((IPEndPoint)echo.LocalEndpoint).Port,1,deadline.Token);
        await client.GetStream().ReadExactlyAsync(new byte[10],deadline.Token);using var server=await echo.AcceptTcpClientAsync(deadline.Token);
        var input=client.GetStream();var output=server.GetStream();
        client.Client.Shutdown(SocketShutdown.Send);Assert.Equal(0,await output.ReadAsync(new byte[1],deadline.Token));
        await output.WriteAsync(new byte[]{42},deadline.Token);server.Client.Shutdown(SocketShutdown.Send);
        var b=new byte[1];await input.ReadExactlyAsync(b,deadline.Token);Assert.Equal(42,b[0]);
        Assert.Equal(0,await input.ReadAsync(b,deadline.Token));await Wait(()=>!f.Capture().Any(r=>r.DestinationPrefix=="127.0.0.3/32"));
    }
    private static async Task<IPEndPoint> Associate(TcpClient client,CancellationToken ct)
    {
        await Request(client,"0.0.0.0",0,3,ct);var reply=new byte[10];await client.GetStream().ReadExactlyAsync(reply,ct);Assert.Equal(0,reply[1]);
        return new(IPAddress.Loopback,BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(8,2)));
    }
    private static byte[] Datagram(string host,int port,byte data=26)=>new byte[]{0,0,0}.Concat(Address(host,port)).Append(data).ToArray();
    [Fact] public async Task UdpLeaseCreatedBeforeSend()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();using var gateway=Gateway(f);gateway.Activate();
        using var echo=new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.3"),0));int port=((IPEndPoint)echo.Client.LocalEndPoint!).Port;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.BeforeCreate=async(_,ct)=>{entered.TrySetResult();await release.Task.WaitAsync(ct);};
        using var control=await Authenticate(gateway,deadline.Token);var relay=await Associate(control,deadline.Token);using var client=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        await client.SendAsync(Datagram("127.0.0.3",port),relay,deadline.Token);
        try{await entered.Task.WaitAsync(deadline.Token);Assert.Equal(0,echo.Available);}finally{release.TrySetResult();}
        var packet=await echo.ReceiveAsync(deadline.Token);Assert.Equal(new byte[]{26},packet.Buffer);Assert.Contains(f.Capture(),r=>r.DestinationPrefix=="127.0.0.3/32");
        await echo.SendAsync(packet.Buffer,packet.RemoteEndPoint,deadline.Token);Assert.Equal(26,(await client.ReceiveAsync(deadline.Token)).Buffer[^1]);
    }
    [Fact] public async Task UdpIdleExpiryReleasesReference()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();using var gateway=new OpenVpnDestinationGateway(f.Leases,()=>Environment.ProcessId){UdpIdleTimeout=TimeSpan.FromMilliseconds(120)};gateway.Activate();
        using var echo=new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.3"),0));int port=((IPEndPoint)echo.Client.LocalEndPoint!).Port;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));using var control=await Authenticate(gateway,deadline.Token);var relay=await Associate(control,deadline.Token);
        using var client=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));await client.SendAsync(Datagram("127.0.0.3",port),relay,deadline.Token);await echo.ReceiveAsync(deadline.Token);
        await Wait(()=>!f.Capture().Any(r=>r.DestinationPrefix=="127.0.0.3/32"));Assert.Equal(0,f.Leases.ReferenceCount(f.Leases.Capture(),IPAddress.Parse("127.0.0.3")));
        await client.SendAsync(Datagram("127.0.0.3",port),relay,deadline.Token);await echo.ReceiveAsync(deadline.Token);Assert.Contains(f.Capture(),r=>r.DestinationPrefix=="127.0.0.3/32");
    }
    [Fact] public async Task HostnameOutsidePushResolvesAndGetsExactLease()=>await Hostname(false,false);
    [Fact] public async Task CnameFinalAddressGetsExactLease()=>await Hostname(true,false);
    [Fact] public async Task MultipleARecordsDoNotCreateBroadRoute()=>await Hostname(true,true);
    private static async Task Hostname(bool cname,bool multiple)
    {
        await using var f=new Candidate26RouteFixture();await f.Start();int calls=0;
        using var gateway=new OpenVpnDestinationGateway(f.Leases,()=>Environment.ProcessId){Resolve=(host,link,current,ct)=>OpenVpnDestinationDns.ResolveAsync(host,link,current,ct,(_,query,_,_)=>{calls++;return Task.FromResult(Candidate26ResolverTests.Response(query,cname,multiple));})};gateway.Activate();
        using var echo=new TcpListener(IPAddress.Parse("127.0.0.3"),0);echo.Start();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var client=await Authenticate(gateway,deadline.Token);await Request(client,"corporate.example",((IPEndPoint)echo.LocalEndpoint).Port,1,deadline.Token);
        await client.GetStream().ReadExactlyAsync(new byte[10],deadline.Token);using var server=await echo.AcceptTcpClientAsync(deadline.Token);
        Assert.Equal(1,calls);Assert.Contains(f.Capture(),r=>r.DestinationPrefix=="127.0.0.3/32");Assert.All(f.Capture(),r=>Assert.EndsWith("/32",r.DestinationPrefix));
        Assert.DoesNotContain(f.Capture(),r=>r.DestinationPrefix=="127.0.0.5/32");
        // TLS/application data travels untouched across the resolved-IP proxy.
        byte[] payload=[22,3,3,0,4,1,2,3,4];await client.GetStream().WriteAsync(payload,deadline.Token);var got=new byte[payload.Length];await server.GetStream().ReadExactlyAsync(got,deadline.Token);Assert.Equal(payload,got);
    }
    [Fact] public async Task StaleDnsResultCannotCreateRoute()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gateway=new OpenVpnDestinationGateway(f.Leases,()=>Environment.ProcessId){Resolve=async(_,_,_,_)=>{entered.SetResult();await release.Task;return [new(IPAddress.Parse("127.0.0.3"),1)];}};gateway.Activate();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));using var client=await Authenticate(gateway,deadline.Token);await Request(client,"corporate.example",80,1,deadline.Token);await entered.Task.WaitAsync(deadline.Token);
        await f.Stop();release.SetResult();Assert.Equal(0,await client.GetStream().ReadAsync(new byte[1],deadline.Token));Assert.Empty(f.Capture());Assert.DoesNotContain(f.Commands,c=>c.StartsWith("New-NetRoute")&&c.Contains("127.0.0.3/32"));
    }
    [Fact] public async Task FailedRouteNeverDialsReachableEndpoint()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();f.FailCreate=true;using var gateway=Gateway(f);gateway.Activate();
        using var echo=new TcpListener(IPAddress.Parse("127.0.0.3"),0);echo.Start();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var client=await Authenticate(gateway,deadline.Token);await Request(client,"127.0.0.3",((IPEndPoint)echo.LocalEndpoint).Port,1,deadline.Token);
        Assert.Equal(0,await client.GetStream().ReadAsync(new byte[1],deadline.Token));Assert.False(echo.Pending());
    }
    [Fact] public async Task ExplicitCredentialCannotAuthenticateAsOwned()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();using var gateway=Gateway(f);gateway.Activate();using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client=await Authenticate(gateway,ct.Token,true,gateway.Endpoint.ExplicitPassword);Assert.Equal(0,await client.GetStream().ReadAsync(new byte[1],ct.Token));
    }
    [Fact] public async Task OwnedHostnameRejectedBeforeCorporateDns()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();int queries=0;
        using var gateway=new OpenVpnDestinationGateway(f.Leases,()=>Environment.ProcessId){Resolve=(_,_,_,_)=>{queries++;throw new Exception("Unauthorized DNS");}};gateway.Activate();
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(5));using var client=await Authenticate(gateway,ct.Token,true);await Request(client,"corporate.example",80,1,ct.Token);
        Assert.Equal(0,await client.GetStream().ReadAsync(new byte[1],ct.Token));Assert.Equal(0,queries);
    }
    [Fact] public async Task UdpHostnameOutsidePushUsesCorporateResolver()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();int queries=0;
        using var gateway=new OpenVpnDestinationGateway(f.Leases,()=>Environment.ProcessId){Resolve=(host,link,current,ct)=>OpenVpnDestinationDns.ResolveAsync(host,link,current,ct,(_,q,_,_)=>{queries++;return Task.FromResult(Candidate26ResolverTests.Response(q,true));})};gateway.Activate();
        using var echo=new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.3"),0));using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var control=await Authenticate(gateway,deadline.Token);var relay=await Associate(control,deadline.Token);using var client=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        await client.SendAsync(Datagram("corporate.example",((IPEndPoint)echo.Client.LocalEndPoint!).Port),relay,deadline.Token);
        Assert.Equal(new byte[]{26},(await echo.ReceiveAsync(deadline.Token)).Buffer);Assert.Equal(1,queries);Assert.Contains(f.Capture(),r=>r.DestinationPrefix=="127.0.0.3/32");
    }
    [Fact] public async Task GenerationSwitchClosesUdpAndCannotSendThroughNewLink()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();using var gateway=Gateway(f);gateway.Activate();
        using var echo=new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.3"),0));using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var control=await Authenticate(gateway,deadline.Token);var stream=control.GetStream();var relay=await Associate(control,deadline.Token);using var client=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        var data=Datagram("127.0.0.3",((IPEndPoint)echo.Client.LocalEndPoint!).Port);await client.SendAsync(data,relay,deadline.Token);await echo.ReceiveAsync(deadline.Token);
        await f.Stop();await f.Start(f.Link with{Generation=2});Assert.Equal(0,await stream.ReadAsync(new byte[1],deadline.Token));
        await client.SendAsync(data,relay,deadline.Token);using var noLeak=new CancellationTokenSource(150);await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await echo.ReceiveAsync(noLeak.Token));
        Assert.DoesNotContain(f.Capture(),r=>r.DestinationPrefix=="127.0.0.3/32");
    }
    [Fact] public async Task SameAddressTcpAndUdpHoldIndependentReferences()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();using var gateway=Gateway(f);gateway.Activate();
        using var echo=new TcpListener(IPAddress.Parse("127.0.0.3"),0);echo.Start();using var udpEcho=new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.3"),0));using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var tcp=await Authenticate(gateway,ct.Token);await Request(tcp,"127.0.0.3",((IPEndPoint)echo.LocalEndpoint).Port,1,ct.Token);await tcp.GetStream().ReadExactlyAsync(new byte[10],ct.Token);using var server=await echo.AcceptTcpClientAsync(ct.Token);
        using var control=await Authenticate(gateway,ct.Token);var relay=await Associate(control,ct.Token);using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        await udp.SendAsync(Datagram("127.0.0.3",((IPEndPoint)udpEcho.Client.LocalEndPoint!).Port),relay,ct.Token);await udpEcho.ReceiveAsync(ct.Token);
        var gen=f.Leases.Capture();var address=IPAddress.Parse("127.0.0.3");await Wait(()=>f.Leases.ReferenceCount(gen,address)==2);
        control.Dispose();await Wait(()=>f.Leases.ReferenceCount(gen,address)==1);Assert.Contains(f.Capture(),r=>r.DestinationPrefix=="127.0.0.3/32");
        tcp.Dispose();server.Dispose();await Wait(()=>f.Leases.ReferenceCount(gen,address)==0);
    }
    [Fact] public async Task Udp256SimultaneousDestinationsAndMoreThan300SequentialWork()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();
        using var gateway=new OpenVpnDestinationGateway(f.Leases,()=>Environment.ProcessId){UdpIdleTimeout=TimeSpan.FromSeconds(3)};gateway.Activate();
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(30));using var control=await Authenticate(gateway,ct.Token);var relay=await Associate(control,ct.Token);
        using var client=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));var servers=new List<UdpClient>();
        try
        {
            for(int i=0;i<256;i++)
            {
                var server=new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.3"),0));servers.Add(server);
                await client.SendAsync(Datagram("127.0.0.3",((IPEndPoint)server.Client.LocalEndPoint!).Port),relay,ct.Token);
                var packet=await server.ReceiveAsync(ct.Token);await server.SendAsync(packet.Buffer,packet.RemoteEndPoint,ct.Token);Assert.Equal(26,(await client.ReceiveAsync(ct.Token)).Buffer[^1]);
            }
            Assert.Equal(256,f.Leases.ReferenceCount(f.Leases.Capture(),IPAddress.Parse("127.0.0.3")));
            await Wait(()=>f.Leases.ReferenceCount(f.Leases.Capture(),IPAddress.Parse("127.0.0.3"))==0);
            for(int i=256;i<310;i++)
            {
                using var server=new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.3"),0));
                await client.SendAsync(Datagram("127.0.0.3",((IPEndPoint)server.Client.LocalEndPoint!).Port),relay,ct.Token);var packet=await server.ReceiveAsync(ct.Token);
                await server.SendAsync(packet.Buffer,packet.RemoteEndPoint,ct.Token);Assert.Equal(26,(await client.ReceiveAsync(ct.Token)).Buffer[^1]);
            }
        }
        finally{foreach(var server in servers)server.Dispose();}
    }
    [Fact] public async Task UdpExpiryCleanupFailureClosesAssociationAndRetainsJournal()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();var logs=new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var gateway=new OpenVpnDestinationGateway(f.Leases,()=>Environment.ProcessId){UdpIdleTimeout=TimeSpan.FromMilliseconds(100),Diagnostic=logs.Enqueue};gateway.Activate();
        using var echo=new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.3"),0));using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var control=await Authenticate(gateway,ct.Token);var stream=control.GetStream();var relay=await Associate(control,ct.Token);using var client=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        f.FailRemove=true;await client.SendAsync(Datagram("127.0.0.3",((IPEndPoint)echo.Client.LocalEndPoint!).Port),relay,ct.Token);await echo.ReceiveAsync(ct.Token);
        Assert.Equal(0,await stream.ReadAsync(new byte[1],ct.Token));Assert.Contains(logs,s=>s.Contains("lease_cleanup_failed"));
        Assert.Contains("127.0.0.3/32",(await OpenVpnRouteJournal.ReadAsync(f.Journal))!.DestinationPrefixes);
        f.FailRemove=false;await f.Stop();Assert.Empty(f.Capture());
    }
}
