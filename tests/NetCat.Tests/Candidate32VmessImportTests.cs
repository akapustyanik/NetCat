using System.Text;
using System.Text.Json.Nodes;
using NetCat.Engine;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32VmessImportTests
{
    private static string Link(string? cipher)
    {
        var value = new JsonObject
        {
            ["v"] = "2", ["ps"] = "VMess fixture", ["add"] = "vpn.example",
            ["port"] = "443", ["id"] = "00000000-0000-4000-8000-000000000001",
            ["aid"] = "0", ["net"] = "ws", ["host"] = "vpn.example",
            ["path"] = "/fixture", ["tls"] = "tls", ["sni"] = "vpn.example"
        };
        if (cipher != null) value["scy"] = cipher;
        return "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value.ToJsonString()));
    }

    [Theory]
    [InlineData("aes-128-gcm")]
    [InlineData("chacha20-poly1305")]
    [InlineData("none")]
    [InlineData("auto")]
    public void VmessShareLinkPreservesExplicitCipherWithTls(string cipher)
    {
        var imported = ProfileImporter.ParseForImport(Link(cipher));
        Assert.Empty(imported.Errors);
        var profile = Assert.Single(imported.Profiles);
        var outbound = JsonNode.Parse(profile.OutboundJson)!;
        Assert.Equal(cipher, outbound["security"]!.GetValue<string>());
        Assert.Equal("sing-box", profile.Core);
        Assert.True(outbound["tls"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("vpn.example", outbound["tls"]!["server_name"]!.GetValue<string>());
        Assert.Null(outbound["tls"]!["insecure"]);
        Assert.Equal("/fixture", outbound["transport"]!["path"]!.GetValue<string>());
        Assert.Equal(0, outbound["alter_id"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void LegacyVmessShareLinkWithoutCipherRetainsAuto(string? cipher)
    {
        var profile = ProfileImporter.ParseLink(Link(cipher));
        Assert.Equal("auto", JsonNode.Parse(profile.OutboundJson)!["security"]!.GetValue<string>());
    }

    [Fact]
    public void VmessTlsShareLinkPreservesAlpnFingerprintAndValidationFlag()
    {
        var value = new JsonObject
        {
            ["v"] = "2", ["add"] = "vpn.example", ["port"] = "443",
            ["id"] = "00000000-0000-4000-8000-000000000001", ["aid"] = "0",
            ["net"] = "ws", ["host"] = "vpn.example", ["path"] = "/vmess",
            ["tls"] = "tls", ["sni"] = "vpn.example", ["scy"] = "aes-128-gcm",
            ["alpn"] = "h2,http/1.1", ["fp"] = "chrome", ["allowInsecure"] = "1"
        };
        var profile = ProfileImporter.ParseLink("vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value.ToJsonString())));
        var outbound = JsonNode.Parse(profile.OutboundJson)!;
        Assert.Equal("aes-128-gcm", outbound["security"]!.GetValue<string>());
        Assert.Equal("chrome", outbound["tls"]!["utls"]!["fingerprint"]!.GetValue<string>());
        Assert.True(outbound["tls"]!["insecure"]!.GetValue<bool>());
        Assert.Equal(new[] { "h2", "http/1.1" }, outbound["tls"]!["alpn"]!.AsArray().Select(x => x!.GetValue<string>()));
    }
}
