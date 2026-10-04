using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32TransportBypassTests
{
    [Theory]
    [InlineData(RoutingMode.Global)]
    [InlineData(RoutingMode.Rules)]
    public void CoreProcessAttributionCannotBypassPublicSocksOrUdpRouting(RoutingMode mode)
    {
        var settings = new AppSettings { Mode = mode };
        settings.Rules.Add(new RoutingRule { Kind = RuleKind.IpCidr, Value = "192.0.2.0/24", Target = RouteTarget.Block });
        var config = SingBoxConfig.Build(settings, new("Ethernet", 1, "192.0.2.10", "192.0.2.1", []), null, null, true);
        var rules = config["route"]!["rules"]!.AsArray();
        var bypass = Assert.Single(rules.OfType<JsonObject>(), r => r["process_name"]?.AsArray().Any(n => n?.ToString() == "sing-box.exe") == true);
        // Process attribution on SOCKS UDP is not an authenticated transport
        // identity. A wildcard Windows UDP row must never grant direct routing.
        Assert.Equal("tun", Assert.Single(bypass["inbound"]!.AsArray())!.ToString());
        Assert.Equal("tcp", bypass["network"]!.ToString());
        Assert.Contains(rules.OfType<JsonObject>(), r => r["ip_cidr"]?.ToJsonString().Contains("192.0.2.0/24") == true && r["action"]?.ToString() == "reject");
    }
}
