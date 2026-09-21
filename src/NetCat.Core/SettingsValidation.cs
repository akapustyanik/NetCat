using System.Net;
using System.Text.Json.Nodes;
namespace NetCat.Core;
public static class SettingsValidation
{
    public static void Validate(AppSettings s)
    {
        if(s.Profiles.Select(p=>p.Id).Distinct().Count()!=s.Profiles.Count || s.Subscriptions.Select(p=>p.Id).Distinct().Count()!=s.Subscriptions.Count || s.Rules.Select(p=>p.Id).Distinct().Count()!=s.Rules.Count) throw new FormatException("Повторяющийся идентификатор профиля, подписки или правила.");
        if(s.MainProfileId.HasValue && !s.Profiles.Any(p=>p.Id==s.MainProfileId && !p.IsOpenVpn)) throw new FormatException("Основной VPN-профиль отсутствует.");
        if(s.OpenVpnProfileId.HasValue && !s.Profiles.Any(p=>p.Id==s.OpenVpnProfileId && p.IsOpenVpn)) throw new FormatException("OpenVPN-профиль отсутствует.");
        foreach(var port in new[]{s.SocksPort,s.TelegramSocksPort,s.TelegramWsPort}) if(port is <1 or >65535) throw new FormatException("Порт должен быть от 1 до 65535.");
        foreach(var dns in new[]{s.DirectDns,s.OpenVpnDns}) if(dns.Length>0 && !IPAddress.TryParse(dns,out _)) throw new FormatException("DNS должен быть IP-адресом сервера.");
        if(s.TelegramSocks && s.TelegramSocksHost is "localhost" or "127.0.0.1" && s.TelegramSocksPort==s.SocksPort) throw new FormatException("Telegram SOCKS не должен указывать на вход самого NetCat.");
        RuleValidation.Domains(s.LocalDomains); RuleValidation.Domains(s.OpenVpnDomains);
        foreach(var rule in s.Rules) RuleValidation.Validate(rule);
        foreach(var profile in s.Profiles)
        {
            if(string.IsNullOrWhiteSpace(profile.Name)) throw new FormatException("Введите название профиля.");
            if(profile.IsOpenVpn) continue; // Native OpenVPN validation belongs to the network layer.
            if(string.IsNullOrWhiteSpace(profile.Host) || Uri.CheckHostName(profile.Host)==UriHostNameType.Unknown) throw new FormatException("Некорректный адрес сервера.");
            if(profile.Port is <1 or >65535) throw new FormatException("Порт профиля должен быть от 1 до 65535.");
            if(JsonNode.Parse(profile.OutboundJson) is not JsonObject) throw new FormatException("Нужен объект outbound JSON.");
        }
    }
    public static void RepairSelections(AppSettings s)
    {
        if(!s.Profiles.Any(p=>p.Id==s.MainProfileId && !p.IsOpenVpn)) s.MainProfileId=s.Profiles.FirstOrDefault(p=>!p.IsOpenVpn)?.Id;
        if(!s.Profiles.Any(p=>p.Id==s.OpenVpnProfileId && p.IsOpenVpn)) s.OpenVpnProfileId=s.Profiles.FirstOrDefault(p=>p.IsOpenVpn)?.Id;
    }
}
