using System.Net;
using System.Net.Sockets;
using System.Text;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class RuntimeTests
{
    [Fact]
    public async Task RealSingBoxForwardsHttpThroughSelectedSocksProfile()
    {
        var network = PhysicalNetwork.Capture("");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = deadline.Token;
        using var server = new TcpListener(IPAddress.Parse(network.Address), 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serving = Task.Run(async () =>
        {
            using var connection = await server.AcceptTcpClientAsync(ct);
            using var stream = connection.GetStream();
            var header = new byte[2]; await stream.ReadExactlyAsync(header, ct);
            Assert.Equal(5, header[0]);
            var methods = new byte[header[1]]; await stream.ReadExactlyAsync(methods, ct);
            await stream.WriteAsync(new byte[] { 5, 0 }, ct);
            var command = new byte[4]; await stream.ReadExactlyAsync(command, ct);
            Assert.Equal(1, command[1]); Assert.Equal(3, command[3]);
            var length = new byte[1]; await stream.ReadExactlyAsync(length, ct);
            var host = new byte[length[0]]; await stream.ReadExactlyAsync(host, ct);
            var destinationPort = new byte[2]; await stream.ReadExactlyAsync(destinationPort, ct);
            received.TrySetResult(Encoding.ASCII.GetString(host));
            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 }, ct);
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var request = await reader.ReadLineAsync(ct);
            Assert.StartsWith("GET /generate_204", request);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), ct);
        }, ct);
        var root = RoutingTests.FindRoot();
        using var router = new RouterService(Path.Combine(root, "bin"), Path.Combine(root, "artifacts", "runtime-test"));
        var profile = ProfileImporter.ParseLink($"socks://{network.Address}:{port}#Local-test");
        var settings = new AppSettings { TestUrl = "http://probe.example.invalid/generate_204", TestTimeoutSeconds = 10, PhysicalInterface = network.Name };
        var result = await router.TestProfileAsync(profile, settings, ct);
        Assert.True(result.Success, result.Error);
        Assert.Equal("probe.example.invalid", await received.Task.WaitAsync(ct));
        await serving;
        Assert.False(router.Running);
        Assert.False(router.VpnRequested);
        Assert.False(router.OpenVpn.Running);
    }

    [Fact]
    public async Task OfficialXrayAcceptsVlessTlsConfiguration()
    {
        var root = RoutingTests.FindRoot();
        var profile = ProfileImporter.ParseLink("vless://00000000-0000-4000-8000-000000000001@vpn.example.com:443?security=tls&sni=vpn.example.com#Test");
        var config = XrayConfig.Build(profile, 19089, "192.168.20.10");
        var directory = Path.Combine(root, "artifacts", "validation"); Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "xray-test.json"); await File.WriteAllTextAsync(file, config.ToJsonString());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await ProcessHost.RunAsync(Path.Combine(root, "bin", "xray", "xray.exe"), ["run", "-test", "-c", file], timeout.Token);
        Assert.True(result.Code == 0, result.Output);
    }

    [Fact]
    public async Task BundledOpenVpnSupportsWintun()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await ProcessHost.RunAsync(Path.Combine(RoutingTests.FindRoot(), "bin", "openvpn", "openvpn.exe"), ["--help"], timeout.Token);
        Assert.Contains("wintun", result.Output, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task ForeignListenerNeverCountsAsNewCoreReadiness()
    {
        var root=RoutingTests.FindRoot(); var folder=Path.Combine(root,"artifacts","collision-test"); Directory.CreateDirectory(folder);
        using var listener=new TcpListener(IPAddress.Loopback,0); listener.Start(); var port=((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.Equal(Environment.ProcessId,LocalListener.Owner(port));
        var path=Path.Combine(folder,"config.json");
        await File.WriteAllTextAsync(path,$$"""{"inbounds":[{"type":"mixed","listen":"127.0.0.1","listen_port":{{port}}}],"outbounds":[{"type":"direct"}]}""");
        using var process=new ProcessHost(); process.Start(Path.Combine(root,"bin","sing-box","sing-box.exe"),["run","-c",path]);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<PortCollisionException>(()=>RouterService.WaitPortAsync(port,process,deadline.Token));
    }
    [Fact]
    public async Task XhttpAutomaticallyUsesRealXrayAndPassesItsValidator()
    {
        var profile=ProfileImporter.ParseLink("vless://00000000-0000-4000-8000-000000000001@vpn.example.com:443?type=xhttp&security=tls&sni=vpn.example.com&path=%2Ftransport&host=cdn.example.com&mode=packet-up&extra=%7B%22noGRPCHeader%22%3Atrue%7D#Test");
        Assert.Equal("Xray",profile.Core); Assert.Contains("packet-up",profile.OutboundJson); Assert.Contains("noGRPCHeader",profile.OutboundJson);
        var config=XrayConfig.Build(profile,19089,"192.168.20.10"); var root=RoutingTests.FindRoot(); var path=Path.Combine(root,"artifacts","validation","xhttp.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path,config.ToJsonString()); using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result=await ProcessHost.RunAsync(Path.Combine(root,"bin","xray","xray.exe"),["run","-test","-c",path],timeout.Token);
        Assert.True(result.Code==0,result.Output);
        var reimport=ProfileImporter.Parse(config.ToJsonString()); Assert.Empty(reimport.Errors); Assert.Single(reimport.Profiles); Assert.Equal("Xray",reimport.Profiles[0].Core);
    }
    [Fact]
    public async Task TelegramCliStartsAndStopsWithoutAnyTrayApplication()
    {
        var root=RoutingTests.FindRoot(); var port=OpenVpnService.FreePort();
        using var service=new TelegramService(Path.Combine(root,"bin"),Path.Combine(root,"artifacts","telegram-test"));
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await service.StartAsync(new AppSettings { TelegramWsPort=port },deadline.Token);
        Assert.True(service.Running); Assert.Contains("port="+port+"&",service.Link); Assert.NotEqual(0,LocalListener.Owner(port));
        await service.StopAsync(); Assert.False(service.Running); Assert.Equal(0,LocalListener.Owner(port));
    }
    [Fact]
    public async Task SlowBackgroundProbeDoesNotHoldConnectionControls()
    {
        var network=PhysicalNetwork.Capture(""); using var server=new TcpListener(IPAddress.Parse(network.Address),0); server.Start();
        var port=((IPEndPoint)server.LocalEndpoint).Port; var profile=ProfileImporter.ParseLink($"socks://{network.Address}:{port}#Slow-test");
        var root=RoutingTests.FindRoot(); using var router=new RouterService(Path.Combine(root,"bin"),Path.Combine(root,"artifacts","concurrent-test"));
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var probe=router.TestProfileAsync(profile,new AppSettings { TestUrl="http://probe.example.invalid/",TestTimeoutSeconds=10 },cancel.Token);
        using var connection=await server.AcceptTcpClientAsync(cancel.Token);
        // The server deliberately leaves the SOCKS handshake pending while a user stops VPN.
        await router.SetVpnAsync(new AppSettings(),false).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(router.VpnRequested); Assert.False(probe.IsCompleted);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>probe);
    }
}
