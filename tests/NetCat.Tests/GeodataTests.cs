using System.Text;
using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Updater;
using Xunit;

namespace NetCat.Tests;
public sealed class GeodataTests
{
    [Theory]
    [InlineData("202609122339", "202609112339", true)]
    [InlineData("202609122339", "202609122339", false)]
    [InlineData("202609122339", "202610010001", false)]
    [InlineData("202609122339", "Не установлен", true)]
    public void DateVersionsAreComparedChronologically(string remote, string installed, bool expected)
        => Assert.Equal(expected, ModuleUpdater.IsNewer(remote, installed));

    [Theory]
    [InlineData("geosite:category-ru", RuleKind.GeoSite, "category-ru")]
    [InlineData("geoip:RU", RuleKind.GeoIp, "ru")]
    [InlineData("geosite:google@cn", RuleKind.GeoSite, "google@cn")]
    public void PastedPrefixesSelectGeodataKind(string text, RuleKind kind, string category)
    {
        var rule = new RoutingRule { Kind = RuleKind.Domain, Value = text };
        RuleValidation.Validate(rule); Assert.Equal(kind, rule.Kind); Assert.Equal(category, rule.Value);
    }
    [Fact]
    public void ProtobufPreservesDomainTypesAttributesAndRejectsTruncation()
    {
        byte[] Field(int number, byte[] content) => [(byte)(number * 8 + 2), (byte)content.Length, ..content];
        byte[] Text(int number, string text) => Field(number, Encoding.UTF8.GetBytes(text));
        byte[] Domain(int type, string text, bool attributed = false) => Field(2, [8, (byte)type, ..Text(2, text), ..(attributed ? Field(3, Text(1, "cn")) : [])]);
        var data = Field(1, [..Text(1, "test"), ..Domain(0, "keyword"), ..Domain(1, "^regex"), ..Domain(2, "suffix.test", true), ..Domain(3, "exact.test")]);
        var file = Path.Combine(Path.GetTempPath(), "netcat-geo-" + Guid.NewGuid() + ".dat");
        try
        {
            File.WriteAllBytes(file, data); var geo = new Geodata(file, false); var all = geo.Match("test");
            Assert.Equal(4, all.Count); Assert.True(geo.Contains("test", "a.suffix.test")); Assert.False(geo.Contains("test", "a.exact.test"));
            var filtered = geo.Match("test@cn"); Assert.Single(filtered); Assert.Equal("suffix.test", filtered["domain_suffix"]![0]!.ToString());
            Assert.Throws<InvalidDataException>(() => geo.Match("missing")); Assert.Throws<InvalidDataException>(() => geo.Match("test@missing"));
            File.WriteAllBytes(file, data[..^1]); Assert.Throws<InvalidDataException>(() => new Geodata(file, false));
        }
        finally { File.Delete(file); }
    }
    [Fact]
    public async Task BundledDatabasesProduceValidNativeRoutingAndDnsRules()
    {
        var root = RoutingTests.FindRoot(); var bin = Path.Combine(root, "bin");
        var site = new Geodata(Geodata.FilePath(bin, RuleKind.GeoSite), false);
        var ip = new Geodata(Geodata.FilePath(bin, RuleKind.GeoIp), true);
        Assert.True(site.Contains("youtube", "www.youtube.com")); Assert.False(site.Contains("youtube", "youtube.com.evil.example"));
        Assert.NotEmpty(site.Match("category-ru")); Assert.NotEmpty(site.Match("category-ads-all"));
        Assert.True(ip.Contains("telegram", "149.154.167.51")); Assert.False(ip.Contains("telegram", "192.168.1.1"));
        var settings = new AppSettings { Mode = RoutingMode.Global, OpenVpnDomains = "office.example" };
        settings.Rules.AddRange([
            new() { Kind = RuleKind.GeoSite, Value = "category-ru", Target = RouteTarget.Direct },
            new() { Kind = RuleKind.GeoSite, Value = "category-ads-all", Target = RouteTarget.Block },
            new() { Kind = RuleKind.GeoIp, Value = "ru", Target = RouteTarget.Direct }
        ]);
        var profile = ProfileImporter.ParseLink("socks://192.0.2.1:1080#test");
        var config = SingBoxConfig.Build(settings, new("Ethernet", 1, "192.168.1.2", "192.168.1.1", []), profile, null, false, geodataDirectory: bin);
        var routes = config["route"]!["rules"]!.AsArray();
        Assert.Contains(routes, r => r?["ip_cidr"] is JsonArray a && a.Count > 100 && r?["outbound"]?.ToString() == "direct");
        Assert.Contains(config["dns"]!["rules"]!.AsArray(), r => r?["action"]?.ToString() == "reject");
        Assert.DoesNotContain("\"geosite\"", config.ToJsonString());
        var file = Path.Combine(root, "artifacts", "geodata-check", "native-config.json"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, config.ToJsonString(JsonSettings.Options));
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await ProcessHost.RunAsync(Path.Combine(bin, "sing-box", "sing-box.exe"), ["check", "-c", file], ct.Token);
        Assert.True(result.Code == 0, result.Output);
    }
    [Fact]
    public void PackageGeodataHasIndependentOwnershipAndVersionSelection()
    {
        var manifest = new PackageManifest(1, "0.5.0", [
            new("netcat", "0.5.0", [new("NetCat.exe", "")]),
            new("geoip", "202609122339", [new("modules/geoip/geoip.dat", "")]),
            new("geosite", "202609122339", [new("modules/geosite/geosite.dat", "")])
        ]);
        var plan = PortableUpdate.Plan(manifest, key => key == "geoip" ? "202609132339" : key == "netcat" ? "0.5.1" : "202609112339", new HashSet<string>());
        Assert.Equal("geosite", Assert.Single(plan).Key);
        Assert.True(PortableUpdate.Owns("geosite", "modules/geosite/geosite.dat")); Assert.False(PortableUpdate.Owns("xray", "modules/geosite/geosite.dat"));
        Assert.Empty(PortableUpdate.Plan(manifest, key => key == "netcat" ? "0.5.1" : "202609112339", new HashSet<string> { "geoip", "geosite" }));
    }
}
