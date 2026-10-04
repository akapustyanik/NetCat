using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace NetCat.Network;

// Public sockets never move to the child process. They remain exclusively owned
// from constructor until Dispose, including OFF, validation failures and crashes.
// Payload is authenticated/encrypted end-to-end by the native cores (AEAD-2022).
internal sealed class OpenVpnGatewayLease : IDisposable
{
    private readonly TcpListener tcp;
    private readonly UdpClient udp;
    private readonly TcpListener dnsTcp;
    private readonly UdpClient dnsUdp;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private RelayGeneration? current;
    private readonly Timer expiry;
    public TimeSpan UdpIdleTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
    public int UdpSessionCount { get { lock(gate) return current?.Clients.Count ?? 0; } }
    private sealed class UdpSession(UdpClient client,long now)
    {
        public UdpClient Client { get; } = client;
        public long LastActivity = now;
    }
    public int Port => ((IPEndPoint)tcp.LocalEndpoint).Port;
    public int DnsPort => ((IPEndPoint)dnsTcp.LocalEndpoint).Port;
    private sealed class RelayGeneration(int port)
    {
        public int Port { get; } = port;
        public CancellationTokenSource Stop { get; } = new();
        public ConcurrentDictionary<IPEndPoint, UdpSession> Clients { get; } = new();
        public void Close() { Stop.Cancel(); foreach (var session in Clients.Values) session.Client.Dispose(); Clients.Clear(); }
    }
    private static (TcpListener, UdpClient) Bind()
    {
        return PortStartup.BindTcpUdp();
    }
    public OpenVpnGatewayLease()
    {
        (tcp, udp) = Bind();
        try { (dnsTcp, dnsUdp) = Bind(); }
        catch { tcp.Stop(); udp.Dispose(); throw; }
        _ = AcceptAsync(tcp, false); _ = AcceptAsync(dnsTcp, true); _ = ReadUdpAsync();
        _ = DropDnsAsync(); // virtual DNS destination is only reachable INSIDE the encrypted transport
        expiry = new Timer(_ => ExpireIdle(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }
    public void ExpireIdle()
    {
        lock(gate)
        {
            if(current==null)return;
            foreach(var entry in current.Clients)
                if(TimeProvider.GetElapsedTime(entry.Value.LastActivity)>=UdpIdleTimeout && current.Clients.TryRemove(entry.Key,out var old))old.Client.Dispose();
        }
    }
    public void SetBackend(int? port)
    {
        lock (gate)
        {
            var old = current; current = port.HasValue && !lifetime.IsCancellationRequested ? new(port.Value) : null;
            old?.Close();
        }
    }
    private async Task AcceptAsync(TcpListener listener, bool reject)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                RelayGeneration? target; lock (gate) target = current;
                if (reject || target == null) client.Dispose(); else _ = RelayTcpAsync(client, target);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private static async Task RelayTcpAsync(TcpClient client, RelayGeneration target)
    {
        using (client)
        using (var backend = new TcpClient(AddressFamily.InterNetwork))
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(target.Stop.Token))
        {
            try
            {
                await backend.ConnectAsync(IPAddress.Loopback, target.Port, stop.Token).ConfigureAwait(false);
                await TcpRelay.CopyAsync(client,backend,stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
    private async Task ReadUdpAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var packet = await udp.ReceiveAsync(lifetime.Token).ConfigureAwait(false);
                RelayGeneration? target; UdpSession? session; ValueTask<int> send;
                try
                {
                lock (gate)
                {
                    target = current; if (target == null) continue;
                    ExpireIdle();
                    if (!target.Clients.TryGetValue(packet.RemoteEndPoint, out session))
                    {
                        // Bound resource use; overflow fails closed rather than leaking.
                        if (target.Clients.Count >= 256) continue;
                        var backend = new UdpClient(AddressFamily.InterNetwork); backend.Connect(IPAddress.Loopback, target.Port);
                        session = new(backend,TimeProvider.GetTimestamp());target.Clients[packet.RemoteEndPoint] = session;
                        _ = ReturnUdpAsync(target, session, packet.RemoteEndPoint);
                    }
                    session.LastActivity=TimeProvider.GetTimestamp();
                    send=session.Client.SendAsync(packet.Buffer,target.Stop.Token);
                }
                await send.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
    private async Task ReturnUdpAsync(RelayGeneration target, UdpSession session, IPEndPoint client)
    {
        try
        {
            while (!target.Stop.IsCancellationRequested)
            {
                var packet = await session.Client.ReceiveAsync(target.Stop.Token).ConfigureAwait(false);
                ValueTask<int> send;
                lock(gate)
                {
                    if(!ReferenceEquals(current,target)||!target.Clients.TryGetValue(client,out var owned)||!ReferenceEquals(owned,session))return;
                    session.LastActivity=TimeProvider.GetTimestamp();send=udp.SendAsync(packet.Buffer,client,target.Stop.Token);
                }
                await send.ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { lock(gate) { if(target.Clients.TryGetValue(client,out var owned)&&ReferenceEquals(owned,session))target.Clients.TryRemove(client,out _); } session.Client.Dispose(); }
    }
    private async Task DropDnsAsync()
    {
        try { while (!lifetime.IsCancellationRequested) await dnsUdp.ReceiveAsync(lifetime.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
    public void Dispose()
    {
        expiry.Dispose(); lifetime.Cancel(); SetBackend(null); tcp.Stop(); udp.Dispose(); dnsTcp.Stop(); dnsUdp.Dispose();
    }
}
