using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed class Candidate18NativeTests
{
    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "NetCat-C18-native-" + Guid.NewGuid().ToString("N"));
        public readonly CancellationTokenSource Timeout = new(TimeSpan.FromSeconds(25));
        public readonly Profile A = new() { Protocol = "openvpn", AllowPublicPushedRoutes = true, LearnedRoutes = ["127.0.0.2/32"] };
        public readonly Profile B = new() { Protocol = "openvpn", AllowPublicPushedRoutes = true, LearnedRoutes = ["127.0.0.3/32"] };
        public readonly RouterService Router;
        public readonly AppSettings Settings;
        public readonly TcpListener Echo = new(IPAddress.Any, 0);
        public readonly NetworkSnapshot Physical;
        public CancellationToken Token => Timeout.Token;
        public int EchoPort => ((IPEndPoint)Echo.LocalEndpoint).Port;
        public OpenVpnSidecar Gateway => (OpenVpnSidecar)typeof(RouterService).GetField("openVpnSidecar", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Router)!;
        private ProcessHost Host(string field) => (ProcessHost)typeof(RouterService).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Router)!;
        public (int, int, long) Identity => (Host("core").Id, Host("xray").Id, Router.SessionRevision);
        public string MainBytes => File.ReadAllText(Path.Combine(Root, "router.json"));
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var nic = NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback);
            Physical = new(nic.Name, nic.GetIPProperties().GetIPv4Properties()!.Index, "127.0.0.1", "127.0.0.1", []);
            var main = new Profile { Protocol = "vless", Core = "Xray", Host = "127.0.0.1", Port = 9,
                OutboundJson = "{\"type\":\"vless\",\"server\":\"127.0.0.1\",\"server_port\":9,\"uuid\":\"11111111-1111-1111-1111-111111111111\"}" };
            Settings = new() { Profiles = [main, A, B], MainProfileId = main.Id, OpenVpnProfileId = A.Id, Tun = false, SocksPort = OpenVpnService.FreePort() };
            Router = new(RoutingTests.ModuleRoot, Root); Echo.Start();
        }
        public Task Start() => Router.SetVpnAsync(Settings, true, Token, physical: Physical);
        public async Task Apply(Profile? p, long generation = 1)
        {
            if (p != null) Settings.OpenVpnProfileId = p.Id;
            OpenVpnLink? link = p == null ? null : new OpenVpnLink(Physical.Name, Physical.Index, "127.0.0.1", "127.0.0.1", "127.0.0.1", p.LearnedRoutes) { ProfileId = p.Id, Generation = generation };
            await Candidate26NativeRoutes.Prepare(Router,Root,link);
            await Router.ApplyOpenVpnOverlayAsync(Settings,link,Token);
        }
        public async Task<TcpClient> Connect(string address)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, Router.ListenPort, Token); var stream = client.GetStream();
                await stream.WriteAsync(new byte[] { 5, 1, 0 }, Token); await stream.ReadExactlyAsync(new byte[2], Token);
                await stream.WriteAsync(new byte[] { 5, 1, 0, 1 }.Concat(IPAddress.Parse(address).GetAddressBytes()).Concat(new[] { (byte)(EchoPort >> 8), (byte)EchoPort }).ToArray(), Token);
                var reply = new byte[10]; await stream.ReadExactlyAsync(reply, Token); Assert.Equal(0, reply[1]); return client;
            }
            catch { client.Dispose(); throw; }
        }
        public async Task Allowed(string address)
        {
            using var client = await Connect(address); await client.GetStream().WriteAsync(new byte[] { 18 }, Token);
            using var accepted = await Echo.AcceptTcpClientAsync(Token); var data = new byte[1]; await accepted.GetStream().ReadExactlyAsync(data, Token); Assert.Equal(18, data[0]);
        }
        public async Task Denied(string address)
        {
            using var client = await Connect(address); await client.GetStream().WriteAsync(new byte[] { 18 }, Token);
            using var noLeak = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var leaked = await Echo.AcceptTcpClientAsync(noLeak.Token); });
        }
        public void Dispose() { Router.Dispose(); Echo.Stop(); Timeout.Dispose(); Directory.Delete(Root, true); }
    }
    [Fact] public async Task InactiveProfileRoutesAreFailClosedWhileAnotherProfileIsActive()
    {
        using var f = new Fixture(); await f.Start(); await f.Apply(f.A);
        await f.Allowed("127.0.0.2"); await f.Denied("127.0.0.3");
    }
    [Fact] public async Task ProfileSwitchDoesNotRouteOldProfileThroughNewTunnel()
    {
        using var f = new Fixture(); await f.Start(); var identity = f.Identity; var bytes = f.MainBytes;
        await f.Apply(f.A); await f.Allowed("127.0.0.2"); await f.Apply(null); await f.Apply(f.B);
        await f.Denied("127.0.0.2"); await f.Allowed("127.0.0.3"); Assert.Equal(identity, f.Identity); Assert.Equal(bytes, f.MainBytes);
    }
    [Fact] public async Task OpenVpnOffBlocksKnownRoutesWithoutBlockingUnrelatedRfc1918()
    {
        using var f = new Fixture(); await f.Start(); await f.Apply(f.A); await f.Apply(null);
        await f.Denied("127.0.0.2"); await f.Denied("127.0.0.3"); await f.Allowed("127.0.0.1");
        // No real RFC1918 address is created on the user's machine. The same
        // production private-direct rule is asserted by Candidate17 separately.
    }
    [Fact] public async Task OpenVpnReconnectKeepsMainSingBoxPidStable()
    {
        using var f = new Fixture(); await f.Start(); var before = f.Identity; var config = f.MainBytes;
        await f.Apply(f.A); f.Router.InvalidateOpenVpnOverlay(); await f.Denied("127.0.0.2");
        await f.Apply(f.A, 2); Assert.Equal(before.Item1, f.Identity.Item1); Assert.Equal(config, f.MainBytes);
    }
    [Fact] public async Task OpenVpnReconnectKeepsXrayPidStable()
    {
        using var f = new Fixture(); await f.Start(); var before = f.Identity;
        await f.Apply(f.A); f.Router.InvalidateOpenVpnOverlay(); await f.Apply(f.B, 2);
        Assert.Equal(before.Item2, f.Identity.Item2); Assert.Equal(before.Item3, f.Identity.Item3);
    }
    [Fact] public async Task SidecarCrashWhileOpenVpnOnDoesNotRestartMainRouter()
    {
        using var f = new Fixture(); await f.Start(); await f.Apply(f.A); var before = f.Identity; var bytes = f.MainBytes;
        using var child = Process.GetProcessById(f.Gateway.ProcessId); child.Kill(); await child.WaitForExitAsync();
        await f.Denied("127.0.0.2"); await f.Apply(f.A); await f.Allowed("127.0.0.2");
        Assert.Equal(before, f.Identity); Assert.Equal(bytes, f.MainBytes);
    }
    [Fact] public async Task SidecarCrashWhileOpenVpnOffRecoversRejectGateway()
    {
        using var f = new Fixture(); await f.Start(); var endpoints = (f.Gateway.Gateway.SocksPort, f.Gateway.Gateway.DnsPort); var before = f.Identity;
        using var child = Process.GetProcessById(f.Gateway.ProcessId); child.Kill(); await child.WaitForExitAsync();
        await f.Apply(null); Assert.True(f.Gateway.Running); Assert.False(f.Gateway.Active); await f.Denied("127.0.0.2");
        Assert.Equal(endpoints, (f.Gateway.Gateway.SocksPort, f.Gateway.Gateway.DnsPort)); Assert.Equal(before, f.Identity);
    }
    [Fact] public async Task GatewayPortsRemainOwnedWhileOpenVpnIsOff()
    {
        using var f = new Fixture(); await f.Start(); await f.Apply(f.A); await f.Apply(null);
        foreach (int port in new[] { f.Gateway.Gateway.SocksPort, f.Gateway.Gateway.DnsPort })
        {
            using var tcp = new TcpListener(IPAddress.Loopback, port); Assert.Throws<SocketException>(() => tcp.Start());
            using var udp = new UdpClient(AddressFamily.InterNetwork); Assert.Throws<SocketException>(() => udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, port)));
        }
    }
    [Fact] public async Task GatewayPortCollisionCannotRedirectCorporateTraffic()
    {
        // Candidate32 Dev5: local port recovery.
        // A failed private bind must never redirect corporate traffic
        // into a foreign listener.

        using var f = new Fixture();

        await f.Start();
        await f.Apply(f.A);

        var identity = f.Identity;
        var mainBytes = f.MainBytes;

        var publicPort = f.Gateway.Gateway.SocksPort;
        var publicDnsPort = f.Gateway.Gateway.DnsPort;

        var backend = (OpenVpnGateway)typeof(OpenVpnSidecar)
            .GetField(
                "backend",
                BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(f.Gateway)!;

        var occupiedPort = backend.SocksPort;

        await f.Gateway.DeactivateAsync();

        using var impostorTcp =
            new TcpListener(IPAddress.Loopback, occupiedPort);

        impostorTcp.Start();

        using var impostorUdp = new UdpClient(
            new IPEndPoint(IPAddress.Loopback, occupiedPort));

        // The gateway must reject corporate traffic while inactive.
        await f.Denied("127.0.0.2");

        // The first private port is occupied. Apply must recover
        // internally without publishing that occupied port.
        await f.Apply(f.A);

        var replacement = (OpenVpnGateway)typeof(OpenVpnSidecar)
            .GetField(
                "backend",
                BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(f.Gateway)!;

        Assert.NotEqual(
            occupiedPort,
            replacement.SocksPort);

        // The public gateway remains stable.
        Assert.Equal(
            publicPort,
            f.Gateway.Gateway.SocksPort);

        Assert.Equal(
            publicDnsPort,
            f.Gateway.Gateway.DnsPort);

        Assert.True(f.Gateway.Running);
        Assert.True(f.Gateway.Active);

        // The legitimate route must work through the recovered backend.
        await f.Allowed("127.0.0.2");

        // Recovery must not restart main sing-box or Xray.
        Assert.Equal(identity, f.Identity);
        Assert.Equal(mainBytes, f.MainBytes);

        // OFF must remain fail-closed after recovery.
        await f.Apply(null);
        await f.Denied("127.0.0.2");

        // The attacker must not have received a connection at
        // any point during failure, recovery or subsequent OFF.
        using var noTraffic =
            new CancellationTokenSource(
                TimeSpan.FromMilliseconds(400));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () =>
            {
                using var leaked =
                    await impostorTcp.AcceptTcpClientAsync(
                        noTraffic.Token);
            });
    }
    [Fact] public async Task OldGenerationCannotLeakDuringDelayedMainRuleSetReload()
    {
        using var f = new Fixture(); await f.Start(); await f.Apply(f.A);
        var oldRules = File.ReadAllText(f.Gateway.Gateway.RulesPath);
        f.A.LearnedRoutes = ["127.0.0.4/32"]; await f.Apply(f.A, 2);
        // Force the main core to retain/reload old interception. The sidecar's
        // authenticated owned channel must reject a no-longer-active prefix.
        File.WriteAllText(f.Gateway.Gateway.RulesPath, oldRules); await Task.Delay(200, f.Token);
        await f.Denied("127.0.0.2");
    }
    [Fact] public async Task SidecarValidationFailureClosesExistingCorporateConnections()
    {
        using var f = new Fixture(); await f.Start(); await f.Apply(f.A); var identity = f.Identity;
        using var client = await f.Connect("127.0.0.2"); await client.GetStream().WriteAsync(new byte[] { 18 }, f.Token);
        using var accepted = await f.Echo.AcceptTcpClientAsync(f.Token); await accepted.GetStream().ReadExactlyAsync(new byte[1], f.Token);
        f.Settings.OpenVpnDns = "invalid DNS address";
        await Assert.ThrowsAnyAsync<IOException>(() => f.Apply(f.A)); Assert.False(f.Gateway.Active);
        var read = client.GetStream().ReadAsync(new byte[1], f.Token).AsTask();
        try { Assert.Equal(0, await read.WaitAsync(TimeSpan.FromSeconds(3))); } catch (IOException) { }
        Assert.Equal(identity, f.Identity);
    }
    [Fact] public async Task CancelledOrStaleOverlayCannotPublishGateway()
    {
        using var f = new Fixture(); await f.Start(); await f.Apply(f.A);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Router.ApplyOpenVpnOverlayAsync(f.Settings,
            new(f.Physical.Name, f.Physical.Index, "127.0.0.1", "127.0.0.1", "127.0.0.1", f.A.LearnedRoutes), () => false, f.Token));
        await f.Denied("127.0.0.2"); Assert.False(f.Gateway.Active);
    }
    [Fact] public async Task OpenVpnDomainEditsKeepMainConfigByteIdentical()
    {
        using var f = new Fixture(); await f.Start(); var bytes = f.MainBytes; var identity = f.Identity;
        f.Settings.OpenVpnDomains = "new.corp.test"; await f.Apply(f.A);
        Assert.Contains("new.corp.test", File.ReadAllText(f.Gateway.Gateway.DomainsPath));
        Assert.Equal(bytes, f.MainBytes); Assert.Equal(identity, f.Identity);
    }
}
