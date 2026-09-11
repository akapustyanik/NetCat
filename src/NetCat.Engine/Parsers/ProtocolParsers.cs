using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using NetCat.Core.Enums;
using NetCat.Core.Models;

namespace NetCat.Engine.Parsers
{
    public static class LinkDecoders
    {
        public static string Base64Decode(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64)) return string.Empty;
            base64 = base64.Trim().Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }
            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public class ProtocolParser
    {
        public static ProxyProfile? Parse(string rawUri)
        {
            if (string.IsNullOrWhiteSpace(rawUri)) return null;
            rawUri = rawUri.Trim();

            try
            {
                if (rawUri.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                    return ParseVless(rawUri);
                if (rawUri.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
                    return ParseVmess(rawUri);
                if (rawUri.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
                    return ParseTrojan(rawUri);
                if (rawUri.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
                    return ParseShadowsocks(rawUri);
                if (rawUri.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) || rawUri.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase))
                    return ParseHysteria2(rawUri);
                if (rawUri.StartsWith("tuic://", StringComparison.OrdinalIgnoreCase))
                    return ParseTuic(rawUri);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse link {rawUri}: {ex.Message}");
            }

            return null;
        }

        private static ProxyProfile ParseVless(string uriStr)
        {
            var uri = new Uri(uriStr);
            var query = HttpUtility.ParseQueryString(uri.Query);

            return new ProxyProfile
            {
                RawUri = uriStr,
                Name = GetFragmentOrFallback(uri.Fragment, $"VLESS-{uri.Host}"),
                Protocol = "vless",
                TargetCore = CoreType.SingBox,
                ServerAddress = uri.Host,
                ServerPort = uri.Port > 0 ? uri.Port : 443,
                Uuid = uri.UserInfo,
                Flow = query["flow"] ?? string.Empty,
                Security = query["security"] ?? "none",
                Sni = query["sni"] ?? query["peer"] ?? uri.Host,
                PublicKey = query["pbk"] ?? string.Empty,
                ShortId = query["sid"] ?? string.Empty,
                Fingerprint = query["fp"] ?? "chrome",
                Network = query["type"] ?? "tcp",
                Path = query["path"] ?? string.Empty,
                ServiceName = query["serviceName"] ?? string.Empty
            };
        }

        private static ProxyProfile ParseTrojan(string uriStr)
        {
            var uri = new Uri(uriStr);
            var query = HttpUtility.ParseQueryString(uri.Query);

            return new ProxyProfile
            {
                RawUri = uriStr,
                Name = GetFragmentOrFallback(uri.Fragment, $"Trojan-{uri.Host}"),
                Protocol = "trojan",
                TargetCore = CoreType.SingBox,
                ServerAddress = uri.Host,
                ServerPort = uri.Port > 0 ? uri.Port : 443,
                Password = uri.UserInfo,
                Security = query["security"] ?? "tls",
                Sni = query["sni"] ?? query["peer"] ?? uri.Host,
                Fingerprint = query["fp"] ?? "chrome",
                Network = query["type"] ?? "tcp"
            };
        }

        private static ProxyProfile ParseHysteria2(string uriStr)
        {
            var uri = new Uri(uriStr);
            var query = HttpUtility.ParseQueryString(uri.Query);

            return new ProxyProfile
            {
                RawUri = uriStr,
                Name = GetFragmentOrFallback(uri.Fragment, $"Hysteria2-{uri.Host}"),
                Protocol = "hysteria2",
                TargetCore = CoreType.SingBox,
                ServerAddress = uri.Host,
                ServerPort = uri.Port > 0 ? uri.Port : 443,
                Password = uri.UserInfo,
                Sni = query["sni"] ?? uri.Host
            };
        }

        private static ProxyProfile ParseTuic(string uriStr)
        {
            var uri = new Uri(uriStr);
            var query = HttpUtility.ParseQueryString(uri.Query);
            var parts = uri.UserInfo.Split(':');

            return new ProxyProfile
            {
                RawUri = uriStr,
                Name = GetFragmentOrFallback(uri.Fragment, $"TUIC-{uri.Host}"),
                Protocol = "tuic",
                TargetCore = CoreType.SingBox,
                ServerAddress = uri.Host,
                ServerPort = uri.Port > 0 ? uri.Port : 443,
                Uuid = parts.Length > 0 ? parts[0] : string.Empty,
                Password = parts.Length > 1 ? parts[1] : string.Empty,
                Sni = query["sni"] ?? uri.Host
            };
        }

        private static ProxyProfile ParseShadowsocks(string uriStr)
        {
            string clean = uriStr.Substring(5);
            string name = string.Empty;
            int hashIndex = clean.IndexOf('#');
            if (hashIndex != -1)
            {
                name = HttpUtility.UrlDecode(clean.Substring(hashIndex + 1));
                clean = clean.Substring(0, hashIndex);
            }

            string host = string.Empty;
            int port = 8388;
            string method = "aes-256-gcm";
            string password = "";

            if (clean.Contains("@"))
            {
                var atParts = clean.Split('@');
                string userinfo = LinkDecoders.Base64Decode(atParts[0]);
                if (string.IsNullOrEmpty(userinfo)) userinfo = atParts[0];

                var uParts = userinfo.Split(':');
                if (uParts.Length >= 2)
                {
                    method = uParts[0];
                    password = uParts[1];
                }

                var hp = atParts[1].Split(':');
                if (hp.Length >= 2)
                {
                    host = hp[0];
                    int.TryParse(hp[1].Split('?')[0], out port);
                }
            }
            else
            {
                string decoded = LinkDecoders.Base64Decode(clean);
                if (!string.IsNullOrEmpty(decoded) && decoded.Contains("@"))
                {
                    return ParseShadowsocks("ss://" + decoded + (!string.IsNullOrEmpty(name) ? "#" + name : ""));
                }
            }

            return new ProxyProfile
            {
                RawUri = uriStr,
                Name = string.IsNullOrEmpty(name) ? $"SS-{host}" : name,
                Protocol = "ss",
                TargetCore = CoreType.SingBox,
                ServerAddress = host,
                ServerPort = port,
                Password = password,
                Security = method
            };
        }

        private static ProxyProfile? ParseVmess(string uriStr)
        {
            string b64 = uriStr.Substring(8);
            string json = LinkDecoders.Base64Decode(b64);
            if (string.IsNullOrWhiteSpace(json)) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string ps = root.TryGetProperty("ps", out var psProp) ? psProp.GetString() ?? "" : "VMess";
            string add = root.TryGetProperty("add", out var addProp) ? addProp.GetString() ?? "" : "";
            int port = root.TryGetProperty("port", out var portProp) ? (portProp.ValueKind == JsonValueKind.Number ? portProp.GetInt32() : int.Parse(portProp.GetString() ?? "443")) : 443;
            string id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
            string net = root.TryGetProperty("net", out var netProp) ? netProp.GetString() ?? "tcp" : "tcp";
            string tls = root.TryGetProperty("tls", out var tlsProp) ? tlsProp.GetString() ?? "" : "";
            string sni = root.TryGetProperty("sni", out var sniProp) ? sniProp.GetString() ?? add : add;

            return new ProxyProfile
            {
                RawUri = uriStr,
                Name = ps,
                Protocol = "vmess",
                TargetCore = CoreType.SingBox,
                ServerAddress = add,
                ServerPort = port,
                Uuid = id,
                Network = net,
                Security = string.Equals(tls, "tls", StringComparison.OrdinalIgnoreCase) ? "tls" : "none",
                Sni = sni
            };
        }

        private static string GetFragmentOrFallback(string fragment, string fallback)
        {
            if (!string.IsNullOrEmpty(fragment) && fragment.Length > 1)
            {
                return HttpUtility.UrlDecode(fragment.TrimStart('#'));
            }
            return fallback;
        }
    }

    public class SubscriptionParser
    {
        public static List<ProxyProfile> ParseSubscriptionContent(string content)
        {
            var profiles = new List<ProxyProfile>();
            if (string.IsNullOrWhiteSpace(content)) return profiles;

            string decoded = LinkDecoders.Base64Decode(content);
            string workingContent = !string.IsNullOrWhiteSpace(decoded) && decoded.Contains("://") ? decoded : content;

            using var reader = new StringReader(workingContent);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var profile = ProtocolParser.Parse(line);
                if (profile != null)
                {
                    profiles.Add(profile);
                }
            }

            return profiles;
        }
    }
}
