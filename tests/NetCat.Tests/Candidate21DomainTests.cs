using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Network;
using Xunit;
namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed partial class Candidate21DomainTests
{
    private sealed class Resolver : IDisposable
    {
        private readonly UdpClient socket;private readonly CancellationTokenSource stop=new();public int Queries;
        public int Port=>((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        public Resolver(string ip,byte answer,int port=0){socket=new(new IPEndPoint(IPAddress.Parse(ip),port));_=Run(answer);}
        private async Task Run(byte answer){try{while(!stop.IsCancellationRequested){var q=await socket.ReceiveAsync(stop.Token);Interlocked.Increment(ref Queries);
            var b=q.Buffer.ToList();b[2]=0x81;b[3]=0x80;b[6]=0;b[7]=1;b.AddRange(new byte[]{0xc0,12,0,1,0,1,0,0,0,0,0,4,127,0,0,answer});await socket.SendAsync(b.ToArray(),q.RemoteEndPoint,stop.Token);}}
            catch(Exception e)when(e is SocketException or OperationCanceledException or ObjectDisposedException){}}
        public void Dispose(){stop.Cancel();socket.Dispose();}
    }
    private sealed class Echo : IDisposable
    {
        private readonly TcpListener listener;private readonly CancellationTokenSource stop=new();public int Received;
        public int Port=>((IPEndPoint)listener.LocalEndpoint).Port;
        public Echo(string ip,int port=0){listener=new(IPAddress.Parse(ip),port);listener.Start();_=Run();}
        private async Task Run(){try{while(!stop.IsCancellationRequested){using var c=await listener.AcceptTcpClientAsync(stop.Token);Interlocked.Increment(ref Received);var b=new byte[1];await c.GetStream().ReadExactlyAsync(b,stop.Token);await c.GetStream().WriteAsync(b,stop.Token);}}
            catch(Exception e)when(e is IOException or SocketException or OperationCanceledException or ObjectDisposedException){}}
        public void Dispose(){stop.Cancel();listener.Stop();}
    }
    private static async Task Query(int port,string name)
    {
        var b=new List<byte>{0x21,1,1,0,0,1,0,0,0,0,0,0};foreach(var label in name.Split('.')){b.Add((byte)label.Length);b.AddRange(Encoding.ASCII.GetBytes(label));}b.AddRange(new byte[]{0,0,1,0,1});
        using var socket=new UdpClient();using var ct=new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await socket.SendAsync(b.ToArray(),new IPEndPoint(IPAddress.Loopback,port),ct.Token);
        try{await socket.ReceiveAsync(ct.Token);}catch(OperationCanceledException){}
    }
    private sealed class VpnEndpoint : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Targets = new();
        public int Connections;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public VpnEndpoint() { listener.Start(); _ = Run(); }
        private async Task Run()
        {
            try { while (!stop.IsCancellationRequested) { var c = await listener.AcceptTcpClientAsync(stop.Token); Interlocked.Increment(ref Connections); _ = Serve(c); } }
            catch (Exception e) when (e is SocketException or OperationCanceledException) { }
        }
        private async Task Serve(TcpClient c)
        {
            using(c) try {
                var s=c.GetStream(); var hello=new byte[3]; await s.ReadExactlyAsync(hello,stop.Token); await s.WriteAsync(new byte[]{5,0},stop.Token);
                var h=new byte[4]; await s.ReadExactlyAsync(h,stop.Token);
                int n=h[3]==1?4:h[3]==4?16:s.ReadByte(); var a=new byte[n]; await s.ReadExactlyAsync(a,stop.Token); var p=new byte[2]; await s.ReadExactlyAsync(p,stop.Token);
                if(h[1]==3)
                {
                    using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));int port=((IPEndPoint)udp.Client.LocalEndPoint!).Port;
                    await s.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,(byte)(port>>8),(byte)port},stop.Token);
                    using var done=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    async Task Close(){try{await s.ReadAsync(new byte[1],done.Token);}finally{done.Cancel();}}
                    var closed=Close();
                    try{while(!done.IsCancellationRequested){var packet=await udp.ReceiveAsync(done.Token);var b=packet.Buffer;
                        Targets.Enqueue(b[3]==3?Encoding.ASCII.GetString(b,5,b[4]):new IPAddress(b.AsSpan(4,b[3]==1?4:16)).ToString());
                        await udp.SendAsync(b,packet.RemoteEndPoint,done.Token);}}
                    finally{done.Cancel();try{await closed;}catch(OperationCanceledException){}}
                    return;
                }
                Targets.Enqueue(h[3]==3?Encoding.ASCII.GetString(a):new IPAddress(a).ToString());
                await s.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,0},stop.Token);
                var buffer=new byte[1024]; int count; while((count=await s.ReadAsync(buffer,stop.Token))>0) await s.WriteAsync(buffer.AsMemory(0,count),stop.Token);
            } catch(Exception e) when(e is IOException or SocketException or OperationCanceledException) { }
        }
        public void Dispose(){stop.Cancel();listener.Stop();}
    }
    private static async Task<bool> Request(int port,string domain,int destinationPort=443)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2)); using var c=new TcpClient();
        try {
            await c.ConnectAsync(IPAddress.Loopback,port,timeout.Token);var s=c.GetStream();
            await s.WriteAsync(new byte[]{5,1,0},timeout.Token);await s.ReadExactlyAsync(new byte[2],timeout.Token);
            await s.WriteAsync(new byte[]{5,1,0,3,(byte)domain.Length}.Concat(Encoding.ASCII.GetBytes(domain)).Concat(new byte[]{(byte)(destinationPort>>8),(byte)destinationPort}).ToArray(),timeout.Token);
            var reply=new byte[10];await s.ReadExactlyAsync(reply,timeout.Token);if(reply[1]!=0)return false;
            await s.WriteAsync(new byte[]{42},timeout.Token);var echo=new byte[1];await s.ReadExactlyAsync(echo,timeout.Token);return echo[0]==42;
        }catch(Exception e)when(e is IOException or SocketException or OperationCanceledException){return false;}
    }
    [Theory][InlineData(RoutingMode.Global)][InlineData(RoutingMode.Rules)]
    public async Task NewCorporateDomainNeverReachesMainVpnOutboundBeforeRuleReload(RoutingMode mode)
    {
        using var endpoint=new VpnEndpoint(); using var f=new Candidate21PortTests.Fixture();
        using var ordinary=new Resolver("127.0.0.3",4,53);using var corporate=new Resolver("127.0.0.1",2);
        using var corpEcho=new Echo("127.0.0.2");using var physicalEcho=new Echo("127.0.0.4",corpEcho.Port);
        var corporateProfile=new Profile{Protocol="openvpn",AllowPublicPushedRoutes=true,LearnedRoutes=["127.0.0.2/32"]};
        var p=f.Settings.Profiles.Single(); p.Port=endpoint.Port;
        p.OutboundJson=new JsonObject{["type"]="socks",["server"]="127.0.0.1",["server_port"]=endpoint.Port}.ToJsonString();
        f.Settings.Mode=mode;
        f.Settings.Profiles.Add(corporateProfile);f.Settings.OpenVpnProfileId=corporateProfile.Id;
        f.Settings.DirectDns="127.0.0.3";int dnsPort=OpenVpnService.FreeTcpUdpPort();
        f.Configure=c=>c["inbounds"]!.AsArray().Add(new JsonObject{["type"]="direct",["tag"]="dns-probe",["listen"]="127.0.0.1",["listen_port"]=dnsPort,["network"]="udp",["override_address"]="1.1.1.1",["override_port"]=53});
        // An automatic service default exercises VPN selection even in Rules mode.
        f.Settings.YouTube=ServiceRoute.Vpn;
        await f.Start();
        Assert.True(await Request(f.Router.ListenPort,mode==RoutingMode.Global?"public.example.test":"www.youtube.com")); // reachable control; not a dead VPN fixture
        int before=endpoint.Connections;int targets=endpoint.Targets.Count;
        string domain=mode==RoutingMode.Global?"fresh.corp.test":"new.youtube.com";
        f.Settings.OpenVpnDomains=mode==RoutingMode.Global?"corp.test":"youtube.com";
        // Deliberately withhold native publication: running rule-set is still empty.
        f.Router.PrepareDomainOwnership(f.Settings);
        await Task.WhenAll(Enumerable.Range(0,12).Select(async _=>{await Query(dnsPort,domain);Assert.False(await Request(f.Router.ListenPort,domain,corpEcho.Port));}));
        Assert.Equal(before,endpoint.Connections);Assert.Equal(targets,endpoint.Targets.Count);
        Assert.DoesNotContain(domain,endpoint.Targets);Assert.Equal(0,ordinary.Queries);Assert.Equal(0,physicalEcho.Received);Assert.Equal(0,corpEcho.Received);
        var identity=f.Router.CaptureRuntime();long revision=f.Router.SessionRevision;
        var link=new OpenVpnLink(f.Physical.Name,f.Physical.Index,"127.0.0.1","127.0.0.1","127.0.0.1",corporateProfile.LearnedRoutes){ProfileId=corporateProfile.Id,Generation=1,DnsPort=corporate.Port};
        await Candidate26NativeRoutes.Prepare(f.Router,f.Root,link);
        await f.Router.ApplyOpenVpnOverlayAsync(f.Settings,link,f.Timeout.Token);
        bool connected=false;for(int i=0;i<10&&!connected;i++){connected=await Request(f.Router.ListenPort,domain,corpEcho.Port);if(!connected)await Task.Delay(100,f.Timeout.Token);}
        Assert.True(connected);Assert.True(corpEcho.Received>0);Assert.True(corporate.Queries>0);
        Assert.Equal(0,ordinary.Queries);Assert.Equal(0,physicalEcho.Received);Assert.Equal(before,endpoint.Connections);Assert.Equal(identity,f.Router.CaptureRuntime());Assert.Equal(revision,f.Router.SessionRevision);
        if(Environment.GetEnvironmentVariable("NETCAT_ACCEPTANCE_EVIDENCE") is {} evidence)
            await File.WriteAllTextAsync(Path.Combine(evidence,$"domain-{mode}.json"),System.Text.Json.JsonSerializer.Serialize(new{Mode=mode.ToString(),OrdinaryDns=ordinary.Queries,PhysicalConnections=physicalEcho.Received,NewMainVpnConnections=endpoint.Connections-before,CorporateConnections=corpEcho.Received,CorporateDns=corporate.Queries,SessionRevisionBefore=revision,SessionRevisionAfter=f.Router.SessionRevision,Scope="controlled native loopback; real TUN/OpenVPN not exercised"}));
    }
    [Fact] public async Task ExplicitUserVpnStillOverridesCorporateDomain()
    {
        using var endpoint=new VpnEndpoint();using var f=new Candidate21PortTests.Fixture();var p=f.Settings.Profiles.Single();p.Port=endpoint.Port;
        p.OutboundJson=new JsonObject{["type"]="socks",["server"]="127.0.0.1",["server_port"]=endpoint.Port}.ToJsonString();
        f.Settings.Mode=RoutingMode.Global;f.Settings.OpenVpnDomains="corp.test";
        f.Settings.Rules.Add(new(){Kind=RuleKind.Domain,Value="corp.test",Target=RouteTarget.Vpn,Enabled=true});
        await f.Start();Assert.True(await Request(f.Router.ListenPort,"explicit.corp.test"));Assert.Contains("explicit.corp.test",endpoint.Targets);
    }
    [Fact] public async Task AutomaticVpnUdpWorksAndNewCorporateDomainIsNotForwarded()
    {
        using var endpoint=new VpnEndpoint();using var f=new Candidate21PortTests.Fixture();var p=f.Settings.Profiles.Single();p.Port=endpoint.Port;
        p.OutboundJson=new JsonObject{["type"]="socks",["server"]="127.0.0.1",["server_port"]=endpoint.Port}.ToJsonString();f.Settings.Mode=RoutingMode.Global;
        await f.Start();using var control=new TcpClient();await control.ConnectAsync(IPAddress.Loopback,f.Router.ListenPort,f.Timeout.Token);
        var s=control.GetStream();await s.WriteAsync(new byte[]{5,1,0},f.Timeout.Token);await s.ReadExactlyAsync(new byte[2],f.Timeout.Token);
        await s.WriteAsync(new byte[]{5,3,0,1,127,0,0,1,0,0},f.Timeout.Token);var reply=new byte[10];await s.ReadExactlyAsync(reply,f.Timeout.Token);Assert.Equal(0,reply[1]);
        using var udp=new UdpClient();udp.Connect(IPAddress.Loopback,reply[8]*256+reply[9]);
        byte[] Packet(string name)=>new byte[]{0,0,0,3,(byte)name.Length}.Concat(Encoding.ASCII.GetBytes(name)).Concat(new byte[]{0,80,42}).ToArray();
        await udp.SendAsync(Packet("public.example.test"),f.Timeout.Token);using(var wait=new CancellationTokenSource(3000)){var response=await udp.ReceiveAsync(wait.Token);Assert.Equal(42,response.Buffer[^1]);}
        Assert.Contains("public.example.test",endpoint.Targets);
        f.Settings.OpenVpnDomains="corp.test";f.Router.PrepareDomainOwnership(f.Settings);int before=endpoint.Targets.Count;
        await udp.SendAsync(Packet("new.corp.test"),f.Timeout.Token);
        using(var wait=new CancellationTokenSource(500)){await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>{await udp.ReceiveAsync(wait.Token);});}
        Assert.Equal(before,endpoint.Targets.Count);Assert.DoesNotContain("new.corp.test",endpoint.Targets);
    }
}
