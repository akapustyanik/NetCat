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
public sealed class Candidate19NativeTests
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
        public Fixture(bool freezeOwnership = false)
        {
            Directory.CreateDirectory(Root);
            var nic = NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback);
            Physical = new(nic.Name, nic.GetIPProperties().GetIPv4Properties()!.Index, "127.0.0.1", "127.0.0.1", []);
            var main = new Profile { Protocol = "vless", Core = "Xray", Host = "127.0.0.1", Port = 9,
                OutboundJson = "{\"type\":\"vless\",\"server\":\"127.0.0.1\",\"server_port\":9,\"uuid\":\"11111111-1111-1111-1111-111111111111\"}" };
            Settings = new() { Profiles = [main, A, B], MainProfileId = main.Id, OpenVpnProfileId = A.Id, Tun = false, SocksPort = OpenVpnService.FreePort() };
            Router = new(RoutingTests.ModuleRoot, Root) { StartProcessOverride = (host, exe, args) => {
                if(freezeOwnership) {
                    var file=args[Array.IndexOf(args,"-c")+1];
                    var config=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
                    foreach(var ruleSet in config["route"]!["rule_set"]!.AsArray()) {
                        if(ruleSet!["tag"]!.ToString()!="openvpn-owned") continue;
                        var original=ruleSet["path"]!.ToString();var frozen=Path.Combine(Root,"frozen-ownership.json");
                        File.Copy(original,frozen,true);ruleSet["path"]=frozen;
                    }
                    File.WriteAllText(file,config.ToJsonString());
                }
                host.Start(exe,args);
            } }; Echo.Start();
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
    [Fact] public async Task NewGenerationCannotLeakBeforeMainRuleSetReload()
    {
        using var f=new Fixture(freezeOwnership:true);f.A.LearnedRoutes.Clear();f.B.LearnedRoutes.Clear();
        await f.Start();await f.Apply(null);var identity=f.Identity;
        // Freeze the actual main native watcher to its initial empty rules. The
        // production sidecar accepts the new generation while main is delayed.
        f.A.LearnedRoutes=["127.0.0.2/32"];await f.Apply(f.A,2);
        await f.Denied("127.0.0.2");await f.Allowed("127.0.0.1");Assert.Equal(identity,f.Identity);
    }
    [Fact] public async Task NoOpApplyKeepsCorporateTcpConnection()
    {
        using var f=new Fixture();await f.Start();await f.Apply(f.A);
        using var client=await f.Connect("127.0.0.2");await client.GetStream().WriteAsync(new byte[]{19},f.Token);
        using var server=await f.Echo.AcceptTcpClientAsync(f.Token);await server.GetStream().ReadExactlyAsync(new byte[1],f.Token);
        await f.Apply(f.A);
        await server.GetStream().WriteAsync(new byte[]{20},f.Token);var response=new byte[1];await client.GetStream().ReadExactlyAsync(response,f.Token);Assert.Equal(20,response[0]);
    }
    [Fact] public async Task PrivateBackendCollisionRetriesAnotherPort()
    {
        // Candidate32 Dev5: local port recovery.
        // The private backend changes port without replacing the
        // stable public gateway or restarting the main router.

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

        using var tcp =
            new TcpListener(IPAddress.Loopback, occupiedPort);

        tcp.Start();

        using var udp = new UdpClient(
            new IPEndPoint(IPAddress.Loopback, occupiedPort));

        // An inactive backend cannot carry corporate traffic.
        await f.Denied("127.0.0.2");

        // Apply retries internally and must select another port.
        await f.Apply(f.A);

        var replacement = (OpenVpnGateway)typeof(OpenVpnSidecar)
            .GetField(
                "backend",
                BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(f.Gateway)!;

        Assert.NotEqual(
            occupiedPort,
            replacement.SocksPort);

        Assert.True(f.Gateway.Running);
        Assert.True(f.Gateway.Active);

        // Public endpoints and unrelated process identities
        // must remain unchanged.
        Assert.Equal(
            publicPort,
            f.Gateway.Gateway.SocksPort);

        Assert.Equal(
            publicDnsPort,
            f.Gateway.Gateway.DnsPort);

        Assert.Equal(identity, f.Identity);
        Assert.Equal(mainBytes, f.MainBytes);

        // Traffic must use the recovered private backend.
        await f.Allowed("127.0.0.2");

        // OFF must still block the corporate destination.
        await f.Apply(null);
        await f.Denied("127.0.0.2");

        Assert.Equal(identity, f.Identity);
        Assert.Equal(mainBytes, f.MainBytes);

        // No connection may ever reach the occupied private port.
        using var noLeak =
            new CancellationTokenSource(
                TimeSpan.FromMilliseconds(400));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () =>
            {
                using var leaked =
                    await tcp.AcceptTcpClientAsync(
                        noLeak.Token);
            });
    }
    [Fact] public async Task BackendRetryNeverReusesAnyFailedPortInSameApply()
    {
        using var f = new Fixture();
        await f.Start(); await f.Apply(f.A);
        var identity = f.Identity;
        var backend = (OpenVpnGateway)typeof(OpenVpnSidecar).GetField("backend", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Gateway)!;
        await f.Gateway.DeactivateAsync();
        using var first = new TcpListener(IPAddress.Loopback, backend.SocksPort); first.Start();
        using var second = new TcpListener(IPAddress.Loopback, 0); second.Start();
        var secondPort = ((IPEndPoint)second.LocalEndpoint).Port;
        var samples = new Queue<int>([secondPort, backend.SocksPort, OpenVpnService.FreeTcpUdpPort()]);
        f.Gateway.AllocateBackendPort = () => samples.Dequeue();
        await f.Apply(f.A);
        Assert.Empty(samples);
        Assert.True(f.Gateway.Active);
        Assert.Equal(identity, f.Identity);
        await f.Allowed("127.0.0.2");
        using var noLeak = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var unexpected = await first.AcceptTcpClientAsync(noLeak.Token); });
        using var noSecondLeak = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var unexpected = await second.AcceptTcpClientAsync(noSecondLeak.Token); });
    }
    [Fact] public async Task BackendRetryExhaustionRemainsClosedAndPreservesMainIdentity()
    {
        using var f = new Fixture();
        await f.Start(); await f.Apply(f.A);
        var identity = f.Identity;
        var publicPort = f.Gateway.Gateway.SocksPort;
        var backend = (OpenVpnGateway)typeof(OpenVpnSidecar).GetField("backend", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Gateway)!;
        await f.Gateway.DeactivateAsync();
        using var first = new TcpListener(IPAddress.Loopback, backend.SocksPort); first.Start();
        using var second = new TcpListener(IPAddress.Loopback, 0); second.Start();
        using var third = new TcpListener(IPAddress.Loopback, 0); third.Start();
        var samples = new Queue<int>([((IPEndPoint)second.LocalEndpoint).Port, ((IPEndPoint)third.LocalEndpoint).Port]);
        f.Gateway.AllocateBackendPort = () => samples.Dequeue();
        await Assert.ThrowsAsync<PortCollisionException>(() => f.Apply(f.A));
        Assert.Empty(samples);
        Assert.False(f.Gateway.Active); Assert.False(f.Gateway.Running);
        Assert.Equal(publicPort, f.Gateway.Gateway.SocksPort);
        Assert.Equal(identity, f.Identity);
        await f.Denied("127.0.0.2");
        foreach (var listener in new[] { first, second, third })
        {
            using var noLeak = new CancellationTokenSource(150);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var unexpected = await listener.AcceptTcpClientAsync(noLeak.Token); });
        }
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ReconnectAndProfileSwitchNewPrefixUseActivationBarrier(bool switchProfile)
    {
        using var f=new Fixture(freezeOwnership:true);f.A.LearnedRoutes.Clear();f.B.LearnedRoutes.Clear();await f.Start();await f.Apply(f.A);var identity=f.Identity;
        var next=switchProfile?f.B:f.A;next.LearnedRoutes=["127.0.0.4/32"];await f.Apply(next,2);await f.Denied("127.0.0.4");await f.Allowed("127.0.0.1");Assert.Equal(identity,f.Identity);
    }
    [Fact] public async Task FailedRuleSetActivationKeepsCorporateTrafficClosed()
    {
        using var f=new Fixture(freezeOwnership:true);f.A.LearnedRoutes.Clear();f.B.LearnedRoutes.Clear();await f.Start();await f.Apply(null);var identity=f.Identity;
        f.A.LearnedRoutes=["127.0.0.4/32"];
        var blockedPath=f.Gateway.Gateway.RulesPath+".next";Directory.CreateDirectory(blockedPath);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Apply(f.A,2));
        await f.Denied("127.0.0.4");await f.Allowed("127.0.0.1");Assert.Equal(identity,f.Identity);
    }
    [Fact] public async Task ExplicitUserDirectRuleCanResolveLanOverlap()
    {
        // Historical baseline name retained. This uses a synthetic Link and
        // proves router rule priority only, not OpenVpnService overlap admission.
        // Candidate20LanTests exercises production's conservative rejection.
        using var f=new Fixture(freezeOwnership:true);f.A.LearnedRoutes.Clear();f.B.LearnedRoutes.Clear();
        f.Settings.Rules.Add(new(){Kind=RuleKind.IpCidr,Value="127.0.0.4/32",Target=RouteTarget.Direct});await f.Start();var identity=f.Identity;
        f.A.LearnedRoutes=["127.0.0.4/32"];await f.Apply(f.A,2);await f.Allowed("127.0.0.4");Assert.Equal(identity,f.Identity);
    }
    [Fact] public async Task DomainOnlyEditKeepsCorporateTcpConnection()
    {
        using var f=new Fixture();await f.Start();await f.Apply(f.A);
        using var client=await f.Connect("127.0.0.2");await client.GetStream().WriteAsync(new byte[]{19},f.Token);
        using var server=await f.Echo.AcceptTcpClientAsync(f.Token);await server.GetStream().ReadExactlyAsync(new byte[1],f.Token);
        f.Settings.OpenVpnDomains="corp.example";await f.Apply(f.A);
        await server.GetStream().WriteAsync(new byte[]{20},f.Token);var response=new byte[1];await client.GetStream().ReadExactlyAsync(response,f.Token);Assert.Equal(20,response[0]);
    }
}
