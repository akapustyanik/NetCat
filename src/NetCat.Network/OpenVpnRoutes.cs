using System.Net;

namespace NetCat.Network;

public static class OpenVpnRoutes
{
    public static bool IsValidNetmask(string maskStr)
    {
        if (!IPAddress.TryParse(maskStr, out var mask)) return false;
        return OpenVpnService.TryGetContiguousCidr(mask, out _);
    }

    public static bool ValidatePushedRoute(string cidrPrefix, bool allowPublic)
    {
        if (string.IsNullOrWhiteSpace(cidrPrefix)) return false;
        var parts = cidrPrefix.Split('/');
        if (parts.Length != 2) return false;
        if (!IPAddress.TryParse(parts[0], out var ip)) return false;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || ip.ToString() != parts[0]) return false;
        if (!int.TryParse(parts[1], out var cidr) || cidr < 0 || cidr > 32) return false;
        uint address = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        uint mask = cidr == 0 ? 0 : uint.MaxValue << (32 - cidr);
        if ((address & mask) != address) return false;

        // Default routes are rejected: 0.0.0.0 or 128.0.0.0/1
        if (parts[0] == "0.0.0.0" || (parts[0] == "128.0.0.0" && cidr == 1)) return false;

        if (OpenVpnService.IsPrefixContainedInAllowedRanges(ip, cidr))
            return true;

        if (allowPublic)
            return true;

        return false;
    }
}
