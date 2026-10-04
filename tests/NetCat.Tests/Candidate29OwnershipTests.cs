using NetCat.Core;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed class Candidate29OwnershipTests
{
    [Fact] public void OwnerDiagnosticSeparatesRevisionAndAliasesWithoutRawIdentity()
    {
        var owner=Guid.NewGuid();var profile=Guid.NewGuid();
        var link=new OpenVpnLink("private-name",9,"192.0.2.2","192.0.2.1","192.0.2.53"){Generation=17,RouteOwnerId=owner,ProfileId=profile};
        var text=RuntimeIdentityDiagnostic.Owner(link);
        Assert.Contains("controlRevision=17",text);Assert.Contains("routeOwner="+RuntimeIdentityDiagnostic.Alias(owner),text);
        Assert.DoesNotContain(owner.ToString(),text);Assert.DoesNotContain(profile.ToString(),text);
        Assert.DoesNotContain("192.0.2",text);Assert.DoesNotContain("private-name",text);
        Assert.NotEqual(RuntimeIdentityDiagnostic.Alias(owner),RuntimeIdentityDiagnostic.Alias(profile));
    }
    [Fact]
    public async Task CurrentReplacementOwnerWithSameControlRevisionCanActivateAndRejectLateOldOwner()
    {
        await using var routes = new Candidate26RouteFixture();
        var first = routes.Link with { RouteOwnerId = Guid.NewGuid() };
        await routes.Start(first);
        var settings = new AppSettings { OpenVpnProfileId = first.ProfileId };
        using var sidecar = new OpenVpnSidecar(Path.Combine(RoutingTests.ModuleRoot, "sing-box", "sing-box.exe"), Path.Combine(routes.Root, "sidecar"), routes.Leases);
        await sidecar.ApplyAsync(settings, first, default, () => true);
        Assert.True(sidecar.Active);
        await routes.Leases.RetireAsync(() => OpenVpnRouteJournal.CleanupAsync(routes.Journal, routes.Run, routes.Capture));
        var replacement = first with { RouteOwnerId = Guid.NewGuid() };
        await routes.Start(replacement);
        await sidecar.ApplyAsync(settings, replacement, default, () => true);
        Assert.True(sidecar.Active);
        var pid = sidecar.ProcessId;
        var unscoped=first with {ProfileId=null,Generation=0,RouteOwnerId=Guid.Empty};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>sidecar.ApplyAsync(settings,unscoped,default,()=>true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sidecar.ApplyAsync(settings, first, default, () => true));
        Assert.ThrowsAny<OperationCanceledException>(() => sidecar.RememberRoutes(settings, first, () => true));
        Assert.True(sidecar.Active);
        Assert.Equal(pid, sidecar.ProcessId);
        Assert.Equal(replacement.RouteOwnerId, routes.Leases.Capture().Link.RouteOwnerId);
    }

    [Fact]
    public async Task CleanupCommandFailureWithoutCaptureRetainsJournal()
    {
        await using var routes = new Candidate26RouteFixture();
        await routes.Start();
        var before = await File.ReadAllTextAsync(routes.Journal);
        routes.FailRemove = true;
        await Assert.ThrowsAsync<IOException>(() => OpenVpnRouteJournal.CleanupAsync(routes.Journal, routes.Run));
        Assert.Equal(before, await File.ReadAllTextAsync(routes.Journal));
        Assert.NotEmpty(routes.Capture());
    }
}
