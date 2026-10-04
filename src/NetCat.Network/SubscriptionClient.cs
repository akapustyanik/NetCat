using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NetCat.Network;

// This policy is specific to remote subscriptions, not VPN destinations.
public sealed class SubscriptionClient
{
    public Func<string, CancellationToken, Task<IPAddress[]>> Resolve { get; init; } = Dns.GetHostAddressesAsync;
    public Func<Uri, IPAddress[], HttpMessageHandler> CreateHandler { get; init; } = PinnedHandler;

    public async Task<string> ReadAsync(string url, bool allowInsecureTransport, CancellationToken ct)
    {
        try { return await ReadCoreAsync(url, allowInsecureTransport, ct).ConfigureAwait(false); }
        catch (HttpRequestException) { throw new HttpRequestException("Сервер подписки недоступен или проверка HTTPS не пройдена."); }
        catch (SocketException) { throw new IOException("Не удалось разрешить адрес сервера подписки."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("Истекло время ожидания подписки."); }
    }
    private async Task<string> ReadCoreAsync(string url, bool allowInsecureTransport, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25)); ct = deadline.Token;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var next)) throw new InvalidDataException("Некорректный адрес подписки.");
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            ValidateUri(next, allowInsecureTransport);
            var addresses = IPAddress.TryParse(next.DnsSafeHost, out var literal) ? [literal] : await Resolve(next.DnsSafeHost, ct).ConfigureAwait(false);
            if (addresses.Length == 0 || !allowInsecureTransport && addresses.Any(a => !IsPublicAddress(a)))
                throw new InvalidDataException("Удалённая подписка указывает на локальную или закрытую сеть. Для доверенной локальной подписки нужен явный небезопасный режим.");
            using var client = new HttpClient(CreateHandler(next, addresses)) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NetCat/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd(NetCat.Engine.SubscriptionDocument.MediaType);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/plain;q=0.9");
            using var response = await client.GetAsync(next, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (response.Headers.Location == null || redirects == 5) throw new InvalidDataException("Слишком много или некорректное перенаправление подписки.");
                next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(next, response.Headers.Location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            const int limit = 8 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Подписка больше 8 МБ.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var body = new MemoryStream(); var chunk = new byte[16384]; int count;
            while ((count = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (body.Length + count > limit) throw new InvalidDataException("Подписка больше 8 МБ.");
                body.Write(chunk, 0, count);
            }
            return Encoding.UTF8.GetString(body.ToArray());
        }
        throw new InvalidDataException("Слишком много перенаправлений подписки.");
    }
    public static void ValidateUri(Uri uri, bool allowInsecureTransport)
    {
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length != 0 || uri.Scheme is not ("https" or "http"))
            throw new InvalidDataException("Подписка должна иметь адрес HTTPS без user-info.");
        if (uri.Scheme != "https" && !allowInsecureTransport)
            throw new InvalidDataException("HTTP-подписки запрещены: используйте HTTPS или явно разрешите небезопасный транспорт для этой подписки.");
    }
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        // Conservative subscription policy, IANA special-purpose registries
        // reviewed 2026-10-04. Transition/protocol-assignment ranges are not
        // generic subscription destinations; explicit local opt-in is separate.
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (b[0] & 0xe0) == 0x20 &&
                !(b[0] == 0x20 && b[1] == 0x01 && (b[2] & 0xfe) == 0) && // 2001::/23
                !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) &&
                !(b[0] == 0x20 && b[1] == 0x02) && // 6to4
                !(b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0); // 3fff::/20
        return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
            !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
            !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] is >= 64 and <= 127) &&
            !(b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) &&
            !(b[0] == 192 && b[1] == 88 && b[2] == 99) &&
            !(b[0] == 198 && b[1] is 18 or 19) &&
            !(b[0] == 198 && b[1] == 51 && b[2] == 100) &&
            !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }
    private static HttpMessageHandler PinnedHandler(Uri uri, IPAddress[] addresses) => new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false,
        ConnectCallback = async (context, ct) =>
        {
            if (!context.DnsEndPoint.Host.Equals(uri.DnsSafeHost, StringComparison.OrdinalIgnoreCase) || context.DnsEndPoint.Port != uri.Port)
                throw new IOException("Изменился адрес соединения подписки.");
            // Avoid a second DNS lookup after validation. TLS validates the URI
            // hostname with the platform trust store, never a custom bypass.
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(address, uri.Port), ct).ConfigureAwait(false); return new NetworkStream(socket, true); }
                catch (SocketException) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new IOException("Не удалось подключиться к серверу подписки.");
        }
    };
}
