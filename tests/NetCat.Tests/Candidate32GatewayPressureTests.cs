using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using NetCat.Core;
using Xunit;

namespace NetCat.Tests;

public sealed partial class Candidate21DomainTests
{
    // A real SOCKS UDP relay can share one bound UDP port across associations,
    // as native Xray does. Preserve per-source payload/peer identity without
    // spending a test-only UDP listener for every retained association.
    private sealed class PressureVpnEndpoint : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly UdpClient relay = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource stop = new();
        public readonly ConcurrentQueue<string> Targets = new();
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public PressureVpnEndpoint() { listener.Start(); _ = Accept(); _ = EchoUdp(); }
        private async Task Accept()
        {
            try { while (!stop.IsCancellationRequested) _ = Serve(await listener.AcceptTcpClientAsync(stop.Token)); }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        private async Task EchoUdp()
        {
            try { while (!stop.IsCancellationRequested) {
                var packet = await relay.ReceiveAsync(stop.Token);
                if (packet.Buffer.Length < 5 || packet.Buffer[3] != 3) continue;
                Targets.Enqueue(Encoding.ASCII.GetString(packet.Buffer, 5, packet.Buffer[4]));
                await relay.SendAsync(packet.Buffer, packet.RemoteEndPoint, stop.Token);
            } }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        private async Task Serve(TcpClient client)
        {
            using (client) try {
                var s = client.GetStream(); var greeting = new byte[3]; await s.ReadExactlyAsync(greeting, stop.Token);
                await s.WriteAsync(new byte[] { 5, 0 }, stop.Token); var h = new byte[4]; await s.ReadExactlyAsync(h, stop.Token);
                int count = h[3] == 1 ? 4 : h[3] == 4 ? 16 : s.ReadByte();
                var address = new byte[count]; await s.ReadExactlyAsync(address, stop.Token); await s.ReadExactlyAsync(new byte[2], stop.Token);
                int port = h[1] == 3 ? ((IPEndPoint)relay.Client.LocalEndPoint!).Port : 0;
                await s.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, (byte)(port >> 8), (byte)port }, stop.Token);
                if (h[1] == 3) { await s.ReadAsync(new byte[1], stop.Token); return; }
                Targets.Enqueue(h[3] == 3 ? Encoding.ASCII.GetString(address) : new IPAddress(address).ToString());
                var buffer = new byte[1024]; int read; while ((read = await s.ReadAsync(buffer, stop.Token)) > 0) await s.WriteAsync(buffer.AsMemory(0, read), stop.Token);
            } catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        public void Dispose() { stop.Cancel(); listener.Stop(); relay.Dispose(); }
    }

    private sealed record HeldUdp(TcpClient Control, UdpClient Socket) : IDisposable
    { public void Dispose() { Socket.Dispose(); Control.Dispose(); } }

    private static async Task<HeldUdp> OpenHeldUdp(int port, int index, CancellationToken ct)
    {
        var control = new TcpClient(); UdpClient? udp = null;
        try
        {
            await control.ConnectAsync(IPAddress.Loopback, port, ct);
            var stream = control.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, ct);
            var method = new byte[2]; await stream.ReadExactlyAsync(method, ct);
            Assert.Equal(new byte[] { 5, 0 }, method);
            await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 127, 0, 0, 1, 0, 0 }, ct);
            var reply = new byte[10]; await stream.ReadExactlyAsync(reply, ct);
            Assert.Equal(0, reply[1]);
            udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            udp.Connect(IPAddress.Loopback, reply[8] * 256 + reply[9]);
            const string host = "public.example.test";
            var payload = new byte[] { 0, 0, 0, 3, (byte)host.Length }
                .Concat(Encoding.ASCII.GetBytes(host)).Concat(new byte[] { 0, 80 })
                .Concat(BitConverter.GetBytes(index)).Concat(new byte[64]).ToArray();
            await udp.SendAsync(payload, ct);
            using var responseDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            responseDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            Assert.Equal(payload, (await udp.ReceiveAsync(responseDeadline.Token)).Buffer);
            return new(control, udp);
        }
        catch { control.Dispose(); udp?.Dispose(); throw; }
    }

    private static void ConfigurePressureVpn(Candidate21PortTests.Fixture f, PressureVpnEndpoint endpoint)
    {
        var p = f.Settings.Profiles.Single(); p.Port = endpoint.Port;
        p.OutboundJson = new JsonObject { ["type"] = "socks", ["server"] = "127.0.0.1", ["server_port"] = endpoint.Port }.ToJsonString();
        f.Settings.Mode = RoutingMode.Global;
    }

    [Fact]
    public async Task GuardedVpnUdpPressureDoesNotStarveTcpOrBypassCorporateOwnership()
    {
        using var endpoint = new PressureVpnEndpoint(); using var f = new Candidate21PortTests.Fixture();
        ConfigurePressureVpn(f, endpoint); await f.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var held = new List<HeldUdp>();
        try
        {
            for (int i = 0; i < 512; i++) held.Add(await OpenHeldUdp(f.Router.ListenPort, i, timeout.Token));
            Assert.True(await Request(f.Router.ListenPort, "public.example.test"), "Held UDP must not exhaust TCP admission.");
            f.Settings.OpenVpnDomains = "corp.test"; f.Router.PrepareDomainOwnership(f.Settings);
            Assert.False(await Request(f.Router.ListenPort, "fresh.corp.test"));
            Assert.DoesNotContain("fresh.corp.test", endpoint.Targets);
            Assert.True(await Request(f.Router.ListenPort, "another.public.example.test"));
        }
        finally { foreach (var session in held) session.Dispose(); }
    }

    [Fact]
    public async Task GuardedVpnRetainedSequentialUdpAnd256ConcurrentSessionsAllTransferData()
    {
        using var endpoint = new PressureVpnEndpoint(); using var f = new Candidate21PortTests.Fixture();
        ConfigurePressureVpn(f, endpoint); await f.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var held = new ConcurrentBag<HeldUdp>();
        try
        {
            for (int i = 0; i < 310; i++) held.Add(await OpenHeldUdp(f.Router.ListenPort, i, timeout.Token));
            await Task.WhenAll(Enumerable.Range(310, 256).Select(async i => held.Add(await OpenHeldUdp(f.Router.ListenPort, i, timeout.Token))));
            Assert.Equal(566, held.Count);
            Assert.True(await Request(f.Router.ListenPort, "public.example.test"));
            Assert.True(endpoint.Targets.Count(t => t == "public.example.test") >= 566);
        }
        finally { foreach (var session in held) session.Dispose(); }
    }
}
