using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;
namespace NetCat.Tests;
public sealed class ServiceRoutesTests
{
    [Theory]
    [InlineData(RoutingMode.Global,true,true,false)][InlineData(RoutingMode.Rules,true,true,false)]
    [InlineData(RoutingMode.Global,true,false,false)][InlineData(RoutingMode.Rules,true,false,false)]
    [InlineData(RoutingMode.Global,false,false,false)][InlineData(RoutingMode.Rules,false,false,false)]
    [InlineData(RoutingMode.Global,true,true,true)][InlineData(RoutingMode.Rules,true,true,true)]
    public async Task RealRequestsRespectZapretAndTelegramRoutes(RoutingMode mode, bool vpnDefault, bool externalSocks, bool userOverrides)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));var ct=deadline.Token;
        var physical=PhysicalNetwork.Capture(""); var address=IPAddress.Parse(physical.Address);
        using var direct=new TcpListener(address,0); using var vpn=new TcpListener(address,0); using var telegram=new TcpListener(address,0);
        direct.Start();vpn.Start();telegram.Start();
        using var dns=new UdpClient(new IPEndPoint(address,0));
        var dnsTask=Task.Run(async()=> {while(!ct.IsCancellationRequested)
        {
            var query=await dns.ReceiveAsync(ct);var q=query.Buffer;int end=12;while(q[end]!=0)end+=1+q[end];end+=5;
            bool ipv4=q[end-4]==0&&q[end-3]==1;var answer=q[..end].ToList();answer[2]=0x81;answer[3]=0x80;answer[6]=0;answer[7]=(byte)(ipv4?1:0);answer[8]=answer[9]=answer[10]=answer[11]=0;
            if(ipv4) answer.AddRange(new byte[]{0xC0,0x0C,0,1,0,1,0,0,0,60,0,4}.Concat(address.GetAddressBytes()));
            await dns.SendAsync(answer.ToArray(),query.RemoteEndPoint,ct);
        }});
        var servers=new[]{Serve(direct,false,"direct",ct),Serve(vpn,true,"vpn",ct),Serve(telegram,true,"telegram",ct)};
        int Port(TcpListener listener)=>((IPEndPoint)listener.LocalEndpoint).Port;
        var profile=ProfileImporter.ParseLink($"socks://{address}:{Port(vpn)}");
        var settings=new AppSettings {Mode=mode,YouTube=ServiceRoute.Zapret,Discord=ServiceRoute.Zapret,TelegramVpnDefault=vpnDefault,TelegramSocks=externalSocks,TelegramSocksHost=address.ToString(),TelegramSocksPort=Port(telegram)};
        if(userOverrides) settings.Rules=[
            new() {Kind=RuleKind.Domain,Value="youtube.com",Target=RouteTarget.Vpn},
            new() {Kind=RuleKind.Domain,Value="discordapp.net",Target=RouteTarget.Vpn},
            new() {Kind=RuleKind.Domain,Value="telegram.org",Target=RouteTarget.Vpn},
            new() {Kind=RuleKind.IpCidr,Value="149.154.167.50/32",Target=RouteTarget.Vpn}
        ];
        var local=OpenVpnService.FreePort(); var config=SingBoxConfig.Build(settings,physical,profile,null,false,local);
        var resolver=config["dns"]!["servers"]!.AsArray().First(x=>x?["tag"]?.ToString()=="dns-direct")!;resolver["server"]=address.ToString();resolver["server_port"]=((IPEndPoint)dns.Client.LocalEndPoint!).Port;
        var root=Path.Combine(RoutingTests.FindRoot(),"artifacts","service-route-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var path=Path.Combine(root,"router.json");
        await File.WriteAllTextAsync(path,config.ToJsonString(),ct);
        using var core=new ProcessHost();core.Start(Path.Combine(RoutingTests.FindRoot(),"bin","sing-box","sing-box.exe"),["run","-c",path]);await RouterService.WaitPortAsync(local,core,ct);
        using var client=new HttpClient(new SocketsHttpHandler {Proxy=new WebProxy($"socks5://127.0.0.1:{local}"),UseProxy=true});
        try
        {
            Assert.Equal(userOverrides?"vpn":"direct",await client.GetStringAsync($"http://www.youtube.com:{Port(direct)}/probe",ct));
            Assert.Equal(userOverrides?"vpn":"direct",await client.GetStringAsync($"http://media.discordapp.net:{Port(direct)}/probe",ct));
            Assert.Equal("direct",await client.GetStringAsync($"http://yt3.googleusercontent.com:{Port(direct)}/probe",ct));
            Assert.Equal("direct",await client.GetStringAsync($"http://youtubeembeddedplayer.googleapis.com:{Port(direct)}/probe",ct));
            Assert.Equal("direct",await client.GetStringAsync($"http://discord-attachments-uploads-prd.storage.googleapis.com:{Port(direct)}/probe",ct));
            var expectedTelegram=userOverrides?"vpn":externalSocks?"telegram":vpnDefault?"vpn":"direct";
            Assert.Equal(expectedTelegram,await client.GetStringAsync($"http://api.telegram.org:{Port(direct)}/probe",ct));
            if(expectedTelegram!="direct") Assert.Equal(expectedTelegram,await client.GetStringAsync("http://149.154.167.50/probe",ct));
            if(mode==RoutingMode.Global) Assert.Equal("vpn",await client.GetStringAsync("http://198.51.100.99/probe",ct));
            // An unavailable explicitly selected proxy must not leak Telegram to the VPN fallback.
            if(externalSocks && !userOverrides)
            {
                telegram.Stop();
                await Assert.ThrowsAsync<HttpRequestException>(()=>client.GetStringAsync("http://api.telegram.org/probe",ct));
            }
        }
        finally
        {
            await core.StopAsync();deadline.Cancel();
            foreach(var server in servers.Append(dnsTask)) try{await server;}catch(Exception e)when(e is OperationCanceledException or SocketException or ObjectDisposedException){}
        }
    }
    private static async Task Serve(TcpListener listener,bool socks,string label,CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            using var connection=await listener.AcceptTcpClientAsync(ct);using var stream=connection.GetStream();
            if(socks)
            {
                var header=new byte[2];await stream.ReadExactlyAsync(header,ct);await stream.ReadExactlyAsync(new byte[header[1]],ct);await stream.WriteAsync(new byte[]{5,0},ct);
                var request=new byte[4];await stream.ReadExactlyAsync(request,ct);int length=request[3] switch{1=>4,4=>16,3=>await ReadByte(stream,ct),_=>throw new IOException("SOCKS address")};
                await stream.ReadExactlyAsync(new byte[length+2],ct);await stream.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,80},ct);
            }
            using var reader=new StreamReader(stream,Encoding.ASCII,leaveOpen:true);var first=await reader.ReadLineAsync(ct);if(first==null)continue;while(!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))){}
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {label.Length}\r\nConnection: close\r\n\r\n{label}"),ct);
        }
    }
    private static async Task<int> ReadByte(Stream stream,CancellationToken ct) {var value=new byte[1];await stream.ReadExactlyAsync(value,ct);return value[0];}
    [Theory][InlineData(RoutingMode.Global)][InlineData(RoutingMode.Rules)]
    public void ServiceDnsAndWorkerBypassMatchTheirRoutes(RoutingMode mode)
    {
        var c=SingBoxConfig.Build(new AppSettings{Mode=mode,YouTube=ServiceRoute.Zapret,Discord=ServiceRoute.Zapret,TelegramSocks=true},new("Ethernet",1,"192.168.1.2","192.168.1.1",[]),ProfileImporter.ParseLink("socks://192.0.2.1:1080"),null,false);
        var dns=c["dns"]!["rules"]!.AsArray();foreach(var host in new[]{"youtube.com","discord.com","telegram.org"})Assert.Equal("dns-direct",dns.First(x=>x?["domain_suffix"]?.ToJsonString().Contains(host)==true)!["server"]!.ToString());
        var rules=c["route"]!["rules"]!.AsArray();var worker=rules.First(x=>x?["process_name"]?.ToJsonString().Contains("NetCat.Telegram.exe")==true)!;Assert.Equal("direct",worker["outbound"]!.ToString());
        Assert.True(rules.IndexOf(worker)<rules.IndexOf(rules.First(x=>x?["outbound"]?.ToString()=="telegram")));
    }
}
