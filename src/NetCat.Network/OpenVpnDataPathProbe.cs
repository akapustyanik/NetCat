using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using NetCat.Core;

namespace NetCat.Network;

// A transport check, not a corporate application-health assertion. The root NS
// question contains no customer hostname. Even REFUSED proves a round trip.
public static class OpenVpnDataPathProbe
{
    public static async Task<bool> CheckAsync(OpenVpnLink link, CancellationToken ct)
    {
        if (!IPAddress.TryParse(link.Dns, out var server) || server.AddressFamily != AddressFamily.InterNetwork) return false;
        using var socket = new TcpClient(AddressFamily.InterNetwork);
        socket.Client.Bind(new IPEndPoint(IPAddress.Parse(link.Address), 0));
        socket.Client.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(link.Index));
        await socket.ConnectAsync(server, link.DnsPort, ct).ConfigureAwait(false);
        byte[] query = [0,0,1,0,0,1,0,0,0,0,0,0,0,0,2,0,1];
        RandomNumberGenerator.Fill(query.AsSpan(0,2));
        var stream = socket.GetStream();
        await stream.WriteAsync(new byte[] { 0, (byte)query.Length }, ct).ConfigureAwait(false);
        await stream.WriteAsync(query, ct).ConfigureAwait(false);
        var length = new byte[2]; await stream.ReadExactlyAsync(length, ct).ConfigureAwait(false);
        var response = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
        await stream.ReadExactlyAsync(response, ct).ConfigureAwait(false);
        return IsResponse(query, response);
    }
    public static bool IsResponse(byte[] query, byte[] response) => response.Length >= 17 &&
        response[0] == query[0] && response[1] == query[1] && (response[2] & 0xF8) == 0x80 &&
        BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(4,2)) == 1 && response.AsSpan(12,5).SequenceEqual(query.AsSpan(12,5));

    public static async Task ValidateAsync(OpenVpnLink link, Func<OpenVpnLink,CancellationToken,Task<bool>> probe,
        Func<bool> current, CancellationToken ct, TimeSpan attemptTimeout, int attempts = 3)
    {
        for (int i=0;i<attempts;i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!current()) throw new OperationCanceledException("Obsolete OpenVPN probe.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(attemptTimeout);
            try
            {
                if (await probe(link, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!current()) throw new OperationCanceledException("Obsolete OpenVPN probe.");
                    return;
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is SocketException or IOException or OperationCanceledException) { }
            ct.ThrowIfCancellationRequested();
            if(!current())throw new OperationCanceledException("Obsolete OpenVPN probe.");
            if(i+1<attempts)await Task.Delay(TimeSpan.FromMilliseconds(250),ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        if (!current()) throw new OperationCanceledException("Obsolete OpenVPN probe.");
        throw new TimeoutException("OpenVPN datapath probe timed out.");
    }
}
