namespace NetCat.Core.Enums
{
    public enum CoreType
    {
        SingBox,
        Xray,
        OpenVpn
    }

    public enum RoutingMode
    {
        Global,             // All traffic through VPN
        RuleBased,          // Route according to Geosite/GeoIP and rules
        SelectiveVpn,       // VPN only for specified apps/domains (Whitelist)
        SelectiveDirect     // VPN for everything except specified apps/domains (Blacklist)
    }

    public enum FailoverStrategy
    {
        ActiveActiveRacing, // Parallel SYN to 2 protocols
        HotStandby          // Immediate switch on metric drop
    }

    public enum NodeStatus
    {
        Active,
        Standby,
        Degraded,
        Dead
    }

    public enum RuleType
    {
        Process,
        Domain,
        IP,
        GeoData
    }

    public enum RuleAction
    {
        Proxy,
        Direct,
        Block
    }
}
