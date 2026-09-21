using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using NetCat.Core;
namespace NetCat.Engine;
public static class ProfileIdentity
{
    public static bool MatchesLegacyGrpc(Profile saved, Profile incoming)
    {
        var config = JsonNode.Parse(incoming.OutboundJson)!;
        if (config["transport"]?["type"]?.ToString() != "grpc") return false;
        var service = config["transport"]?["service_name"]?.ToString() ?? "";
        if (!service.StartsWith('/')) return false;
        var legacy = JsonSettings.Clone(incoming);
        config["transport"]!["service_name"] = service.TrimStart('/');
        legacy.OutboundJson = config.ToJsonString();
        return Key(saved) == Key(legacy);
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string CredentialHash(JsonNode config)
    {
        var credentials=new List<string>();
        void Visit(JsonNode? node)
        {
            if(node is JsonObject obj) foreach(var pair in obj.OrderBy(p=>p.Key,StringComparer.Ordinal))
            {
                if(pair.Key is "uuid" or "id" or "password" or "username" or "private_key" or "key") credentials.Add(pair.Key+"="+pair.Value?.ToJsonString());
                else Visit(pair.Value);
            }
            else if(node is JsonArray array) foreach(var child in array) Visit(child);
        }
        Visit(config); return Hash(string.Join("|",credentials));
    }
    public static string Key(Profile profile)
    {
        if(profile.IsOpenVpn) return "openvpn|"+Hash(string.Join("\n",profile.OpenVpnConfig.Replace("\r", "").Split('\n').Select(l=>l.Trim()).Where(l=>l.Length>0 && !l.StartsWith('#') && !l.StartsWith(';')))+"|"+profile.Username+"|"+profile.Password);
        var config=JsonNode.Parse(profile.OutboundJson)!;
        var transport=config["transport"]?["type"]?.ToString() ?? config["streamSettings"]?["network"]?.ToString() ?? "tcp";
        if(transport is "raw" or "none")transport="tcp";
        var transportConfig=config["transport"] ?? config["streamSettings"]?[transport+"Settings"];
        var path=transportConfig?["path"]?.ToString() ?? transportConfig?["service_name"]?.ToString() ?? transportConfig?["serviceName"]?.ToString() ?? "";
        var security=config["tls"]?["reality"]!=null?"reality":(bool?)config["tls"]?["enabled"]==true?"tls":config["streamSettings"]?["security"]?.ToString()??"none";
        var sni=config["tls"]?["server_name"]?.ToString()??config["streamSettings"]?[security+"Settings"]?["serverName"]?.ToString()??"";
        return string.Join('|',profile.Host.ToLowerInvariant(),profile.Port,profile.Protocol,transport,path,security,sni,CredentialHash(config));
    }
}
