using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed class OpenVpnIndependentNativeTests
{
    [Fact]
    public async Task AbsentOwnedRouteCleanupIsIdempotentWithRealWindowsPowerShell()
    {
        var root=Path.Combine(Path.GetTempPath(),"NetCat-absent-route-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var path=Path.Combine(root,"journal.json");
        try {
            // Impossible interface index: no host route can match this command.
            var journal=new OpenVpnRouteJournal(4,null,1,int.MaxValue,"192.0.2.1",["192.0.2.254/32"],false,DateTimeOffset.UtcNow);
            await journal.SaveAsync(path,CancellationToken.None);
            await OpenVpnRouteJournal.CleanupAsync(path,PhysicalNetwork.PowerShell,()=>[]);
            Assert.False(File.Exists(path));
        } finally { Directory.Delete(root,true); }
    }

    [Theory][InlineData(RoutingMode.Global)][InlineData(RoutingMode.Rules)]
    public async Task NativeCarrierWithoutMainProfileTransfersTcpUdpAndGuardsPendingCorporateHostname(RoutingMode mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "NetCat-independent-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)); var ct = timeout.Token;
        using var echo = new TcpListener(IPAddress.Loopback, 0); echo.Start();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var nic = NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback);
        var physical = new NetworkSnapshot(nic.Name, nic.GetIPProperties().GetIPv4Properties()!.Index, "127.0.0.1", "127.0.0.1", []);
        var settings = new AppSettings { Tun = false, Mode = mode, SocksPort = OpenVpnService.FreePort(), OpenVpnDomains = "office.example" };
        try
        {
            using var router = new RouterService(RoutingTests.ModuleRoot, root);
            router.PrepareDomainOwnership(settings, true);
            await router.EnsureRunningAsync(settings, physical, false, "independent-native", ct);
            Assert.True(router.IsRunning); Assert.False(router.VpnRequested); Assert.False(router.VpnRunning);
            Assert.Null(router.ActiveProfileId); Assert.Equal(0, router.LatencyPort); Assert.True(router.DependenciesHealthy);
            var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "router.json")))!;
            Assert.Equal("direct", cfg["route"]!["final"]!.ToString());
            Assert.False(File.Exists(Path.Combine(root, "xray.json")));
            var revision = router.SessionRevision;
            await router.EnsureRunningAsync(settings, physical, false, "noop", ct);
            Assert.Equal(revision, router.SessionRevision); Assert.Equal(1, router.StartCount);

            using var client = await Connect(router.ListenPort, "127.0.0.1", ((IPEndPoint)echo.LocalEndpoint).Port, 1, ct);
            var payload = Encoding.UTF8.GetBytes("independent carrier full duplex " + Guid.NewGuid());
            await client.GetStream().WriteAsync(payload, ct);
            using var server = await echo.AcceptTcpClientAsync(ct);
            var received = new byte[payload.Length]; await server.GetStream().ReadExactlyAsync(received, ct); Assert.Equal(payload, received);
            await server.GetStream().WriteAsync(received, ct); await client.GetStream().ReadExactlyAsync(received, ct); Assert.Equal(payload, received);

            using var association = await Connect(router.ListenPort, "0.0.0.0", 0, 3, ct);
            var relay = new IPEndPoint(IPAddress.Loopback, udpRelayPort);
            using var sender = new UdpClient();
            int target = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            var packet = new byte[] { 0,0,0,1,127,0,0,1,(byte)(target >> 8),(byte)target }.Concat(payload).ToArray();
            await sender.SendAsync(packet, relay, ct);
            var datagram = await udp.ReceiveAsync(ct); Assert.Equal(payload, datagram.Buffer);
            await udp.SendAsync(datagram.Buffer, datagram.RemoteEndPoint, ct);
            var response = await sender.ReceiveAsync(ct); Assert.Equal(payload, response.Buffer[10..]);

            using var denied = await Connect(router.ListenPort, "new.office.example", ((IPEndPoint)echo.LocalEndpoint).Port, 1, ct);
            await denied.GetStream().WriteAsync(payload, ct);
            var b = new byte[1];
            try { Assert.Equal(0, await denied.GetStream().ReadAsync(b, ct)); }
            catch (IOException) { /* Native SOCKS rejection may reset the TCP socket. */ }
            Assert.False(echo.Pending());
            await router.EnsureStoppedAsync(ct);
            Assert.False(router.IsRunning); Assert.False(router.VpnRequested); Assert.False(router.TunActive);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private int udpRelayPort;
    private async Task<TcpClient> Connect(int proxy, string host, int port, byte command, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, proxy, ct); var s = client.GetStream();
            await s.WriteAsync(new byte[] { 5,1,0 }, ct); var greeting = new byte[2]; await s.ReadExactlyAsync(greeting, ct); Assert.Equal(new byte[] {5,0}, greeting);
            await s.WriteAsync(new byte[] {5,command,0,3,(byte)host.Length}.Concat(Encoding.ASCII.GetBytes(host)).Concat(new byte[] {(byte)(port >> 8),(byte)port}).ToArray(),ct);
            var reply = new byte[10]; await s.ReadExactlyAsync(reply, ct); Assert.Equal(0,reply[1]); Assert.Equal(1,reply[3]);
            if (command == 3) udpRelayPort = reply[8]*256 + reply[9];
            return client;
        }
        catch { client.Dispose(); throw; }
    }
}
