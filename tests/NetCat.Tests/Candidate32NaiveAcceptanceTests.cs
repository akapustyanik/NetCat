using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32NaiveAcceptanceTests
{
    [Theory]
    [InlineData(RoutingMode.Global, "192.0.2.23")]
    [InlineData(RoutingMode.Rules, "[2001:db8::23]")]
    public async Task NaiveH2UotShareLinkProducesConfigurationAcceptedByNativeCore(RoutingMode mode, string endpoint)
    {
        var imported = ProfileImporter.ParseForImport($"naive+https://synthetic%3Auser:synthetic%3Apassword%40x@{endpoint}:40004?sni=naive.example.com&udp_over_tcp=true#Naive-H2");
        Assert.Empty(imported.Errors); var profile = Assert.Single(imported.Profiles);
        Assert.Equal("naive", profile.Protocol); Assert.Equal("sing-box", profile.Core);
        var settings = new AppSettings { Mode=mode, Tun=true, Profiles=[profile], MainProfileId=profile.Id };
        var config = SingBoxConfig.Build(settings, new("Ethernet",1,"192.0.2.10","192.0.2.1",[]),profile,null,true);
        var outbound = Assert.Single(config["outbounds"]!.AsArray().OfType<JsonObject>(), n=>n["type"]?.ToString()=="naive");
        Assert.Equal("synthetic:user",outbound["username"]!.ToString()); Assert.Equal("synthetic:password@x",outbound["password"]!.ToString());
        Assert.True(outbound["udp_over_tcp"]!.GetValue<bool>()); Assert.False(outbound["quic"]!.GetValue<bool>());
        Assert.True(outbound["tls"]!["enabled"]!.GetValue<bool>()); Assert.Null(outbound["tls"]!["insecure"]);
        Assert.Equal("naive.example.com",outbound["tls"]!["server_name"]!.ToString());
        var file=Path.Combine(Path.GetTempPath(),"NetCat-naive-check-"+Guid.NewGuid().ToString("N")+".json");
        try {
            await File.WriteAllTextAsync(file,config.ToJsonString());
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var check=await ProcessHost.RunAsync(Path.Combine(RoutingTests.ModuleRoot,"sing-box","sing-box.exe"),["check","-c",file],stop.Token);
            Assert.True(check.Code==0,check.Output);
        } finally {File.Delete(file);}
    }

    [Theory]
    [InlineData("naive+http://u:p@192.0.2.23:40004")]
    [InlineData("naive://u:p@192.0.2.23:40004")]
    [InlineData("naive+https://u:p@192.0.2.23:40004?insecure=true")]
    [InlineData("naive+https://u:p@192.0.2.23:40004?fp=chrome")]
    [InlineData("naive+https://u:p@192.0.2.23:40004?udp_over_tcp=garbage")]
    [InlineData("naive+https://u@192.0.2.23:40004")]
    public void IncompatibleNaiveLinksFailImportWithoutFallback(string link)
    {
        var imported=ProfileImporter.ParseForImport(link); Assert.Empty(imported.Profiles); Assert.NotEmpty(imported.Errors);
        Assert.DoesNotContain("u:p@",string.Join(" ",imported.Errors));
    }

    [Fact]
    public async Task NativeNaiveJsonPreservesUotAndCertificatePolicyWithoutMutatingInput()
    {
        const string json="""{"type":"naive","server":"192.0.2.23","server_port":40004,"username":"synthetic","password":"synthetic","udp_over_tcp":{"enabled":true,"version":2},"tls":{"enabled":true,"server_name":"naive.example.com"}}""";
        var profile=Assert.Single(ProfileImporter.ParseForImport(json).Profiles);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json),JsonNode.Parse(profile.OutboundJson)));
        var settings=new AppSettings{Profiles=[profile]};
        var folder=Path.Combine(Path.GetTempPath(),"NetCat-naive-settings-"+Guid.NewGuid().ToString("N"));
        try {
            var store=new SettingsStore(folder);await store.SaveAsync(settings);
            Assert.Equal(profile.OutboundJson,Assert.Single(store.Load().Profiles).OutboundJson);
            Assert.DoesNotContain("naive.example.com",System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(folder,"settings.dpapi"))));
        }finally{Directory.Delete(folder,true);}
    }

    [Theory]
    [InlineData("naive+https://synthetic:credential@naive.example.com:40004")]
    [InlineData("NAIVE://synthetic:credential@naive.example.com:40004")]
    public void NaiveUrisAreFullyRedacted(string uri)
    {
        var redacted=ProcessHost.Redact("dial "+uri+" failed"); Assert.DoesNotContain("credential",redacted); Assert.DoesNotContain("naive.example.com",redacted);Assert.DoesNotContain("naive+",redacted);
    }

    [Theory]
    [InlineData("insecure", "true")]
    [InlineData("alpn", "[\"h2\"]")]
    [InlineData("utls", "{\"enabled\":true}")]
    public void UnsupportedNativeNaiveTlsCannotSilentlyImport(string key,string value)
    {
        var imported=ProfileImporter.ParseForImport("{\"type\":\"naive\",\"server\":\"192.0.2.23\",\"server_port\":40004,\"username\":\"synthetic\",\"password\":\"synthetic\",\"tls\":{\"enabled\":true,\""+key+"\":"+value+"}}");
        Assert.Empty(imported.Profiles);Assert.NotEmpty(imported.Errors);
    }
}
