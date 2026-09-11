using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetCat.Core.Enums;
using NetCat.Core.Interfaces;
using NetCat.Core.Models;
using NetCat.Engine.Management;
using NetCat.Engine.Parsers;
using NetCat.Engine.SingBox;
using NetCat.Engine.TgWsProxy;
using NetCat.Engine.Zapret;
using NetCat.Network.Failover;
using NetCat.Network.Metrics;
using NetCat.Network.Tun;

namespace NetCat.UI.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly ProcessSupervisor _supervisor = new();
        private readonly SingBoxProcess _singBox;
        private readonly ZapretProcess _zapret;
        private readonly TgWsProxyProcess _tgProxy;
        private readonly DualPathFailoverEngine _failoverEngine;
        private readonly RealDelayTester _delayTester = new();
        private readonly DispatcherTimer _telemetryTimer = new();

        private long _lastBytesReceived;
        private long _lastBytesSent;
        private DateTime _lastTelemetryTime = DateTime.UtcNow;

        [ObservableProperty]
        private bool _isConnected;

        [ObservableProperty]
        private string _statusText = "VPN Отключен";

        [ObservableProperty]
        private string _statusSubtext = "Нажмите 'Подключить' для защищённого соединения";

        [ObservableProperty]
        private double _downloadSpeed; // bytes/sec for graph

        [ObservableProperty]
        private double _uploadSpeed; // bytes/sec for graph

        [ObservableProperty]
        private string _downloadSpeedText = "0.0 KB/s";

        [ObservableProperty]
        private string _uploadSpeedText = "0.0 KB/s";

        [ObservableProperty]
        private ProxyProfile? _activeProfile;

        [ObservableProperty]
        private ProxyProfile? _backupProfile;

        [ObservableProperty]
        private bool _zapretEnabled = true;

        [ObservableProperty]
        private string _zapretStrategy = "ALT13";

        [ObservableProperty]
        private bool _tgProxyEnabled = true;

        [ObservableProperty]
        private RoutingMode _currentRoutingMode = RoutingMode.RuleBased;

        [ObservableProperty]
        private int _activePing = -1;

        [ObservableProperty]
        private string _activeCountry = "RU";

        [ObservableProperty]
        private string _logsText = "";

        [ObservableProperty]
        private int _selectedNavIndex = 0; // 0: Dashboard, 1: Profiles, 2: Rules, 3: DPI, 4: TG Proxy, 5: Logs, 6: Settings

        public ObservableCollection<ProxyProfile> Profiles { get; } = new();
        public ObservableCollection<RoutingRule> Rules { get; } = new();
        public AppSettings Settings { get; private set; } = new();

        public MainViewModel()
        {
            _singBox = new SingBoxProcess(_supervisor);
            _zapret = new ZapretProcess(_supervisor);
            _tgProxy = new TgWsProxyProcess(_supervisor);
            _failoverEngine = new DualPathFailoverEngine(_delayTester);

            _supervisor.OutputReceived += (s, e) => AppendLog($"[{e.ProcessName}] {e.Message}");
            _supervisor.ErrorReceived += (s, e) => AppendLog($"[{e.ProcessName} ERR] {e.Error}");
            _failoverEngine.OnActiveNodeChanged += (s, node) =>
            {
                ActiveProfile = node;
                ActivePing = node.LastMetrics.DisplayDelay;
                AppendLog($"[Failover] Активный узел переключен на {node.Name}");
            };

            LoadSettingsAndData();
            SetupTelemetryTimer();
        }

        private void SetupTelemetryTimer()
        {
            _telemetryTimer.Interval = TimeSpan.FromSeconds(1);
            _telemetryTimer.Tick += (s, e) => UpdateTelemetry();
            _telemetryTimer.Start();
        }

        private void UpdateTelemetry()
        {
            if (!IsConnected)
            {
                DownloadSpeed = 0;
                UploadSpeed = 0;
                DownloadSpeedText = "0.0 KB/s";
                UploadSpeedText = "0.0 KB/s";
                return;
            }

            try
            {
                long totalRecv = 0;
                long totalSent = 0;

                var wintun = WintunManager.FindWintunInterface(Settings.Network.TunInterfaceName);
                if (wintun != null)
                {
                    var stats = wintun.GetIPStatistics();
                    totalRecv = stats.BytesReceived;
                    totalSent = stats.BytesSent;
                }

                var now = DateTime.UtcNow;
                double seconds = (now - _lastTelemetryTime).TotalSeconds;
                if (seconds > 0.1 && _lastBytesReceived > 0)
                {
                    double dSpeed = Math.Max(0, (totalRecv - _lastBytesReceived) / seconds);
                    double uSpeed = Math.Max(0, (totalSent - _lastBytesSent) / seconds);

                    DownloadSpeed = dSpeed;
                    UploadSpeed = uSpeed;
                    DownloadSpeedText = FormatSpeed(dSpeed);
                    UploadSpeedText = FormatSpeed(uSpeed);
                }

                _lastBytesReceived = totalRecv;
                _lastBytesSent = totalSent;
                _lastTelemetryTime = now;
            }
            catch
            {
                // Ignored
            }
        }

        private static string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec >= 1024 * 1024 * 1024)
                return $"{bytesPerSec / (1024 * 1024 * 1024):F1} GB/s";
            if (bytesPerSec >= 1024 * 1024)
                return $"{bytesPerSec / (1024 * 1024):F1} MB/s";
            if (bytesPerSec >= 1024)
                return $"{bytesPerSec / 1024:F1} KB/s";
            return $"{bytesPerSec:F0} B/s";
        }

        public void AppendLog(string message)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                LogsText = $"[{timestamp}] {message}\n" + (LogsText.Length > 20000 ? LogsText.Substring(0, 15000) : LogsText);
            });
        }

        [RelayCommand]
        public async Task ToggleConnectionAsync()
        {
            if (IsConnected)
            {
                await DisconnectAsync();
            }
            else
            {
                await ConnectAsync();
            }
        }

        public async Task ConnectAsync()
        {
            if (ActiveProfile == null)
            {
                if (Profiles.Count > 0)
                {
                    ActiveProfile = Profiles[0];
                }
                else
                {
                    MessageBox.Show("Добавьте хотя бы один профиль или ссылку для подключения!", "NetCat", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }

            StatusText = "Подключение...";
            StatusSubtext = $"Инициализация L3 Wintun и {ActiveProfile.Name}...";

            try
            {
                // 1. Build configuration for sing-box
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "generated_sing-box.json");
                string configJson = SingBoxConfigBuilder.BuildConfig(
                    ActiveProfile,
                    BackupProfile,
                    CurrentRoutingMode,
                    Rules.ToList(),
                    Settings.Network.TunInterfaceName,
                    Settings.Network.TunAddress,
                    Settings.Network.SocksInboundPort);

                File.WriteAllText(configPath, configJson);

                // 2. Start sing-box
                _singBox.Start(configPath);
                AppendLog("[Engine] sing-box успешно запущен с Wintun TUN-адаптером");

                // 3. Start Zapret DPI bypass if enabled
                if (ZapretEnabled)
                {
                    StartZapret();
                }

                // 4. Start Telegram WS-Proxy if enabled
                if (TgProxyEnabled)
                {
                    StartTgProxy();
                }

                // 5. Register Failover
                _failoverEngine.RegisterNodes(ActiveProfile, BackupProfile);
                await _failoverEngine.StartMonitorAsync(CancellationToken.None);

                IsConnected = true;
                StatusText = "NetCat Подключен";
                StatusSubtext = $"{ActiveProfile.Protocol.ToUpperInvariant()} • {ActiveProfile.ServerAddress}:{ActiveProfile.ServerPort}";

                // Measure active ping
                _ = Task.Run(async () =>
                {
                    int ping = await _delayTester.TestTcpRttAsync(ActiveProfile.ServerAddress, ActiveProfile.ServerPort);
                    ActivePing = ping;
                    ActiveProfile.LastMetrics.TcpPingMs = ping;
                });
            }
            catch (Exception ex)
            {
                AppendLog($"[Error] Ошибка подключения: {ex.Message}");
                MessageBox.Show($"Ошибка запуска: {ex.Message}", "Ошибка NetCat", MessageBoxButton.OK, MessageBoxImage.Error);
                await DisconnectAsync();
            }
        }

        public async Task DisconnectAsync()
        {
            StatusText = "Отключение...";
            try
            {
                _failoverEngine.StopMonitor();
                _singBox.Stop();
                RouteTableManager.ClearTrackedRoutes();
                RouteTableManager.FlushDns();

                IsConnected = false;
                StatusText = "VPN Отключен";
                StatusSubtext = "Трафик идёт напрямую через провайдера";
                ActivePing = -1;
                AppendLog("[Engine] Соединение разорвано, Wintun адаптер сброшен");
            }
            catch (Exception ex)
            {
                AppendLog($"[Error] Ошибка отключения: {ex.Message}");
            }
            await Task.CompletedTask;
        }

        [RelayCommand]
        public void ToggleZapret()
        {
            if (ZapretEnabled)
            {
                StartZapret();
            }
            else
            {
                _zapret.Stop();
                AppendLog("[Zapret] DPI Bypass остановлен");
            }
        }

        private void StartZapret()
        {
            try
            {
                _zapret.Start(ZapretStrategy, Settings.Zapret.CustomArgs);
                AppendLog($"[Zapret] Winws DPI Bypass запущен (Стратегия: {ZapretStrategy})");
            }
            catch (Exception ex)
            {
                AppendLog($"[Zapret ERR] {ex.Message}");
            }
        }

        [RelayCommand]
        public void ToggleTgProxy()
        {
            if (TgProxyEnabled)
            {
                StartTgProxy();
            }
            else
            {
                _tgProxy.Stop();
                AppendLog("[TG-Proxy] Сервис остановлен");
            }
        }

        private void StartTgProxy()
        {
            try
            {
                _tgProxy.Start(Settings.TgProxy.ListenPort, Settings.TgProxy.TargetHost);
                AppendLog($"[TG-Proxy] Telegram WS Proxy запущен на порту 127.0.0.1:{Settings.TgProxy.ListenPort}");
            }
            catch (Exception ex)
            {
                AppendLog($"[TG-Proxy ERR] {ex.Message}");
            }
        }

        [RelayCommand]
        public void ImportFromClipboard()
        {
            try
            {
                string text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text))
                {
                    MessageBox.Show("Буфер обмена пуст!", "NetCat", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var imported = SubscriptionParser.ParseSubscriptionContent(text);
                if (imported.Count == 0)
                {
                    var single = ProtocolParser.Parse(text);
                    if (single != null) imported.Add(single);
                }

                if (imported.Count > 0)
                {
                    foreach (var p in imported)
                    {
                        Profiles.Add(p);
                    }
                    if (ActiveProfile == null)
                    {
                        ActiveProfile = imported[0];
                    }
                    SaveProfiles();
                    MessageBox.Show($"Успешно импортировано профилей: {imported.Count}", "NetCat", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Не удалось распознать формат ссылки (поддерживаются vless://, vmess://, ss://, trojan://, hysteria2://)", "NetCat", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка импорта: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        public async Task TestAllPingsAsync()
        {
            foreach (var profile in Profiles)
            {
                profile.LastMetrics.TcpPingMs = await _delayTester.TestTcpRttAsync(profile.ServerAddress, profile.ServerPort);
            }
        }

        [RelayCommand]
        public void SetPrimaryProfile(ProxyProfile profile)
        {
            foreach (var p in Profiles) p.IsPrimary = (p == profile);
            ActiveProfile = profile;
            Settings.Network.ActiveProfileId = profile.Id;
            SaveSettingsAndData();
        }

        [RelayCommand]
        public void SetBackupProfile(ProxyProfile profile)
        {
            foreach (var p in Profiles) p.IsBackup = (p == profile);
            BackupProfile = profile;
            Settings.Network.BackupProfileId = profile.Id;
            SaveSettingsAndData();
        }

        [RelayCommand]
        public void DeleteProfile(ProxyProfile profile)
        {
            Profiles.Remove(profile);
            if (ActiveProfile == profile)
            {
                ActiveProfile = Profiles.FirstOrDefault();
            }
            if (BackupProfile == profile)
            {
                BackupProfile = null;
            }
            SaveProfiles();
        }

        private void LoadSettingsAndData()
        {
            try
            {
                string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                Directory.CreateDirectory(dataDir);

                // Settings
                string settingsPath = Path.Combine(dataDir, "appsettings.json");
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }

                ZapretEnabled = Settings.Zapret.Enabled;
                ZapretStrategy = Settings.Zapret.Strategy;
                TgProxyEnabled = Settings.TgProxy.Enabled;
                CurrentRoutingMode = Settings.Network.RoutingMode;

                // Rules
                string rulesPath = Path.Combine(dataDir, "routing_rules.json");
                if (File.Exists(rulesPath))
                {
                    string json = File.ReadAllText(rulesPath);
                    var list = JsonSerializer.Deserialize<ObservableCollection<RoutingRule>>(json);
                    if (list != null)
                    {
                        foreach (var r in list) Rules.Add(r);
                    }
                }

                // Profiles
                string profilesPath = Path.Combine(dataDir, "profiles.json");
                if (File.Exists(profilesPath))
                {
                    string json = File.ReadAllText(profilesPath);
                    var list = JsonSerializer.Deserialize<ObservableCollection<ProxyProfile>>(json);
                    if (list != null)
                    {
                        foreach (var p in list) Profiles.Add(p);
                    }
                }

                // If no profiles exist, add a demo VLESS Reality configuration
                if (Profiles.Count == 0)
                {
                    Profiles.Add(new ProxyProfile
                    {
                        Name = "Netherlands - VLESS Reality (Auto)",
                        Protocol = "vless",
                        ServerAddress = "nl-node.netcat-vpn.net",
                        ServerPort = 443,
                        Uuid = "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
                        Flow = "xtls-rprx-vision",
                        Security = "reality",
                        Sni = "microsoft.com",
                        Fingerprint = "chrome",
                        CountryCode = "NL",
                        IsPrimary = true
                    });
                    Profiles.Add(new ProxyProfile
                    {
                        Name = "Germany - Hysteria2 Backup",
                        Protocol = "hysteria2",
                        ServerAddress = "de-node.netcat-vpn.net",
                        ServerPort = 8443,
                        Password = "netcat-super-secure-pass",
                        Sni = "bing.com",
                        CountryCode = "DE",
                        IsBackup = true
                    });
                }

                ActiveProfile = Profiles.FirstOrDefault(p => p.IsPrimary) ?? Profiles.FirstOrDefault();
                BackupProfile = Profiles.FirstOrDefault(p => p.IsBackup);
            }
            catch (Exception ex)
            {
                AppendLog($"[Init ERR] {ex.Message}");
            }
        }

        public void SaveSettingsAndData()
        {
            try
            {
                string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                Directory.CreateDirectory(dataDir);

                Settings.Zapret.Enabled = ZapretEnabled;
                Settings.Zapret.Strategy = ZapretStrategy;
                Settings.TgProxy.Enabled = TgProxyEnabled;
                Settings.Network.RoutingMode = CurrentRoutingMode;

                File.WriteAllText(Path.Combine(dataDir, "appsettings.json"),
                    JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));

                SaveProfiles();

                File.WriteAllText(Path.Combine(dataDir, "routing_rules.json"),
                    JsonSerializer.Serialize(Rules, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private void SaveProfiles()
        {
            string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            File.WriteAllText(Path.Combine(dataDir, "profiles.json"),
                JsonSerializer.Serialize(Profiles, new JsonSerializerOptions { WriteIndented = true }));
        }

        public void Cleanup()
        {
            _telemetryTimer.Stop();
            _supervisor.Dispose();
            RouteTableManager.ClearTrackedRoutes();
            RouteTableManager.FlushDns();
        }
    }
}
