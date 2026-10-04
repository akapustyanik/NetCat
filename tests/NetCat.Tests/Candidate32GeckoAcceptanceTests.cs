using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32GeckoAcceptanceTests
{
    [Theory]
    [InlineData("gecko")]
    [InlineData("Gecko")]
    [InlineData("salamander")]
    public async Task HysteriaObfuscationUriPreservesRequestedTypeAndNativeCoreAcceptsIt(string type)
    {
        var imported=ProfileImporter.ParseForImport("hy2://synthetic-auth@192.0.2.23:40005?sni=hy2.example.com&obfs="+type+"&obfs-password=synthetic%3Aobfuscation");
        Assert.Empty(imported.Errors);var profile=Assert.Single(imported.Profiles);
        var outbound=JsonNode.Parse(profile.OutboundJson)!;
        Assert.Equal(type.ToLowerInvariant(),outbound["obfs"]?["type"]?.ToString());
        Assert.Equal("synthetic:obfuscation",outbound["obfs"]?["password"]?.ToString());
        var config=SingBoxConfig.Build(new AppSettings{Tun=true,Mode=RoutingMode.Global},new("Ethernet",1,"192.0.2.10","192.0.2.1",[]),profile,null,true);
        var file=Path.Combine(Path.GetTempPath(),"NetCat-gecko-check-"+Guid.NewGuid().ToString("N")+".json");
        try{
            await File.WriteAllTextAsync(file,config.ToJsonString());using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result=await ProcessHost.RunAsync(Path.Combine(RoutingTests.ModuleRoot,"sing-box","sing-box.exe"),["check","-c",file],ct.Token);
            Assert.True(result.Code==0,result.Output);
        }finally{File.Delete(file);}
    }

    [Theory]
    [InlineData("obfs=unknown&obfs-password=synthetic")]
    [InlineData("obfs=gecko")]
    [InlineData("obfs=salamander&obfs-password=")]
    [InlineData("obfs-password=synthetic")]
    public void UnsupportedOrIncompleteHysteriaObfuscationCannotSilentlyBecomePlain(string query)
    {
        var imported=ProfileImporter.ParseForImport("hy2://synthetic-auth@192.0.2.23:40005?"+query);
        Assert.Empty(imported.Profiles);Assert.NotEmpty(imported.Errors);
    }
}
