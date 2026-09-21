using System.Net;
using System.Net.Sockets;
using System.Text;
using NetCat.Network;
using NetCat.Core;
using NetCat.Engine;
using Xunit;

namespace NetCat.Tests;
public sealed class ConnectionLatencyTests
{
    [Theory]
    [InlineData(204, true)]
    [InlineData(503, false)]
    [InlineData(204, true, RoutingMode.SelectiveVpn)]
    [InlineData(204, true, RoutingMode.SelectiveDirect)]
    [InlineData(204, true, RoutingMode.Rules)]
    [InlineData(204, true, RoutingMode.Global)]
    public async Task MeasuresResponseThroughSocksWithoutLocalDns(int status, bool success, RoutingMode? mode = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        var physical = PhysicalNetwork.Capture("");
        using var listener = new TcpListener(mode.HasValue ? IPAddress.Parse(physical.Address) : IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(ct);
            using var stream = connection.GetStream();
            var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, ct);
            var methods = new byte[greeting[1]]; await stream.ReadExactlyAsync(methods, ct);
            await stream.WriteAsync(new byte[] { 5, 0 }, ct);
            var command = new byte[5]; await stream.ReadExactlyAsync(command, ct);
            Assert.Equal(3, command[3]);
            var host = new byte[command[4]]; await stream.ReadExactlyAsync(host, ct);
            Assert.Equal("latency.example.invalid", Encoding.ASCII.GetString(host));
            await stream.ReadExactlyAsync(new byte[2], ct);
            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 }, ct);
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen:true);
            Assert.StartsWith("GET /probe", await reader.ReadLineAsync(ct));
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))) { }
            await Task.Delay(100, ct);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), ct);
        }, ct);
        var root = RoutingTests.FindRoot();
        using var router = new RouterService(Path.Combine(root,"bin"),Path.Combine(root,"artifacts","latency-test-"+Guid.NewGuid().ToString("N")));
        if (mode.HasValue)
        {
            var profile = ProfileImporter.ParseLink($"socks://{physical.Address}:{port}#Latency-test");
            var settings = new AppSettings { Tun=false, SocksPort=OpenVpnService.FreePort(), PhysicalInterface=physical.Name, Profiles=[profile], MainProfileId=profile.Id, Mode=mode.Value,
                Rules=[new RoutingRule { Kind=RuleKind.Process, Value=Path.GetFileName(Environment.ProcessPath)!, Target=RouteTarget.Direct }, new RoutingRule { Kind=RuleKind.Domain, Value="latency.example.invalid", Target=RouteTarget.Block }] };
            await router.SetVpnAsync(settings,true,ct);
            Assert.NotEqual(0,router.LatencyPort); Assert.NotEqual(router.ListenPort,router.LatencyPort);
            port=router.LatencyPort;
        }
        var result = await ConnectionLatency.MeasureAsync(port, "http://latency.example.invalid/probe", ct);
        await serving;
        Assert.Equal(success, result.Success);
        if (success) Assert.InRange(result.Milliseconds, 90, 3999);
        else Assert.Equal("HTTP 503", result.Error);
        await router.StopAllAsync(); Assert.Equal(0,router.LatencyPort);
    }

    [Fact]
    public async Task CancellationStopsOutstandingMeasurement()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource();
        var measuring = ConnectionLatency.MeasureAsync(port, "http://latency.example.invalid/probe", cancellation.Token);
        using var connection = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => measuring);
    }
}
