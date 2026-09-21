using System.Text;
using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;
public sealed class RoutingTests
{
    private static readonly NetworkSnapshot Physical = new("Wi-Fi", 5, "192.168.20.10", "192.168.20.1", ["corp.example"]);
    private static Profile Vless() => ProfileImporter.ParseLink("vless://00000000-0000-4000-8000-000000000001@vpn.example.com:443?security=tls&sni=vpn.example.com#Test");
    [Theory]
    [InlineData("https://Portal.Example.com/path?q=x", "portal.example.com")]
    [InlineData("domain:vk.com", "vk.com")]
    [InlineData("*.example.com", "example.com")]
    public void DomainsNormalize(string value, string expected) => Assert.Equal(expected, RuleValidation.Domain(value));
    [Theory]
    [InlineData("10.0.4.5", "10.0.0.0/8", true)]
    [InlineData("172.32.1.1", "172.16.0.0/12", false)]
    [InlineData("fd00::12", "fc00::/7", true)]
    [InlineData("1.2.3.4", "1.0.0.0/33", false)]
    public void CidrMatches(string address, string cidr, bool matches) => Assert.Equal(matches, RuleValidation.MatchesCidr(address, cidr));
    [Fact]
    public void DirectCorporateDomainsUsePhysicalDnsBeforeRemoteDefault()
    {
        var s = new AppSettings { Mode = RoutingMode.Global, Rules = [new() { Kind = RuleKind.Domain, Value = "portal.corp.example", Target = RouteTarget.Direct }] };
        var config = SingBoxConfig.Build(s, Physical, Vless(), null, true);
        var dns = config["dns"]!;
        Assert.Equal("192.168.20.1", dns["servers"]![0]!["server"]!.ToString());
        Assert.Equal("Wi-Fi", dns["servers"]![0]!["bind_interface"]!.ToString());
        var rule = dns["rules"]!.AsArray().First(n => n?["domain_suffix"]?.ToJsonString().Contains("portal.corp.example") == true);
        Assert.Equal("dns-direct", rule!["server"]!.ToString());
        Assert.DoesNotContain("fakeip", config.ToJsonString());
        Assert.Equal("dns-vpn", dns["final"]!.ToString());
    }
    [Fact]
    public void SimultaneousVpnAndOpenVpnKeepSeparateInterfacesAndDns()
    {
        var s = new AppSettings { OpenVpnDomains = "office.example", LocalDomains = "wifi.example" };
        var link = new OpenVpnLink("NetCat-OpenVPN", 17, "10.42.0.2", "10.42.0.1", "10.42.0.53");
        var config = SingBoxConfig.Build(s, Physical, Vless(), link, true);
        Assert.Contains(config["outbounds"]!.AsArray(), n => n!["tag"]!.ToString() == "vpn");
        var output = config["outbounds"]!.AsArray().First(n => n!["tag"]!.ToString() == "openvpn")!;
        Assert.Equal("NetCat-OpenVPN", output["bind_interface"]!.ToString());
        Assert.Equal("10.42.0.2", output["inet4_bind_address"]!.ToString());
        var rules = config["route"]!["rules"]!.AsArray();
        var office = rules.First(n => n?["domain_suffix"]?.ToJsonString().Contains("office.example") == true)!;
        Assert.Equal("openvpn", office["outbound"]!.ToString());
        Assert.True(rules.IndexOf(office) < rules.IndexOf(rules.First(n => n?["ip_is_private"] != null)));
    }
    [Fact]
    public void OpenVpnDomainsFailClosedWhenDisconnected()
    {
        var config = SingBoxConfig.Build(new AppSettings { OpenVpnDomains = "office.example" }, Physical, Vless(), null, true);
        var route = config["route"]!["rules"]!.AsArray().First(n => n?["domain_suffix"]?.ToJsonString().Contains("office.example") == true)!;
        Assert.Equal("reject", route["action"]!.ToString());
        var dns = config["dns"]!["rules"]!.AsArray().First(n => n?["domain_suffix"]?.ToJsonString().Contains("office.example") == true)!;
        Assert.Equal("reject", dns["action"]!.ToString());
    }
    [Theory]
    [InlineData(ServiceRoute.Zapret, ServiceRoute.Vpn, "direct", "vpn")]
    [InlineData(ServiceRoute.Vpn, ServiceRoute.Zapret, "vpn", "direct")]
    [InlineData(ServiceRoute.Zapret, ServiceRoute.Zapret, "direct", "direct")]
    public void ServiceScenariosAreIndependent(ServiceRoute yt, ServiceRoute dc, string ytTarget, string dcTarget)
    {
        var config = SingBoxConfig.Build(new AppSettings { YouTube = yt, Discord = dc }, Physical, Vless(), null, false);
        var rules = config["route"]!["rules"]!.AsArray();
        Assert.Equal(ytTarget, rules.First(n => n?["domain_suffix"]?.ToJsonString().Contains("youtube.com") == true)!["outbound"]!.ToString());
        Assert.Equal(dcTarget, rules.First(n => n?["process_name"]?.ToJsonString().Contains("Discord.exe") == true)!["outbound"]!.ToString());
    }
    [Theory]
    [InlineData(false, "vpn")]
    [InlineData(true, "telegram")]
    public void TelegramUsesVpnUnlessExternalSocksEnabled(bool socks, string target)
    {
        var c = SingBoxConfig.Build(new AppSettings { TelegramSocks = socks }, Physical, Vless(), null, false);
        Assert.Equal(target, c["route"]!["rules"]!.AsArray().First(n => n?["process_name"] is JsonArray names && names.Any(name=>name?.ToString()=="Telegram.exe"))!["outbound"]!.ToString());
    }
    [Fact]
    public void StoppedZapretKeepsDirectRouteForBothServices()
    {
        var c = SingBoxConfig.Build(new AppSettings { YouTube = ServiceRoute.Zapret, Discord = ServiceRoute.Zapret }, Physical, Vless(), null, false, zapretRunning: false);
        foreach (var domain in new[] { "youtube.com", "discord.com" })
            Assert.Equal("direct", c["route"]!["rules"]!.AsArray().First(n => n?["domain_suffix"]?.ToJsonString().Contains(domain) == true)!["outbound"]!.ToString());
    }
    [Fact]
    public void DiscordAndYouTubeZapretBypassPrecedesVpnFallbackInAllModes()
    {
        var settings = new AppSettings { Mode = RoutingMode.Global, YouTube = ServiceRoute.Zapret, Discord = ServiceRoute.Zapret };
        var c = SingBoxConfig.Build(settings, Physical, Vless(), null, true);
        var route = c["route"]!;
        Assert.Equal("vpn", route["final"]!.ToString());
        var rules = route["rules"]!.AsArray();
        var ytDomain = rules.First(n => n?["domain_suffix"]?.ToJsonString().Contains("youtube.com") == true)!;
        var dcDomain = rules.First(n => n?["domain_suffix"]?.ToJsonString().Contains("discord.com") == true)!;
        var dcProcess = rules.First(n => n?["process_name"]?.ToJsonString().Contains("Discord.exe") == true)!;
        var dcVoice = rules.First(n => n?["network"]?.ToString() == "udp" && n?["port_range"]?.ToJsonString().Contains("19294:19344") == true)!;
        Assert.Equal("direct", ytDomain["outbound"]!.ToString());
        Assert.Equal("direct", dcDomain["outbound"]!.ToString());
        Assert.Equal("direct", dcProcess["outbound"]!.ToString());
        Assert.Equal("direct", dcVoice["outbound"]!.ToString());
        Assert.Contains("DiscordCanary.exe", dcProcess["process_name"]!.ToJsonString());
        Assert.Contains("Discord.exe", dcVoice["process_name"]!.ToJsonString());
    }
    [Fact]
    public void TunDirectOutboundAndRouteSettingsEnsureProcessFindingAndLoopPrevention()
    {
        var settings = new AppSettings { Mode = RoutingMode.Global, YouTube = ServiceRoute.Zapret, Discord = ServiceRoute.Zapret };
        var c = SingBoxConfig.Build(settings, Physical, Vless(), null, true);
        var route = c["route"]!;
        Assert.True(route["find_process"]?.GetValue<bool>());
        Assert.Null(route["auto_detect_interface"]);
        Assert.Equal(Physical.Name, route["default_interface"]!.ToString());
        var direct = c["outbounds"]!.AsArray().First(n => n?["tag"]?.ToString() == "direct")!;
        Assert.Equal(Physical.Name, direct["bind_interface"]!.ToString());
        Assert.Equal(Physical.Address, direct["inet4_bind_address"]!.ToString());
        var tun = c["inbounds"]!.AsArray().First(n => n?["type"]?.ToString() == "tun")!;
        var addresses = tun["address"]!.AsArray().Select(a => a!.ToString()).ToArray();
        Assert.Single(addresses);
        Assert.Equal("172.29.255.1/30", addresses[0]);
        Assert.DoesNotContain(addresses, a => a.Contains(':'));
    }
    [Fact]
    public void AutoDetectInterfaceUsedOnlyWhenPhysicalNameIsEmpty()
    {
        var anonymousPhysical = new NetworkSnapshot("", 1, "192.168.1.5", "192.168.1.1", []);
        var c = SingBoxConfig.Build(new AppSettings { Mode = RoutingMode.Global }, anonymousPhysical, Vless(), null, true);
        Assert.True(c["route"]!["auto_detect_interface"]?.GetValue<bool>());
        Assert.Null(c["route"]!["default_interface"]);
    }
    [Fact]
    public void ConditionalIpv6KeepsIpv6WhenPhysicalHasUsableDefaultRoute()
    {
        var physicalWithV6 = new NetworkSnapshot("Wi-Fi", 5, "192.168.20.10", "192.168.20.1", ["corp.example"], HasIpv6DefaultRoute: true, Ipv6Address: "2001:db8::10");
        var c = SingBoxConfig.Build(new AppSettings { Mode = RoutingMode.Global, YouTube = ServiceRoute.Zapret, Discord = ServiceRoute.Zapret }, physicalWithV6, Vless(), null, true);
        var tun = c["inbounds"]!.AsArray().First(n => n?["type"]?.ToString() == "tun")!;
        var addresses = tun["address"]!.AsArray().Select(a => a!.ToString()).ToArray();
        Assert.Equal(2, addresses.Length);
        Assert.Contains("172.29.255.1/30", addresses);
        Assert.Contains("fdfe:dcba:1984::1/126", addresses);
        Assert.Equal("prefer_ipv4", c["dns"]!["strategy"]!.ToString());
        var ytDns = c["dns"]!["rules"]!.AsArray().First(n => n?["domain_suffix"]?.ToJsonString().Contains("youtube.com") == true)!;
        Assert.Null(ytDns["strategy"]);
    }
    [Fact]
    public void ConditionalIpv6ForcesIpv4OnlyOnDirectWhenPhysicalLacksIpv6DefaultRoute()
    {
        var physicalNoV6 = new NetworkSnapshot("Wi-Fi", 5, "192.168.20.10", "192.168.20.1", ["corp.example"], HasIpv6DefaultRoute: false);
        var c = SingBoxConfig.Build(new AppSettings { Mode = RoutingMode.Global, YouTube = ServiceRoute.Zapret, Discord = ServiceRoute.Zapret }, physicalNoV6, Vless(), null, true);
        var tun = c["inbounds"]!.AsArray().First(n => n?["type"]?.ToString() == "tun")!;
        var addresses = tun["address"]!.AsArray().Select(a => a!.ToString()).ToArray();
        Assert.Single(addresses);
        Assert.Equal("172.29.255.1/30", addresses[0]);
        // Top-level DNS in global mode remains prefer_ipv4 for VPN DNS, but direct DNS rules enforce ipv4_only
        Assert.Equal("prefer_ipv4", c["dns"]!["strategy"]!.ToString());
        var ytDns = c["dns"]!["rules"]!.AsArray().First(n => n?["domain_suffix"]?.ToJsonString().Contains("youtube.com") == true)!;
        Assert.Equal("ipv4_only", ytDns["strategy"]!.ToString());
        var dcDns = c["dns"]!["rules"]!.AsArray().First(n => n?["domain_suffix"]?.ToJsonString().Contains("discord.com") == true)!;
        Assert.Equal("ipv4_only", dcDns["strategy"]!.ToString());
    }
    [Fact]
    public void PhysicalNetworkCaptureDetectsUsableIpv6StatusCorrectly()
    {
        var snapshot = PhysicalNetwork.Capture("");
        Assert.NotNull(snapshot.Name);
        Assert.True(snapshot.Index > 0);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Address));
        Assert.False(snapshot.HasIpv6DefaultRoute);
    }
    [Fact]
    public void DefaultRouteExistsButNoGlobalIpv6AddressReturnsFalse()
    {
        var addresses = new[]
        {
            System.Net.IPAddress.Parse("fe80::d78c:eec3:e989:45d9"),
            System.Net.IPAddress.Parse("fdc9:6fdc:6dc1:0:c0f0:2d33:1ce:30d5"),
            System.Net.IPAddress.Parse("fdc9:6fdc:6dc1::f3a"),
            System.Net.IPAddress.Parse("192.168.1.105")
        };
        bool usable = PhysicalNetwork.EvaluateUsableIpv6(hasDefaultRoute: true, addresses, out var primaryAddress);
        Assert.False(usable);
        Assert.Empty(primaryAddress);

        var globalAddresses = addresses.Append(System.Net.IPAddress.Parse("2001:db8::1"));
        bool globalUsable = PhysicalNetwork.EvaluateUsableIpv6(hasDefaultRoute: true, globalAddresses, out var globalPrimary);
        Assert.True(globalUsable);
        Assert.Equal("2001:db8::1", globalPrimary);

        bool noRouteUsable = PhysicalNetwork.EvaluateUsableIpv6(hasDefaultRoute: false, globalAddresses, out _);
        Assert.False(noRouteUsable);
    }
    [Fact]
    public void VerifyFivePointRoutingMatrix()
    {
        var physical = new NetworkSnapshot("Ethernet", 24, "192.168.1.105", "192.168.1.1", [], HasIpv6DefaultRoute: false);
        var vless = Vless();

        // (1) VPN OFF + Zapret ON (YouTube + Discord -> Zapret)
        var s1 = new AppSettings { Mode = RoutingMode.Rules, YouTube = ServiceRoute.Zapret, Discord = ServiceRoute.Zapret };
        var c1 = SingBoxConfig.Build(s1, physical, null, null, false);
        Assert.DoesNotContain(c1["inbounds"]!.AsArray(), i => i?["type"]?.ToString() == "tun");
        var r1 = c1["route"]!["rules"]!.AsArray();
        Assert.Equal("direct", r1.First(n => n?["domain_suffix"]?.ToJsonString().Contains("youtube.com") == true)!["outbound"]!.ToString());
        Assert.Equal("direct", r1.First(n => n?["domain_suffix"]?.ToJsonString().Contains("discord.com") == true)!["outbound"]!.ToString());
        Assert.Equal("direct", r1.First(n => n?["process_name"]?.ToJsonString().Contains("Discord.exe") == true && n?["network"] == null)!["outbound"]!.ToString());
        Assert.Equal("direct", r1.First(n => n?["process_name"]?.ToJsonString().Contains("Discord.exe") == true && n?["network"]?.ToString() == "udp")!["outbound"]!.ToString());

        // (2) VPN ON + TUN OFF + Zapret ON (YouTube + Discord -> Zapret, rest via proxy/socks)
        var s2 = new AppSettings { Mode = RoutingMode.Global, YouTube = ServiceRoute.Zapret, Discord = ServiceRoute.Zapret };
        var c2 = SingBoxConfig.Build(s2, physical, vless, null, false);
        Assert.DoesNotContain(c2["inbounds"]!.AsArray(), i => i?["type"]?.ToString() == "tun");
        Assert.Contains(c2["outbounds"]!.AsArray(), o => o?["tag"]?.ToString() == "vpn");
        var r2 = c2["route"]!["rules"]!.AsArray();
        Assert.Equal("direct", r2.First(n => n?["domain_suffix"]?.ToJsonString().Contains("youtube.com") == true)!["outbound"]!.ToString());
        Assert.Equal("direct", r2.First(n => n?["domain_suffix"]?.ToJsonString().Contains("discord.com") == true)!["outbound"]!.ToString());
        Assert.Equal("direct", r2.First(n => n?["process_name"]?.ToJsonString().Contains("Discord.exe") == true && n?["network"]?.ToString() == "udp")!["outbound"]!.ToString());
        Assert.Equal("vpn", c2["route"]!["final"]!.ToString());

        // (3) VPN ON + TUN ON + Discord = VPN, YouTube = Zapret
        var s3 = new AppSettings { Mode = RoutingMode.Global, YouTube = ServiceRoute.Zapret, Discord = ServiceRoute.Vpn };
        var c3 = SingBoxConfig.Build(s3, physical, vless, null, true);
        var tun3 = c3["inbounds"]!.AsArray().First(i => i?["type"]?.ToString() == "tun")!;
        Assert.Single(tun3["address"]!.AsArray());
        Assert.Equal("172.29.255.1/30", tun3["address"]![0]!.ToString());
        var r3 = c3["route"]!["rules"]!.AsArray();
        Assert.Equal("direct", r3.First(n => n?["domain_suffix"]?.ToJsonString().Contains("youtube.com") == true)!["outbound"]!.ToString());
        Assert.Equal("vpn", r3.First(n => n?["domain_suffix"]?.ToJsonString().Contains("discord.com") == true)!["outbound"]!.ToString());
        Assert.Equal("vpn", r3.First(n => n?["process_name"]?.ToJsonString().Contains("Discord.exe") == true && n?["network"] == null)!["outbound"]!.ToString());
        Assert.Equal("vpn", r3.First(n => n?["process_name"]?.ToJsonString().Contains("Discord.exe") == true && n?["network"]?.ToString() == "udp")!["outbound"]!.ToString());

        // (4) VPN ON + TUN ON + Discord = Zapret, YouTube = Zapret
        var s4 = new AppSettings { Mode = RoutingMode.Global, YouTube = ServiceRoute.Zapret, Discord = ServiceRoute.Zapret };
        var c4 = SingBoxConfig.Build(s4, physical, vless, null, true);
        var tun4 = c4["inbounds"]!.AsArray().First(i => i?["type"]?.ToString() == "tun")!;
        Assert.Single(tun4["address"]!.AsArray());
        Assert.Equal("172.29.255.1/30", tun4["address"]![0]!.ToString());
        var r4 = c4["route"]!["rules"]!.AsArray();
        Assert.Equal("direct", r4.First(n => n?["domain_suffix"]?.ToJsonString().Contains("youtube.com") == true)!["outbound"]!.ToString());
        Assert.Equal("direct", r4.First(n => n?["domain_suffix"]?.ToJsonString().Contains("discord.com") == true)!["outbound"]!.ToString());
        Assert.Equal("direct", r4.First(n => n?["process_name"]?.ToJsonString().Contains("Discord.exe") == true && n?["network"] == null)!["outbound"]!.ToString());
        Assert.Equal("direct", r4.First(n => n?["process_name"]?.ToJsonString().Contains("Discord.exe") == true && n?["network"]?.ToString() == "udp")!["outbound"]!.ToString());
        Assert.Equal("vpn", c4["route"]!["final"]!.ToString());

        // (5) Discord Voice Scoped UDP check
        var dcVoiceRule = r4.First(n => n?["network"]?.ToString() == "udp" && n?["port_range"]?.ToJsonString().Contains("19294:19344") == true)!;
        var processes = dcVoiceRule["process_name"]!.AsArray().Select(p => p!.ToString()).ToArray();
        Assert.Contains("Discord.exe", processes);
        Assert.Contains("DiscordCanary.exe", processes);
        Assert.Contains("DiscordPTB.exe", processes);
        Assert.Contains("DiscordDevelopment.exe", processes);
        Assert.DoesNotContain("chrome.exe", processes);
        Assert.DoesNotContain("firefox.exe", processes);
    }
    [Fact]
    public void SubscriptionBase64ImportsActualProfiles()
    {
        var uri = "vless://00000000-0000-4000-8000-000000000001@a.example.com:443?security=tls#Alpha";
        var result = ProfileImporter.Parse(Convert.ToBase64String(Encoding.UTF8.GetBytes(uri + "\n" + uri.Replace("Alpha", "Beta"))));
        Assert.Empty(result.Errors); Assert.Equal(2, result.Profiles.Count); Assert.Equal("Beta", result.Profiles[1].Name);
    }
    [Fact] public void InvalidSubscriptionDoesNotCreateProfiles() => Assert.Empty(ProfileImporter.Parse("<html>Unauthorized</html>").Profiles);
    [Theory]
    [InlineData("client\ndev tun\nup malware.exe", true)]
    [InlineData("client\ndev tun\nremote vpn.example.com 1194", false)]
    public void OpenVpnRejectsExternalExecution(string text, bool reject)
    {
        if (reject) Assert.Throws<InvalidDataException>(() => OpenVpnConfiguration.Validate(text)); else OpenVpnConfiguration.Validate(text);
    }
    [Fact]
    public void OpenVpnDoesNotHijackDefaultRouteOrDns()
    {
        var prepared = OpenVpnConfiguration.Prepare("client\ndev tun\nremote vpn.example.com 1194\nredirect-gateway def1\ndhcp-option DNS 8.8.8.8\nroute 0.0.0.0 0.0.0.0\n<ca>\nCERTIFICATE\n</ca>");
        Assert.DoesNotContain("redirect-gateway def1", prepared); Assert.DoesNotContain("dhcp-option DNS", prepared);
        Assert.Contains("route-nopull", prepared); Assert.Contains("route-noexec", prepared); Assert.Contains("CERTIFICATE", prepared);
    }
    [Fact]
    public async Task OfficialSingBoxAcceptsAllGeneratedScenarios()
    {
        var root = FindRoot(); var exe = Path.Combine(root, "bin/sing-box/sing-box.exe"); Assert.True(File.Exists(exe), "Fetch official modules before integration tests.");
        var folder = Path.Combine(root, "artifacts", "validation"); Directory.CreateDirectory(folder);
        foreach (var yt in Enum.GetValues<ServiceRoute>()) foreach (var dc in Enum.GetValues<ServiceRoute>()) foreach (var openvpn in new[] { false, true })
        {
            var settings = new AppSettings { YouTube = yt, Discord = dc, OpenVpnDomains = "office.example", TelegramSocks = true, Rules = [new() { Kind = RuleKind.Domain, Value = "portal.example", Target = RouteTarget.Direct }] };
            var c = SingBoxConfig.Build(settings, Physical, Vless(), openvpn ? new("NetCat-OpenVPN", 17, "10.0.0.2", "10.0.0.1", "10.0.0.53") : null, true);
            var file = Path.Combine(folder, $"scenario-{yt}-{dc}-{openvpn}.json"); await File.WriteAllTextAsync(file, c.ToJsonString(JsonSettings.Options));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var result = await ProcessHost.RunAsync(exe, ["check", "-c", file], timeout.Token); Assert.True(result.Code == 0, result.Output);
        }
    }
    [Fact]
    public void FlowsealStrategiesAreRestrictedToScenarioWithoutExecutingBatch()
    {
        var root = FindRoot(); var zapret = Path.Combine(root, "bin", "zapret"); var file = Path.Combine(zapret, "general (ALT).bat");
        var youtube = ZapretArguments.Build(file, zapret, "scenario-hosts.txt", true, false);
        Assert.DoesNotContain(youtube, a => a.Contains("--filter-l7=discord")); Assert.Contains(youtube, a => a.StartsWith("--ipset="));
        Assert.Contains(youtube, a => a == "--hostlist=scenario-hosts.txt"); Assert.DoesNotContain(youtube, a => a.Contains("list-general.txt"));
        var discord = ZapretArguments.Build(file, zapret, "scenario-hosts.txt", false, true);
        Assert.Contains(discord, a => a.StartsWith("--filter-l7=discord"));
    }
    [Fact]
    public void AllFlowsealStrategiesReferenceExistingFiles()
    {
        var root=Path.Combine(FindRoot(),"bin","zapret");
        foreach(var file in Directory.GetFiles(root,"general*.bat")) foreach(var scenario in new[]{(true,false),(false,true),(true,true)})
        {
            var args=ZapretArguments.Build(file,root,"scenario-hosts.txt",scenario.Item1,scenario.Item2);
            foreach(var arg in args.Where(a=>a.Contains('=') && (a.EndsWith(".txt")||a.EndsWith(".bin"))))
            {
                var path=arg[(arg.IndexOf('=')+1)..]; if(path=="scenario-hosts.txt")continue;
                Assert.True(File.Exists(path),Path.GetFileName(file)+": "+path);
            }
        }
    }
    [Theory]
    [InlineData(0,1,1,255,0,0)]
    [InlineData(120,1,1,0,255,0)]
    [InlineData(240,1,1,0,0,255)]
    [InlineData(100,0,1,255,255,255)]
    [InlineData(180,1,0,0,0,0)]
    public void GradientColorConversion(double h,double s,double v,byte r,byte g,byte b) => Assert.Equal((r,g,b),ColorMath.FromHsv(h,s,v));
    [Fact]
    public void SubscriptionDoesNotMergeDifferentTransportsOnSameServer()
    {
        var link="vless://00000000-0000-4000-8000-000000000001@vpn.example.com:443?security=tls";
        var tcp=ProfileImporter.ParseLink(link+"&type=tcp"); var xhttp=ProfileImporter.ParseLink(link+"&type=xhttp&path=%2Fa"); var ws=ProfileImporter.ParseLink(link+"&type=ws&path=%2Fb");
        Assert.Equal(3,new[]{tcp,xhttp,ws}.Select(ProfileIdentity.Key).Distinct().Count());
        var renamed=JsonSettings.Clone(xhttp); renamed.Name="Локальное имя"; Assert.Equal(ProfileIdentity.Key(xhttp),ProfileIdentity.Key(renamed));
    }
    public static string FindRoot() { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d != null && !File.Exists(Path.Combine(d.FullName, "NetCat.sln"))) d = d.Parent; return d?.FullName ?? throw new DirectoryNotFoundException(); }
}
