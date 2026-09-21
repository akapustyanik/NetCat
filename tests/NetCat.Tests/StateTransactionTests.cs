using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;
namespace NetCat.Tests;
public sealed class StateTransactionTests
{
    private static string Folder()=>Path.Combine(RoutingTests.FindRoot(),"artifacts","state-"+Guid.NewGuid().ToString("N"));
    private static AppSettings Settings() {var p=ProfileImporter.ParseLink("socks://192.0.2.1:1080");return new(){Tun=false,Profiles=[p],MainProfileId=p.Id,SocksPort=OpenVpnService.FreePort()};}
    [Fact]
    public async Task InvalidEditLeavesStateRuntimeAndSavedSettingsUntouchedAndCanRetryById()
    {
        var root=Folder();var store=new SettingsStore(root);var old=Settings();await store.SaveAsync(old);
        using var router=new RouterService(Path.Combine(RoutingTests.FindRoot(),"bin"),Path.Combine(root,"runtime")); await router.SetVpnAsync(old,true);
        var port=router.LatencyPort; var next=JsonSettings.Clone(old); next.Profiles[0].Port=0;
        var transaction=new SettingsTransaction();
        Task Validate(AppSettings s,CancellationToken _) {SettingsValidation.Validate(s);return Task.CompletedTask;}
        await Assert.ThrowsAsync<FormatException>(()=>transaction.ExecuteAsync(old,next,Validate,(s,t)=>router.SwitchProfileAsync(s,t),store.SaveAsync,(s,t)=>router.SetVpnAsync(s,true,t),router.StopAllAsync));
        Assert.Equal(1080,old.Profiles[0].Port); Assert.Equal(1080,store.Load().Profiles[0].Port);Assert.True(router.VpnRunning);Assert.Equal(port,router.LatencyPort);
        var id=old.Profiles[0].Id;next=JsonSettings.Clone(old);next.Profiles[next.Profiles.FindIndex(p=>p.Id==id)].Name="Retry";
        var result=await transaction.ExecuteAsync(old,next,Validate,null,store.SaveAsync,null,router.StopAllAsync);
        Assert.Equal("Retry",result.Profiles[0].Name);Assert.Equal("Retry",store.Load().Profiles[0].Name);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task SaveFailureRollsBackOrStopsRuntimeWithoutPublishing(bool rollbackFails)
    {
        var root=Folder();var store=new SettingsStore(root);var old=Settings();await store.SaveAsync(old);
        using var router=new RouterService(Path.Combine(RoutingTests.FindRoot(),"bin"),Path.Combine(root,"runtime")); await router.SetVpnAsync(old,true);
        var next=JsonSettings.Clone(old);var newer=ProfileImporter.ParseLink("socks://192.0.2.2:1080");next.Profiles.Add(newer);next.MainProfileId=newer.Id;
        var transaction=new SettingsTransaction(); bool applied=false, rolledBack=false;
        await Assert.ThrowsAnyAsync<Exception>(()=>transaction.ExecuteAsync(old,next,(s,_)=>Task.CompletedTask,
            async (s,t)=>{await router.SwitchProfileAsync(s,t,preflight:false);applied=router.ActiveProfileId==newer.Id;},
            _=>throw new IOException("injected save failure"),async(s,t)=>{rolledBack=true;if(rollbackFails)throw new IOException("injected rollback failure");await router.SetVpnAsync(s,true,t);},router.StopAllAsync));
        Assert.True(applied);Assert.True(rolledBack);Assert.Equal(old.MainProfileId,store.Load().MainProfileId);
        if(rollbackFails)AssertStopped(router);else {Assert.True(router.VpnRunning);Assert.Equal(old.MainProfileId,router.ActiveProfileId);}
    }
    [Fact]
    public async Task FailedNewStartAndFailedRestoreClearAllRuntimeFields()
    {
        int starts=0;var s=Settings();
        using var router=new RouterService(Path.Combine(RoutingTests.FindRoot(),"bin"),Folder())
        {StartProcessOverride=(host,exe,args)=>{if(++starts>1)throw new IOException("injected start failure");host.Start(exe,args);}};
        await router.SetVpnAsync(s,true);await Assert.ThrowsAsync<IOException>(()=>router.ApplyAsync(s));
        Assert.Equal(3,starts);AssertStopped(router);await router.StopAllAsync();await router.StopAllAsync();AssertStopped(router);
    }
    private static void AssertStopped(RouterService router)
    {
        Assert.False(router.VpnRunning);Assert.False(router.VpnRequested);Assert.Null(router.ActiveProfileId);Assert.Equal(0,router.ListenPort);
        Assert.Equal(0,router.LatencyPort);Assert.Equal(0,router.HealthSourcePort);Assert.False(router.TunActive);
    }
    [Fact]
    public void SettingsRollbackNotifiesScalarBindings()
    {
        var s=new AppSettings();var old=JsonSettings.Clone(s);var changes=new List<string?>();s.PropertyChanged+=(_,e)=>changes.Add(e.PropertyName);
        s.Tun=false;s.PhysicalInterface="Test";s.CopyFrom(old);
        Assert.True(s.Tun);Assert.Equal("",s.PhysicalInterface);Assert.Equal(2,changes.Count(x=>x==nameof(AppSettings.Tun)));Assert.Equal(2,changes.Count(x=>x==nameof(AppSettings.PhysicalInterface)));
    }
    [Fact]
    public void BackgroundTestsCannotAffectActiveFailureHistory()
    {
        var policy=new FailoverPolicy();var id=Guid.NewGuid();var now=DateTimeOffset.UtcNow;policy.ObserveSession(1);
        policy.Record(id,new(false,0),now);policy.Record(id,new(false,0),now,activeConnection:false);
        Assert.False(policy.ShouldRecover(id,2,now,TimeSpan.FromMinutes(1)));
        policy.Record(id,new(true,10),now,activeConnection:false);policy.Record(id,new(false,0),now);
        Assert.True(policy.ShouldRecover(id,2,now,TimeSpan.FromMinutes(1)));
        policy.ObserveSession(2);Assert.False(policy.ShouldRecover(id,2,now,TimeSpan.FromMinutes(1)));
        policy.Record(id,new(false,0),now);policy.Record(id,new(false,0),now);policy.Switched(now);
        Assert.False(policy.ShouldRecover(id,2,now,TimeSpan.FromMinutes(1)));
    }
    [Fact]
    public async Task OpenVpnIdentityCannotFollowAChangedSelectionOrBeReusedForAnotherTest()
    {
        var root=Folder();Directory.CreateDirectory(root);var bin=Path.Combine(RoutingTests.FindRoot(),"bin");
        using var router=new RouterService(bin,root);var service=router.OpenVpn;
        var host=(ProcessHost)typeof(OpenVpnService).GetField("host",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(service)!;
        var port=OpenVpnService.FreePort();var file=Path.Combine(root,"stub.json");
        await File.WriteAllTextAsync(file,$$"""{"inbounds":[{"type":"mixed","listen":"127.0.0.1","listen_port":{{port}}}],"outbounds":[{"type":"direct"}]}""");
        host.Start(router.SingBox,["run","-c",file]);await RouterService.WaitPortAsync(port,host,CancellationToken.None);
        var a=new Profile{Protocol="openvpn"};var b=new Profile{Protocol="openvpn"};
        typeof(OpenVpnService).GetProperty(nameof(OpenVpnService.Link))!.SetValue(service,new OpenVpnLink("test",1,"192.0.2.1","192.0.2.2","192.0.2.3"));
        typeof(OpenVpnService).GetProperty(nameof(OpenVpnService.ActiveProfileId))!.SetValue(service,a.Id);
        Assert.NotNull(await service.StartAsync(a,"",CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.StartAsync(b,"",CancellationToken.None));
        var result=await router.TestProfileAsync(b,new(){Profiles=[a,b],OpenVpnProfileId=b.Id},CancellationToken.None);
        Assert.False(result.Success);Assert.Equal(a.Id,service.ActiveProfileId);Assert.True(service.Running);
        await service.StopAsync();Assert.Null(service.ActiveProfileId);Assert.Null(service.Link);Assert.False(service.Running);
    }
}
