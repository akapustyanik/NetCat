using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetCat.Core;

namespace NetCat.Network;

public sealed record TrafficProbeTargets(Uri Download, Uri Upload, string StunHost, int StunPort, IReadOnlyList<Uri>? Web = null, int? WebExpectedStatusCode = null)
{
    public static TrafficProbeTargets Default { get; } = new(
        new("https://speed.cloudflare.com/__down?bytes=131072"), new("https://httpbin.org/post"), "stun.l.google.com", 19302,
        [new("https://www.gstatic.com/generate_204"), new("https://cp.cloudflare.com/generate_204")], 204);
}

/// <summary>Bounded payload tests. Every connection uses the supplied SOCKS; there is no direct fallback.</summary>
public static class ProfileTrafficProbe
{
    public const int DownloadBytes = 128 * 1024;
    public const int UploadBytes = 16 * 1024;
    public const int UdpRequests = 8;

    public static async Task<TrafficTestResult> MeasureAsync(int socksPort, CancellationToken ct, TrafficProbeTargets? targets = null)
    {
        targets ??= TrafficProbeTargets.Default;
        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"), UseProxy = true,
            AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(8), UseCookies = false
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var download = await DownloadAsync(client, targets.Download, ct);
        var upload = await UploadAsync(client, targets.Upload, ct);
        var udp = await UdpAsync(socksPort, targets, ct);
        var web = await CheckWebAsync(client, targets.Web, ct, targets.WebExpectedStatusCode);
        return new(download, upload, udp.Sent, udp.Received, udp.Error, web);
    }

