using NetCat.Engine;
using System.Text.Json.Nodes;
using Xunit;

namespace NetCat.Tests;
public sealed class Candidate27EvidenceTests
{
    [Fact] public async Task RuntimeStructureExporterPreservesSeparationWithoutLiteralSecrets()
    {
        var root=Path.Combine(Path.GetTempPath(),"NetCat-C27-evidence-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var input=Path.Combine(root,"router.json");var output=Path.Combine(root,"structure.json");
            var explicitSecret=Guid.NewGuid().ToString("N");var ownedSecret=Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(input,new JsonObject{["outbounds"]=new JsonArray(new JsonObject{["tag"]="openvpn",["password"]=explicitSecret,["server"]="private.example"},new JsonObject{["tag"]="openvpn-owned",["password"]=ownedSecret}),["inbounds"]=new JsonArray()}.ToJsonString());
            using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(10));var script=Path.Combine(RoutingTests.FindRoot(),"scripts","verification","Export-RuntimeStructure.ps1");
            var result=await ProcessHost.RunAsync(ProcessHost.PowerShellPath,["-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",script,"-InputFile",input,"-OutputFile",output],ct.Token);
            Assert.Equal(0,result.Code);var text=await File.ReadAllTextAsync(output);var proof=JsonNode.Parse(text)!;
            Assert.True((bool)proof["ExplicitCredentialPresent"]!);Assert.True((bool)proof["OwnedCredentialPresent"]!);Assert.True((bool)proof["CredentialsDistinct"]!);
            Assert.DoesNotContain(explicitSecret,text+result.Output);Assert.DoesNotContain(ownedSecret,text+result.Output);Assert.DoesNotContain("private.example",text+result.Output);Assert.False((bool)proof["LiteralSecretsExported"]!);
            Assert.Contains(explicitSecret,await File.ReadAllTextAsync(input)); // Source evidence is untouched.
        }
        finally{Directory.Delete(root,true);}
    }
}
