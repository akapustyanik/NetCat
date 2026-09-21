using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;
public sealed class Build06Tests
{
    private static readonly NetworkSnapshot Net = new("Ethernet",1,"192.168.1.10","192.168.1.1",["corp.example"]);
    private static Profile Trojan() => ProfileImporter.ParseLink("trojan://test-password@vpn.example.com:443?type=grpc&security=tls&serviceName=%2F123%2Fcustom&sni=vpn.example.com#Trojan");
    [Fact]
    public void TrojanImportPreservesCustomGrpcPathAndUsesXray()
    {
        var profile = Trojan();
        Assert.Equal("Xray",profile.Core);
        var xray = XrayConfig.Build(profile,19080,Net.Address);
        Assert.Equal("/123/custom",xray["outbounds"]![0]!["streamSettings"]!["grpcSettings"]!["serviceName"]!.ToString());
        Assert.Equal("test-password",xray["outbounds"]![0]!["settings"]!["servers"]![0]!["password"]!.ToString());
    }
    [Fact]
    public void LegacyGrpcCanBeMatchedAgainstSubscriptionWithoutGuessingPath()
    {
        var incoming=Trojan(); var old=JsonSettings.Clone(incoming);
        var json=JsonNode.Parse(old.OutboundJson)!; json["transport"]!["service_name"]="123/custom"; old.OutboundJson=json.ToJsonString(); old.Core="sing-box";
        Assert.NotEqual(ProfileIdentity.Key(old),ProfileIdentity.Key(incoming));
        Assert.True(ProfileIdentity.MatchesLegacyGrpc(old,incoming));
        old.Host="different.example"; Assert.False(ProfileIdentity.MatchesLegacyGrpc(old,incoming));
    }
    [Theory]
    [InlineData(RoutingMode.Rules,RoutingMode.Rules)]
    [InlineData(RoutingMode.SelectiveVpn,RoutingMode.Rules)]
    [InlineData(RoutingMode.Global,RoutingMode.Global)]
    [InlineData(RoutingMode.SelectiveDirect,RoutingMode.Global)]
    public void ExistingSettingsMigrateWithoutLosingProfileIdentity(RoutingMode old,RoutingMode expected)
    {
        var profile=Trojan(); profile.Core="sing-box";
        var settings=new AppSettings {Mode=old,Profiles=[profile],MainProfileId=profile.Id};
        SettingsMigration.Apply(settings); SettingsMigration.Apply(settings);
        Assert.Equal(expected,settings.Mode); Assert.Equal(profile.Id,settings.MainProfileId); Assert.Equal("Xray",profile.Core);
    }
    [Theory]
    [InlineData(RoutingMode.Rules,"direct")]
    [InlineData(RoutingMode.Global,"vpn")]
    public void TwoModesHonorExplicitExceptionsAndDefineUnmatchedTraffic(RoutingMode mode,string fallback)
    {
        var s=new AppSettings {Mode=mode,Fallback=RouteTarget.Block,Rules=[new() {Kind=RuleKind.Domain,Value="corp.example",Target=RouteTarget.Direct},new() {Kind=RuleKind.Process,Value="browser.exe",Target=RouteTarget.Vpn}]};
        var config=SingBoxConfig.Build(s,Net,Trojan(),null,true,xrayPort:19081);
        Assert.Equal(fallback,config["route"]!["final"]!.ToString());
        var rule=config["route"]!["rules"]!.AsArray().First(n=>n?["domain_suffix"]?.ToJsonString().Contains("corp.example")==true)!;
        Assert.Equal("direct",rule["outbound"]!.ToString());
        Assert.Equal("dns-direct",config["dns"]!["rules"]!.AsArray().First(n=>n?["domain_suffix"]?.ToJsonString().Contains("corp.example")==true)!["server"]!.ToString());
        Assert.Contains(config["route"]!["rules"]!.AsArray(),n=>n?["process_name"]?.ToJsonString().Contains("browser.exe")==true && n?["outbound"]?.ToString()=="vpn");
        Assert.Equal("address_and_port_dependent",config["inbounds"]!.AsArray().First(n=>n?["type"]?.ToString()=="tun")!["udp_mapping"]!.ToString());
    }
    [Fact]
    public async Task XrayAcceptsIndependentBootstrapDnsAndCustomGrpc()
    {
        var config=XrayConfig.Build(Trojan(),19080,Net.Address);
        Assert.Equal("ForceIPv4",config["outbounds"]![0]!["streamSettings"]!["sockopt"]!["domainStrategy"]!.ToString());
        Assert.Equal("dns-direct",config["routing"]!["rules"]![0]!["outboundTag"]!.ToString());
        Assert.Equal(Net.Address,config["outbounds"]![1]!["sendThrough"]!.ToString());
        var file=Path.Combine(RoutingTests.FindRoot(),"artifacts","validation","xray06.json"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file,config.ToJsonString());
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result=await ProcessHost.RunAsync(Path.Combine(RoutingTests.FindRoot(),"bin/xray/xray.exe"),["run","-test","-c",file],timeout.Token);
        Assert.True(result.Code==0,result.Output);
    }
    [Fact]
    public async Task SwitchingDoesNotEnableStoppedVpn()
    {
        using var router=new RouterService(Path.Combine(RoutingTests.FindRoot(),"bin"),Path.Combine(RoutingTests.FindRoot(),"artifacts","switch-stopped"));
        await router.SwitchProfileAsync(new AppSettings {Profiles=[Trojan()]});
        Assert.False(router.VpnRequested); Assert.False(router.Running);
    }
}
