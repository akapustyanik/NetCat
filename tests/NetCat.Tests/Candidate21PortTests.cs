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
public sealed class Candidate21PortTests
{
    internal sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "NetCat-C21-" + Guid.NewGuid().ToString("N"));
        public readonly CancellationTokenSource Timeout = new(TimeSpan.FromSeconds(35));
        public readonly RouterService Router;
        public readonly AppSettings Settings;
        public readonly NetworkSnapshot Physical;
        public readonly List<JsonNode> Starts = [];
        public Action<JsonNode>? BeforeStart;
        public Action<JsonNode>? Configure;
        public Fixture()
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback);
            Physical = new(nic.Name, nic.GetIPProperties().GetIPv4Properties()!.Index, "127.0.0.1", "127.0.0.1", []);
            var profile = ProfileImporter.ParseLink("socks://127.0.0.1:9");
            Settings = new() { Tun = false, Profiles = [profile], MainProfileId = profile.Id, SocksPort = OpenVpnService.FreePort() };
            Router = new(RoutingTests.ModuleRoot, Root) { StartProcessOverride = (host, exe, args) => {
                var path=args[Array.IndexOf(args, "-c") + 1];
                var config = JsonNode.Parse(File.ReadAllText(path))!;
                Configure?.Invoke(config);File.WriteAllText(path,config.ToJsonString());
                Starts.Add(config.DeepClone()); BeforeStart?.Invoke(config); host.Start(exe, args);
            } };
        }
        public Task Start() => Router.SetVpnAsync(Settings, true, Timeout.Token, physical: Physical);
        public Task Stop() => Router.SetVpnAsync(Settings, false, Timeout.Token, physical: Physical);
        public static int Port(JsonNode config, string tag) => (int)config["inbounds"]!.AsArray().Single(x => x!["tag"]!.ToString() == tag)!["listen_port"]!;
        public int CurrentPort(string tag) => Port(JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "router.json")))!, tag);
        public void Dispose() { Router.Dispose(); Timeout.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private static async Task Collision(string tag)
    {
        using var f = new Fixture(); await f.Start(); int old = f.CurrentPort(tag); await f.Stop();
        using var occupied = new UdpClient(AddressFamily.InterNetwork); occupied.Client.ExclusiveAddressUse = true;
        occupied.Client.Bind(new IPEndPoint(IPAddress.Loopback, old));
        await f.Start();
        Assert.True(f.Router.VpnRunning); Assert.NotEqual(old, f.CurrentPort(tag));
        Assert.InRange(f.Starts.Count, 2, 4);
        Assert.All(f.Starts.Skip(2), c => Assert.NotEqual(old, Fixture.Port(c, tag)));
        if(Environment.GetEnvironmentVariable("NETCAT_ACCEPTANCE_EVIDENCE") is {} evidence)
            await File.WriteAllTextAsync(Path.Combine(evidence,tag+"-collision.json"),System.Text.Json.JsonSerializer.Serialize(new{Tag=tag,OldPort=old,NewPort=f.CurrentPort(tag),Attempts=f.Starts.Count-1,Running=f.Router.VpnRunning,TunEnabled=false,Scope="real Windows sockets + native sing-box; component integration"}));
    }
    [Fact] public Task InternalDnsReturnPortCollisionReallocatesAndStartsMainRouter() => Collision("dns-direct-return");
    [Fact] public Task InternalPortCollisionDoesNotReuseFailedPort() => Collision("dns-vpn-return");

    [Fact] public async Task InternalPortCollisionRecoveryIsBounded()
    {
        using var f = new Fixture(); var held = new List<UdpClient>();
        try
        {
            f.BeforeStart = config => { var u = new UdpClient(AddressFamily.InterNetwork); u.Client.ExclusiveAddressUse = true;
                u.Client.Bind(new IPEndPoint(IPAddress.Loopback, Fixture.Port(config, "dns-direct-return"))); held.Add(u); };
            await Assert.ThrowsAsync<PortCollisionException>(f.Start);
            Assert.Equal(3, f.Starts.Count); Assert.False(f.Router.VpnRunning);
            Assert.Equal(3, f.Starts.Select(c => Fixture.Port(c, "dns-direct-return")).Distinct().Count());
        }
        finally { foreach (var u in held) u.Dispose(); }
    }
    [Fact] public async Task ReleasedInternalPortEventuallyRecoversWithoutAppRestart()
    {
        using var f = new Fixture(); var held = new List<UdpClient>();
        try
        {
            f.BeforeStart = c => { var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, Fixture.Port(c, "dns-vpn-return"))); held.Add(u); };
            await Assert.ThrowsAsync<PortCollisionException>(f.Start);
            foreach (var u in held) u.Dispose(); held.Clear(); f.BeforeStart = null;
            await f.Start(); Assert.True(f.Router.VpnRunning);
        }
        finally { foreach (var u in held) u.Dispose(); }
    }
}
