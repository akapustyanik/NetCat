using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class TcpListenerRecoveryTests
{
    [Theory]
    [InlineData(SocketError.NoBufferSpaceAvailable)]
    [InlineData(SocketError.ConnectionAborted)]
    public async Task OpenVpnGatewayAcceptsAuthenticatedRequestsAfterTransientFailure(SocketError failure)
    {
        int calls = 0;
        ValueTask<TcpClient> Accept(TcpListener listener, CancellationToken ct) =>
            Interlocked.Increment(ref calls) is 1 or 3
                ? ValueTask.FromException<TcpClient>(new SocketException((int)failure))
                : listener.AcceptTcpClientAsync(ct);
        await using var routes = new Candidate26RouteFixture(); await routes.Start();
        var initialCommands = routes.Commands.ToArray();
        using var gateway = new OpenVpnDestinationGateway(routes.Leases, () => Environment.ProcessId, Accept);
        gateway.Activate();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        for (int i = 0; i < 2; i++)
        {
            using var client = await Candidate26EgressTests.Authenticate(gateway, deadline.Token);
        }
        Assert.True(Volatile.Read(ref calls) >= 4);
        Assert.Equal(initialCommands, routes.Commands);
    }

    private static byte[] Query(string label) =>
        new byte[] { 0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, (byte)label.Length }
        .Concat(System.Text.Encoding.ASCII.GetBytes(label))
        .Concat(new byte[] { 4, (byte)'t', (byte)'e', (byte)'s', (byte)'t', 0, 0, 1, 0, 1 }).ToArray();

    [Theory]
    [InlineData(SocketError.NoBufferSpaceAvailable)]
    [InlineData(SocketError.ConnectionAborted)]
    public async Task BothDnsTcpListenersResumeAfterTransientAcceptFailure(SocketError failure)
    {
        var calls = new ConcurrentDictionary<TcpListener, int>();
        ValueTask<TcpClient> Accept(TcpListener listener, CancellationToken ct) =>
            calls.AddOrUpdate(listener, 1, (_, count) => count + 1) is 1 or 3
                ? ValueTask.FromException<TcpClient>(new SocketException((int)failure))
                : listener.AcceptTcpClientAsync(ct);
        var domains = new CorporateDomainGuard(); domains.Prepare("corp.test");
        using var guard = new CorporateDnsGuard(domains, acceptTcp: Accept);
        using var upstream = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var upstreamPort = ((IPEndPoint)upstream.Client.LocalEndPoint!).Port;
        guard.RestoreReturnPorts(upstreamPort, upstreamPort);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        async Task<byte[]> Exchange(int ingress, byte[] query)
        {
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, ingress, deadline.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { (byte)(query.Length >> 8), (byte)query.Length }.Concat(query).ToArray(), deadline.Token);
            if (query.SequenceEqual(Query("ordinary")))
            {
                var received = await upstream.ReceiveAsync(deadline.Token); Assert.Equal(query, received.Buffer);
                received.Buffer[2] = 0x81; received.Buffer[3] = 0x80;
                await upstream.SendAsync(received.Buffer, received.RemoteEndPoint, deadline.Token);
            }
            var prefix = new byte[2]; await stream.ReadExactlyAsync(prefix, deadline.Token);
            var answer = new byte[prefix[0] * 256 + prefix[1]]; await stream.ReadExactlyAsync(answer, deadline.Token);
            return answer;
        }
        foreach (var ingress in new[] { guard.DirectPort, guard.VpnPort })
        {
            Assert.Equal(0, (await Exchange(ingress, Query("ordinary")))[3] & 15);
            Assert.Equal(5, (await Exchange(ingress, Query("corp")))[3] & 15);
            Assert.Equal(0, upstream.Available);
        }
        Assert.Equal(2, calls.Count); Assert.All(calls.Values, count => Assert.True(count >= 4));
    }

    [Fact]
    public async Task PersistentAcceptFailureBacksOffAndStopsOnDisposal()
    {
        int calls = 0;
        ValueTask<TcpClient> Fail(TcpListener listener, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromException<TcpClient>(new SocketException((int)SocketError.NoBufferSpaceAvailable));
        }
        using var guard = new CorporateDnsGuard(acceptTcp: Fail);
        await Task.Delay(450); Assert.InRange(Volatile.Read(ref calls), 4, 12);
        guard.Dispose(); await Task.Delay(100); var after = Volatile.Read(ref calls);
        await Task.Delay(300); Assert.Equal(after, Volatile.Read(ref calls));
    }
}
