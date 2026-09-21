using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetCat.Core;

public enum RuleKind { Domain, ExactDomain, Process, ExecutablePath, IpCidr, GeoSite, GeoIp }
public enum RouteTarget { Direct, Vpn, OpenVpn, Block }
public enum ServiceRoute { Vpn, Zapret }
public enum RoutingMode { Rules, Global, SelectiveVpn, SelectiveDirect }

public sealed class Profile : System.ComponentModel.INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Новый профиль";
    public string Protocol { get; set; } = "vless";
    public string Core { get; set; } = "sing-box";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 443;
    public string OutboundJson { get; set; } = "{}";
    public string OpenVpnConfig { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public Guid? SubscriptionId { get; set; }
    public string SubscriptionItemId { get; set; } = "";
    public bool Candidate { get; set; } = true;
    private string result = "Не проверен";
    [JsonIgnore] public string Result { get => result; set { result = value; PropertyChanged?.Invoke(this,new(nameof(Result))); } }
    private string resultDetails = "Проверка ещё не выполнялась.";
    [JsonIgnore] public string ResultDetails { get => resultDetails; set { resultDetails = value; PropertyChanged?.Invoke(this,new(nameof(ResultDetails))); } }
    public void SetTestResult(DelayResult tested)
    {
        Result = TestFeedback.Summary(tested);
        ResultDetails = DateTime.Now.ToString("dd.MM HH:mm:ss") + " · " + Core + Environment.NewLine + (tested.Success ? $"HTTP-ответ получен за {tested.Milliseconds} мс." : tested.Error);
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    [JsonIgnore] public bool IsOpenVpn => Protocol.Equals("openvpn", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => $"{Name} · {Protocol}";
}
public sealed class RoutingRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public RuleKind Kind { get; set; }
    public string Value { get; set; } = "";
    public RouteTarget Target { get; set; } = RouteTarget.Direct;
    public string Network { get; set; } = "";
    public string Port { get; set; } = "";
}
public sealed class Subscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Подписка";
    public string Url { get; set; } = "";
    public DateTimeOffset? UpdatedAt { get; set; }
    public int UpdateHours { get; set; } = 24;
    public override string ToString() => Name;
}
public sealed class AppSettings : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void SetField<T>(ref T field,T value,[System.Runtime.CompilerServices.CallerMemberName]string? name=null)
    {
        if(EqualityComparer<T>.Default.Equals(field,value)) return;
        field=value; PropertyChanged?.Invoke(this,new(name));
    }
    public void CopyFrom(AppSettings value)
    {
        var copy=JsonSettings.Clone(value);
        foreach(var property in typeof(AppSettings).GetProperties().Where(p=>p.CanWrite)) property.SetValue(this,property.GetValue(copy));
    }

    private int _profileFormatVersion;
    public int ProfileFormatVersion { get => _profileFormatVersion; set => SetField(ref _profileFormatVersion, value); }
    public List<Profile> Profiles { get; set; } = [];
    public List<Subscription> Subscriptions { get; set; } = [];
    public List<RoutingRule> Rules { get; set; } = [];
    private Guid? _mainProfileId;
    public Guid? MainProfileId { get => _mainProfileId; set => SetField(ref _mainProfileId, value); }
    private Guid? _openVpnProfileId;
    public Guid? OpenVpnProfileId { get => _openVpnProfileId; set => SetField(ref _openVpnProfileId, value); }
    private RoutingMode _mode;
    public RoutingMode Mode { get => _mode; set => SetField(ref _mode, value); }
    private RouteTarget _fallback = RouteTarget.Direct;
    public RouteTarget Fallback { get => _fallback; set => SetField(ref _fallback, value); }
    private ServiceRoute _youTube = ServiceRoute.Zapret;
    public ServiceRoute YouTube { get => _youTube; set => SetField(ref _youTube, value); }
    private ServiceRoute _discord = ServiceRoute.Vpn;
    public ServiceRoute Discord { get => _discord; set => SetField(ref _discord, value); }
    private bool _telegramSocks;
    public bool TelegramSocks { get => _telegramSocks; set => SetField(ref _telegramSocks, value); }
    private bool _telegramVpnDefault = true;
    public bool TelegramVpnDefault { get => _telegramVpnDefault; set => SetField(ref _telegramVpnDefault, value); }
    private string _telegramSocksHost = "127.0.0.1";
    public string TelegramSocksHost { get => _telegramSocksHost; set => SetField(ref _telegramSocksHost, value); }
    private int _telegramSocksPort = 1080;
    public int TelegramSocksPort { get => _telegramSocksPort; set => SetField(ref _telegramSocksPort, value); }
    private int _telegramWsPort = 1443;
    public int TelegramWsPort { get => _telegramWsPort; set => SetField(ref _telegramWsPort, value); }
    private string _telegramWsSecret = "";
    public string TelegramWsSecret { get => _telegramWsSecret; set => SetField(ref _telegramWsSecret, value); }
    private bool _checkModuleUpdates = true;
    public bool CheckModuleUpdates { get => _checkModuleUpdates; set => SetField(ref _checkModuleUpdates, value); }
    private bool _tun = true;
    public bool Tun { get => _tun; set => SetField(ref _tun, value); }
    private int _socksPort = 10808;
    public int SocksPort { get => _socksPort; set => SetField(ref _socksPort, value); }
    private string _physicalInterface = "";
    public string PhysicalInterface { get => _physicalInterface; set => SetField(ref _physicalInterface, value); }
    private string _directDns = "";
    public string DirectDns { get => _directDns; set => SetField(ref _directDns, value); }
    private string _openVpnDns = "";
    public string OpenVpnDns { get => _openVpnDns; set => SetField(ref _openVpnDns, value); }
    private string _localDomains = "";
    public string LocalDomains { get => _localDomains; set => SetField(ref _localDomains, value); }
    private string _openVpnDomains = "";
    public string OpenVpnDomains { get => _openVpnDomains; set => SetField(ref _openVpnDomains, value); }
    private bool _autoTest;
    public bool AutoTest { get => _autoTest; set => SetField(ref _autoTest, value); }
    private bool _autoSwitch;
    public bool AutoSwitch { get => _autoSwitch; set => SetField(ref _autoSwitch, value); }
    private int _testIntervalSeconds = 60;
    public int TestIntervalSeconds { get => _testIntervalSeconds; set => SetField(ref _testIntervalSeconds, value); }
    private int _failureThreshold = 2;
    public int FailureThreshold { get => _failureThreshold; set => SetField(ref _failureThreshold, value); }
    private int _testTimeoutSeconds = 8;
    public int TestTimeoutSeconds { get => _testTimeoutSeconds; set => SetField(ref _testTimeoutSeconds, value); }
    private string _testUrl = "https://www.gstatic.com/generate_204";
    public string TestUrl { get => _testUrl; set => SetField(ref _testUrl, value); }
    private string _accentColor = "#3975D6";
    public string AccentColor { get => _accentColor; set => SetField(ref _accentColor, value); }
    private string _baseColor = "#F4F6F8";
    public string BaseColor { get => _baseColor; set => SetField(ref _baseColor, value); }
    private bool _minimizeToTray = true;
    public bool MinimizeToTray { get => _minimizeToTray; set => SetField(ref _minimizeToTray, value); }
    private string _zapretStrategy = "";
    public string ZapretStrategy { get => _zapretStrategy; set => SetField(ref _zapretStrategy, value); }
    public Dictionary<string, string> BestZapretByScenario { get; set; } = [];
    public List<ZapretTestRecord> ZapretResults { get; set; } = [];
    private bool _applyBestZapret;
    public bool ApplyBestZapret { get => _applyBestZapret; set => SetField(ref _applyBestZapret, value); }
    public HashSet<string> PinnedModules { get; set; } = [];
    [JsonIgnore] public string Scenario => $"youtube-{YouTube}_discord-{Discord}";
}
public sealed record NetworkSnapshot(string Name, int Index, string Address, string Dns, string[] Suffixes, bool HasIpv6DefaultRoute = false, string Ipv6Address = "");
public sealed record OpenVpnLink(string Name, int Index, string Address, string Gateway, string Dns);
public sealed record DelayResult(bool Success, int Milliseconds, string Error = "");
public sealed record ZapretTestRecord(string File, string Scenario, DateTimeOffset At, string YouTube, string Discord, bool Passed, int Score, int Delay, string Details);
public static class JsonSettings
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
public static class ServiceDomains
{
    public static readonly string[] YouTube = ["youtube.com", "youtu.be", "googlevideo.com", "ytimg.com", "youtubei.googleapis.com", "youtube-nocookie.com", "ggpht.com",
        "yt3.googleusercontent.com", "jnn-pa.googleapis.com", "wide-youtube.l.google.com", "youtube-ui.l.google.com",
        "youtubeembeddedplayer.googleapis.com", "youtubekids.com", "youtube.googleapis.com", "yt-video-upload.l.google.com", "ytimg.l.google.com"];
    public static readonly string[] Discord = ["discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media", "discordcdn.com",
        "dis.gd", "discord-attachments-uploads-prd.storage.googleapis.com", "discord.app", "discord.co", "discord.design", "discord.dev",
        "discord.gift", "discord.gifts", "discord.new", "discord.store", "discord.status", "discord-activities.com", "discordactivities.com",
        "discordmerch.com", "discordpartygames.com", "discordsays.com", "discordsez.com", "discordstatus.com"];
    public static readonly string[] Telegram = ["telegram.org", "t.me", "telegram.me", "tdesktop.com", "telesco.pe"];
    public static readonly string[] TelegramIps = ["91.108.4.0/22", "91.108.8.0/22", "91.108.12.0/22", "91.108.16.0/22", "91.108.20.0/22", "91.108.56.0/22", "149.154.160.0/20", "2001:b28:f23d::/48", "2001:b28:f23f::/48", "2001:67c:4e8::/48"];
}
