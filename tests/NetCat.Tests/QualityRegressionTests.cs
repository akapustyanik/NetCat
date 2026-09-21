using System.Buffers.Binary;
using System.Text;
using System.Net;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;
namespace NetCat.Tests;

public sealed class QualityRegressionTests
{
    [Fact]
    public async Task GeodataCacheParsesOnceAndReplacesOldBufferAfterChange()
    {
        static byte[] Field(int n,byte[] value)=>[(byte)(n*8+2),(byte)value.Length,..value];
        static byte[] Data(string domain)=>Field(1,[..Field(1,Encoding.UTF8.GetBytes("test")),..Field(2,[8,2,..Field(2,Encoding.UTF8.GetBytes(domain))])]);
        var path=Path.Combine(Path.GetTempPath(),"NetCat-Cache-"+Guid.NewGuid()+".dat");var cache=new GeodataCache();
        try
        {
            File.WriteAllBytes(path,Data("old.test")); var old=cache.Get(path,false);
            var results=await Task.WhenAll(Enumerable.Range(0,20).Select(_=>Task.Run(()=>cache.Get(path,false))));
            Assert.All(results,p=>Assert.Same(old,p));Assert.Equal(1,cache.ParseCount);
            Assert.Equal(new Geodata(path,false).Match("test").ToJsonString(),old.Match("test").ToJsonString());
            File.WriteAllBytes(path,Data("new.example.test"));File.SetLastWriteTimeUtc(path,DateTime.UtcNow.AddSeconds(3));
            var next=cache.Get(path,false);Assert.NotSame(old,next);Assert.Equal(2,cache.ParseCount);
            Assert.True(next.Contains("test","new.example.test"));Assert.False(next.Contains("test","old.test"));Assert.Same(next,cache.Get(path,false));
            Assert.Equal(new Geodata(path,false).Match("test").ToJsonString(),next.Match("test").ToJsonString());
        }
        finally{File.Delete(path);}
    }
    [Fact]
    public void IcoChooses32PixelDirectoryEntryUsingOffsetAndLength()
    {
        var ico=new byte[80]; BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2),1);BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4),3);
        foreach(var entry in new[]{(0,16,54,4),(1,64,58,7),(2,32,72,5)})
        {int at=6+16*entry.Item1;ico[at]=ico[at+1]=(byte)entry.Item2;BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(at+8),(uint)entry.Item4);BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(at+12),(uint)entry.Item3);Array.Fill(ico,(byte)entry.Item2,entry.Item3,entry.Item4);}
        Assert.Equal(new byte[]{32,32,32,32,32},IconDirectory.Image(ico));
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(6+32+12),1000);Assert.Throws<InvalidDataException>(()=>IconDirectory.Image(ico));
    }
    [Theory][InlineData(false,false)][InlineData(true,false)][InlineData(true,true)]
    public async Task PartialApplyRollsBackOrStopsAndPreservesAllErrors(bool failRollback,bool failStop)
    {
        bool applied=false,rolledBack=false,stopped=false,saved=false;
        var error=await Assert.ThrowsAnyAsync<Exception>(()=>new SettingsTransaction().ExecuteAsync(new(),new(),(_,_)=>Task.CompletedTask,
            (_,_)=>{applied=true;throw new IOException("apply");},_=>{saved=true;return Task.CompletedTask;},
            (_,_)=>{rolledBack=true;if(failRollback)throw new IOException("rollback");return Task.CompletedTask;},
            ()=>{stopped=true;if(failStop)throw new IOException("stop");return Task.CompletedTask;}));
        Assert.True(applied);Assert.True(rolledBack);Assert.False(saved);Assert.Equal(failRollback,stopped);
        if(failRollback)Assert.Equal(failStop?3:2,Assert.IsType<AggregateException>(error).InnerExceptions.Count);else Assert.Equal("apply",error.Message);
    }
    [Fact]
    public void SettingsRejectDuplicateAndWrongKindAndRepairDanglingSelections()
    {
        var a=ProfileImporter.ParseLink("socks://192.0.2.1:1080");var ovpn=new Profile {Protocol="openvpn"};
        var s=new AppSettings{Profiles=[a,ovpn],MainProfileId=ovpn.Id,OpenVpnProfileId=a.Id};
        Assert.Throws<FormatException>(()=>SettingsValidation.Validate(s));SettingsValidation.RepairSelections(s);
        Assert.Equal(a.Id,s.MainProfileId);Assert.Equal(ovpn.Id,s.OpenVpnProfileId);
        s.Profiles.Remove(a);SettingsValidation.RepairSelections(s);Assert.Null(s.MainProfileId);
        s.Profiles.Remove(ovpn);SettingsValidation.RepairSelections(s);Assert.Null(s.OpenVpnProfileId);
        s.Profiles=[a,JsonSettings.Clone(a)];Assert.Throws<FormatException>(()=>SettingsValidation.Validate(s));
    }
    [Fact]
    public async Task LoadingRepairsMissingAndWrongKindProfileReferences()
    {
        var root=Path.Combine(Path.GetTempPath(),"NetCat-Load-"+Guid.NewGuid());var store=new SettingsStore(root);
        var profile=ProfileImporter.ParseLink("socks://192.0.2.1:1080");var ovpn=new Profile {Protocol="openvpn"};
        try
        {
            await store.SaveAsync(new(){Profiles=[profile,ovpn],MainProfileId=Guid.NewGuid(),OpenVpnProfileId=profile.Id});
            var loaded=store.Load();Assert.Equal(profile.Id,loaded.MainProfileId);Assert.Equal(ovpn.Id,loaded.OpenVpnProfileId);
            await store.SaveAsync(new(){Profiles=[profile,ovpn],MainProfileId=ovpn.Id});Assert.Equal(profile.Id,store.Load().MainProfileId);
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact]
    public async Task ReleaseGuardAllowsIdenticalContentButRefusesVersionReuse()
    {
        var root=Path.Combine(Path.GetTempPath(),"NetCat-ReleaseGuard-"+Guid.NewGuid());var candidate=Path.Combine(root,"candidate");var destination=Path.Combine(root,"published");
        foreach(var dir in new[]{candidate,destination}) {Directory.CreateDirectory(Path.Combine(dir,"metadata"));File.WriteAllText(Path.Combine(dir,"metadata/release-manifest.json"),"{\"Version\":\"0.5.0-test.14\"}");File.WriteAllText(Path.Combine(dir,"NetCat.exe"),"same");}
        var script=Path.Combine(RoutingTests.FindRoot(),"scripts/Assert-ImmutableRelease.ps1");
        Task<(int Code,string Output)> Run() => ProcessHost.RunAsync(ProcessHost.PowerShellPath,["-NoProfile","-ExecutionPolicy","Bypass","-File",script,"-Candidate",candidate,"-Destination",destination]);
        try
        {
            var first=await Run();Assert.True(first.Code==0,first.Output);File.WriteAllText(Path.Combine(candidate,"NetCat.exe"),"changed");Assert.NotEqual(0,(await Run()).Code);
            Assert.Equal("same",File.ReadAllText(Path.Combine(destination,"NetCat.exe")));
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact]
    public async Task RuntimeRollbackRestoresEverySnapshotField()
    {
        var root=Path.Combine(Path.GetTempPath(),"NetCat-Snapshot-"+Guid.NewGuid());var p=ProfileImporter.ParseLink("socks://192.0.2.1:1080");
        var s=new AppSettings{Profiles=[p],MainProfileId=p.Id,Tun=false,SocksPort=OpenVpnService.FreePort()};int starts=0;RouterService? router=null;
        using(router=new RouterService(Path.Combine(RoutingTests.FindRoot(),"bin"),root){StartProcessOverride=(host,exe,args)=>
        {
            if(++starts==2)
            {
                foreach(var name in new[]{"ListenPort","LatencyPort","HealthSourcePort"})typeof(RouterService).GetProperty(name)!.SetValue(router,32123);
                typeof(RouterService).GetProperty("ActiveProfileId")!.SetValue(router,Guid.NewGuid());typeof(RouterService).GetProperty("TunActive")!.SetValue(router,true);
                typeof(RouterService).GetField("requiresXray",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.SetValue(router,true);
                throw new IOException("injected partial state corruption");
            }
            host.Start(exe,args);
        }})
        {
            await router.SetVpnAsync(s,true);var snapshot=router.CaptureRuntime();await Assert.ThrowsAsync<IOException>(()=>router.ApplyAsync(s));
            Assert.Equal(snapshot,router.CaptureRuntime());Assert.True(router.VpnRunning);
        }
        Directory.Delete(root,true);
    }
    private sealed class ProbeHandler : HttpMessageHandler
    {
        public bool BadGateway;public List<string> Seen=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {Seen.Add(request.RequestUri!.ToString());return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(request.RequestUri.AbsolutePath.EndsWith("gateway")?(BadGateway?"{\"url\":\"wss://evil.example/\"}":"{\"url\":\"wss://gateway.discord.gg\"}"):"ok")});}
    }
    [Fact]
    public async Task ServiceProbesSeparateHttpsGatewayAndUntestedVoice()
    {
        using var handler=new ProbeHandler();using var client=new HttpClient(handler);bool ws=false;
        var result=await ZapretProbes.RunAsync(client,ZapretProbes.Defaults,CancellationToken.None,(uri,_)=>{Assert.Equal("gateway.discord.gg",uri.Host);ws=true;return Task.CompletedTask;});
        Assert.True(ws);Assert.Equal(5,result.Count);Assert.All(result,r=>Assert.True(r.Success));Assert.Equal(4,handler.Seen.Count);
        Assert.Contains("Voice UDP: не проверялось",ZapretProbes.Summary(result,"Discord"));
        handler.BadGateway=true;ws=false;result=await ZapretProbes.RunAsync(client,ZapretProbes.Defaults,CancellationToken.None,(_,_)=>{ws=true;return Task.CompletedTask;});
        Assert.False(ws);Assert.Contains(result,r=>r.Name=="Gateway discovery"&&!r.Success);
    }
    [Theory][InlineData("Local")][InlineData("Global")]
    public async Task IsolatedMachineOwnerExcludesSecondAndSameSessionShowActivates(string scope)
    {
        var id=Guid.NewGuid().ToString("N");var name=scope+"\\NetCat.Test.Owner."+id;var pipe="NetCat.Test.Show."+id;
        using(var first=new NetworkOwner(name))
        {
            Assert.True(first.Acquired);using var second=new NetworkOwner(name);Assert.False(second.Acquired);
            var shown=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.Listen(()=>{shown.SetResult();return Task.CompletedTask;},pipe);
            Assert.True(await NetworkOwner.ShowExistingAsync(pipe));await shown.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        using var replacement=new NetworkOwner(name);Assert.True(replacement.Acquired);
    }
}
