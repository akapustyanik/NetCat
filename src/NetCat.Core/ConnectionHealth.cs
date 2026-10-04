namespace NetCat.Core;
// Legacy probe formatter retained for detailed diagnostics and compatibility
// tests. MainViewModel.VpnStatus is authoritative and comes from the runtime
// coordinator's observed TUN health; this formatter must never drive the
// primary connection headline.
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
