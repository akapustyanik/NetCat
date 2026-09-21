using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using NetCat.Core;
using NetCat.Engine;

namespace NetCat.Network;
public static class PhysicalNetwork
{
    public static NetworkInterface[] Adapters() => NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 && !new[] { "vpn", "wintun", "tap-", "wireguard", "netcat", "tailscale", "hyper-v" }.Any(s => (n.Name + " " + n.Description).Contains(s, StringComparison.OrdinalIgnoreCase))).ToArray();

    public static bool HasGlobalUnicastIpv6(IEnumerable<IPAddress> addresses, out string primaryAddress)
    {
        primaryAddress = "";
        foreach (var a in addresses)
        {
            if (a.AddressFamily == AddressFamily.InterNetworkV6 &&
                !a.IsIPv6LinkLocal &&
                !a.IsIPv6SiteLocal &&
                !a.IsIPv6Multicast &&
                (a.GetAddressBytes()[0] & 0xfe) != 0xfc) // not ULA (fc00::/7)
            {
                primaryAddress = a.ToString();
                return true;
            }
        }
        return false;
    }

    public static bool EvaluateUsableIpv6(bool hasDefaultRoute, IEnumerable<IPAddress> addresses, out string primaryAddress)
    {
        bool hasGlobal = HasGlobalUnicastIpv6(addresses, out primaryAddress);
        return hasDefaultRoute && hasGlobal;
    }

    public static NetworkSnapshot Capture(string preferred = "")
    {
        preferred ??= "";
        var adapters = Adapters();
        NetworkInterface? adapter;
        if (preferred.Length > 0) adapter = adapters.FirstOrDefault(n => n.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        else
        {
            var metrics = DefaultRoutes.Metrics();
            var selected = DefaultRoutes.Select(adapters.Where(n => n.Supports(NetworkInterfaceComponent.IPv4)).Select(n => n.GetIPProperties().GetIPv4Properties().Index), metrics);
            adapter = adapters.FirstOrDefault(n => n.Supports(NetworkInterfaceComponent.IPv4) && n.GetIPProperties().GetIPv4Properties().Index == selected);
        }
        if (adapter == null) throw new InvalidOperationException(preferred.Length > 0 ? "Выбранный физический адаптер недоступен." : "Нет физического IPv4-маршрута по умолчанию. Выберите адаптер вручную.");
        var props = adapter.GetIPProperties();
        var ip = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? throw new InvalidOperationException("У адаптера нет IPv4.");
        var dns = props.DnsAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a))?.ToString() ?? throw new InvalidOperationException("У физического адаптера нет IPv4 DNS. Укажите DNS в настройках сети.");
        var suffixes = new[] { props.DnsSuffix, IPGlobalProperties.GetIPGlobalProperties().DomainName }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        int ifIndex = props.GetIPv4Properties().Index;
        bool hasV6Route = DefaultRoutes.HasIpv6DefaultRoute(ifIndex);
        bool usableV6 = EvaluateUsableIpv6(hasV6Route, props.UnicastAddresses.Select(a => a.Address), out var v6Unicast);
        return new(adapter.Name, ifIndex, ip, dns, suffixes, usableV6, v6Unicast);
    }
    public static Task<(int Code, string Output)> PowerShell(string script, CancellationToken ct = default) => ProcessHost.RunAsync(ProcessHost.PowerShellPath, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $ErrorActionPreference='Stop'; " + script))], ct);
    public static string Literal(string text) => "'" + text.Replace("'", "''") + "'";
}
