namespace NetCat.Core;
public static class ConnectionHealth
{
    public static string Status(DelayResult profile, DelayResult? system, bool tun)
    {
        if (profile.Success && (!tun || system?.Success == true)) return tun ? "VPN работает" : "Прокси работает";
        if (profile.Success) return "VPN подключён · TUN не подтверждён";
        if (system?.Success == true) return "VPN работает · проверка профиля не удалась";
        return "Доступ не подтверждён";
    }
}
