using System.Net.Sockets;
namespace NetCat.Network;

public sealed class PortCollisionException(string message):IOException(message);
public static class PortStartup
{
    public static int Distinct(Func<int> allocate,params int?[] excluded)
    {
        for(int i=0;i<8;i++){var port=allocate();if(port is >0 and <=65535 && !excluded.Contains(port))return port;}
        throw new PortCollisionException("Не удалось выбрать разные внутренние порты.");
    }
    public static bool IsCollision(string text) => text.Contains("address already in use",StringComparison.OrdinalIgnoreCase)
        || text.Contains("Only one usage of each socket address",StringComparison.OrdinalIgnoreCase)
        || text.Contains("WSAEADDRINUSE",StringComparison.OrdinalIgnoreCase);
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
