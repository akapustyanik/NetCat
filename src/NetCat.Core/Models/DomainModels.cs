using System;
using NetCat.Core.Enums;

namespace NetCat.Core.Models
{
    public class ProxyProfile
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public CoreType TargetCore { get; set; } = CoreType.SingBox;
        public string Protocol { get; set; } = string.Empty; // vless, vmess, trojan, hysteria2, ss, ovpn, tuic
        public string ServerAddress { get; set; } = string.Empty;
        public int ServerPort { get; set; } = 443;
        public string RawUri { get; set; } = string.Empty;
        public string GeneratedConfigJson { get; set; } = string.Empty;
        public bool IsPrimary { get; set; }
        public bool IsBackup { get; set; }
        public NodeStatus Status { get; set; } = NodeStatus.Standby;
        public LatencyResult LastMetrics { get; set; } = new();

        // Protocol specific parameters
        public string Uuid { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Flow { get; set; } = string.Empty;
        public string Security { get; set; } = "tls"; // tls, reality, none
        public string Sni { get; set; } = string.Empty;
        public string PublicKey { get; set; } = string.Empty;
        public string ShortId { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = "chrome";
        public string Network { get; set; } = "tcp"; // tcp, ws, grpc, h2
        public string Path { get; set; } = string.Empty;
        public string ServiceName { get; set; } = string.Empty;
        public string CountryCode { get; set; } = "UN";
    }

    public class RoutingRule
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Target { get; set; } = string.Empty; // "discord.exe", "domain:youtube.com", "geoip:ru"
        public RuleType Type { get; set; } = RuleType.Domain;
        public RuleAction Action { get; set; } = RuleAction.Proxy;
        public string Description { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
    }

    public class LatencyResult
    {
        public int TcpPingMs { get; set; } = -1;
        public int RealHttpDelayMs { get; set; } = -1;
        public double PacketLossRate { get; set; } = 0.0;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public bool IsSuccess => RealHttpDelayMs >= 0 || TcpPingMs >= 0;
        public int DisplayDelay => RealHttpDelayMs >= 0 ? RealHttpDelayMs : TcpPingMs;
    }
}
