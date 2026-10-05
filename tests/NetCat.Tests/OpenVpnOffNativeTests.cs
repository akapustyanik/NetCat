using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;
namespace NetCat.Tests;
[Collection("Candidate18 native resources")]
public sealed class OpenVpnOffNativeTests
{
    [Theory][InlineData(RoutingMode.Rules)][InlineData(RoutingMode.Global)]
    public async Task IntentionalOffAllowsTrafficAndReenableProtectsPendingGenerationWithoutMainRestart(RoutingMode mode)
    {
        var root=Path.Combine(Path.GetTempPath(),"NetCat-off-native-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            using var resolver=new Resolver("127.0.0.3",3,53);
            using var echo=new Echo("127.0.0.3");
            using var endpoint=new VpnEndpoint();
            using var udpEcho=new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.3"),0));
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var ct=deadline.Token;
            var udpTask=Task.Run(async()=>{try{while(!ct.IsCancellationRequested){var packet=await udpEcho.ReceiveAsync(ct);await udpEcho.SendAsync(packet.Buffer,packet.RemoteEndPoint,ct);}}catch(OperationCanceledException){}},ct);
            var nic=NetworkInterface.GetAllNetworkInterfaces().First(n=>n.NetworkInterfaceType==NetworkInterfaceType.Loopback);
            var physical=new NetworkSnapshot(nic.Name,nic.GetIPProperties().GetIPv4Properties()!.Index,"127.0.0.1","127.0.0.3",[]);
            var main=ProfileImporter.ParseLink("socks://127.0.0.1:"+endpoint.Port);
            var corporate=new Profile{Protocol="openvpn",AllowPublicPushedRoutes=true,LearnedRoutes=["127.0.0.3/32"]};
            var settings=new AppSettings{Mode=mode,Tun=false,Profiles=[main,corporate],MainProfileId=main.Id,OpenVpnProfileId=corporate.Id,SocksPort=OpenVpnService.FreePort(),OpenVpnDomains="office.example",DirectDns="127.0.0.3"};
            var originalSettings=JsonSerializer.Serialize(settings,JsonSettings.Options);
            int dnsPort=OpenVpnService.FreeTcpUdpPort();
            int originalProbePort=dnsPort;
            using var occupiedProbePort=new UdpClient(new IPEndPoint(IPAddress.Loopback,originalProbePort));
            using var router=new RouterService(RoutingTests.ModuleRoot,root){StartProcessOverride=(host,exe,arguments)=>{
                var path=arguments[Array.IndexOf(arguments,"-c")+1];var config=JsonNode.Parse(File.ReadAllText(path))!;
                dnsPort=PortStartup.Distinct(OpenVpnService.FreeTcpUdpPort,config["inbounds"]!.AsArray().Select(n=>n?["listen_port"]?.GetValue<int>()).Append(dnsPort).ToArray());
                config["inbounds"]!.AsArray().Add(new JsonObject{["type"]="direct",["tag"]="dns-probe",["listen"]="127.0.0.1",["listen_port"]=dnsPort,["network"]="udp",["override_address"]="1.1.1.1",["override_port"]=53});
                var vpnDns=config["dns"]!["servers"]!.AsArray().First(n=>n!["tag"]!.ToString()=="dns-vpn-origin")!;
                vpnDns.AsObject().Clear();
                vpnDns["type"]="udp";vpnDns["tag"]="dns-vpn-origin";vpnDns["server"]="127.0.0.3";vpnDns["server_port"]=53;vpnDns["detour"]="vpn-user";
                File.WriteAllText(path,config.ToJsonString());host.Start(exe,arguments);
            }};
            Assert.True(await Request(endpoint.Port,"public.example",echo.Port,true)); // Live endpoint positive control.
            Assert.True(await Request(echo.Port,"127.0.0.3",echo.Port,false));
            router.PrepareDomainOwnership(settings,false);
            await router.SetVpnAsync(settings,true,ct,physical:physical);
            Assert.NotEqual(originalProbePort,dnsPort);
            // The reserved synthetic zone is not installed into Windows DNS.
            // The existing resolver seam performs real UDP queries to its DNS
            // server; it does not replace domain/prefix authorization decisions.
            var direct=typeof(RouterService).GetField("guardedDirect",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(router)!;
            Func<string,CancellationToken,Task<IPAddress[]>> resolve=async (name,token)=>{
                var query=OpenVpnDestinationDns.Query(name);
                var link=new OpenVpnLink(physical.Name,physical.Index,physical.Address,"127.0.0.1","127.0.0.3");
                var response=await OpenVpnDestinationDns.ExchangeAsync(link,query,false,token);
                return OpenVpnDestinationDns.Parse(query,response).Addresses.Select(a=>a.Address).ToArray();
            };
            direct.GetType().GetProperty("ResolveHostOverride")!.SetValue(direct,resolve);
            var host=(ProcessHost)typeof(RouterService).GetField("core",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(router)!;
            var pid=host.Id;var revision=router.SessionRevision;var configBytes=File.ReadAllBytes(Path.Combine(root,"router.json"));
            int vpnBefore=endpoint.Connections,directBefore=echo.Received;
            Assert.True(await Request(router.ListenPort,"intranet.office.example",echo.Port,true));
            Assert.True(await Udp(router.ListenPort,((IPEndPoint)udpEcho.Client.LocalEndPoint!).Port));
            var answer=await Query("127.0.0.1",dnsPort,"intranet.office.example");
            Assert.NotNull(answer);Assert.Equal(0,answer![3]&15);Assert.Equal(1,answer[7]);
            Assert.False(router.DomainGuard.IsCorporate("intranet.office.example"));
            if(mode==RoutingMode.Rules){Assert.Equal(vpnBefore,endpoint.Connections);Assert.True(echo.Received>directBefore);Assert.True(resolver.Queries>0);}
            else{Assert.True(endpoint.Connections>vpnBefore);Assert.Contains("intranet.office.example",endpoint.Targets);Assert.Equal(directBefore,echo.Received);}
            router.PrepareDomainOwnership(settings,true);
            await router.ApplyOpenVpnOverlayAsync(settings,null,ct);
            // Requested ON with no verified link is Starting/Failed/Reconnecting, not OFF.
            Assert.True(router.DomainGuard.IsCorporate("intranet.office.example"));
            int protectedVpn=endpoint.Connections,protectedDns=resolver.Queries,protectedDirect=echo.Received;
            Assert.False(await Request(router.ListenPort,"intranet.office.example",echo.Port,true));
            answer=await Query("127.0.0.1",dnsPort,"intranet.office.example");
            Assert.True(answer is null || (answer[3]&15)!=0);
            Assert.Equal(protectedVpn,endpoint.Connections);Assert.Equal(protectedDns,resolver.Queries);Assert.Equal(protectedDirect,echo.Received);
            var sidecar=(OpenVpnSidecar)typeof(RouterService).GetField("openVpnSidecar",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(router)!;
            using(var locked=new FileStream(sidecar.Gateway.DomainsPath,FileMode.Open,FileAccess.Read,FileShare.None))
            {
                router.PrepareDomainOwnership(settings,false);
                Assert.False(router.DomainGuard.IsCorporate("intranet.office.example"));
                Assert.False(router.OpenVpnGatewayReady);
                router.PrepareDomainOwnership(settings,true);
                Assert.True(router.DomainGuard.IsCorporate("intranet.office.example"));
                Assert.True(router.OpenVpnGatewayReady);
                router.PrepareDomainOwnership(settings,false);
                Assert.False(router.OpenVpnGatewayReady);
            }
            router.PrepareDomainOwnership(settings,false);
            Assert.True(router.OpenVpnGatewayReady);
            var watch=System.Diagnostics.Stopwatch.StartNew();
            bool restored=false;
            while(watch.Elapsed<TimeSpan.FromSeconds(5) && !(restored=await Request(router.ListenPort,"intranet.office.example",echo.Port,true)))await Task.Delay(50,ct);
            Assert.True(restored);
            Assert.True(await Udp(router.ListenPort,((IPEndPoint)udpEcho.Client.LocalEndPoint!).Port));
            answer=await Query("127.0.0.1",dnsPort,"intranet.office.example");
            Assert.NotNull(answer);Assert.Equal(0,answer![3]&15);Assert.Equal(1,answer[7]);
            Assert.Equal(pid,host.Id);Assert.Equal(revision,router.SessionRevision);Assert.Equal(configBytes,File.ReadAllBytes(Path.Combine(root,"router.json")));
            Assert.Equal(originalSettings,JsonSerializer.Serialize(settings,JsonSettings.Options));
            var evidence=Environment.GetEnvironmentVariable("NETCAT_ACCEPTANCE_EVIDENCE");
            if(!string.IsNullOrEmpty(evidence)){Directory.CreateDirectory(evidence);File.WriteAllText(Path.Combine(evidence,"off-"+mode+".json"),JsonSerializer.Serialize(new{Mode=mode.ToString(),OffTcp="PASS",OffUdp="PASS",OffDns="PASS",PendingOnGuard="PASS",OffAgain="PASS",MainPid=pid,MainPidUnchanged=host.Id==pid,SessionRevisionUnchanged=router.SessionRevision==revision,MainConfigUnchanged=configBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"router.json"))),SettingsUnchanged=originalSettings==JsonSerializer.Serialize(settings,JsonSettings.Options),TunExercised=false,RealOpenVpnTransport=false}));}
            await router.SetVpnAsync(settings,false,ct,physical:physical);
            deadline.Cancel();await udpTask;
        }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private static async Task<bool> Udp(int port,int targetPort)
    {
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(2));using var client=new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback,port,ct.Token);var stream=client.GetStream();
        await stream.WriteAsync(new byte[]{5,1,0},ct.Token);await stream.ReadExactlyAsync(new byte[2],ct.Token);
        await stream.WriteAsync(new byte[]{5,3,0,1,127,0,0,1,0,0},ct.Token);var reply=new byte[10];await stream.ReadExactlyAsync(reply,ct.Token);
        Assert.Equal(0,reply[1]);using var udp=new UdpClient();udp.Connect(IPAddress.Loopback,(reply[8]<<8)|reply[9]);
        var packet=new byte[]{0,0,0,1,127,0,0,3,(byte)(targetPort>>8),(byte)targetPort,42};
        await udp.SendAsync(packet,ct.Token);var response=await udp.ReceiveAsync(ct.Token);return response.Buffer.SequenceEqual(packet);
    }
