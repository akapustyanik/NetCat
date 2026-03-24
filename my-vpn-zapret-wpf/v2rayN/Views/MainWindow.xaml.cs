using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Net;
using System.Net.Http;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using MaterialDesignColors;
using MaterialDesignColors.ColorManipulation;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using ServiceLib.Handler;
using ServiceLib.Handler.SysProxy;
using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.Services;
using ServiceLib.ViewModels;
using v2rayN.Base;
using v2rayN.Models;

namespace v2rayN.Views;

public partial class MainWindow : WindowBase<MainWindowViewModel>, INotifyPropertyChanged
{
    private const double CustomColorPlaneWidth = 260;
    private const double CustomColorPlaneHeight = 160;
    private const int SecretAutoRunClickThreshold = 7;
    private const string DefaultInterfacePresetKey = "NightShift";
    private const string SecretAssetName = "secret.dat";
    private static readonly byte[] SecretKey = Encoding.UTF8.GetBytes("NetCat::secret::2026");

    private readonly Config _config;
    private readonly PaletteHelper _paletteHelper = new();
    private QuickRuleConfig _quickRules;
    private readonly DispatcherTimer _connectionPingTimer;
    private bool _closing;
    private bool _isPickingCustomColor;
    private bool _isUpdatingConnectionPing;
    private bool _isRefreshingZapretConfigs;
    private bool _isSwitchingZapretConfig;
    private bool _isAutoTestingZapret;
    private bool _startupUiHandled;
    private bool _startupZapretRestorePending = true;
    private bool _startupUpdateCheckStarted;
    private bool _suppressConnectionToggleEvents;
    private int _autoRunSecretClickCount;
    private CancellationTokenSource? _zapretAutoTestCts;
    private Task? _zapretAutoTestTask;
    private RegisteredWaitHandle? _singleInstanceWaitHandle;

    public ObservableCollection<ProfileItemModel> Profiles { get; } = new();
    public ObservableCollection<string> DirectApps { get; } = new();
    public ObservableCollection<string> DirectDomains { get; } = new();
    public ObservableCollection<string> ProxyApps { get; } = new();
    public ObservableCollection<string> ProxyDomains { get; } = new();
    public ObservableCollection<string> BlockDomains { get; } = new();
    public ObservableCollection<ZapretConfigItem> ZapretConfigs { get; } = new();
    public ObservableCollection<RunningProcessItem> RunningProcesses { get; } = new();
    public ObservableCollection<PrimaryColorOption> PrimaryColors { get; } = new();
    public ObservableCollection<InterfaceVariantOption> InterfaceVariants { get; } = new();
    public ICollectionView RunningProcessesView { get; }

    private ProfileItemModel? _selectedProfile;
    public ProfileItemModel? SelectedProfile
    {
        get => _selectedProfile;
        set => SetField(ref _selectedProfile, value);
    }

    private string? _selectedApp;
    public string? SelectedApp
    {
        get => _selectedApp;
        set => SetField(ref _selectedApp, value);
    }

    private string? _selectedDomain;
    public string? SelectedDomain
    {
        get => _selectedDomain;
        set => SetField(ref _selectedDomain, value);
    }

    private string? _selectedBlockedDomain;
    public string? SelectedBlockedDomain
    {
        get => _selectedBlockedDomain;
        set => SetField(ref _selectedBlockedDomain, value);
    }

    private string? _selectedProxyDomain;
    public string? SelectedProxyDomain
    {
        get => _selectedProxyDomain;
        set => SetField(ref _selectedProxyDomain, value);
    }

    private string? _selectedProxyApp;
    public string? SelectedProxyApp
    {
        get => _selectedProxyApp;
        set => SetField(ref _selectedProxyApp, value);
    }

    private string _inputLink = string.Empty;
    public string InputLink
    {
        get => _inputLink;
        set => SetField(ref _inputLink, value);
    }

    private string _newDomain = string.Empty;
    public string NewDomain
    {
        get => _newDomain;
        set => SetField(ref _newDomain, value);
    }

    private string _newBlockedDomain = string.Empty;
    public string NewBlockedDomain
    {
        get => _newBlockedDomain;
        set => SetField(ref _newBlockedDomain, value);
    }

