using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetCat.Core;

namespace NetCat.Network;

/// <summary>Runs in a separate process identity so the router's NetCat transport bypass cannot apply.</summary>
public static class SystemTrafficProbe
{
    [DllImport("iphlpapi.dll")] private static extern uint GetBestInterface(uint destination, out uint index);
    private sealed record Binding(IPAddress Address, int Index, string Id);
    private static Binding Capture()
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
            n.Name == "NetCat-TUN" && n.OperationalStatus == OperationalStatus.Up);
        if(adapter == null) throw new IOException("NetCat-TUN unavailable");
        var properties = adapter.GetIPProperties();
        var address = properties.UnicastAddresses.FirstOrDefault(a => a.Address.Equals(IPAddress.Parse("172.29.255.1")))?.Address;
        if(address == null) throw new IOException("NetCat-TUN address unavailable");
        return new(address, properties.GetIPv4Properties().Index, adapter.Id);
    }
    private static void Validate(Binding expected, IPAddress destination)
    {
        if(Capture() != expected || GetBestInterface(BitConverter.ToUInt32(destination.GetAddressBytes()), out var index) != 0 || index != expected.Index)
            throw new IOException("Test route is outside the expected NetCat-TUN");
    }
    private static void Bind(Socket socket, Binding binding, IPAddress destination)
    {
        Validate(binding, destination);
        // IP_UNICAST_IF uses the interface index in network byte order on Windows.
        // The real socket owns its OS-assigned source port; there is no free-port probe.
        socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(binding.Index));
        socket.Bind(new IPEndPoint(binding.Address, 0));
    }
    public static async Task<TrafficTestResult> MeasureAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(!string.Equals(Path.GetFileName(Environment.ProcessPath), SystemTrafficProbeWorker.ExecutableName, StringComparison.OrdinalIgnoreCase))
            return TrafficTestResult.Failed("Проверка TUN требует отдельного процесса без обхода маршрутизации");
        Binding binding;
        try { binding = Capture(); }
        catch(Exception ex) { return TrafficTestResult.Failed(ProfileTrafficProbe.Describe(ex)); }
        var targets = TrafficProbeTargets.Default;
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(8),
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, token);
                var address = addresses.FirstOrDefault() ?? throw new IOException("No IPv4 test address");
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
                try
                {
                    Bind(socket, binding, address);
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var download = await ProfileTrafficProbe.DownloadAsync(client, targets.Download, ct);
        var upload = await ProfileTrafficProbe.UploadAsync(client, targets.Upload, ct);
        var sent = 0; var replies = new HashSet<string>(StringComparer.Ordinal);
        var udpError = "";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(targets.StunHost, AddressFamily.InterNetwork, timeout.Token);
            var address = addresses.FirstOrDefault() ?? throw new IOException("No IPv4 STUN address");
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            Bind(udp.Client, binding, address);
            var remote = new IPEndPoint(address, targets.StunPort);
            var transactions = new HashSet<string>(StringComparer.Ordinal);
            for(var i = 0; i < ProfileTrafficProbe.UdpRequests; i++)
            {
                var packet = ProfileTrafficProbe.StunPacket([], out var id); transactions.Add(id);
                await udp.SendAsync(packet, remote, timeout.Token); sent++;
            }
            while(replies.Count < sent)
            {
                var response = await udp.ReceiveAsync(timeout.Token);
                if(!response.RemoteEndPoint.Equals(remote) || response.Buffer.Length < 20) continue;
                var id = ProfileTrafficProbe.StunResponseId(response.Buffer, 0);
                if(id != null && transactions.Contains(id)) replies.Add(id);
            }
            Validate(binding, address);
        }
        catch(Exception ex) when(!ct.IsCancellationRequested) { udpError = ProfileTrafficProbe.Describe(ex); }
        var web = await ProfileTrafficProbe.CheckWebAsync(client, targets.Web, ct, targets.WebExpectedStatusCode);
        if(CaptureSafely() != binding) return TrafficTestResult.Failed("TUN изменился во время проверки");
        return new(download, upload, sent, replies.Count, udpError, web);
    }
    private static Binding? CaptureSafely() { try { return Capture(); } catch { return null; } }
}
