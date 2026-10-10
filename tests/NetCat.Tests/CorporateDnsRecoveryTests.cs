using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class CorporateDnsRecoveryTests
{
    private static byte[] Query(string label) =>
        new byte[] { 0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, (byte)label.Length }
        .Concat(System.Text.Encoding.ASCII.GetBytes(label))
        .Concat(new byte[] { 4, (byte)'t', (byte)'e', (byte)'s', (byte)'t', 0, 0, 1, 0, 1 }).ToArray();

    [Theory]
    [InlineData(SocketError.NoBufferSpaceAvailable)]
    [InlineData(SocketError.ConnectionReset)]
    public async Task DnsListenersRecoverTransientReceiveFailureAndPreserveCorporateGuard(SocketError failure)
    {
        var calls = new ConcurrentDictionary<UdpClient, int>();
        ValueTask<UdpReceiveResult> Receive(UdpClient socket, CancellationToken ct) =>
            calls.AddOrUpdate(socket, 1, (_, count) => count + 1) is 1 or 3
                ? ValueTask.FromException<UdpReceiveResult>(new SocketException((int)failure))
                : socket.ReceiveAsync(ct);
        var domains = new CorporateDomainGuard(); domains.Prepare("corp.test");
        using var guard = new CorporateDnsGuard(domains, receiveUdp: Receive);
        using var direct = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var vpn = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        guard.RestoreReturnPorts(((IPEndPoint)direct.Client.LocalEndPoint!).Port, ((IPEndPoint)vpn.Client.LocalEndPoint!).Port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        async Task Exchange(int ingress, UdpClient origin)
        {
            using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var query = Query("ordinary");
            await client.SendAsync(query, new IPEndPoint(IPAddress.Loopback, ingress), deadline.Token);
            var received = await origin.ReceiveAsync(deadline.Token);
            Assert.Equal(query, received.Buffer);
            var response = received.Buffer; response[2] = 0x81; response[3] = 0x80;
            await origin.SendAsync(response, received.RemoteEndPoint, deadline.Token);
            Assert.Equal(response, (await client.ReceiveAsync(deadline.Token)).Buffer);
            await client.SendAsync(Query("corp"), new IPEndPoint(IPAddress.Loopback, ingress), deadline.Token);
            Assert.Equal(5, (await client.ReceiveAsync(deadline.Token)).Buffer[3] & 15);
            Assert.Equal(0, origin.Available);
        }
        await Exchange(guard.DirectPort, direct);
        await Exchange(guard.VpnPort, vpn);
        Assert.Equal(2, calls.Count);
        Assert.All(calls.Values, count => Assert.True(count >= 5));
    }

    [Fact]
    public async Task PersistentReceiveResourceFailureHasBoundedRetryAndDisposalStopsIt()
    {
        var count = 0;
        ValueTask<UdpReceiveResult> Fail(UdpClient socket, CancellationToken ct)
        {
            Interlocked.Increment(ref count);
            return ValueTask.FromException<UdpReceiveResult>(new SocketException((int)SocketError.NoBufferSpaceAvailable));
        }
        using var guard = new CorporateDnsGuard(receiveUdp: Fail);
        await Task.Delay(450);
        Assert.InRange(Volatile.Read(ref count), 4, 12);
        guard.Dispose();
        await Task.Delay(100);
        var afterDispose = Volatile.Read(ref count);
        await Task.Delay(300);
        Assert.Equal(afterDispose, Volatile.Read(ref count));
    }
}