    private string _newProxyDomain = string.Empty;
    public string NewProxyDomain
    {
        get => _newProxyDomain;
        set => SetField(ref _newProxyDomain, value);
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    private string _updateBannerMessage = string.Empty;
    public string UpdateBannerMessage
    {
        get => _updateBannerMessage;
        set => SetField(ref _updateBannerMessage, value);
    }

    private Visibility _updateBannerVisibility = Visibility.Collapsed;
    public Visibility UpdateBannerVisibility
    {
        get => _updateBannerVisibility;
        set => SetField(ref _updateBannerVisibility, value);
    }

    private string _systemStatusSummary = string.Empty;
    public string SystemStatusSummary
    {
        get => _systemStatusSummary;
        set => SetField(ref _systemStatusSummary, value);
    }

    private string _serverPing = string.Empty;
    public string ServerPing
    {
        get => _serverPing;
        set => SetField(ref _serverPing, value);
    }

    private string _debugLog = string.Empty;
    public string DebugLog
    {
        get => _debugLog;
        set => SetField(ref _debugLog, value);
    }

    private string _zapretStatus = string.Empty;
    public string ZapretStatus
    {
        get => _zapretStatus;
        set => SetField(ref _zapretStatus, value);
    }

    private string _zapretPath = string.Empty;
    public string ZapretPath
    {
        get => _zapretPath;
        set => SetField(ref _zapretPath, value);
    }

    private ZapretConfigItem? _selectedZapretConfig;
    public ZapretConfigItem? SelectedZapretConfig
    {
        get => _selectedZapretConfig;
        set
        {
            if (SetField(ref _selectedZapretConfig, value)
                && !_isRefreshingZapretConfigs
                && !_isSwitchingZapretConfig
                && IsLoaded
                && ZapretRunning
                && value?.Name.IsNullOrEmpty() == false)
            {
                _ = SwitchZapretConfigAsync(value.Name);
            }
        }
    }

    private RunningProcessItem? _selectedRunningProcess;
    public RunningProcessItem? SelectedRunningProcess
    {
        get => _selectedRunningProcess;
        set => SetField(ref _selectedRunningProcess, value);
    }

    private string _runningProcessSearchText = string.Empty;
    public string RunningProcessSearchText
    {
        get => _runningProcessSearchText;
        set
        {
            if (SetField(ref _runningProcessSearchText, value))
            {
                ApplyRunningProcessFilter();
            }
        }
    }

    private bool _zapretRunning;
    public bool ZapretRunning
    {
        get => _zapretRunning;
        set => SetField(ref _zapretRunning, value);
    }

    private bool _autoRun;
    public bool AutoRun
    {
        get => _autoRun;
        set => SetField(ref _autoRun, value);
    }

    private bool _hideToTrayOnClose;
    public bool HideToTrayOnClose
    {
        get => _hideToTrayOnClose;
        set => SetField(ref _hideToTrayOnClose, value);
    }

    private bool _bypassPrivate = true;
    public bool BypassPrivate
    {
        get => _bypassPrivate;
        set => SetField(ref _bypassPrivate, value);
    }

    private bool _proxyOnlyMode;
    public bool ProxyOnlyMode
    {
        get => _proxyOnlyMode;
        set => SetField(ref _proxyOnlyMode, value);
    }

    private bool _useProxyDomainsPreset;
    public bool UseProxyDomainsPreset
    {
        get => _useProxyDomainsPreset;
        set => SetField(ref _useProxyDomainsPreset, value);
    }

    private bool _vpnEnabled;
    public bool VpnEnabled
    {
        get => _vpnEnabled;
        set => SetField(ref _vpnEnabled, value);
    }

    private bool _tunEnabled;
    public bool TunEnabled
    {
        get => _tunEnabled;
        set => SetField(ref _tunEnabled, value);
    }

    private bool _mainVpnEnabled;
    public bool MainVpnEnabled
    {
        get => _mainVpnEnabled;
        set
        {
            if (SetField(ref _mainVpnEnabled, value))
            {
                UpdateTrayToolTip();
            }
        }
    }

    private bool _encryptAllTraffic;
    public bool EncryptAllTraffic
    {
        get => _encryptAllTraffic;
        set
        {
            if (SetField(ref _encryptAllTraffic, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionModeSummary)));
                UpdateTrayToolTip();
            }
        }
    }

    public string EncryptionModeSummary => EncryptAllTraffic
        ? "По умолчанию весь трафик идёт через сервер через полный туннель."
        : "Через сервер идёт только системный прокси, остальной трафик идёт напрямую.";

    private bool _zapretEnabled;
    public bool ZapretEnabled
    {
        get => _zapretEnabled;
        set => SetField(ref _zapretEnabled, value);
    }

    private PrimaryColorOption? _selectedPrimaryColor;
    public PrimaryColorOption? SelectedPrimaryColor
    {
        get => _selectedPrimaryColor;
        set
        {
            if (SetField(ref _selectedPrimaryColor, value))
            {
                NotifyCustomColorStateChanged();
            }
        }
    }

    private bool _useCustomPrimaryColor;
    public bool UseCustomPrimaryColor
    {
        get => _useCustomPrimaryColor;
        set
        {
            if (SetField(ref _useCustomPrimaryColor, value))
            {
                NotifyCustomColorStateChanged();
            }
        }
    }

    private InterfaceVariantOption? _selectedInterfaceVariant;
    public InterfaceVariantOption? SelectedInterfaceVariant
    {
        get => _selectedInterfaceVariant;
        set
        {
            if (SetField(ref _selectedInterfaceVariant, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentInterfaceVariantTitle)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentInterfaceVariantDescription)));
            }
        }
    }

    public string CurrentInterfaceVariantTitle => SelectedInterfaceVariant?.Title ?? "Night Shift";
    public string CurrentInterfaceVariantDescription => SelectedInterfaceVariant?.Description ?? "Dark compact workspace with restrained contrast and cleaner focus.";

    private double _customHue = 220;
    public double CustomHue
    {
        get => _customHue;
        set
        {
            if (SetField(ref _customHue, value))
            {
                NotifyCustomColorStateChanged();
            }
        }
    }

    private double _customSaturation = 1;
    public double CustomSaturation
    {
        get => _customSaturation;
        set
        {
            if (SetField(ref _customSaturation, value))
            {
                NotifyCustomColorStateChanged();
            }
        }
    }

    private double _customValue = 1;
    public double CustomValue
    {
        get => _customValue;
        set
        {
            if (SetField(ref _customValue, value))
            {
                NotifyCustomColorStateChanged();
            }
        }
    }

    public Brush CustomPrimaryBaseBrush => new SolidColorBrush(ColorFromHsv(CustomHue, 1, 1));
    public Brush CustomPrimaryPreviewBrush => new SolidColorBrush(GetSelectedPrimaryColor());
    public string CustomPrimaryColorHex => $"#{GetSelectedPrimaryColor().R:X2}{GetSelectedPrimaryColor().G:X2}{GetSelectedPrimaryColor().B:X2}";
    public double CustomColorCursorLeft => Math.Clamp(CustomSaturation * CustomColorPlaneWidth - 6, -6, CustomColorPlaneWidth - 6);
    public double CustomColorCursorTop => Math.Clamp((1 - CustomValue) * CustomColorPlaneHeight - 6, -6, CustomColorPlaneHeight - 6);

    private string _connectionPing = "Connection ping: --";
    public string ConnectionPing
    {
        get => _connectionPing;
        set
        {
            if (SetField(ref _connectionPing, value))
            {
                UpdateTrayToolTip();
            }
        }
    }

    private string _trayToolTip = "NetCat";
    public string TrayToolTip
    {
        get => _trayToolTip;
        set => SetField(ref _trayToolTip, value);
    }

    private string _diagnosticOverview = string.Empty;
    public string DiagnosticOverview
    {
        get => _diagnosticOverview;
        set => SetField(ref _diagnosticOverview, value);
    }

    private string _dataLayoutSummary = string.Empty;
    public string DataLayoutSummary
    {
        get => _dataLayoutSummary;
        set => SetField(ref _dataLayoutSummary, value);
    }

    private string _startupUpdateStatus = "Update check: pending";
    public string StartupUpdateStatus
    {
        get => _startupUpdateStatus;
        set => SetField(ref _startupUpdateStatus, value);
    }

    public string AppVersion => $"v{Utils.GetVersionInfo()}";

    public MainWindow()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;
        _quickRules = QuickRuleHandler.Load();
        RunningProcessesView = CollectionViewSource.GetDefaultView(RunningProcesses);
        RunningProcessesView.Filter = FilterRunningProcess;

        ViewModel = new MainWindowViewModel((_, _) => Task.FromResult(false));
        DataContext = this;

        AutoRun = _config.GuiItem.AutoRun;
        HideToTrayOnClose = _config.UiItem.Hide2TrayWhenClose;
        BypassPrivate = _quickRules.BypassPrivate;
        ProxyOnlyMode = _quickRules.ProxyOnlyMode;
        UseProxyDomainsPreset = _quickRules.UseProxyDomainsPreset;
        TunEnabled = _config.TunModeItem.EnableTun;
        LoadInterfaceVariants();
        SelectedInterfaceVariant = InterfaceVariants.FirstOrDefault(t => string.Equals(t.Key, _config.UiItem.MainWindowPreset, StringComparison.OrdinalIgnoreCase))
            ?? InterfaceVariants.FirstOrDefault();
        LoadCustomAppearance();
        ApplyAppearance();
        LoadQuickLists();
        RefreshRunningProcesses();
        VpnEnabled = _config.SystemProxyItem.SysProxyType == ESysProxyType.ForcedChange;
        EncryptAllTraffic = _config.UiItem.PreferFullTrafficVpn || (!VpnEnabled && TunEnabled);
        MainVpnEnabled = VpnEnabled || TunEnabled;
        if (ShouldHideWindowOnStartup())
        {
            WindowState = WindowState.Minimized;
        }

        _ = ApplyQuickRulesAsync(reload: false);
        _ = RefreshZapretAsync();
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        _connectionPingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(8)
        };
        _connectionPingTimer.Tick += ConnectionPingTimer_Tick;
        _connectionPingTimer.Start();
        RegisterSingleInstanceRestore();
        _ = RefreshProfilesAsync();
        UpdateTrayToolTip();
        _ = UpdateConnectionPingAsync();
        _ = RefreshSupportSnapshotAsync(false);
    }

    protected override async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        base.OnLoaded(sender, e);
        if (_startupUiHandled)
        {
            return;
        }

        _startupUiHandled = true;
        if (ShouldHideWindowOnStartup())
        {
            HideWindowToTray();
        }

        await RefreshZapretAsync();
        await RefreshSupportSnapshotAsync(true);
        if (!_startupUpdateCheckStarted)
        {
            _startupUpdateCheckStarted = true;
            _ = CheckStartupGuiUpdateAsync();
        }
    }

    private void LoadQuickLists()
    {
        DirectApps.Clear();
        DirectDomains.Clear();
        ProxyApps.Clear();
        ProxyDomains.Clear();
        BlockDomains.Clear();

        foreach (var app in _quickRules.DirectProcesses.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(app))
            {
                DirectApps.Add(app);
            }
        }

        foreach (var domain in _quickRules.DirectDomains.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(domain))
            {
                DirectDomains.Add(domain);
            }
        }

        foreach (var domain in _quickRules.BlockDomains.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(domain))
            {
                BlockDomains.Add(domain);
            }
        }

        foreach (var app in _quickRules.ProxyProcesses.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(app))
            {
                ProxyApps.Add(app);
            }
        }

        foreach (var domain in _quickRules.ProxyDomains.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(domain))
            {
                ProxyDomains.Add(domain);
            }
        }
    }

    private async Task RefreshProfilesAsync()
    {
        var items = await AppManager.Instance.ProfileModels("", "") ?? new List<ProfileItemModel>();
        var preferredIndexId = SelectedProfile?.IndexId ?? _config.IndexId;
        foreach (var item in items)
        {
            item.IsActive = item.IndexId == _config.IndexId;
        }

        Profiles.Clear();
        foreach (var item in items.OrderBy(t => t.Sort))
        {
            Profiles.Add(item);
        }

        SelectedProfile = Profiles.FirstOrDefault(t => t.IndexId == preferredIndexId)
            ?? Profiles.FirstOrDefault(t => t.IsActive)
            ?? Profiles.FirstOrDefault();
        await UpdateConnectionPingAsync();
        await RefreshSupportSnapshotAsync(false);
    }

    private async Task ApplyQuickRulesAsync(bool reload)
    {
        _quickRules.DirectProcesses = DirectApps
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _quickRules.DirectDomains = DirectDomains
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _quickRules.ProxyProcesses = ProxyApps
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _quickRules.BlockDomains = BlockDomains
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _quickRules.ProxyDomains = ProxyDomains
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _quickRules.UseProxyDomainsPreset = UseProxyDomainsPreset;
        _quickRules.ProxyOnlyMode = ProxyOnlyMode;
        _quickRules.BypassPrivate = BypassPrivate;

        await QuickRuleHandler.Apply(_config, _quickRules);

        if (reload)
        {
            if (VpnEnabled || TunEnabled)
            {
                await CoreManager.Instance.CoreStop();
                await Task.Delay(400);
            }

            await ViewModel.Reload();
        }
    }

    private void SetStatus(string message)
    {
        StatusMessage = message;
        UpdateTrayToolTip();
    }

    private void SetZapretStatus(string message)
    {
        ZapretStatus = message;
    }

    private async Task RefreshSupportSnapshotAsync(bool refreshDebugLog)
    {
        SystemStatusSummary = await BuildSystemStatusSummaryAsync();
        DataLayoutSummary = BuildDataLayoutSummary();
        DiagnosticOverview = await BuildDiagnosticOverviewAsync();

        if (refreshDebugLog || string.IsNullOrWhiteSpace(DebugLog))
        {
            DebugLog = await BuildDebugInfoAsync();
        }
    }

    private string BuildDataLayoutSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Mode: separated install and user data");
        sb.AppendLine($"Install root: {Utils.StartupPath()}");
        sb.AppendLine($"User data root: {Utils.GetUserDataPath()}");
        sb.AppendLine($"Config file: {Utils.GetConfigPath(Global.ConfigFileName)}");
        sb.AppendLine($"Log folder: {Utils.GetLogPath()}");
        sb.AppendLine($"Temp folder: {Utils.GetTempPath()}");
        sb.AppendLine($"Generated configs: {Utils.GetBinConfigPath()}");
        sb.AppendLine($"Updater: {Utils.GetUpgradeAppPath()}");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> BuildDiagnosticOverviewAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Version: {AppVersion}");
        sb.AppendLine($"Profiles: {Profiles.Count}");
        sb.AppendLine($"VPN: {(VpnEnabled ? "enabled" : "disabled")}");
        sb.AppendLine($"TUN: {(TunEnabled ? "enabled" : "disabled")}");
        sb.AppendLine($"Zapret: {(ZapretRunning ? "running" : "stopped")}");
        sb.AppendLine($"Updater: {(Utils.UpgradeAppExists(out var updaterPath) ? "ready" : "missing")}");
        sb.AppendLine($"Updater path: {updaterPath}");
        sb.AppendLine($"Xray core: {(await CoreExistsAsync(ECoreType.Xray) ? "ready" : "missing")}");
        sb.AppendLine($"sing-box core: {(await CoreExistsAsync(ECoreType.sing_box) ? "ready" : "missing")}");
        sb.AppendLine($"Zapret folder: {(ZapretPath.IsNullOrEmpty() ? "missing" : ZapretPath)}");
        sb.AppendLine($"Connection: {ConnectionPing}");
        return sb.ToString().TrimEnd();
    }

    private Task<string> BuildSystemStatusSummaryAsync()
    {
        var updaterReady = Utils.UpgradeAppExists(out var updaterPath);
        var resolvedZapretPath = ZapretPath.IsNullOrEmpty()
            ? ZapretHandler.FindZapretPath(_config.GuiItem.ZapretPath) ?? string.Empty
            : ZapretPath;
        var hiddenLaunchers = resolvedZapretPath.IsNullOrEmpty()
            ? 0
            : ZapretHandler.CountHiddenLaunchBats(resolvedZapretPath);
        var staleUpdaterDirs = Utils.CountStaleUpdaterDirectories();
        var tempStats = Utils.GetDirectoryStats(Utils.GetTempPath(), "*", SearchOption.AllDirectories);
        var logStats = Utils.GetDirectoryStats(Utils.GetLogPath(), "*", SearchOption.TopDirectoryOnly);
        var updaterLogStats = Utils.GetDirectoryStats(Utils.GetInstallLogPath(), "updater-*.log", SearchOption.TopDirectoryOnly);
        var latestError = Utils.ReadLatestLogError() ?? "none";

        var sb = new StringBuilder();
        sb.AppendLine(StartupUpdateStatus);
        sb.AppendLine($"Updater: {(updaterReady ? "ready" : "missing")}");
        sb.AppendLine($"Updater path: {updaterPath}");
        sb.AppendLine($"Zapret path: {(resolvedZapretPath.IsNullOrEmpty() ? "missing" : resolvedZapretPath)}");
        sb.AppendLine($"Zapret config: {SelectedZapretConfig?.Name ?? "none"}");
        sb.AppendLine($"Zapret hidden launchers: {hiddenLaunchers}");
        sb.AppendLine($"Stale updater dirs: {staleUpdaterDirs}");
        sb.AppendLine($"Temp folder: {tempStats.FileCount} files, {Utils.HumanFy(tempStats.TotalBytes)}");
        sb.AppendLine($"App logs: {logStats.FileCount} files, {Utils.HumanFy(logStats.TotalBytes)}");
        sb.AppendLine($"Updater logs: {updaterLogStats.FileCount} files, {Utils.HumanFy(updaterLogStats.TotalBytes)}");
        sb.AppendLine($"Latest error: {latestError}");
        return Task.FromResult(sb.ToString().TrimEnd());
    }

    private bool CoreExists(ECoreType coreType)
    {
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
        var coreExec = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out _);
        return coreExec.IsNotEmpty() && File.Exists(coreExec);
    }

    private Task<bool> CoreExistsAsync(ECoreType coreType)
    {
        return Task.FromResult(CoreExists(coreType));
    }

    private async Task RefreshZapretAsync()
    {
        _isRefreshingZapretConfigs = true;
        try
        {
            var preferred = _config.GuiItem.ZapretPath;
            var selectedName = SelectedZapretConfig?.Name;
            var preferredConfigName = _config.GuiItem.LastZapretConfig;
            var existing = ZapretConfigs.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
            ZapretPath = ZapretHandler.FindZapretPath(preferred) ?? string.Empty;
            ZapretConfigs.Clear();
            if (!ZapretPath.IsNullOrEmpty())
            {
                foreach (var cfg in ZapretHandler.GetBatFiles(ZapretPath))
                {
                    if (!existing.TryGetValue(cfg, out var item))
                    {
                        item = new ZapretConfigItem { Name = cfg };
                    }

                    ZapretConfigs.Add(item);
                }
            }

            if (!selectedName.IsNullOrEmpty())
            {
                SelectedZapretConfig = ZapretConfigs.FirstOrDefault(t => string.Equals(t.Name, selectedName, StringComparison.OrdinalIgnoreCase));
            }

            if (SelectedZapretConfig == null && !preferredConfigName.IsNullOrEmpty())
            {
                SelectedZapretConfig = ZapretConfigs.FirstOrDefault(t => string.Equals(t.Name, preferredConfigName, StringComparison.OrdinalIgnoreCase));
            }

            if (SelectedZapretConfig == null && ZapretConfigs.Count > 0)
            {
                SelectedZapretConfig = ZapretConfigs[0];
            }

            ZapretRunning = ZapretHandler.IsRunning();
            ZapretEnabled = ZapretRunning;
            if (ZapretPath.IsNullOrEmpty() && !preferred.IsNullOrEmpty())
            {
                SetZapretStatus("Zapret path not found. Select folder or place zapret рядом с программой.");
            }
            else
            {
                SetZapretStatus(ZapretPath.IsNullOrEmpty() ? "Zapret not found" : "Zapret ready");
            }
        }
        finally
        {
            _isRefreshingZapretConfigs = false;
        }

        var shouldRestoreZapret = _startupZapretRestorePending
            && _config.GuiItem.ZapretEnabled
            && !ZapretRunning;
        _startupZapretRestorePending = false;
        if (shouldRestoreZapret)
        {
            await StartZapretAsync(persistEnabledState: false, initialStatus: "Restoring zapret...");
        }

        await Task.CompletedTask;
        await RefreshSupportSnapshotAsync(false);
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_closing && HideToTrayOnClose)
        {
            e.Cancel = true;
            HideWindowToTray();
            return;
        }

        if (_closing)
        {
            return;
        }

        _closing = true;
        e.Cancel = true;
        await AppManager.Instance.AppExitAsync(true);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _singleInstanceWaitHandle?.Unregister(null);
        _connectionPingTimer.Stop();
        TrayIcon?.Dispose();
    }

    private async void AutoRun_Checked(object sender, RoutedEventArgs e)
    {
        _config.GuiItem.AutoRun = AutoRun;
        await AutoStartupHandler.UpdateTask(_config);
        await ConfigHandler.SaveConfig(_config);
        SetStatus(AutoRun ? "Autostart enabled" : "Autostart disabled");
        TryShowAutoRunSecret();
    }

    private async void HideToTrayOnClose_Checked(object sender, RoutedEventArgs e)
    {
        _config.UiItem.Hide2TrayWhenClose = HideToTrayOnClose;
        if (AutoRun)
        {
            await AutoStartupHandler.UpdateTask(_config);
        }

        await ConfigHandler.SaveConfig(_config);
        SetStatus(HideToTrayOnClose ? "Hide to tray enabled" : "Hide to tray disabled");
    }

    private void OnOpenUpdateWindow(object sender, RoutedEventArgs e)
    {
        var window = new Window
        {
            Title = "NetCat Update",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 900,
            Height = 640,
            MinWidth = 760,
            MinHeight = 520,
            ResizeMode = ResizeMode.CanResize,
            Icon = this.Icon,
            Content = new CheckUpdateView()
        };

        window.SetResourceReference(Window.BackgroundProperty, "NetCatWindowBackgroundBrush");
        window.SetResourceReference(Window.ForegroundProperty, "NetCatStrongTextBrush");
        window.Loaded += (_, _) => WindowsUtils.SetDarkBorder(window, _config.UiItem.CurrentTheme);

        window.ShowDialog();
    }

    private void OnDismissUpdateBanner(object sender, RoutedEventArgs e)
    {
        HideUpdateBanner();
    }

    private async void BypassPrivate_Checked(object sender, RoutedEventArgs e)
    {
        await ApplyQuickRulesAsync(reload: true);
        SetStatus("Routing updated");
    }

    private async void OnEncryptAllTrafficChanged(object sender, RoutedEventArgs e)
    {
        _config.UiItem.PreferFullTrafficVpn = EncryptAllTraffic;
        await ConfigHandler.SaveConfig(_config);

        if (MainVpnEnabled)
        {
            if (EncryptAllTraffic)
            {
                if (VpnEnabled)
                {
                    await SetVpnEnabledAsync(false);
                }

                if (!TunEnabled)
                {
                    await SetTunEnabledAsync(true);
                }
            }
            else
            {
                if (TunEnabled)
                {
                    await SetTunEnabledAsync(false);
                }

                if (!VpnEnabled)
                {
                    await SetVpnEnabledAsync(true);
                }
            }
        }

        MainVpnEnabled = VpnEnabled || TunEnabled;
        SetStatus(EncryptAllTraffic
            ? "Режим VPN: полный туннель"
            : "Режим VPN: только системный прокси");
    }

    private async void OnToggleMainVpn(object sender, RoutedEventArgs e)
    {
        if (_suppressConnectionToggleEvents)
        {
            return;
        }

        await ApplyMainVpnStateAsync(MainVpnEnabled);
    }

    private async Task ApplyMainVpnStateAsync(bool enabled)
    {
        if (enabled)
        {
            if (EncryptAllTraffic)
            {
                if (VpnEnabled)
                {
                    await SetVpnEnabledAsync(false);
                }

                if (!TunEnabled)
                {
                    await SetTunEnabledAsync(true);
                }
            }
            else
            {
                if (TunEnabled)
                {
                    await SetTunEnabledAsync(false);
                }

                if (!VpnEnabled)
                {
                    await SetVpnEnabledAsync(true);
                }
            }
        }
        else
        {
            if (TunEnabled)
            {
                await SetTunEnabledAsync(false);
            }

            if (VpnEnabled)
            {
                await SetVpnEnabledAsync(false);
            }
        }

        MainVpnEnabled = VpnEnabled || TunEnabled;
    }

    private async void ProxyOnlyMode_Checked(object sender, RoutedEventArgs e)
    {
        await ApplyQuickRulesAsync(reload: true);
        SetStatus(ProxyOnlyMode ? "Selective VPN mode enabled" : "Full VPN mode enabled");
    }

    private async void UseProxyDomainsPreset_Checked(object sender, RoutedEventArgs e)
    {
        await ApplyQuickRulesAsync(reload: true);
        SetStatus(UseProxyDomainsPreset
            ? "Preset blocked domains list enabled"
            : "Preset blocked domains list disabled");
    }

    private async void OnPrimaryColorChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UseCustomPrimaryColor = true;
        ApplyAppearance();
        await ConfigHandler.SaveConfig(_config);
        SetStatus($"Accent color set to {CustomPrimaryColorHex}");
    }

    private async void OnUseCustomPrimaryColorChanged(object sender, RoutedEventArgs e)
    {
        UseCustomPrimaryColor = true;
        ApplyAppearance();
        await ConfigHandler.SaveConfig(_config);
        SetStatus($"Accent color set to {CustomPrimaryColorHex}");
    }

    private async void OnInterfaceVariantChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SelectedInterfaceVariant == null || !IsLoaded)
        {
            return;
        }

        ApplyAppearance();
        await ConfigHandler.SaveConfig(_config);
        SetStatus($"Interface preset: {SelectedInterfaceVariant.Title}");
    }

    private async Task CheckStartupGuiUpdateAsync()
    {
        try
        {
            var updateService = new UpdateService(_config, (_, _) => Task.CompletedTask);
            var result = await updateService.CheckGuiUpdateAvailability(_config.CheckUpdateItem.CheckPreReleaseUpdate);
            StartupUpdateStatus = result.Status switch
            {
                EUpdateAvailabilityStatus.Available => $"Update check: available ({result.Release?.TagName ?? result.Version?.ToString() ?? "latest"})",
                EUpdateAvailabilityStatus.UpToDate => $"Update check: up to date ({AppVersion})",
                EUpdateAvailabilityStatus.Failed => $"Update check: failed{(result.FailureStage != EUpdateFailureStage.None ? $" ({result.FailureStage})" : string.Empty)}{(result.Msg.IsNullOrEmpty() ? string.Empty : $" - {result.Msg}")}",
                _ => "Update check: no result"
            };

            if (!result.Success || result.Version == null || result.Url.IsNullOrEmpty())
            {
                await RefreshSupportSnapshotAsync(false);
                return;
            }

            var versionText = result.Release?.TagName
                ?? result.Version.ToString()
                ?? result.Msg
                ?? "latest";
            ShowUpdateBanner($"Доступно обновление NetCat {versionText}. Можно открыть окно обновления и установить его вручную.");
            await RefreshSupportSnapshotAsync(false);
        }
        catch (Exception ex)
        {
            StartupUpdateStatus = $"Update check: failed ({EUpdateFailureStage.Check}) - {ex.Message}";
            Logging.SaveLog("MainWindow.CheckStartupGuiUpdateAsync", ex);
            await RefreshSupportSnapshotAsync(false);
        }
    }

    private void ShowUpdateBanner(string message)
    {
        UpdateBannerMessage = message;
        UpdateBannerVisibility = Visibility.Visible;
    }

    private void HideUpdateBanner()
    {
        UpdateBannerVisibility = Visibility.Collapsed;
        UpdateBannerMessage = string.Empty;
    }

    private async void OnCustomHueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (!UseCustomPrimaryColor)
        {
            return;
        }

        ApplyAppearance();
        await ConfigHandler.SaveConfig(_config);
    }

    private async void OnCustomColorPlaneMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isPickingCustomColor = true;
        if (sender is IInputElement inputElement)
        {
            Mouse.Capture(inputElement);
        }

        if (sender is IInputElement activeInputElement)
        {
            UpdateCustomColorFromPoint(e.GetPosition(activeInputElement));
        }
        UseCustomPrimaryColor = true;
        ApplyAppearance();
        await ConfigHandler.SaveConfig(_config);
    }

    private async void OnCustomColorPlaneMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPickingCustomColor)
        {
            return;
        }

        if (sender is IInputElement activeInputElement)
        {
            UpdateCustomColorFromPoint(e.GetPosition(activeInputElement));
        }
        if (!UseCustomPrimaryColor)
        {
            UseCustomPrimaryColor = true;
        }

        ApplyAppearance();
        await ConfigHandler.SaveConfig(_config);
    }

    private void OnCustomColorPlaneMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isPickingCustomColor = false;
        Mouse.Capture(null);
    }

    private async void OnToggleVpn(object sender, RoutedEventArgs e)
    {
        if (_suppressConnectionToggleEvents)
        {
            return;
        }

        await HandleVpnToggleAsync();
    }

    private async Task HandleVpnToggleAsync()
    {
        if (VpnEnabled)
        {
            var ready = await EnsureActiveCoreReadyAsync();

            if (!ready)
            {
                VpnEnabled = false;
                return;
            }

            await EnsureInboundPortAvailableAsync();

            _config.SystemProxyItem.SysProxyType = ESysProxyType.ForcedChange;
            await ConfigHandler.SaveConfig(_config);
            await ViewModel.Reload();
            await SysProxyHandler.UpdateSysProxy(_config, false);
            await Task.Delay(800);
            var running = Process.GetProcessesByName("xray").Length > 0
                          || Process.GetProcessesByName("sing-box").Length > 0
                          || Process.GetProcessesByName("mihomo").Length > 0;
            MainVpnEnabled = VpnEnabled || TunEnabled;
            await UpdateConnectionPingAsync();
            SetStatus(running ? "Прокси через VPN включен" : "Прокси включен, но core не запущен");
        }
        else
        {
            _config.SystemProxyItem.SysProxyType = ESysProxyType.ForcedClear;
            await ConfigHandler.SaveConfig(_config);

            if (TunEnabled)
            {
                await ViewModel.Reload();
            }
            else
            {
                await CoreManager.Instance.CoreStop();
            }

            await SysProxyHandler.UpdateSysProxy(_config, true);
            MainVpnEnabled = VpnEnabled || TunEnabled;
            await UpdateConnectionPingAsync();
            SetStatus(TunEnabled ? "Прокси выключен, полный туннель остаётся активным" : "Прокси выключен");
        }
    }

    private async void OnAddLink(object sender, RoutedEventArgs e)
    {
        var link = InputLink?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(link))
        {
            SetStatus("Введите ссылку или подписку");
            return;
        }

        try
        {
            if (link.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var subscriptionExists = (await AppManager.Instance.SubItems())
                    .Any(t => string.Equals(t.Url, link, StringComparison.OrdinalIgnoreCase));
                var ret = await ConfigHandler.AddSubItem(_config, link);
                if (ret == 0)
                {
                    await SubscriptionHandler.UpdateProcess(_config, "", false, (_, _) => Task.CompletedTask);
                    SetStatus(subscriptionExists ? "Subscription updated" : "Subscription added and updated");
                }
                else
                {
                    SetStatus("Failed to add subscription");
                }
            }
            else
            {
                var ret = await ConfigHandler.AddBatchServers(_config, link, _config.SubIndexId, false);
                SetStatus(ret > 0 ? "Link imported" : "Failed to import link");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
        finally
        {
            InputLink = string.Empty;
        }

        await RefreshProfilesAsync();
    }

    private async void OnSetActive(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile == null)
        {
            SetStatus("Select a profile first");
            return;
        }

        await ConfigHandler.SetDefaultServerIndex(_config, SelectedProfile.IndexId);
        await ViewModel.Reload();
        await RefreshProfilesAsync();
        await UpdateConnectionPingAsync();
        SetStatus("Active profile updated");
    }

    private async void OnProfilesMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedProfile == null)
        {
            return;
        }

        OnEditProfile(sender, e);
        await Task.CompletedTask;
    }

    private void OnNestedScrollViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject source)
        {
            return;
        }

        var innerScrollViewer = FindVisualChild<ScrollViewer>(source);
        if (innerScrollViewer != null && innerScrollViewer.ScrollableHeight > 0)
        {
            var scrollingUp = e.Delta > 0;
            var canScrollInner =
                (scrollingUp && innerScrollViewer.VerticalOffset > 0)
                || (!scrollingUp && innerScrollViewer.VerticalOffset < innerScrollViewer.ScrollableHeight);

            if (canScrollInner)
            {
                return;
            }
        }

        var parentScrollViewer = FindVisualParent<ScrollViewer>(source);
        if (parentScrollViewer == null)
        {
            return;
        }

        e.Handled = true;
        var eventArgs = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender
        };
        parentScrollViewer.RaiseEvent(eventArgs);
    }

    private async void OnEditProfile(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile == null)
        {
            SetStatus("Select a profile first");
            return;
        }

        var item = await AppManager.Instance.GetProfileItem(SelectedProfile.IndexId);
        if (item == null)
        {
            SetStatus("Configuration not found");
            return;
        }

        bool? result;
        if (item.ConfigType == EConfigType.Custom)
        {
            result = new AddServer2Window(item).ShowDialog();
        }
        else if (item.ConfigType.IsGroupType())
        {
            result = new AddGroupServerWindow(item).ShowDialog();
        }
        else
        {
            result = new AddServerWindow(item).ShowDialog();
        }

        if (result != true)
        {
            return;
        }

        await RefreshProfilesAsync();
        if (item.IndexId == _config.IndexId)
        {
            await ViewModel.Reload();
        }

        SetStatus("Configuration updated");
    }

    private async void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile == null)
        {
            SetStatus("Select a profile first");
            return;
        }

        var item = await AppManager.Instance.GetProfileItem(SelectedProfile.IndexId);
        if (item == null)
        {
            SetStatus("Configuration not found");
            return;
        }

        if (UI.ShowYesNo(ResUI.RemoveServer) == MessageBoxResult.No)
        {
            return;
        }

        var wasActive = item.IndexId == _config.IndexId;
        await ConfigHandler.RemoveServers(_config, new List<ProfileItem> { item });
        await RefreshProfilesAsync();

        if (wasActive)
        {
            await ViewModel.Reload();
        }

        SetStatus("Configuration deleted");
    }

    private async void OnDuplicateProfile(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile == null)
        {
            SetStatus("Select a profile first");
            return;
        }

        var item = await AppManager.Instance.GetProfileItem(SelectedProfile.IndexId);
        if (item == null)
        {
            SetStatus("Configuration not found");
            return;
        }

        await ConfigHandler.CopyServer(_config, new List<ProfileItem> { item });
        await RefreshProfilesAsync();
        SetStatus("Configuration duplicated");
    }

    private async void OnAddApp(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Title = "Select application executable"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var fullPath = dialog.FileName;
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        AddDirectAppEntries(fullPath);
        await ApplyQuickRulesAsync(reload: true);
        SetStatus("App added to direct list");
    }

    private async void OnRefreshRunningProcesses(object sender, RoutedEventArgs e)
    {
        RefreshRunningProcesses();
        await Task.CompletedTask;
        SetStatus("Running processes refreshed");
    }

    private async void OnAddRunningProcess(object sender, RoutedEventArgs e)
    {
        if (SelectedRunningProcess == null || SelectedRunningProcess.FilePath.IsNullOrEmpty())
        {
            SetStatus("Select a running process first");
            return;
        }

        AddDirectAppEntries(SelectedRunningProcess.FilePath);
        await ApplyQuickRulesAsync(reload: true);
        SetStatus($"Added running app: {Path.GetFileName(SelectedRunningProcess.FilePath)}");
    }

    private async void OnRemoveApp(object sender, RoutedEventArgs e)
    {
        if (SelectedApp == null)
        {
            return;
        }

        DirectApps.Remove(SelectedApp);
        await ApplyQuickRulesAsync(reload: true);
        SetStatus("App removed");
    }

    private async void OnAddDomain(object sender, RoutedEventArgs e)
    {
        var domain = NewDomain?.Trim();
        if (string.IsNullOrWhiteSpace(domain))
        {
            SetStatus("Введите домен");
            return;
        }

        var normalized = NormalizeDomainRule(domain);
        if (DirectDomains.Any(t => string.Equals(t, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("Domain already in list");
            return;
        }

        DirectDomains.Add(normalized);
        NewDomain = string.Empty;
        await ApplyQuickRulesAsync(reload: true);
        SetStatus("Domain added");
    }

    private async void OnRemoveDomain(object sender, RoutedEventArgs e)
    {
        if (SelectedDomain == null)
        {
            return;
        }

        DirectDomains.Remove(SelectedDomain);
        await ApplyQuickRulesAsync(reload: true);
        SetStatus("Domain removed");
    }

    private async void OnAddRussianWhitelistPreset(object sender, RoutedEventArgs e)
    {
        var presetRules = new[]
        {
            "geosite:category-ru",
            "domain:vk.com"
        };

        var added = 0;
        foreach (var rule in presetRules)
        {
            if (DirectDomains.Any(t => string.Equals(t, rule, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            DirectDomains.Add(rule);
            added++;
        }

        if (added == 0)
        {
            SetStatus("RU preset is already in whitelist");
            return;
        }

        await ApplyQuickRulesAsync(reload: true);
        SetStatus("Added RU sites preset to whitelist");
    }

    private async void OnApplyRouting(object sender, RoutedEventArgs e)
    {
        await ApplyQuickRulesAsync(reload: true);
        SetStatus("Routing applied");
    }

    private async void OnAddBlockedDomain(object sender, RoutedEventArgs e)
    {
        var domain = NewBlockedDomain?.Trim();
        if (string.IsNullOrWhiteSpace(domain))
        {
            SetStatus("Введите домен для блокировки");
            return;
        }

        var normalized = NormalizeDomainRule(domain);
        if (BlockDomains.Any(t => string.Equals(t, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("Blocked domain already in list");
            return;
        }

        BlockDomains.Add(normalized);
        NewBlockedDomain = string.Empty;
        await ApplyQuickRulesAsync(reload: true);
        SetStatus("Blocked domain added");
    }

    private async void OnRemoveBlockedDomain(object sender, RoutedEventArgs e)
    {
        if (SelectedBlockedDomain == null)
        {
            return;
        }

        BlockDomains.Remove(SelectedBlockedDomain);
        await ApplyQuickRulesAsync(reload: true);
        SetStatus("Blocked domain removed");
    }

    private async void OnAddAdsBlacklistPreset(object sender, RoutedEventArgs e)
    {
        const string presetRule = "geosite:category-ads-all";
        if (BlockDomains.Any(t => string.Equals(t, presetRule, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("Ads preset is already in blacklist");
            return;
        }

        BlockDomains.Add(presetRule);
        await ApplyQuickRulesAsync(reload: true);
        SetStatus("Added ads preset to blacklist");
    }

    private async void OnAddProxyDomain(object sender, RoutedEventArgs e)
    {
        var domain = NewProxyDomain?.Trim();
        if (string.IsNullOrWhiteSpace(domain))
        {
            SetStatus("Введите домен для VPN");
            return;
        }

        var normalized = NormalizeDomainRule(domain);
        if (ProxyDomains.Any(t => string.Equals(t, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("VPN domain already in list");
            return;
        }

        ProxyDomains.Add(normalized);
        NewProxyDomain = string.Empty;
        await ApplyQuickRulesAsync(reload: true);
        SetStatus("VPN domain added");
    }

    private async void OnAddProxyApp(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Title = "Select application executable"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var fullPath = dialog.FileName;
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        AddProxyAppEntries(fullPath);
        await ApplyQuickRulesAsync(reload: true);
        SetStatus("App added to VPN list");
    }

    private async void OnAddRunningProxyProcess(object sender, RoutedEventArgs e)
    {
        if (SelectedRunningProcess == null || SelectedRunningProcess.FilePath.IsNullOrEmpty())
        {
            SetStatus("Select a running process first");
            return;
        }

        AddProxyAppEntries(SelectedRunningProcess.FilePath);
        await ApplyQuickRulesAsync(reload: true);
        SetStatus($"Added running app to VPN list: {Path.GetFileName(SelectedRunningProcess.FilePath)}");
    }

    private async void OnRemoveProxyApp(object sender, RoutedEventArgs e)
    {
        if (SelectedProxyApp == null)
        {
            return;
        }

        ProxyApps.Remove(SelectedProxyApp);
        await ApplyQuickRulesAsync(reload: true);
        SetStatus("VPN app removed");
    }

    private async void OnRemoveProxyDomain(object sender, RoutedEventArgs e)
    {
        if (SelectedProxyDomain == null)
        {
            return;
        }

        ProxyDomains.Remove(SelectedProxyDomain);
        await ApplyQuickRulesAsync(reload: true);
        SetStatus("VPN domain removed");
    }

    private async void OnZapretRefresh(object sender, RoutedEventArgs e)
    {
        await RefreshZapretAsync();
    }

    private async void OnSelectZapretFolder(object sender, RoutedEventArgs e)
    {
        if (UI.OpenZapretDialog(out var folderPath) != true)
        {
            return;
        }

        var resolvedPath = folderPath;
        if (!ZapretHandler.IsValidZapretPath(resolvedPath))
        {
            var candidate = Path.Combine(folderPath, "zapret");
            if (ZapretHandler.IsValidZapretPath(candidate))
            {
                resolvedPath = candidate;
            }
        }

        if (!ZapretHandler.IsValidZapretPath(resolvedPath))
        {
            SetZapretStatus("Selected folder is not zapret (bin\\winws.exe not found)");
            return;
        }

        _config.GuiItem.ZapretPath = resolvedPath;
        await ConfigHandler.SaveConfig(_config);
        await RefreshZapretAsync();
        SetZapretStatus("Zapret path updated");
    }

    private async void OnStartZapret(object sender, RoutedEventArgs e)
    {
        await StartZapretAsync();
    }

    private async void OnStopZapret(object sender, RoutedEventArgs e)
    {
        await StopZapretAsync();
    }

    private async Task SwitchZapretConfigAsync(string configName)
    {
        if (_isSwitchingZapretConfig || ZapretPath.IsNullOrEmpty())
        {
            return;
        }

        _isSwitchingZapretConfig = true;
        try
        {
            SetZapretStatus($"Switching to {configName}...");
            ZapretHandler.Stop();
            await Task.Delay(800);

            if (ZapretHandler.IsRunning())
            {
                SetZapretStatus("Failed to stop current zapret config");
                return;
            }

            if (!ZapretHandler.Start(ZapretPath, configName, out var error))
            {
                ZapretRunning = false;
                ZapretEnabled = false;
                SetZapretStatus(error);
                return;
            }

            ZapretRunning = true;
            ZapretEnabled = true;
            await PersistZapretEnabledAsync(true);
            await RememberLastZapretConfigAsync(configName);
            SetZapretStatus($"Started: {configName}");
        }
        finally
        {
            _isSwitchingZapretConfig = false;
        }
    }

    private async Task RememberLastZapretConfigAsync(string? configName)
    {
        if (configName.IsNullOrEmpty() || string.Equals(_config.GuiItem.LastZapretConfig, configName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _config.GuiItem.LastZapretConfig = configName;
        await ConfigHandler.SaveConfig(_config);
    }

    private async Task PersistZapretEnabledAsync(bool enabled)
    {
        if (_config.GuiItem.ZapretEnabled == enabled)
        {
            return;
        }

        _config.GuiItem.ZapretEnabled = enabled;
        await ConfigHandler.SaveConfig(_config);
    }

    private async Task StartZapretAsync(bool persistEnabledState = true, string initialStatus = "Starting...")
    {
        if (ZapretRunning)
        {
            if (persistEnabledState)
            {
                await PersistZapretEnabledAsync(true);
            }

            SetZapretStatus("Zapret already running");
            return;
        }

        if (ZapretPath.IsNullOrEmpty() || SelectedZapretConfig?.Name.IsNullOrEmpty() != false)
        {
            if (persistEnabledState)
            {
                await PersistZapretEnabledAsync(false);
            }

            ZapretEnabled = false;
            SetZapretStatus("Select config");
            return;
        }

        SetZapretStatus(initialStatus);
        if (ZapretHandler.Start(ZapretPath, SelectedZapretConfig.Name, out var error))
        {
            ZapretRunning = true;
            ZapretEnabled = true;
            if (persistEnabledState)
            {
                await PersistZapretEnabledAsync(true);
            }

            await RememberLastZapretConfigAsync(SelectedZapretConfig.Name);
            SetZapretStatus($"Started: {SelectedZapretConfig.Name}");
            return;
        }

        if (persistEnabledState)
        {
            await PersistZapretEnabledAsync(false);
        }

        ZapretEnabled = false;
        SetZapretStatus(error);
    }

    private async Task StopZapretAsync()
    {
        ZapretHandler.Stop();
        await Task.Delay(500);
        ZapretRunning = ZapretHandler.IsRunning();
        ZapretEnabled = ZapretRunning;
        await PersistZapretEnabledAsync(ZapretRunning);
        SetZapretStatus(ZapretRunning ? "Failed to stop" : "Stopped");
    }

    private async void OnTestZapret(object sender, RoutedEventArgs e)
    {
        var result = await RunZapretTestAsync("Testing config", keepRunning: true);
        if (result == null)
        {
            return;
        }

        UpdateZapretConfigResult(SelectedZapretConfig?.Name, result);
        SetZapretStatus($"Test config: {result.YoutubeMessage}; {result.DiscordMessage}");
    }

    private async void OnTestZapretDiscord(object sender, RoutedEventArgs e)
    {
        var result = await RunZapretTestAsync("Testing Discord", keepRunning: true);
        if (result == null)
        {
            return;
        }

        UpdateZapretConfigResult(SelectedZapretConfig?.Name, result);
        SetZapretStatus($"Discord: {result.DiscordMessage}");
    }

    private async void OnAutoTestZapret(object sender, RoutedEventArgs e)
    {
        if (ZapretPath.IsNullOrEmpty() || ZapretConfigs.Count == 0)
        {
            SetZapretStatus("Zapret not found");
            return;
        }

        if (ZapretRunning)
        {
            SetZapretStatus("Stop Zapret before testing");
            return;
        }

        await CancelZapretAutoTestAsync();
        _zapretAutoTestCts = new CancellationTokenSource();
        _zapretAutoTestTask = RunAutoTestZapretAsync(_zapretAutoTestCts.Token);
        try
        {
            await _zapretAutoTestTask;
        }
        catch (OperationCanceledException)
        {
            SetZapretStatus("Auto test stopped");
        }
        finally
        {
            _zapretAutoTestCts?.Dispose();
            _zapretAutoTestCts = null;
            _zapretAutoTestTask = null;
            _isAutoTestingZapret = false;
        }
    }

    private async Task RunAutoTestZapretAsync(CancellationToken cancellationToken)
    {
        _isAutoTestingZapret = true;
        long best = long.MaxValue;
        ZapretConfigItem? bestCfg = null;
        ZapretTestResult? bestResult = null;

        foreach (var cfg in ZapretConfigs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetZapretStatus($"Testing {cfg.Name} for YouTube + Discord...");
            var result = await ZapretHandler.TestConfigAsync(ZapretPath, cfg.Name, keepRunning: false, cancellationToken);
            UpdateZapretConfigResult(cfg.Name, result);
            if (result.Success && result.TimeMs.HasValue && result.TimeMs.Value < best)
            {
                best = result.TimeMs.Value;
                bestCfg = cfg;
                bestResult = result;
            }
        }

        if (bestCfg != null)
        {
            SelectedZapretConfig = bestCfg;
            SetZapretStatus(
                $"Best: {bestCfg.Name} | YouTube: {bestResult?.YoutubeMessage} | Discord: {bestResult?.DiscordMessage} | score {best} ms");
            return;
        }

        SetZapretStatus("No config passed both YouTube and Discord");
    }

    private async void OnPingServer(object sender, RoutedEventArgs e)
    {
        var profile = SelectedProfile ?? Profiles.FirstOrDefault(t => t.IsActive);
        var label = await GetProfilePingLabelAsync(profile);
        ServerPing = label;
        ConnectionPing = label.StartsWith("Connection ping:", StringComparison.OrdinalIgnoreCase)
            ? label
            : $"Connection ping: {label}";
    }

    private async void OnRefreshDebug(object sender, RoutedEventArgs e)
    {
        await RefreshSupportSnapshotAsync(true);
        SetStatus("Diagnostics refreshed");
    }

    private void OnCopyDebug(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(DebugLog))
        {
            Clipboard.SetText(DebugLog);
            SetStatus("Debug copied to clipboard");
        }
    }

    private void OnClearDebug(object sender, RoutedEventArgs e)
    {
        DebugLog = string.Empty;
        SetStatus("Debug output cleared");
    }

    private async void OnExportDiagnostics(object sender, RoutedEventArgs e)
    {
        await RefreshSupportSnapshotAsync(true);
        var fileName = Utils.GetLogPath($"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var export = new StringBuilder();
        export.AppendLine(SystemStatusSummary);
        export.AppendLine();
        export.AppendLine(DiagnosticOverview);
        export.AppendLine();
        export.AppendLine(DataLayoutSummary);
        export.AppendLine();
        export.AppendLine(DebugLog);
        await File.WriteAllTextAsync(fileName, export.ToString());
        SetStatus($"Diagnostics exported: {Path.GetFileName(fileName)}");
        OpenPath(Path.GetDirectoryName(fileName) ?? Utils.GetLogPath());
    }

    private async void OnOpenDiagnosticsWindow(object sender, RoutedEventArgs e)
    {
        await RefreshSupportSnapshotAsync(true);

        var content = $"{SystemStatusSummary}{Environment.NewLine}{Environment.NewLine}{DiagnosticOverview}{Environment.NewLine}{Environment.NewLine}{DebugLog}".Trim();
        var diagnosticsBox = new TextBox
        {
            Text = content,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = (Brush)FindResource("NetCatSurfaceAltBrush"),
            Foreground = (Brush)FindResource("NetCatStrongTextBrush"),
            BorderBrush = (Brush)FindResource("NetCatWindowChromeBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14)
        };

        var window = new Window
        {
            Title = "Diagnostics",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 1080,
            Height = 760,
            MinWidth = 780,
            MinHeight = 520,
            ResizeMode = ResizeMode.CanResize,
            WindowState = WindowState.Normal,
            Icon = Icon,
            Background = (Brush)FindResource("NetCatWindowBackgroundBrush"),
            Content = new Border
            {
                Padding = new Thickness(16),
                Background = (Brush)FindResource("NetCatWindowBackgroundBrush"),
                Child = diagnosticsBox
            }
        };

        window.Show();
        SetStatus("Diagnostics window opened");
    }

    private void OnOpenInstallFolder(object sender, RoutedEventArgs e)
    {
        OpenPath(Utils.StartupPath());
    }

    private void OnOpenUserDataFolder(object sender, RoutedEventArgs e)
    {
        OpenPath(Utils.GetUserDataPath());
    }

    private void OnOpenLogsFolder(object sender, RoutedEventArgs e)
    {
        OpenPath(Utils.GetLogPath());
    }

    private void OnOpenTempFolder(object sender, RoutedEventArgs e)
    {
        OpenPath(Utils.GetTempPath());
    }

    private async Task<string> BuildDebugInfoAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Utils.GetRuntimeInfo());
        sb.AppendLine($"Version: {Utils.GetVersion()}");
        sb.AppendLine($"InstallPath: {Utils.StartupPath()}");
        sb.AppendLine($"UserDataPath: {Utils.GetUserDataPath()}");
        sb.AppendLine($"BinPath: {Utils.GetBinPath("")}");
        sb.AppendLine($"ConfigPath: {Utils.GetConfigPath(Global.ConfigFileName)}");
        sb.AppendLine($"LogPath: {Utils.GetLogPath()}");
        sb.AppendLine($"UpdaterPath: {Utils.GetUpgradeAppPath()}");
        sb.AppendLine($"CoreConfig: {Utils.GetBinConfigPath(Global.CoreConfigFileName)}");
        sb.AppendLine($"SystemProxyType: {_config.SystemProxyItem.SysProxyType}");
        sb.AppendLine($"SystemProxyAdvancedProtocol: {_config.SystemProxyItem.SystemProxyAdvancedProtocol}");
        sb.AppendLine($"SystemProxyNotProxyLocal: {_config.SystemProxyItem.NotProxyLocalAddress}");
        sb.AppendLine($"SystemProxyExceptions: {_config.SystemProxyItem.SystemProxyExceptions}");
        sb.AppendLine($"TunEnabled: {_config.TunModeItem.EnableTun}");
        sb.AppendLine($"VpnEnabled: {VpnEnabled}");
        var localPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        var localPortFree = Utils.GetFreePort(localPort) == localPort;
        var xrayRunning = Process.GetProcessesByName("xray").Length > 0
                          || Process.GetProcessesByName("sing-box").Length > 0
                          || Process.GetProcessesByName("mihomo").Length > 0;
        sb.AppendLine($"LocalSocksPort: {localPort}");
        sb.AppendLine($"LocalSocksPortFree: {localPortFree} (xray running: {xrayRunning})");
        sb.AppendLine($"ZapretPath: {ZapretPath}");
        sb.AppendLine($"ZapretConfig: {SelectedZapretConfig?.Name}");
        sb.AppendLine($"ZapretRunning: {ZapretRunning}");
        sb.AppendLine($"DirectApps: {DirectApps.Count}");
        sb.AppendLine($"DirectDomains: {DirectDomains.Count}");
        sb.AppendLine($"ProxyApps: {ProxyApps.Count}");
        sb.AppendLine($"ProxyDomains: {ProxyDomains.Count}");
        sb.AppendLine($"UseProxyDomainsPreset: {UseProxyDomainsPreset}");
        sb.AppendLine($"BlockDomains: {BlockDomains.Count}");
        sb.AppendLine($"ProxyOnlyMode: {ProxyOnlyMode}");
        sb.AppendLine($"BypassPrivate: {BypassPrivate}");
        var defaultProfile = SelectedProfile != null
            ? await AppManager.Instance.GetProfileItem(SelectedProfile.IndexId)
            : await ConfigHandler.GetDefaultServer(_config);

        if (defaultProfile != null)
        {
            sb.AppendLine($"ActiveProfile: {defaultProfile.GetSummary()}");
            var coreType = AppManager.Instance.GetCoreType(defaultProfile, defaultProfile.ConfigType);
            sb.AppendLine($"CoreType: {coreType}");

            var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
            var coreExec = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out var msg);
            sb.AppendLine($"CoreExec: {coreExec}");
            if (!string.IsNullOrWhiteSpace(msg))
            {
                sb.AppendLine($"CoreExecMsg: {msg}");
            }
        }
        else
        {
            sb.AppendLine("ActiveProfile: none");
        }

        var xrayDir = Utils.GetBinPath("", ECoreType.Xray.ToString());
        sb.AppendLine($"XrayDir: {xrayDir}");
        var binDir = Utils.GetBinPath("");
        sb.AppendLine($"BinRoot: {binDir}");
        foreach (var name in new[] { "geoip.dat", "geosite.dat" })
        {
            var path = Path.Combine(binDir, name);
            sb.AppendLine(File.Exists(path) ? $"BinAsset: {name} OK" : $"BinAsset: {name} MISSING");
        }
        foreach (var name in new[] { "xray.exe", "geoip.dat", "geosite.dat" })
        {
            var path = Path.Combine(xrayDir, name);
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                sb.AppendLine($"XrayFile: {name} {info.Length} bytes {info.LastWriteTime}");
            }
            else
            {
                sb.AppendLine($"XrayFile: {name} MISSING");
            }
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false);
            if (key != null)
            {
                var proxyEnable = key.GetValue("ProxyEnable");
                var proxyServer = key.GetValue("ProxyServer");
                var proxyOverride = key.GetValue("ProxyOverride");
                var autoConfig = key.GetValue("AutoConfigURL");
                sb.AppendLine($"Registry ProxyEnable: {proxyEnable}");
                sb.AppendLine($"Registry ProxyServer: {proxyServer}");
                sb.AppendLine($"Registry ProxyOverride: {proxyOverride}");
                sb.AppendLine($"Registry AutoConfigURL: {autoConfig}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"RegistryReadError: {ex.Message}");
        }

        var coreConfigPath = Utils.GetBinConfigPath(Global.CoreConfigFileName);
        if (File.Exists(coreConfigPath))
        {
            var info = new FileInfo(coreConfigPath);
            sb.AppendLine($"CoreConfigSize: {info.Length}");
        }
        else
        {
            sb.AppendLine("CoreConfigMissing");
        }

        sb.AppendLine($"Process xray: {Process.GetProcessesByName("xray").Length}");
        sb.AppendLine($"Process v2ray: {Process.GetProcessesByName("v2ray").Length}");
        sb.AppendLine($"Process sing-box: {Process.GetProcessesByName("sing-box").Length}");
        sb.AppendLine($"Process mihomo: {Process.GetProcessesByName("mihomo").Length}");

        if (!VpnEnabled && Process.GetProcessesByName("xray").Length > 0)
        {
            sb.AppendLine("Warning: xray is running while VPN/system proxy is OFF");
        }

        var logDir = Utils.GetLogPath();
        sb.AppendLine($"LogDir: {logDir}");
        try
        {
            var latestLog = Directory.GetFiles(logDir)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .FirstOrDefault();

            if (latestLog != null)
            {
                sb.AppendLine($"LatestLog: {latestLog.FullName}");
                using var stream = new FileStream(latestLog.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var lines = new List<string>();
                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (line != null)
                    {
                        lines.Add(line);
                    }
                }
                var tail = lines.Skip(Math.Max(0, lines.Count - 200));
                sb.AppendLine("---- Log Tail ----");
                foreach (var line in tail)
                {
                    sb.AppendLine(line);
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"LogReadError: {ex.Message}");
        }

        return sb.ToString();
    }

    private async Task<bool> EnsureXrayCoreAsync()
    {
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(ECoreType.Xray);
        var coreExec = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out _);
        if (!coreExec.IsNullOrEmpty())
        {
            await EnsureXrayAssetsAsync();
            return true;
        }

        var targetDir = Utils.GetBinPath("", ECoreType.Xray.ToString());
        var candidateDirs = new List<string>();

        var current = new DirectoryInfo(Utils.StartupPath());
        for (var i = 0; i < 6 && current != null; i++)
        {
            candidateDirs.Add(Path.Combine(current.FullName, "my-vpn-zapret", "resources", "xray"));
            candidateDirs.Add(Path.Combine(current.FullName, "resources", "xray"));
            candidateDirs.Add(Path.Combine(current.FullName, "xray"));
            current = current.Parent;
        }

        var sourceDir = candidateDirs.FirstOrDefault(dir => File.Exists(Path.Combine(dir, "xray.exe")));
        if (sourceDir.IsNullOrEmpty())
        {
            return false;
        }

        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, true);
        }

        await EnsureXrayAssetsAsync();
        return File.Exists(Path.Combine(targetDir, "xray.exe"));
    }

    private async Task<bool> EnsureSingboxCoreAsync()
    {
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(ECoreType.sing_box);
        var coreExec = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out _);
        if (!coreExec.IsNullOrEmpty())
        {
            return true;
        }

        var targetDir = Utils.GetBinPath("", ECoreType.sing_box.ToString());
        var candidateDirs = new List<string>();

        var current = new DirectoryInfo(Utils.StartupPath());
        for (var i = 0; i < 6 && current != null; i++)
        {
            candidateDirs.Add(Path.Combine(current.FullName, "my-vpn-zapret", "resources", "v2rayn", "bin", "sing_box"));
            candidateDirs.Add(Path.Combine(current.FullName, "resources", "v2rayn", "bin", "sing_box"));
            candidateDirs.Add(Path.Combine(current.FullName, "v2rayn", "bin", "sing_box"));
            candidateDirs.Add(Path.Combine(current.FullName, "sing_box"));
            current = current.Parent;
        }

        var exeName = Utils.GetExeName("sing-box");
        var sourceDir = candidateDirs.FirstOrDefault(dir => File.Exists(Path.Combine(dir, exeName)));
        if (sourceDir.IsNullOrEmpty())
        {
            return false;
        }

        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, true);
        }

        TryCopyWintun(targetDir, sourceDir);
        return File.Exists(Path.Combine(targetDir, exeName));
    }

    private void TryCopyWintun(string targetDir, string sourceDir)
    {
        var wintunName = "wintun.dll";
        var targetPath = Path.Combine(targetDir, wintunName);
        if (File.Exists(targetPath))
        {
            return;
        }

        var candidates = new List<string>
        {
            Path.Combine(sourceDir, wintunName),
            Path.Combine(Directory.GetParent(sourceDir)?.FullName ?? string.Empty, "xray", wintunName),
            Path.Combine(Utils.GetBinPath("", ECoreType.Xray.ToString()), wintunName),
            Path.Combine(Utils.GetBinPath(""), "xray", wintunName),
        };

        var current = new DirectoryInfo(Utils.StartupPath());
        for (var i = 0; i < 6 && current != null; i++)
        {
            candidates.Add(Path.Combine(current.FullName, "my-vpn-zapret", "resources", "xray", wintunName));
            candidates.Add(Path.Combine(current.FullName, "resources", "xray", wintunName));
            candidates.Add(Path.Combine(current.FullName, "xray", wintunName));
            current = current.Parent;
        }

        var source = candidates.FirstOrDefault(File.Exists);
        if (!source.IsNullOrEmpty())
        {
            File.Copy(source, targetPath, true);
        }
    }

    private async Task EnsureXrayAssetsAsync()
    {
        var assetTarget = Utils.GetBinPath("");
        var sourceDir = Utils.GetBinPath("", ECoreType.Xray.ToString());
        var files = new[] { "geoip.dat", "geosite.dat" };
        foreach (var file in files)
        {
            var src = Path.Combine(sourceDir, file);
            if (File.Exists(src))
            {
                var dest = Path.Combine(assetTarget, file);
                if (!File.Exists(dest))
                {
                    File.Copy(src, dest, true);
                }
            }
        }

        await Task.CompletedTask;
    }

    private async Task EnsureInboundPortAvailableAsync()
    {
        var inbound = _config.Inbound.FirstOrDefault(t => t.Protocol == nameof(EInboundProtocol.socks));
        if (inbound == null)
        {
            return;
        }

        var desired = inbound.LocalPort > 0 ? inbound.LocalPort : 10808;
        var free = Utils.GetFreePort(desired);
        if (free != desired)
        {
            inbound.LocalPort = free;
            await ConfigHandler.SaveConfig(_config);
            SetStatus($"Local port {desired} is busy. Switched to {free}.");
        }
    }

    private async void OnTestCore(object sender, RoutedEventArgs e)
    {
        var coreType = TunEnabled ? ECoreType.sing_box : ECoreType.Xray;
        var ready = TunEnabled ? await EnsureSingboxCoreAsync() : await EnsureXrayCoreAsync();
        if (!ready)
        {
            SetStatus(TunEnabled ? "sing-box core not found" : "Xray core not found");
            return;
        }

        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
        var coreExec = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out _);
        if (coreExec.IsNullOrEmpty())
        {
            SetStatus(TunEnabled ? "sing-box core not found" : "Xray core not found");
            return;
        }

        var versionArg = coreInfo?.VersionArg.IsNullOrEmpty() == true ? "-version" : coreInfo?.VersionArg ?? "-version";

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = coreExec,
                Arguments = versionArg,
                WorkingDirectory = Path.GetDirectoryName(coreExec) ?? Utils.GetBinPath(""),
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (coreType == ECoreType.Xray)
            {
                startInfo.EnvironmentVariables[Global.XrayLocalAsset] = Utils.GetBinPath("");
                startInfo.EnvironmentVariables[Global.XrayLocalCert] = Utils.GetBinPath("");
            }

            using var proc = Process.Start(startInfo);
            if (proc == null)
            {
                SetStatus("Failed to start core");
                return;
            }
            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var result = string.IsNullOrWhiteSpace(output) ? error : output;
            DebugLog = $"{DebugLog}\n---- Core Test ----\n{result}".Trim();
            SetStatus("Core test done");
        }
        catch (Exception ex)
        {
            SetStatus($"Core test failed: {ex.Message}");
        }
    }

    private async void OnTestConfig(object sender, RoutedEventArgs e)
    {
        if (Process.GetProcessesByName("xray").Length > 0
            || Process.GetProcessesByName("sing-box").Length > 0
            || Process.GetProcessesByName("mihomo").Length > 0)
        {
            SetStatus("Stop VPN before config test");
            return;
        }

        var coreType = TunEnabled ? ECoreType.sing_box : ECoreType.Xray;
        var ready = TunEnabled ? await EnsureSingboxCoreAsync() : await EnsureXrayCoreAsync();
        if (!ready)
        {
            SetStatus(TunEnabled ? "sing-box core not found" : "Xray core not found");
            return;
        }

        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
        var coreExec = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out _);
        if (coreExec.IsNullOrEmpty())
        {
            SetStatus(TunEnabled ? "sing-box core not found" : "Xray core not found");
            return;
        }

        var configPath = Utils.GetBinConfigPath(Global.CoreConfigFileName);
        if (!File.Exists(configPath))
        {
            SetStatus("Core config not found");
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = coreExec,
                Arguments = coreType == ECoreType.sing_box
                    ? $"run -c \"{configPath}\" --disable-color"
                    : $"run -c \"{configPath}\"",
                WorkingDirectory = Path.GetDirectoryName(coreExec) ?? Utils.GetBinPath(""),
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (coreType == ECoreType.Xray)
            {
                startInfo.EnvironmentVariables[Global.XrayLocalAsset] = Utils.GetBinPath("");
                startInfo.EnvironmentVariables[Global.XrayLocalCert] = Utils.GetBinPath("");
            }

            using var proc = Process.Start(startInfo);
            if (proc == null)
            {
                SetStatus("Failed to start xray");
                return;
            }

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            var exited = await Task.Run(() => proc.WaitForExit(2000));
            if (!exited)
            {
                try
                {
                    proc.Kill(true);
                }
                catch { }
                SetStatus("Config test: core started");
                return;
            }

            var output = await outputTask;
            var error = await errorTask;
            var result = string.IsNullOrWhiteSpace(error) ? output : error;
            DebugLog = $"{DebugLog}\n---- Config Test ----\n{result}".Trim();
            SetStatus("Config test finished");
        }
        catch (Exception ex)
        {
            SetStatus($"Config test failed: {ex.Message}");
        }
    }

    private async void OnTestProxy(object sender, RoutedEventArgs e)
    {
        var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        var result = await TestProxyIpAsync(port);
        DebugLog = $"{DebugLog}\n---- Proxy Test ----\n{result}".Trim();
        SetStatus("Proxy test finished");
    }

    private static async Task<string> TestProxyIpAsync(int port)
    {
        var proxyUri = new Uri($"http://{Global.Loopback}:{port}");
        var target = "https://api64.ipify.org";

        var direct = await FetchIpAsync(target, proxy: null);
        var proxied = await FetchIpAsync(target, proxy: new WebProxy(proxyUri));
        var match = string.Equals(direct.Ip, proxied.Ip, StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine($"direct: {direct}");
        sb.AppendLine($"proxy:  {proxied}");
        sb.AppendLine($"match:  {match}");
        return sb.ToString().Trim();
    }

    private static async Task<(bool Ok, string Ip, string Detail)> FetchIpAsync(string target, WebProxy? proxy)
    {
        var handler = new HttpClientHandler
        {
            Proxy = proxy,
            UseProxy = proxy != null
        };

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        try
        {
            var response = await client.GetAsync(target);
            var body = (await response.Content.ReadAsStringAsync()).Trim();
            return (response.IsSuccessStatusCode, body, $"{(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }

    private static string NormalizeDomainRule(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.IsNullOrEmpty())
        {
            return trimmed;
        }

        if (trimmed.Contains(':'))
        {
            return trimmed;
        }

        var value = trimmed.TrimStart('*').TrimStart('.');
        if (value.IsNullOrEmpty())
        {
            return trimmed;
        }

        return $"domain:{value}";
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            child = VisualTreeHelper.GetParent(child);
            if (child is T target)
            {
                return target;
            }
        }

        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent == null)
        {
            return null;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T target)
            {
                return target;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant != null)
            {
                return descendant;
            }
        }

        return null;
    }

    private async void OnToggleTun(object sender, RoutedEventArgs e)
    {
        if (_suppressConnectionToggleEvents)
        {
            return;
        }

        await HandleTunToggleAsync();
    }

    private async Task HandleTunToggleAsync()
    {
        if (!TunEnabled)
        {
            _config.TunModeItem.EnableTun = false;
            await ConfigHandler.SaveConfig(_config);

            if (VpnEnabled)
            {
                if (!await EnsureActiveCoreReadyAsync())
                {
                    TunEnabled = true;
                    _config.TunModeItem.EnableTun = true;
                    await ConfigHandler.SaveConfig(_config);
                    SetStatus("Active core not found. TUN was restored.");
                    return;
                }

                await ViewModel.Reload();
                MainVpnEnabled = VpnEnabled || TunEnabled;
                SetStatus("TUN disabled");
                return;
            }

            await CoreManager.Instance.CoreStop();
            MainVpnEnabled = false;
            SetStatus("TUN disabled");
            return;
        }

        if (!await EnsureTunReadyAsync())
        {
            return;
        }

        await ViewModel.Reload();
        MainVpnEnabled = VpnEnabled || TunEnabled;
        await UpdateConnectionPingAsync();
        SetStatus(VpnEnabled ? "Полный туннель включен вместе с прокси" : "Полный туннель включен");
    }

    private async Task SetVpnEnabledAsync(bool enabled)
    {
        _suppressConnectionToggleEvents = true;
        VpnEnabled = enabled;
        _suppressConnectionToggleEvents = false;
        await HandleVpnToggleAsync();
    }

    private async Task SetTunEnabledAsync(bool enabled)
    {
        _suppressConnectionToggleEvents = true;
        TunEnabled = enabled;
        _suppressConnectionToggleEvents = false;
        await HandleTunToggleAsync();
    }

    private async Task<bool> EnsureTunReadyAsync()
    {
        _config.TunModeItem.EnableTun = TunEnabled;
        if (Utils.IsWindows() && !Utils.IsAdministrator())
        {
            TunEnabled = false;
            _config.TunModeItem.EnableTun = false;
            await ConfigHandler.SaveConfig(_config);
            SetStatus("TUN requires administrator privileges");
            return false;
        }

        _config.TunModeItem.AutoRoute = true;
        _config.TunModeItem.StrictRoute = true;
        if (_config.TunModeItem.Mtu <= 0)
        {
            _config.TunModeItem.Mtu = Global.TunMtus.First();
        }
        if (_config.TunModeItem.Stack.IsNullOrEmpty() || ZapretEnabled)
        {
            _config.TunModeItem.Stack = "system";
        }

        var ready = await EnsureSingboxCoreAsync();
        if (!ready)
        {
            TunEnabled = false;
            _config.TunModeItem.EnableTun = false;
            await ConfigHandler.SaveConfig(_config);
            SetStatus("sing-box core not found. Put sing-box.exe into bin\\sing_box or use resources.");
            return false;
        }

        await ConfigHandler.SaveConfig(_config);
        return true;
    }

    private async Task<bool> EnsureActiveCoreReadyAsync()
    {
        if (TunEnabled)
        {
            return await EnsureTunReadyAsync();
        }

        var profile = SelectedProfile != null
            ? await AppManager.Instance.GetProfileItem(SelectedProfile.IndexId)
            : await ConfigHandler.GetDefaultServer(_config);

        if (profile == null)
        {
            SetStatus("Select a profile first");
            return false;
        }

        var coreType = AppManager.Instance.GetCoreType(profile, profile.ConfigType);
        return coreType switch
        {
            ECoreType.sing_box or ECoreType.mihomo => await EnsureSingboxCoreAsync(),
            _ => await EnsureXrayCoreAsync()
        };
    }

    private async void OnToggleZapret(object sender, RoutedEventArgs e)
    {
        if (ZapretEnabled)
        {
            await CancelZapretAutoTestAsync();
            if (TunEnabled && !_config.TunModeItem.Stack.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                _config.TunModeItem.Stack = "system";
                _ = ConfigHandler.SaveConfig(_config);
                if (VpnEnabled)
                {
                    _ = ViewModel.Reload();
                }
            }
            OnStartZapret(sender, e);
        }
        else
        {
            OnStopZapret(sender, e);
        }
    }

    private async Task CancelZapretAutoTestAsync()
    {
        if (_zapretAutoTestTask == null || !_isAutoTestingZapret)
        {
            return;
        }

        _zapretAutoTestCts?.Cancel();
        try
        {
            await _zapretAutoTestTask;
        }
        catch (OperationCanceledException)
        {
            // ignore cancellation
        }
    }

    private void LoadAppearanceOptions()
    {
        PrimaryColors.Clear();
    }

    private void LoadInterfaceVariants()
    {
        InterfaceVariants.Clear();
        InterfaceVariants.Add(new()
        {
            Key = DefaultInterfacePresetKey,
            Title = "Night Shift",
            Description = "Тёмная базовая схема с компактными контрастами и спокойной рабочей подачей.",
            IsLight = false,
            WindowBackgroundColor = (Color)ColorConverter.ConvertFromString("#0B1220"),
            WindowChromeColor = (Color)ColorConverter.ConvertFromString("#253246"),
            SurfaceColor = (Color)ColorConverter.ConvertFromString("#111A2B"),
            SurfaceAltColor = (Color)ColorConverter.ConvertFromString("#162235"),
            SurfaceHeaderColor = (Color)ColorConverter.ConvertFromString("#182538"),
            MutedTextColor = (Color)ColorConverter.ConvertFromString("#8EA0B8"),
            StrongTextColor = (Color)ColorConverter.ConvertFromString("#E8EEF7"),
            HeroStartColor = (Color)ColorConverter.ConvertFromString("#0D1626"),
            HeroEndColor = (Color)ColorConverter.ConvertFromString("#111C2F"),
            FooterColor = (Color)ColorConverter.ConvertFromString("#0E1727")
        });
        InterfaceVariants.Add(new()
        {
            Key = "CarbonBlue",
            Title = "Carbon Blue",
            Description = "Более холодный тёмный вариант с выраженным синим акцентом и плотной сеткой поверхностей.",
            IsLight = false,
            WindowBackgroundColor = (Color)ColorConverter.ConvertFromString("#0A1020"),
            WindowChromeColor = (Color)ColorConverter.ConvertFromString("#22314A"),
            SurfaceColor = (Color)ColorConverter.ConvertFromString("#0F182A"),
            SurfaceAltColor = (Color)ColorConverter.ConvertFromString("#14213A"),
            SurfaceHeaderColor = (Color)ColorConverter.ConvertFromString("#172742"),
            MutedTextColor = (Color)ColorConverter.ConvertFromString("#8CA3C3"),
            StrongTextColor = (Color)ColorConverter.ConvertFromString("#E7F0FF"),
            HeroStartColor = (Color)ColorConverter.ConvertFromString("#0E1830"),
            HeroEndColor = (Color)ColorConverter.ConvertFromString("#12203D"),
            FooterColor = (Color)ColorConverter.ConvertFromString("#0B1324")
        });
        InterfaceVariants.Add(new()
        {
            Key = "SlateMono",
            Title = "Slate Mono",
            Description = "Нейтральный графитовый пресет без яркой подачи, если нужен максимально спокойный фон.",
            IsLight = false,
            WindowBackgroundColor = (Color)ColorConverter.ConvertFromString("#101318"),
            WindowChromeColor = (Color)ColorConverter.ConvertFromString("#303640"),
            SurfaceColor = (Color)ColorConverter.ConvertFromString("#151A21"),
            SurfaceAltColor = (Color)ColorConverter.ConvertFromString("#1A2028"),
            SurfaceHeaderColor = (Color)ColorConverter.ConvertFromString("#212933"),
            MutedTextColor = (Color)ColorConverter.ConvertFromString("#98A2B3"),
            StrongTextColor = (Color)ColorConverter.ConvertFromString("#F2F5F8"),
            HeroStartColor = (Color)ColorConverter.ConvertFromString("#161B22"),
            HeroEndColor = (Color)ColorConverter.ConvertFromString("#1B222B"),
            FooterColor = (Color)ColorConverter.ConvertFromString("#12161C")
        });
    }

    private void LoadCustomAppearance()
    {
        UseCustomPrimaryColor = true;

        var customColor = TryParseColor(_config.UiItem.CustomPrimaryColor);
        if (customColor.HasValue)
        {
            var hsv = ColorToHsv(customColor.Value);
            _customHue = hsv.Hue;
            _customSaturation = hsv.Saturation;
            _customValue = hsv.Value;
        }
        else
        {
            var fallback = (Color)ColorConverter.ConvertFromString("#4F8CFF");
            var hsv = ColorToHsv(fallback);
            _customHue = hsv.Hue;
            _customSaturation = hsv.Saturation;
            _customValue = hsv.Value;
        }
    }

    private void ApplyAppearance()
    {
        var variant = GetActiveInterfaceVariant();
        _config.UiItem.MainWindowPreset = variant.Key;
        _config.UiItem.CurrentTheme = variant.IsLight ? nameof(ETheme.Light) : nameof(ETheme.Dark);
        _config.UiItem.ColorPrimaryName = "Custom";
        _config.UiItem.UseCustomPrimaryColor = true;
        _config.UiItem.CustomPrimaryColor = CustomPrimaryColorHex;

        var theme = _paletteHelper.GetTheme();
        theme.SetBaseTheme(variant.IsLight ? BaseTheme.Light : BaseTheme.Dark);

        var color = GetSelectedPrimaryColor();
        theme.PrimaryLight = new ColorPair(color.Lighten());
        theme.PrimaryMid = new ColorPair(color);
        theme.PrimaryDark = new ColorPair(color.Darken());
        theme.SecondaryLight = new ColorPair(color.Lighten());
        theme.SecondaryMid = new ColorPair(color);
        theme.SecondaryDark = new ColorPair(color.Darken());
        _paletteHelper.SetTheme(theme);

        ApplyInterfacePresetResources(variant, color);
        WindowsUtils.SetDarkBorder(this, _config.UiItem.CurrentTheme);
    }

    private InterfaceVariantOption GetActiveInterfaceVariant()
    {
        return SelectedInterfaceVariant
            ?? InterfaceVariants.FirstOrDefault(t => string.Equals(t.Key, _config.UiItem.MainWindowPreset, StringComparison.OrdinalIgnoreCase))
            ?? InterfaceVariants.First();
    }

    private void ApplyInterfacePresetResources(InterfaceVariantOption variant, Color accentColor)
    {
        SetThemeResource("NetCatWindowBackgroundBrush", CreateFrozenBrush(variant.WindowBackgroundColor));
        SetThemeResource("NetCatWindowChromeBrush", CreateFrozenBrush(variant.WindowChromeColor));
        SetThemeResource("NetCatSurfaceBrush", CreateFrozenBrush(variant.SurfaceColor));
        SetThemeResource("NetCatSurfaceAltBrush", CreateFrozenBrush(variant.SurfaceAltColor));
        SetThemeResource("NetCatSurfaceHeaderBrush", CreateFrozenBrush(variant.SurfaceHeaderColor));
        SetThemeResource("NetCatMutedTextBrush", CreateFrozenBrush(variant.MutedTextColor));
        SetThemeResource("NetCatStrongTextBrush", CreateFrozenBrush(variant.StrongTextColor));
        SetThemeResource("NetCatFooterBrush", CreateFrozenBrush(variant.FooterColor));
        SetThemeResource("NetCatAccentBrush", CreateFrozenBrush(accentColor));
        SetThemeResource("NetCatAccentSoftBrush", CreateFrozenBrush(Color.FromArgb(48, accentColor.R, accentColor.G, accentColor.B)));
        SetThemeResource("NetCatScrollBarTrackBrush", CreateFrozenBrush(Color.FromArgb(48, accentColor.R, accentColor.G, accentColor.B)));
        SetThemeResource("NetCatScrollBarThumbBrush", CreateFrozenBrush(Color.FromArgb(200, accentColor.R, accentColor.G, accentColor.B)));
        SetThemeResource("NetCatScrollBarThumbBorderBrush", CreateFrozenBrush(accentColor.Lighten()));
        SetThemeResource("NetCatScrollBarThumbHoverBrush", CreateFrozenBrush(accentColor.Lighten()));
        SetThemeResource("NetCatScrollBarThumbDragBrush", CreateFrozenBrush(accentColor.Darken()));
        SetThemeResource("NetCatHeroGradientBrush", CreateFrozenGradientBrush(variant.HeroStartColor, variant.HeroEndColor));
        Background = CreateFrozenBrush(variant.WindowBackgroundColor);
    }

    private void SetThemeResource(string key, object value)
    {
        Resources[key] = value;
        if (Application.Current != null)
        {
            Application.Current.Resources[key] = value;
        }
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush CreateFrozenGradientBrush(Color startColor, Color endColor)
    {
        var brush = new LinearGradientBrush(startColor, endColor, 25);
        brush.Freeze();
        return brush;
    }

    private Color GetSelectedPrimaryColor()
    {
        return ColorFromHsv(CustomHue, CustomSaturation, CustomValue);
    }

    private void UpdateCustomColorFromPoint(Point point)
    {
        var x = Math.Clamp(point.X, 0, CustomColorPlaneWidth);
        var y = Math.Clamp(point.Y, 0, CustomColorPlaneHeight);
        CustomSaturation = x / CustomColorPlaneWidth;
        CustomValue = 1 - (y / CustomColorPlaneHeight);
    }

    private void NotifyCustomColorStateChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomPrimaryBaseBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomPrimaryPreviewBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomPrimaryColorHex)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomColorCursorLeft)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomColorCursorTop)));
    }

    private static Color? TryParseColor(string? value)
    {
        if (value.IsNullOrEmpty())
        {
            return null;
        }

        try
        {
            var converted = ColorConverter.ConvertFromString(value);
            if (converted is Color color)
            {
                return color;
            }
        }
        catch
        {
            // ignore parse errors
        }

        return null;
    }

    private static Color ColorFromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);

        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60.0 % 2) - 1));
        var m = value - chroma;

        (double r, double g, double b) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x),
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static (double Hue, double Saturation, double Value) ColorToHsv(Color color)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double hue;
        if (delta == 0)
        {
            hue = 0;
        }
        else if (max == r)
        {
            hue = 60 * (((g - b) / delta) % 6);
        }
        else if (max == g)
        {
            hue = 60 * (((b - r) / delta) + 2);
        }
        else
        {
            hue = 60 * (((r - g) / delta) + 4);
        }

        if (hue < 0)
        {
            hue += 360;
        }

        var saturation = max == 0 ? 0 : delta / max;
        return (hue, saturation, max);
    }

    private async Task<ZapretTestResult?> RunZapretTestAsync(string title, bool keepRunning)
    {
        if (ZapretPath.IsNullOrEmpty() || SelectedZapretConfig?.Name.IsNullOrEmpty() != false)
        {
            SetZapretStatus("Select config");
            return null;
        }

        SetZapretStatus($"{title}...");
        await Task.Delay(100);
        try
        {
            var result = await ZapretHandler.TestConfigAsync(ZapretPath, SelectedZapretConfig.Name, keepRunning);
            ZapretRunning = ZapretHandler.IsRunning();
            ZapretEnabled = ZapretRunning;
            return result;
        }
        catch (Exception ex)
        {
            SetZapretStatus($"Test failed: {ex.Message}");
            return null;
        }
    }

    private void RefreshRunningProcesses()
    {
        var previousPath = SelectedRunningProcess?.FilePath;
        RunningProcesses.Clear();

        var items = Process.GetProcesses()
            .Select(TryCreateRunningProcessItem)
            .Where(t => t != null)
            .Cast<RunningProcessItem>()
            .GroupBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(t => t.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var item in items)
        {
            RunningProcesses.Add(item);
        }

        ApplyRunningProcessFilter();
        SelectedRunningProcess = RunningProcesses
            .FirstOrDefault(t => string.Equals(t.FilePath, previousPath, StringComparison.OrdinalIgnoreCase) && FilterRunningProcess(t))
            ?? RunningProcesses.FirstOrDefault(t => FilterRunningProcess(t));
    }

    private void ApplyRunningProcessFilter()
    {
        RunningProcessesView.Refresh();

        if (SelectedRunningProcess != null && FilterRunningProcess(SelectedRunningProcess))
        {
            return;
        }

        SelectedRunningProcess = RunningProcesses.FirstOrDefault(t => FilterRunningProcess(t));
    }

    private bool FilterRunningProcess(object? item)
    {
        if (item is not RunningProcessItem processItem)
        {
            return false;
        }

        var query = RunningProcessSearchText?.Trim();
        if (query.IsNullOrEmpty())
        {
            return true;
        }

        return processItem.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
               || processItem.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase)
               || processItem.FilePath.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static RunningProcessItem? TryCreateRunningProcessItem(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return null;
            }

            var filePath = process.MainModule?.FileName ?? string.Empty;
            if (filePath.IsNullOrEmpty() || !File.Exists(filePath))
            {
                return null;
            }

            var processName = process.ProcessName;
            var fileName = Path.GetFileName(filePath);
            var title = process.MainWindowTitle?.Trim();
            var displayName = title.IsNullOrEmpty()
                ? $"{fileName} (PID {process.Id})"
                : $"{fileName} - {title} (PID {process.Id})";

            return new RunningProcessItem
            {
                ProcessId = process.Id,
                ProcessName = processName,
                FilePath = filePath,
                DisplayName = displayName
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            process.Dispose();
        }
    }

    private void AddDirectAppEntries(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath);
        if (!fileName.IsNullOrEmpty()
            && !DirectApps.Any(t => string.Equals(t, fileName, StringComparison.OrdinalIgnoreCase)))
        {
            DirectApps.Add(fileName);
        }

        if (!fullPath.IsNullOrEmpty()
            && !DirectApps.Any(t => string.Equals(t, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            DirectApps.Add(fullPath);
        }
    }

    private void AddProxyAppEntries(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath);
        if (!fileName.IsNullOrEmpty()
            && !ProxyApps.Any(t => string.Equals(t, fileName, StringComparison.OrdinalIgnoreCase)))
        {
            ProxyApps.Add(fileName);
        }

        if (!fullPath.IsNullOrEmpty()
            && !ProxyApps.Any(t => string.Equals(t, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            ProxyApps.Add(fullPath);
        }
    }

    private void UpdateZapretConfigResult(string? configName, ZapretTestResult result)
    {
        if (configName.IsNullOrEmpty())
        {
            return;
        }

        var item = ZapretConfigs.FirstOrDefault(t => string.Equals(t.Name, configName, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            return;
        }

        item.HasTestResult = true;
        item.IsPassing = result.Success;
        item.YouTubeLabel = FormatZapretProbeLabel("YouTube", result.YoutubeSuccess, result.YoutubePingMs, result.YoutubeHttpMs);
        item.DiscordLabel = FormatZapretProbeLabel("Discord", result.DiscordSuccess, result.DiscordPingMs, result.DiscordHttpMs);
    }

    private static string FormatZapretProbeLabel(string service, bool success, long? pingMs, long? httpMs)
    {
        var metric = httpMs ?? pingMs;

        if (!success)
        {
            if (httpMs.HasValue)
            {
                return $"{service}: http {httpMs.Value} ms, failed";
            }

            if (pingMs.HasValue)
            {
                return $"{service}: ping {pingMs.Value} ms, failed";
            }

            return $"{service}: failed";
        }

        return metric.HasValue
            ? $"{service}: http {metric.Value} ms"
            : $"{service}: ok";
    }

    private void ConnectionPingTimer_Tick(object? sender, EventArgs e)
    {
        _ = UpdateConnectionPingAsync();
    }

    private async Task UpdateConnectionPingAsync()
    {
        if (_isUpdatingConnectionPing)
        {
            return;
        }

        _isUpdatingConnectionPing = true;
        try
        {
            var activeProfile = Profiles.FirstOrDefault(t => t.IsActive) ?? SelectedProfile;
            ConnectionPing = await GetProfilePingLabelAsync(activeProfile, includePrefix: true);
        }
        finally
        {
            _isUpdatingConnectionPing = false;
        }
    }

    private async Task<string> GetProfilePingLabelAsync(ProfileItemModel? profile, bool includePrefix = false)
    {
        var prefix = includePrefix ? "Connection ping: " : string.Empty;
        var proxyPing = await MeasureProxyPingAsync();
        if (proxyPing.HasValue)
        {
            return $"{prefix}{proxyPing.Value} ms via proxy";
        }

        if (profile == null)
        {
            return $"{prefix}no active profile";
        }

        if (profile.Address.IsNullOrEmpty() || profile.Port <= 0)
        {
            return $"{prefix}n/a";
        }

        var pingMs = await MeasureTcpPingAsync(profile.Address, profile.Port);
        if (pingMs.HasValue && pingMs.Value >= 0)
        {
            return $"{prefix}{profile.Address}:{profile.Port} {pingMs.Value} ms";
        }

        return $"{prefix}{profile.Address}:{profile.Port} unavailable";
    }

    private async Task<int?> MeasureProxyPingAsync()
    {
        var coreRunning = Process.GetProcessesByName("xray").Length > 0
                          || Process.GetProcessesByName("sing-box").Length > 0
                          || Process.GetProcessesByName("mihomo").Length > 0;
        if (!coreRunning || (!VpnEnabled && !TunEnabled))
        {
            return null;
        }

        try
        {
            var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
            var webProxy = new WebProxy($"socks5://{Global.Loopback}:{port}");
            var url = _config.SpeedTestItem.SpeedPingTestUrl;
            var delay = await ConnectionHandler.GetRealPingTime(url, webProxy, 10);
            return delay > 0 ? delay : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<int?> MeasureTcpPingAsync(string host, int port)
    {
        if (host.IsNullOrEmpty() || port <= 0)
        {
            return null;
        }

        try
        {
            IPAddress ipAddress;
            if (!IPAddress.TryParse(host, out ipAddress!))
            {
                var hostEntry = await Dns.GetHostEntryAsync(host);
                ipAddress = hostEntry.AddressList
                    .FirstOrDefault(t => t.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    ?? hostEntry.AddressList.First();
            }

            var endPoint = new IPEndPoint(ipAddress, port);
            using var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var timer = Stopwatch.StartNew();
            try
            {
                await socket.ConnectAsync(endPoint, cts.Token).ConfigureAwait(false);
                timer.Stop();
                return (int)timer.ElapsedMilliseconds;
            }
            finally
            {
                timer.Stop();
            }
        }
        catch
        {
            return null;
        }
    }

    private void RegisterSingleInstanceRestore()
    {
        if (App.ProgramStarted == null)
        {
            return;
        }

        _singleInstanceWaitHandle = ThreadPool.RegisterWaitForSingleObject(
            App.ProgramStarted,
            (_, _) =>
            {
                Dispatcher.BeginInvoke(new Action(RestoreWindowFromTray));
            },
            null,
            Timeout.Infinite,
            false);
    }

    private bool ShouldHideWindowOnStartup()
    {
        return App.StartMinimizedToTray || _config.UiItem.AutoHideStartup;
    }

    private void HideWindowToTray()
    {
        ShowInTaskbar = false;
        WindowState = WindowState.Minimized;
        Hide();
        SetStatus("Application hidden to tray");
    }

    private void RestoreWindowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        UpdateTrayToolTip();
    }

    private void TrayIcon_TrayLeftMouseUp(object sender, RoutedEventArgs e)
    {
        RestoreWindowFromTray();
    }

    private void OnTrayOpen(object sender, RoutedEventArgs e)
    {
        RestoreWindowFromTray();
    }

    private void OnTrayToggleVpn(object sender, RoutedEventArgs e)
    {
        MainVpnEnabled = !MainVpnEnabled;
        OnToggleMainVpn(sender, e);
    }

    private void OnTrayToggleZapret(object sender, RoutedEventArgs e)
    {
        ZapretEnabled = !ZapretEnabled;
        OnToggleZapret(sender, e);
    }

    private async void OnTrayExit(object sender, RoutedEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        TrayIcon.Dispose();
        await AppManager.Instance.AppExitAsync(true);
    }

    private void UpdateTrayToolTip()
    {
        var vpnState = MainVpnEnabled ? "ON" : "OFF";
        var vpnMode = EncryptAllTraffic ? "Full" : "Proxy";
        var zapretState = ZapretEnabled ? "ON" : "OFF";
        TrayToolTip = $"NetCat | VPN: {vpnState} ({vpnMode}) | Zapret: {zapretState}{Environment.NewLine}{ConnectionPing}";
    }

    private void OpenPath(string path)
    {
        if (path.IsNullOrEmpty())
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to open path: {ex.Message}");
        }
    }

    private void TryShowAutoRunSecret()
    {
        if (!IsLoaded)
        {
            return;
        }

        _autoRunSecretClickCount++;
        if (_autoRunSecretClickCount < SecretAutoRunClickThreshold)
        {
            return;
        }

        _autoRunSecretClickCount = 0;
        var secretImagePath = Utils.GetPath(SecretAssetName);
        if (!File.Exists(secretImagePath))
        {
            SetStatus($"{SecretAssetName} not found");
            return;
        }

        try
        {
            var encryptedBytes = File.ReadAllBytes(secretImagePath);
            var decryptedBytes = DecryptSecretBytes(encryptedBytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(decryptedBytes, writable: false);
            bitmap.EndInit();
            bitmap.Freeze();

            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(12)
            };

            var window = new Window
            {
                Title = "Secret",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = Math.Min(Math.Max(bitmap.PixelWidth + 48, 420), 1100),
                Height = Math.Min(Math.Max(bitmap.PixelHeight + 72, 360), 900),
                MinWidth = 360,
                MinHeight = 280,
                Icon = this.Icon,
                Background = Brushes.Black,
                Content = new Border
                {
                    Background = Brushes.Black,
                    Padding = new Thickness(8),
                    Child = new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = image
                    }
                }
            };

            window.ShowDialog();
            SetStatus("Secret unlocked");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to open secret: {ex.Message}");
        }
    }

    private static byte[] DecryptSecretBytes(byte[] encryptedBytes)
    {
        var result = new byte[encryptedBytes.Length];
        for (var i = 0; i < encryptedBytes.Length; i++)
        {
            result[i] = (byte)(encryptedBytes[i] ^ SecretKey[i % SecretKey.Length]);
        }

        return result;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class InterfaceVariantOption
{
    public string Key { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsLight { get; init; }
    public Color WindowBackgroundColor { get; init; }
    public Color WindowChromeColor { get; init; }
    public Color SurfaceColor { get; init; }
    public Color SurfaceAltColor { get; init; }
    public Color SurfaceHeaderColor { get; init; }
    public Color MutedTextColor { get; init; }
    public Color StrongTextColor { get; init; }
    public Color HeroStartColor { get; init; }
    public Color HeroEndColor { get; init; }
    public Color FooterColor { get; init; }
}
