using System.Net;
using System.Runtime.InteropServices;

namespace NetCat.Network;

public sealed record RouteRow(
    int InterfaceIndex,
    string DestinationPrefix,
    string NextHop,
    uint RouteMetric,
    string Protocol,
    string Origin)
{
    public override string ToString() =>
        $"InterfaceIndex={InterfaceIndex} DestinationPrefix={DestinationPrefix} NextHop={NextHop} RouteMetric={RouteMetric} Protocol={Protocol} Origin={Origin}";
}

public sealed record RouteDiff(
    IReadOnlyList<RouteRow> Added,
    IReadOnlyList<RouteRow> Removed)
{
    public override string ToString() =>
        $"added=[{string.Join("; ", Added)}] removed=[{string.Join("; ", Removed)}]";
}

public enum RouteObservationStatus
{
    Verified,
    Missing,
    Unknown,
    Error
}

public sealed record RouteObservationResult(
    RouteObservationStatus Status,
    IReadOnlyList<RouteRow> Routes,
    string? Error = null,
    IReadOnlyList<string>? MissingRoutes = null
)
{
    public bool IsVerified => Status == RouteObservationStatus.Verified;
}

public static class RouteTable
{
    public static RouteObservationResult VerifyOpenVpn(NetCat.Core.OpenVpnLink link, IReadOnlyList<RouteRow> rows)
    {
        var missing = link.LearnedRoutes.Where(p => !rows.Any(r => OpenVpnRouteJournal.Matches(r, link, p, OpenVpnRouteJournal.OwnedMetric))).ToList();
        missing.AddRange(OpenVpnRouteJournal.RequiredDnsPrefixes(link).Where(p=>!rows.Any(r=>OpenVpnRouteJournal.Matches(r,link,p,OpenVpnRouteJournal.OwnedMetric))));
        return new(missing.Count == 0 ? RouteObservationStatus.Verified : RouteObservationStatus.Missing, rows, MissingRoutes: missing);
    }
    [DllImport("iphlpapi.dll")]
    private static extern int GetIpForwardTable2(ushort family, out nint table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(nint table);

    public const ushort AF_INET = 2;
    public const ushort AF_INET6 = 23;

    public static RouteObservationResult VerifyRoutes(int ifIndex, IReadOnlyList<string> requiredRoutes)
    {
        if (requiredRoutes == null || requiredRoutes.Count == 0)
        {
            return new RouteObservationResult(RouteObservationStatus.Verified, Array.Empty<RouteRow>());
        }

        if (!OperatingSystem.IsWindows())
        {
            return new RouteObservationResult(RouteObservationStatus.Unknown, Array.Empty<RouteRow>(), "Non-Windows platform");
        }

        int err = GetIpForwardTable2(AF_INET, out nint table);
        if (err != 0 || table == 0)
        {
            return new RouteObservationResult(RouteObservationStatus.Error, Array.Empty<RouteRow>(), $"GetIpForwardTable2 returned error code {err}");
        }

        try
        {
            var rows = ReadTableRows(table);
            if (rows.Count == 0)
            {
                return new RouteObservationResult(RouteObservationStatus.Unknown, Array.Empty<RouteRow>(), "Route table empty or unverified");
            }

            var set = new HashSet<string>(rows.Where(r => r.InterfaceIndex == ifIndex).Select(r => r.DestinationPrefix), StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            foreach (var route in requiredRoutes)
            {
                var prefix = route.Trim();
                if (!prefix.Contains('/')) prefix += "/32";
                if (!set.Contains(prefix)) missing.Add(prefix);
            }

            if (missing.Count > 0)
            {
                return new RouteObservationResult(RouteObservationStatus.Missing, rows, MissingRoutes: missing);
            }

            return new RouteObservationResult(RouteObservationStatus.Verified, rows);
        }
        catch (Exception ex)
        {
            return new RouteObservationResult(RouteObservationStatus.Error, Array.Empty<RouteRow>(), ex.Message);
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    public static IReadOnlyList<RouteRow> CaptureIpv4()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<RouteRow>();

        int err = GetIpForwardTable2(AF_INET, out nint table);
        if (err != 0 || table == 0) return Array.Empty<RouteRow>();

        try
        {
            return ReadTableRows(table);
        }
        catch
        {
            return Array.Empty<RouteRow>();
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    private static List<RouteRow> ReadTableRows(nint table)
    {
        int count = Marshal.ReadInt32(table);
        var list = new List<RouteRow>(count);
        nint row = table + 8;
        const int rowSize = 104;

        for (int i = 0; i < count; i++)
        {
            int ifIndex = Marshal.ReadInt32(row + 8);
            short destFamily = Marshal.ReadInt16(row + 12);
            if (destFamily == AF_INET)
            {
                byte b1 = Marshal.ReadByte(row + 16);
                byte b2 = Marshal.ReadByte(row + 17);
                byte b3 = Marshal.ReadByte(row + 18);
                byte b4 = Marshal.ReadByte(row + 19);
                var destIp = $"{b1}.{b2}.{b3}.{b4}";
                byte prefixLength = Marshal.ReadByte(row + 40);
                string destPrefix = $"{destIp}/{prefixLength}";

                short hopFamily = Marshal.ReadInt16(row + 44);
                string nextHop = "0.0.0.0";
                if (hopFamily == AF_INET)
                {
                    byte h1 = Marshal.ReadByte(row + 48);
                    byte h2 = Marshal.ReadByte(row + 49);
                    byte h3 = Marshal.ReadByte(row + 50);
                    byte h4 = Marshal.ReadByte(row + 51);
                    nextHop = $"{h1}.{h2}.{h3}.{h4}";
                }

                uint metric = unchecked((uint)Marshal.ReadInt32(row + 84));
                int protocolCode = Marshal.ReadInt32(row + 88);
                int originCode = Marshal.ReadInt32(row + 100);

                list.Add(new RouteRow(
                    ifIndex,
                    destPrefix,
                    nextHop,
                    metric,
                    RouteProtocolName(protocolCode),
                    RouteOriginName(originCode)));
            }

            row += rowSize;
        }

        return list;
    }

    public static string RouteProtocolName(int protocol) => protocol switch
    {
        1 => "Other",
        2 => "Local",
        3 => "NetMgmt",
        4 => "Icmp",
        5 => "Egp",
        6 => "Ggp",
        7 => "Hello",
        8 => "Rip",
        9 => "IsIs",
        10 => "EsIs",
        11 => "Cisco",
        12 => "Bbn",
        13 => "Ospf",
        14 => "Bgp",
        15 => "Idpr",
        16 => "Eigrp",
        17 => "Dvmrp",
        18 => "Rpl",
        19 => "Dhcp",
        _ => protocol.ToString()
    };

    public static string RouteOriginName(int origin) => origin switch
    {
        0 => "Manual",
        1 => "WellKnown",
        2 => "Dhcp",
        3 => "RouterAdvertisement",
        4 => "6to4",
        _ => origin.ToString()
    };

    public static RouteDiff ComputeDiff(IEnumerable<RouteRow> before, IEnumerable<RouteRow> after)
    {
        var beforeList = before.ToList();
        var afterList = after.ToList();

        var added = afterList
            .Where(a => !beforeList.Any(b => b.InterfaceIndex == a.InterfaceIndex && b.DestinationPrefix == a.DestinationPrefix && b.NextHop == a.NextHop && b.RouteMetric == a.RouteMetric))
            .ToList();

        var removed = beforeList
            .Where(b => !afterList.Any(a => a.InterfaceIndex == b.InterfaceIndex && a.DestinationPrefix == b.DestinationPrefix && a.NextHop == b.NextHop && a.RouteMetric == b.RouteMetric))
            .ToList();

        return new RouteDiff(added, removed);
    }

    public static bool IsOpenVpnDefaultTakeover(IEnumerable<RouteRow> routesOnInterface, out List<RouteRow> offendingRoutes)
    {
        offendingRoutes = new List<RouteRow>();
        var routes = routesOnInterface.ToList();

        // 1. 0.0.0.0/0 on the OpenVPN interface
        var zeroRoute = routes.FirstOrDefault(r => r.DestinationPrefix == "0.0.0.0/0");
        if (zeroRoute != null)
        {
            offendingRoutes.Add(zeroRoute);
            return true;
        }

        // 2. Complete redirect-gateway def1 pair: 0.0.0.0/1 AND 128.0.0.0/1
        var def1Lower = routes.FirstOrDefault(r => r.DestinationPrefix == "0.0.0.0/1");
        var def1Upper = routes.FirstOrDefault(r => r.DestinationPrefix == "128.0.0.0/1");
        if (def1Lower != null && def1Upper != null)
        {
            offendingRoutes.Add(def1Lower);
            offendingRoutes.Add(def1Upper);
            return true;
        }

        return false;
    }
}