static async Task<byte[]?> Query(string server,int port,string name) {
 using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));using var client=new UdpClient();
 client.Connect(server,port);await client.SendAsync(OpenVpnDestinationDns.Query(name),timeout.Token);
 try{return(await client.ReceiveAsync(timeout.Token)).Buffer;}catch(OperationCanceledException){return null;}
}
static async Task<bool> Request(int listenerPort,string target,int targetPort,bool socks) {
 using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));using var client=new TcpClient();
 try{
  await client.ConnectAsync(socks?IPAddress.Loopback:IPAddress.Parse(target),socks?listenerPort:targetPort,timeout.Token);
  var stream=client.GetStream();
  if(socks){await stream.WriteAsync(new byte[]{5,1,0},timeout.Token);await stream.ReadExactlyAsync(new byte[2],timeout.Token);
   await stream.WriteAsync(new byte[]{5,1,0,3,(byte)target.Length}.Concat(Encoding.ASCII.GetBytes(target)).Concat(new byte[]{(byte)(targetPort>>8),(byte)targetPort}).ToArray(),timeout.Token);
   var reply=new byte[10];await stream.ReadExactlyAsync(reply,timeout.Token);if(reply[1]!=0)return false;}
  await stream.WriteAsync(new byte[]{42},timeout.Token);var response=new byte[1];await stream.ReadExactlyAsync(response,timeout.Token);return response[0]==42;
 }catch(Exception e)when(e is IOException or SocketException or OperationCanceledException){return false;}
}

    sealed class Resolver : IDisposable
    {
        private readonly UdpClient socket;private readonly CancellationTokenSource stop=new();public int Queries;
        public int Port=>((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        public Resolver(string ip,byte answer,int port=0){socket=new(new IPEndPoint(IPAddress.Parse(ip),port));_=Run(answer);}
        private async Task Run(byte answer){try{while(!stop.IsCancellationRequested){var q=await socket.ReceiveAsync(stop.Token);Interlocked.Increment(ref Queries);
            var b=q.Buffer.ToList();b[2]=0x81;b[3]=0x80;b[6]=0;b[7]=1;b.AddRange(new byte[]{0xc0,12,0,1,0,1,0,0,0,0,0,4,127,0,0,answer});await socket.SendAsync(b.ToArray(),q.RemoteEndPoint,stop.Token);}}
            catch(Exception e)when(e is SocketException or OperationCanceledException or ObjectDisposedException){}}
        public void Dispose(){stop.Cancel();socket.Dispose();}
    }
    sealed class Echo : IDisposable
    {
        private readonly TcpListener listener;private readonly CancellationTokenSource stop=new();public int Received;
        public int Port=>((IPEndPoint)listener.LocalEndpoint).Port;
        public Echo(string ip,int port=0){listener=new(IPAddress.Parse(ip),port);listener.Start();_=Run();}
        private async Task Run(){try{while(!stop.IsCancellationRequested){using var c=await listener.AcceptTcpClientAsync(stop.Token);Interlocked.Increment(ref Received);var b=new byte[1];await c.GetStream().ReadExactlyAsync(b,stop.Token);await c.GetStream().WriteAsync(b,stop.Token);}}
            catch(Exception e)when(e is IOException or SocketException or OperationCanceledException or ObjectDisposedException){}}
        public void Dispose(){stop.Cancel();listener.Stop();}
    }

    sealed class VpnEndpoint : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Targets = new();
        public int Connections;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public VpnEndpoint() { listener.Start(); _ = Run(); }
        private async Task Run()
        {
            try { while (!stop.IsCancellationRequested) { var c = await listener.AcceptTcpClientAsync(stop.Token); Interlocked.Increment(ref Connections); _ = Serve(c); } }
            catch (Exception e) when (e is SocketException or OperationCanceledException) { }
        }
        private async Task Serve(TcpClient c)
        {
            using(c) try {
                var s=c.GetStream(); var hello=new byte[3]; await s.ReadExactlyAsync(hello,stop.Token); await s.WriteAsync(new byte[]{5,0},stop.Token);
                var h=new byte[4]; await s.ReadExactlyAsync(h,stop.Token);
                int n=h[3]==1?4:h[3]==4?16:s.ReadByte(); var a=new byte[n]; await s.ReadExactlyAsync(a,stop.Token); var p=new byte[2]; await s.ReadExactlyAsync(p,stop.Token);
                if(h[1]==3)
                {
                    using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));int port=((IPEndPoint)udp.Client.LocalEndPoint!).Port;
                    await s.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,(byte)(port>>8),(byte)port},stop.Token);
                    using var done=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    async Task Close(){try{await s.ReadAsync(new byte[1],done.Token);}finally{done.Cancel();}}
                    var closed=Close();
                    try{while(!done.IsCancellationRequested){var packet=await udp.ReceiveAsync(done.Token);var b=packet.Buffer;
                        Targets.Enqueue(b[3]==3?Encoding.ASCII.GetString(b,5,b[4]):new IPAddress(b.AsSpan(4,b[3]==1?4:16)).ToString());
                                                int length=b[3]==3?7+b[4]:b[3]==1?10:22;
                        int destinationPort=(b[length-2]<<8)|b[length-1];
                        if(destinationPort==53) {
                            var query=b[length..];var answer=query.ToList();answer[2]=0x81;answer[3]=0x80;answer[6]=0;answer[7]=1;
                            answer.AddRange(new byte[]{0xc0,12,0,1,0,1,0,0,0,0,0,4,127,0,0,3});
                            b=b[..length].Concat(answer).ToArray();
                        }
                        await udp.SendAsync(b,packet.RemoteEndPoint,done.Token);}}
                    finally{done.Cancel();try{await closed;}catch(OperationCanceledException){}}
                    return;
                }
                Targets.Enqueue(h[3]==3?Encoding.ASCII.GetString(a):new IPAddress(a).ToString());
                await s.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,0},stop.Token);
                var buffer=new byte[1024]; int count; while((count=await s.ReadAsync(buffer,stop.Token))>0) await s.WriteAsync(buffer.AsMemory(0,count),stop.Token);
            } catch(Exception e) when(e is IOException or SocketException or OperationCanceledException) { }
        }
        public void Dispose(){stop.Cancel();listener.Stop();}
    }

}
