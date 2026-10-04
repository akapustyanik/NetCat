using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32TrojanGrpcUdpTests
{
    private static Profile Trojan(string transport="grpc") => new()
    {
        Protocol="trojan",Core="Xray",Host="vpn.example.invalid",Port=443,
        OutboundJson=new JsonObject
        {
            ["type"]="trojan",["server"]="vpn.example.invalid",["server_port"]=443,["password"]="synthetic-password",
            ["tls"]=new JsonObject{["enabled"]=true,["server_name"]="vpn.example.invalid",["insecure"]=false,["alpn"]=new JsonArray("h2")},
            ["transport"]=new JsonObject{["type"]=transport,["service_name"]="synthetic-grpc",["path"]="/synthetic"}
        }.ToJsonString()
    };
    private static JsonNode Outbound(Profile profile)=>XrayConfig.Build(profile,19080,"192.0.2.10")["outbounds"]![0]!;

    [Fact] public void TrojanGrpcUdpMultiplexingDoesNotMultiplexTcp()
    {
        var outbound=Outbound(Trojan());
        Assert.True(outbound["mux"]?["enabled"]?.GetValue<bool>());
        Assert.Equal(-1,outbound["mux"]?["concurrency"]?.GetValue<int>());
        Assert.Equal(16,outbound["mux"]?["xudpConcurrency"]?.GetValue<int>());
    }
    [Fact] public void TrojanGrpcUdpMultiplexingKeepsUdp443Allowed()
        =>Assert.Equal("allow",Outbound(Trojan())["mux"]?["xudpProxyUDP443"]?.ToString());

    [Fact] public void TrojanGrpcUdpMultiplexingPreservesAuthenticationTlsAndPhysicalBinding()
    {
        var outbound=Outbound(Trojan());
        Assert.NotNull(outbound["mux"]);
        Assert.Equal("synthetic-password",outbound["settings"]!["servers"]![0]!["password"]!.ToString());
        Assert.Equal("vpn.example.invalid",outbound["settings"]!["servers"]![0]!["address"]!.ToString());
        Assert.Equal("192.0.2.10",outbound["sendThrough"]!.ToString());
        Assert.Equal("tls",outbound["streamSettings"]!["security"]!.ToString());
        Assert.False(outbound["streamSettings"]!["tlsSettings"]!["allowInsecure"]!.GetValue<bool>());
        Assert.Equal("h2",outbound["streamSettings"]!["tlsSettings"]!["alpn"]![0]!.ToString());
        Assert.Equal("synthetic-grpc",outbound["streamSettings"]!["grpcSettings"]!["serviceName"]!.ToString());
    }
    [Fact] public void ExplicitTrojanGrpcMuxPolicyIsPreserved()
    {
        var profile=Trojan();var raw=Outbound(profile).DeepClone();
        raw["mux"]=new JsonObject{["enabled"]=false,["concurrency"]=8};profile.OutboundJson=raw.ToJsonString();
        Assert.Equal(raw["mux"]!.ToJsonString(),Outbound(profile)["mux"]!.ToJsonString());
    }
    [Theory]
    [InlineData("trojan","ws")]
    [InlineData("vless","grpc")]
    [InlineData("vmess","grpc")]
    public void OtherProtocolsAndTransportsDoNotReceiveImplicitUdpMultiplexing(string protocol,string transport)
    {
        var profile=Trojan(transport);profile.Protocol=protocol;
        var json=JsonNode.Parse(profile.OutboundJson)!;json["type"]=protocol;json["uuid"]="00000000-0000-4000-8000-000000000001";profile.OutboundJson=json.ToJsonString();
        Assert.Null(Outbound(profile)["mux"]);
    }
    [Fact] public async Task BundledXrayAcceptsTrojanGrpcUdpOnlyMultiplexing()
    {
        var config=XrayConfig.Build(Trojan(),19080,"192.0.2.10");
        Assert.NotNull(config["outbounds"]![0]!["mux"]);
        var file=Path.Combine(Path.GetTempPath(),"NetCat-trojan-grpc-udp-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            await File.WriteAllTextAsync(file,config.ToJsonString());using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result=await ProcessHost.RunAsync(Path.Combine(RoutingTests.ModuleRoot,"xray","xray.exe"),["run","-test","-c",file],deadline.Token);
            Assert.True(result.Code==0,result.Output);
        }
        finally{File.Delete(file);}
    }
}
