using System.Net.Sockets;

namespace NetCat.Network;

internal static class TcpRelay
{
    // EOF only closes the peer's send half. The reverse stream may still carry
    // a response. Idle timeout/failure/generation invalidation closes both.
    public static async Task CopyAsync(TcpClient left,TcpClient right,CancellationToken ct,TimeSpan? idle=null)
    {
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout=idle??TimeSpan.FromMinutes(2);stop.CancelAfter(timeout);
        var leftStream = left.GetStream(); var rightStream = right.GetStream();
        async Task Pump(NetworkStream from,NetworkStream to,Socket peer)
        {
            try
            {
                var buffer=new byte[16384];int count;
                while((count=await from.ReadAsync(buffer,stop.Token))>0)
                {await to.WriteAsync(buffer.AsMemory(0,count),stop.Token);stop.CancelAfter(timeout);}
                peer.Shutdown(SocketShutdown.Send);
            }
            catch{stop.Cancel();throw;}
        }
        await Task.WhenAll(Pump(leftStream,rightStream,right.Client),Pump(rightStream,leftStream,left.Client));
    }
}
