using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate17Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NetCat-C17-" + Guid.NewGuid().ToString("N"));
    public Candidate17Tests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private Candidate12Tests.Fixture Fixture() => new(new SettingsStore(root));
    private static async Task Toggle(Candidate12Tests.Fixture f, bool enabled)
    {
        f.Desired.Current = f.Desired.Current with { OpenVpnEnabled = enabled };
        await f.Reconcile(ReconcileReason.UserToggledOpenVpn);
    }
    private async Task<Candidate12Tests.Fixture> Started(bool openVpn = false)
    {
        var f = Fixture(); await f.Reconcile(); if (openVpn) await Toggle(f, true); return f;
    }

    [Fact] public async Task OpenVpnEnableDoesNotRestartMainRouter()
    { using var f = await Started(); await Toggle(f, true); Assert.Equal(1, f.Router.Starts); Assert.Equal(0, f.Router.Overlays); }
    [Fact] public async Task OpenVpnDisableDoesNotRestartMainRouter()
    { using var f = await Started(true); await Toggle(f, false); Assert.Equal(1, f.Router.Starts); Assert.Equal(0, f.Router.Overlays); }
    [Fact] public async Task OpenVpnEnableDoesNotStopMainSingBox()
    { using var f = await Started(); await Toggle(f, true); Assert.Equal(0, f.Router.Stops); Assert.True(f.Router.IsRunning); }
    [Fact] public async Task OpenVpnDisableDoesNotStopMainSingBox()
    { using var f = await Started(true); await Toggle(f, false); Assert.Equal(0, f.Router.Stops); Assert.True(f.Router.IsRunning); }
    [Fact] public async Task OpenVpnEnableDoesNotRecreateTun()
    { using var f = await Started(); var revision = f.Router.SessionRevision; await Toggle(f, true); Assert.Equal(revision, f.Router.SessionRevision); Assert.Equal(1, f.Tunnel.Waits); }
    [Fact] public async Task OpenVpnDisableDoesNotRecreateTun()
    { using var f = await Started(true); var revision = f.Router.SessionRevision; await Toggle(f, false); Assert.Equal(revision, f.Router.SessionRevision); Assert.Equal(1, f.Tunnel.Waits); }
    [Fact] public async Task OpenVpnEnableKeepsMainRouterLifecycleRunning()
    { using var f = await Started(); var logs = new List<string>(); f.Coordinator.Log = logs.Add; await Toggle(f, true); Assert.Equal(MainRouterLifecycle.Running, f.Coordinator.MainRouterLifecycle); Assert.DoesNotContain(logs, s => s.Contains("Running -> Starting")); }
    [Fact] public async Task OpenVpnDisableKeepsMainRouterLifecycleRunning()
    { using var f = await Started(true); var logs = new List<string>(); f.Coordinator.Log = logs.Add; await Toggle(f, false); Assert.Equal(MainRouterLifecycle.Running, f.Coordinator.MainRouterLifecycle); Assert.DoesNotContain(logs, s => s.Contains("Running -> Starting")); }
    [Fact] public async Task MainTunIdentityStableAcrossOpenVpnOnOff()
    {
        using var f = await Started(); var session = f.Router.SessionRevision;
        await Toggle(f, true); await Toggle(f, false);
        Assert.Equal(session, f.Router.SessionRevision); Assert.True(f.Router.TunActive);
        Assert.Equal(TunStructuralStatus.Healthy, f.Coordinator.CurrentObservedState!.TunStatus);
        Assert.Equal(0, f.Router.Stops); Assert.Equal(0, f.Router.Overlays);
    }
    [Fact] public async Task OpenVpnLateSuccessAppliesRoutesWithoutMainRouterRestart()
    { using var f = await Started(); await Toggle(f, true); Assert.Equal(1, f.Router.GatewayUpdates); Assert.Equal(0, f.Router.Overlays); Assert.True(f.Coordinator.CurrentObservedState!.OpenVpnRoutesInstalled); }
    [Fact] public async Task OpenVpnDisconnectRemovesRoutesWithoutMainRouterRestart()
    { using var f = await Started(true); await Toggle(f, false); Assert.False(f.OpenVpn.IsRunning); Assert.Equal(false, f.Coordinator.CurrentObservedState!.OpenVpnRoutesInstalled); Assert.Equal(1, f.Router.Starts); }

    private static OpenVpnGateway Gateway(string folder) => new(31001, 31002, Path.Combine(folder, "rules.json"), "test", "test-only");
    private static NetworkSnapshot Physical => new("", 0, "", "127.0.0.1", []);
    private static JsonObject MainConfig(AppSettings s, OpenVpnGateway gateway, bool tun = false)
        => SingBoxConfig.Build(s, Physical, null, null, tun, openVpnGateway: gateway);

    [Fact] public void OpenVpnDnsActivatesWithoutMainRouterRestart()
    {
        var gateway = Gateway(root); var settings = new AppSettings { OpenVpnDomains = "corp.test" };
        var main = MainConfig(settings, gateway);
        var dns = Assert.Single(main["dns"]!["servers"]!.AsArray(), x => x?["tag"]?.ToString() == "dns-openvpn");
        Assert.Equal("127.0.0.1", dns!["server"]!.ToString());
        var sidecar = OpenVpnSidecar.BuildConfig(gateway, new("TestAdapter", 10, "10.7.0.2", "10.7.0.1", "10.7.0.53", []), destination:new(31003,"owned-test","explicit-test"));
        Assert.Equal("10.7.0.53", sidecar["dns"]!["servers"]![0]!["server"]!.ToString());
        Assert.Equal(main.ToJsonString(), MainConfig(settings, gateway).ToJsonString());
    }
    [Fact] public void OpenVpnDnsDeactivatesWithoutMainRouterRestart()
    {
        var sidecar = OpenVpnSidecar.BuildConfig(Gateway(root), null);
        Assert.Null(sidecar["dns"]);
        Assert.Equal("reject", sidecar["route"]!["rules"]![0]!["action"]!.ToString());
        var main = MainConfig(new AppSettings { OpenVpnDomains = "corp.test" }, Gateway(root));
        var rule = Assert.Single(main["dns"]!["rules"]!.AsArray(), n => n?["server"]?.ToString() == "dns-openvpn");
        Assert.True(rule!["disable_cache"]!.GetValue<bool>());
    }
    [Fact] public void OpenVpnFailureKeepsKnownPrivateRoutesFailClosed()
    {
        using var gateway = new OpenVpnSidecar("unused.exe", root);
        var settings = new AppSettings { Profiles = [new() { Protocol = "openvpn", LearnedRoutes = ["10.7.0.0/24"] }] };
        gateway.RememberRoutes(settings, new("Test", 7, "10.7.0.2", "10.7.0.1", "10.7.0.53", ["10.8.0.0/24"]));
        gateway.RememberRoutes(settings, null);
        var routes = File.ReadAllText(gateway.Gateway.RulesPath);
        Assert.Contains("10.7.0.0/24", routes); Assert.Contains("10.8.0.0/24", routes);
    }
    [Fact] public void OpenVpnOffDoesNotRouteKnownPrivateTargetsViaDirect()
    {
        var cfg = MainConfig(new(), Gateway(root));
        var rule = Assert.Single(cfg["route"]!["rules"]!.AsArray(), n => n?["rule_set"] != null);
        Assert.Equal("openvpn", rule!["outbound"]!.ToString());
        var outbound = Assert.Single(cfg["outbounds"]!.AsArray(), n => n?["tag"]?.ToString() == "openvpn");
        Assert.Equal("socks", outbound!["type"]!.ToString());
    }
    [Fact] public void UnrelatedRfc1918RoutesAreNotBlocked()
    {
        var rule = Assert.Single(MainConfig(new(), Gateway(root))["route"]!["rules"]!.AsArray(), n => n?["ip_is_private"] != null);
        Assert.Equal("direct", rule!["outbound"]!.ToString());
    }
    [Fact] public async Task OpenVpnAdapterEventsDoNotTriggerPhysicalNetworkChanged()
    {
        using var f = await Started(); using var monitor = new PhysicalNetworkMonitor(f.Coordinator, f.Repo);
        var gen = f.Coordinator.CurrentGeneration; monitor.Signal(); await monitor.ObserveAsync();
        Assert.Equal(gen, f.Coordinator.CurrentGeneration); Assert.Equal(0, monitor.Generation);
    }
    [Fact] public async Task OpenVpnAdapterEventBurstDoesNotCauseNoOpReconcileStorm()
    {
        using var f = await Started(); using var monitor = new PhysicalNetworkMonitor(f.Coordinator, f.Repo);
        var gen = f.Coordinator.CurrentGeneration;
        for (int i = 0; i < 30; i++) { monitor.Signal(); await monitor.ObserveAsync(); }
        Assert.Equal(gen, f.Coordinator.CurrentGeneration); Assert.Equal(0, monitor.Generation);
    }
    [Fact] public async Task EthernetLossStillTriggersPhysicalNetworkChanged()
    {
        using var f = await Started(); using var monitor = new PhysicalNetworkMonitor(f.Coordinator, f.Repo);
        f.Physical.Current = null; await monitor.ObserveAsync();
        Assert.Equal(1, monitor.Generation); Assert.Equal(PhysicalNetworkAvailability.Unavailable, f.Coordinator.PhysicalNetworkState);
    }
    [Fact] public async Task WifiFailoverStillWorksAfterOpenVpnVirtualAdapterFiltering()
    {
        using var f = await Started(); using var monitor = new PhysicalNetworkMonitor(f.Coordinator, f.Repo);
        await monitor.ObserveAsync(); f.Physical.Current = new("Wi-Fi", 25, "192.168.2.2", "192.168.2.1", []);
        await monitor.ObserveAsync(); Assert.Equal("Wi-Fi", f.Router.ActivePhysical!.Name); Assert.Equal(2, f.Router.Starts);
    }
    [Fact] public async Task OpenVpnRetryDoesNotRestartMainRouter()
    { using var f = await Started(); f.Desired.Current = f.Desired.Current with { OpenVpnEnabled = true, SelectedOpenVpnProfileId = Guid.NewGuid() }; await f.Reconcile(); await f.Reconcile(); Assert.Equal(1, f.Router.Starts); Assert.Equal(0, f.Router.Overlays); }
    [Fact] public async Task OpenVpnRetryDoesNotRestartZapret()
    { using var f = await Started(); f.Desired.Current = f.Desired.Current with { OpenVpnEnabled = true, SelectedOpenVpnProfileId = Guid.NewGuid() }; await f.Reconcile(); await f.Reconcile(); Assert.Equal(1, f.Zapret.Starts); }
    [Fact] public async Task OpenVpnRetryDoesNotRecreateTun()
    { using var f = await Started(); var revision = f.Router.SessionRevision; f.Desired.Current = f.Desired.Current with { OpenVpnEnabled = true, SelectedOpenVpnProfileId = Guid.NewGuid() }; await f.Reconcile(); await f.Reconcile(); Assert.Equal(revision, f.Router.SessionRevision); Assert.Equal(1, f.Tunnel.Waits); }

    [Fact]
    public async Task MainSingBoxPidStableAcrossOpenVpnOnOff()
    {
        // Real bundled processes and TCP session, loopback only. No OS TUN is
        // created here, so this cannot disturb an existing user's NetCat-TUN.
        var executable = Path.Combine(RoutingTests.ModuleRoot, "sing-box", "sing-box.exe");
        await using var routes = new Candidate26RouteFixture();
        using var gateway = new OpenVpnSidecar(executable, Path.Combine(root, "gateway"),routes.Leases);
        var settings = new AppSettings(); gateway.RememberRoutes(settings);
        int mainPort = OpenVpnService.FreePort();
        var config = SingBoxConfig.Build(settings, Physical, null, null, false, port: mainPort, openVpnGateway: gateway.Gateway);
        var configPath = Path.Combine(root, "main.json"); await File.WriteAllTextAsync(configPath, config.ToJsonString());
        using var main = new ProcessHost(); main.Start(executable, ["run", "-c", configPath]);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await RouterService.WaitPortAsync(mainPort, main, ct.Token);
        int pid = main.Id;
        using var echo = new TcpListener(IPAddress.Any, 0); echo.Start(); int echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        var accept = echo.AcceptTcpClientAsync(ct.Token);
        using var client = await SocksConnect(mainPort, "127.0.0.1", echoPort, ct.Token);
        using var server = await accept;
        async Task Exchange()
        {
            await client.GetStream().WriteAsync(new byte[] { 42 }, ct.Token);
            var b = new byte[1]; await server.GetStream().ReadExactlyAsync(b, ct.Token); Assert.Equal(42, b[0]);
        }
        await Exchange();
        var nic = NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback);
        var link = new OpenVpnLink(nic.Name, nic.GetIPProperties().GetIPv4Properties()!.Index, "127.0.0.1", "127.0.0.1", "127.0.0.1", ["127.0.0.2/32"]);
        await routes.Start(link);
        await gateway.ApplyAsync(settings, link, ct.Token); await Exchange(); Assert.Equal(pid, main.Id); Assert.True(main.Running);
        // Prove automatic reload of the newly learned /32 by reaching the
        // authenticated gateway, then turning it OFF without touching main.
        await Task.Delay(250, ct.Token); // Native watcher observation window, not a product startup delay.
        using (var corp = await SocksConnect(mainPort, "127.0.0.2", echoPort, ct.Token))
        using (var accepted = await echo.AcceptTcpClientAsync(ct.Token))
        {
            await corp.GetStream().WriteAsync(new byte[] { 17 }, ct.Token);
            var b = new byte[1]; await accepted.GetStream().ReadExactlyAsync(b, ct.Token); Assert.Equal(17, b[0]);
        }
        await gateway.ApplyAsync(settings, null, ct.Token); await Exchange();
        Assert.Equal(pid, main.Id); Assert.True(main.Running);
        // sing-box acknowledges SOCKS before sniffing/dialing; rejection is
        // observable only after payload, not at the SOCKS handshake itself.
        using (var denied = await SocksConnect(mainPort, "127.0.0.2", echoPort, ct.Token))
        {
            await denied.GetStream().WriteAsync(new byte[] { 99 }, ct.Token);
            var read = denied.GetStream().ReadAsync(new byte[1], ct.Token).AsTask();
            var leaked = echo.AcceptTcpClientAsync(ct.Token).AsTask();
            Assert.Same(read, await Task.WhenAny(read, leaked).WaitAsync(TimeSpan.FromSeconds(3), ct.Token));
            try { Assert.Equal(0, await read); } catch (IOException) { }
        }
        Assert.Contains("127.0.0.2/32", File.ReadAllText(gateway.Gateway.RulesPath));
        Assert.Equal(config.ToJsonString(), await File.ReadAllTextAsync(configPath, ct.Token));
    }

    private static async Task<TcpClient> SocksConnect(int port, string ip, int targetPort, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port, ct); var stream = tcp.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, ct); var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, ct);
            var request = new byte[] { 5, 1, 0, 1 }.Concat(IPAddress.Parse(ip).GetAddressBytes()).Concat(new byte[] { (byte)(targetPort >> 8), (byte)targetPort }).ToArray();
            await stream.WriteAsync(request, ct); var reply = new byte[10]; await stream.ReadExactlyAsync(reply, ct);
            if (reply[1] != 0) throw new IOException("SOCKS rejected destination: " + reply[1]);
            return tcp;
        }
        catch { tcp.Dispose(); throw; }
    }

    [Fact]
    public async Task ProductionRouterAndXrayPidsStayStableDuringGatewayUpdates()
    {
        var bin = RoutingTests.ModuleRoot;
        var nic = NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback);
        var physical = new NetworkSnapshot(nic.Name, nic.GetIPProperties().GetIPv4Properties()!.Index, "127.0.0.1", "127.0.0.1", []);
        var profile = new Profile { Name = "isolated test", Protocol = "vless", Core = "Xray", Host = "127.0.0.1", Port = 9,
            OutboundJson = "{\"type\":\"vless\",\"server\":\"127.0.0.1\",\"server_port\":9,\"uuid\":\"11111111-1111-1111-1111-111111111111\"}" };
        var settings = new AppSettings { Profiles = [profile], MainProfileId = profile.Id, Tun = false, SocksPort = OpenVpnService.FreePort() };
        using var router = new RouterService(bin, Path.Combine(root, "runtime"));
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await router.SetVpnAsync(settings, true, ct.Token, physical: physical);
        ProcessHost Host(string field) => (ProcessHost)typeof(RouterService).GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(router)!;
        var core = Host("core"); var xray = Host("xray");
        var ids = (core.Id, xray.Id, router.SessionRevision);
        var configFile = Path.Combine(root, "runtime", "router.json"); var config = await File.ReadAllTextAsync(configFile, ct.Token);
        await router.ApplyOpenVpnOverlayAsync(settings, new(nic.Name, physical.Index, "127.0.0.1", "127.0.0.1", "127.0.0.1", ["127.0.0.2/32"]), ct.Token);
        Assert.Equal(ids, (core.Id, xray.Id, router.SessionRevision));
        await router.ApplyOpenVpnOverlayAsync(settings, null, ct.Token);
        Assert.Equal(ids, (core.Id, xray.Id, router.SessionRevision));
        Assert.True(core.Running); Assert.True(xray.Running);
        Assert.Equal(config, await File.ReadAllTextAsync(configFile, ct.Token));
    }

    [Fact]
    public async Task NativeCorporateDnsForwarderUsesOnlyDedicatedResolver()
    {
        var exe = Path.Combine(RoutingTests.ModuleRoot, "sing-box", "sing-box.exe");
        using var upstream = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int upstreamPort = ((IPEndPoint)upstream.Client.LocalEndPoint!).Port;
        using var gateway = new OpenVpnSidecar(exe, Path.Combine(root, "dns-gateway")) { DnsUpstreamPort = upstreamPort };
        var nic = NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback);
        var link = new OpenVpnLink(nic.Name, nic.GetIPProperties().GetIPv4Properties()!.Index, "127.0.0.1", "127.0.0.1", "127.0.0.1", []);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Candidate18 owns public endpoints permanently: exercise the real lease,
        // encrypted transport and production sidecar rather than binding over it.
        await gateway.ApplyAsync(new(), link, ct.Token);
        // Exercise the main resolver too, with the real physical default
        // binding (still loopback-only test listeners, no TUN/system routes).
        var settings = new AppSettings { OpenVpnDomains = "corp.test" };
        gateway.RememberRoutes(settings);
        int mainPort = OpenVpnService.FreePort(), dnsPort = OpenVpnService.FreeTcpUdpPort();
        var mainConfig = SingBoxConfig.Build(settings, PhysicalNetwork.TryCapture() ?? Physical, null, null, false,
            port: mainPort, openVpnGateway: gateway.Gateway);
        mainConfig["inbounds"]!.AsArray().Add(new JsonObject { ["type"] = "direct", ["listen"] = "127.0.0.1",
            ["listen_port"] = dnsPort, ["network"] = "udp", ["override_address"] = "1.1.1.1", ["override_port"] = 53 });
        var mainFile = Path.Combine(root, "dns-main.json"); await File.WriteAllTextAsync(mainFile, mainConfig.ToJsonString());
        using var main = new ProcessHost(); main.Start(exe, ["run", "-c", mainFile]);
        await RouterService.WaitPortAsync(mainPort, main, ct.Token); int mainPid = main.Id;
        using var query = new UdpClient(); query.Connect(IPAddress.Loopback, dnsPort);
        var payload = new byte[] { 0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 4, 99, 111, 114, 112, 4, 116, 101, 115, 116, 0, 0, 1, 0, 1 };
        await query.SendAsync(payload, ct.Token);
        var received = await upstream.ReceiveAsync(ct.Token); Assert.Equal(payload[12..], received.Buffer[12..]);
        var reply = (byte[])received.Buffer.Clone(); reply[2] = 0x81; reply[3] = 0x83;
        await upstream.SendAsync(reply, received.RemoteEndPoint, ct.Token);
        var response = (await query.ReceiveAsync(ct.Token)).Buffer;
        Assert.Equal(payload[..2], response[..2]); Assert.Equal(3, response[3] & 15);
        await gateway.ApplyAsync(new(), null, ct.Token);
        await query.SendAsync(payload, ct.Token);
        using var noLeak = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await upstream.ReceiveAsync(noLeak.Token));
        Assert.Equal(mainPid, main.Id); Assert.True(main.Running);
    }
}
