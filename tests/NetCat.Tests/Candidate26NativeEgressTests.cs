using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed class Candidate26NativeEgressTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly Candidate26RouteFixture Routes=new();
        public readonly ProcessHost Main=new();
        public readonly OpenVpnSidecar Sidecar;
        public readonly CancellationTokenSource Deadline=new(TimeSpan.FromSeconds(20));
        public int MainPort;
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Logs=new();
        public Fixture(){Sidecar=new(Path.Combine(RoutingTests.ModuleRoot,"sing-box","sing-box.exe"),Path.Combine(Routes.Root,"sidecar"),Routes.Leases){Log=line=>Logs.Enqueue(line)};Main.Line+=line=>Logs.Enqueue("main: "+line);}
        public async Task Start(RuleKind kind)
        {
            await Routes.Start();var settings=new AppSettings{Tun=false,Mode=RoutingMode.Rules};
            string value=kind switch{RuleKind.Process=>Process.GetCurrentProcess().ProcessName+".exe",RuleKind.ExecutablePath=>Environment.ProcessPath!,RuleKind.GeoIp=>"test",_=>"127.0.0.3/32"};
            settings.Rules.Add(new(){Kind=kind,Value=value,Target=RouteTarget.OpenVpn});
            if(kind==RuleKind.GeoIp)
            {
                byte[] Field(int n,byte[] bytes)=>[(byte)(n*8+2),(byte)bytes.Length,..bytes];
                byte[] data=Field(1,[..Field(1,Encoding.ASCII.GetBytes("test")),..Field(2,[..Field(1,[127,0,0,3]),16,32])]);
                var file=Geodata.FilePath(Routes.Root,RuleKind.GeoIp);Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllBytes(file,data);
            }
            await Sidecar.ApplyAsync(settings,Routes.Link,Deadline.Token);
            MainPort=OpenVpnService.FreeTcpUdpPort();
            var config=SingBoxConfig.Build(settings,new(Routes.Link.Name,Routes.Link.Index,"127.0.0.1","127.0.0.1",[]),null,null,false,MainPort,geodataDirectory:Routes.Root,openVpnGateway:Sidecar.Gateway);
            var path=Path.Combine(Routes.Root,"main.json");await File.WriteAllTextAsync(path,config.ToJsonString());
            Main.Start(Path.Combine(RoutingTests.ModuleRoot,"sing-box","sing-box.exe"),["run","-c",path]);await RouterService.WaitPortAsync(MainPort,Main,Deadline.Token);
        }
        public async Task<TcpClient> Connect(string host,int port,byte command=1)
        {
            var client=new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback,MainPort,Deadline.Token);var s=client.GetStream();
                await s.WriteAsync(new byte[]{5,1,0},Deadline.Token);await s.ReadExactlyAsync(new byte[2],Deadline.Token);
                await s.WriteAsync(new byte[]{5,command,0}.Concat(Candidate26EgressTests.Address(host,port)).ToArray(),Deadline.Token);return client;
            }
            catch{client.Dispose();throw;}
        }
        public async ValueTask DisposeAsync(){Main.Dispose();Sidecar.Dispose();Deadline.Dispose();await Routes.DisposeAsync();}
    }
    [Fact] public Task ExplicitOpenVpnIpRuleGetsLease()=>Route(RuleKind.IpCidr);
    [Fact] public Task ProcessOpenVpnRuleGetsDestinationLease()=>Route(RuleKind.Process);
    [Fact] public Task ExecutablePathOpenVpnRuleGetsDestinationLease()=>Route(RuleKind.ExecutablePath);
    [Fact] public Task GeoIpOpenVpnRuleGetsDestinationLease()=>Route(RuleKind.GeoIp);
    private static async Task Route(RuleKind kind)
    {
        await using var f=new Fixture();await f.Start(kind);
        using var echo=new TcpListener(IPAddress.Parse("127.0.0.3"),0);echo.Start();
        using var client=await f.Connect("127.0.0.3",((IPEndPoint)echo.LocalEndpoint).Port);var reply=new byte[10];await client.GetStream().ReadExactlyAsync(reply,f.Deadline.Token);Assert.Equal(0,reply[1]);
        await client.GetStream().WriteAsync(new byte[]{26},f.Deadline.Token);
        using var server=await echo.AcceptTcpClientAsync(f.Deadline.Token);var b=new byte[1];await server.GetStream().ReadExactlyAsync(b,f.Deadline.Token);Assert.Equal(26,b[0]);
        // A plain/direct fallback would reach loopback but would not acquire the
        // authenticated explicit egress lease; assert both data and route state.
        Assert.Contains(f.Routes.Capture(),r=>r.DestinationPrefix=="127.0.0.3/32");Assert.Equal(1,f.Routes.Leases.ReferenceCount(f.Routes.Leases.Capture(),IPAddress.Parse("127.0.0.3")));
    }
    [Fact] public async Task EncryptedNativeUdpUsesDestinationGateway()
    {
        await using var f=new Fixture();await f.Start(RuleKind.IpCidr);
        using var echo=new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.3"),0));int port=((IPEndPoint)echo.Client.LocalEndPoint!).Port;
        using var control=await f.Connect("0.0.0.0",0,3);var reply=new byte[10];await control.GetStream().ReadExactlyAsync(reply,f.Deadline.Token);Assert.Equal(0,reply[1]);
        using var client=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));var relay=new IPEndPoint(IPAddress.Loopback,reply[8]*256+reply[9]);
        await client.SendAsync(new byte[]{0,0,0}.Concat(Candidate26EgressTests.Address("127.0.0.3",port)).Append((byte)26).ToArray(),relay,f.Deadline.Token);
        UdpReceiveResult received;
        try{received=await echo.ReceiveAsync(f.Deadline.Token);}catch(OperationCanceledException){Assert.Fail("Native UDP did not arrive: "+string.Join(";",f.Logs));throw;}
        Assert.Equal(new byte[]{26},received.Buffer);
        try { Assert.Contains(f.Routes.Capture(),r=>r.DestinationPrefix=="127.0.0.3/32"); }
        catch(Xunit.Sdk.XunitException error) { throw new Xunit.Sdk.XunitException(error.Message+"; native diagnostics="+string.Join(";",f.Logs)+"; route commands="+string.Join(";",f.Routes.Commands)); }
        await echo.SendAsync(received.Buffer,received.RemoteEndPoint,f.Deadline.Token);Assert.Equal(26,(await client.ReceiveAsync(f.Deadline.Token)).Buffer[^1]);
    }
}
