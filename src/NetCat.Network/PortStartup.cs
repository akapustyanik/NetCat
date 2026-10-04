using System.Net.Sockets;
using System.Net;
using System.Security.Cryptography;
namespace NetCat.Network;

public sealed class PortCollisionException(string message):IOException(message);
public static class PortStartup
{
    // TCP and UDP have independent allocation/exclusion tables. These listeners
    // require the same numerical port for BOTH transports; retain both leases.
    // Random sampling avoids walking a long contiguous exclusion in one protocol;
    // the actual exclusive binds, not the candidate number/range, establish ownership.
    public static (TcpListener Tcp, UdpClient Udp) BindTcpUdp(Func<int>? choosePort = null)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            int port = choosePort?.Invoke() ?? RandomNumberGenerator.GetInt32(49152, 65536);
            var tcp = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.ExclusiveAddressUse = true;
            try { tcp.Start(); udp.Client.Bind(tcp.LocalEndpoint); return (tcp, udp); }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            { tcp.Stop(); udp.Dispose(); }
            catch { tcp.Stop(); udp.Dispose(); throw; }
        }
        throw new PortCollisionException("Не удалось закрепить внутренний TCP/UDP-порт после 100 попыток.");
    }
    public static int Distinct(Func<int> allocate,params int?[] excluded)
    {
        for(int i=0;i<8;i++){var port=allocate();if(port is >0 and <=65535 && !excluded.Contains(port))return port;}
        throw new PortCollisionException("Не удалось выбрать разные внутренние порты.");
    }
    public static bool IsCollision(string text) => text.Contains("address already in use",StringComparison.OrdinalIgnoreCase)
        || text.Contains("Only one usage of each socket address",StringComparison.OrdinalIgnoreCase)
        || text.Contains("WSAEADDRINUSE",StringComparison.OrdinalIgnoreCase)
        || text.Contains("listen",StringComparison.OrdinalIgnoreCase) && text.Contains("bind:",StringComparison.OrdinalIgnoreCase)
            && text.Contains("forbidden by its access permissions",StringComparison.OrdinalIgnoreCase);
    public static async Task<T> RetryAsync<T>(Func<int,Task<T>> start,CancellationToken ct,int attempts=3)
    {
        if(attempts is <1 or >5)throw new ArgumentOutOfRangeException(nameof(attempts));
        for(var i=0;;i++)
        {
            ct.ThrowIfCancellationRequested();
            try{return await start(i);}
            catch(PortCollisionException)when(i+1<attempts) { }
            catch(SocketException e)when(e.SocketErrorCode==SocketError.AddressAlreadyInUse && i+1<attempts) { }
        }
    }
}
