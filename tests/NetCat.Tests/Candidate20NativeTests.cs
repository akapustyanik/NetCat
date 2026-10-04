using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed class Candidate20NativeTests
{
    private sealed class Resolver : IDisposable
    {
        private readonly UdpClient socket; private readonly CancellationTokenSource stop=new();
        public int Queries; public int Port=>((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        public Resolver(string ip,byte answer,int port=0){socket=new(new IPEndPoint(IPAddress.Parse(ip),port));_=Run(answer);}
        private async Task Run(byte answer)
        {try{while(!stop.IsCancellationRequested){var q=await socket.ReceiveAsync(stop.Token);Interlocked.Increment(ref Queries);var b=q.Buffer.ToList();b[2]=0x81;b[3]=0x80;b[6]=0;b[7]=1;b.AddRange(new byte[]{0xc0,12,0,1,0,1,0,0,0,0,0,4,127,0,0,answer});await socket.SendAsync(b.ToArray(),q.RemoteEndPoint,stop.Token);}}catch(Exception e)when(e is OperationCanceledException or ObjectDisposedException or SocketException){}}
        public void Dispose(){stop.Cancel();socket.Dispose();}
    }
    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "NetCat-C18-native-" + Guid.NewGuid().ToString("N"));
        public readonly CancellationTokenSource Timeout = new(TimeSpan.FromSeconds(25));
        public readonly Profile A = new() { Protocol = "openvpn", AllowPublicPushedRoutes = true, LearnedRoutes = ["127.0.0.2/32"] };
        public readonly Profile B = new() { Protocol = "openvpn", AllowPublicPushedRoutes = true, LearnedRoutes = ["127.0.0.3/32"] };
        public readonly RouterService Router;
        public readonly AppSettings Settings;
        public readonly Resolver Ordinary = new("127.0.0.3", 3, 53);
        public readonly Resolver Corporate = new("127.0.0.1", 1);
        public readonly int DnsPort = OpenVpnService.FreeTcpUdpPort();
        public readonly TcpListener Echo = new(IPAddress.Any, 0);
        public readonly NetworkSnapshot Physical;
        public CancellationToken Token => Timeout.Token;
        public int EchoPort => ((IPEndPoint)Echo.LocalEndpoint).Port;
        public OpenVpnSidecar Gateway => (OpenVpnSidecar)typeof(RouterService).GetField("openVpnSidecar", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Router)!;
        public object GuardedDirect => typeof(RouterService).GetField("guardedDirect", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Router)!;
        public void SetResolveHostOverride(Func<string, CancellationToken, Task<IPAddress[]>>? overrideFn)
        {
            GuardedDirect.GetType().GetProperty("ResolveHostOverride")!.SetValue(GuardedDirect, overrideFn);
        }
        private ProcessHost Host(string field) => (ProcessHost)typeof(RouterService).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Router)!;
        public (int, int, long) Identity => (Host("core").Id, Host("xray").Id, Router.SessionRevision);
        public string MainBytes => File.ReadAllText(Path.Combine(Root, "router.json"));
        public Fixture(bool freezeOwnership = false, bool freezeDomains = false, bool vpnDns = false)
        {
            Directory.CreateDirectory(Root);
            var nic = NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback);
            Physical = new(nic.Name, nic.GetIPProperties().GetIPv4Properties()!.Index, "127.0.0.1", "127.0.0.1", []);
            var main = new Profile { Protocol = "vless", Core = "Xray", Host = "127.0.0.1", Port = 9,
                OutboundJson = "{\"type\":\"vless\",\"server\":\"127.0.0.1\",\"server_port\":9,\"uuid\":\"11111111-1111-1111-1111-111111111111\"}" };
            Settings = new() { DirectDns="127.0.0.3", Mode=vpnDns ? RoutingMode.Global : RoutingMode.Rules, Profiles = [main, A, B], MainProfileId = main.Id, OpenVpnProfileId = A.Id, Tun = false, SocksPort = OpenVpnService.FreePort() };
            Router = new(RoutingTests.ModuleRoot, Root) { StartProcessOverride = (host, exe, args) => {
                {
                    var file=args[Array.IndexOf(args,"-c")+1];
                    var config=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
                    foreach(var ruleSet in config["route"]!["rule_set"]!.AsArray()) {
                        if(!(freezeOwnership && ruleSet!["tag"]!.ToString()=="openvpn-owned") && !(freezeDomains && ruleSet!["tag"]!.ToString()=="openvpn-domains")) continue;
                        var original=ruleSet["path"]!.ToString();var frozen=Path.Combine(Root,ruleSet!["tag"]!.ToString()+"-frozen.json");
                        File.Copy(original,frozen,true);ruleSet["path"]=frozen;
                    }
                    if(vpnDns && config["dns"]!["servers"]!.AsArray().First(s=>s!["tag"]!.ToString()==(config["dns"]!["servers"]!.AsArray().Any(n=>n!["tag"]!.ToString()=="dns-vpn-origin")?"dns-vpn-origin":"dns-vpn")) is {} vpn && vpn["type"]!.ToString()=="https") { var servers=config["dns"]!["servers"]!.AsArray(); var i=servers.IndexOf(vpn); servers[i]=new System.Text.Json.Nodes.JsonObject{["type"]="udp",["tag"]=vpn["tag"]!.ToString(),["server"]="127.0.0.3",["server_port"]=Ordinary.Port}; }
                    config["inbounds"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject{["type"]="direct",["tag"]="dns-probe",["listen"]="127.0.0.1",["listen_port"]=DnsPort,["network"]="udp",["override_address"]="1.1.1.1",["override_port"]=53});
                    File.WriteAllText(file,config.ToJsonString());
                }
                host.Start(exe,args);
            } }; Echo.Start();
        }
        public Task Start() => Router.SetVpnAsync(Settings, true, Token, physical: Physical);
        public async Task Apply(Profile? p, long generation = 1)
        {
            if (p != null) Settings.OpenVpnProfileId = p.Id;
            OpenVpnLink? link = p == null ? null : new OpenVpnLink(Physical.Name, Physical.Index, "127.0.0.1", "127.0.0.1", "127.0.0.1", p.LearnedRoutes) { ProfileId = p.Id, Generation = generation, DnsPort = Corporate.Port };
            await Candidate26NativeRoutes.Prepare(Router,Root,link);
            await Router.ApplyOpenVpnOverlayAsync(Settings,link,Token);
        }
        public async Task<TcpClient> Connect(string address)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, Router.ListenPort, Token); var stream = client.GetStream();
                await stream.WriteAsync(new byte[] { 5, 1, 0 }, Token); await stream.ReadExactlyAsync(new byte[2], Token);
                var destination=IPAddress.TryParse(address,out var ip) ? new byte[]{1}.Concat(ip.GetAddressBytes()).ToArray() : new byte[]{3,(byte)address.Length}.Concat(System.Text.Encoding.ASCII.GetBytes(address)).ToArray();
                await stream.WriteAsync(new byte[] { 5, 1, 0 }.Concat(destination).Concat(new[] { (byte)(EchoPort >> 8), (byte)EchoPort }).ToArray(), Token);
                var reply = new byte[10]; await stream.ReadExactlyAsync(reply, Token); Assert.Equal(0, reply[1]); return client;
            }
            catch { client.Dispose(); throw; }
        }
        public async Task Allowed(string address)
        {
            using var client = await Connect(address); await client.GetStream().WriteAsync(new byte[] { 18 }, Token);
            using var accepted = await Echo.AcceptTcpClientAsync(Token); var data = new byte[1]; await accepted.GetStream().ReadExactlyAsync(data, Token); Assert.Equal(18, data[0]);
        }
        public async Task Denied(string address)
        {
            using var client = await Connect(address); await client.GetStream().WriteAsync(new byte[] { 18 }, Token);
            using var noLeak = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var leaked = await Echo.AcceptTcpClientAsync(noLeak.Token); });
        }
        public void Dispose() { Router.Dispose(); Ordinary.Dispose(); Corporate.Dispose(); Echo.Stop(); Timeout.Dispose(); Directory.Delete(Root, true); }
    }

    private static async Task<byte[]?> Query(Fixture f,string name="new.corp.test")
    {
        var bytes=new List<byte>{0x20,1,1,0,0,1,0,0,0,0,0,0};foreach(var part in name.Split('.')){bytes.Add((byte)part.Length);bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(part));}bytes.AddRange(new byte[]{0,0,1,0,1});
        using var udp=new UdpClient();udp.Connect(IPAddress.Loopback,f.DnsPort);await udp.SendAsync(bytes.ToArray(),f.Token);
        using var deadline=new CancellationTokenSource(450);try{return(await udp.ReceiveAsync(deadline.Token)).Buffer;}catch(OperationCanceledException){return null;}
    }
    private static async Task DomainBarrier(bool vpn=false,bool failed=false,bool reconnect=false)
    {
        using var f=new Fixture(freezeDomains:true,vpnDns:vpn);await f.Start();await f.Apply(f.A);var identity=f.Identity;var main=f.MainBytes;
        if(reconnect)f.Gateway.Invalidate();
        f.Settings.OpenVpnDomains="new.corp.test";
        if(failed){Directory.CreateDirectory(f.Gateway.Gateway.DomainsPath+".next");await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Apply(f.A,2));}
        else await f.Apply(f.A,reconnect?2:1);
        var answer=await Query(f);Assert.Equal(0,f.Ordinary.Queries);Assert.True(answer==null||(answer[3]&15)!=0||answer[7]==0,"Frozen policy returned an ordinary DNS answer");Assert.Equal(identity,f.Identity);Assert.Equal(main,f.MainBytes);
        if(!failed&&!vpn){await Query(f,"unrelated.test");Assert.True(f.Ordinary.Queries>0);}
    }
    [Fact] public Task NewCorporateDomainCannotLeakBeforeRuleSetReload()=>DomainBarrier();

    [Fact] public Task NewCorporateDomainCannotUseMainVpnDnsDuringActivation()=>DomainBarrier(vpn:true);
    [Fact] public Task FailedDomainRuleReloadKeepsNewDomainClosed()=>DomainBarrier(failed:true);
    [Fact] public Task ReconnectDomainActivationRemainsClosed()=>DomainBarrier(reconnect:true);
    [Fact] public async Task DomainActivationKeepsExistingCorporateTcpConnection()
    {
        using var f=new Fixture(freezeDomains:true);await f.Start();await f.Apply(f.A);var identity=f.Identity;
        using var client=await f.Connect("127.0.0.2");await client.GetStream().WriteAsync(new byte[]{1},f.Token);using var server=await f.Echo.AcceptTcpClientAsync(f.Token);await server.GetStream().ReadExactlyAsync(new byte[1],f.Token);
        f.Settings.OpenVpnDomains="new.corp.test";await f.Apply(f.A);await Query(f);Assert.Equal(0,f.Ordinary.Queries);
        await server.GetStream().WriteAsync(new byte[]{2},f.Token);var result=new byte[1];await client.GetStream().ReadExactlyAsync(result,f.Token);Assert.Equal(2,result[0]);Assert.Equal(identity,f.Identity);
    }
    private static async Task Stale(bool domains,bool cancelled=false,bool newer=false)
    {
        using var f=new Fixture();await f.Start();f.Settings.OpenVpnDomains="valid.corp.test";await f.Apply(f.A);var pid=f.Gateway.ProcessId;var identity=f.Identity;
        var rules=File.ReadAllText(f.Gateway.Gateway.RulesPath);var dns=File.ReadAllText(f.Gateway.Gateway.DomainsPath);
        var old=JsonSettings.Clone(f.Settings);old.OpenVpnDomains="obsolete.corp.test";old.Profiles.First(p=>p.Id==f.A.Id).LearnedRoutes=["127.0.0.99/32"];
        var link=new OpenVpnLink(f.Physical.Name,f.Physical.Index,"127.0.0.1","127.0.0.1","127.0.0.1",["127.0.0.99/32"]){ProfileId=f.A.Id,Generation=newer?0:1,DnsPort=f.Corporate.Port};
        using var stop=new CancellationTokenSource();if(cancelled)stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Gateway.ApplyAsync(old,link,cancelled?stop.Token:f.Token,()=>false));
        Assert.Equal(domains?dns:rules,File.ReadAllText(domains?f.Gateway.Gateway.DomainsPath:f.Gateway.Gateway.RulesPath));
        Assert.True(f.Gateway.Active);Assert.Equal(pid,f.Gateway.ProcessId);Assert.Equal(identity,f.Identity);await f.Allowed("127.0.0.2");
    }
    [Fact] public Task StaleOverlayApplyDoesNotMutateOwnershipFiles()=>Stale(false);
    [Fact] public Task StaleOverlayApplyDoesNotMutateDomainRules()=>Stale(true);
    [Fact] public Task CancelledOverlayBeforeCommitKeepsValidBackend()=>Stale(true,cancelled:true);
    [Fact] public Task StaleGenerationCannotOverwriteNewOwnership()=>Stale(false,newer:true);
    [Fact] public Task StaleGenerationCannotOverwriteNewDomains()=>Stale(true,newer:true);
    [Fact] public async Task ActivatedDomainUsesOnlyCorporateResolver()
    {
        using var f=new Fixture();await f.Start();await f.Apply(f.A);var identity=f.Identity;
        f.Settings.OpenVpnDomains="new.corp.test";await f.Apply(f.A);
        byte[]? answer=null;using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while(answer==null||answer[7]==0){answer=await Query(f);Assert.Equal(0,f.Ordinary.Queries);if(answer==null||answer[7]==0)await Task.Delay(20,deadline.Token);}
        Assert.Equal(1,answer[^1]);Assert.True(f.Corporate.Queries>0);Assert.Equal(identity,f.Identity);
    }
    [Fact] public async Task PreviousOrdinaryDnsCacheCannotBypassNewDomainGuard()
    {
        using var f=new Fixture(freezeDomains:true);await f.Start();await f.Apply(f.A);
        Assert.Equal(3,(await Query(f))![^1]);int old=f.Ordinary.Queries;
        f.Settings.OpenVpnDomains="new.corp.test";await f.Apply(f.A);
        var answer=await Query(f);Assert.True(answer==null||(answer[3]&15)!=0||answer[7]==0);Assert.Equal(old,f.Ordinary.Queries);
    }
    [Fact] public async Task RemovingDomainReleasesAutomaticDnsGuard()
    {
        using var f=new Fixture(freezeDomains:true);await f.Start();await f.Apply(f.A);
        f.Settings.OpenVpnDomains="new.corp.test";await f.Apply(f.A);await Query(f);Assert.Equal(0,f.Ordinary.Queries);
        f.Settings.OpenVpnDomains="";await f.Apply(f.A);Assert.Equal(3,(await Query(f))![^1]);Assert.True(f.Ordinary.Queries>0);
    }
    [Theory][InlineData(RouteTarget.Direct)][InlineData(RouteTarget.Vpn)][InlineData(RouteTarget.Block)]
    public async Task ExplicitDomainRuleRetainsPriorityOverCorporateGuard(RouteTarget target)
    {
        using var f=new Fixture(freezeDomains:true,vpnDns:target==RouteTarget.Vpn);
        f.Settings.Rules.Add(new(){Kind=RuleKind.Domain,Value="new.corp.test",Target=target});await f.Start();await f.Apply(f.A);
        f.Settings.OpenVpnDomains="new.corp.test";await f.Apply(f.A);var answer=await Query(f);
        Assert.Equal(0,f.Corporate.Queries);
        if(target==RouteTarget.Block){Assert.Equal(0,f.Ordinary.Queries);Assert.True(answer==null||(answer[3]&15)!=0||answer[7]==0);}
        else{Assert.Equal(3,answer![^1]);Assert.True(f.Ordinary.Queries>0);}
    }
    [Fact] public async Task ExplicitDirectDomainConnectionUsesUserResolver()
    {
        using var f=new Fixture(freezeDomains:true);f.Settings.Rules.Add(new(){Kind=RuleKind.Domain,Value="new.corp.test",Target=RouteTarget.Direct});
        await f.Start();await f.Apply(f.A);f.Settings.OpenVpnDomains="new.corp.test";await f.Apply(f.A);
        await f.Allowed("new.corp.test");Assert.True(f.Ordinary.Queries>0);Assert.Equal(0,f.Corporate.Queries);
    }
    [Fact] public async Task NewCorporateDomainCannotUseAutomaticDirectBeforeRuleReload()
    {
        using var f=new Fixture(freezeDomains:true);await f.Start();await f.Apply(f.A);
        f.Settings.OpenVpnDomains="new.corp.test";await f.Apply(f.A);
        f.SetResolveHostOverride((host, ct) => Task.FromResult(new[] { IPAddress.Loopback }));
        await f.Denied("new.corp.test");
    }
    [Fact] public async Task NewCorporateDomainCannotReachSystemDnsBeforeRuleReload()
    {
        using var f=new Fixture(freezeDomains:true);await f.Start();await f.Apply(f.A);
        f.Settings.OpenVpnDomains="new.corp.test";await f.Apply(f.A);
        int systemDnsCalls = 0;
        f.SetResolveHostOverride((host, ct) => {
            Interlocked.Increment(ref systemDnsCalls);
            throw new InvalidOperationException("SYSTEM_DNS_LEAK: " + host);
        });
        using var client = await f.Connect("new.corp.test");
        await client.GetStream().WriteAsync(new byte[] { 1 }, f.Token);
        // Wait up to 1s to see if resolution was attempted:
        for (int i = 0; i < 20 && systemDnsCalls == 0; i++) await Task.Delay(50, f.Token);
        // It must NOT reach system DNS before rule reload:
        Assert.Equal(0, systemDnsCalls);
    }
    [Fact] public async Task NewCorporateDomainCannotUseMainVpnFallbackBeforeRuleReload()
    {
        using var f=new Fixture(freezeDomains:true,vpnDns:true);await f.Start();await f.Apply(f.A);
        f.Settings.OpenVpnDomains="new.corp.test";await f.Apply(f.A);
        await f.Denied("new.corp.test");
    }

    [Fact] public async Task CorporateDomainOffStateCannotUseOrdinaryDns()
    {
        using var f = new Fixture(); await f.Start();
        f.Settings.OpenVpnDomains = "offstate.corp.test";
        f.Router.PrepareDomainOwnership(f.Settings);
        var answer = await Query(f, "offstate.corp.test");
        Assert.Equal(0, f.Ordinary.Queries);
        Assert.True(answer == null || (answer[3] & 15) != 0 || answer[7] == 0);
    }
    [Fact] public async Task CorporateDomainOffStateCannotUseAutomaticDirect()
    {
        using var f = new Fixture(); await f.Start();
        f.Settings.OpenVpnDomains = "offstate.corp.test";
        f.Router.PrepareDomainOwnership(f.Settings);
        await f.Denied("offstate.corp.test");
    }
    [Fact] public async Task ApplyBecomingStaleDuringOwnershipPreparationCannotPublishObsoleteState()
    {
        using var f = new Fixture(); await f.Start();
        f.Settings.OpenVpnDomains = "valid.corp.test"; await f.Apply(f.A);
        var pid = f.Gateway.ProcessId;
        var old = JsonSettings.Clone(f.Settings); old.OpenVpnDomains = "stale.corp.test";
        var link = new OpenVpnLink(f.Physical.Name, f.Physical.Index, "127.0.0.1", "127.0.0.1", "127.0.0.1", f.A.LearnedRoutes) { ProfileId = f.A.Id, Generation = 2, DnsPort = f.Corporate.Port };
        bool firstCall = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Gateway.ApplyAsync(old, link, f.Token, () => {
            if (firstCall) { firstCall = false; return true; }
            return false;
        }));
        Assert.DoesNotContain("stale.corp.test", File.ReadAllText(f.Gateway.Gateway.DomainsPath));
        Assert.True(f.Gateway.Active);
        Assert.Equal(pid, f.Gateway.ProcessId);
    }
    [Fact] public async Task StaleApplyCannotOverwriteNewDomainOwnership()
    {
        using var f = new Fixture(); await f.Start();
        f.Settings.OpenVpnDomains = "new.corp.test"; await f.Apply(f.A, generation: 2);
        var pid = f.Gateway.ProcessId;
        var stale = JsonSettings.Clone(f.Settings); stale.OpenVpnDomains = "stale.corp.test";
        var staleLink = new OpenVpnLink(f.Physical.Name, f.Physical.Index, "127.0.0.1", "127.0.0.1", "127.0.0.1", f.A.LearnedRoutes) { ProfileId = f.A.Id, Generation = 1, DnsPort = f.Corporate.Port };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Gateway.ApplyAsync(stale, staleLink, f.Token, () => false));
        Assert.Contains("new.corp.test", File.ReadAllText(f.Gateway.Gateway.DomainsPath));
        Assert.DoesNotContain("stale.corp.test", File.ReadAllText(f.Gateway.Gateway.DomainsPath));
        Assert.True(f.Gateway.Active);
        Assert.Equal(pid, f.Gateway.ProcessId);
    }
    [Fact] public async Task StaleApplyCannotOverwriteNewPrefixOwnership()
    {
        using var f = new Fixture(); await f.Start();
        await f.Apply(f.A, generation: 2);
        var pid = f.Gateway.ProcessId;
        var stale = JsonSettings.Clone(f.Settings);
        stale.Profiles.First(p => p.Id == f.A.Id).LearnedRoutes = ["127.0.0.99/32"];
        var staleLink = new OpenVpnLink(f.Physical.Name, f.Physical.Index, "127.0.0.1", "127.0.0.1", "127.0.0.1", ["127.0.0.99/32"]) { ProfileId = f.A.Id, Generation = 1, DnsPort = f.Corporate.Port };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Gateway.ApplyAsync(stale, staleLink, f.Token, () => false));
        Assert.DoesNotContain("127.0.0.99", File.ReadAllText(f.Gateway.Gateway.RulesPath));
        Assert.True(f.Gateway.Active);
        Assert.Equal(pid, f.Gateway.ProcessId);
    }
    [Fact] public async Task StaleApplyCannotTearDownCurrentValidBackend()
    {
        using var f = new Fixture(); await f.Start();
        f.Settings.OpenVpnDomains = "current.corp.test"; await f.Apply(f.A, generation: 2);
        var pid = f.Gateway.ProcessId;
        var stale = JsonSettings.Clone(f.Settings); stale.OpenVpnDomains = "stale.corp.test";
        var staleLink = new OpenVpnLink(f.Physical.Name, f.Physical.Index, "127.0.0.1", "127.0.0.1", "127.0.0.1", f.A.LearnedRoutes) { ProfileId = f.A.Id, Generation = 1, DnsPort = f.Corporate.Port };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Gateway.ApplyAsync(stale, staleLink, f.Token, () => false));
        Assert.True(f.Gateway.Active);
        Assert.Equal(pid, f.Gateway.ProcessId);
        await f.Allowed("127.0.0.2");
    }
}
