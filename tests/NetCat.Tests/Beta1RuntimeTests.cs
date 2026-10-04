using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using NetCat.Updater;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace NetCat.Tests;

public sealed class Beta1RuntimeTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "NetCat-Beta1Runtime-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    private string Put(string root, string relative, byte[] content)
    {
        var path = PortableUpdate.SafePath(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, content); return path;
    }
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private sealed class MockRelease(Dictionary<string, byte[]> assets) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(assets[request.RequestUri!.AbsolutePath]) });
    }
    [Fact]
    public async Task SignedLocalReleaseDownloadVerifyExtractApplyAndForgeryRejection()
    {
        var payload = Path.Combine(folder, "source"); var stage = Path.Combine(folder, "stage"); var target = Path.Combine(folder, "target");
        Directory.CreateDirectory(stage); var components = new List<PackageComponent>(); const string version = "1.0.0-beta.2";
        foreach (var key in ModuleUpdater.Keys)
        {
            var path = key == "netcat" ? "NetCat.exe" : $"modules/{key}/test.bin"; byte[] data = Encoding.UTF8.GetBytes(key + " new");
            Put(payload, path, data); Put(target, path, Encoding.UTF8.GetBytes(key + " old"));
            components.Add(new(key, key == "netcat" ? version : "2.0.0", [new(path, Hash(data))]));
        }
        var inner = new PackageManifest(1, version, components); var innerBytes = JsonSerializer.SerializeToUtf8Bytes(inner, JsonSettings.Options);
        Put(payload, PortableUpdate.ManifestPath, innerBytes);
        var zip = Path.Combine(folder, "source.zip"); ZipFile.CreateFromDirectory(payload, zip); var archive = await File.ReadAllBytesAsync(zip);
        var asset = $"NetCat-v{version}-win-x64.zip";
        var signed = inner with { Channel = "beta", Package = new(asset, Hash(archive)), ExtraFiles = [new(PortableUpdate.ManifestPath, Hash(innerBytes))] };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(signed); var keyPair = new Ed25519PrivateKeyParameters(RandomNumberGenerator.GetBytes(32), 0);
        var signer = new Ed25519Signer(); signer.Init(true, keyPair); signer.BlockUpdate(bytes, 0, bytes.Length); var signature = signer.GenerateSignature();
        using var http = new HttpClient(new MockRelease(new() { ["/release-manifest.json"] = bytes, ["/release-manifest.sig"] = signature, ["/" + asset] = archive }));
        var downloaded = await ReleaseTrust.DownloadAsync(http, "https://mock.test/release-manifest.json", 100000, default);
        var sig = await ReleaseTrust.DownloadAsync(http, "https://mock.test/release-manifest.sig", 64, default);
        var verified = ReleaseTrust.Verify(downloaded, sig, keyPair.GeneratePublicKey().GetEncoded(), "1.0.0-beta.1", "beta", asset, version);
        Put(stage, "package.zip", await ReleaseTrust.DownloadAsync(http, "https://mock.test/" + asset, 1000000, default));
        await PortableUpdate.ExtractVerifiedAsync(stage, verified.Package!.Sha256, default);
        await ReleaseTrust.VerifyPayloadAsync(verified, Path.Combine(stage, "payload"), default);
        var plan = PortableUpdate.Plan(verified, k => k == "netcat" ? "1.0.0-beta.1" : "1.0.0", new HashSet<string>());
        PortableUpdate.ApplyFiles(target, Path.Combine(stage, "payload"), plan, ModuleUpdater.Keys.ToDictionary(k => k, _ => "1.0.0"));
        Assert.Equal("netcat new", File.ReadAllText(Path.Combine(target, "NetCat.exe")));
        sig[0] ^= 1; Assert.Throws<InvalidDataException>(() => ReleaseTrust.Verify(downloaded, sig, keyPair.GeneratePublicKey().GetEncoded(), "1.0.0-beta.1", "beta", asset, version));
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableUpdate.ExtractVerifiedAsync(stage, new string('a', 64), default));
        Put(Path.Combine(stage, "payload"), "unexpected.exe", [1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseTrust.VerifyPayloadAsync(verified, Path.Combine(stage, "payload"), default));
    }
    [Fact]
    public async Task RealCoreStaleBindFailsThenRefreshRestoresDirectWithDeadVpn()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = deadline.Token;
        var physical = PhysicalNetwork.Capture(); var current = physical with { Address = "192.0.2.123" };
        using var server = new TcpListener(IPAddress.Parse(physical.Address), 0); server.Start(); var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var accepted = await server.AcceptTcpClientAsync(ct); using var stream = accepted.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true); while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"), ct);
        }, ct);
        var p = ProfileImporter.ParseLink("socks://127.0.0.1:9#Unavailable-upstream");
        var settings = new AppSettings { Tun = false, Mode = RoutingMode.Global, SocksPort = OpenVpnService.FreePort(), MainProfileId = p.Id, Profiles = [p], YouTube = ServiceRoute.Zapret, Discord = ServiceRoute.Zapret,
            Rules = [new() { Kind = RuleKind.IpCidr, Value = physical.Address + "/32", Target = RouteTarget.Direct }] };
        ProcessHost? owned = null;
        using var router = new RouterService(RoutingTests.ModuleRoot, Path.Combine(folder, "runtime"))
        {
            CaptureBinding = _ => current,
            StartProcessOverride = (host, exe, args) => { owned = host; host.Start(exe, args); }
        };
        var logs = new ConcurrentQueue<string>(); router.Log += logs.Enqueue;
        await router.SetVpnAsync(settings, true, ct);
        using var client = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{settings.SocksPort}"), UseProxy = true }) { Timeout = TimeSpan.FromSeconds(4) };
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync($"http://{physical.Address}:{port}/probe", ct));
        for (int i = 0; i < 30 && !logs.Any(l => l.Contains("bind", StringComparison.OrdinalIgnoreCase)); i++) await Task.Delay(50, ct);
        Assert.Contains(logs, l => l.Contains("bind", StringComparison.OrdinalIgnoreCase));
        current = physical;
        Assert.True(await router.RefreshNetworkAsync(settings, false, "simulated DHCP", ct));
        Assert.Equal("OK", await client.GetStringAsync($"http://{physical.Address}:{port}/probe", ct)); await serverTask;
        Assert.Equal(physical.Address, router.ActivePhysical!.Address); Assert.True(router.VpnRequested); Assert.Equal(p.Id, router.ActiveProfileId);
        // Crash only this isolated core; the user's TUN and installed application are untouched.
        await owned!.StopAsync(); Assert.False(router.Running);
        var health = await TunnelInspection.ReadAsync(router.Running, router.ListenPort, false, ct); Assert.True(health.StructuralFailure);
        Assert.True(await router.RefreshNetworkAsync(settings, true, "owned core crash", ct)); Assert.True(router.Running);
        Assert.True(await RouterService.SocksAvailableAsync("127.0.0.1", router.ListenPort, ct));
        var evidence = RoutingTests.TestArtifacts("beta1-evidence"); Directory.CreateDirectory(evidence);
        await File.WriteAllLinesAsync(Path.Combine(evidence, "stale-bind-runtime.txt"), logs, ct);
    }
    [Theory]
    [InlineData(false, false, false)] [InlineData(true, false, true)] [InlineData(true, true, false)] [InlineData(true, true, true)]
    public async Task UpdatedDnsSyntaxPassesNativeValidatorAndOpenVpnRulesFailClosed(bool tun, bool openVpn, bool zapret)
    {
        var physical = PhysicalNetwork.Capture() with { HasIpv6DefaultRoute = false };
        var p = ProfileImporter.ParseLink("socks://127.0.0.1:9#dead");
        var s = new AppSettings { Mode = RoutingMode.Global, YouTube = ServiceRoute.Zapret, Discord = ServiceRoute.Zapret, OpenVpnDomains = "corp.example", Rules = [new() { Kind = RuleKind.IpCidr, Value = "10.42.0.0/16", Target = RouteTarget.OpenVpn }] };
        var config = SingBoxConfig.Build(s, physical, p, openVpn ? new("NetCat-OpenVPN", 500, "10.42.0.2", "10.42.0.1", "10.42.0.53") : null, tun, zapretRunning: zapret);
        var rules = config["route"]!["rules"]!.AsArray(); var corporate = rules.Single(r => r?["ip_cidr"]?.ToJsonString().Contains("10.42.0.0") == true);
        Assert.Equal(openVpn ? "route" : "reject", corporate!["action"]!.ToString());
        Assert.DoesNotContain(config["dns"]!["rules"]!.AsArray(), r => r?["strategy"] != null);
        var path = Put(folder, "router.json", Encoding.UTF8.GetBytes(config.ToJsonString()));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await ProcessHost.RunAsync(Path.Combine(RoutingTests.ModuleRoot, "sing-box/sing-box.exe"), ["check", "-c", path], deadline.Token);
        Assert.True(result.Code == 0, result.Output); Assert.DoesNotContain("deprecated", result.Output, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task MalformedDnsErrorsReproducedWithKnownUdpPayloadAndInbound()
    {
        var physical = PhysicalNetwork.Capture(); int local = OpenVpnService.FreePort(), dnsPort = OpenVpnService.FreeTcpUdpPort();
        var config = SingBoxConfig.Build(new(), physical, null, null, false, local);
        config["log"]!["level"] = "debug";
        // Reproduce the released configuration's unconditional port-53 hijack.
        config["route"]!["rules"]!.AsArray().First(r => r?["action"]?.ToString() == "hijack-dns")!.AsObject().Remove("protocol");
        config["inbounds"]!.AsArray().Add(new JsonObject { ["type"] = "direct", ["tag"] = "diagnostic-malformed-dns", ["listen"] = "127.0.0.1", ["listen_port"] = dnsPort,
            ["network"] = "udp", ["override_address"] = "1.1.1.1", ["override_port"] = 53 });
        var file = Put(folder, "dns-trace.json", Encoding.UTF8.GetBytes(config.ToJsonString()));
        using var host = new ProcessHost(); var logs = new ConcurrentQueue<string>(); host.Line += logs.Enqueue;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12)); var ct = deadline.Token;
        host.Start(Path.Combine(RoutingTests.ModuleRoot, "sing-box/sing-box.exe"), ["run", "-c", file]);
        await RouterService.WaitPortAsync(local, host, ct);
        using var udp = new UdpClient(); await udp.SendAsync(new byte[] { 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, new IPEndPoint(IPAddress.Loopback, dnsPort), ct);
        using var empty = new UdpClient(); await empty.SendAsync(new byte[12], new IPEndPoint(IPAddress.Loopback, dnsPort), ct);
        for (int i = 0; i < 60 && !logs.Any(l => l.Contains("bad question size")); i++) await Task.Delay(100, ct);
        await host.StopAsync();
        var evidence = RoutingTests.TestArtifacts("beta1-evidence"); Directory.CreateDirectory(evidence);
        await File.WriteAllLinesAsync(Path.Combine(evidence, "malformed-dns-runtime.txt"), new[] { "Isolated direct UDP inbound -> destination 1.1.1.1:53 -> generated port-53 hijack. Input: truncated 12-byte query header (QDCOUNT=1, missing question); then 12-byte zero header (QDCOUNT=0). No TUN or live settings changes. This does not identify the historical sender in the user's TUN." }.Concat(logs), ct);
        Assert.Contains(logs, l => l.Contains("diagnostic-malformed-dns"));
        Assert.Contains(logs, l => l.Contains("bad question size: 0"));
        // The historical TUN-specific overflow message is not claimed as reproduced by a direct inbound.

    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DnsSniffAcceptsValidUdpAndTcpButRejectsEmptyQuestions(bool tcp)
    {
        var physical = PhysicalNetwork.Capture(); var ip = IPAddress.Parse(physical.Address);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var ct = deadline.Token;
        using var resolver = new UdpClient(new IPEndPoint(ip, 0));
        var serving = Task.Run(async () =>
        {
            var received = await resolver.ReceiveAsync(ct); var reply = received.Buffer;
            reply[2] = 0x81; reply[3] = 0x80;
            await resolver.SendAsync(reply, received.RemoteEndPoint, ct);
        }, ct);
        int local = OpenVpnService.FreePort(), ingress = OpenVpnService.FreeTcpUdpPort();
        var config = SingBoxConfig.Build(new(), physical, null, null, false, local);
        config["log"]!["level"] = "debug";
        config["dns"]!["servers"]![0]!["server"] = ip.ToString(); config["dns"]!["servers"]![0]!["server_port"] = ((IPEndPoint)resolver.Client.LocalEndPoint!).Port;
        config["inbounds"]!.AsArray().Add(new JsonObject { ["type"] = "direct", ["tag"] = "dns-regression", ["listen"] = "127.0.0.1", ["listen_port"] = ingress, ["override_address"] = "1.1.1.1", ["override_port"] = 53 });
        var file = Put(folder, "valid-dns.json", Encoding.UTF8.GetBytes(config.ToJsonString()));
        using var host = new ProcessHost(); var logs = new ConcurrentQueue<string>(); host.Line += logs.Enqueue;
        host.Start(Path.Combine(RoutingTests.ModuleRoot, "sing-box/sing-box.exe"), ["run", "-c", file]); await RouterService.WaitPortAsync(local, host, ct);
        using var bad = new UdpClient(); await bad.SendAsync(new byte[12], new IPEndPoint(IPAddress.Loopback, ingress), ct);
        await Task.Delay(150, ct);
        byte[] query = [0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 3, (byte)'f', (byte)'o', (byte)'o', 4, (byte)'t', (byte)'e', (byte)'s', (byte)'t', 0, 0, 1, 0, 1];
        byte[] answer;
        if (tcp)
        {
            using var connection = new TcpClient(); await connection.ConnectAsync(IPAddress.Loopback, ingress, ct); using var stream = connection.GetStream();
            await stream.WriteAsync(new byte[] { 0, (byte)query.Length }.Concat(query).ToArray(), ct);
            var length = new byte[2]; await stream.ReadExactlyAsync(length, ct); answer = new byte[length[0] * 256 + length[1]]; await stream.ReadExactlyAsync(answer, ct);
        }
        else
        {
            using var udp = new UdpClient(); await udp.SendAsync(query, new IPEndPoint(IPAddress.Loopback, ingress), ct); answer = (await udp.ReceiveAsync(ct)).Buffer;
        }
        Assert.Equal(query[0], answer[0]); Assert.Equal(query[1], answer[1]); Assert.Equal(0x80, answer[2] & 0x80);
        await serving; await host.StopAsync();
        Assert.DoesNotContain(logs, l => l.Contains("bad question size") || l.Contains("overflow unpacking"));
        Assert.Contains(logs, l => l.Contains("=> reject")); Assert.Contains(logs, l => l.Contains("=> hijack-dns"));
    }
}
