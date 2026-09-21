using System.Net;
using System.Net.Sockets;
using System.Text;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;
public sealed class ProfileSwitchTests
{
    [Fact]
    public async Task DisabledAutoSwitchDoesNotCommitSuccessfulPreflight()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20)); var ct=deadline.Token;
        var physical=PhysicalNetwork.Capture("");
        using var server=new TcpListener(IPAddress.Parse(physical.Address),0); server.Start();
        var candidate=ProfileImporter.ParseLink($"socks://{physical.Address}:{((IPEndPoint)server.LocalEndpoint).Port}");
        var old=ProfileImporter.ParseLink("socks://127.0.0.1:19999");
        var serving=Serve(server,204,ct);
        using var router=new RouterService(Path.Combine(RoutingTests.FindRoot(),"bin"),Path.Combine(RoutingTests.FindRoot(),"artifacts","preflight-disabled-"+Guid.NewGuid().ToString("N")));
        var s=new AppSettings {Tun=false,Profiles=[old,candidate],MainProfileId=old.Id,SocksPort=OpenVpnService.FreePort(),TestUrl="http://switch.example.invalid/probe"};
        await router.SetVpnAsync(s,true,ct); var port=router.LatencyPort;
        s.MainProfileId=candidate.Id;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>router.SwitchProfileAsync(s,ct,canCommit:()=>false));
        await serving;
        Assert.Equal(old.Id,router.ActiveProfileId); Assert.Equal(port,router.LatencyPort);
    }
    [Fact]
    public async Task FailedPreflightLeavesCurrentConnectionAvailable()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20)); var ct=deadline.Token;
        var physical=PhysicalNetwork.Capture("");
        using var oldServer=new TcpListener(IPAddress.Parse(physical.Address),0); oldServer.Start();
        using var newServer=new TcpListener(IPAddress.Parse(physical.Address),0); newServer.Start();
        Profile P(TcpListener server)=>ProfileImporter.ParseLink($"socks://{physical.Address}:{((IPEndPoint)server.LocalEndpoint).Port}");
        var old=P(oldServer); var candidate=P(newServer);
        var oldServing=Serve(oldServer,204,ct); var candidateServing=Serve(newServer,503,ct);
        using var router=new RouterService(Path.Combine(RoutingTests.FindRoot(),"bin"),Path.Combine(RoutingTests.FindRoot(),"artifacts","preflight-"+Guid.NewGuid().ToString("N")));
        var s=new AppSettings {Tun=false,Profiles=[old,candidate],MainProfileId=old.Id,SocksPort=OpenVpnService.FreePort(),TestUrl="http://switch.example.invalid/probe"};
        await router.SetVpnAsync(s,true,ct); var port=router.LatencyPort;
        s.MainProfileId=candidate.Id;
        await Assert.ThrowsAsync<InvalidDataException>(()=>router.SwitchProfileAsync(s,ct)); await candidateServing;
        Assert.Equal(old.Id,router.ActiveProfileId); Assert.Equal(port,router.LatencyPort);
        Assert.True((await ConnectionLatency.MeasureAsync(port,s.TestUrl,ct)).Success); await oldServing;
    }
    [Fact]
    public async Task ConnectedProfileChangesTrafficAndFailedSwitchKeepsPreviousCore()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25)); var ct = deadline.Token;
        var physical = PhysicalNetwork.Capture("");
        using var first = new TcpListener(IPAddress.Parse(physical.Address),0); first.Start();
        using var second = new TcpListener(IPAddress.Parse(physical.Address),0); second.Start();
        Profile Profile(TcpListener server) => ProfileImporter.ParseLink($"socks://{physical.Address}:{((IPEndPoint)server.LocalEndpoint).Port}#Test");
        var p1=Profile(first); var p2=Profile(second);
        var serving1=Serve(first,204,ct); var serving2=Serve(second,503,ct);
        using var router=new RouterService(Path.Combine(RoutingTests.FindRoot(),"bin"),Path.Combine(RoutingTests.FindRoot(),"artifacts","switch-"+Guid.NewGuid().ToString("N")));
        var s=new AppSettings {Tun=false,SocksPort=OpenVpnService.FreePort(),Profiles=[p1,p2],MainProfileId=p1.Id,Mode=RoutingMode.Global};
        await router.SetVpnAsync(s,true,ct);
        Assert.True((await ConnectionLatency.MeasureAsync(router.LatencyPort,"http://switch.example.invalid/probe",ct)).Success); await serving1;
        s.MainProfileId=p2.Id; await router.SwitchProfileAsync(s,ct,preflight:false);
        Assert.True(router.VpnRequested); Assert.Equal(p2.Id,router.ActiveProfileId);
        var result=await ConnectionLatency.MeasureAsync(router.LatencyPort,"http://switch.example.invalid/probe",ct); await serving2;
        Assert.Equal("HTTP 503",result.Error);
        var invalid=new Profile {Host="invalid.example",OutboundJson="{\"type\":\"invalid-protocol\"}"}; s.Profiles.Add(invalid); s.MainProfileId=invalid.Id;
        await Assert.ThrowsAsync<InvalidDataException>(()=>router.SwitchProfileAsync(s,ct,preflight:false));
        Assert.True(router.VpnRunning); Assert.Equal(p2.Id,router.ActiveProfileId);
        await router.SetVpnAsync(s,false,ct); s.MainProfileId=p1.Id; await router.SwitchProfileAsync(s,ct,preflight:false);
        Assert.False(router.VpnRunning);
    }
    private static async Task Serve(TcpListener listener,int status,CancellationToken ct)
    {
        using var connection=await listener.AcceptTcpClientAsync(ct); using var stream=connection.GetStream();
        var greeting=new byte[2]; await stream.ReadExactlyAsync(greeting,ct); await stream.ReadExactlyAsync(new byte[greeting[1]],ct);
        await stream.WriteAsync(new byte[]{5,0},ct);
        var command=new byte[5]; await stream.ReadExactlyAsync(command,ct); Assert.Equal(3,command[3]);
        await stream.ReadExactlyAsync(new byte[command[4]+2],ct);
        await stream.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,80},ct);
        using var reader=new StreamReader(stream,Encoding.ASCII,leaveOpen:true);
        Assert.StartsWith("GET /probe",await reader.ReadLineAsync(ct));
        while(!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))) { }
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),ct);
    }
}
