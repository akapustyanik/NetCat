using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate18Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NetCat-C18-" + Guid.NewGuid().ToString("N"));
    public Candidate18Tests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private static Profile Profile(params string[] routes) => new() { Protocol = "openvpn", LearnedRoutes = routes.ToList() };

    [Fact] public void DeletingOpenVpnProfileRemovesItsRouteOwnership()
    {
        using var sidecar = new OpenVpnSidecar("unused.exe", root);
        var s = new AppSettings { Profiles = [Profile("10.1.0.0/16")] };
        sidecar.RememberRoutes(s); s.Profiles.Clear(); sidecar.RememberRoutes(s);
        Assert.DoesNotContain("10.1.0.0/16", File.ReadAllText(sidecar.Gateway.RulesPath));
    }
    [Fact] public void AllowPublicPushedRoutesDisableRemovesPreviouslyAcceptedPublicOwnership()
    {
        using var sidecar = new OpenVpnSidecar("unused.exe", root);
        var p = Profile("203.0.113.0/24"); p.AllowPublicPushedRoutes = true;
        var s = new AppSettings { Profiles = [p] }; sidecar.RememberRoutes(s);
        p.AllowPublicPushedRoutes = false; sidecar.RememberRoutes(s);
        Assert.DoesNotContain("203.0.113.0/24", File.ReadAllText(sidecar.Gateway.RulesPath));
    }
    [Fact] public async Task SuccessfulReconnectReplacesPreviousGenerationRoutes()
    {
        var p = Profile("10.1.0.0/16"); var repo = new RuntimeConfigurationRepository(new AppSettings { Profiles = [p] }, new SettingsStore(root));
        await repo.UpdateOpenVpnLearnedRoutesAsync(p.Id, ["10.2.0.0/16"]);
        Assert.Equal(new[] { "10.2.0.0/16" }, repo.CurrentSettings.Profiles.Single().LearnedRoutes);
        await repo.UpdateOpenVpnLearnedRoutesAsync(p.Id, []);
        Assert.Empty(repo.CurrentSettings.Profiles.Single().LearnedRoutes);
    }
    [Fact] public void OpenVpnStaticProfileRouteIsPreservedByNetCatOrExplicitlyRejected()
        => Assert.Throws<InvalidDataException>(() => OpenVpnConfiguration.Prepare("client\ndev tun\nroute 10.1.0.0 255.255.0.0"));
    [Fact] public void OpenVpnStaticRouteCannotTakeOverDefaultRoute()
        => Assert.Throws<InvalidDataException>(() => OpenVpnConfiguration.Prepare("client\ndev tun\nroute 0.0.0.0 0.0.0.0"));
    [Fact] public void OpenVpnUnsupportedIpv6RouteIsNotSilentlyDropped()
        => Assert.Throws<InvalidDataException>(() => OpenVpnConfiguration.Prepare("client\ndev tun\nroute-ipv6 fd00::/8"));
    [Fact] public async Task OpenVpnProfileMismatchConvergesByLocalProfileSwitch()
    {
        using var f = new Candidate12Tests.Fixture(new SettingsStore(root));
        f.Desired.Current = f.Desired.Current with { OpenVpnEnabled = true }; await f.Reconcile();
        var b = Profile("10.2.0.0/16"); await f.Repo.UpdateSettingsAsync(s => { s.Profiles.Add(b); return s; });
        f.Desired.Current = f.Desired.Current with { SelectedOpenVpnProfileId = b.Id }; await f.Reconcile();
        Assert.Equal(1, f.OpenVpn.Stops); Assert.Equal(b.Id, f.OpenVpn.ActiveProfileId);
        Assert.Equal(1, f.Router.Starts); Assert.Equal(0, f.Router.Stops); Assert.Equal(0, f.Router.Overlays); Assert.Equal(1, f.Zapret.Starts);
    }
    [Fact] public void GatewayEndpointsAreOwnedBeforeMainReferencesThem()
    {
        using var sidecar = new OpenVpnSidecar("unused.exe", root);
        using var tcp = new TcpListener(IPAddress.Loopback, sidecar.Gateway.SocksPort);
        Assert.Throws<SocketException>(() => tcp.Start());
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        Assert.Throws<SocketException>(() => udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, sidecar.Gateway.DnsPort)));
    }
    [Fact] public void ActiveProfileDoesNotRouteOtherProfilesLearnedPrefixes()
    {
        var a = Profile("10.1.0.0/16"); var b = Profile("10.2.0.0/16");
        var s = new AppSettings { Profiles = [a, b], OpenVpnProfileId = b.Id };
        var link = new OpenVpnLink("B", 20, "10.2.0.2", "10.2.0.1", "10.2.0.53", b.LearnedRoutes) { ProfileId = b.Id, Generation = 2 };
        var own = OpenVpnOwnership.Build(s, link);
        Assert.Equal(b.LearnedRoutes, own.Active); Assert.Equal(a.LearnedRoutes, own.Blocked);
        var cfg = OpenVpnSidecar.BuildConfig(new(10001, 10002, "unused", "test", "test"), link, ownership: own, destination:new(10003,"owned-test","explicit-test"));
        var rules = cfg["route"]!["rules"]!.AsArray();
        Assert.Equal("corp", rules[0]!["outbound"]!.ToString());
        Assert.Equal("10.1.0.0/16", rules[1]!["ip_cidr"]![0]!.ToString()); Assert.Equal("reject", rules[1]!["action"]!.ToString());
    }
    [Fact] public void UserRulePriorityIsPreservedAboveOpenVpnOwnership()
    {
        using var gateway = new OpenVpnSidecar("unused", root);
        var s = new AppSettings { Rules = [new() { Kind = RuleKind.IpCidr, Value = "10.1.0.0/16", Target = RouteTarget.Direct }] };
        var cfg = SingBoxConfig.Build(s, new("", 0, "", "127.0.0.1", []), null, null, false, openVpnGateway: gateway.Gateway);
        var rules = cfg["route"]!["rules"]!.AsArray().ToList();
        int user = rules.FindIndex(n => n?["ip_cidr"]?.ToJsonString().Contains("10.1.0.0/16") == true);
        int owned = rules.FindIndex(n => n?["rule_set"]?.ToJsonString().Contains("openvpn-owned") == true);
        Assert.True(user >= 0 && user < owned); Assert.Equal("direct", rules[user]!["outbound"]!.ToString());
        int telegram = rules.FindIndex(n => n?["process_name"]?.ToJsonString().Contains("Telegram.exe") == true);
        Assert.True(owned < telegram);
    }
    [Fact] public async Task OpenVpnOnlyCorporateDnsBehaviorIsExplicitAndVerified()
    {
        using var f = new Candidate12Tests.Fixture(new SettingsStore(root));
        f.Desired.Current = f.Desired.Current with { MainVpnEnabled = false, OpenVpnEnabled = true };
        await f.Repo.UpdateSettingsAsync(s => { s.OpenVpnDomains = "corp.test"; return s; }); await f.Reconcile();
        Assert.True(f.OpenVpn.IsRunning); Assert.True(f.Router.IsRunning); Assert.True(f.Router.TunActive);
        Assert.Null(f.Router.ActiveProfileId); Assert.Equal(1, f.Router.Starts); Assert.Equal(0, f.Router.Overlays);
        var xaml = File.ReadAllText(Path.Combine(RoutingTests.FindRoot(), "src", "NetCat.UI", "MainWindow.xaml"));
        Assert.Contains("При включённом VPN / TUN корпоративные домены работают через OpenVPN независимо от основного VPN.", xaml);
        Assert.Contains("При выключенном TUN доступны только IP-маршруты OpenVPN; системный DNS не изменяется.", xaml);
    }
    [Fact] public void GenerationPublicationCannotOutliveInvalidation()
    {
        var monitor = new OpenVpnGenerationMonitor(false); long generation = 0;
        monitor.Ready += g => generation = g.Revision;
        monitor.Observe("Initialization Sequence Completed"); monitor.Invalidate();
        Assert.False(monitor.PublishIfCurrent(generation, () => throw new Exception("stale publication")));
    }
}
