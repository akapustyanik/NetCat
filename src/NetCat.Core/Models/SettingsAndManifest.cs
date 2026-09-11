using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using NetCat.Core.Enums;

namespace NetCat.Core.Models
{
    public class ManifestModel
    {
        [JsonPropertyName("app_version")]
        public string AppVersion { get; set; } = "0.5.0";

        [JsonPropertyName("last_check")]
        public DateTime LastCheck { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("modules")]
        public Dictionary<string, ModuleMetadata> Modules { get; set; } = new();
    }

    public class ModuleMetadata
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("repo")]
        public string Repo { get; set; } = string.Empty;

        [JsonPropertyName("asset_pattern")]
        public string AssetPattern { get; set; } = string.Empty;

        [JsonPropertyName("binary_path")]
        public string BinaryPath { get; set; } = string.Empty;

        [JsonPropertyName("expected_sha256")]
        public string ExpectedSha256 { get; set; } = string.Empty;

        [JsonPropertyName("pinned")]
        public bool Pinned { get; set; } = false;
    }

    public class AppSettings
    {
        public GeneralSettings General { get; set; } = new();
        public NetworkSettings Network { get; set; } = new();
        public ZapretSettings Zapret { get; set; } = new();
        public TgProxySettings TgProxy { get; set; } = new();
        public FailoverSettings Failover { get; set; } = new();
    }

    public class GeneralSettings
    {
        public string Theme { get; set; } = "Dark";
        public string AccentColor { get; set; } = "#06B6D4";
        public bool MinimizeToTrayOnClose { get; set; } = true;
        public bool StartMinimized { get; set; } = false;
        public bool AutoStartWithWindows { get; set; } = false;
        public bool RunAsAdmin { get; set; } = true;
        public string Language { get; set; } = "ru-RU";
    }

    public class NetworkSettings
    {
        public RoutingMode RoutingMode { get; set; } = RoutingMode.RuleBased;
        public FailoverStrategy FailoverStrategy { get; set; } = FailoverStrategy.ActiveActiveRacing;
        public Guid? ActiveProfileId { get; set; }
        public Guid? BackupProfileId { get; set; }
        public string DnsRemote { get; set; } = "tcp://1.1.1.1";
        public string DnsDirect { get; set; } = "local";
        public string TunInterfaceName { get; set; } = "wintun-netcat";
        public string TunAddress { get; set; } = "172.19.0.1/30";
        public int SocksInboundPort { get; set; } = 10808;
        public int HttpInboundPort { get; set; } = 10809;
        public int XrayBridgePort { get; set; } = 10850;
        public int OpenVpnBridgePort { get; set; } = 10851;
        public int TgProxyPort { get; set; } = 10852;
    }

    public class ZapretSettings
    {
        public bool Enabled { get; set; } = true;
        public string Strategy { get; set; } = "ALT13"; // ALT13, general, discord, youtube
        public string CustomArgs { get; set; } = string.Empty;
        public List<int> FilterDstPorts { get; set; } = new() { 80, 443 };
    }

    public class TgProxySettings
    {
        public bool Enabled { get; set; } = true;
        public int ListenPort { get; set; } = 10852;
        public string TargetHost { get; set; } = "149.154.167.50";
        public bool AutoStart { get; set; } = true;
    }

    public class FailoverSettings
    {
        public bool RacingEnabled { get; set; } = true;
        public int ProbeIntervalSeconds { get; set; } = 5;
        public int ConsecutiveLossThreshold { get; set; } = 2;
        public string ProbeUrl { get; set; } = "http://cp.cloudflare.com/generate_204";
        public int TimeoutMs { get; set; } = 2500;
    }
}
