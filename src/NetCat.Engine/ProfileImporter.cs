using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NetCat.Core;

namespace NetCat.Engine;
public sealed record ImportResult(List<Profile> Profiles, List<string> Errors)
{
    // Only a versioned, explicitly complete manifest may authorize deletion.
    public bool CompleteSnapshot { get; init; }
    public int ReceivedCount { get; init; }
}
public static partial class ProfileImporter
{
    public static ImportResult ParseForImport(string input, string name = "Импорт")
    {
        var result = Parse(input, name);
        foreach(var profile in result.Profiles) profile.Name = ProfileDisplayNames.Normalize(profile.Name);
        foreach (var profile in result.Profiles.Where(p => p.IsOpenVpn).ToArray())
        {
            try { OpenVpnConfiguration.ValidateNewImport(profile.OpenVpnConfig); }
            catch (InvalidDataException ex) { result.Profiles.Remove(profile); result.Errors.Add(ex.Message); }
        }
        return result;
    }
    public static ImportResult Parse(string input, string name = "Импорт")
    {
        var result = new ImportResult([], []);
        input = input.Trim().TrimStart('\uFEFF');
        if (input.Length > 8 * 1024 * 1024) { result.Errors.Add("Конфигурация больше 8 МБ."); return result; }
        if (SubscriptionDocument.TryParse(input, name, out var document)) return document;
        if (input.StartsWith('<') && !input.StartsWith("<ca>")) { result.Errors.Add("Ответ содержит HTML/XML вместо конфигурации."); return result; }
        if (Regex.IsMatch(input, @"(?m)^\s*(client|dev\s+(tun|tap))\s*$"))
        {
            try {
                input = OpenVpnConfiguration.Normalize(input);
                OpenVpnConfiguration.Validate(input);
                result.Profiles.Add(new Profile { Name = name, Protocol = "openvpn", Core = "OpenVPN", OpenVpnConfig = input, Candidate = false, Host = Regex.Match(input, @"(?m)^\s*remote\s+(\S+)").Groups[1].Value });
            }
            catch (Exception e) { result.Errors.Add(e.Message); }
            return result;
        }
        if (input.StartsWith('{') || input.StartsWith('['))
        {
            try
            {
                var root = JsonNode.Parse(input)!;
                var list = root is JsonArray arr ? arr : root["outbounds"] as JsonArray ?? new JsonArray(root.DeepClone());
                foreach (var node in list)
                {
                    try
                    {
                    if (node is not JsonObject item) throw new InvalidDataException();
                    if (item["protocol"] != null)
                    {
                        if (item["protocol"]!.ToString() is "freedom" or "blackhole" or "dns") continue;
                        result.Profiles.Add(FromXray(item, item["tag"]?.ToString() ?? name)); continue;
                    }
                    var type = item["type"]?.ToString() ?? "";
                    if (type is "direct" or "block" or "dns" or "selector" or "urltest") continue;
                    if (!Supported.Contains(type)) { result.Errors.Add("Неподдерживаемый JSON outbound."); continue; }
                    result.Profiles.Add(FromOutbound(item, item["tag"]?.ToString() ?? name));
                    }
                    catch (Exception e) when (e is not OutOfMemoryException) { result.Errors.Add("Некорректный JSON outbound."); }
                }
            }
            catch { result.Errors.Add("Не удалось разобрать JSON конфигурации sing-box или Xray."); }
            return result;
        }
        if (!input.Contains("://"))
        {
            try
            {
                var decoded = Decode64(input).Trim();
                if (decoded.StartsWith('{') || decoded.StartsWith('[') || SubscriptionDocument.IsYaml(decoded)) return Parse(decoded, name);
                if (!decoded.Contains("://")) throw new FormatException();
                input = decoded;
            }
            catch { result.Errors.Add("Неизвестный формат: ожидаются ссылки, Base64, JSON, YAML или .ovpn."); return result; }
        }
        var lines = input.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith('#')) continue;
            try { result.Profiles.Add(ParseLink(lines[i].Trim())); }
            catch (Exception e) when (e is not OutOfMemoryException) { result.Errors.Add($"Строка {i + 1}: " + (e is NotSupportedException ? e.Message : "Некорректные параметры ссылки: " + e.GetType().Name)); }
        }
        return result;
    }
    public static readonly HashSet<string> Supported = ["vless", "vmess", "trojan", "shadowsocks", "hysteria2", "tuic", "naive", "socks", "http"];
    private static string Decode64(string text) { text = text.Replace('-', '+').Replace('_', '/'); text = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()); return Encoding.UTF8.GetString(Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '='))); }
    private static Profile FromOutbound(JsonObject obj, string name)
    {
        var copy = (JsonObject)obj.DeepClone(); copy.Remove("tag"); copy.Remove("detour"); copy.Remove("bind_interface");
        if (copy["type"]?.ToString() == "naive") ValidateNaive(copy);
        return new() { Name = name, Core = copy["type"]?.ToString() == "trojan" || copy["tls"]?["reality"] != null ? "Xray" : "sing-box", Protocol = copy["type"]!.ToString(), Host = copy["server"]?.ToString() ?? "", Port = (int?)copy["server_port"] ?? 443, OutboundJson = copy.ToJsonString(JsonSettings.Options) };
    }
    private static Profile FromXray(JsonObject item, string name)
    {
        var copy = (JsonObject)item.DeepClone(); copy.Remove("tag"); copy.Remove("sendThrough"); copy.Remove("proxySettings");
        var server = copy["settings"]?["vnext"]?[0] ?? copy["settings"]?["servers"]?[0];
        if (server == null) throw new NotSupportedException("В Xray outbound не найден сервер.");
        return new() { Name = name, Core = "Xray", Protocol = copy["protocol"]!.ToString(), Host = server["address"]!.ToString(), Port = (int?)server["port"] ?? 443, OutboundJson = copy.ToJsonString(JsonSettings.Options) };
    }
    public static Profile ParseLink(string link)
    {
        if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
        {
            var v = JsonNode.Parse(Decode64(link[8..].Split('#')[0]))!;
            var o = new JsonObject { ["type"] = "vmess", ["server"] = v["add"]!.ToString(), ["server_port"] = int.Parse(v["port"]!.ToString()), ["uuid"] = v["id"]!.ToString(), ["security"] = v["scy"]?.ToString() is { Length: > 0 } cipher ? cipher : "auto", ["alter_id"] = int.TryParse(v["aid"]?.ToString(), out var aid) ? aid : 0 };
            if (v["tls"]?.ToString() == "tls")
            {
                var tls = new JsonObject { ["enabled"] = true, ["server_name"] = v["sni"]?.ToString() is { Length: > 0 } sni ? sni : v["host"]?.ToString() ?? v["add"]!.ToString() };
                if (v["alpn"]?.ToString() is { Length: > 0 } alpn)
                    tls["alpn"] = new JsonArray(alpn.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => (JsonNode?)JsonValue.Create(x.Trim())).ToArray());
                if (v["fp"]?.ToString() is { Length: > 0 } fingerprint)
                    tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fingerprint };
                if (v["allowInsecure"]?.ToString() is "1" or "true") tls["insecure"] = true;
                o["tls"] = tls;
            }
            AddTransport(o, v["net"]?.ToString() ?? "tcp", v["path"]?.ToString() ?? "/", v["host"]?.ToString() ?? "");
            return FromOutbound(o, v["ps"]?.ToString() ?? "VMess");
        }
        if (link.StartsWith("ss://") && !link[5..].Split('#')[0].Contains('@'))
        {
            var parts = link[5..].Split('#', 2); link = "ss://" + Decode64(parts[0]) + (parts.Length > 1 ? "#" + parts[1] : "");
        }
        var u = new Uri(link); var type = u.Scheme.ToLowerInvariant() switch { "ss" => "shadowsocks", "hy2" => "hysteria2", "naive+https" => "naive", var s => s };
        if (!Supported.Contains(type)) throw new NotSupportedException("Неизвестный протокол.");
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2); var key = Uri.UnescapeDataString(pair[0]);
            var value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "";
            if (q.TryGetValue(key, out var previous) && previous != value)
                throw new InvalidDataException("Ссылка содержит повторяющийся параметр с разными значениями.");
            q[key] = value;
        }
        string Q(string key, string fallback = "") => q.GetValueOrDefault(key, fallback);
        var auth = Uri.UnescapeDataString(u.UserInfo);
        var obj = new JsonObject { ["type"] = type, ["server"] = u.Host, ["server_port"] = u.Port > 0 ? u.Port : 443 };
        if (type == "naive")
        {
            if (!u.Scheme.Equals("naive+https", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Naive: ожидается ссылка naive+https с проверкой TLS.");
            if (q.Keys.Any(key => key is not ("sni" or "peer" or "udp_over_tcp" or "quic")))
                throw new NotSupportedException("Naive: неподдерживаемый параметр ссылки; проверка TLS не отключается.");
            bool Flag(string key)
            {
                var value = Q(key, "false");
                return value.ToLowerInvariant() switch { "true" or "1" => true, "false" or "0" => false, _ => throw new FormatException("Invalid Naive flag") };
            }
            var credentials = u.UserInfo.Split(':', 2);
            obj["username"] = Uri.UnescapeDataString(credentials[0]);
            obj["password"] = credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : "";
            obj["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = Q("sni", Q("peer", u.Host)) };
            obj["udp_over_tcp"] = Flag("udp_over_tcp"); obj["quic"] = Flag("quic");
            return FromOutbound(obj, u.Fragment.Length > 1 ? Uri.UnescapeDataString(u.Fragment[1..]) : u.Host);
        }
        switch (type)
        {
            case "vless": obj["uuid"] = auth; if (Q("flow").Length > 0) obj["flow"] = Q("flow"); break;
            case "trojan": case "hysteria2": obj["password"] = auth; break;
            case "tuic": var pair = auth.Split(':', 2); obj["uuid"] = pair[0]; obj["password"] = pair.ElementAtOrDefault(1) ?? ""; obj["congestion_control"] = Q("congestion_control", "bbr"); break;
            case "shadowsocks": if (!auth.Contains(':')) auth = Decode64(auth); var ss = auth.Split(':', 2); obj["method"] = ss[0]; obj["password"] = ss[1]; if (q.ContainsKey("plugin")) throw new NotSupportedException("Shadowsocks plugins не поддерживаются."); break;
            case "http": case "socks": var credentials = auth.Split(':', 2); if (auth.Length > 0) { obj["username"] = credentials[0]; obj["password"] = credentials.ElementAtOrDefault(1) ?? ""; } break;
        }
        if (Q("security") is "tls" or "reality" || type is "trojan" or "hysteria2" or "tuic")
        {
            var tls = new JsonObject { ["enabled"] = true, ["server_name"] = Q("sni", Q("peer", u.Host)) };
            if (Q("insecure", Q("allowInsecure")) is "1" or "true") tls["insecure"] = true;
            if (Q("alpn").Length > 0) tls["alpn"] = new JsonArray(Q("alpn").Split(',').Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            if (Q("fp").Length > 0) tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = Q("fp") };
            if (Q("security") == "reality") tls["reality"] = new JsonObject { ["enabled"] = true, ["public_key"] = Q("pbk"), ["short_id"] = Q("sid") };
            obj["tls"] = tls;
        }
        if (type == "hysteria2" && (q.ContainsKey("obfs") || q.ContainsKey("obfs-password")))
        {
            var obfs = Q("obfs").ToLowerInvariant();
            if (obfs is not ("salamander" or "gecko"))
                throw new NotSupportedException("Hysteria2: неподдерживаемый тип обфускации.");
            if (string.IsNullOrEmpty(Q("obfs-password")))
                throw new FormatException("Hysteria2: нужен пароль обфускации.");
            obj["obfs"] = new JsonObject { ["type"] = obfs, ["password"] = Q("obfs-password") };
        }
        if (type is "vless" or "trojan" && Q("type") is "xhttp" or "splithttp")
        {
            var stream = new JsonObject { ["network"] = "xhttp", ["security"] = Q("security", "none") };
            var xhttp = new JsonObject { ["host"] = Q("host"), ["path"] = Q("path", "/"), ["mode"] = Q("mode", "auto") };
            if (Q("extra").Length > 0) xhttp["extra"] = JsonNode.Parse(Q("extra"));
            stream["xhttpSettings"] = xhttp;
            if (Q("security") is "tls" or "reality")
            {
                var tls = new JsonObject { ["serverName"] = Q("sni", u.Host), ["fingerprint"] = Q("fp", "chrome") };
                if (Q("security") == "reality") { tls["publicKey"] = Q("pbk"); tls["shortId"] = Q("sid"); tls["spiderX"] = Q("spx"); }
                else { tls["allowInsecure"] = Q("insecure", Q("allowInsecure")) is "1" or "true"; if (Q("alpn").Length > 0) tls["alpn"] = SingBoxConfig.Array(Q("alpn").Split(',')); }
                stream[Q("security") + "Settings"] = tls;
            }
            JsonObject settings = type == "trojan"
                ? new() { ["servers"] = new JsonArray(new JsonObject { ["address"] = u.Host, ["port"] = u.Port > 0 ? u.Port : 443, ["password"] = auth }) }
                : new() { ["vnext"] = new JsonArray(new JsonObject { ["address"] = u.Host, ["port"] = u.Port > 0 ? u.Port : 443, ["users"] = new JsonArray(new JsonObject { ["id"] = auth, ["encryption"] = Q("encryption", "none"), ["flow"] = Q("flow") }) }) };
            return FromXray(new JsonObject { ["protocol"] = type, ["settings"] = settings, ["streamSettings"] = stream }, u.Fragment.Length > 1 ? Uri.UnescapeDataString(u.Fragment[1..]) : u.Host);
        }
        if (type is "vless" or "trojan") AddTransport(obj, Q("type", "tcp"), Q("type")=="grpc"?Q("serviceName",Q("path","/")):Q("path","/"), Q("host"));
        return FromOutbound(obj, u.Fragment.Length > 1 ? Uri.UnescapeDataString(u.Fragment[1..]) : u.Host);
    }
    private static void ValidateNaive(JsonObject obj)
    {
        if (string.IsNullOrWhiteSpace(obj["server"]?.ToString()) ||
            obj["server_port"]?.GetValue<int>() is not (>= 1 and <= 65535) ||
            string.IsNullOrEmpty(obj["username"]?.ToString()) || string.IsNullOrEmpty(obj["password"]?.ToString()))
            throw new FormatException("Naive endpoint and authentication required");
        if (obj["tls"] is not JsonObject tls || tls["enabled"]?.GetValue<bool>() != true ||
            tls.Any(p => p.Key is not ("enabled" or "server_name" or "certificate" or "certificate_path" or "ech")))
            throw new NotSupportedException("Naive требует TLS; поддерживаются только параметры TLS нативного Cronet runtime.");
    }
    private static void AddTransport(JsonObject obj, string type, string path, string host)
    {
        if (type is "tcp" or "none" or "") return;
        obj["transport"] = type switch
        {
            "ws" => new JsonObject { ["type"] = "ws", ["path"] = path, ["headers"] = new JsonObject { ["Host"] = host } },
            "grpc" => new JsonObject { ["type"] = "grpc", ["service_name"] = path },
            "http" or "h2" => new JsonObject { ["type"] = "http", ["path"] = path, ["host"] = new JsonArray(host) },
            "httpupgrade" => new JsonObject { ["type"] = "httpupgrade", ["path"] = path, ["host"] = host },
            _ => throw new NotSupportedException("Неподдерживаемый транспорт.")
        };
    }
}
public static class OpenVpnConfiguration
{
    public static void ValidateNewImport(string raw)
    {
        Validate(raw);
        if (!ProfileSecurity.HasOpenVpnServerAuthentication(raw))
            throw new InvalidDataException("Новый профиль OpenVPN должен проверять сервер: remote-cert-tls server, verify-x509-name или peer-fingerprint. Исправьте профиль у его поставщика и импортируйте снова.");
    }
    public static string? MigrationDiagnostic(Profile profile)
    {
        if (!profile.IsOpenVpn) return null;
        try { Validate(Normalize(profile.OpenVpnConfig)); return null; }
        catch (InvalidDataException error) { return "Профиль «" + profile.Name + "»: " + error.Message + " Исходная конфигурация сохранена; исправьте её или повторно импортируйте совместимый профиль."; }
    }
    // Privileged child boundary: unknown directives are rejected, not forwarded.
    private static readonly HashSet<string> Allowed = new(("client dev proto remote remote-random remote-random-hostname resolv-retry nobind " +
        "persist-key persist-tun remote-cert-tls verify-x509-name peer-fingerprint auth cipher data-ciphers data-ciphers-fallback ncp-ciphers " +
        "tls-client tls-version-min tls-version-max tls-cipher tls-ciphersuites tls-timeout reneg-sec reneg-bytes reneg-pkts " +
        "key-direction auth-user-pass auth-nocache auth-retry auth-token-user connect-retry connect-retry-max connect-timeout server-poll-timeout " +
        "keepalive ping ping-restart ping-exit explicit-exit-notify float tun-mtu tun-mtu-extra mssfix fragment sndbuf rcvbuf " +
        "verb mute mute-replay-warnings replay-window pull route-nopull route-noexec redirect-gateway redirect-private " +
        "dhcp-option register-dns block-outside-dns windows-driver dev-node script-security " +
        "ca cert key pkcs12 tls-auth tls-crypt tls-crypt-v2 crl-verify compress comp-lzo allow-compression fast-io topology").Split(' '), StringComparer.Ordinal);
    private static readonly HashSet<string> InlineOptions = ["ca", "cert", "key", "pkcs12", "tls-auth", "tls-crypt", "tls-crypt-v2", "crl-verify"];
    public static void Validate(string raw)
    {
        string? inline = null;
        foreach (var line in raw.Split('\n'))
        {
            var text = line.Trim();
            if (inline != null)
            {
                if (text == "</" + inline + ">") inline = null;
                else if (text.StartsWith('<')) throw new InvalidDataException("Некорректный inline-блок OpenVPN.");
                continue;
            }
            if (text.Length == 0 || text.StartsWith('#') || text.StartsWith(';')) continue;
            if (text.StartsWith('<'))
            {
                if (!text.EndsWith('>') || !InlineOptions.Contains(text[1..^1])) throw new InvalidDataException("Неподдерживаемый inline-блок OpenVPN.");
                inline = text[1..^1]; continue;
            }
            var key = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimStart('-') ?? "";
            var args = OpenVpnBundle.Tokens(text);
            if (key is "route" or "route-ipv6" or "route-gateway" or "route-metric")
                throw new InvalidDataException("Статические маршруты .ovpn пока не поддерживаются: " + key + ". Удалите эту директиву; NetCat устанавливает только проверенные IPv4-маршруты PUSH. IPv6 и перехват default route не поддерживаются.");
            if (OpenVpnBundle.FileOptions.Contains(key) && args.Length > 1 && args[1] != "[inline]")
                throw new InvalidDataException("Внешняя зависимость OpenVPN: " + key + ". Импортируйте исходный .ovpn вместе с файлами из его папки.");
            if (!Allowed.Contains(key) || (key == "dev" && (args.Length != 2 || args[1] != "tun"))) throw new InvalidDataException("Не поддерживается директива OpenVPN: " + key + ". Импортируйте совместимый клиентский TUN-профиль без внешних скриптов и системных DNS-настроек.");
        }
        if (inline != null) throw new InvalidDataException("Незакрытый inline-блок OpenVPN: " + inline);
    }
    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string? inline = null;
        bool hasDataCiphers = false;
        foreach (var line in lines)
        {
            var text = line.Trim();
            if (inline != null)
            {
                if (text == "</" + inline + ">") inline = null;
                continue;
            }
            if (text.StartsWith('<') && text.EndsWith('>') && !text.StartsWith("</"))
            {
                inline = text[1..^1];
                continue;
            }
            if (text.Length == 0 || text.StartsWith('#') || text.StartsWith(';')) continue;
            var key = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimStart('-') ?? "";
            if (string.Equals(key, "data-ciphers", StringComparison.OrdinalIgnoreCase))
            {
                hasDataCiphers = true;
                break;
            }
        }

        inline = null;
        var output = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var text = line.Trim();
            if (inline != null)
            {
                if (text == "</" + inline + ">") inline = null;
                output.Add(line);
                continue;
            }
            if (text.StartsWith('<') && text.EndsWith('>') && !text.StartsWith("</"))
            {
                inline = text[1..^1];
                output.Add(line);
                continue;
            }
            if (text.Length == 0 || text.StartsWith('#') || text.StartsWith(';'))
            {
                output.Add(line);
                continue;
            }
            var key = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimStart('-') ?? "";
            if (string.Equals(key, "ncp-ciphers", StringComparison.OrdinalIgnoreCase))
            {
                if (hasDataCiphers) continue;
                var replaced = Regex.Replace(line, @"^(\s*)(--)?ncp-ciphers\b", "${1}data-ciphers", RegexOptions.IgnoreCase);
                output.Add(replaced);
                hasDataCiphers = true;
                continue;
            }
            output.Add(line);
        }
        return string.Join("\n", output);
    }
    public static string Prepare(string raw)
    {
        raw = Normalize(raw);
        Validate(raw); var output = new List<string>(); bool inline = false;
        foreach (var line in raw.Split('\n'))
        {
            var t = line.Trim(); if (t.StartsWith('<')) { inline = !t.StartsWith("</"); output.Add(line); continue; }
            if (!inline && Regex.IsMatch(t, @"^(--)?(redirect-gateway|redirect-private|route|route-ipv6|route-gateway|dhcp-option|register-dns|block-outside-dns|windows-driver|dev-node|script-security|auth-user-pass|management|route-nopull|route-noexec|ncp-ciphers)\b")) continue;
            output.Add(line);
        }
        output.AddRange(["route-nopull", "route-noexec", "pull-filter ignore redirect-gateway", "pull-filter ignore block-outside-dns", "pull-filter ignore \"dns \"", "pull-filter ignore \"dhcp-option \"", "script-security 1", "windows-driver wintun", "dev-node NetCat-OpenVPN"]);
        return string.Join(Environment.NewLine, output);
    }
    public static string ReadWithCertificates(string path)
    {
        var bundle = OpenVpnBundle.Read(path);
        if (bundle.Username.Length > 0) throw new InvalidDataException("Используйте импорт OpenVPN bundle для безопасного сохранения логина и пароля.");
        return bundle.Config;
    }
}
