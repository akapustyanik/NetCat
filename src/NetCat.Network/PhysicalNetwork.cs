using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using NetCat.Core;
using NetCat.Engine;

namespace NetCat.Network;
public static class PhysicalNetwork
{
    public static Func<bool> IsHyperVGuestDetector { get; set; } = DetectHyperVGuest;

    public static bool DetectHyperVGuest()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters");
                if (key != null) return true;

                using var biosKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                if (biosKey != null)
                {
                    var prod = biosKey.GetValue("SystemProductName")?.ToString();
                    var mfg = biosKey.GetValue("SystemManufacturer")?.ToString();
                    if (string.Equals(prod, "Virtual Machine", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(mfg, "Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch { }
        return false;
    }

    public static bool IsVirtualAdapter(string name, string description, bool? isHyperVGuest = null)
    {
        var identity = name + " " + description;
        if (new[] { "vpn", "wintun", "tap-", "wireguard", "netcat", "tailscale", "radmin" }
            .Any(marker => identity.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (identity.Contains("hyper-v", StringComparison.OrdinalIgnoreCase))
        {
            bool inGuest = isHyperVGuest ?? IsHyperVGuestDetector();
            if (inGuest &&
                description.Contains("Hyper-V Network Adapter", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }

        return false;
    }

    public static bool IsEligibleUplink(
        string name,
        string description,
        NetworkInterfaceType type,
        OperationalStatus status,
        bool hasUsableIpv4,
        bool hasDefaultGateway,
        bool? isHyperVGuest = null)
    {
        if (status != OperationalStatus.Up) return false;
        if (type is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)) return false;
        if (IsVirtualAdapter(name, description, isHyperVGuest)) return false;
        if (!hasUsableIpv4) return false;
        if (!hasDefaultGateway) return false;
        return true;
    }

    public static NetworkInterface[] Adapters()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
                        !IsVirtualAdapter(n.Name, n.Description))
            .ToArray();
    }

    public static bool HasGlobalUnicastIpv6(IEnumerable<IPAddress> addresses, out string primaryAddress)
    {
        primaryAddress = "";
        foreach (var a in addresses)
        {
            if (a.AddressFamily == AddressFamily.InterNetworkV6 &&
                !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.IPv6Any) &&
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

    public static bool IsUsablePhysicalBinding(NetworkSnapshot? net) =>
        net != null &&
        net.Index > 0 &&
        !string.IsNullOrWhiteSpace(net.Address) &&
        net.Address != "0.0.0.0" &&
        net.Address != "127.0.0.1";

    public static bool SameBinding(NetworkSnapshot? a, NetworkSnapshot? b)
    {
        if (a == null || b == null) return a == null && b == null;
        return a.Name == b.Name && a.Index == b.Index && a.Address == b.Address && a.Dns == b.Dns &&
               a.HasIpv6DefaultRoute == b.HasIpv6DefaultRoute && a.Ipv6Address == b.Ipv6Address &&
               a.Suffixes.SequenceEqual(b.Suffixes) && a.DefaultRoute == b.DefaultRoute;
    }
    public static bool HasPhysicalChanged(NetworkSnapshot? active, NetworkSnapshot? current, bool captureFailed) =>
        captureFailed ? active != null : !SameBinding(active, current);
    public static NetworkSnapshot ResolveCurrentBinding(string preferred = "", NetworkSnapshot? active = null) => Capture(preferred, active);
    public static NetworkSnapshot? TryCapture(string preferred = "", NetworkSnapshot? active = null)
    {
        try { return Capture(preferred, active); }
        catch { return null; }
    }

    private static long physicalGeneration;
    public static long PhysicalGeneration => Interlocked.Read(ref physicalGeneration);
    public static long NextGeneration() => Interlocked.Increment(ref physicalGeneration);

    public static int SelectInterfaceIndex(IEnumerable<int> candidates, IReadOnlyDictionary<int, uint> metrics, int activeIndex = 0)
    {
        var available = candidates.Where(metrics.ContainsKey).Distinct().ToArray();
        if (activeIndex > 0 && available.Contains(activeIndex)) return activeIndex;
        return available.OrderBy(index => metrics[index]).ThenBy(index => index).DefaultIfEmpty(-1).First();
    }

    public static NetworkSnapshot Capture(string preferred = "", NetworkSnapshot? active = null, bool allowFallback = true)
    {
        preferred ??= "";
        var adapters = Adapters();
        var metrics = DefaultRoutes.Metrics();
        var candidates = adapters.Where(n => n.Supports(NetworkInterfaceComponent.IPv4) &&
                                             metrics.ContainsKey(n.GetIPProperties().GetIPv4Properties().Index) &&
                                             n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.Any.Equals(g.Address))).ToList();

        NetworkInterface? adapter = null;
        if (preferred.Length > 0)
        {
            adapter = candidates.FirstOrDefault(n => n.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        }

        if (adapter == null && !allowFallback && preferred.Length > 0)
        {
            throw new InvalidOperationException($"Указанный сетевой адаптер «{preferred}» не найден среди доступных физических интерфейсов.");
        }

        if (adapter == null && active != null)
        {
            adapter = candidates.FirstOrDefault(n => n.Name == active.Name && n.GetIPProperties().GetIPv4Properties().Index == active.Index);
        }

        if (adapter == null)
        {
            var selected = SelectInterfaceIndex(candidates.Select(n => n.GetIPProperties().GetIPv4Properties().Index), metrics);
            adapter = candidates.FirstOrDefault(n => n.GetIPProperties().GetIPv4Properties().Index == selected);
        }

        if (adapter == null) throw new InvalidOperationException("Нет доступного физического IPv4-маршрута по умолчанию.");
        var props = adapter.GetIPProperties();
        var ip = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.") && a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred)?.Address.ToString() ?? throw new InvalidOperationException("У адаптера нет IPv4.");
        var dns = props.DnsAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a))?.ToString() ?? throw new InvalidOperationException("У физического адаптера нет IPv4 DNS. Укажите DNS в настройках сети.");
        var suffixes = new[] { props.DnsSuffix, IPGlobalProperties.GetIPGlobalProperties().DomainName }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        int ifIndex = props.GetIPv4Properties().Index;
        bool hasV6Route = DefaultRoutes.HasIpv6DefaultRoute(ifIndex);
        bool usableV6 = EvaluateUsableIpv6(hasV6Route, props.UnicastAddresses.Select(a => a.Address), out var v6Unicast);
        return new(adapter.Name, ifIndex, ip, dns, suffixes, usableV6, v6Unicast, string.Join(",", props.GatewayAddresses.Select(g => g.Address.ToString())));
    }
    public static NetworkSnapshot CaptureExact(string preferred)
    {
        if (string.IsNullOrWhiteSpace(preferred))
            throw new ArgumentException("Имя сетевого адаптера не может быть пустым.", nameof(preferred));
        return Capture(preferred, allowFallback: false);
    }
    public static Task<(int Code, string Output)> PowerShell(string script, CancellationToken ct = default) => ProcessHost.RunAsync(ProcessHost.PowerShellPath, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $ErrorActionPreference='Stop'; " + script))], ct);
    public static string Literal(string text) => "'" + text.Replace("'", "''") + "'";
}

public sealed class SystemPhysicalNetworkProvider : IPhysicalNetworkProvider
{
    public static readonly SystemPhysicalNetworkProvider Instance = new();
    public NetworkSnapshot? ResolveCurrentBinding(string preferred = "") => PhysicalNetwork.TryCapture(preferred);
    public NetworkSnapshot? ResolveCurrentBinding(string preferred, NetworkSnapshot? active) => PhysicalNetwork.TryCapture(preferred, active);
    public bool IsUsable(NetworkSnapshot? snapshot) => PhysicalNetwork.IsUsablePhysicalBinding(snapshot);
    public bool HasChanged(NetworkSnapshot? active, NetworkSnapshot? current, bool captureFailed) => PhysicalNetwork.HasPhysicalChanged(active, current, captureFailed);
}
