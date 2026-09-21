using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using NetCat.Updater;
using Xunit;

namespace NetCat.Tests;
public sealed class Build10Tests
{
    private static string Folder() { var p=Path.Combine(RoutingTests.FindRoot(),"artifacts","audit-test-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
    [Fact]
    public async Task ForgedUpdateJobAndUnsignedPublisherAreRejected()
    {
        var root=Folder(); var exe=Path.Combine(root,"NetCat.exe"); File.WriteAllText(exe,"untrusted");
        var job=Path.Combine(root,"job.json"); File.WriteAllText(job,JsonSerializer.Serialize(new UpdateJob(root,root,Environment.ProcessId,0,"fake",[])));
        await Assert.ThrowsAsync<InvalidDataException>(()=>UpdateChannel.ReceiveAndApplyAsync(job));
        Assert.Throws<InvalidDataException>(()=>PublisherTrust.RequireSamePublisher(exe,exe)); Assert.Equal("untrusted",File.ReadAllText(exe));
    }
    [Fact]
    public void SubscriptionAccountsDoNotCollideAndRenameDoesNotChangeOpenVpnIdentity()
    {
        var a=ProfileImporter.ParseLink("vless://00000000-0000-4000-8000-000000000001@vpn.example:443?security=tls#one");
        var b=ProfileImporter.ParseLink("vless://00000000-0000-4000-8000-000000000002@vpn.example:443?security=tls#two");
        Assert.NotEqual(ProfileIdentity.Key(a),ProfileIdentity.Key(b));
        var sub=new Subscription(); a.SubscriptionId=sub.Id; a.Name="Local name"; var current=new AppSettings { Profiles=[a],Subscriptions=[sub],MainProfileId=a.Id };
        var next=SubscriptionMerge.Prepare(current,sub.Id,[a,b]);
        Assert.Single(current.Profiles); Assert.Null(current.Subscriptions[0].UpdatedAt); Assert.Equal(2,next.Profiles.Count);
        Assert.Equal("Local name",next.Profiles.Single(p=>p.Id==a.Id).Name);
        var ovpn=new Profile {Protocol="openvpn",OpenVpnConfig="client\nremote vpn.example 1194\n",Name="Server"}; var identity=ProfileIdentity.Key(ovpn); ovpn.Name="My office"; Assert.Equal(identity,ProfileIdentity.Key(ovpn));
    }
    [Fact]
    public void RemovedComponentFilesAreDeletedAndRestoredOnRollback()
    {
        var root=Folder(); var payload=Folder(); Directory.CreateDirectory(Path.Combine(root,"modules/xray")); Directory.CreateDirectory(Path.Combine(root,"metadata"));
        var old=new PackageComponent("xray","1.0",[new("modules/xray/old.dll","")]);
        File.WriteAllText(Path.Combine(root,"modules/xray/old.dll"),"old");
        File.WriteAllText(Path.Combine(root,PortableUpdate.ManifestPath),JsonSerializer.Serialize(new PackageManifest(1,"1.0",[old]),JsonSettings.Options));
        var next=new PackageComponent("xray","2.0",[new("modules/xray/new.dll","")]); var versions=new Dictionary<string,string>{{"xray","1.0"}};
        Assert.ThrowsAny<IOException>(()=>PortableUpdate.ApplyFiles(root,payload,[next],versions));
        Assert.Equal("old",File.ReadAllText(Path.Combine(root,"modules/xray/old.dll"))); Assert.Equal("1.0",versions["xray"]);
        Directory.CreateDirectory(Path.Combine(payload,"modules/xray")); File.WriteAllText(Path.Combine(payload,"modules/xray/new.dll"),"new");
        PortableUpdate.ApplyFiles(root,payload,[next],versions);
        Assert.False(File.Exists(Path.Combine(root,"modules/xray/old.dll"))); Assert.Equal("new",File.ReadAllText(Path.Combine(root,"modules/xray/new.dll")));
        Assert.True(File.Exists(Path.Combine(root,"metadata/components/xray.json")));
    }
    [Fact]
    public async Task PreparedUpdateRejectsExtraFilesAndDoesNotInstallItsManifest()
    {
        var root=Path.Combine(Folder(),"modules"); var folder=Path.Combine(root,".prepared/geosite"); Directory.CreateDirectory(folder);
        var release=new ModuleRelease("geosite","Loyalsoldier/v2ray-rules-dat","202609122339","geosite.dat","unused","");
        File.WriteAllText(Path.Combine(folder,"geosite.dat"),"test payload"); File.WriteAllText(Path.Combine(folder,"netcat-source.json"),JsonSerializer.Serialize(release,JsonSettings.Options));
        var files=Directory.GetFiles(folder).ToDictionary(p=>Path.GetFileName(p)!,p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        File.WriteAllText(Path.Combine(folder,"prepared.json"),JsonSerializer.Serialize(new {Release=release,Files=files},JsonSettings.Options));
        using var updater=new ModuleUpdater(root); File.WriteAllText(Path.Combine(folder,"extra.dll"),"unverified");
        await Assert.ThrowsAsync<InvalidDataException>(()=>updater.InstallPreparedAsync(release,new(),CancellationToken.None)); Assert.False(Directory.Exists(Path.Combine(root,"geosite")));
        File.Delete(Path.Combine(folder,"extra.dll")); await updater.InstallPreparedAsync(release,new(),CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(root,"geosite/prepared.json"))); Assert.Equal(release.Version,updater.InstalledVersion("geosite"));
        Assert.Contains("modules/geosite/geosite.dat",File.ReadAllText(Path.Combine(root,"../metadata/components/geosite.json")));
    }
    [Fact]
    public async Task SecretsAreRemovedEvenWhenRouteRecoveryFails()
    {
        var root=Folder(); foreach(var name in new[]{"auth.pass","management.pass","openvpn.conf"}) File.WriteAllText(Path.Combine(root,name),"secret");
        File.WriteAllText(Path.Combine(root,"openvpn-route.json"),"invalid journal");
        using var ovpn=new OpenVpnService("unused",root);
        await Assert.ThrowsAsync<JsonException>(()=>ovpn.StopAsync());
        foreach(var name in new[]{"auth.pass","management.pass","openvpn.conf"}) Assert.False(File.Exists(Path.Combine(root,name)));
        Assert.True(File.Exists(Path.Combine(root,"openvpn-route.json")));
        File.WriteAllText(Path.Combine(root,"telegram.json"),"secret"); using var tg=new TelegramService(root,root); await tg.StopAsync(); Assert.False(File.Exists(Path.Combine(root,"telegram.json")));
    }
    [Fact]
    public void AutoInterfaceUsesCombinedMetricAndExcludesVirtualAdapters()
    {
        Assert.Equal(22,DefaultRoutes.Select([13,22],new Dictionary<int,uint>{{1,0},{13,45},{22,15}}));
        Assert.Equal(-1,DefaultRoutes.Select([13],new Dictionary<int,uint>{{1,0}}));
    }
    [Fact]
    public async Task StopAllClearsRuntimeFlags()
    {
        using var router=new RouterService(Path.Combine(RoutingTests.FindRoot(),"bin"),Folder());
        typeof(RouterService).GetProperty(nameof(RouterService.TunActive))!.SetValue(router,true);
        typeof(RouterService).GetProperty(nameof(RouterService.HealthSourcePort))!.SetValue(router,12345);
        await router.StopAllAsync(); Assert.False(router.TunActive); Assert.Equal(0,router.HealthSourcePort); Assert.False(router.VpnRequested);
    }
    [Fact]
    public void LocalProbeFailureDoesNotClaimWorkingVpnIsOffline()
    {
        var status=ConnectionHealth.Status(new(true,55),new(false,-1,"Address already in use"),true);
        Assert.Contains("VPN подключён",status); Assert.DoesNotContain("Нет доступа",status);
        Assert.Contains("VPN работает",ConnectionHealth.Status(new(false,-1,"timeout"),new(true,80),true));
    }
    [Fact]
    public void ZapretScenarioRoundTripRestoresDirectRouting()
    {
        var settings=new AppSettings {Mode=RoutingMode.Rules,YouTube=ServiceRoute.Zapret,Discord=ServiceRoute.Zapret};
        var p=ProfileImporter.ParseLink("socks://192.0.2.1:1080");
        string Route() => SingBoxConfig.Build(settings,new("Ethernet",1,"192.168.1.2","192.168.1.1",[]),p,null,false,zapretRunning:false)["route"]!["rules"]!.AsArray().First(r=>r?["domain_suffix"]?.ToJsonString().Contains("youtube.com")==true)!["outbound"]!.ToString();
        Assert.Equal("direct",Route()); settings.YouTube=ServiceRoute.Vpn; Assert.Equal("vpn",Route()); settings.YouTube=ServiceRoute.Zapret; Assert.Equal("direct",Route());
    }
    [Fact]
    public async Task HttpLatencyExcludesConnectionWarmup()
    {
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        var serving=Task.Run(async()=>
        {
            using var tcp=await listener.AcceptTcpClientAsync(ct.Token); using var stream=tcp.GetStream();
            var hello=new byte[2]; await stream.ReadExactlyAsync(hello,ct.Token); await stream.ReadExactlyAsync(new byte[hello[1]],ct.Token);
            await Task.Delay(350,ct.Token); await stream.WriteAsync(new byte[]{5,0},ct.Token);
            var command=new byte[5]; await stream.ReadExactlyAsync(command,ct.Token); await stream.ReadExactlyAsync(new byte[command[4]+2],ct.Token);
            await stream.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,80},ct.Token); using var reader=new StreamReader(stream,Encoding.ASCII,leaveOpen:true);
            for(int i=0;i<3;i++) { Assert.StartsWith("GET /probe",await reader.ReadLineAsync(ct.Token)); while(!string.IsNullOrEmpty(await reader.ReadLineAsync(ct.Token))){} await Task.Delay(35,ct.Token); await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 OK\r\nContent-Length: 0\r\n\r\n"),ct.Token); }
        });
        var result=await ConnectionLatency.MeasureAsync(((IPEndPoint)listener.LocalEndpoint).Port,"http://probe.example.invalid/probe",ct.Token);
        await serving; Assert.True(result.Success,result.Error); Assert.InRange(result.Milliseconds,25,250);
    }
    [Fact]
    public async Task ModuleDownloadUsesSelectedSocksTunnel()
    {
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        var serving=Task.Run(async()=>
        {
            using var tcp=await listener.AcceptTcpClientAsync(ct.Token); using var stream=tcp.GetStream();
            var hello=new byte[2]; await stream.ReadExactlyAsync(hello,ct.Token); await stream.ReadExactlyAsync(new byte[hello[1]],ct.Token); await stream.WriteAsync(new byte[]{5,0},ct.Token);
            var command=new byte[5]; await stream.ReadExactlyAsync(command,ct.Token); Assert.Equal(3,command[3]);
            var host=new byte[command[4]]; await stream.ReadExactlyAsync(host,ct.Token); Assert.Equal("download.example.invalid",Encoding.ASCII.GetString(host));
            await stream.ReadExactlyAsync(new byte[2],ct.Token); await stream.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,80},ct.Token);
            using var reader=new StreamReader(stream,Encoding.ASCII,leaveOpen:true); Assert.Equal("GET /component HTTP/1.1",await reader.ReadLineAsync(ct.Token)); while(!string.IsNullOrEmpty(await reader.ReadLineAsync(ct.Token))){}
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 7\r\nConnection: close\r\n\r\npayload"),ct.Token);
        });
        using var client=ModuleUpdater.CreateClient(((IPEndPoint)listener.LocalEndpoint).Port);
        Assert.Equal("payload",await client.GetStringAsync("http://download.example.invalid/component",ct.Token)); await serving;
    }
    [Fact]
    public async Task FailedOpenVpnDisableKeepsOldAdapterProcessAndRouter()
    {
        var runtime=Folder(); var bin=Path.Combine(RoutingTests.FindRoot(),"bin");
        using var router=new RouterService(bin,runtime); var profile=ProfileImporter.ParseLink("socks://192.0.2.1:1080");
        var settings=new AppSettings {Tun=false,MainProfileId=profile.Id,Profiles=[profile],SocksPort=OpenVpnService.FreePort()};
        await router.SetVpnAsync(settings,true);
        var host=(ProcessHost)typeof(OpenVpnService).GetField("host",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(router.OpenVpn)!;
        var stub=Path.Combine(runtime,"openvpn-process-stub.json");
        var port=OpenVpnService.FreePort();
        File.WriteAllText(stub,$$"""{"inbounds":[{"type":"mixed","listen":"127.0.0.1","listen_port":{{port}}}],"outbounds":[{"type":"direct"}]}""");
        host.Start(router.SingBox,["run","-c",stub]); await RouterService.WaitPortAsync(port,host,CancellationToken.None);
        typeof(OpenVpnService).GetProperty(nameof(OpenVpnService.Link))!.SetValue(router.OpenVpn,new OpenVpnLink("Test adapter",123,"10.1.1.2","10.1.1.1","10.1.1.1"));
        try
        {
            var invalid=JsonSettings.Clone(settings); invalid.MainProfileId=Guid.NewGuid();
            await Assert.ThrowsAsync<InvalidOperationException>(()=>router.SetOpenVpnAsync(invalid,false));
            Assert.True(router.OpenVpn.Running); Assert.True(router.VpnRunning); Assert.Equal(profile.Id,router.ActiveProfileId);
        }
        finally { await router.StopAllAsync(); }
    }
}