    internal static async Task<IReadOnlyList<TrafficWebCheck>> CheckWebAsync(HttpClient client, IReadOnlyList<Uri>? targets, CancellationToken ct, int? expectedStatusCode = null)
    {
        var results = new List<TrafficWebCheck>();
        foreach(var target in targets ?? [])
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var status = (int)response.StatusCode;
                var success = response.IsSuccessStatusCode && (expectedStatusCode == null || status == expectedStatusCode);
                var error = success ? "" : $"Тестовый сайт: HTTP {status}" +
                    (response.IsSuccessStatusCode && expectedStatusCode.HasValue ? $", ожидался {expectedStatusCode}" : "");
                results.Add(new(target.Host, success, status, error));
            }
            catch(Exception ex) when(!ct.IsCancellationRequested) { results.Add(new(target.Host, false, 0, Describe(ex))); }
        }
        return results;
    }

    internal static async Task<TrafficTransfer> DownloadAsync(HttpClient client, Uri url, CancellationToken ct)
    {
        long received = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if(!response.IsSuccessStatusCode) return new(false, 0, 0, $"Тестовый сервис: HTTP {(int)response.StatusCode}");
            if(response.Content.Headers.ContentType?.MediaType == "text/html") return new(false, 0, 0, "Сервис вернул HTML вместо данных");
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[8192];
            while(true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, DownloadBytes + 1 - received)), timeout.Token);
                if(count == 0) break;
                received += count;
                if(received > DownloadBytes) return new(false, 0, received, "Размер ответа превышает лимит проверки");
            }
            return new(received == DownloadBytes, 0, received, received == DownloadBytes ? "" : "Получен неполный ответ");
        }
        catch(Exception ex) when(!ct.IsCancellationRequested) { return new(false, 0, received, Describe(ex)); }
    }

    internal static async Task<TrafficTransfer> UploadAsync(HttpClient client, Uri url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var payload = RandomNumberGenerator.GetBytes(UploadBytes);
        for(var i = 0; i < payload.Length; i++) payload[i] = (byte)('a' + payload[i] % 26);
        try
        {
            using var body = new ByteArrayContent(payload);
            body.Headers.ContentType = new("application/octet-stream");
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = body };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if(!response.IsSuccessStatusCode) return new(false, 0, 0, $"Тестовый сервис: HTTP {(int)response.StatusCode}");
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var data = new MemoryStream();
            var buffer = new byte[8192];
            while(true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, DownloadBytes + 1 - data.Length)), timeout.Token);
                if(count == 0) break;
                data.Write(buffer, 0, count);
                if(data.Length > DownloadBytes) return new(false, 0, data.Length, "Эхо-ответ превышает лимит проверки");
            }
            using var json = JsonDocument.Parse(data.ToArray());
            var echoed = json.RootElement.TryGetProperty("data", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var matches = echoed == Encoding.ASCII.GetString(payload) || echoed == "data:application/octet-stream;base64," + Convert.ToBase64String(payload);
            return new(matches, matches ? payload.Length : 0, data.Length, matches ? "" : "Сервис не подтвердил получение отправленных данных");
        }
        catch(Exception ex) when(!ct.IsCancellationRequested) { return new(false, 0, 0, Describe(ex)); }
    }

    private static async Task<(int Sent, int Received, string Error)> UdpAsync(int socksPort, TrafficProbeTargets targets, CancellationToken ct)
    {
        var sent = 0;
        var replies = new HashSet<string>(StringComparer.Ordinal);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            using var control = new TcpClient(AddressFamily.InterNetwork);
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            await control.ConnectAsync(IPAddress.Loopback, socksPort, timeout.Token);
            using var stream = control.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
            var auth = new byte[2]; await stream.ReadExactlyAsync(auth, timeout.Token);
            if(auth[0] != 5 || auth[1] != 0) throw new IOException("SOCKS UDP authentication rejected");
            var local = (IPEndPoint)udp.Client.LocalEndPoint!;
            var request = new byte[] { 5, 3, 0, 1, 127, 0, 0, 1, (byte)(local.Port >> 8), (byte)local.Port };
            await stream.WriteAsync(request, timeout.Token);
            var head = new byte[4]; await stream.ReadExactlyAsync(head, timeout.Token);
            if(head[0] != 5 || head[1] != 0 || head[2] != 0) throw new NotSupportedException("SOCKS UDP ASSOCIATE rejected");
            var relay = await ReadRelayAsync(stream, head[3], timeout.Token);
            if(!IPAddress.IsLoopback(relay.Address)) throw new IOException("SOCKS UDP relay is not local");
            if(relay.Port == 0) throw new IOException("SOCKS UDP relay port is zero");
            var hostname = Encoding.ASCII.GetBytes(targets.StunHost);
            if(hostname.Length is 0 or > 255) throw new ArgumentException("Invalid STUN hostname");
            var prefix = new byte[7 + hostname.Length]; prefix[3] = 3; prefix[4] = (byte)hostname.Length;
            hostname.CopyTo(prefix, 5);
            BinaryPrimitives.WriteUInt16BigEndian(prefix.AsSpan(prefix.Length - 2), checked((ushort)targets.StunPort));
            var transactions = new HashSet<string>(StringComparer.Ordinal);
            for(var i = 0; i < UdpRequests; i++)
            {
                var packet = StunPacket(prefix, out var id); transactions.Add(id);
                await udp.SendAsync(packet, relay, timeout.Token); sent++;
            }
            while(replies.Count < sent)
            {
                var response = await udp.ReceiveAsync(timeout.Token);
                if(!response.RemoteEndPoint.Equals(relay)) continue;
                var offset = UdpPayloadOffset(response.Buffer);
                if(offset < 0 || response.Buffer.Length < offset + 20) continue;
                var id = StunResponseId(response.Buffer, offset);
                if(id == null) continue;
                if(transactions.Contains(id)) replies.Add(id);
            }
            return (sent, replies.Count, "");
        }
        catch(Exception ex) when(!ct.IsCancellationRequested) { return (sent, replies.Count, Describe(ex)); }
    }

    internal static byte[] StunPacket(byte[] prefix, out string id)
    {
        var packet = new byte[prefix.Length + 20]; prefix.CopyTo(packet, 0);
        var stun = packet.AsSpan(prefix.Length); stun[1] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(stun[4..], 0x2112A442);
        RandomNumberGenerator.Fill(stun[8..20]); id = Convert.ToHexString(stun[8..20]);
        return packet;
    }

    internal static string? StunResponseId(byte[] packet, int offset)
    {
        var stun = packet.AsSpan(offset);
        if(BinaryPrimitives.ReadUInt16BigEndian(stun) != 0x0101 ||
           BinaryPrimitives.ReadUInt32BigEndian(stun[4..]) != 0x2112A442 ||
           BinaryPrimitives.ReadUInt16BigEndian(stun[2..]) + 20 != stun.Length) return null;
        return Convert.ToHexString(stun[8..20]);
    }

    private static async Task<IPEndPoint> ReadRelayAsync(Stream stream, byte type, CancellationToken ct)
    {
        IPAddress address;
        if(type is 1 or 4)
        {
            var bytes = new byte[type == 1 ? 4 : 16]; await stream.ReadExactlyAsync(bytes, ct);
            address = new IPAddress(bytes);
        }
        else if(type == 3)
        {
            var count = new byte[1]; await stream.ReadExactlyAsync(count, ct);
            var name = new byte[count[0]]; await stream.ReadExactlyAsync(name, ct);
            // Our core is local. Do not resolve a relay through the system DNS path.
            if(Encoding.ASCII.GetString(name) != "localhost") throw new IOException("Invalid local relay hostname");
            address = IPAddress.Loopback;
        }
        else throw new IOException("Invalid SOCKS relay address type");
        if(address.Equals(IPAddress.Any)) address = IPAddress.Loopback;
        var port = new byte[2]; await stream.ReadExactlyAsync(port, ct);
        return new(address, BinaryPrimitives.ReadUInt16BigEndian(port));
    }

    private static int UdpPayloadOffset(byte[] frame)
    {
        if(frame.Length < 4 || frame[0] != 0 || frame[1] != 0 || frame[2] != 0) return -1;
        return frame[3] switch { 1 => 10, 4 => 22, 3 when frame.Length >= 5 => 7 + frame[4], _ => -1 };
    }

    internal static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "Истекло время проверки",
        NotSupportedException => "Профиль не предоставил SOCKS UDP",
        HttpRequestException http => $"Ошибка TCP/TLS ({http.HttpRequestError})",
        SocketException socket => $"Ошибка сокета ({socket.SocketErrorCode})",
        JsonException => "Тестовый сервис вернул некорректное эхо",
        _ => $"Проверка недоступна ({ex.GetType().Name})"
    };
}
