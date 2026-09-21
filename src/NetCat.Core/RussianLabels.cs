namespace NetCat.Core;
public static class RussianLabels
{
    public static string Of(object? value) => value switch
    {
        RouteTarget.Direct => "Напрямую", RouteTarget.Vpn => "Через VPN", RouteTarget.OpenVpn => "Через OpenVPN", RouteTarget.Block => "Заблокировать",
        RuleKind.GeoSite => "GeoSite · группа доменов", RuleKind.GeoIp => "GeoIP · страна / сеть", RuleKind.Domain => "Домен и поддомены", RuleKind.ExactDomain => "Точный домен", RuleKind.Process => "Имя приложения", RuleKind.ExecutablePath => "Путь к приложению", RuleKind.IpCidr => "IP-адрес / подсеть",
        ServiceRoute.Vpn => "VPN", ServiceRoute.Zapret => "Zapret", _ => value?.ToString() ?? ""
    };
}
