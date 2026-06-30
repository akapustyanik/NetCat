namespace ServiceLib.Services;

public sealed class AutoProtocolWarmStandbyService : IDisposable
{
    private const string WarmInboundPrefix = "auto-failover-warm-";
    private const string WarmUrl = "http://cp.cloudflare.com/generate_204";
    private readonly List<HttpClient> _clients = [];
    private CancellationTokenSource? _cts;

    public void StartFromSingboxConfig(string configPath)
    {
        Stop();

        var warmInbounds = ReadWarmInbounds(configPath);
        if (warmInbounds.Count < 2)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        foreach (var inbound in warmInbounds)
        {
            var client = CreateWarmClient(inbound.Port);
            _clients.Add(client);
            _ = Task.Run(() => WarmLoopAsync(inbound, client, _cts.Token));
        }

        Logging.SaveLog($"AutoProtocolFailover warm standby started | count={warmInbounds.Count} | ports={string.Join(",", warmInbounds.Select(item => item.Port))}");
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            foreach (var client in _clients)
            {
                client.Dispose();
            }
            _clients.Clear();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(nameof(AutoProtocolWarmStandbyService), ex);
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private static List<WarmInbound> ReadWarmInbounds(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return [];
            }

            var root = JsonNode.Parse(File.ReadAllText(configPath))?.AsObject();
            var inbounds = root?["inbounds"]?.AsArray();
            if (inbounds == null)
            {
                return [];
            }

            return inbounds
                .Select(node => node?.AsObject())
                .Where(node => node != null)
                .Select(node => new
                {
                    Tag = node?["tag"]?.GetValue<string>() ?? string.Empty,
                    Port = node?["listen_port"]?.GetValue<int?>()
                })
                .Where(item => item.Tag.StartsWith(WarmInboundPrefix, StringComparison.OrdinalIgnoreCase)
                               && item.Port is > 0 and <= 65535)
                .OrderBy(item => item.Tag, StringComparer.OrdinalIgnoreCase)
                .Select(item => new WarmInbound(item.Tag, item.Port!.Value))
                .ToList();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(nameof(AutoProtocolWarmStandbyService), ex);
            return [];
        }
    }

    private static HttpClient CreateWarmClient(int port)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"{Global.Socks5Protocol}{Global.Loopback}:{port}"),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 1,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    private static async Task WarmLoopAsync(WarmInbound inbound, HttpClient client, CancellationToken token)
    {
        var wasHealthy = false;
        var successLogged = false;

        while (!token.IsCancellationRequested)
        {
            var elapsed = Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, WarmUrl);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                elapsed.Stop();
                var healthy = (int)response.StatusCode is >= 200 and < 400;
                if (healthy && (!wasHealthy || !successLogged))
                {
                    Logging.SaveLog($"AutoProtocolFailover warm standby healthy | inbound={inbound.Tag} | port={inbound.Port} | status={(int)response.StatusCode} | elapsed={elapsed.ElapsedMilliseconds}ms");
                    successLogged = true;
                }
                else if (!healthy && wasHealthy)
                {
                    Logging.SaveLog($"AutoProtocolFailover warm standby unhealthy | inbound={inbound.Tag} | port={inbound.Port} | status={(int)response.StatusCode} | elapsed={elapsed.ElapsedMilliseconds}ms");
                    successLogged = false;
                }

                wasHealthy = healthy;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (wasHealthy)
                {
                    Logging.SaveLog($"AutoProtocolFailover warm standby failed | inbound={inbound.Tag} | port={inbound.Port} | error={ex.GetType().Name}: {ex.Message}");
                }
                wasHealthy = false;
                successLogged = false;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private sealed record WarmInbound(string Tag, int Port);
}
