using System.Net;
using System.Net.Sockets;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;
namespace NetCat.Tests;
public sealed class PortStartupTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task OccupiedInternalPortRetriesAndLeavesNoPartialCore(bool alwaysOccupied)
    {
        using var occupied=new TcpListener(IPAddress.Loopback,0);occupied.Start();var port=((IPEndPoint)occupied.LocalEndpoint).Port;int allocations=0;
        var started=new List<int>();var root=Path.Combine(RoutingTests.FindRoot(),"artifacts","ports-"+Guid.NewGuid().ToString("N"));
        using var router=new RouterService(Path.Combine(RoutingTests.FindRoot(),"bin"),root)
        {
            AllocatePort=()=>++allocations==1 || alwaysOccupied?port:OpenVpnService.FreePort(),
            StartProcessOverride=(host,exe,args)=>{host.Start(exe,args);started.Add(host.Id);}
        };
        var p=ProfileImporter.ParseLink("socks://192.0.2.1:1080");var settings=new AppSettings{Tun=false,Profiles=[p],MainProfileId=p.Id,SocksPort=OpenVpnService.FreePort()};
        if(alwaysOccupied){await Assert.ThrowsAsync<PortCollisionException>(()=>router.SetVpnAsync(settings,true));Assert.Equal(3,started.Count);Assert.False(router.VpnRunning);}
        else {await router.SetVpnAsync(settings,true);Assert.Equal(2,started.Count);Assert.NotEqual(port,router.LatencyPort);Assert.True(router.VpnRunning);}
        await router.StopAllAsync();foreach(var pid in started)
        {
            try {using var process=System.Diagnostics.Process.GetProcessById(pid);process.WaitForExit(3000);Assert.True(process.HasExited);}
            catch(ArgumentException) { }
        }
    }
    [Fact]
    public async Task OnlyCollisionsRetryAndDuplicateAllocatorIsBounded()
    {
        int attempts=0;await Assert.ThrowsAsync<InvalidDataException>(()=>PortStartup.RetryAsync<bool>(_=>{attempts++;throw new InvalidDataException("invalid config");},CancellationToken.None));Assert.Equal(1,attempts);
        int allocated=0;Assert.Throws<PortCollisionException>(()=>PortStartup.Distinct(()=>{allocated++;return 1000;},1000));Assert.Equal(8,allocated);
        Assert.False(PortStartup.IsCollision("access denied loading wintun"));Assert.True(PortStartup.IsCollision("bind: address already in use"));
    }
    [Fact]
    public async Task XrayBridgeRetriesWithFreshTcpUdpPort()
    {
        using var occupied=new TcpListener(IPAddress.Loopback,0);occupied.Start();var port=((IPEndPoint)occupied.LocalEndpoint).Port;int allocations=0;
        var root=Path.Combine(Path.GetTempPath(),"NetCat-XrayPort-"+Guid.NewGuid());
        using(var router=new RouterService(Path.Combine(RoutingTests.FindRoot(),"bin"),root){AllocateTcpUdpPort=()=>++allocations==1?port:OpenVpnService.FreeTcpUdpPort()})
        {
            var p=ProfileImporter.ParseLink("trojan://test@192.0.2.1:443?security=tls#port");var s=new AppSettings {Tun=false,Profiles=[p],MainProfileId=p.Id,SocksPort=OpenVpnService.FreePort()};
            await router.SetVpnAsync(s,true);Assert.True(router.VpnRunning);Assert.Equal(2,allocations);await router.StopAllAsync();
        }
        Directory.Delete(root,true);
    }
    [Fact]
    public async Task ProfileProbeBoundsForeignPortRetriesWithoutTouchingRuntime()
    {
        using var occupied=new TcpListener(IPAddress.Loopback,0);occupied.Start();var port=((IPEndPoint)occupied.LocalEndpoint).Port;int allocations=0;
        var root=Path.Combine(Path.GetTempPath(),"NetCat-ProbePort-"+Guid.NewGuid());
        using var router=new RouterService(Path.Combine(RoutingTests.FindRoot(),"bin"),root){AllocatePort=()=>{allocations++;return port;}};
        var result=await router.TestProfileAsync(ProfileImporter.ParseLink("socks://192.0.2.1:1080"),new(){Tun=false},CancellationToken.None);
        Assert.False(result.Success);Assert.Equal(3,allocations);Assert.False(router.Running);Assert.Null(router.ActiveProfileId);
        if(Directory.Exists(root))Directory.Delete(root,true);
    }
    [Fact]
    public async Task OpenVpnManagementCollisionIsBoundedBeforeCreatingAdapter()
    {
        using var occupied=new TcpListener(IPAddress.Loopback,0);occupied.Start();var port=((IPEndPoint)occupied.LocalEndpoint).Port;int allocations=0;
        var root=Path.Combine(Path.GetTempPath(),"NetCat-ManagementPort-"+Guid.NewGuid());
        using var service=new OpenVpnService("unused-no-driver-or-executable",root){AllocateManagementPort=()=>{allocations++;return port;}};
        await Assert.ThrowsAsync<PortCollisionException>(()=>service.StartAsync(new Profile{Protocol="openvpn",OpenVpnConfig="client\ndev tun\nremote 192.0.2.1 1194\n"},"",CancellationToken.None));
        Assert.Equal(3,allocations);Assert.False(service.Running);Assert.Null(service.ActiveProfileId);Assert.Null(service.Link);
        Assert.False(File.Exists(Path.Combine(root,"management.pass")));Directory.Delete(root,true);
    }
}
