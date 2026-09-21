using System.Net;
using System.Text.Json;
using System.Security.Cryptography;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using NetCat.Updater;
using Xunit;
namespace NetCat.Tests;
public sealed class Build05Tests
{
    [Theory]
    [InlineData("v1.2.0","1.2",false)]
    [InlineData("0.5.0","0.5.0-test.5",true)]
    [InlineData("0.5.0-test.5","0.5.0",false)]
    [InlineData("0.5.0-test.10","0.5.0-test.5",true)]
    public void VersionsDoNotDowngrade(string remote,string local,bool newer) => Assert.Equal(newer,ModuleUpdater.IsNewer(remote,local));
    [Theory]
    [InlineData("../NetCat.exe")][InlineData("modules/xray/../../NetCat.exe")][InlineData("C:/temp/evil")][InlineData("modules/xray/file:stream")]
    public void PackagePathsCannotEscape(string path) => Assert.Throws<InvalidDataException>(()=>PortableUpdate.SafePath(Path.GetTempPath(),path));
    [Fact]
    public async Task PackageUpdatesOnlyOlderUnpinnedComponentsAndRejectsCorruption()
    {
        var root=Path.Combine(RoutingTests.FindRoot(),"artifacts","package-test-"+Guid.NewGuid().ToString("N"));var payload=Path.Combine(root,"payload");var installed=Path.Combine(root,"installed");Directory.CreateDirectory(payload);Directory.CreateDirectory(installed);
        var components=new List<PackageComponent>();
        foreach(var key in ModuleUpdater.Keys)
        {
            var relative=key=="netcat"?"NetCat.exe":"modules/"+key+"/test.bin";var path=PortableUpdate.SafePath(payload,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);await File.WriteAllTextAsync(path,"new-"+key);
            components.Add(new(key,"2.0.0",[new(relative,Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))))]));
            var old=PortableUpdate.SafePath(installed,relative);Directory.CreateDirectory(Path.GetDirectoryName(old)!);await File.WriteAllTextAsync(old,"old-"+key);
        }
        var manifest=new PackageManifest(1,"2.0.0",components);Directory.CreateDirectory(Path.Combine(payload,"metadata"));await File.WriteAllTextAsync(Path.Combine(payload,PortableUpdate.ManifestPath),JsonSerializer.Serialize(manifest,JsonSettings.Options));
        await PortableUpdate.VerifyAsync(payload,CancellationToken.None);
        var versions=ModuleUpdater.Keys.ToDictionary(k=>k,k=>k=="xray"?"3.0.0":"1.0.0");var plan=PortableUpdate.Plan(manifest,k=>versions[k],new HashSet<string>{"zapret"});
        Assert.DoesNotContain(plan,c=>c.Key is "xray" or "zapret");
        PortableUpdate.ApplyFiles(installed,payload,plan,versions);
        Assert.Equal("old-xray",File.ReadAllText(Path.Combine(installed,"modules/xray/test.bin")));Assert.Equal("old-zapret",File.ReadAllText(Path.Combine(installed,"modules/zapret/test.bin")));Assert.Equal("new-netcat",File.ReadAllText(Path.Combine(installed,"NetCat.exe")));
        await File.WriteAllTextAsync(Path.Combine(payload,"NetCat.exe"),"corrupted");await Assert.ThrowsAsync<InvalidDataException>(()=>PortableUpdate.VerifyAsync(payload,CancellationToken.None));
    }
    [Fact]
    public void GlobalModeHonorsExplicitServiceRoutes()
    {
        var profile=ProfileImporter.ParseLink("socks://192.0.2.1:1080#test");
        var config=SingBoxConfig.Build(new AppSettings {Mode=RoutingMode.Global,TelegramSocks=true},new("Ethernet",1,"192.168.1.2","192.168.1.1",[]),profile,null,false);
        var rules=config["route"]!["rules"]!.ToJsonString();Assert.Contains("\"outbound\":\"telegram\"",rules);Assert.Contains("youtube.com",rules);Assert.Equal("vpn",config["route"]!["final"]!.ToString());
    }
    [Fact]
    public void UpdateRestoresAlreadyChangedFilesWhenLaterFileFails()
    {
        var root=Path.Combine(RoutingTests.FindRoot(),"artifacts","rollback-test-"+Guid.NewGuid().ToString("N"));var installed=Path.Combine(root,"installed");var payload=Path.Combine(root,"payload");Directory.CreateDirectory(installed);Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(installed,"NetCat.exe"),"old");File.WriteAllText(Path.Combine(payload,"NetCat.exe"),"new");
        var components=new List<PackageComponent>{new("netcat","2.0",[new("NetCat.exe","")]),new("xray","2.0",[new("modules/xray/missing.exe","")])};
        Assert.ThrowsAny<IOException>(()=>PortableUpdate.ApplyFiles(installed,payload,components,new()));
        Assert.Equal("old",File.ReadAllText(Path.Combine(installed,"NetCat.exe")));
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task ZapretCanCancelPendingHttpWithoutWaitingForTestTimeout(bool stop)
    {
        var root=RoutingTests.FindRoot();var handler=new HangingHandler();using var service=new FakeZapret(Path.Combine(root,"bin"),Path.Combine(root,"artifacts","zapret-cancel-"+Guid.NewGuid().ToString("N")),handler);
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(12));var row=new StrategyResult {File="test.bat"};
        var test=service.TestAsync(new AppSettings {TestTimeoutSeconds=60},[row],new Progress<StrategyResult>(),ct.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if(stop)await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));else ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>test.WaitAsync(TimeSpan.FromSeconds(2)));Assert.False(service.Running);Assert.Equal("Отменён",row.YouTube);
    }
    private sealed class HangingHandler:HttpMessageHandler
    {
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Started.TrySetResult();await Task.Delay(Timeout.Infinite,ct);return new(HttpStatusCode.OK);}
    }
    private sealed class FakeZapret(string bin,string runtime,HangingHandler handler):ZapretService(bin,runtime)
    {
        protected override Task StartInternal(AppSettings s,string file,CancellationToken ct)=>Task.CompletedTask;
        protected override HttpClient CreateTestClient(int port,int timeout)=>new(handler){Timeout=TimeSpan.FromSeconds(timeout)};
    }
}
