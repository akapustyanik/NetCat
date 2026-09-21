using NetCat.Core;
using NetCat.Engine;
using System.Text.Json.Nodes;
using Xunit;

namespace NetCat.Tests;
public sealed class RoutingDefaultsTests
{
    private static JsonObject Build(AppSettings s, Profile? profile = null) => SingBoxConfig.Build(s,
        new("Ethernet",24,"192.168.1.2","192.168.1.1",[]), profile, null, false, geodataDirectory:Path.Combine(RoutingTests.FindRoot(),"bin"));
    [Theory][InlineData(true)][InlineData(false)]
    public void ChoosingTelegramDefaultDisablesOldExternalProxyAndPersists(bool vpn)
    {
        var s = new AppSettings {TelegramSocks=true,TelegramSocksHost="127.0.0.1",TelegramSocksPort=1000};
        RoutingPreset.SelectTelegramDefault(s,vpn);
        s=JsonSettings.Clone(s); Assert.False(s.TelegramSocks); Assert.Equal(vpn,s.TelegramVpnDefault);
        Assert.DoesNotContain(Build(s)["outbounds"]!.AsArray(), n=>n?["tag"]?.ToString()=="telegram");
    }
    [Theory][InlineData(RoutingMode.Global)][InlineData(RoutingMode.Rules)]
    public void TelegramDefaultCoversProcessIpAndDnsInBothModes(RoutingMode mode)
    {
        foreach(var viaVpn in new[]{true,false})
        {
            var config=Build(new(){Mode=mode,TelegramVpnDefault=viaVpn},ProfileImporter.ParseLink("socks://192.0.2.1:1080"));
            var rules=config["route"]!["rules"]!.AsArray(); var target=viaVpn?"vpn":"direct";
            Assert.Equal(target,rules.First(n=>n?["process_name"]?.ToJsonString()=="[\"Telegram.exe\"]")!["outbound"]!.ToString());
            Assert.Equal(target,rules.First(n=>n?["ip_cidr"]?.ToJsonString().Contains("149.154.160.0/20")==true)!["outbound"]!.ToString());
            Assert.Equal(viaVpn?"dns-vpn":"dns-direct",config["dns"]!["rules"]!.AsArray().First(n=>n?["domain_suffix"]?.ToJsonString().Contains("telegram.org")==true)!["server"]!.ToString());
        }
    }
    [Theory][InlineData("Telegram.exe")][InlineData("Discord.exe")][InlineData("NetCat.Telegram.exe")]
    public void UserProcessRulePrecedesServiceDefaultsAndDnsCannotPreemptIt(string process)
    {
        var s=new AppSettings {TelegramSocks=true,Discord=ServiceRoute.Zapret,OpenVpnDomains="telegram.org",
            Rules=[new(){Kind=RuleKind.Process,Value=process,Target=RouteTarget.Vpn},new(){Kind=RuleKind.Domain,Value="telegram.org",Target=RouteTarget.Block}]};
        var c=Build(s,ProfileImporter.ParseLink("socks://192.0.2.1:1080"));var rules=c["route"]!["rules"]!.AsArray();
        Assert.Equal("vpn",rules.First(n=>n?["process_name"] is JsonArray names && names.Any(name=>name?.ToString()==process))!["outbound"]!.ToString());
        Assert.DoesNotContain(c["dns"]!["rules"]!.AsArray(),n=>n?["action"]?.ToString()=="reject");
        Assert.Contains(rules,n=>n?["action"]?.ToString()=="reject"); // Other applications are still blocked by their connection rule.
    }
    [Fact]
    public void UserDomainRulePrecedesCorporateAndBuiltinRulesIncludingDns()
    {
        var c=Build(new(){OpenVpnDomains="telegram.org",TelegramSocks=true,Rules=[new(){Kind=RuleKind.Domain,Value="telegram.org",Target=RouteTarget.Direct}]});
        foreach(var pair in new[]{("route","outbound","direct"),("dns","server","dns-direct")})
            Assert.Equal(pair.Item3,c[pair.Item1]!["rules"]!.AsArray().First(n=>n?["domain_suffix"]?.ToJsonString().Contains("telegram.org")==true)![pair.Item2]!.ToString());
    }
    [Fact]
    public void StandardPresetKeepsExistingRulesAndBlocksAdsBeforeDirectRu()
    {
        var existing=new RoutingRule {Kind=RuleKind.Domain,Value="custom.ru",Target=RouteTarget.Vpn};
        var rules=new List<RoutingRule>{existing}; rules.AddRange(RoutingPreset.MissingStandardRules(rules));
        Assert.Same(existing,rules[0]);Assert.Equal("category-ads-all",rules[1].Value);Assert.Equal(RouteTarget.Block,rules[1].Target);
        Assert.Equal("category-ru",rules[2].Value);Assert.Equal(RouteTarget.Direct,rules[2].Target);
        Assert.Empty(RoutingPreset.MissingStandardRules(rules));
        rules[2].Enabled=false;rules[2].Target=RouteTarget.Vpn;Assert.Empty(RoutingPreset.MissingStandardRules(rules));
        var database=new Geodata(Geodata.FilePath(Path.Combine(RoutingTests.FindRoot(),"bin"),RuleKind.GeoSite),false);
        Assert.True(database.Contains("category-ads-all","doubleclick.net"));Assert.True(database.Contains("category-ru","yandex.ru"));
        Assert.NotNull(Build(new(){Rules=rules}));
    }
}
