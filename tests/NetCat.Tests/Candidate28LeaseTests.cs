using System.Net;
using NetCat.Core;
using NetCat.Network;
using Xunit;
namespace NetCat.Tests;

public sealed class Candidate28LeaseTests
{
    private static readonly IPAddress Target=IPAddress.Parse("127.0.0.3");
    private static Task Retire(Candidate26RouteFixture f)=>f.Leases.RetireAsync(()=>OpenVpnRouteJournal.CleanupAsync(f.Journal,f.Run,f.Capture));
    [Fact] public async Task OldDynamicLeaseReleaseCannotAffectNewGeneration()
    {
        await using var f=new Candidate26RouteFixture();await f.Start(f.Link with{RouteOwnerId=Guid.NewGuid()});var old=f.Leases.Capture();var a=await f.Leases.AcquireAsync(old,Target,true,default);
        await Retire(f);await f.Start(f.Link with{RouteOwnerId=Guid.NewGuid()});var current=f.Leases.Capture();
        await using var b=await f.Leases.AcquireAsync(current,Target,true,default);await a.DisposeAsync();
        Assert.Equal(1,f.Leases.ReferenceCount(current,Target));Assert.Contains(f.Capture(),r=>r.DestinationPrefix==Target+"/32");Assert.Equal(current.Link.RouteOwnerId,(await OpenVpnRouteJournal.ReadAsync(f.Journal))!.RouteOwnerId);
    }
    [Fact] public async Task SameDestinationAcrossGenerationsKeepsNewRoute()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();var old=f.Leases.Capture();var a=await f.Leases.AcquireAsync(old,Target,true,default);
        await Retire(f);await f.Start(f.Link with{Generation=2});var g=f.Leases.Capture();
        await using var b=await f.Leases.AcquireAsync(g,Target,true,default);await using var c=await f.Leases.AcquireAsync(g,Target,true,default);
        await a.DisposeAsync();Assert.Equal(2,f.Leases.ReferenceCount(g,Target));Assert.Single(f.Capture(),r=>r.DestinationPrefix==Target+"/32");
    }
    [Fact] public async Task SamePushPrefixAcrossGenerationsKeepsNewRoute()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();var old=f.Leases.Capture();var a=await f.Leases.AcquireAsync(old,IPAddress.Parse("127.0.0.2"),true,default);
        await Retire(f);await f.Start(f.Link with{Generation=2});await a.DisposeAsync();
        Assert.Single(f.Capture(),r=>r.DestinationPrefix=="127.0.0.2/32");Assert.Equal(2,(await OpenVpnRouteJournal.ReadAsync(f.Journal))!.Generation);
    }
    [Fact] public async Task OldCleanupCannotDeleteNewSamePrefixRoute()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();var old=f.Leases.Capture();var oldLink=f.Link;
        await using var a=await f.Leases.AcquireAsync(old,Target,true,default);await Retire(f);await f.Start(f.Link with{Generation=2});
        await using var b=await f.Leases.AcquireAsync(f.Leases.Capture(),Target,true,default);f.Commands.Clear();
        await OpenVpnRouteJournal.ReleaseDestinationAsync(f.Journal,oldLink,Target,f.Run,()=>f.Leases.IsCurrent(old));Assert.Empty(f.Commands);Assert.Contains(f.Capture(),r=>r.DestinationPrefix==Target+"/32");
    }
    [Fact] public async Task OldDnsCompletionCannotCreateNewGenerationRoute()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();var old=f.Leases.Capture();
        var dns=new TaskCompletionSource<IPAddress>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task ResolveThenAcquire(){var ip=await dns.Task;await using var lease=await f.Leases.AcquireAsync(old,ip,true,default);}
        var pending=ResolveThenAcquire();await Retire(f);await f.Start(f.Link with{Generation=2});dns.SetResult(Target);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);Assert.DoesNotContain(f.Capture(),r=>r.DestinationPrefix==Target+"/32");
    }
    [Fact] public async Task OldRouteInstallCompletionCannotPublishAfterRetirement()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();var old=f.Leases.Capture();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.BeforeCreate=async(_,_)=>{entered.TrySetResult();await release.Task;};
        var acquire=f.Leases.AcquireAsync(old,Target,true,default);await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));var retirement=Retire(f);
        try{Assert.True(old.Token.IsCancellationRequested);Assert.False(retirement.IsCompleted);Assert.Throws<IOException>(()=>f.Leases.Capture());}
        finally{release.TrySetResult();}
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>acquire);await retirement;f.BeforeCreate=null;await f.Start(f.Link with{Generation=2});
        Assert.Equal(0,f.Leases.ReferenceCount(old,Target));Assert.DoesNotContain(f.Capture(),r=>r.DestinationPrefix==Target+"/32");
    }
    [Fact] public async Task ForeignRouteSurvivesGenerationReplacement()
    {
        await using var f=new Candidate26RouteFixture();var foreign=new RouteRow(f.Link.Index,"127.0.0.9/32",f.Link.Gateway,OpenVpnRouteJournal.OwnedMetric,"NetMgmt","Manual");f.Rows.Add(foreign);
        await f.Start();await Retire(f);await f.Start(f.Link with{Generation=2});Assert.Contains(foreign,f.Capture());await Retire(f);Assert.Equal(new[]{foreign},f.Capture());
    }
    [Fact] public async Task CleanupSuccessExitWithoutRouteRemovalFailsClosed()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();
        await Assert.ThrowsAsync<IOException>(()=>f.Leases.RetireAsync(()=>OpenVpnRouteJournal.CleanupAsync(f.Journal,(_,_)=>Task.FromResult((0,"")),f.Capture)));
        Assert.True(File.Exists(f.Journal));Assert.NotEmpty(f.Capture());Assert.Throws<IOException>(()=>f.Leases.Capture());
    }
    [Fact] public async Task HardCrashDuringOldGenerationCleanupRecoversOnStartup()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();await using var a=await f.Leases.AcquireAsync(f.Leases.Capture(),Target,true,default);
        var record=(await OpenVpnRouteJournal.ReadAsync(f.Journal))!;f.Leases.Invalidate();
        // Crash checkpoint: one Windows removal persisted, journal still owns
        // all intents. Startup must tolerate already absent exact entries.
        lock(f.Rows)f.Rows.RemoveAll(r=>r.DestinationPrefix==record.Prefixes[0]);
        await OpenVpnRouteJournal.CleanupAsync(f.Journal,f.Run,f.Capture);Assert.Empty(f.Capture());Assert.False(File.Exists(f.Journal));
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public async Task HardCrashDuringNewGenerationInstallRecoversOnStartup(int checkpoint)
    {
        await using var f=new Candidate26RouteFixture();await f.Start();await Retire(f);var next=f.Link with{Generation=2,RouteOwnerId=Guid.NewGuid()};
        if(checkpoint>0)
        {
            var intent=OpenVpnRouteJournal.Empty(next) with{Prefixes=next.LearnedRoutes.ToArray(),DnsPrefixes=OpenVpnRouteJournal.RequiredDnsPrefixes(next)};
            await intent.SaveAsync(f.Journal,default);
            // Intent-only; partially installed; fully installed awaiting proof.
            foreach(var prefix in intent.Prefixes.Concat(intent.DnsPrefixes).Take(checkpoint-1))f.Rows.Add(new(next.Index,prefix,next.Gateway,OpenVpnRouteJournal.OwnedMetric,"NetMgmt","Manual"));
        }
        await OpenVpnRouteJournal.CleanupAsync(f.Journal,f.Run,f.Capture);Assert.Empty(f.Capture());Assert.False(File.Exists(f.Journal));
        await f.Start(next);Assert.True(OpenVpnRouteJournal.SameGeneration((await OpenVpnRouteJournal.ReadAsync(f.Journal))!,next));
    }
    [Fact] public async Task JournalGenerationGuardStillRejectsUnretiredOwner()
    {
        await using var f=new Candidate26RouteFixture();await f.Start();var original=await File.ReadAllTextAsync(f.Journal);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>OpenVpnRouteJournal.InstallAsync(f.Journal,f.Link with{Generation=2},f.Capture,f.Run,default));
        Assert.Equal(original,await File.ReadAllTextAsync(f.Journal));
    }
    [Fact] public async Task SameControlRevisionDifferentPhysicalOwnershipIsRejected()
    {
        await using var f=new Candidate26RouteFixture();await f.Start(f.Link with{RouteOwnerId=Guid.NewGuid()});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>OpenVpnRouteJournal.InstallAsync(f.Journal,f.Link with{RouteOwnerId=Guid.NewGuid()},f.Capture,f.Run,default));
    }
}
