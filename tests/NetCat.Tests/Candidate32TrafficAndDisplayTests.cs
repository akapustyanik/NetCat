using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

public sealed partial class Candidate32TrafficAndDisplayTests
{
    [Theory]
    [InlineData("server-a · Trojan gRPC", "Germany · Trojan gRPC")]
    [InlineData("Server A - WebSocket", "Germany - WebSocket")]
    [InlineData("server-b · TUIC", "Finland · TUIC")]
    [InlineData("SERVER B", "Finland")]
    [InlineData("server-ab · custom", "server-ab · custom")]
    [InlineData("My server-a profile", "My server-a profile")]
    public void LegacyLabelsRenameOnlyTheirPrefix(string original, string expected) => Assert.Equal(expected, ProfileDisplayNames.Normalize(original));

    [Fact]
    public void DisplayMigrationPreservesSecretsIdentityAndCustomSubscriptionName()
    {
        var profile = new Profile { Name = "server-b · WebSocket", Host = "fixture.invalid", OutboundJson = "{}", Password = "fixture-password", SubscriptionSource = "Моя подписка", SubscriptionItemId = "manifest:ws" };
        var original = JsonSettings.Clone(profile);
        var settings = new AppSettings { Profiles = [profile], MainProfileId = profile.Id };
        SettingsMigration.Apply(settings);
        Assert.Equal("Finland · WebSocket", profile.Name);
        profile.Name = original.Name;
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(profile));
        Assert.Equal(profile.Id, settings.MainProfileId);
    }

    [Fact]
    public void SubscriptionRefreshRenamesLegacyNameButPreservesCustomNameAndStableId()
    {
        var sub = new Subscription { Name = "Custom subscription" };
        var legacy = new Profile { Name = "server-a · WebSocket", SubscriptionId = sub.Id, SubscriptionItemId = "manifest:ws" };
        var custom = new Profile { Name = "My Trojan", SubscriptionId = sub.Id, SubscriptionItemId = "manifest:trojan" };
        var settings = new AppSettings { Subscriptions = [sub], Profiles = [legacy, custom], MainProfileId = legacy.Id };
        var incoming = new ImportResult([new() { Name = "server-a · WebSocket", SubscriptionItemId = "manifest:ws" }, new() { Name = "server-a · Trojan", SubscriptionItemId = "manifest:trojan" }], []);
        var result = SubscriptionMerge.Apply(settings, sub.Id, incoming).Settings;
        Assert.Equal("Germany · WebSocket", result.Profiles[0].Name);
        Assert.Equal("My Trojan", result.Profiles[1].Name);
        Assert.Equal(legacy.Id, result.Profiles[0].Id);
        Assert.Equal(custom.Id, result.Profiles[1].Id);
        Assert.Equal(sub.Name, result.Profiles[0].SubscriptionSource);
    }

    [Theory]
    [InlineData("https://github.com/akapustyanik/NetCat", true)]
    [InlineData("http://example.com/a?b=c", true)]
    [InlineData("https://user:password@example.com", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///not-an-approved-file", false)]
    [InlineData("https://example.com/\nargument", false)]
    [InlineData("https:", false)]
    public void ShellClassifierAcceptsWebAndRejectsUnapprovedTargets(string target, bool allowed)
    {
        var shell = typeof(MainWindow).Assembly.GetType("NetCat.UI.ExplorerDesktopShell")!;
        var classify = shell.GetMethod("ClassifyTarget", BindingFlags.NonPublic | BindingFlags.Static)!;
        if(allowed) Assert.Equal("WebProtocol", classify.Invoke(null, [target])!.ToString());
        else Assert.Throws<TargetInvocationException>(() => classify.Invoke(null, [target]));
    }

    [Fact]
    public void GithubButtonInheritsThemedButtonAndTrafficActionsAreSeparate()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while(root != null && !File.Exists(Path.Combine(root.FullName, "NetCat.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var doc = System.Xml.Linq.XDocument.Load(Path.Combine(root!.FullName, "src/NetCat.UI/MainWindow.xaml"));
        var button = doc.Descendants().Single(e => e.Name.LocalName == "Button" && (string?)e.Attribute("Content") == "GitHub →");
        var style = button.Descendants().Single(e => e.Name.LocalName == "Style");
        Assert.Equal("{StaticResource {x:Type Button}}", (string?)style.Attribute("BasedOn"));
        Assert.Contains(doc.Descendants(), e => (string?)e.Attribute("Click") == "TestTraffic_Click");
        Assert.Contains(doc.Descendants(), e => (string?)e.Attribute("Click") == "TestProfile_Click");
    }

    [Fact]
    public async Task TrafficProbeTransfersFullPayloadAndCorrelatedUdpThroughSocks()
    {
        await using var fixture = new TrafficFixture();
        var result = await ProfileTrafficProbe.MeasureAsync(fixture.SocksPort, CancellationToken.None, fixture.Targets);
        Assert.True(result.Success, result.Details);
        Assert.Equal(ProfileTrafficProbe.DownloadBytes, result.Download.ReceivedBytes);
        Assert.Equal(ProfileTrafficProbe.UploadBytes, fixture.UploadReceived);
        Assert.Equal(ProfileTrafficProbe.UdpRequests, fixture.UdpReceived);
        Assert.Equal(ProfileTrafficProbe.UdpRequests, result.UdpReceived);
        Assert.All(fixture.UdpHosts, host => Assert.Equal("stun.fixture.invalid", host));
        Assert.True(fixture.UdpControlAlive);
        Assert.Equal(2, fixture.ConnectCount);
    }

    [Fact]
    public async Task Http200WithoutPayloadEchoIsNotSuccessfulUpload()
    {
        await using var fixture = new TrafficFixture { CorruptEcho = true };
        var result = await ProfileTrafficProbe.MeasureAsync(fixture.SocksPort, CancellationToken.None, fixture.Targets);
        Assert.True(result.Download.Success);
        Assert.False(result.Upload.Success);
        Assert.False(result.Success);
        Assert.Equal(ProfileTrafficProbe.UploadBytes, fixture.UploadReceived);
        Assert.Contains("не подтвердил", result.Upload.Error);
        Assert.Equal(0, result.Upload.SentBytes); // Do not claim server receipt without matching echo.
    }

    [Fact]
    public async Task TruncatedDownloadDoesNotPassDataCheck()
    {
        await using var fixture = new TrafficFixture { Truncate = true };
        var result = await ProfileTrafficProbe.MeasureAsync(fixture.SocksPort, CancellationToken.None, fixture.Targets);
        Assert.False(result.Download.Success);
        Assert.True(result.Upload.Success);
        Assert.True(result.Download.ReceivedBytes < ProfileTrafficProbe.DownloadBytes);
    }

    [Fact]
    public async Task UncorrelatedUdpRepliesCannotProduceFalsePass()
    {
        await using var fixture = new TrafficFixture { CorruptUdp = true };
        var result = await ProfileTrafficProbe.MeasureAsync(fixture.SocksPort, CancellationToken.None, fixture.Targets);
        Assert.True(result.Download.Success);
        Assert.True(result.Upload.Success);
        Assert.Equal(8, fixture.UdpReceived);
        Assert.Equal(0, result.UdpReceived);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task UnavailableProxyNeverFallsBackToReachableDirectServer()
    {
        await using var fixture = new TrafficFixture();
        var closed = new TcpListener(IPAddress.Loopback, 0); closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port; closed.Stop();
        var result = await ProfileTrafficProbe.MeasureAsync(port, CancellationToken.None, fixture.Targets);
        Assert.False(result.Download.Success);
        Assert.False(result.Upload.Success);
        Assert.Equal(0, fixture.HttpReceived);
        Assert.Equal(0, fixture.UdpReceived);
        Assert.DoesNotContain(fixture.Targets.Download.Host, result.Download.Error);
    }

    [Fact]
    public async Task CancelledTrafficProbeClosesSocksConnections()
    {
        await using var fixture = new TrafficFixture { StallHttp = true };
        using var ct = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProfileTrafficProbe.MeasureAsync(fixture.SocksPort, ct.Token, fixture.Targets));
        await Task.Delay(150);
        Assert.Equal(0, fixture.ActiveConnections);
    }

    [Fact]
    public async Task OversizedDownloadStopsAtBoundAndFails()
    {
        await using var fixture = new TrafficFixture { Oversized = true };
        var result = await ProfileTrafficProbe.MeasureAsync(fixture.SocksPort, CancellationToken.None, fixture.Targets);
        Assert.False(result.Download.Success);
        Assert.Equal(ProfileTrafficProbe.DownloadBytes + 1, result.Download.ReceivedBytes);
        Assert.True(result.Upload.Success);
    }

    [Fact]
    public async Task HtmlHttp200DoesNotPassDownload()
    {
        await using var fixture = new TrafficFixture { Html = true };
        var result = await ProfileTrafficProbe.MeasureAsync(fixture.SocksPort, CancellationToken.None, fixture.Targets);
        Assert.False(result.Download.Success);
        Assert.Contains("HTML", result.Download.Error);
    }

    [Fact]
    public async Task UnsupportedUdpDoesNotHideSuccessfulTcp()
    {
        await using var fixture = new TrafficFixture { RejectUdp = true };
        var result = await ProfileTrafficProbe.MeasureAsync(fixture.SocksPort, CancellationToken.None, fixture.Targets);
        Assert.True(result.Download.Success);
        Assert.True(result.Upload.Success);
        Assert.False(result.Success);
        Assert.Equal(0, result.UdpReceived);
        Assert.Equal(0, fixture.UdpReceived);
        Assert.Contains("UDP", result.UdpError);
    }

    [Fact]
    public void TrafficResultsAreSeparateFromHttpAndNotPersisted()
    {
        var profile = new Profile(); profile.SetTestResult(new(true, 15, ""));
        var prior = profile.Result;
        profile.SetTrafficTestResult(TrafficTestResult.Failed("fixture-only-error"));
        Assert.Equal(prior, profile.Result);
        Assert.DoesNotContain("fixture-only-error", JsonSerializer.Serialize(profile));
        Assert.Contains("не проверяет системный TUN", profile.TrafficDetails);
    }

    private sealed class TrafficFixture : IAsyncDisposable
    {
        private readonly TcpListener socks = new(IPAddress.Loopback, 0), http = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly List<Task> workers = [];
        public int SocksPort { get; }
        public TrafficProbeTargets Targets { get; }
        public bool CorruptEcho { get; init; }
        public bool Truncate { get; init; }
        public bool CorruptUdp { get; init; }
        public bool StallHttp { get; init; }
        public bool Oversized { get; init; }
        public bool Html { get; init; }
        public bool RejectUdp { get; init; }
        public int UploadReceived, UdpReceived, ConnectCount, HttpReceived, ActiveConnections;
        public bool UdpControlAlive = true;
        public List<string> UdpHosts { get; } = [];
        public TrafficFixture()
        {
            socks.Start(); http.Start(); SocksPort = ((IPEndPoint)socks.LocalEndpoint).Port;
            var httpPort = ((IPEndPoint)http.LocalEndpoint).Port;
            Targets = new(new($"http://127.0.0.1:{httpPort}/download"), new($"http://127.0.0.1:{httpPort}/upload"), "stun.fixture.invalid", 19302);
            workers.Add(Accept(socks, Socks)); workers.Add(Accept(http, Http));
        }
        private async Task Accept(TcpListener listener, Func<TcpClient, Task> work)
        {
            try
            {
                while(!lifetime.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                    var task = Run(client, work); lock(workers) workers.Add(task);
                }
            }
            catch(OperationCanceledException) { }
        }
        private async Task Run(TcpClient client, Func<TcpClient, Task> work)
        {
            using(client) try { await work(client); } catch(Exception ex) when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
        private async Task Socks(TcpClient client)
        {
            Interlocked.Increment(ref ActiveConnections);
            try
            {
                var stream = client.GetStream(); var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, lifetime.Token);
                var auth = new byte[greeting[1]]; await stream.ReadExactlyAsync(auth, lifetime.Token);
                await stream.WriteAsync(new byte[] { 5, 0 }, lifetime.Token);
                var head = new byte[4]; await stream.ReadExactlyAsync(head, lifetime.Token);
                var addr = new byte[head[3] == 1 ? 4 : head[3] == 4 ? 16 : await ReadByte(stream)]; await stream.ReadExactlyAsync(addr, lifetime.Token);
                var portBytes = new byte[2]; await stream.ReadExactlyAsync(portBytes, lifetime.Token);
                var port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);
                if(head[1] == 1)
                {
                    Interlocked.Increment(ref ConnectCount);
                    using var remote = new TcpClient(); await remote.ConnectAsync(IPAddress.Loopback, port, lifetime.Token);
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 1 }, lifetime.Token);
                    var outbound = stream.CopyToAsync(remote.GetStream(), lifetime.Token);
                    var inbound = remote.GetStream().CopyToAsync(stream, lifetime.Token);
                    await Task.WhenAny(outbound, inbound); client.Close(); remote.Close();
                    try { await Task.WhenAll(outbound, inbound); } catch(Exception ex) when(ex is IOException or SocketException or ObjectDisposedException) { }
                }
                else
                {
                    if(RejectUdp) { await stream.WriteAsync(new byte[] { 5, 7, 0, 1, 0, 0, 0, 0, 0, 0 }, lifetime.Token); return; }
                    using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    var endpoint = (IPEndPoint)udp.Client.LocalEndPoint!;
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, (byte)(endpoint.Port >> 8), (byte)endpoint.Port }, lifetime.Token);
                    for(var i = 0; i < 8; i++)
                    {
                        var packet = await udp.ReceiveAsync(lifetime.Token);
                        Assert.Equal(3, packet.Buffer[3]);
                        var nameLength = packet.Buffer[4]; UdpHosts.Add(Encoding.ASCII.GetString(packet.Buffer, 5, nameLength));
                        UdpControlAlive &= client.Connected && !(client.Client.Poll(0, SelectMode.SelectRead) && client.Available == 0);
                        Interlocked.Increment(ref UdpReceived);
                        packet.Buffer[7 + nameLength] = 1; // Binding success; retain the request transaction.
                        if(CorruptUdp) packet.Buffer[7 + nameLength + 8] ^= 0xff;
                        await udp.SendAsync(packet.Buffer, packet.RemoteEndPoint, lifetime.Token);
                    }
                    var end = new byte[1]; await stream.ReadAsync(end, lifetime.Token);
                }
            }
            finally { Interlocked.Decrement(ref ActiveConnections); }
        }
        private async Task<int> ReadByte(Stream stream)
        { var b = new byte[1]; await stream.ReadExactlyAsync(b, lifetime.Token); return b[0]; }
        private async Task Http(TcpClient client)
        {
            Interlocked.Increment(ref HttpReceived); var stream = client.GetStream();
            var header = new List<byte>();
            while(header.Count < 16384)
            {
                header.Add((byte)await ReadByte(stream));
                if(header.Count >= 4 && Encoding.ASCII.GetString(header.TakeLast(4).ToArray()) == "\r\n\r\n") break;
            }
            if(StallHttp) { var end = new byte[1]; await stream.ReadAsync(end, lifetime.Token); return; }
            var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
            var path = lines[0].Split(' ')[1].Split('?')[0];
            if(path.StartsWith("/web-status/", StringComparison.Ordinal))
            {
                var status = int.Parse(path["/web-status/".Length..]);
                var content = status == 200 ? Encoding.ASCII.GetBytes("<html>Captive portal</html>") : [];
                var redirect = status == 302 ? "Location: /web-status/204\r\n" : "";
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\n{redirect}Content-Type: text/html\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n"), lifetime.Token);
                await stream.WriteAsync(content, lifetime.Token);
                return;
            }
            if(lines[0].Contains("/web-unavailable "))
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), lifetime.Token);
                return;
            }
            byte[] body;
            if(lines[0].StartsWith("POST"))
            {
                var length = int.Parse(lines.Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                var data = new byte[length]; await stream.ReadExactlyAsync(data, lifetime.Token); UploadReceived = length;
                body = JsonSerializer.SerializeToUtf8Bytes(new { data = CorruptEcho ? "ok" : Encoding.ASCII.GetString(data) });
            }
            else body = new byte[ProfileTrafficProbe.DownloadBytes + (Oversized ? 32 : 0)];
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {(Html ? "text/html" : "application/octet-stream")}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, lifetime.Token);
            await stream.WriteAsync(Truncate && !lines[0].StartsWith("POST") ? body.AsMemory(0, 1024) : body, lifetime.Token);
        }
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel(); socks.Stop(); http.Stop(); Task[] tasks; lock(workers) tasks = workers.ToArray();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)); lifetime.Dispose();
        }
    }
}
