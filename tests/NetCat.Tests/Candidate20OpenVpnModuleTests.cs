using System.Text.Json.Nodes;
using NetCat.Engine;
using Xunit;
namespace NetCat.Tests;
public sealed class Candidate20OpenVpnModuleTests
{
    [Fact] public async Task BundledOpenVpnMatchesDeclaredModuleVersion()
    {
        var root=RoutingTests.FindRoot();var manifest=JsonNode.Parse(File.ReadAllText(Path.Combine(RoutingTests.ModuleRoot, "modules.lock.json")))!.AsArray();
        var declared=manifest.Single(n=>n!["key"]!.ToString()=="openvpn")!["version"]!.ToString();
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result=await ProcessHost.RunAsync(Path.Combine(RoutingTests.ModuleRoot, "openvpn/openvpn.exe"),["--version"],stop.Token);
        Assert.Equal(0,result.Code);Assert.StartsWith("OpenVPN "+declared+" ",result.Output.TrimStart());
    }
    [Fact] public async Task BundledOpenVpnRetainsWintunCliContract()
    {
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result=await ProcessHost.RunAsync(Path.Combine(RoutingTests.ModuleRoot, "openvpn/openvpn.exe"),["--help"],stop.Token);
        // OpenVPN intentionally exits 1 after its usage/help path.
        Assert.Equal(1,result.Code);foreach(var option in new[]{"wintun","--route-noexec","--route-nopull","--pull-filter","--management"})Assert.Contains(option,result.Output,StringComparison.OrdinalIgnoreCase);
    }
}
