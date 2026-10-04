using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NetCat.Core;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace NetCat.Engine;

public static class SubscriptionDocument
{
    public const string MediaType = "application/vnd.netcat.subscription+json";
    public static bool IsYaml(string text) => Regex.IsMatch(text, @"(?m)^\s*(proxies|outbounds):\s*");

    public static bool TryParse(string input, string name, out ImportResult result)
    {
        result = new([], []);
        if (IsYaml(input) && !input.StartsWith('{'))
        {
            try { result = ParseYaml(input, name); }
            catch (Exception e) when (e is not OutOfMemoryException) { result.Errors.Add("Некорректный или неподдерживаемый YAML; параметры не упрощены."); }
            return true;
        }
        if (!input.StartsWith('{')) return false;
        JsonObject? root;
        try { root = JsonNode.Parse(input) as JsonObject; }
        catch { return false; }
        if (root?["format"]?.ToString() != "netcat-subscription") return false;
        if (root["version"]?.ToString() != "1" || root["profiles"] is not JsonArray entries)
        { result.Errors.Add("Неподдерживаемая версия или структура манифеста подписки."); return true; }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < entries.Count; i++)
        {
            try
            {
                var entry = entries[i] as JsonObject ?? throw new InvalidDataException();
                var id = entry["id"]?.GetValue<string>() ?? "";
                if (!Regex.IsMatch(id, @"\A[A-Za-z0-9._:-]{1,128}\z") || !ids.Add(id)) throw new InvalidDataException();
                if ((entry["uri"] != null) == (entry["outbound"] != null)) throw new InvalidDataException();
                var parsed = ProfileImporter.Parse(entry["uri"]?.GetValue<string>() ?? entry["outbound"]!.ToJsonString(), name);
                if (parsed.Errors.Count != 0 || parsed.Profiles.Count != 1) throw new InvalidDataException();
                var profile = parsed.Profiles[0];
                if (string.IsNullOrWhiteSpace(profile.Host) || profile.Port is < 1 or > 65535 || !ProfileImporter.Supported.Contains(profile.Protocol)) throw new InvalidDataException();
                profile.SubscriptionItemId = "manifest:" + id;
                if (entry["name"] is JsonValue label) profile.Name = label.GetValue<string>();
                result.Profiles.Add(profile);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { result.Errors.Add($"Профиль {i + 1}: некорректный идентификатор, протокол или параметры."); }
        }
        result = result with { ReceivedCount = entries.Count, CompleteSnapshot = root["complete"]?.ToString() == "true" && result.Errors.Count == 0 };
        return true;
    }

