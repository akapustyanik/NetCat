using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;
namespace NetCat.Tests;
public sealed class Candidate21DiagnosticTests
{
    [Fact] public void TunnelDiagnosticDoesNotDependOnReusableFixedSourcePort()
    {
        var p=ProfileImporter.ParseLink("socks://127.0.0.1:19900");
        var config=SingBoxConfig.Build(new(),new("Ethernet",1,"192.168.1.10","192.168.1.1",[]),p,null,true,healthSourcePort:19901);
        var rules=config["route"]!["rules"]!.AsArray();
        var probe=Assert.Single(rules,r=>r?["source_ip_cidr"]?.ToJsonString().Contains("172.29.255.1/32")==true && r?["port"]?.ToString()=="443");
        Assert.Null(probe!["source_port"]);Assert.Equal("1.1.1.1/32",probe["ip_cidr"]![0]!.ToString());
        Assert.Contains("NetCat.exe",probe["process_name"]!.ToJsonString());
    }
    [Fact] public async Task ConcurrentDiagnosticsOwnSeparateSourceSockets()
    {
        using var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var destination=(System.Net.IPEndPoint)listener.LocalEndpoint;
        using var first=await SystemTunnelHealth.ConnectProbeAsync(System.Net.IPAddress.Loopback,destination,ct.Token);
        using var firstPeer=await listener.AcceptTcpClientAsync(ct.Token);
        using var second=await SystemTunnelHealth.ConnectProbeAsync(System.Net.IPAddress.Loopback,destination,ct.Token);
        using var secondPeer=await listener.AcceptTcpClientAsync(ct.Token);
        Assert.NotEqual(firstPeer.Client.RemoteEndPoint,secondPeer.Client.RemoteEndPoint);
        await first.WriteAsync(new byte[]{1},ct.Token);await second.WriteAsync(new byte[]{2},ct.Token);
        var bytes=new byte[1];await firstPeer.GetStream().ReadExactlyAsync(bytes,ct.Token);Assert.Equal(1,bytes[0]);
        await secondPeer.GetStream().ReadExactlyAsync(bytes,ct.Token);Assert.Equal(2,bytes[0]);
    }
    [Fact] public void UnavailableProbeDoesNotOverrideHealthyAuthoritativeState()
    {
        var policy=new TunDiagnosticPolicy(); for(int i=0;i<20;i++)policy.Observe(new(false,-1,"probe port unavailable"),true,1);
        Assert.DoesNotContain("восстанавливаю",policy.Detail(TunStructuralStatus.Healthy));
        Assert.Contains("восстанавливаю",policy.Detail(TunStructuralStatus.StructuralFailure));
    }
}
