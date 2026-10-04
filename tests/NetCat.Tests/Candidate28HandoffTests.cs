using System.Net;
using NetCat.Core;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed class Candidate28HandoffTests
{
    private static string Journal(Candidate18ReconnectTests.Fixture f)=>Path.Combine(f.Root,"openvpn-route.json");
    [Fact] public async Task OpenVpnCrashReplacesRouteGeneration()
    {
        await using var f=new Candidate18ReconnectTests.Fixture();await f.Start();await f.Reconnect();f.Push();
        await Candidate18ReconnectTests.Fixture.Until(()=>f.Service.Running);var old=f.Service.Link!;
        using(var process=System.Diagnostics.Process.GetProcessById(f.Service.ProcessId)){process.Kill();await process.WaitForExitAsync();}
        await Candidate18ReconnectTests.Fixture.Until(()=>!f.Service.Running);f.PrepareNextProcessTranscript();
        var next=await f.Service.EnsureRunningAsync(f.Profile,"",default);
        Assert.True(next.Generation>old.Generation,"A process restart must not rewind the revision seen by the retained sidecar.");
        Assert.Equal(next.Generation,(await OpenVpnRouteJournal.ReadAsync(Journal(f)))!.Generation);Assert.True(f.Service.VerifyRoutes().IsVerified);
    }
    [Fact] public async Task RuntimeGenerationReplacementCleansOldJournalBeforeInstall()
    {
        int probes=0;await using var f=new Candidate18ReconnectTests.Fixture((_,_)=>{probes++;return Task.FromResult(true);});await f.Start();
        var link=f.Service.Link!;var path=Journal(f);var old=(await OpenVpnRouteJournal.ReadAsync(path))!;
        // Reproduce the reported boundary state, without pretending that this
        // injection proves which VM callback originally produced the mismatch.
        await (old with{Generation=old.Generation+1}).SaveAsync(path,default);
        f.Commands.Clear();await f.Service.EnsureRoutesAsync(default);
        Assert.True(OpenVpnRouteJournal.SameGeneration((await OpenVpnRouteJournal.ReadAsync(path))!,f.Service.Link!));
        Assert.Equal(2,probes);Assert.True(f.Service.Running);
        Assert.StartsWith("Get-NetRoute",f.Commands[0]);Assert.Contains(f.Commands,c=>c.StartsWith("New-NetRoute"));
        Assert.Equal(link.ProfileId,f.Service.Link!.ProfileId);
    }
    [Fact] public async Task SameGenerationEnsureRoutesRepairsWithoutReplacement()
    {
        await using var f=new Candidate18ReconnectTests.Fixture();await f.Start();var link=f.Service.Link!;var g=f.Service.DestinationLeases.Capture();
        var ip=IPAddress.Parse("203.0.113.29");await using var lease=await f.Service.DestinationLeases.AcquireAsync(g,ip,true,default);
        lock(f.Routes)f.Routes.RemoveAll(r=>r.DestinationPrefix==link.LearnedRoutes[0]);f.Commands.Clear();
        await f.Service.EnsureRoutesAsync(default);
        Assert.Same(link,f.Service.Link);Assert.Same(g,f.Service.DestinationLeases.Capture());
        Assert.Equal(1,f.Service.DestinationLeases.ReferenceCount(g,ip));Assert.DoesNotContain(f.Commands,c=>c.StartsWith("Get-NetRoute"));
        Assert.True(f.Service.VerifyRoutes().IsVerified);
    }
    [Fact] public async Task RepeatedApplyDoesNotReplaceCurrentGeneration()
    {
        await using var f=new Candidate18ReconnectTests.Fixture();await f.Start();var link=f.Service.Link;var g=f.Service.DestinationLeases.Capture();var pid=f.Service.ProcessId;
        var journal=await File.ReadAllTextAsync(Journal(f));var written=File.GetLastWriteTimeUtc(Journal(f));f.Commands.Clear();
        for(int i=0;i<4;i++){Assert.Same(link,await f.Service.EnsureRunningAsync(f.Profile,"",default));await f.Service.EnsureRoutesAsync(default);}
        Assert.Same(g,f.Service.DestinationLeases.Capture());Assert.Equal(pid,f.Service.ProcessId);Assert.Empty(f.Commands);Assert.Equal(journal,await File.ReadAllTextAsync(Journal(f)));Assert.Equal(written,File.GetLastWriteTimeUtc(Journal(f)));
    }
    [Fact] public async Task PhysicalEpochChangeReplacesRouteGeneration()
    {
        await using var f=new Candidate18ReconnectTests.Fixture();await f.Start();var old=f.Service.Link!;var lease=f.Service.DestinationLeases.Capture();
        f.Service.PhysicalNetworkChanged();await Candidate18ReconnectTests.Fixture.Until(()=>f.Service.Link is {} l && l.RouteOwnerId!=old.RouteOwnerId);
        Assert.True(lease.Token.IsCancellationRequested);Assert.NotEqual(Guid.Empty,f.Service.Link!.RouteOwnerId);
        Assert.Equal(f.Service.Link.RouteOwnerId,(await OpenVpnRouteJournal.ReadAsync(Journal(f)))!.RouteOwnerId);
    }
    private sealed class Barrier(Candidate18ReconnectTests.Fixture f)
    {
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Install()
        {
            int waits=0;f.Service.PowerShellOverride=async(s,ct)=>{if(s.StartsWith("Get-NetRoute") && Interlocked.Increment(ref waits)==1){Entered.TrySetResult();await Release.Task;}return await f.Run(s,ct);};
        }
    }
    [Fact] public async Task OldCleanupCompletesBeforeNewInstall()
    {
        await using var f=new Candidate18ReconnectTests.Fixture();await f.Start();var old=f.Service.Link!;var b=new Barrier(f);b.Install();f.Commands.Clear();
        f.Service.PhysicalNetworkChanged();await b.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try{Assert.Null(f.Service.Link);Assert.Empty(f.Commands);Assert.Throws<IOException>(()=>f.Service.DestinationLeases.Capture());}
        finally{b.Release.TrySetResult();}
        await Candidate18ReconnectTests.Fixture.Until(()=>f.Service.Running);Assert.NotEqual(old.RouteOwnerId,f.Service.Link!.RouteOwnerId);
        int firstAdd=f.Commands.FindIndex(x=>x.StartsWith("New-NetRoute"));Assert.True(firstAdd>0);Assert.All(f.Commands.Take(firstAdd),x=>Assert.StartsWith("Get-NetRoute",x));
    }
    [Fact] public async Task OffDuringGenerationHandoffLeavesNoRoutes()
    {
        await using var f=new Candidate18ReconnectTests.Fixture();await f.Start();var b=new Barrier(f);b.Install();
        f.Service.PhysicalNetworkChanged();await b.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var off=f.Service.StopAsync();b.Release.TrySetResult();await off;
        Assert.Null(f.Service.Link);Assert.Empty(f.Routes);Assert.False(File.Exists(Journal(f)));Assert.False(f.Service.DataPathRecoveryRequired);
    }
    [Fact] public async Task ProfileSwitchDuringGenerationHandoffLeavesOnlyNewProfileRoutes()
    {
        await using var f=new Candidate18ReconnectTests.Fixture();await f.Start();var old=f.Service.Link!;var b=new Barrier(f);b.Install();
        f.Service.PhysicalNetworkChanged();await b.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var off=f.Service.StopAsync();b.Release.TrySetResult();await off;
        var next=new Profile{Protocol="openvpn",OpenVpnConfig="client\ndev tun\n",OpenVpnLegacyProviderRequired=true};f.PrepareNextProcessTranscript();
        await f.Service.StartAsync(next,"",default);var journal=(await OpenVpnRouteJournal.ReadAsync(Journal(f)))!;
        Assert.Equal(next.Id,journal.ProfileId);Assert.NotEqual(old.RouteOwnerId,journal.RouteOwnerId);Assert.Equal(next.Id,f.Service.DestinationLeases.Capture().Link.ProfileId);
    }
    [Fact] public async Task TwoConcurrentRecoveryRequestsProduceSingleReplacement()
    {
        int probes=0;await using var f=new Candidate18ReconnectTests.Fixture((_,_)=>{Interlocked.Increment(ref probes);return Task.FromResult(true);});await f.Start();
        var b=new Barrier(f);b.Install();f.Service.PhysicalNetworkChanged();await b.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Service.PhysicalNetworkChanged();b.Release.TrySetResult();await Candidate18ReconnectTests.Fixture.Until(()=>f.Service.Running);
        Assert.Equal(2,probes);Assert.Equal(f.Service.Link!.RouteOwnerId,(await OpenVpnRouteJournal.ReadAsync(Journal(f)))!.RouteOwnerId);
    }
    [Fact] public async Task RapidDownUpDownUpConverges()
    {
        int probes=0;await using var f=new Candidate18ReconnectTests.Fixture((_,_)=>{Interlocked.Increment(ref probes);return Task.FromResult(true);});await f.Start();
        var b=new Barrier(f);b.Install();f.Service.PhysicalNetworkChanged();await b.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for(int i=0;i<3;i++)f.Service.PhysicalNetworkChanged();b.Release.TrySetResult();await Candidate18ReconnectTests.Fixture.Until(()=>f.Service.Running);
        Assert.Equal(2,probes);Assert.True(f.Service.VerifyRoutes().IsVerified);Assert.False(f.Service.DataPathRecoveryRequired);
    }
    [Fact] public async Task OpenVpnExitPlusPhysicalChangeProducesSingleReplacement()
    {
        int probes=0;await using var f=new Candidate18ReconnectTests.Fixture((_,_)=>{Interlocked.Increment(ref probes);return Task.FromResult(true);});await f.Start();var old=f.Service.Link!;
        using(var p=System.Diagnostics.Process.GetProcessById(f.Service.ProcessId)){p.Kill();await p.WaitForExitAsync();}
        await Candidate18ReconnectTests.Fixture.Until(()=>f.Service.Link==null);f.Service.PhysicalNetworkChanged();f.PrepareNextProcessTranscript();
        await f.Service.EnsureRunningAsync(f.Profile,"",default);Assert.Equal(2,probes);Assert.True(f.Service.Link!.Generation>old.Generation);
    }
    [Fact] public async Task RouteCleanupFailureFailsClosed()
    {
        await using var f=new Candidate18ReconnectTests.Fixture();await f.Start();var before=(await OpenVpnRouteJournal.ReadAsync(Journal(f)))!;
        f.Service.PowerShellOverride=(s,ct)=>s.StartsWith("Get-NetRoute")?Task.FromResult((1,"controlled removal failure")):f.Run(s,ct);
        f.Service.PhysicalNetworkChanged();await Candidate18ReconnectTests.Fixture.Until(()=>f.Service.DataPathRecoveryRequired);
        try{Assert.Null(f.Service.Link);Assert.Equal(before.RouteOwnerId,(await OpenVpnRouteJournal.ReadAsync(Journal(f)))!.RouteOwnerId);Assert.Throws<IOException>(()=>f.Service.DestinationLeases.Capture());}
        finally{f.Service.PowerShellOverride=f.Run;}
    }
    [Fact] public async Task OldJournalCannotBlockNewGenerationForever()
    {
        await using var f=new Candidate18ReconnectTests.Fixture();await f.Start();f.Service.PowerShellOverride=(s,ct)=>Task.FromResult((1,"controlled failure"));
        f.Service.PhysicalNetworkChanged();await Candidate18ReconnectTests.Fixture.Until(()=>f.Service.DataPathRecoveryRequired);
        f.Service.PowerShellOverride=f.Run;f.PrepareNextProcessTranscript();await f.Service.EnsureRunningAsync(f.Profile,"",default);
        Assert.True(f.Service.Running);Assert.True(f.Service.VerifyRoutes().IsVerified);Assert.False(f.Service.DataPathRecoveryRequired);
    }
    [Fact] public async Task LateOldPreferredCannotPublishReady()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);bool tentative=false;int probes=0;
        await using var f=new Candidate18ReconnectTests.Fixture((_,_)=>{probes++;return Task.FromResult(true);},_=>tentative?OpenVpnAddressState.Tentative:OpenVpnAddressState.Preferred,(_,_)=>{entered.TrySetResult();return release.Task;});await f.Start();
        tentative=true;f.Service.PhysicalNetworkChanged();await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));tentative=false;f.Service.PhysicalNetworkChanged();
        await Candidate18ReconnectTests.Fixture.Until(()=>f.Service.Running);var current=f.Service.Link;release.TrySetResult();
        Assert.Same(current,f.Service.Link);Assert.Equal(2,probes);Assert.False(f.Service.DataPathRecoveryRequired);
    }
    [Fact] public async Task NoPermanentOpenVpnDefaultDuringGenerationReplacement()
    {
        await using var f=new Candidate18ReconnectTests.Fixture();await f.Start();var old=f.Service.Link;f.Service.PhysicalNetworkChanged();
        await Candidate18ReconnectTests.Fixture.Until(()=>f.Service.Link!=null&&!ReferenceEquals(old,f.Service.Link));
        Assert.DoesNotContain(f.Commands,s=>s.Contains("0.0.0.0/0")||s.Contains("9999"));Assert.False((await OpenVpnRouteJournal.ReadAsync(Journal(f)))!.HelperDefault);
    }
    [Fact] public async Task RetainedSidecarRejectsLateOldOwnerWithSameControlRevision()
    {
        await using var routes = new Candidate26RouteFixture();
        var profileId = Guid.NewGuid();
        var owner1 = Guid.NewGuid();
        var owner2 = Guid.NewGuid();
        var linkA = routes.Link with { ProfileId = profileId, Generation = 1, RouteOwnerId = owner1 };
        var linkB = routes.Link with { ProfileId = profileId, Generation = 1, RouteOwnerId = owner2 };
        await routes.Start(linkB);
        var profile = new Profile { Id = profileId, Protocol = "openvpn", AllowPublicPushedRoutes = true, LearnedRoutes = linkB.LearnedRoutes.ToList() };
        var settings = new AppSettings { Profiles = [profile], OpenVpnProfileId = profile.Id };
        using var sidecar = new OpenVpnSidecar(Path.Combine(RoutingTests.ModuleRoot, "sing-box", "sing-box.exe"), Path.Combine(routes.Root, "sidecar"), routes.Leases);

        await sidecar.ApplyAsync(settings, linkB, default, () => true);
        Assert.True(sidecar.Active);
        var pid = sidecar.ProcessId;
        Assert.True(pid > 0);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sidecar.ApplyAsync(settings, linkA, default, () => true));

        Assert.True(sidecar.Active);
        Assert.Equal(pid, sidecar.ProcessId);
    }
}

