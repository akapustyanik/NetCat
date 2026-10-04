using System.Reflection;
using NetCat.Core;
using NetCat.Network;
using Xunit;
namespace NetCat.Tests;
public sealed class Candidate20LanTests
{
    private static void Physical(OpenVpnService service,Func<IReadOnlyList<string>> capture)=>typeof(OpenVpnService).GetProperty(nameof(OpenVpnService.CapturePhysicalPrefixes))!.SetValue(service,capture);
    private static async Task Reject(bool direct)
    {
        await using var f=new Candidate19StateTests.Fixture();Physical(f.Service,()=>["10.1.2.3/24"]);int installs=0;
        var run=f.Service.PowerShellOverride!;f.Service.PowerShellOverride=(command,ct)=>{if(command.StartsWith("New-NetRoute"))installs++;return run(command,ct);};
        await f.Base.Repo.UpdateSettingsAsync(s=>{s.Profiles.First(p=>p.Id==f.Base.O.Id).OpenVpnConfig="client\ndev tun\n";if(direct)s.Rules.Add(new(){Kind=RuleKind.IpCidr,Value="10.1.0.0/16",Target=RouteTarget.Direct});return s;});
        var profile=f.Base.Repo.CurrentSettings.Profiles.First(p=>p.Id==f.Base.O.Id);
        f.Append("PUSH_REPLY,route 10.1.0.0 255.255.0.0,route-gateway 10.1.0.1,dhcp-option DNS 10.1.0.53","Initialization Sequence Completed");
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>f.Service.StartAsync(profile,"",CancellationToken.None));
        Assert.Contains("10.1.0.0/16",error.Message);Assert.Equal(0,installs);Assert.Null(f.Service.Link);Assert.False(f.Service.IsRunning);Assert.Empty(f.Routes);
    }

    [Fact] public Task DirectRuleDoesNotOverrideOpenVpnServiceLanOverlapAdmission()=>Reject(true);
    [Fact] public Task OverlapPolicyRunsBeforeRouteInstallation()=>Reject(false);
    [Fact] public async Task UnrelatedLanRemainsReachable()
    {await using var f=new Candidate19StateTests.Fixture();Physical(f.Service,()=>["192.168.1.2/24"]);await f.Start();Assert.True(f.Service.IsRunning);Assert.DoesNotContain(f.Routes,r=>r.DestinationPrefix.StartsWith("192.168."));f.Stable();}
    [Fact] public async Task PhysicalFailoverReevaluatesOverlap()
    {await using var f=new Candidate19StateTests.Fixture();string prefix="192.168.1.2/24";Physical(f.Service,()=>[prefix]);await f.Start();prefix="10.1.2.3/24";await Assert.ThrowsAsync<InvalidDataException>(()=>f.Service.EnsureRoutesAsync(CancellationToken.None));Assert.Null(f.Service.Link);f.Stable();}
}
