using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using NetCat.Core;
using NetCat.Engine;
namespace NetCat.Network;

public sealed record ServiceProbe(string Service,string Name,string Url,bool Gateway=false);
public sealed record ProbeOutcome(string Service,string Name,bool Success,int Milliseconds,string Detail);
public static class ZapretProbes
{
    public static IReadOnlyList<ServiceProbe> Defaults {get;} = Array.AsReadOnly(new[] {
        new ServiceProbe("YouTube","HTTPS","https://www.youtube.com/"),
        new ServiceProbe("YouTube","HTTPS · Player API","https://www.youtube.com/iframe_api"),
        new ServiceProbe("Discord","HTTPS","https://discord.com/"),
        new ServiceProbe("Discord","Gateway discovery","https://discord.com/api/v10/gateway",true)
    });
    public static async Task<List<ProbeOutcome>> RunAsync(HttpClient client,IEnumerable<ServiceProbe> probes,CancellationToken ct,
        Func<Uri,CancellationToken,Task>? webSocket=null)
    {
        var outcomes=new List<ProbeOutcome>();
        foreach(var probe in probes)
        {
            ct.ThrowIfCancellationRequested();var watch=Stopwatch.StartNew();Uri? gateway=null;
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(client.Timeout==Timeout.InfiniteTimeSpan?TimeSpan.FromSeconds(20):client.Timeout);
            try
            {
                using var response=await client.GetAsync(probe.Url,HttpCompletionOption.ResponseHeadersRead,deadline.Token);response.EnsureSuccessStatusCode();
                if(probe.Gateway)
                {
                    await using var body=await response.Content.ReadAsStreamAsync(deadline.Token);using var buffer=new MemoryStream();var chunk=new byte[4096];int read;
                    while((read=await body.ReadAsync(chunk,deadline.Token))>0) {if(buffer.Length+read>65536)throw new InvalidDataException("Слишком большой ответ Gateway.");buffer.Write(chunk,0,read);}
                    using var json=JsonDocument.Parse(buffer.ToArray());
                    if(json.RootElement.ValueKind!=JsonValueKind.Object || !json.RootElement.TryGetProperty("url",out var urlProperty) || urlProperty.ValueKind!=JsonValueKind.String)throw new InvalidDataException("Gateway не вернул URL.");
                    var url=urlProperty.GetString();
                    if(!Uri.TryCreate(url,UriKind.Absolute,out gateway) || gateway.Scheme!="wss" || gateway.Host!="gateway.discord.gg" || !gateway.IsDefaultPort || gateway.UserInfo.Length>0) throw new InvalidDataException("Некорректный адрес Discord Gateway.");
                }
                outcomes.Add(new(probe.Service,probe.Name,true,(int)watch.ElapsedMilliseconds,"HTTP "+(int)response.StatusCode));
            }
            catch(Exception ex) when(ex is HttpRequestException or InvalidDataException or JsonException or KeyNotFoundException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {outcomes.Add(new(probe.Service,probe.Name,false,0,ProcessHost.Redact(ex.Message)));continue;}
            if(gateway!=null && webSocket!=null)
            {
                watch.Restart();
                try {await webSocket(gateway,ct);outcomes.Add(new(probe.Service,"WebSocket · Hello",true,(int)watch.ElapsedMilliseconds,"Получен Gateway Hello"));}
                catch(Exception ex) when(ex is WebSocketException or HttpRequestException or InvalidDataException || ex is OperationCanceledException && !ct.IsCancellationRequested)
                {outcomes.Add(new(probe.Service,"WebSocket · Hello",false,0,ProcessHost.Redact(ex.Message)));}
            }
        }
        return outcomes;
    }
    public static async Task GatewayHelloAsync(Uri gateway,int proxyPort,int timeout,CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
        using var socket=new ClientWebSocket();socket.Options.Proxy=new WebProxy($"socks5://127.0.0.1:{proxyPort}");
        var uri=new UriBuilder(gateway) {Query="v=10&encoding=json"}.Uri;
        await socket.ConnectAsync(uri,deadline.Token);
        var bytes=new byte[16384];int length=0;
        while(true)
        {
            var received=await socket.ReceiveAsync(bytes.AsMemory(length),deadline.Token);length+=received.Count;
            if(received.MessageType!=WebSocketMessageType.Text) throw new InvalidDataException("Gateway не прислал текстовый Hello.");
            if(received.EndOfMessage)break;
            if(length==bytes.Length)throw new InvalidDataException("Gateway Hello превышает допустимый размер.");
        }
        using var json=JsonDocument.Parse(bytes.AsMemory(0,length));
        if(!json.RootElement.TryGetProperty("op",out var op) || op.GetInt32()!=10)throw new InvalidDataException("Gateway Hello не получен.");
        socket.Abort(); // No login/token/voice session is created by this transport probe.
    }
    public static string Summary(IEnumerable<ProbeOutcome> outcomes,string service)
    {
        var rows=outcomes.Where(p=>p.Service==service).ToArray();
        if(rows.Length==0)return "Через VPN";
        var text=string.Join("\n",rows.Select(p=>p.Name+": "+(p.Success?$"доступен · {p.Milliseconds} мс":"нет ответа")));
        if(service=="Discord")text+="\nVoice UDP: не проверялось";
        return text;
    }
}
