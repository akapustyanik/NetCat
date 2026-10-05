using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed class OpenVpnOffRoutingTests
{
    [Fact]
    public async Task OffReleasesDnsAndVpnGuardsButRequestedReconnectKeepsThem()
    {
        var guard = new CorporateDomainGuard();
        guard.Prepare("office.example");
        Assert.True(guard.IsCorporate("intranet.office.example"));
        await Assert.ThrowsAsync<IOException>(() => guard.DispatchAutomatic("intranet.office.example", () => Task.CompletedTask));
        guard.SetEnabled(false);
        guard.Prepare("office.example"); // A settings refresh must not undo OFF.
        guard.Candidate(Guid.NewGuid(), 1, ["obsolete.example"]);
        Assert.False(guard.IsCorporate("intranet.office.example"));
        var sent = 0;
        await guard.DispatchAutomatic("intranet.office.example", () => { sent++; return Task.CompletedTask; });
        Assert.Equal(1, sent);
        guard.SetEnabled(true);
        Assert.True(guard.IsCorporate("intranet.office.example"));
        Assert.False(guard.IsCorporate("obsolete.example"));
        await Assert.ThrowsAsync<IOException>(() => guard.DispatchAutomatic("intranet.office.example", () => Task.CompletedTask));
    }

    [Fact]
    public void OffClearsRuleSetsAndLateOldLinkCannotRestoreThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "NetCat-off-rules-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profile = new Profile { Protocol = "openvpn", LearnedRoutes = ["10.23.0.0/16"] };
            var settings = new AppSettings { OpenVpnDomains = "office.example", Profiles = [profile], OpenVpnProfileId = profile.Id };
            using var sidecar = new OpenVpnSidecar(Path.Combine(RoutingTests.ModuleRoot, "sing-box", "sing-box.exe"), root);
            var old = new OpenVpnLink("OpenVPN", 8, "10.99.0.2", "10.99.0.1", "10.99.0.1", ["10.99.0.0/16"]);
            sidecar.RememberRoutes(settings, old);
            sidecar.RoutingRequested = false;
            sidecar.RememberRoutes(settings, old);
            Assert.Empty(sidecar.Ownership.Known);
            Assert.Empty(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(sidecar.Gateway.DomainsPath))!["rules"]!.AsArray());
            Assert.Empty(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(sidecar.Gateway.RulesPath))!["rules"]!.AsArray());
            sidecar.RoutingRequested = true;
            sidecar.RememberRoutes(settings);
            Assert.Contains("office.example", File.ReadAllText(sidecar.Gateway.DomainsPath));
            Assert.DoesNotContain("10.99.0.0/16", File.ReadAllText(sidecar.Gateway.RulesPath));
            Assert.Contains("10.23.0.0/16", File.ReadAllText(sidecar.Gateway.RulesPath));
            Assert.Equal("office.example", settings.OpenVpnDomains);
            Assert.Equal(["10.23.0.0/16"], profile.LearnedRoutes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(RoutingMode.Rules)]
    [InlineData(RoutingMode.Global)]
    public void OffDoesNotCreateAutomaticCorporateDomainOrPrefixRules(RoutingMode mode)
    {
        var profile = new Profile { Protocol = "openvpn", LearnedRoutes = ["10.23.0.0/16"] };
        var settings = new AppSettings { Mode = mode, OpenVpnDomains = "office.example", Profiles = [profile], OpenVpnProfileId = profile.Id };
        var physical = new NetworkSnapshot("Ethernet", 5, "192.168.20.10", "192.168.20.1", []);
        var main = ProfileImporter.ParseLink("socks://127.0.0.1:1080");
        var config = SingBoxConfig.Build(settings, physical, main, null, true);
        Assert.DoesNotContain(config["route"]!["rules"]!.AsArray(), rule => rule!.ToJsonString().Contains("office.example") || rule.ToJsonString().Contains("10.23.0.0/16"));
        Assert.DoesNotContain(config["dns"]!["rules"]!.AsArray(), rule => rule!.ToJsonString().Contains("office.example"));
        Assert.Equal(mode == RoutingMode.Global ? "vpn" : "direct", config["route"]!["final"]!.ToString());
        Assert.Equal("office.example", settings.OpenVpnDomains);
        Assert.Equal(["10.23.0.0/16"], profile.LearnedRoutes);
    }
}
