using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetCat.Core;

public static class ProfileSecurity
{
    public static bool CertificateValidationDisabled(Profile profile)
    {
        if (profile.IsOpenVpn) return false;
        try { return Disabled(JsonNode.Parse(profile.OutboundJson)); }
        catch (System.Text.Json.JsonException) { return false; }
    }
    private static bool Disabled(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Any(p =>
            (p.Key is "insecure" or "allowInsecure" or "skip-cert-verify" or "skipCertVerify") &&
                (p.Value?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true || p.Value?.ToString() == "1") || Disabled(p.Value)),
        JsonArray array => array.Any(Disabled),
        _ => false
    };

    // Only active directives count. A certificate body or comment cannot supply
    // a server-authentication policy. The last repeated directive is effective.
    public static bool HasOpenVpnServerAuthentication(string config)
    {
        string? block = null;
        var options = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var line in config.Replace("\r", "").Split('\n'))
        {
            var text = line.Trim();
            if (block != null) { if (text == "</" + block + ">") block = null; continue; }
            if (text.StartsWith('<') && text.EndsWith('>')) { block = text[1..^1]; continue; }
            var args = Regex.Matches(text, "\"([^\"]*)\"|'([^']*)'|([^\\s]+)")
                .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Value)
                .TakeWhile(t => !t.StartsWith('#') && !t.StartsWith(';')).ToArray();
            if (args.Length > 0) options[args[0].TrimStart('-')] = args.Skip(1).ToArray();
        }
        return options.TryGetValue("remote-cert-tls", out var role) && role.Length == 1 && role[0] == "server"
            || options.TryGetValue("verify-x509-name", out var name) && name.Length is 1 or 2 &&
                !string.IsNullOrWhiteSpace(name[0]) && (name.Length == 1 || name[1] is "subject" or "name" or "name-prefix")
            || options.TryGetValue("peer-fingerprint", out var pins) && pins.Length > 0 &&
                pins.All(p => Regex.IsMatch(p, @"^(?:[a-fA-F0-9]{2}:){31}[a-fA-F0-9]{2}$"));
    }

    public static string Warning(Profile profile) => profile.IsOpenVpn
        ? HasOpenVpnServerAuthentication(profile.OpenVpnConfig) ? "" : "Безопасность: старый OpenVPN-профиль без проверки подлинности сервера. Требуется миграция; исходный профиль не изменён."
        : CertificateValidationDisabled(profile) ? "Безопасность: проверка TLS-сертификата отключена." : "";
}
