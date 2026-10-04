using NetCat.Core;
using NetCat.Network;
using Xunit;
namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed class Candidate28NativeTests
{
    [Fact] public async Task RetainedNativeSidecarAcceptsReplacementAfterProcessRevisionRestart()
    {
        await using var service=new Candidate18ReconnectTests.Fixture();await service.Start();await service.Reconnect();service.Push();
        await Candidate18ReconnectTests.Fixture.Until(()=>service.Service.Running);var before=service.Service.Link!;
        await using var routes=new Candidate26RouteFixture();var first=routes.Link with{ProfileId=before.ProfileId,Generation=before.Generation,RouteOwnerId=before.RouteOwnerId};await routes.Start(first);
        var profile=new Profile{Id=before.ProfileId!.Value,Protocol="openvpn",AllowPublicPushedRoutes=true,LearnedRoutes=first.LearnedRoutes.ToList()};
        var settings=new AppSettings{Profiles=[profile],OpenVpnProfileId=profile.Id};
        using var sidecar=new OpenVpnSidecar(Path.Combine(RoutingTests.ModuleRoot,"sing-box","sing-box.exe"),Path.Combine(routes.Root,"sidecar"),routes.Leases);
        await sidecar.ApplyAsync(settings,first,default,()=>service.Service.Link==before);
        Assert.True(sidecar.Active);sidecar.Invalidate();
        using(var process=System.Diagnostics.Process.GetProcessById(service.Service.ProcessId)){process.Kill();await process.WaitForExitAsync();}
        await Candidate18ReconnectTests.Fixture.Until(()=>service.Service.Link==null);service.PrepareNextProcessTranscript();
        var after=await service.Service.EnsureRunningAsync(service.Profile,"",default);await routes.Leases.RetireAsync(()=>OpenVpnRouteJournal.CleanupAsync(routes.Journal,routes.Run,routes.Capture));
        var second=first with{Generation=after.Generation,RouteOwnerId=after.RouteOwnerId};await routes.Start(second);
        await sidecar.ApplyAsync(settings,second,default,()=>service.Service.Link==after);
        Assert.True(sidecar.Active);Assert.True(second.Generation>first.Generation);
        var pid=sidecar.ProcessId;await sidecar.ApplyAsync(settings,second,default,()=>true);Assert.Equal(pid,sidecar.ProcessId);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>sidecar.ApplyAsync(settings,first,default,()=>false));Assert.True(sidecar.Active);Assert.Equal(pid,sidecar.ProcessId);
    }
}