    private static ImportResult ParseYaml(string input, string name)
    {
        // Validate before materializing: no aliases, custom tags, multiple documents,
        // or unbounded nesting. YAML is data, never an object construction mechanism.
        var parser = new Parser(new StringReader(input)); int count = 0, depth = 0, documents = 0;
        while (parser.MoveNext())
        {
            if (++count > 65536 || parser.Current is AnchorAlias) throw new InvalidDataException();
            if (parser.Current is NodeEvent node && !node.Tag.IsEmpty) throw new InvalidDataException();
            if (parser.Current is DocumentStart && ++documents > 1) throw new InvalidDataException();
            if (parser.Current is MappingStart or SequenceStart && ++depth > 64) throw new InvalidDataException();
            if (parser.Current is MappingEnd or SequenceEnd) depth--;
        }
        var stream = new YamlStream(); stream.Load(new StringReader(input));
        var root = ToJson(stream.Documents.Single().RootNode) as JsonObject ?? throw new InvalidDataException();
        if (root["outbounds"] is JsonArray) return ProfileImporter.Parse(root.ToJsonString(), name);
        var proxies = root["proxies"] as JsonArray ?? throw new InvalidDataException();
        var result = new ImportResult([], []) { ReceivedCount = proxies.Count };
        for (int i = 0; i < proxies.Count; i++)
        {
            try
            {
                var proxy = proxies[i] as JsonObject ?? throw new InvalidDataException();
                var outbound = ClashOutbound(proxy);
                var imported = ProfileImporter.Parse(outbound.ToJsonString(), name);
                if (imported.Errors.Count != 0 || imported.Profiles.Count != 1) throw new InvalidDataException();
                imported.Profiles[0].Name = proxy["name"]?.ToString() ?? name;
                result.Profiles.Add(imported.Profiles[0]);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { result.Errors.Add($"Профиль YAML {i + 1}: неподдерживаемые или некорректные параметры."); }
        }
        return result;
    }
    private static JsonNode ToJson(YamlNode node) => node switch
    {
        YamlMappingNode map => new JsonObject(map.Children.Select(pair => new KeyValuePair<string, JsonNode?>(((YamlScalarNode)pair.Key).Value ?? "", ToJson(pair.Value)))),
        YamlSequenceNode list => new JsonArray(list.Children.Select(c => (JsonNode?)ToJson(c)).ToArray()),
        YamlScalarNode scalar => Scalar(scalar),
        _ => throw new InvalidDataException()
    };
    private static JsonNode Scalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";
        if (scalar.Style == ScalarStyle.Plain && (value is "true" or "false" || Regex.IsMatch(value, @"\A-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?\z")))
            return JsonNode.Parse(value)!;
        return JsonValue.Create(value)!;
    }
    private static JsonObject ClashOutbound(JsonObject p)
    {
        string S(string key, string fallback = "") => p[key]?.ToString() ?? fallback;
        bool B(string key) => S(key).ToLowerInvariant() is "true" or "1";
        // Fail closed on extensions we cannot represent; never silently lose obfs.
        var allowed = new HashSet<string> { "name", "type", "server", "port", "uuid", "password", "cipher", "alterId", "udp", "tls", "servername", "sni", "skip-cert-verify", "client-fingerprint", "alpn", "flow", "network", "ws-opts", "grpc-opts", "reality-opts", "obfs", "obfs-password", "congestion-controller", "udp-relay-mode", "disable-sni", "reduce-rtt" };
        if (p.Any(k => !allowed.Contains(k.Key))) throw new NotSupportedException();
        var type = S("type") == "ss" ? "shadowsocks" : S("type");
        if (type is not ("vless" or "vmess" or "trojan" or "shadowsocks" or "hysteria2" or "tuic")) throw new NotSupportedException();
        var o = new JsonObject { ["type"] = type, ["server"] = S("server"), ["server_port"] = int.Parse(S("port")) };
        if (type is "vless" or "vmess" or "tuic") o["uuid"] = S("uuid");
        if (type is "trojan" or "shadowsocks" or "hysteria2" or "tuic") o["password"] = S("password");
        if (type == "shadowsocks") o["method"] = S("cipher");
        if (type == "vmess") { o["security"] = S("cipher", "auto"); o["alter_id"] = int.Parse(S("alterId", "0")); }
        if (S("flow").Length > 0) o["flow"] = S("flow");
        if (B("tls") || type is "trojan" or "hysteria2" or "tuic" || p["reality-opts"] != null)
        {
            var tls = new JsonObject { ["enabled"] = true, ["server_name"] = S("servername", S("sni", S("server"))) };
            if (B("skip-cert-verify")) tls["insecure"] = true;
            if (p["alpn"] is JsonArray alpn) tls["alpn"] = alpn.DeepClone();
            if (S("client-fingerprint").Length > 0) tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = S("client-fingerprint") };
            if (p["reality-opts"] is JsonObject reality)
            {
                if (reality.Any(k => k.Key is not ("public-key" or "short-id"))) throw new NotSupportedException();
                tls["reality"] = new JsonObject { ["enabled"] = true, ["public_key"] = reality["public-key"]?.ToString(), ["short_id"] = reality["short-id"]?.ToString() };
            }
            if (type == "tuic" && B("disable-sni")) tls["disable_sni"] = true;
            o["tls"] = tls;
        }
        var network = S("network", "tcp");
        if (network == "ws")
        {
            var ws = p["ws-opts"] as JsonObject;
            if (ws != null && ws.Any(k => k.Key is not ("path" or "headers" or "max-early-data" or "early-data-header-name"))) throw new NotSupportedException();
            var t = new JsonObject { ["type"] = "ws", ["path"] = ws?["path"]?.ToString() ?? "/" };
            if (ws?["headers"] is JsonObject headers) t["headers"] = headers.DeepClone();
            if (ws?["max-early-data"] is { } early) t["max_early_data"] = int.Parse(early.ToString());
            if (ws?["early-data-header-name"] is { } header) t["early_data_header_name"] = header.ToString();
            o["transport"] = t;
        }
        else if (network == "grpc")
        {
            if (p["grpc-opts"] is JsonObject grpc && grpc.Any(k => k.Key != "grpc-service-name")) throw new NotSupportedException();
            o["transport"] = new JsonObject { ["type"] = "grpc", ["service_name"] = p["grpc-opts"]?["grpc-service-name"]?.ToString() ?? "" };
        }
        else if (network != "tcp") throw new NotSupportedException();
        if (p["obfs"] != null || p["obfs-password"] != null)
        {
            if (type != "hysteria2" || S("obfs") is not ("salamander" or "gecko") || S("obfs-password").Length == 0) throw new NotSupportedException();
            o["obfs"] = new JsonObject { ["type"] = S("obfs"), ["password"] = S("obfs-password") };
        }
        if (type == "tuic") { o["congestion_control"] = S("congestion-controller", "bbr"); o["udp_relay_mode"] = S("udp-relay-mode", "native"); o["zero_rtt_handshake"] = B("reduce-rtt"); }
        return o;
    }
}
