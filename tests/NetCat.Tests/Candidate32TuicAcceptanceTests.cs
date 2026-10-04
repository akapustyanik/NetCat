using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32TuicAcceptanceTests
{
    [Theory]
    [InlineData(RoutingMode.Global,"192.0.2.23")]
    [InlineData(RoutingMode.Rules,"[2001:db8::23]")]
    public async Task TuicShareLinkProducesConfigurationAcceptedByBundledNativeCore(RoutingMode mode,string endpoint)
    {
        const string uuid="8a8288e3-9022-4b14-991f-877349b0b103";
        // Reserved documentation endpoints and synthetic authentication only.
        var imported=ProfileImporter.ParseForImport($"tuic://{uuid}:synthetic%3Apassword@{endpoint}:40003?sni=tuic.example.com&alpn=h3&congestion_control=bbr#TUIC-test");
        Assert.Empty(imported.Errors);var profile=Assert.Single(imported.Profiles);
        Assert.Equal("tuic",profile.Protocol);Assert.Equal("sing-box",profile.Core);
        var settings=new AppSettings{Mode=mode,Tun=true,Profiles=[profile],MainProfileId=profile.Id};
        settings.Rules.Add(new(){Kind=RuleKind.Domain,Value="app.example.com",Target=RouteTarget.Vpn});
        var config=SingBoxConfig.Build(settings,new("Ethernet",1,"192.0.2.10","192.0.2.1",[]),profile,null,true);
        var outbound=Assert.Single(config["outbounds"]!.AsArray().OfType<JsonObject>(),o=>o["type"]?.ToString()=="tuic");
        Assert.Equal(uuid,outbound["uuid"]!.ToString());Assert.Equal("synthetic:password",outbound["password"]!.ToString());
        Assert.Equal("bbr",outbound["congestion_control"]!.ToString());
        Assert.Equal("tuic.example.com",outbound["tls"]!["server_name"]!.ToString());
        Assert.Equal("h3",Assert.Single(outbound["tls"]!["alpn"]!.AsArray())!.ToString());
        Assert.Null(outbound["tls"]!["insecure"]);
        var file=Path.Combine(Path.GetTempPath(),"NetCat-tuic-check-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            await File.WriteAllTextAsync(file,config.ToJsonString());
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result=await ProcessHost.RunAsync(Path.Combine(RoutingTests.ModuleRoot,"sing-box","sing-box.exe"),["check","-c",file],timeout.Token);
            Assert.True(result.Code==0,result.Output);
        }
        finally{File.Delete(file);}
    }
}
