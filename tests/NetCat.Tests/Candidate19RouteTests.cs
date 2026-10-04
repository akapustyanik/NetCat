using NetCat.Core;
using NetCat.Network;
using Xunit;
namespace NetCat.Tests;
public sealed class Candidate19RouteTests
{
    private static readonly OpenVpnLink Link = new("test",42,"10.1.0.2","10.1.0.1","10.1.0.53",["10.2.0.0/16"]);
    private static RouteRow Corporate => new(42,"10.2.0.0/16","10.1.0.1",OpenVpnRouteJournal.OwnedMetric,"NetMgmt","Manual");
    private static RouteRow DnsHost => new(42,"10.1.0.53/32","10.1.0.1",OpenVpnRouteJournal.OwnedMetric,"NetMgmt","Manual");
    [Fact] public void CorrectPrefixWrongGatewayIsNotVerified() => Assert.False(RouteTable.VerifyOpenVpn(Link,[Corporate with {NextHop="10.1.0.9"},DnsHost]).IsVerified);
    [Fact] public void CorrectPrefixWrongInterfaceIsNotVerified() => Assert.False(RouteTable.VerifyOpenVpn(Link,[Corporate with {InterfaceIndex=43},DnsHost]).IsVerified);
    [Fact] public void MissingDnsHostDefaultIsNotVerified() => Assert.False(RouteTable.VerifyOpenVpn(Link,[Corporate]).IsVerified);
    [Fact] public void WrongDnsHostMetricIsNotVerified() => Assert.False(RouteTable.VerifyOpenVpn(Link,[Corporate,DnsHost with {RouteMetric=1}]).IsVerified);
    [Fact] public void ExactRoutesAreVerified() => Assert.True(RouteTable.VerifyOpenVpn(Link,[Corporate,DnsHost]).IsVerified);
    [Fact] public async Task HungRouteCleanupTimesOut()
    {
        var cancelled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAsync<TimeoutException>(()=>OpenVpnRouteJournal.RunBounded(async (_,ct)=>
        {try{await Task.Delay(Timeout.InfiniteTimeSpan,ct);return (0,"");}finally{cancelled.TrySetResult();}},"test",default,TimeSpan.FromMilliseconds(50)));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
    [Fact] public async Task PreExistingEquivalentRouteIsNotOwned()
    {
        var root=Path.Combine(Path.GetTempPath(),"NetCat-C19-preexisting-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var path=Path.Combine(root,"routes.json");int commands=0;
            await OpenVpnRouteJournal.InstallAsync(path,Link,()=>[Corporate,DnsHost],(_,_)=>{commands++;return Task.FromResult((0,""));},default);
            var journal=await OpenVpnRouteJournal.ReadAsync(path);Assert.Empty(journal!.Prefixes);Assert.False(journal.HelperDefault);Assert.Equal(0,commands);
            await OpenVpnRouteJournal.CleanupAsync(path,(_,_)=>{commands++;return Task.FromResult((0,""));});Assert.Equal(0,commands);
        }
        finally {Directory.Delete(root,true);}
    }
}
