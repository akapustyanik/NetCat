using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NetCat.Core;

namespace NetCat.Engine;
public sealed record ImportResult(List<Profile> Profiles, List<string> Errors);
public static partial class ProfileImporter
{
    public static ImportResult Parse(string input, string name = "Импорт")
    {
        var result = new ImportResult([], []);
        input = input.Trim().TrimStart('\uFEFF');
        if (input.StartsWith('<') && !input.StartsWith("<ca>")) { result.Errors.Add("Ответ содержит HTML/XML вместо конфигурации."); return result; }
        if (Regex.IsMatch(input, @"(?m)^\s*(client|dev\s+(tun|tap))\s*$"))
        {
            try { OpenVpnConfiguration.Validate(input); result.Profiles.Add(new Profile { Name = name, Protocol = "openvpn", Core = "OpenVPN", OpenVpnConfig = input, Candidate = false, Host = Regex.Match(input, @"(?m)^\s*remote\s+(\S+)").Groups[1].Value }); }
            catch (Exception e) { result.Errors.Add(e.Message); }
            return result;
        }
        if (input.StartsWith('{') || input.StartsWith('['))
        {
            try
            {
                var root = JsonNode.Parse(input)!;
                var list = root is JsonArray arr ? arr : root["outbounds"] as JsonArray ?? new JsonArray(root.DeepClone());
                foreach (var item in list.OfType<JsonObject>())
                {
                    if (item["protocol"] != null)
                    {
                        if (item["protocol"]!.ToString() is "freedom" or "blackhole" or "dns") continue;
                        result.Profiles.Add(FromXray(item, item["tag"]?.ToString() ?? name)); continue;
                    }
                    var type = item["type"]?.ToString() ?? "";
                    if (type is "direct" or "block" or "dns" or "selector" or "urltest") continue;
                    if (!Supported.Contains(type)) { result.Errors.Add("Неподдерживаемый JSON outbound: " + type); continue; }
                    result.Profiles.Add(FromOutbound(item, item["tag"]?.ToString() ?? name));
                }
            }
            catch { result.Errors.Add("Не удалось разобрать JSON конфигурации sing-box или Xray."); }
            return result;
        }
        if (!input.Contains("://"))
        {
            try { input = Decode64(input); } catch { result.Errors.Add("Неизвестный формат: ожидаются ссылки, Base64, sing-box JSON или .ovpn."); return result; }
        }
        var lines = input.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            try { result.Profiles.Add(ParseLink(lines[i].Trim())); }
            catch (Exception e) when (e is not OutOfMemoryException) { result.Errors.Add($"Строка {i + 1}: " + (e is NotSupportedException ? e.Message : "Некорректные параметры ссылки: " + e.GetType().Name)); }
        }
        return result;
    }
    public static readonly HashSet<string> Supported = ["vless", "vmess", "trojan", "shadowsocks", "hysteria2", "tuic", "socks", "http"];
    private static string Decode64(string text) { text = text.Replace('-', '+').Replace('_', '/'); text = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()); return Encoding.UTF8.GetString(Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '='))); }
    private static Profile FromOutbound(JsonObject obj, string name)
    {
        var copy = (JsonObject)obj.DeepClone(); copy.Remove("tag"); copy.Remove("detour"); copy.Remove("bind_interface");
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
            var o = new JsonObject { ["type"] = "vmess", ["server"] = v["add"]!.ToString(), ["server_port"] = int.Parse(v["port"]!.ToString()), ["uuid"] = v["id"]!.ToString(), ["security"] = "auto", ["alter_id"] = int.TryParse(v["aid"]?.ToString(), out var aid) ? aid : 0 };
            if (v["tls"]?.ToString() == "tls") o["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = v["sni"]?.ToString() is { Length: > 0 } sni ? sni : v["host"]?.ToString() ?? v["add"]!.ToString() };
            AddTransport(o, v["net"]?.ToString() ?? "tcp", v["path"]?.ToString() ?? "/", v["host"]?.ToString() ?? "");
            return FromOutbound(o, v["ps"]?.ToString() ?? "VMess");
        }
        if (link.StartsWith("ss://") && !link[5..].Split('#')[0].Contains('@'))
        {
            var parts = link[5..].Split('#', 2); link = "ss://" + Decode64(parts[0]) + (parts.Length > 1 ? "#" + parts[1] : "");
        }
        var u = new Uri(link); var type = u.Scheme.ToLowerInvariant() switch { "ss" => "shadowsocks", "hy2" => "hysteria2", var s => s };
        if (!Supported.Contains(type)) throw new NotSupportedException("Неизвестный протокол.");
        var q = u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split('=', 2)).ToDictionary(a => Uri.UnescapeDataString(a[0]), a => a.Length == 2 ? Uri.UnescapeDataString(a[1]) : "", StringComparer.OrdinalIgnoreCase);
        string Q(string key, string fallback = "") => q.GetValueOrDefault(key, fallback);
        var auth = Uri.UnescapeDataString(u.UserInfo);
        var obj = new JsonObject { ["type"] = type, ["server"] = u.Host, ["server_port"] = u.Port > 0 ? u.Port : 443 };
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
        if (type == "hysteria2" && Q("obfs") == "salamander") obj["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = Q("obfs-password") };
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
    private static readonly HashSet<string> Forbidden = ["up", "down", "route-up", "route-pre-down", "ipchange", "plugin", "tls-verify", "auth-user-pass-verify", "client-connect", "client-disconnect", "config", "cd", "chroot", "management", "management-client", "log", "log-append", "status", "writepid", "daemon"];
    public static void Validate(string raw)
    {
        bool inline = false;
        foreach (var line in raw.Split('\n'))
        {
            var text = line.Trim(); if (text.StartsWith("</")) { inline = false; continue; } if (text.StartsWith('<')) { inline = true; continue; } if (inline || text.StartsWith('#') || text.StartsWith(';')) continue;
            var key = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimStart('-') ?? "";
            if (Forbidden.Contains(key) || (key == "dev" && text.Contains("tap"))) throw new InvalidDataException("Не поддерживается директива OpenVPN: " + key + ". Нужен клиентский TUN-профиль без внешних скриптов.");
        }
    }
    public static string Prepare(string raw)
    {
        Validate(raw); var output = new List<string>(); bool inline = false;
        foreach (var line in raw.Split('\n'))
        {
            var t = line.Trim(); if (t.StartsWith('<')) { inline = !t.StartsWith("</"); output.Add(line); continue; }
            if (!inline && Regex.IsMatch(t, @"^(redirect-gateway|redirect-private|route|route-ipv6|route-gateway|dhcp-option|register-dns|block-outside-dns|windows-driver|dev-node|script-security|auth-user-pass|management|route-nopull|route-noexec)\b")) continue;
            output.Add(line);
        }
        output.AddRange(["route-nopull", "route-noexec", "pull-filter ignore redirect-gateway", "pull-filter ignore block-outside-dns", "script-security 1", "windows-driver wintun", "dev-node NetCat-OpenVPN"]);
        return string.Join(Environment.NewLine, output);
    }
    public static string ReadWithCertificates(string path)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(path))!; var raw = File.ReadAllText(path);
        raw = Regex.Replace(raw, @"(?m)^\s*(ca|cert|key|tls-auth|tls-crypt)\s+(?:""([^""]+)""|(\S+))(?:\s+([01]))?\s*$", m =>
        {
            var kind = m.Groups[1].Value; var f = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            if (f == "[inline]") return m.Value;
            var full = Path.GetFullPath(f, root); if (!File.Exists(full)) throw new FileNotFoundException("Не найден связанный сертификат/ключ: " + Path.GetFileName(full));
            return $"<{kind}>\n{File.ReadAllText(full)}\n</{kind}>" + (m.Groups[4].Success ? "\nkey-direction " + m.Groups[4].Value : "");
        });
        Validate(raw); return raw;
    }
}
