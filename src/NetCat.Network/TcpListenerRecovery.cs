using System.Net.Sockets;

namespace NetCat.Network;

internal static class TcpListenerRecovery
{
    public static async ValueTask<TcpClient> AcceptAsync(TcpListener listener, CancellationToken ct,
        Func<TcpListener, CancellationToken, ValueTask<TcpClient>>? accept = null)
    {
        var retryDelay = 100;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await (accept == null ? listener.AcceptTcpClientAsync(ct) : accept(listener, ct)).ConfigureAwait(false);
            }
            catch (SocketException e) when (e.SocketErrorCode is
                SocketError.NoBufferSpaceAvailable or SocketError.ConnectionAborted or
                SocketError.ConnectionReset or SocketError.NetworkReset or
                SocketError.NetworkDown or SocketError.Interrupted or SocketError.TryAgain or SocketError.TimedOut)
            {
                // A transient failure does not release this listening socket.
                // Preserve its ownership and retry without spinning or stacking
                // accept operations; disposal cancels the wait immediately.
                await Task.Delay(retryDelay, ct).ConfigureAwait(false);
                retryDelay = Math.Min(retryDelay * 2, 1000);
            }
        }
    }
}
