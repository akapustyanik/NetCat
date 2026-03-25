using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ServiceLib.Common;
using ServiceLib.Models;

namespace ServiceLib.Handler;

public static class TelegramWsProxyHandler
{
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 1080;
    private const string EmbeddedDisplayPath = "embedded://telegram-ws-proxy";
    private static readonly byte[] Zero64 = new byte[64];
    private static readonly HashSet<uint> ValidProtocols = [0xEFEFEFEF, 0xEEEEEEEE, 0xDDDDDDDD];

    private static readonly object SyncRoot = new();
    private static readonly (uint Start, uint End)[] TelegramRanges =
    [
        (ToUInt32("185.76.151.0"), ToUInt32("185.76.151.255")),
        (ToUInt32("149.154.160.0"), ToUInt32("149.154.175.255")),
        (ToUInt32("91.105.192.0"), ToUInt32("91.105.193.255")),
        (ToUInt32("91.108.0.0"), ToUInt32("91.108.255.255"))
    ];

    private static readonly IReadOnlyDictionary<string, (int Dc, bool IsMedia)> IpToDc =
        new Dictionary<string, (int Dc, bool IsMedia)>(StringComparer.OrdinalIgnoreCase)
        {
            ["149.154.175.50"] = (1, false),
            ["149.154.175.51"] = (1, false),
            ["149.154.175.53"] = (1, false),
            ["149.154.175.54"] = (1, false),
            ["149.154.175.52"] = (1, true),
            ["149.154.167.41"] = (2, false),
            ["149.154.167.50"] = (2, false),
            ["149.154.167.51"] = (2, false),
            ["149.154.167.220"] = (2, false),
            ["95.161.76.100"] = (2, false),
            ["149.154.167.151"] = (2, true),
            ["149.154.167.222"] = (2, true),
            ["149.154.167.223"] = (2, true),
            ["149.154.162.123"] = (2, true),
            ["149.154.175.100"] = (3, false),
            ["149.154.175.101"] = (3, false),
            ["149.154.175.102"] = (3, true),
            ["149.154.167.91"] = (4, false),
            ["149.154.167.92"] = (4, false),
            ["149.154.164.250"] = (4, true),
            ["149.154.166.120"] = (4, true),
            ["149.154.166.121"] = (4, true),
            ["149.154.167.118"] = (4, true),
            ["149.154.165.111"] = (4, true),
            ["91.108.56.100"] = (5, false),
            ["91.108.56.101"] = (5, false),
            ["91.108.56.116"] = (5, false),
            ["91.108.56.126"] = (5, false),
            ["149.154.171.5"] = (5, false),
            ["91.108.56.102"] = (5, true),
            ["91.108.56.128"] = (5, true),
            ["91.108.56.151"] = (5, true),
            ["91.105.192.100"] = (203, false)
        };

    private static readonly IReadOnlyDictionary<int, int> DcOverrides =
        new Dictionary<int, int>
        {
            [203] = 2
        };
    private static readonly string[] TelegramDomainSuffixes =
    [
        "telegram.org",
        "telegram.me",
        "telegram.dog",
        "telegram.space",
        "telegramdownload.com",
        "t.me",
        "telegra.ph",
        "cdn-telegram.org",
        "tg.dev"
    ];
    private static readonly string[] DefaultDcIpEntries =
    [
        "2:149.154.167.220",
        "4:149.154.167.220"
    ];
    private static readonly HttpClient DnsHttpClient = CreateDnsHttpClient();

    private static CancellationTokenSource? _serverCts;
    private static TcpListener? _listener;
    private static Task? _serverTask;
    private static string _lastError = string.Empty;
    private static long _activeConnections;

    public static string GetBundleDirectory()
    {
        return EmbeddedDisplayPath;
    }

    public static string GetExecutablePath()
    {
        return EmbeddedDisplayPath;
    }

    public static string GetAppDataDirectory()
    {
        return Path.Combine(Utils.GetUserDataPath(), "telegram-ws-proxy");
    }

    public static string GetConfigPath()
    {
        return Path.Combine(GetAppDataDirectory(), "config.json");
    }

    public static bool Exists(out string executablePath)
    {
        executablePath = EmbeddedDisplayPath;
        return true;
    }