public sealed class Candidate28RetryTests
{
    [Fact] public async Task JournalConflictDoesNotHotLoop()
    {
        using var f=new OpenVpnBehaviorFixture();int calls=0;
        using var c=new RuntimeCoordinator(f.Desired,f.Main,f.Zapret,f.OpenVpn){GetSettings=()=>f.Settings,Clock=f.Clock,PhysicalNetworkProvider=f.Physical,
            TunnelInspector=new Candidate12Tests.Tunnel(),VerifyOpenVpnRoutesInRouteTable=(_,_)=>new(RouteObservationStatus.Verified,[]),
            FinalizeRoutingOverride=_=>{calls++;throw new InvalidOperationException("synthetic ownership conflict");}};
        await c.ReconcileAsync(ReconcileReason.Startup);
        for(int i=0;i<20;i++)await c.ReconcileAsync(ReconcileReason.OpenVpnStateChanged);
        Assert.Equal(1,calls);Assert.Equal(1,c.GetFailureCount(ComponentId.OpenVpnRoutes));
        f.Clock.Advance(TimeSpan.FromMinutes(6));await c.ReconcileAsync(ReconcileReason.ExternalConditionResolved);Assert.Equal(2,calls);
    }
    [Fact] public async Task RouteCleanupFailureDoesNotHotLoop()
    {
        using var f=new OpenVpnBehaviorFixture();int calls=0;
        using var c=new RuntimeCoordinator(f.Desired,f.Main,f.Zapret,f.OpenVpn){GetSettings=()=>f.Settings,Clock=f.Clock,PhysicalNetworkProvider=f.Physical,
            TunnelInspector=new Candidate12Tests.Tunnel(),VerifyOpenVpnRoutesInRouteTable=(_,_)=>new(RouteObservationStatus.Missing,[]),
            FinalizeRoutingOverride=_=>{calls++;throw new IOException("controlled cleanup failure");}};
        await c.ReconcileAsync(ReconcileReason.Startup);for(int i=0;i<20;i++)await c.ReconcileAsync(ReconcileReason.OpenVpnStateChanged);
        Assert.Equal(1,calls);Assert.Equal(1,c.GetFailureCount(ComponentId.OpenVpnRoutes));Assert.False(c.CurrentConvergenceState.DesiredSatisfied);
    }
    [Fact] public async Task ObsoleteRecoveryDoesNotConsumeCurrentGenerationRetryBudget()
    {
        using var f=new OpenVpnBehaviorFixture();int calls=0;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var c=new RuntimeCoordinator(f.Desired,f.Main,f.Zapret,f.OpenVpn){GetSettings=()=>f.Settings,Clock=f.Clock,PhysicalNetworkProvider=f.Physical,
            TunnelInspector=new Candidate12Tests.Tunnel(),VerifyOpenVpnRoutesInRouteTable=(_,_)=>new(RouteObservationStatus.Verified,[]),
            FinalizeRoutingOverride=async _=>{if(Interlocked.Increment(ref calls)>1)return;entered.TrySetResult();await release.Task;throw new OperationCanceledException();}};
        var pass=c.ReconcileAsync(ReconcileReason.Startup);await entered.Task;f.Desired.Current=f.Desired.Current with{OpenVpnEnabled=false};release.SetResult();await pass;
        Assert.Equal(0,c.GetFailureCount(ComponentId.OpenVpnRoutes));
    }
    [Fact] public async Task XrayStabilizationDoesNotSpinEmptyReconcilePlans()
    {
        var root=Path.Combine(Path.GetTempPath(),"NetCat-C28-retry-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var f=new Candidate12Tests.Fixture(new SettingsStore(root));await f.Reconcile();f.Router.LoseXray();await f.Reconcile();
            f.Clock.Advance(TimeSpan.FromHours(1));await f.Reconcile();Assert.Equal(2,f.Router.Starts);
            // The next pass has no work. Its returned delay must cover the
            // retained Xray failure budget's stability window, not zero.
            var method=typeof(RuntimeCoordinator).GetMethod("ExecuteReconcilePassAsync",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            var delay=await (Task<TimeSpan>)method.Invoke(f.Coordinator,[ReconcileReason.ExternalConditionResolved,CancellationToken.None])!;
            Assert.True(delay>TimeSpan.Zero);Assert.Equal(1,f.Coordinator.GetFailureCount(ComponentId.MainRouter));
            f.Clock.Advance(TimeSpan.FromSeconds(30));await f.Reconcile();Assert.Equal(0,f.Coordinator.GetFailureCount(ComponentId.MainRouter));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact] public async Task OldRouteOwnerFailureDoesNotThrottleReplacementOwner()
    {
        using var f = new OpenVpnBehaviorFixture();
        var owner1 = Guid.NewGuid();
        var owner2 = Guid.NewGuid();
        int attempts = 0;
        using var c = new RuntimeCoordinator(f.Desired, f.Main, f.Zapret, f.OpenVpn)
        {
            GetSettings = () => f.Settings,
            Clock = f.Clock,
            PhysicalNetworkProvider = f.Physical,
            TunnelInspector = new Candidate12Tests.Tunnel(),
            VerifyOpenVpnRoutesInRouteTable = (_, _) => new(RouteObservationStatus.Verified, []),
            FinalizeRoutingOverride = _ =>
            {
                attempts++;
                if (f.OpenVpn.Link?.RouteOwnerId == owner1)
                    throw new IOException("Owner 1 route failure");
                return Task.CompletedTask;
            }
        };

        await f.OpenVpn.EnsureRunningAsync(f.Profile, "", default);
        f.OpenVpn.Link = f.OpenVpn.Link! with { RouteOwnerId = owner1 };
        await c.ReconcileAsync(ReconcileReason.Startup);

        Assert.Equal(1, attempts);
        Assert.Equal(1, c.GetFailureCount(ComponentId.OpenVpnRoutes));
        Assert.False(c.CurrentConvergenceState.DesiredSatisfied);

        f.OpenVpn.Link = f.OpenVpn.Link! with { RouteOwnerId = owner2 };
        await c.ReconcileAsync(ReconcileReason.OpenVpnStateChanged);

        Assert.Equal(2, attempts);
        Assert.Equal(0, c.GetFailureCount(ComponentId.OpenVpnRoutes));
        Assert.True(c.CurrentConvergenceState.DesiredSatisfied);
    }
}

public sealed class Candidate28UpdaterTests
{
    [Theory]
    [InlineData("sing-box")]
    [InlineData("xray")]
    [InlineData("openvpn")]
    [InlineData("wintun")]
    public void PinnedAndKernelModulesDisallowAutoUpdate(string key)
    {
        var release = new NetCat.Updater.ModuleRelease(key, "repo", "9.9.9", "asset.zip", "https://example.com/asset.zip", new string('0', 64));
        var check = new NetCat.Updater.ModuleCheck(key, "1.0.0", release);
        Assert.False(check.AutoUpdateSupported);
        Assert.True(check.Available);
        if (key == "openvpn")
            Assert.Contains("требует отдельной сборки", check.Status);
        else
            Assert.Contains("автообновление пока недоступно", check.Status);
    }
}
