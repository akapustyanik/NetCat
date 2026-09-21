using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetCat.Core;

namespace NetCat.Network;
public sealed class SystemTunnelHealth : IDisposable
{
    [DllImport("iphlpapi.dll")] private static extern uint GetBestInterface(uint destination, out uint index);
    private HttpClient? client;
    private int boundPort;
    public void Dispose() { client?.Dispose(); client = null; }
    public async Task<DelayResult> CheckAsync(int sourcePort, CancellationToken ct)
    {
        var tun = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n=>n.Name=="NetCat-TUN" && n.OperationalStatus==OperationalStatus.Up);
        if (tun == null || sourcePort == 0) return new(false,-1,"Системный туннель не готов.");
        if (GetBestInterface(0x01010101,out var best) != 0 || best != tun.GetIPProperties().GetIPv4Properties().Index)
            return new(false,-1,"Маршрут Windows до адреса проверки проходит вне NetCat-TUN.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        if (client == null || boundPort != sourcePort)
        {
        client?.Dispose(); boundPort = sourcePort;
        var handler = new SocketsHttpHandler { UseProxy=false, AllowAutoRedirect=false,
            ConnectCallback=async (_,token)=>
            {
                var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp) { LingerState=new LingerOption(true,0) };
                try
                {
                    socket.ExclusiveAddressUse = false;
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    socket.Bind(new IPEndPoint(IPAddress.Parse("172.29.255.1"),sourcePort));
                    await socket.ConnectAsync(IPAddress.Parse("1.1.1.1"),443,token);
                    return new NetworkStream(socket,ownsSocket:true);
                }
                catch { socket.Dispose(); throw; }
            } };
        client=new HttpClient(handler);
        }
        try
        {
            var watch=Stopwatch.StartNew();
            using var response=await client.GetAsync("https://1.1.1.1/cdn-cgi/trace",HttpCompletionOption.ResponseContentRead,timeout.Token);
            return response.IsSuccessStatusCode ? new(true,(int)watch.ElapsedMilliseconds) : new(false,-1,"HTTP "+(int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false,-1,"Туннель не ответил за 8 секунд."); }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException)
        {
            for(Exception? inner=ex; inner!=null; inner=inner.InnerException)
                if(inner is SocketException socket && socket.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
                    return new(false,-1,"Локальный порт проверки занят или недоступен. Это не подтверждает потерю доступа.");
            return new(false,-1,ex.Message);
        }
    }
}