    public static bool IsRunning()
    {
        lock (SyncRoot)
        {
            return _listener != null && _serverTask != null && !_serverTask.IsCompleted;
        }
    }

    public static string GetTrafficModeSummary(string? trafficMode)
    {
        return IsLocalSocksMode(trafficMode)
            ? $"Telegram идёт напрямую через встроенный локальный SOCKS5 {GetConfiguredAddress()}."
            : "Telegram идёт через VPN-маршрутизацию NetCat.";
    }

    public static string GetRuntimeSummary(string? trafficMode)
    {
        var address = GetConfiguredAddress();
        var state = IsRunning() ? "running" : "stopped";
        var mode = IsLocalSocksMode(trafficMode) ? "embedded" : "disabled";
        var suffix = _lastError.IsNullOrEmpty() ? string.Empty : $" | last_error={_lastError}";
        return $"TG WS Proxy: {mode}/{state} | {address.Host}:{address.Port} | connections={Interlocked.Read(ref _activeConnections)}{suffix}";
    }

    public static bool TryStart(out string error)
    {
        error = string.Empty;

        lock (SyncRoot)
        {
            if (_listener != null && _serverTask != null && !_serverTask.IsCompleted)
            {
                return true;
            }

            StopInternal(waitForCompletion: false);

            try
            {
                EnsureConfigFile();
                var address = GetConfiguredAddress();
                _serverCts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Parse(address.Host), address.Port);
                _listener.Server.NoDelay = true;
                _listener.Start();
                _lastError = string.Empty;
                _serverTask = Task.Run(() => AcceptLoopAsync(_listener, _serverCts.Token));
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                error = ex.Message;
                Logging.SaveLog("TelegramWsProxyHandler", ex);
                StopInternal(waitForCompletion: false);
                return false;
            }
        }
    }

    public static void Stop()
    {
        lock (SyncRoot)
        {
            StopInternal(waitForCompletion: true);
        }
    }

    public static void OpenInTelegram()
    {
        var address = GetConfiguredAddress();
        ProcUtils.ProcessStart($"tg://socks?server={address.Host}&port={address.Port}");
    }

    public static bool IsLocalSocksMode(string? trafficMode)
    {
        return string.Equals(trafficMode, QuickRuleConfig.TelegramTrafficModeLocalSocks, StringComparison.OrdinalIgnoreCase);
    }

    public static (string Host, int Port) GetConfiguredAddress()
    {
        var config = LoadConfig();
        var host = IPAddress.TryParse(config.Host, out var parsedHost) && parsedHost.AddressFamily == AddressFamily.InterNetwork
            ? parsedHost.ToString()
            : DefaultHost;
        var port = config.Port is > 0 and <= 65535 ? config.Port : DefaultPort;
        return (host, port);
    }

    private static void StopInternal(bool waitForCompletion)
    {
        var cts = _serverCts;
        var listener = _listener;
        var serverTask = _serverTask;

        _serverCts = null;
        _listener = null;
        _serverTask = null;

        try
        {
            cts?.Cancel();
        }
        catch
        {
            // ignored
        }

        try
        {
            listener?.Stop();
        }
        catch
        {
            // ignored
        }

        if (waitForCompletion && serverTask != null)
        {
            try
            {
                serverTask.Wait(3000);
            }
            catch
            {
                // ignored
            }
        }

        cts?.Dispose();
    }

    private static async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                    client.NoDelay = true;
                    _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    client?.Dispose();
                    _lastError = ex.Message;
                    Logging.SaveLog("TelegramWsProxyHandler", ex);
                    await Task.Delay(300, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
    }

    private static async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeConnections);
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var greeting = await ReadExactlyAsync(stream, 2, cancellationToken);
                if (greeting[0] != 5)
                {
                    return;
                }

                var methodsCount = greeting[1];
                if (methodsCount > 0)
                {
                    _ = await ReadExactlyAsync(stream, methodsCount, cancellationToken);
                }

                await stream.WriteAsync(new byte[] { 0x05, 0x00 }, cancellationToken);

                var request = await ReadExactlyAsync(stream, 4, cancellationToken);
                if (request[0] != 5 || request[1] != 1)
                {
                    await WriteReplyAsync(stream, 0x07, cancellationToken);
                    return;
                }

                var destinationHost = await ReadDestinationHostAsync(stream, request[3], cancellationToken);
                var portBytes = await ReadExactlyAsync(stream, 2, cancellationToken);
                var destinationPort = (portBytes[0] << 8) | portBytes[1];

                if (destinationHost.Contains(':'))
                {
                    await WriteReplyAsync(stream, 0x05, cancellationToken);
                    return;
                }

                var resolvedAddress = await ResolveDestinationAsync(destinationHost, cancellationToken);
                if (resolvedAddress == null)
                {
                    await WriteReplyAsync(stream, 0x05, cancellationToken);
                    return;
                }

                if (!IsTelegramIp(resolvedAddress))
                {
                    await HandlePassthroughAsync(stream, resolvedAddress, destinationPort, cancellationToken);
                    return;
                }

                await WriteReplyAsync(stream, 0x00, cancellationToken);

                var initPacket = await ReadExactlyAsync(stream, 64, cancellationToken);
                if (IsHttpTransport(initPacket) || IsTlsClientHello(initPacket))
                {
                    await HandleTcpFallbackAsync(stream, resolvedAddress, destinationPort, initPacket, cancellationToken);
                    return;
                }

                var extractedFromInit = TryExtractDcFromInit(initPacket, out _, out _);
                var dcInfo = TryResolveDcInfo(resolvedAddress, initPacket);
                if (dcInfo == null)
                {
                    await HandleTcpFallbackAsync(stream, resolvedAddress, destinationPort, initPacket, cancellationToken);
                    return;
                }

                var patchedInitPacket = initPacket;
                AbridgedMessageSplitter? splitter = null;
                if (!extractedFromInit && IpToDc.TryGetValue(resolvedAddress, out var mappedDc))
                {
                    patchedInitPacket = PatchInitPacketDc(initPacket, mappedDc.Dc, mappedDc.IsMedia);
                    splitter = AbridgedMessageSplitter.TryCreate(patchedInitPacket);
                }

                if (!await TryHandleTelegramWebSocketAsync(stream, resolvedAddress, destinationPort, patchedInitPacket, dcInfo.Value.Dc, dcInfo.Value.IsMedia, splitter, cancellationToken))
                {
                    await HandleTcpFallbackAsync(stream, resolvedAddress, destinationPort, patchedInitPacket, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (EndOfStreamException)
        {
            // ignored
        }
        catch (IOException)
        {
            // ignored
        }
        catch (SocketException)
        {
            // ignored
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Logging.SaveLog("TelegramWsProxyHandler", ex);
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    private static async Task HandlePassthroughAsync(NetworkStream clientStream, string destinationIp, int destinationPort, CancellationToken cancellationToken)
    {
        using var remoteClient = new TcpClient();
        remoteClient.NoDelay = true;
        await remoteClient.ConnectAsync(destinationIp, destinationPort, cancellationToken);
        using var remoteStream = remoteClient.GetStream();
        await WriteReplyAsync(clientStream, 0x00, cancellationToken);
        await BridgeTcpAsync(clientStream, remoteStream, cancellationToken);
    }

    private static async Task HandleTcpFallbackAsync(NetworkStream clientStream, string destinationIp, int destinationPort, byte[] initPacket, CancellationToken cancellationToken)
    {
        using var remoteClient = new TcpClient();
        remoteClient.NoDelay = true;
        await remoteClient.ConnectAsync(destinationIp, destinationPort, cancellationToken);
        using var remoteStream = remoteClient.GetStream();
        await remoteStream.WriteAsync(initPacket, cancellationToken);
        await remoteStream.FlushAsync(cancellationToken);
        await BridgeTcpAsync(clientStream, remoteStream, cancellationToken);
    }

    private static async Task<bool> TryHandleTelegramWebSocketAsync(NetworkStream clientStream, string destinationIp, int destinationPort, byte[] initPacket, int dc, bool isMedia, AbridgedMessageSplitter? splitter, CancellationToken cancellationToken)
    {
        var targetIp = ResolveTargetDcIp(dc, destinationIp);
        foreach (var domain in GetWsDomains(dc, isMedia))
        {
            try
            {
                await using var webSocket = await RawWebSocket.ConnectAsync(targetIp, domain, cancellationToken);
                await webSocket.SendBinaryAsync(initPacket, cancellationToken);
                await BridgeWebSocketAsync(clientStream, webSocket, splitter, cancellationToken);
                return true;
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or TaskCanceledException)
            {
                _lastError = $"{domain} via {targetIp}: {ex.Message}";
            }
        }

        _lastError = $"WS fallback to TCP for {destinationIp}:{destinationPort}";
        return false;
    }

    private static async Task BridgeTcpAsync(Stream left, Stream right, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linkedCts.Token;

        var leftToRight = CopyLoopAsync(left, right, ct);
        var rightToLeft = CopyLoopAsync(right, left, ct);

        await Task.WhenAny(leftToRight, rightToLeft);
        linkedCts.Cancel();

        await AwaitQuietly(leftToRight);
        await AwaitQuietly(rightToLeft);
    }

    private static async Task BridgeWebSocketAsync(NetworkStream clientStream, RawWebSocket webSocket, AbridgedMessageSplitter? splitter, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linkedCts.Token;

        var tcpToWs = PumpTcpToWebSocketAsync(clientStream, webSocket, splitter, ct);
        var wsToTcp = PumpWebSocketToTcpAsync(webSocket, clientStream, ct);

        await Task.WhenAny(tcpToWs, wsToTcp);
        linkedCts.Cancel();

        await AwaitQuietly(tcpToWs);
        await AwaitQuietly(wsToTcp);
    }

    private static async Task PumpTcpToWebSocketAsync(NetworkStream tcpStream, RawWebSocket webSocket, AbridgedMessageSplitter? splitter, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await tcpStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                if (splitter != null)
                {
                    foreach (var chunk in splitter.Split(buffer.AsSpan(0, read).ToArray()))
                    {
                        await webSocket.SendBinaryAsync(chunk, cancellationToken);
                    }
                }
                else
                {
                    await webSocket.SendBinaryAsync(buffer.AsMemory(0, read).ToArray(), cancellationToken);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task PumpWebSocketToTcpAsync(RawWebSocket webSocket, NetworkStream tcpStream, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await webSocket.ReceiveAsync(cancellationToken);
            if (message == null)
            {
                return;
            }

            if (message.Length > 0)
            {
                await tcpStream.WriteAsync(message, cancellationToken);
                await tcpStream.FlushAsync(cancellationToken);
            }
        }
    }

    private static async Task CopyLoopAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken);
            if (read <= 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }

        return buffer;
    }

    private static async Task<string> ReadDestinationHostAsync(Stream stream, byte atyp, CancellationToken cancellationToken)
    {
        return atyp switch
        {
            0x01 => new IPAddress(await ReadExactlyAsync(stream, 4, cancellationToken)).ToString(),
            0x03 => await ReadDomainAsync(stream, cancellationToken),
            0x04 => new IPAddress(await ReadExactlyAsync(stream, 16, cancellationToken)).ToString(),
            _ => throw new IOException($"Unsupported SOCKS5 address type: {atyp}")
        };
    }

    private static async Task<string> ReadDomainAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = (await ReadExactlyAsync(stream, 1, cancellationToken))[0];
        var raw = await ReadExactlyAsync(stream, length, cancellationToken);
        return System.Text.Encoding.ASCII.GetString(raw);
    }

    private static async Task WriteReplyAsync(Stream stream, byte status, CancellationToken cancellationToken)
    {
        byte[] reply = [0x05, status, 0x00, 0x01, 0, 0, 0, 0, 0, 0];
        await stream.WriteAsync(reply, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<string?> ResolveDestinationAsync(string destinationHost, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(destinationHost, out var address))
        {
            return address.AddressFamily == AddressFamily.InterNetwork ? address.ToString() : null;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(destinationHost, cancellationToken);
            var resolved = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)?.ToString();
            if (!resolved.IsNullOrEmpty() && !IsPoisonedAddress(resolved))
            {
                return resolved;
            }
        }
        catch
        {
            // try DoH fallback for Telegram domains below
        }

        if (!IsTelegramDomain(destinationHost))
        {
            return null;
        }

        return await ResolveTelegramDomainViaDohAsync(destinationHost, cancellationToken);
    }

    private static bool IsTelegramIp(string address)
    {
        if (!IPAddress.TryParse(address, out var ipAddress) || ipAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var value = ToUInt32(ipAddress.ToString());
        return TelegramRanges.Any(range => value >= range.Start && value <= range.End);
    }

    private static bool IsHttpTransport(byte[] data)
    {
        return data.Length >= 4 &&
               (data.Take(5).SequenceEqual("POST "u8.ToArray())
                || data.Take(4).SequenceEqual("GET "u8.ToArray())
                || data.Take(5).SequenceEqual("HEAD "u8.ToArray())
                || data.Take(8).SequenceEqual("OPTIONS "u8.ToArray()));
    }

    private static bool IsTlsClientHello(byte[] data)
    {
        return data.Length >= 6 &&
               data[0] == 0x16 &&
               data[1] == 0x03 &&
               data[2] is >= 0x01 and <= 0x04 &&
               data[5] == 0x01;
    }

    private static bool IsPoisonedAddress(string address)
    {
        if (!IPAddress.TryParse(address, out var ipAddress) || ipAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var value = ToUInt32(ipAddress.ToString());
        return value >= ToUInt32("198.18.0.0") && value <= ToUInt32("198.19.255.255");
    }

    private static bool IsTelegramDomain(string host)
    {
        return TelegramDomainSuffixes.Any(suffix =>
            host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string?> ResolveTelegramDomainViaDohAsync(string host, CancellationToken cancellationToken)
    {
        foreach (var endpoint in new[]
                 {
                     $"https://dns.google/resolve?name={Uri.EscapeDataString(host)}&type=A",
                     $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(host)}&type=A"
                 })
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.TryAddWithoutValidation("accept", "application/dns-json");

                using var response = await DnsHttpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (!document.RootElement.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var answer in answers.EnumerateArray())
                {
                    if (!answer.TryGetProperty("type", out var type) || type.GetInt32() != 1)
                    {
                        continue;
                    }

                    if (!answer.TryGetProperty("data", out var data))
                    {
                        continue;
                    }

                    var ip = data.GetString();
                    if (!ip.IsNullOrEmpty() && IPAddress.TryParse(ip, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork && !IsPoisonedAddress(ip))
                    {
                        return parsed.ToString();
                    }
                }
            }
            catch
            {
                // try next endpoint
            }
        }

        return null;
    }

    private static string[] GetWsDomains(int dc, bool isMedia)
    {
        var resolvedDc = DcOverrides.TryGetValue(dc, out var overrideDc) ? overrideDc : dc;
        return isMedia
            ? [$"kws{resolvedDc}-1.web.telegram.org", $"kws{resolvedDc}.web.telegram.org"]
            : [$"kws{resolvedDc}.web.telegram.org", $"kws{resolvedDc}-1.web.telegram.org"];
    }

    private static string ResolveTargetDcIp(int dc, string destinationIp)
    {
        var configuredDcIps = LoadConfig().GetDcIpMap();
        return configuredDcIps.TryGetValue(dc, out var targetIp) ? targetIp : destinationIp;
    }

    private static byte[] PatchInitPacketDc(byte[] initPacket, int dc, bool isMedia)
    {
        try
        {
            var patched = initPacket.ToArray();
            var keystream = TransformCtr(patched.AsSpan(8, 32).ToArray(), patched.AsSpan(40, 16).ToArray(), Zero64);
            var encodedDc = BitConverter.GetBytes((short)(isMedia ? dc : -dc));
            patched[60] = (byte)(keystream[60] ^ encodedDc[0]);
            patched[61] = (byte)(keystream[61] ^ encodedDc[1]);
            return patched;
        }
        catch
        {
            return initPacket;
        }
    }

    private static async Task AwaitQuietly(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // ignored
        }
    }

    private static uint ToUInt32(string ip)
    {
        var address = IPAddress.Parse(ip).GetAddressBytes();
        return ((uint)address[0] << 24) | ((uint)address[1] << 16) | ((uint)address[2] << 8) | address[3];
    }

    private static TelegramWsProxyConfig LoadConfig()
    {
        try
        {
            EnsureConfigFile();
            var json = File.ReadAllText(GetConfigPath());
            return JsonSerializer.Deserialize<TelegramWsProxyConfig>(json) ?? TelegramWsProxyConfig.CreateDefault();
        }
        catch
        {
            return TelegramWsProxyConfig.CreateDefault();
        }
    }

    private static void EnsureConfigFile()
    {
        Directory.CreateDirectory(GetAppDataDirectory());
        var configPath = GetConfigPath();
        if (!File.Exists(configPath))
        {
            var json = JsonSerializer.Serialize(TelegramWsProxyConfig.CreateDefault(), new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(configPath, json);
        }
    }

    private static (int Dc, bool IsMedia)? TryResolveDcInfo(string resolvedAddress, byte[] initPacket)
    {
        if (TryExtractDcFromInit(initPacket, out var dc, out var isMedia))
        {
            return (dc, isMedia);
        }

        return IpToDc.TryGetValue(resolvedAddress, out var mapped) ? mapped : null;
    }

    private static bool TryExtractDcFromInit(byte[] data, out int dc, out bool isMedia)
    {
        dc = 0;
        isMedia = false;

        if (data.Length < 64)
        {
            return false;
        }

        try
        {
            var key = data.AsSpan(8, 32).ToArray();
            var iv = data.AsSpan(40, 16).ToArray();
            var keystream = TransformCtr(key, iv, Zero64);

            Span<byte> plain = stackalloc byte[8];
            for (var i = 0; i < 8; i++)
            {
                plain[i] = (byte)(data[56 + i] ^ keystream[56 + i]);
            }

            var proto = BinaryPrimitives.ReadUInt32LittleEndian(plain[..4]);
            var rawDc = BinaryPrimitives.ReadInt16LittleEndian(plain.Slice(4, 2));
            if (!ValidProtocols.Contains(proto))
            {
                return false;
            }

            var absDc = Math.Abs(rawDc);
            if (absDc is >= 1 and <= 5 or 203)
            {
                dc = absDc;
                isMedia = rawDc < 0;
                return true;
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }

    private static byte[] TransformCtr(byte[] key, byte[] iv, byte[] input)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;

        using var encryptor = aes.CreateEncryptor();
        var counter = new byte[16];
        Buffer.BlockCopy(iv, 0, counter, 0, Math.Min(iv.Length, counter.Length));
        var output = new byte[input.Length];
        var keystreamBlock = new byte[16];

        for (var offset = 0; offset < input.Length; offset += 16)
        {
            encryptor.TransformBlock(counter, 0, 16, keystreamBlock, 0);
            var count = Math.Min(16, input.Length - offset);
            for (var i = 0; i < count; i++)
            {
                output[offset + i] = (byte)(input[offset + i] ^ keystreamBlock[i]);
            }

            IncrementCounter(counter);
        }

        return output;
    }

    private static void IncrementCounter(byte[] counter)
    {
        for (var i = counter.Length - 1; i >= 0; i--)
        {
            counter[i]++;
            if (counter[i] != 0)
            {
                break;
            }
        }
    }

    private static HttpClient CreateDnsHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    private sealed class RawWebSocket : IAsyncDisposable
    {
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        private readonly TcpClient _client;
        private readonly SslStream _stream;
        private bool _closed;

        private RawWebSocket(TcpClient client, SslStream stream)
        {
            _client = client;
            _stream = stream;
        }

        public static async Task<RawWebSocket> ConnectAsync(string targetIp, string domain, CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            var client = new TcpClient(AddressFamily.InterNetwork)
            {
                NoDelay = true
            };

            try
            {
                await client.ConnectAsync(IPAddress.Parse(targetIp), 443, timeoutCts.Token);

                var stream = new SslStream(
                    client.GetStream(),
                    leaveInnerStreamOpen: false,
                    static (_, _, _, _) => true);

                await stream.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = domain,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    },
                    timeoutCts.Token);

                var request = string.Join("\r\n",
                [
                    "GET /apiws HTTP/1.1",
                    $"Host: {domain}",
                    "Upgrade: websocket",
                    "Connection: Upgrade",
                    $"Sec-WebSocket-Key: {Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))}",
                    "Sec-WebSocket-Version: 13",
                    "Sec-WebSocket-Protocol: binary",
                    "Origin: https://web.telegram.org",
                    $"User-Agent: {UserAgent}",
                    string.Empty,
                    string.Empty
                ]);

                var requestBytes = Encoding.ASCII.GetBytes(request);
                await stream.WriteAsync(requestBytes, timeoutCts.Token);
                await stream.FlushAsync(timeoutCts.Token);

                var responseLines = new List<string>();
                while (true)
                {
                    var line = await ReadHttpLineAsync(stream, timeoutCts.Token);
                    if (line.Length == 0)
                    {
                        break;
                    }
                    responseLines.Add(line);
                }

                if (responseLines.Count == 0)
                {
                    throw new IOException("Empty WebSocket handshake response.");
                }

                var statusLine = responseLines[0];
                var parts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !int.TryParse(parts[1], out var statusCode) || statusCode != 101)
                {
                    throw new IOException($"WS handshake failed: {statusLine}");
                }

                return new RawWebSocket(client, stream);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken)
        {
            if (_closed)
            {
                throw new IOException("WebSocket already closed.");
            }

            var frame = BuildClientFrame(0x2, payload);
            await _stream.WriteAsync(frame, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }

        public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
        {
            while (!_closed)
            {
                var (opcode, payload) = await ReadFrameAsync(cancellationToken);
                switch (opcode)
                {
                    case 0x2:
                    case 0x1:
                        return payload;
                    case 0x8:
                        _closed = true;
                        await CloseAsync(CancellationToken.None);
                        return null;
                    case 0x9:
                        await SendControlAsync(0xA, payload, cancellationToken);
                        continue;
                    case 0xA:
                        continue;
                    default:
                        continue;
                }
            }

            return null;
        }

        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            try
            {
                await SendControlAsync(0x8, Array.Empty<byte>(), cancellationToken);
            }
            catch
            {
                // ignored
            }

            try
            {
                _stream.Close();
                _client.Close();
            }
            catch
            {
                // ignored
            }
        }

        public async ValueTask DisposeAsync()
        {
            await CloseAsync(CancellationToken.None);
            _stream.Dispose();
            _client.Dispose();
        }

        private async Task SendControlAsync(byte opcode, byte[] payload, CancellationToken cancellationToken)
        {
            var frame = BuildClientFrame(opcode, payload);
            await _stream.WriteAsync(frame, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }

        private async Task<(byte Opcode, byte[] Payload)> ReadFrameAsync(CancellationToken cancellationToken)
        {
            var header = await ReadExactlyAsync(_stream, 2, cancellationToken);
            var opcode = (byte)(header[0] & 0x0F);
            var masked = (header[1] & 0x80) != 0;
            ulong length = (uint)(header[1] & 0x7F);
            if (length == 126)
            {
                var ext = await ReadExactlyAsync(_stream, 2, cancellationToken);
                length = BinaryPrimitives.ReadUInt16BigEndian(ext);
            }
            else if (length == 127)
            {
                var ext = await ReadExactlyAsync(_stream, 8, cancellationToken);
                length = BinaryPrimitives.ReadUInt64BigEndian(ext);
            }

            byte[]? mask = null;
            if (masked)
            {
                mask = await ReadExactlyAsync(_stream, 4, cancellationToken);
            }

            var payload = length == 0
                ? Array.Empty<byte>()
                : await ReadExactlyAsync(_stream, checked((int)length), cancellationToken);

            if (masked && mask != null && payload.Length > 0)
            {
                for (var i = 0; i < payload.Length; i++)
                {
                    payload[i] ^= mask[i % 4];
                }
            }

            return (opcode, payload);
        }

        private static byte[] BuildClientFrame(byte opcode, byte[] payload)
        {
            using var ms = new MemoryStream();
            ms.WriteByte((byte)(0x80 | opcode));

            var mask = RandomNumberGenerator.GetBytes(4);
            if (payload.Length < 126)
            {
                ms.WriteByte((byte)(0x80 | payload.Length));
            }
            else if (payload.Length <= ushort.MaxValue)
            {
                ms.WriteByte(0x80 | 126);
                var len = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)payload.Length);
                ms.Write(len);
            }
            else
            {
                ms.WriteByte(0x80 | 127);
                var len = new byte[8];
                BinaryPrimitives.WriteUInt64BigEndian(len, (ulong)payload.Length);
                ms.Write(len);
            }

            ms.Write(mask);
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] ^= mask[i % 4];
            }
            ms.Write(payload);
            return ms.ToArray();
        }

        private static async Task<string> ReadHttpLineAsync(Stream stream, CancellationToken cancellationToken)
        {
            using var ms = new MemoryStream();
            while (true)
            {
                var one = await ReadExactlyAsync(stream, 1, cancellationToken);
                if (one[0] == (byte)'\n')
                {
                    break;
                }
                if (one[0] != (byte)'\r')
                {
                    ms.WriteByte(one[0]);
                }
            }

            return Encoding.ASCII.GetString(ms.ToArray());
        }
    }

    private sealed class AbridgedMessageSplitter
    {
        private readonly CtrCipherState _cipherState;

        private AbridgedMessageSplitter(byte[] initPacket)
        {
            _cipherState = new CtrCipherState(initPacket.AsSpan(8, 32).ToArray(), initPacket.AsSpan(40, 16).ToArray(), 64);
        }

        public static AbridgedMessageSplitter? TryCreate(byte[] initPacket)
        {
            try
            {
                return new AbridgedMessageSplitter(initPacket);
            }
            catch
            {
                return null;
            }
        }

        public List<byte[]> Split(byte[] chunk)
        {
            var plain = _cipherState.Transform(chunk);
            var result = new List<byte[]>();
            var start = 0;
            var pos = 0;

            while (pos < plain.Length)
            {
                int messageLength;
                if (plain[pos] == 0x7F)
                {
                    if (pos + 4 > plain.Length)
                    {
                        break;
                    }
                    messageLength = (int)(BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(pos + 1, 4)) & 0x00FFFFFF) * 4;
                    pos += 4;
                }
                else
                {
                    messageLength = plain[pos] * 4;
                    pos += 1;
                }

                if (messageLength <= 0 || pos + messageLength > plain.Length)
                {
                    break;
                }

                pos += messageLength;
                result.Add(chunk[start..pos]);
                start = pos;
            }

            if (result.Count == 0)
            {
                result.Add(chunk);
            }
            else if (start < chunk.Length)
            {
                result.Add(chunk[start..]);
            }

            return result;
        }
    }

    private sealed class CtrCipherState
    {
        private readonly ICryptoTransform _encryptor;
        private readonly byte[] _counter = new byte[16];
        private readonly byte[] _keystream = new byte[16];
        private int _keystreamOffset = 16;

        public CtrCipherState(byte[] key, byte[] iv, int skipBytes)
        {
            var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = key;
            _encryptor = aes.CreateEncryptor();
            Buffer.BlockCopy(iv, 0, _counter, 0, Math.Min(iv.Length, _counter.Length));
            if (skipBytes > 0)
            {
                _ = Transform(new byte[skipBytes]);
            }
        }

        public byte[] Transform(byte[] input)
        {
            var output = new byte[input.Length];
            for (var i = 0; i < input.Length; i++)
            {
                if (_keystreamOffset >= _keystream.Length)
                {
                    _encryptor.TransformBlock(_counter, 0, _counter.Length, _keystream, 0);
                    IncrementCounter(_counter);
                    _keystreamOffset = 0;
                }

                output[i] = (byte)(input[i] ^ _keystream[_keystreamOffset++]);
            }

            return output;
        }
    }

    private sealed class TelegramWsProxyConfig
    {
        public string Host { get; set; } = DefaultHost;
        public int Port { get; set; } = DefaultPort;
        public List<string>? DcIps { get; set; } = DefaultDcIpEntries.ToList();

        public static TelegramWsProxyConfig CreateDefault()
        {
            return new TelegramWsProxyConfig();
        }

        public Dictionary<int, string> GetDcIpMap()
        {
            var map = new Dictionary<int, string>();
            IEnumerable<string> entries = DcIps?.ToArray() ?? DefaultDcIpEntries;
            foreach (var entry in entries)
            {
                var parts = entry.Split(':', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || !int.TryParse(parts[0], out var dc) || !IPAddress.TryParse(parts[1], out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                map[dc] = address.ToString();
            }

            return map;
        }
    }
}
