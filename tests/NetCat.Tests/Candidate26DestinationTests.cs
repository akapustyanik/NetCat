using System.Net;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate26DestinationTests
{
    private static readonly IPAddress Outside=IPAddress.Parse("127.0.0.3");
    [Fact] public async Task NoPermanentOpenVpnDefaultRoute()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();
        await using var lease=await f.Leases.AcquireAsync(f.Leases.Capture(),Outside,true,default);
        Assert.DoesNotContain(f.Capture(),r=>r.DestinationPrefix=="0.0.0.0/0");
        Assert.DoesNotContain(f.Commands,c=>c.Contains("9999"));
        var j=await OpenVpnRouteJournal.ReadAsync(f.Journal);Assert.Equal(4,j!.Schema);Assert.False(j.HelperDefault);
    }
    [Fact] public async Task CorporateDnsGetsExactHostRoute()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();
        Assert.Contains(f.Capture(),r=>OpenVpnRouteJournal.Matches(r,f.Link,f.Link.Dns+"/32",OpenVpnRouteJournal.OwnedMetric));
        var j=await OpenVpnRouteJournal.ReadAsync(f.Journal);Assert.Equal(new[]{f.Link.Dns+"/32"},j!.DnsPrefixes);Assert.Empty(j.DestinationPrefixes);
    }
    [Fact] public async Task PushedPrefixNeedsNoDynamicLease()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();int count=f.Commands.Count;
        await using var lease=await f.Leases.AcquireAsync(f.Leases.Capture(),IPAddress.Parse("127.0.0.2"),false,default);
        Assert.Equal(count,f.Commands.Count);Assert.Empty((await OpenVpnRouteJournal.ReadAsync(f.Journal))!.DestinationPrefixes);
    }
    [Fact] public async Task LiteralIpOutsidePushGetsExactLease()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();
        await using var lease=await f.Leases.AcquireAsync(f.Leases.Capture(),Outside,true,default);
        Assert.Contains(f.Capture(),r=>OpenVpnRouteJournal.Matches(r,f.Link,"127.0.0.3/32",OpenVpnRouteJournal.OwnedMetric));
        Assert.Equal(new[]{"127.0.0.3/32"},(await OpenVpnRouteJournal.ReadAsync(f.Journal))!.DestinationPrefixes);
    }
    [Fact] public async Task ConcurrentSameIpLeaseIsReferenceCounted()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();var g=f.Leases.Capture();
        var refs=await Task.WhenAll(Enumerable.Range(0,24).Select(_=>f.Leases.AcquireAsync(g,Outside,true,default)));
        Assert.Equal(24,f.Leases.ReferenceCount(g,Outside));Assert.Single(f.Capture(),r=>r.DestinationPrefix==Outside+"/32");
        foreach(var r in refs[..^1])await r.DisposeAsync();Assert.Equal(1,f.Leases.ReferenceCount(g,Outside));Assert.Contains(f.Capture(),r=>r.DestinationPrefix==Outside+"/32");
        await refs[^1].DisposeAsync();Assert.Equal(0,f.Leases.ReferenceCount(g,Outside));Assert.DoesNotContain(f.Capture(),r=>r.DestinationPrefix==Outside+"/32");
    }
    [Fact] public async Task OwnedChannelCannotAllocateOutsidePush()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();int count=f.Commands.Count;
        await Assert.ThrowsAsync<IOException>(()=>f.Leases.AcquireAsync(f.Leases.Capture(),Outside,false,default));Assert.Equal(count,f.Commands.Count);
    }
    [Fact] public async Task OpenVpnEndpointDoesNotRouteIntoOwnTunnel()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();f.Leases.Activate(f.Link,()=>true,[Outside]);int count=f.Commands.Count;
        await Assert.ThrowsAsync<IOException>(()=>f.Leases.AcquireAsync(f.Leases.Capture(),Outside,true,default));Assert.Equal(count,f.Commands.Count);
    }
    [Fact] public async Task GenerationSwitchRemovesOldLeases()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();var old=f.Leases.Capture();var lease=await f.Leases.AcquireAsync(old,Outside,true,default);
        await f.Stop();Assert.Empty(f.Capture());await f.Start(f.Link with{Generation=2});
        await using var current=await f.Leases.AcquireAsync(f.Leases.Capture(),Outside,true,default);await lease.DisposeAsync();
        Assert.True(old.Token.IsCancellationRequested);Assert.Contains(f.Capture(),r=>r.DestinationPrefix==Outside+"/32");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Leases.AcquireAsync(old,Outside,true,default));
    }
    [Fact] public async Task DesiredOffRemovesAllDynamicLeases()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();var g=f.Leases.Capture();
        await using var a=await f.Leases.AcquireAsync(g,Outside,true,default);await using var b=await f.Leases.AcquireAsync(g,IPAddress.Parse("127.0.0.5"),true,default);
        await f.Stop();Assert.Empty(f.Capture());Assert.False(File.Exists(f.Journal));Assert.Throws<IOException>(()=>f.Leases.Capture());
    }
    [Fact] public async Task StaleRouteInstallCannotPublishLease()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();var g=f.Leases.Capture();
        f.BeforeCreate=(_,_)=>{f.Leases.Invalidate();return Task.CompletedTask;};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Leases.AcquireAsync(g,Outside,true,default));
        await f.Stop();Assert.Empty(f.Capture());Assert.Equal(0,f.Leases.ReferenceCount(g,Outside));
    }
    [Fact] public async Task RouteInstallFailureFailsClosed()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();f.FailCreate=true;
        await Assert.ThrowsAsync<IOException>(()=>f.Leases.AcquireAsync(f.Leases.Capture(),Outside,true,default));
        Assert.DoesNotContain(f.Capture(),r=>r.DestinationPrefix==Outside+"/32");Assert.Empty((await OpenVpnRouteJournal.ReadAsync(f.Journal))!.DestinationPrefixes);
    }
    [Fact] public async Task ForeignRouteIsNeverDeleted()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();
        var foreign=new RouteRow(f.Link.Index,Outside+"/32",f.Link.Gateway,OpenVpnRouteJournal.OwnedMetric,"NetMgmt","Manual");f.Rows.Add(foreign);
        await using(var lease=await f.Leases.AcquireAsync(f.Leases.Capture(),Outside,true,default)){}
        await f.Stop();Assert.Equal(new[]{foreign},f.Capture());
    }
    [Fact] public async Task ForeignConflictingMetricIsNotOverwritten()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();var foreign=new RouteRow(f.Link.Index,Outside+"/32",f.Link.Gateway,1,"NetMgmt","Manual");f.Rows.Add(foreign);
        await Assert.ThrowsAsync<IOException>(()=>f.Leases.AcquireAsync(f.Leases.Capture(),Outside,true,default));await f.Stop();Assert.Equal(new[]{foreign},f.Capture());
    }
    [Fact] public async Task CorporateDnsRouteRemovedOnOff()
    {await using var f=new Candidate26RouteFixture();await f.Start();await f.Stop();Assert.Empty(f.Capture());Assert.False(File.Exists(f.Journal));}
    [Fact] public async Task PhysicalDefaultMissingDoesNotCreateOpenVpnDefault()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();await using var lease=await f.Leases.AcquireAsync(f.Leases.Capture(),Outside,true,default);
        Assert.All(f.Capture(),r=>Assert.EndsWith("/32",r.DestinationPrefix));Assert.DoesNotContain(f.Capture(),r=>OpenVpnRouteJournal.Covers([r.DestinationPrefix],IPAddress.Parse("203.0.113.9")));
    }
    [Fact] public async Task HighPhysicalMetricCannotMakeOpenVpnSystemDefault()
    {
        await using var f=new Candidate26RouteFixture();var physical=new RouteRow(4242,"0.0.0.0/0","192.0.2.1",40000,"Dhcp","Dhcp");f.Rows.Add(physical);await f.Start();
        await using var lease=await f.Leases.AcquireAsync(f.Leases.Capture(),Outside,true,default);
        Assert.Equal(physical,Assert.Single(f.Capture(),r=>OpenVpnRouteJournal.Covers([r.DestinationPrefix],IPAddress.Parse("203.0.113.9"))));
        await f.Stop();Assert.Equal(new[]{physical},f.Capture());
    }
    [Fact] public async Task RemovedDynamicRouteIsRepairedBeforeNextAcquisition()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();var g=f.Leases.Capture();await using var first=await f.Leases.AcquireAsync(g,Outside,true,default);
        lock(f.Rows)f.Rows.RemoveAll(r=>r.DestinationPrefix==Outside+"/32");await using var second=await f.Leases.AcquireAsync(g,Outside,true,default);
        Assert.Equal(2,f.Leases.ReferenceCount(g,Outside));Assert.Single(f.Capture(),r=>r.DestinationPrefix==Outside+"/32");
    }
    [Fact] public async Task CrashJournalContainsAndCleansStaticAndDynamicRoutes()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();await using var lease=await f.Leases.AcquireAsync(f.Leases.Capture(),Outside,true,default);
        var journal=await OpenVpnRouteJournal.ReadAsync(f.Journal);Assert.NotEmpty(journal!.Prefixes);Assert.NotEmpty(journal.DnsPrefixes);Assert.NotEmpty(journal.DestinationPrefixes);
        f.Leases.Invalidate();await OpenVpnRouteJournal.CleanupAsync(f.Journal,f.Run);Assert.Empty(f.Capture());
    }
    [Fact] public async Task LegacyHelperCrashJournalCanStillBeCleaned()
    {
        await using var f=new Candidate26RouteFixture();var row=new RouteRow(f.Link.Index,"0.0.0.0/0",f.Link.Gateway,9999,"NetMgmt","Manual");f.Rows.Add(row);
        await (OpenVpnRouteJournal.Empty(f.Link) with{Schema=3,HelperDefault=true}).SaveAsync(f.Journal,default);
        await f.Stop();Assert.Empty(f.Capture());
    }
    [Fact] public async Task InstalledRouteIsRolledBackWhenGenerationExpiresBeforePublication()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();bool current=true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>OpenVpnRouteJournal.EnsureDestinationAsync(f.Journal,f.Link,Outside,()=>current,f.Capture,async(s,ct)=>{var result=await f.Run(s,ct);if(s.StartsWith("New-NetRoute"))current=false;return result;},default));
        Assert.DoesNotContain(f.Capture(),r=>r.DestinationPrefix==Outside+"/32");Assert.Empty((await OpenVpnRouteJournal.ReadAsync(f.Journal))!.DestinationPrefixes);
    }
    [Fact] public async Task OffWaitsForLateMutationThenCleansBeforeNextGeneration()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();using var canceled=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(int Code,string Output)> Late(string command,CancellationToken ct)
        {if(command.StartsWith("New-NetRoute")){entered.SetResult();await release.Task;return await f.Run(command,CancellationToken.None);}return await f.Run(command,ct);}
        var install=OpenVpnRouteJournal.EnsureDestinationAsync(f.Journal,f.Link,Outside,()=>!canceled.IsCancellationRequested,f.Capture,Late,canceled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));canceled.Cancel();var cleanup=OpenVpnRouteJournal.CleanupAsync(f.Journal,f.Run);
        Assert.False(cleanup.IsCompleted);release.SetResult();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>install);await cleanup;
        Assert.Empty(f.Capture());await f.Start(f.Link with{Generation=2});
        Assert.DoesNotContain(f.Capture(),r=>r.DestinationPrefix==Outside+"/32");Assert.Equal(2,(await OpenVpnRouteJournal.ReadAsync(f.Journal))!.Generation);
    }
    [Fact] public async Task ForeignRouteWinningCreateRaceIsNotAdoptedOrDeleted()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();var foreign=new RouteRow(f.Link.Index,Outside+"/32",f.Link.Gateway,OpenVpnRouteJournal.OwnedMetric,"NetMgmt","Manual");
        f.BeforeCreate=(_,_)=>{lock(f.Rows)f.Rows.Add(foreign);return Task.CompletedTask;};
        await Assert.ThrowsAsync<IOException>(()=>f.Leases.AcquireAsync(f.Leases.Capture(),Outside,true,default));await f.Stop();Assert.Equal(new[]{foreign},f.Capture());
    }
}
