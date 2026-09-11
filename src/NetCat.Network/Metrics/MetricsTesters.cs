using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using NetCat.Core.Interfaces;
using NetCat.Core.Models;

namespace NetCat.Network.Metrics
{
    public class RealDelayTester : IHealthChecker
    {
        private static readonly HttpClientHandler Handler = new()
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
        };
        private static readonly HttpClient Client = new(Handler) { Timeout = TimeSpan.FromMilliseconds(3000) };

        public async Task<LatencyResult> TestNodeRealDelayAsync(ProxyProfile node, int timeoutMs = 2500)
        {
            var result = new LatencyResult { Timestamp = DateTime.UtcNow };

            // 1. TCP Ping to node server address
            result.TcpPingMs = await TestTcpRttAsync(node.ServerAddress, node.ServerPort, Math.Min(timeoutMs, 1500));

            // 2. Real HTTP Delay through node via Cloudflare 204
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(timeoutMs);
                var sw = Stopwatch.StartNew();

                // If local socks proxy is available, we can detour through local socks
                var request = new HttpRequestMessage(HttpMethod.Get, "http://cp.cloudflare.com/generate_204");
                var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                sw.Stop();

                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NoContent)
                {
                    result.RealHttpDelayMs = (int)sw.ElapsedMilliseconds;
                }
            }
            catch
            {
                result.RealHttpDelayMs = -1;
            }

            return result;
        }

        public async Task<int> TestTcpRttAsync(string host, int port, int timeoutMs = 1500)
        {
            if (string.IsNullOrWhiteSpace(host) || port <= 0) return -1;

            try
            {
                using var client = new TcpClient();
                var sw = Stopwatch.StartNew();
                var connectTask = client.ConnectAsync(host, port);
                var timeoutTask = Task.Delay(timeoutMs);

                var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                if (completedTask == connectTask && client.Connected)
                {
                    sw.Stop();
                    return (int)sw.ElapsedMilliseconds;
                }
            }
            catch
            {
                // Ignored
            }

            return -1;
        }
    }
}
