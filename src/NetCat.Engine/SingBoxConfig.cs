using System.Text.Json.Nodes;
using NetCat.Core;

namespace NetCat.Engine;
public static class SingBoxConfig
{
    public static JsonArray Array(IEnumerable<string> values) => new(values.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    private static JsonObject Route(JsonObject match, string target) { match["action"] = target == "block" ? "reject" : "route"; if (target != "block") match["outbound"] = target; return match; }
    private static JsonObject Dns(JsonObject match, string server, string? strategy = null)
    {
        match["action"] = server == "block" ? "reject" : "route";
        if (server != "block") match["server"] = server;
        if (!string.IsNullOrWhiteSpace(strategy)) match["strategy"] = strategy;
        return match;
    }
    public static JsonObject Build(AppSettings s, NetworkSnapshot physical, Profile? main, OpenVpnLink? ovpn, bool tun, int? port = null, int? xrayPort = null, bool zapretRunning = true, int? latencyPort = null, int healthSourcePort = 0, string? geodataDirectory = null)
    {
        var outbound = new JsonArray(); var route = new JsonArray(); var dnsRules = new JsonArray();
        var directOutbound = new JsonObject { ["type"] = "direct", ["tag"] = "direct", ["domain_resolver"] = "dns-direct" };
        if (!string.IsNullOrWhiteSpace(physical.Name))
        {
            directOutbound["bind_interface"] = physical.Name;
            if (!string.IsNullOrWhiteSpace(physical.Address)) directOutbound["inet4_bind_address"] = physical.Address;
        }
        outbound.Add(directOutbound);
        if (main != null)
        {
            var proxy = xrayPort.HasValue ? new JsonObject { ["type"] = "socks", ["server"] = "127.0.0.1", ["server_port"] = xrayPort.Value } : JsonNode.Parse(main.OutboundJson)!.AsObject();
            var localNames = RuleValidation.Domains(s.LocalDomains).Concat(physical.Suffixes).Concat(["local","lan","home.arpa"]);
            var localServer = !main.Host.Contains('.') || localNames.Any(d => main.Host.Equals(d,StringComparison.OrdinalIgnoreCase) || main.Host.EndsWith("."+d,StringComparison.OrdinalIgnoreCase));
            proxy["tag"] = "vpn"; if (!xrayPort.HasValue) { proxy["server"] = main.Host; proxy["server_port"] = main.Port; proxy["bind_interface"] = physical.Name; proxy["domain_resolver"] = localServer ? "dns-direct" : "dns-bootstrap"; }
            outbound.Add(proxy);
        }
        if (ovpn != null) outbound.Add(new JsonObject { ["type"] = "direct", ["tag"] = "openvpn", ["bind_interface"] = ovpn.Name, ["inet4_bind_address"] = ovpn.Address, ["domain_resolver"] = "dns-openvpn" });
        if (s.TelegramSocks) outbound.Add(new JsonObject { ["type"] = "socks", ["tag"] = "telegram", ["server"] = s.TelegramSocksHost, ["server_port"] = s.TelegramSocksPort, ["domain_resolver"] = "dns-direct", ["bind_interface"] = physical.Name });
        string Target(RouteTarget t) => t switch { RouteTarget.Vpn => main == null ? "block" : "vpn", RouteTarget.OpenVpn => ovpn == null ? "block" : "openvpn", RouteTarget.Block => "block", _ => "direct" };
        string Server(string target) => target switch { "vpn" => "dns-vpn", "openvpn" => "dns-openvpn", "block" => "block", _ => "dns-direct" };
        var dnsServers = new JsonArray(new JsonObject { ["type"] = "udp", ["tag"] = "dns-direct", ["server"] = string.IsNullOrWhiteSpace(s.DirectDns) ? physical.Dns : s.DirectDns, ["bind_interface"] = physical.Name });
        // Public VPN endpoint names must be resolvable before the VPN exists. LAN names keep adapter DNS.
        if (main != null) dnsServers.Add(new JsonObject { ["type"] = "https", ["tag"] = "dns-bootstrap", ["server"] = "1.1.1.1", ["path"] = "/dns-query", ["bind_interface"] = physical.Name });
        if (main != null) dnsServers.Add(new JsonObject { ["type"] = "https", ["tag"] = "dns-vpn", ["server"] = "1.1.1.1", ["path"] = "/dns-query", ["detour"] = "vpn" });
        if (ovpn != null) dnsServers.Add(new JsonObject { ["type"] = "udp", ["tag"] = "dns-openvpn", ["server"] = string.IsNullOrWhiteSpace(s.OpenVpnDns) ? ovpn.Dns : s.OpenVpnDns, ["bind_interface"] = ovpn.Name });
        // A dedicated loopback inbound measures the active VPN even in selective/direct mode.
        // It precedes the NetCat process bypass and never changes routes for ordinary traffic.
        if (latencyPort.HasValue)
        {
            route.Add(Route(new JsonObject { ["inbound"] = Array(["latency"]) }, main == null ? "block" : "vpn"));
            if (main != null) dnsRules.Insert(0, Dns(new JsonObject { ["inbound"] = Array(["latency"]) }, "dns-vpn"));
        }
        if (tun && main != null && healthSourcePort > 0)
            route.Add(Route(new JsonObject { ["inbound"] = Array(["tun"]), ["source_ip_cidr"] = Array(["172.29.255.1/32"]), ["source_port"] = healthSourcePort, ["network"] = "tcp" }, "vpn"));
        route.Add(new JsonObject { ["action"] = "sniff" });
        route.Add(new JsonObject { ["port"] = 53, ["action"] = "hijack-dns" });
        // Core and OpenVPN transport sockets must never route back into the tunnel.
        route.Add(Route(new JsonObject { ["process_name"] = Array(["NetCat.exe", "sing-box.exe", "xray.exe", "openvpn.exe"]) }, "direct"));
        bool connectionSpecificRuleBefore = false;
        void AddDomains(IEnumerable<string> domains, string target)
        {
            var items = domains.Distinct().ToArray(); if (items.Length == 0) return;
            route.Add(Route(new JsonObject { ["domain_suffix"] = Array(items) }, target));
            if (!(target == "block" && connectionSpecificRuleBefore))
            {
                var dnsServer = Server(target);
                var domainStrategy = (!physical.HasIpv6DefaultRoute && dnsServer == "dns-direct") ? "ipv4_only" : null;
                dnsRules.Add(Dns(new JsonObject { ["domain_suffix"] = Array(items) }, dnsServer, domainStrategy));
            }
        }
        var databases = new Dictionary<RuleKind, Geodata>();
        JsonObject RuleMatch(RoutingRule r, bool dns = false)
        {
            if (r.Kind is not (RuleKind.GeoSite or RuleKind.GeoIp)) return Match(r, dns);
            if (geodataDirectory == null) throw new InvalidDataException("Не задана папка баз геоданных.");
            if (!databases.TryGetValue(r.Kind, out var database)) databases[r.Kind] = database = GeodataCache.Shared.Get(Geodata.FilePath(geodataDirectory, r.Kind), r.Kind == RuleKind.GeoIp);
            var match = database.Match(r.Value);
            if (!dns)
            {
                var filters = new JsonObject();
                if (r.Network.Length > 0) filters["network"] = r.Network;
                if (r.Port.Length > 0) filters["port"] = int.Parse(r.Port);
                if (filters.Count > 0 && match["invert"]?.GetValue<bool>() == true)
                    return new JsonObject { ["type"] = "logical", ["mode"] = "and", ["rules"] = new JsonArray(match, filters) };
                foreach (var filter in filters) match[filter.Key] = filter.Value?.DeepClone();
            }
            return match;
        }
        foreach (var r in s.Rules.Where(r => r.Enabled))
        {
            RuleValidation.Validate(r);
            var m = RuleMatch(r); route.Add(Route(m, Target(r.Target)));
            // A DNS lookup does not reliably identify the destination port/IP/application
            // of the later connection. Do not preempt an earlier exception by rejecting DNS.
            var connectionSpecific = r.Kind is RuleKind.Process or RuleKind.ExecutablePath or RuleKind.IpCidr or RuleKind.GeoIp || r.Network.Length > 0 || r.Port.Length > 0;
            if (r.Kind is RuleKind.Domain or RuleKind.ExactDomain or RuleKind.GeoSite && !connectionSpecific
                && !(Target(r.Target) == "block" && connectionSpecificRuleBefore))
            {
                var dnsServer = Server(Target(r.Target));
                var ruleStrategy = (!physical.HasIpv6DefaultRoute && dnsServer == "dns-direct") ? "ipv4_only" : null;
                dnsRules.Add(Dns(RuleMatch(r, true), dnsServer, ruleStrategy));
            }
            connectionSpecificRuleBefore |= connectionSpecific;
        }
        // User rules precede service defaults, including the WS worker and corporate domains.
        route.Add(Route(new JsonObject { ["process_name"] = Array(["NetCat.Telegram.exe", "TgWsProxy_windows.exe"]) }, "direct"));
        AddDomains(RuleValidation.Domains(s.OpenVpnDomains), Target(RouteTarget.OpenVpn));
        AddDomains(RuleValidation.Domains(s.LocalDomains).Concat(physical.Suffixes).Concat(["local", "lan", "localdomain", "home.arpa"]), "direct");
        route.Add(Route(new JsonObject { ["domain_regex"] = Array(["^[^.]+$"]) }, "direct"));
        dnsRules.Add(Dns(new JsonObject { ["domain_regex"] = Array(["^[^.]+$"]) }, "dns-direct", physical.HasIpv6DefaultRoute ? null : "ipv4_only"));
        // Explicit service choices apply in BOTH modes; Global only changes the fallback.
        AddDomains(ServiceDomains.YouTube, s.YouTube == ServiceRoute.Zapret ? "direct" : Target(RouteTarget.Vpn));
        AddDomains(ServiceDomains.Discord, s.Discord == ServiceRoute.Zapret ? "direct" : Target(RouteTarget.Vpn));
        var discordTarget = s.Discord == ServiceRoute.Zapret ? "direct" : Target(RouteTarget.Vpn);
        var discordProcesses = new[] { "Discord.exe", "DiscordCanary.exe", "DiscordPTB.exe", "DiscordDevelopment.exe" };
        route.Add(Route(new JsonObject { ["process_name"] = Array(discordProcesses) }, discordTarget));
        route.Add(Route(new JsonObject { ["process_name"] = Array(discordProcesses), ["network"] = "udp", ["port_range"] = Array(["19294:19344", "50000:50100"]) }, discordTarget));
        var telegram = s.TelegramSocks ? "telegram" : s.TelegramVpnDefault ? Target(RouteTarget.Vpn) : "direct";
        route.Add(Route(new JsonObject { ["process_name"] = Array(["Telegram.exe"]) }, telegram));
        route.Add(Route(new JsonObject { ["ip_cidr"] = Array(ServiceDomains.TelegramIps) }, telegram));
        AddDomains(ServiceDomains.Telegram, telegram);
        route.Add(Route(new JsonObject { ["ip_is_private"] = true }, "direct"));
        // Resolve direct domains through adapter DNS BEFORE remote default. No FakeIP for LAN.
        var fallback = s.Mode is RoutingMode.Global or RoutingMode.SelectiveDirect ? Target(RouteTarget.Vpn) : "direct";
        if (fallback == "block") route.Add(new JsonObject { ["action"] = "reject" });
        var inbounds = new JsonArray(new JsonObject { ["type"] = "mixed", ["tag"] = "local", ["listen"] = "127.0.0.1", ["listen_port"] = port ?? s.SocksPort });
        if (latencyPort.HasValue) inbounds.Add(new JsonObject { ["type"] = "mixed", ["tag"] = "latency", ["listen"] = "127.0.0.1", ["listen_port"] = latencyPort.Value });
        var tunAddresses = physical.HasIpv6DefaultRoute
            ? new[] { "172.29.255.1/30", "fdfe:dcba:1984::1/126" }
            : new[] { "172.29.255.1/30" };
        if (tun) inbounds.Add(new JsonObject { ["type"] = "tun", ["tag"] = "tun", ["interface_name"] = "NetCat-TUN", ["address"] = Array(tunAddresses), ["auto_route"] = true, ["strict_route"] = false, ["stack"] = "mixed", ["udp_mapping"] = "address_and_port_dependent", ["mtu"] = 1400 });
        var dnsStrategy = physical.HasIpv6DefaultRoute ? "prefer_ipv4" : (fallback == "vpn" ? "prefer_ipv4" : "ipv4_only");
        var routeConfig = new JsonObject
        {
            ["rules"] = route,
            ["final"] = fallback == "block" ? "direct" : fallback,
            ["find_process"] = true,
            ["default_domain_resolver"] = "dns-direct"
        };
        if (!string.IsNullOrWhiteSpace(physical.Name)) routeConfig["default_interface"] = physical.Name;
        else routeConfig["auto_detect_interface"] = true;
        return new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true },
            ["dns"] = new JsonObject { ["servers"] = dnsServers, ["rules"] = dnsRules, ["final"] = fallback == "vpn" ? "dns-vpn" : "dns-direct", ["strategy"] = dnsStrategy, ["reverse_mapping"] = true },
            ["inbounds"] = inbounds, ["outbounds"] = outbound,
            ["route"] = routeConfig
        };
    }
    private static JsonObject Match(RoutingRule r, bool dns = false)
    {
        var key = r.Kind switch { RuleKind.Domain => "domain_suffix", RuleKind.ExactDomain => "domain", RuleKind.Process => "process_name", RuleKind.ExecutablePath => "process_path", _ => "ip_cidr" };
        var result = new JsonObject { [key] = Array([r.Value]) };
        if (!dns) { if (r.Network.Length > 0) result["network"] = r.Network; if (r.Port.Length > 0) result["port"] = int.Parse(r.Port); }
        return result;
    }
}
