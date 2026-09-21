using System.Diagnostics;
using System.Net;
using NetCat.Core;

namespace NetCat.Network;

public static class ConnectionLatency
{
    // Measure the active router, without starting test cores or changing routes.
    public static async Task<DelayResult> MeasureAsync(int socksPort, string url, CancellationToken ct, int timeoutSeconds = 8)
    {
        if (socksPort is < 1 or > 65535 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return new(false, -1, "Нет адреса теста");
        using var handler = new SocketsHttpHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"), UseProxy = true, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var watch = Stopwatch.StartNew();
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseContentRead, deadline.Token);
            if (!response.IsSuccessStatusCode) return new(false, -1, "HTTP " + (int)response.StatusCode);
            var first = (int)watch.ElapsedMilliseconds;
            // A server that closes the connection cannot provide a warm HTTP sample.
            if (response.Headers.ConnectionClose == true) return new(true, first);
            var samples = new List<int>();
            for (int i = 0; i < 2; i++)
            {
                watch.Restart();
                using var measured = await client.GetAsync(uri, HttpCompletionOption.ResponseContentRead, deadline.Token);
                if (!measured.IsSuccessStatusCode) return new(false, -1, "HTTP " + (int)measured.StatusCode);
                samples.Add((int)watch.ElapsedMilliseconds);
            }
            return new(true, (int)Math.Round(samples.Average()));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, -1, $"Нет HTTP-ответа за {timeoutSeconds} секунд"); }
        catch (HttpRequestException e) { return new(false, -1, "Ошибка соединения: " + e.HttpRequestError); }
    }
}
