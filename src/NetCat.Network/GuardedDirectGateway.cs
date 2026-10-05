using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NetCat.Core;

namespace NetCat.Network;

// Fixed authenticated SOCKS endpoint in the main config. Every automatic direct
// send consults in-process ownership, independently of native rule-set reload.
// User-authored Direct rules use the explicit physical outbound instead.
internal sealed class GuardedDirectGateway : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private readonly Dictionary<(Guid Profile, long Generation), string[]> candidates = [];
    private string[] known = [];
    private (uint Network, int Length)[] blocked = [];
    private NetworkSnapshot? physical;
    private readonly ConcurrentDictionary<long, (IPAddress? Destination, CancellationTokenSource Stop)> sessions = new();
    private long sequence;
    private bool protectionEnabled = true;
    private readonly LocalSocksAdmission admission = new();

    public CorporateDomainGuard DomainGuard { get; }
    public string Username { get; } = "netcat-direct";
    public string Password { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public GuardedDirectGateway(CorporateDomainGuard? domainGuard = null)
    {
        DomainGuard = domainGuard ?? new();
        listener.Start();
        _ = AcceptAsync();
    }

    public void Bind(NetworkSnapshot value) { lock (gate) physical = value; }
    public void SetProtectionEnabled(bool value)
    {
        lock (gate)
        {
            protectionEnabled = value;
            if (!value) { known = []; candidates.Clear(); }
            Rebuild();
        }
    }

    public void Candidate(Guid profile, long generation, IReadOnlyList<string> prefixes)
    {
        lock (gate) { if (!protectionEnabled) return; candidates[(profile, generation)] = prefixes.ToArray(); Rebuild(); }
    }

    public void ClearCandidate(Guid profile, long generation)
    {
        lock (gate) { candidates.Remove((profile, generation)); Rebuild(); }
    }

    public void Update(AppSettings settings, OpenVpnLink? link, OpenVpnOwnership ownership)
    {
        lock (gate)
        {
            DomainGuard.Prepare(settings);
            var ids = settings.Profiles.Where(p => p.IsOpenVpn).Select(p => p.Id).ToHashSet();
            foreach (var id in candidates.Keys.Where(id => !ids.Contains(id.Profile)).ToArray()) candidates.Remove(id);
            if (link?.ProfileId is { } active) candidates.Remove((active, link.Generation));
            known = protectionEnabled ? ownership.Known : [];
            Rebuild();
        }
    }

    private void Rebuild()
    {
        blocked = (protectionEnabled ? known.Concat(candidates.Values.SelectMany(x => x)) : []).Distinct().Select(prefix => {
            var parts = prefix.Split('/'); var ip = OpenVpnPushParser.Ipv4(parts[0]); int bits = int.Parse(parts[1]);
            return (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes()), bits);
        }).ToArray();
        foreach (var session in sessions.Values) if (session.Destination != null && Blocked(session.Destination)) { try { session.Stop.Cancel(); } catch (ObjectDisposedException) { } }
    }

    private bool Blocked(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        uint value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        return blocked.Any(p => p.Length == 0 || (value & (uint.MaxValue << (32 - p.Length))) == p.Network);
    }

    private async Task AcceptAsync()
    {
        try { while (!lifetime.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(lifetime.Token); var lease=admission.TryAccept(); if(lease==null){client.Dispose();continue;} _ = ServeAsync(client,lease); } }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static async Task<byte> Byte(Stream stream, CancellationToken ct) { var b = new byte[1]; await stream.ReadExactlyAsync(b, ct); return b[0]; }
    private static async Task<byte[]> Bytes(Stream stream, int count, CancellationToken ct) { var b = new byte[count]; await stream.ReadExactlyAsync(b, ct); return b; }
    private static async Task<(string Host, int Port)> Address(Stream stream, byte type, CancellationToken ct)
    {
        string host = type switch { 1 => new IPAddress(await Bytes(stream, 4, ct)).ToString(), 4 => new IPAddress(await Bytes(stream, 16, ct)).ToString(), 3 => Encoding.UTF8.GetString(await Bytes(stream, await Byte(stream, ct), ct)), _ => throw new IOException("SOCKS address") };
        var p = await Bytes(stream, 2, ct); return (host, p[0] * 256 + p[1]);
    }

    public Func<string, CancellationToken, Task<IPAddress[]>>? ResolveHostOverride { get; set; }

    private async Task<IPAddress> Resolve(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        if (DomainGuard.IsCorporate(host)) throw new IOException("Corporate destination cannot use automatic direct.");
        var addresses = ResolveHostOverride != null ? await ResolveHostOverride(host, ct) : await Dns.GetHostAddressesAsync(host, ct);
        lock (gate) { if (addresses.Any(Blocked)) throw new IOException("Corporate destination cannot use automatic direct."); }
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.First();
    }

    private void BindSocket(Socket socket)
    {
        var binding = physical ?? throw new IOException("Direct binding unavailable.");
        if (socket.AddressFamily == AddressFamily.InterNetwork)
        {
            socket.Bind(new IPEndPoint(IPAddress.Parse(binding.Address), 0));
            if (OperatingSystem.IsWindows()) socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(binding.Index));
        }
        else
        {
            if (!binding.HasIpv6DefaultRoute) throw new IOException("IPv6 physical binding unavailable.");
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
            if (OperatingSystem.IsWindows()) socket.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)31, binding.Index);
        }
    }

    private async Task ServeAsync(TcpClient client,LocalSocksAdmission.Lease lease)
    {
        using (lease)
        using (client)
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            long id = Interlocked.Increment(ref sequence); sessions[id] = (null, stop);
            try
            {
                var stream = client.GetStream(); using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); handshake.CancelAfter(TimeSpan.FromSeconds(5)); var ct = handshake.Token;
                if (await Byte(stream, ct) != 5) throw new IOException("SOCKS version");
                var methods = await Bytes(stream, await Byte(stream, ct), ct); if (!methods.Contains((byte)2)) throw new IOException("SOCKS authentication required");
                await stream.WriteAsync(new byte[] { 5, 2 }, ct);
                if (await Byte(stream, ct) != 1) throw new IOException("SOCKS auth version");
                var user = await Bytes(stream, await Byte(stream, ct), ct); var password = await Bytes(stream, await Byte(stream, ct), ct);
                if (!CryptographicOperations.FixedTimeEquals(user, Encoding.ASCII.GetBytes(Username)) || !CryptographicOperations.FixedTimeEquals(password, Encoding.ASCII.GetBytes(Password))) throw new IOException("SOCKS authentication failed");
                await stream.WriteAsync(new byte[] { 1, 0 }, ct); var header = await Bytes(stream, 4, ct);
                if (header[0] != 5 || header[2] != 0) throw new IOException("SOCKS request");
                var target = await Address(stream, header[3], ct);
                if(!lease.Promote(header[1]==3))throw new IOException("Direct gateway transport capacity reached");
                if (header[1] == 3) { await UdpAssociation(stream, target, stop.Token); return; }
                if (header[1] != 1) throw new IOException("SOCKS command");
                if (DomainGuard.IsCorporate(target.Host)) throw new IOException("Corporate destination cannot use automatic direct.");
                var ip = await Resolve(target.Host, ct); using var remote = new TcpClient(ip.AddressFamily);
                Task connecting;
                lock (gate)
                {
                    if (Blocked(ip)) throw new IOException("Corporate destination cannot use automatic direct.");
                    sessions[id] = (ip, stop); BindSocket(remote.Client); connecting = remote.ConnectAsync(ip, target.Port, stop.Token).AsTask();
                }
                await connecting.WaitAsync(TimeSpan.FromSeconds(10), stop.Token);
                await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, stop.Token);
                await TcpRelay.CopyAsync(client, remote, stop.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or TimeoutException or InvalidOperationException) { }
            finally { sessions.TryRemove(id, out _); }
        }
    }

    private async Task UdpAssociation(NetworkStream control, (string Host, int Port) requested, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct); using var local = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)local.Client.LocalEndPoint!).Port;
        await control.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, (byte)(port >> 8), (byte)port }, ct);
        var peers = new Dictionary<IPEndPoint, long>(); var sockets = new Dictionary<AddressFamily, UdpClient>(); var tasks = new List<Task>(); IPEndPoint? source = null;
        var reader = Task.Run(async () => { try { await control.ReadAsync(new byte[1], linked.Token); } finally { linked.Cancel(); } }, CancellationToken.None);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var packet = await local.ReceiveAsync(linked.Token);
                if (!IPAddress.IsLoopback(packet.RemoteEndPoint.Address) || requested.Port != 0 && packet.RemoteEndPoint.Port != requested.Port) continue;
                source ??= packet.RemoteEndPoint; if (!source.Equals(packet.RemoteEndPoint) || packet.Buffer.Length < 4 || packet.Buffer[0] != 0 || packet.Buffer[1] != 0 || packet.Buffer[2] != 0) continue;
                using var data = new MemoryStream(packet.Buffer, 4, packet.Buffer.Length - 4, false); var destination = await Address(data, packet.Buffer[3], linked.Token);
                if (DomainGuard.IsCorporate(destination.Host)) continue;
                var ip = await Resolve(destination.Host, linked.Token); var endpoint = new IPEndPoint(ip, destination.Port); var payload = new byte[data.Length - data.Position]; await data.ReadExactlyAsync(payload, linked.Token);
                ValueTask<int> send;
                lock (gate)
                {
                    long now = Environment.TickCount64; foreach (var old in peers.Where(p => now - p.Value > 30000).Select(p => p.Key).ToArray()) peers.Remove(old);
                    if (Blocked(ip) || !peers.ContainsKey(endpoint) && peers.Count >= 256) continue; peers[endpoint] = now;
                    if (!sockets.TryGetValue(ip.AddressFamily, out var remote))
                    {
                        remote = new UdpClient(ip.AddressFamily); BindSocket(remote.Client); sockets[ip.AddressFamily] = remote;
                        tasks.Add(ReturnUdp(remote, local, source, peers, linked.Token));
                    }
                    send = remote.SendAsync(payload, endpoint, linked.Token);
                }
                await send;
            }
        }
        finally
        {
            linked.Cancel(); foreach (var socket in sockets.Values) socket.Dispose();
            try { await Task.WhenAll(tasks.Append(reader)); } catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private async Task ReturnUdp(UdpClient remote, UdpClient local, IPEndPoint source, Dictionary<IPEndPoint, long> peers, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var packet = await remote.ReceiveAsync(ct); var ip = packet.RemoteEndPoint.Address;
            lock (gate) if (Blocked(ip) || !peers.TryGetValue(packet.RemoteEndPoint, out var last) || Environment.TickCount64 - last > 30000) continue;
            var port = packet.RemoteEndPoint.Port; var response = new byte[] { 0, 0, 0, (byte)(ip.AddressFamily == AddressFamily.InterNetwork ? 1 : 4) }.Concat(ip.GetAddressBytes()).Concat(new[] { (byte)(port >> 8), (byte)port }).Concat(packet.Buffer).ToArray();
            await local.SendAsync(response, source, ct);
        }
    }

    private bool disposed;
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        try { listener.Stop(); } catch { }
        foreach (var s in sessions.Values)
        {
            try { s.Stop.Cancel(); } catch (ObjectDisposedException) { }
        }
        sessions.Clear();
        try { lifetime.Dispose(); } catch (ObjectDisposedException) { }
    }
}
