using System.Globalization;
using System.Net;

namespace NetCat.Core;
public static class RuleValidation
{
    public static string Domain(string value)
    {
        value = value.Trim();
        if (value.StartsWith("domain:", StringComparison.OrdinalIgnoreCase)) value = value[7..];
        if (value.Contains("://") && Uri.TryCreate(value, UriKind.Absolute, out var u)) value = u.Host;
        value = value.TrimStart('*', '.').TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value) || Uri.CheckHostName(value) == UriHostNameType.Unknown) throw new FormatException("Некорректный домен.");
        return new IdnMapping().GetAscii(value);
    }
    public static string[] Domains(string value) => value.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(Domain).Distinct().ToArray();
    public static bool MatchesCidr(string ip, string cidr)
    {
        var parts = cidr.Split('/');
        if (!IPAddress.TryParse(ip, out var address) || !IPAddress.TryParse(parts[0], out var subnet)) return false;
        var a = address.GetAddressBytes(); var b = subnet.GetAddressBytes();
        if (a.Length != b.Length) return false;
        var bits = parts.Length == 1 ? a.Length * 8 : int.TryParse(parts[1], out var n) ? n : -1;
        if (bits < 0 || bits > a.Length * 8) return false;
        for (int i = 0; i < a.Length; i++) { var count = Math.Clamp(bits - i * 8, 0, 8); var mask = (byte)(255 << (8 - count)); if ((a[i] & mask) != (b[i] & mask)) return false; }
        return true;
    }
    public static void Validate(RoutingRule r)
    {
        r.Value = r.Value.Trim();
        if (r.Value.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase) && r.Kind is RuleKind.Domain or RuleKind.GeoSite) { r.Kind = RuleKind.GeoSite; r.Value = r.Value[8..]; }
        if (r.Value.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase) && r.Kind is RuleKind.Domain or RuleKind.IpCidr or RuleKind.GeoIp) { r.Kind = RuleKind.GeoIp; r.Value = r.Value[6..]; }
        if (r.Kind is RuleKind.GeoSite or RuleKind.GeoIp)
        {
            r.Value = r.Value.ToLowerInvariant();
            var pattern = r.Kind == RuleKind.GeoSite ? @"^[a-z0-9][a-z0-9!_-]*(?:@[a-z0-9!_-]+)*$" : @"^[a-z0-9][a-z0-9_-]*$";
            if (!System.Text.RegularExpressions.Regex.IsMatch(r.Value, pattern)) throw new FormatException("Укажите категорию геоданных, например category-ru, youtube или ru.");
        }
        if (r.Kind is RuleKind.Domain or RuleKind.ExactDomain) r.Value = Domain(r.Value);
        if (r.Value.Length == 0) throw new FormatException("Пустое условие правила.");
        if (r.Kind == RuleKind.IpCidr && !MatchesCidr(r.Value.Split('/')[0], r.Value)) throw new FormatException("Некорректная подсеть.");
        if (r.Network is not ("" or "tcp" or "udp")) throw new FormatException("Транспорт должен быть tcp или udp.");
        if (r.Port.Length > 0 && (!int.TryParse(r.Port, out var p) || p is < 1 or > 65535)) throw new FormatException("Порт: 1–65535.");
    }
}
