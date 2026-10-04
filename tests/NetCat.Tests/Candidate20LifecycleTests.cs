using System.Net;
using System.Net.Sockets;
using System.Reflection;
using NetCat.Core;
using NetCat.Network;
using Xunit;
namespace NetCat.Tests;
public sealed class Candidate20LifecycleTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly Candidate19StateTests.Fixture Os=new();
        public readonly RouterService Router;
        public OpenVpnService Service=>Router.OpenVpn;
        public readonly OpenVpnSidecar Gateway;
        public readonly object Guard;
        public Profile Profile=>Os.Base.O;
        public AppSettings Settings=>Os.Base.Repo.CurrentSettings;
        public Fixture()
        {
            Router=new(RoutingTests.ModuleRoot,Path.Combine(Os.Root,"owner"));
            Gateway=(OpenVpnSidecar)typeof(RouterService).GetProperty("GatewaySidecar",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(Router)!;
            Guard=typeof(RouterService).GetField("guardedDirect",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(Router)!;
            foreach(var name in new[]{"CapturePhysicalPrefixes","CreateAdapterOverride","StartProcessOverride","CaptureLinkOverride","CaptureRouteTableOverride","PowerShellOverride","DataPathProbe","CaptureAddressState"})
                typeof(OpenVpnService).GetProperty(name)!.SetValue(Service,typeof(OpenVpnService).GetProperty(name)!.GetValue(Os.Service));
            Profile.OpenVpnConfig="client\ndev tun\n";
            Gateway.RememberRoutes(Settings);
        }
        public bool Blocked(string address)=> (bool)Guard.GetType().GetMethod("Blocked",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Guard,[IPAddress.Parse(address)])!;
        public async Task<Task<OpenVpnLink>> Candidate()
        {
            var start=Service.StartAsync(Profile,"",CancellationToken.None);
            Os.Append("PUSH_REPLY,route 10.9.0.0 255.255.0.0,route-gateway 10.1.0.1,dhcp-option DNS 10.1.0.53");
            await Candidate19StateTests.Fixture.Until(()=>Blocked("10.9.0.4"));return start;
        }
        public async ValueTask DisposeAsync(){await Service.StopAsync();Router.Dispose();await Os.DisposeAsync();}
    }
    [Fact] public async Task FailedGenerationCandidateHasDeterministicLifetime()
    {await using var f=new Fixture();var start=await f.Candidate();f.Os.Append("AUTH_FAILED");await Assert.ThrowsAnyAsync<Exception>(()=>start);Assert.False(f.Blocked("10.9.0.4"));}
    [Fact] public async Task UserOffClearsUncommittedCandidate()
    {await using var f=new Fixture();var start=await f.Candidate();await f.Service.StopAsync();await Assert.ThrowsAnyAsync<Exception>(()=>start);Assert.False(f.Blocked("10.9.0.4"));}
    [Fact] public async Task ReconnectInvalidationClearsOldCandidateGeneration()
    {await using var f=new Fixture();var start=await f.Candidate();var invalidated=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);f.Service.LinkChanged+=()=>invalidated.TrySetResult();f.Os.Append("SIGUSR1 received, restarting");await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(f.Blocked("10.9.0.4"));await f.Service.StopAsync();await Assert.ThrowsAnyAsync<Exception>(()=>start);}
    [Fact] public async Task SuccessfulCandidateBecomesCommittedOwnership()
    {await using var f=new Fixture();var start=await f.Candidate();f.Os.Append("Initialization Sequence Completed");var link=await start.WaitAsync(TimeSpan.FromSeconds(10));var settings=f.Settings;settings.Profiles.First(p=>p.Id==f.Profile.Id).LearnedRoutes=link.LearnedRoutes.ToList();f.Gateway.RememberRoutes(settings,link);await f.Service.StopAsync();Assert.True(f.Blocked("10.9.0.4"));f.Gateway.RememberRoutes(settings);Assert.True(f.Blocked("10.9.0.4"));}
    [Fact] public async Task ApplicationRestartDoesNotChangeDocumentedCandidatePolicy()
    {await using var f=new Fixture();var start=await f.Candidate();await f.Service.StopAsync();await Assert.ThrowsAnyAsync<Exception>(()=>start);using var restarted=new RouterService(RoutingTests.ModuleRoot,Path.Combine(f.Os.Root,"restarted"));var gateway=(OpenVpnSidecar)typeof(RouterService).GetProperty("GatewaySidecar",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(restarted)!;gateway.RememberRoutes(f.Settings);var guard=typeof(RouterService).GetField("guardedDirect",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(restarted)!;var after=(bool)guard.GetType().GetMethod("Blocked",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(guard,[IPAddress.Parse("10.9.0.4")])!;Assert.Equal(after,f.Blocked("10.9.0.4"));Assert.False(after);}
    [Fact] public async Task NewCandidateGenerationCannotClearAnotherProfile()
    {
        await using var f=new Fixture();var settings=f.Settings;var other=new Profile{Protocol="openvpn",LearnedRoutes=["10.8.0.0/16"]};settings.Profiles.Add(other);f.Gateway.RememberRoutes(settings);
        var start=await f.Candidate();await f.Service.StopAsync();await Assert.ThrowsAnyAsync<Exception>(()=>start);Assert.False(f.Blocked("10.9.0.4"));Assert.True(f.Blocked("10.8.0.4"));
    }
    [Fact] public async Task CandidateClearingIsScopedToExactProfileAndGeneration()
    {
        await using var f=new Fixture();var other=Guid.NewGuid();var type=f.Guard.GetType();var add=type.GetMethod("Candidate")!;var clear=type.GetMethod("ClearCandidate")!;
        add.Invoke(f.Guard,[f.Profile.Id,2L,new[]{"10.6.0.0/16"}]);add.Invoke(f.Guard,[other,2L,new[]{"10.7.0.0/16"}]);
        clear.Invoke(f.Guard,[f.Profile.Id,1L]);Assert.True(f.Blocked("10.6.0.1"));Assert.True(f.Blocked("10.7.0.1"));
        clear.Invoke(f.Guard,[f.Profile.Id,2L]);Assert.False(f.Blocked("10.6.0.1"));Assert.True(f.Blocked("10.7.0.1"));
    }
    [Fact] public async Task RejectedLanGenerationClearsUncommittedCandidate()
    {
        await using var f=new Fixture();typeof(OpenVpnService).GetProperty("CapturePhysicalPrefixes")!.SetValue(f.Service,(Func<IReadOnlyList<string>>)(()=>["10.9.2.2/24"]));
        var start=await f.Candidate();f.Os.Append("Initialization Sequence Completed");await Assert.ThrowsAsync<InvalidDataException>(()=>start);Assert.False(f.Blocked("10.9.0.4"));Assert.Null(f.Service.Link);Assert.Empty(f.Os.Routes);
    }
    [Fact] public void CandidateDomainIsGuardedBeforeAsyncApplyStarts()
    {
        var guard = new CorporateDomainGuard();
        var prof = Guid.NewGuid();
        guard.Candidate(prof, 1, ["candidate.corp.test"]);
        Assert.True(guard.IsCorporate("candidate.corp.test"));
        Assert.True(guard.IsCorporate("sub.candidate.corp.test"));
        Assert.False(guard.IsCorporate("unrelated.test"));
    }
    [Fact] public void DomainGuardDoesNotBlockUnrelatedDomain()
    {
        var guard = new CorporateDomainGuard();
        guard.Prepare("corp.test, internal.net");
        Assert.False(guard.IsCorporate("google.com"));
        Assert.False(guard.IsCorporate("fakecorp.test"));
        Assert.False(guard.IsCorporate("myinternal.net"));
    }
    [Fact] public void DomainGuardIsCaseInsensitive()
    {
        var guard = new CorporateDomainGuard();
        guard.Prepare("CORP.TEST");
        Assert.True(guard.IsCorporate("corp.test"));
        Assert.True(guard.IsCorporate("SUB.CORP.TEST"));
        Assert.True(guard.IsCorporate("sUb.CoRp.TeSt"));
    }
    [Fact] public void TrailingDotDoesNotBypassCorporateDomainGuard()
    {
        var guard = new CorporateDomainGuard();
        guard.Prepare("corp.test.");
        Assert.True(guard.IsCorporate("corp.test"));
        Assert.True(guard.IsCorporate("corp.test."));
        Assert.True(guard.IsCorporate("sub.corp.test."));
    }
    [Fact] public void CorporateDomainSuffixCannotBeBypassedBySubdomain()
    {
        var guard = new CorporateDomainGuard();
        guard.Prepare("corp.test");
        Assert.True(guard.IsCorporate("a.b.c.corp.test"));
        Assert.False(guard.IsCorporate("corp.test.attacker.com"));
    }
    [Fact] public void ProfileDeletionRemovesDomainOwnership()
    {
        var guard = new CorporateDomainGuard();
        var profA = Guid.NewGuid();
        var profB = Guid.NewGuid();
        guard.Candidate(profA, 1, ["a.corp.test"]);
        guard.Candidate(profB, 1, ["b.corp.test"]);
        Assert.True(guard.IsCorporate("a.corp.test"));
        Assert.True(guard.IsCorporate("b.corp.test"));
        guard.RemoveProfile(profA);
        Assert.False(guard.IsCorporate("a.corp.test"));
        Assert.True(guard.IsCorporate("b.corp.test"));
    }
    [Fact] public void RemovedDomainHasDeterministicGuardLifetime()
    {
        var guard = new CorporateDomainGuard();
        guard.Prepare("corp.test, other.test");
        Assert.True(guard.IsCorporate("corp.test"));
        Assert.True(guard.IsCorporate("other.test"));
        guard.Prepare("other.test");
        Assert.False(guard.IsCorporate("corp.test"));
        Assert.True(guard.IsCorporate("other.test"));
    }
    [Fact] public async Task CorporateDomainGuardConcurrentReadsAndGenerationUpdatesDoNotDeadlock()
    {
        var guard = new CorporateDomainGuard();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var prof = Guid.NewGuid();
        var writes = Task.Run(() => {
            long gen = 1;
            while (!cts.IsCancellationRequested)
            {
                guard.Candidate(prof, gen, [$"corp{gen}.test"]);
                guard.ClearCandidate(prof, gen - 1);
                gen++;
            }
        });
        var reads = Task.Run(() => {
            long hits = 0;
            while (!cts.IsCancellationRequested)
            {
                if (guard.IsCorporate("corp1.test") || guard.IsCorporate("unrelated.test")) hits++;
            }
        });
        await Task.WhenAll(writes, reads);
    }
    [Fact] public void CorporateDnsGuardPortsAreUnique()
    {
        using var guard = new CorporateDnsGuard();
        var ports = new[] { guard.DirectPort, guard.DirectReturnPort, guard.VpnPort, guard.VpnReturnPort };
        Assert.Equal(4, ports.Distinct().Count());
        Assert.All(ports, p => Assert.True(p is > 0 and <= 65535));
    }
    [Fact] public void CorporateDnsGuardReturnPortsDoNotOverlapMainListeners()
    {
        int mainSocks = 10808; int mainDns = 10853;
        using var guard = new CorporateDnsGuard(excludedPorts: [mainSocks, mainDns]);
        var ports = new[] { guard.DirectPort, guard.DirectReturnPort, guard.VpnPort, guard.VpnReturnPort };
        Assert.DoesNotContain(mainSocks, ports);
        Assert.DoesNotContain(mainDns, ports);
    }
    [Fact] public void CorporateDnsGuardReturnPortCollisionRetries()
    {
        int collisionPort = 19999;
        int attempts = 0;
        using var guard = new CorporateDnsGuard(allocatePort: () => {
            attempts++;
            return attempts < 3 ? collisionPort : OpenVpnService.FreeTcpUdpPort();
        }, excludedPorts: [collisionPort]);
        Assert.True(attempts >= 3);
        Assert.NotEqual(collisionPort, guard.DirectReturnPort);
    }
    [Fact] public void DnsGuardPortCollisionCannotRedirectTraffic()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        tcp.Start();
        int busyPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        using var guard = new CorporateDnsGuard(excludedPorts: [busyPort]);
        Assert.NotEqual(busyPort, guard.DirectPort);
        Assert.NotEqual(busyPort, guard.VpnPort);
        Assert.NotEqual(busyPort, guard.DirectReturnPort);
        Assert.NotEqual(busyPort, guard.VpnReturnPort);
    }
    [Fact] public void OrdinaryDnsStillWorksWithCorporateGuardEnabled()
    {
        var domainGuard = new CorporateDomainGuard();
        domainGuard.Prepare("corp.test");
        using var guard = new CorporateDnsGuard(domainGuard);
        Assert.False(domainGuard.IsCorporate("example.com"));
        Assert.True(domainGuard.IsCorporate("corp.test"));
    }
    [Fact] public void CorporateDomainCannotReuseOldOrdinaryPositiveCache()
    {
        var domainGuard = new CorporateDomainGuard();
        using var guard = new CorporateDnsGuard(domainGuard);
        Assert.False(domainGuard.IsCorporate("new.corp.test"));
        domainGuard.Prepare("new.corp.test");
        Assert.True(domainGuard.IsCorporate("new.corp.test"));
    }
    [Fact] public void RemovingCorporateDomainHasDocumentedCacheBehavior()
    {
        var domainGuard = new CorporateDomainGuard();
        domainGuard.Prepare("corp.test");
        using var guard = new CorporateDnsGuard(domainGuard);
        Assert.True(domainGuard.IsCorporate("corp.test"));
        domainGuard.Prepare("");
        Assert.False(domainGuard.IsCorporate("corp.test"));
    }
}
