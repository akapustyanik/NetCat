using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed class Candidate18DnsTests
{
    private sealed class Resolver : IDisposable
    {
        public readonly UdpClient Socket;
        private readonly CancellationTokenSource lifetime = new();
        public int Queries;
        public int Port => ((IPEndPoint)Socket.Client.LocalEndPoint!).Port;
        public Resolver(string address, byte response, int port = 0)
        { Socket = new(new IPEndPoint(IPAddress.Parse(address), port)); _ = Answer(response); }
        private async Task Answer(byte response)
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var q = await Socket.ReceiveAsync(lifetime.Token); Interlocked.Increment(ref Queries);
                    var bytes = q.Buffer.ToList(); bytes[2] = 0x81; bytes[3] = 0x80; bytes[6] = 0; bytes[7] = 1;
                    bytes.AddRange(new byte[] { 0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 2, 0x58, 0, 4, 10, 18, 0, response }); // TTL 600
                    await Socket.SendAsync(bytes.ToArray(), q.RemoteEndPoint, lifetime.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }
        public void Dispose() { lifetime.Cancel(); Socket.Dispose(); }
    }
    private static async Task Scenario(bool reconnect)
    {
        var root = Path.Combine(Path.GetTempPath(), "NetCat-C18-DNS-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var a = new Resolver("127.0.0.1", 1); using var b = new Resolver("127.0.0.2", 2, a.Port); using var ordinary = new Resolver("127.0.0.3", 3);
            var exe = Path.Combine(RoutingTests.ModuleRoot, "sing-box", "sing-box.exe");
            using var gateway = new OpenVpnSidecar(exe, Path.Combine(root, "gateway")) { DnsUpstreamPort = a.Port };
            var pa = new Profile { Protocol = "openvpn" }; var pb = new Profile { Protocol = "openvpn" };
            var settings = new AppSettings { Profiles = [pa, pb], OpenVpnProfileId = pa.Id, OpenVpnDomains = "corp.test" };
            var nic = NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback);
            var physical = new NetworkSnapshot(nic.Name, nic.GetIPProperties().GetIPv4Properties()!.Index, "127.0.0.1", "127.0.0.3", []);
            var link = new OpenVpnLink(nic.Name, physical.Index, "127.0.0.1", "127.0.0.1", "127.0.0.1", []) { ProfileId = pa.Id, Generation = 1 };
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await gateway.ApplyAsync(settings, link, ct.Token);
            int mainPort = OpenVpnService.FreePort(), dnsPort = OpenVpnService.FreeTcpUdpPort();
            var config = SingBoxConfig.Build(settings, physical, null, null, false, mainPort, openVpnGateway: gateway.Gateway);
            config["dns"]!["servers"]![0]!["server_port"] = ordinary.Port;
            config["inbounds"]!.AsArray().Add(new JsonObject { ["type"] = "direct", ["listen"] = "127.0.0.1", ["listen_port"] = dnsPort, ["network"] = "udp", ["override_address"] = "1.1.1.1", ["override_port"] = 53 });
            var file = Path.Combine(root, "main.json"); await File.WriteAllTextAsync(file, config.ToJsonString());
            using var main = new ProcessHost(); main.Start(exe, ["run", "-c", file]); await RouterService.WaitPortAsync(mainPort, main, ct.Token); var pid = main.Id;
            var query = new byte[] { 0x18, 0x01, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 4, 99, 111, 114, 112, 4, 116, 101, 115, 116, 0, 0, 1, 0, 1 };
            async Task<byte[]?> Query(int timeout = 2000)
            {
                using var client = new UdpClient(); client.Connect(IPAddress.Loopback, dnsPort); await client.SendAsync(query, ct.Token);
                using var deadline = new CancellationTokenSource(timeout);
                try { return (await client.ReceiveAsync(deadline.Token)).Buffer; } catch (OperationCanceledException) { return null; }
            }
            Assert.Equal(1, (await Query())![^1]);
            gateway.Invalidate(); await gateway.ApplyAsync(settings, null, ct.Token);
            var off = await Query(350); Assert.True(off == null || (off[3] & 15) != 0 || off[7] == 0, "OFF returned stale positive DNS answer");
            int oldQueries = a.Queries;
            settings.OpenVpnProfileId = reconnect ? pa.Id : pb.Id;
            await gateway.ApplyAsync(settings, link with { ProfileId = settings.OpenVpnProfileId, Generation = 2, Dns = "127.0.0.2" }, ct.Token);
            Assert.Equal(2, (await Query())![^1]); Assert.Equal(oldQueries, a.Queries); Assert.True(b.Queries > 0); Assert.Equal(0, ordinary.Queries);
            Assert.Equal(pid, main.Id); Assert.Equal(config.ToJsonString(), await File.ReadAllTextAsync(file));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public Task ProfileBDoesNotUseProfileADns() => Scenario(false);

    [Fact] public Task ReconnectReplacesCorporateDnsGeneration() => Scenario(true);
}
