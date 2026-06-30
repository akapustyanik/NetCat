using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
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
using ServiceLib.Common;
using ServiceLib.Handler;
using ServiceLib.Handler.Builder;
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
    private static readonly HttpClient FlagImageHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private readonly Config _config;
    private readonly PaletteHelper _paletteHelper = new();
    private readonly ServerCountryLookup _serverCountryLookup = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<string>>> _countryFlagCache = new(StringComparer.OrdinalIgnoreCase);
    private QuickRuleConfig _quickRules;
    private ParallelOpenVpnConfig _parallelOpenVpnConfig = new();
    private readonly HashSet<string> _parallelOpenVpnInjectedDirectDomains = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _connectionPingTimer;
    private Process? _parallelOpenVpnProcess;
    private bool _closing;
    private bool _isPickingCustomColor;
    private bool _isPickingInterfaceColor;
    private bool _isUpdatingConnectionPing;
    private bool _isRefreshingZapretConfigs;
    private bool _isSwitchingZapretConfig;
    private bool _isEditingProfile;
    private bool _isAutoTestingZapret;
    private bool _startupUiHandled;
    private bool _startupZapretRestorePending = true;
    private bool _startupUpdateCheckStarted;
    private bool _suppressConnectionToggleEvents;
    private bool _suppressAutoFailoverEvents;
    private bool _isLoadingAutoFailoverSelection;
    private bool _isLoadingParallelOpenVpnDomains;
    private SpeedtestService? _profileSpeedtestService;
    private int _autoRunSecretClickCount;
    private int _profileCountryRefreshVersion;
    private CancellationTokenSource? _zapretAutoTestCts;
    private Task? _zapretAutoTestTask;
    private RegisteredWaitHandle? _singleInstanceWaitHandle;

    public ObservableCollection<ProfileItemModel> Profiles { get; } = new();
    public ObservableCollection<string> DirectApps { get; } = new();
    public ObservableCollection<string> DirectDomains { get; } = new();
    public ObservableCollection<string> ProxyApps { get; } = new();
    public ObservableCollection<string> ProxyDomains { get; } = new();
    public ObservableCollection<string> BlockDomains { get; } = new();
    public ObservableCollection<ParallelOpenVpnProfile> ParallelOpenVpnProfiles { get; } = new();
    public ObservableCollection<string> ParallelOpenVpnDomains { get; } = new();
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

    private string? _selectedParallelOpenVpnDomain;
    public string? SelectedParallelOpenVpnDomain
    {
        get => _selectedParallelOpenVpnDomain;
        set => SetField(ref _selectedParallelOpenVpnDomain, value);
    }

    private ParallelOpenVpnProfile? _selectedParallelOpenVpnProfile;
    public ParallelOpenVpnProfile? SelectedParallelOpenVpnProfile
    {
        get => _selectedParallelOpenVpnProfile;
        set
        {
            if (SetField(ref _selectedParallelOpenVpnProfile, value))
            {
                LoadSelectedParallelOpenVpnDomains();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedParallelOpenVpnConfigPath)));
            }
        }
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

    private string _newParallelOpenVpnDomain = string.Empty;
    public string NewParallelOpenVpnDomain
    {
        get => _newParallelOpenVpnDomain;
        set => SetField(ref _newParallelOpenVpnDomain, value);
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    private string _parallelOpenVpnStatus = "OpenVPN stopped";
    public string ParallelOpenVpnStatus
    {
        get => _parallelOpenVpnStatus;
        set => SetField(ref _parallelOpenVpnStatus, value);
    }

    private bool _parallelOpenVpnRunning;
    public bool ParallelOpenVpnRunning
    {
        get => _parallelOpenVpnRunning;
        set => SetField(ref _parallelOpenVpnRunning, value);
    }

    public string SelectedParallelOpenVpnConfigPath => SelectedParallelOpenVpnProfile?.ConfigPath ?? string.Empty;

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

    private bool _autoProtocolFailoverEnabled;
    public bool AutoProtocolFailoverEnabled
    {
        get => _autoProtocolFailoverEnabled;
        set
        {
            if (SetField(ref _autoProtocolFailoverEnabled, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoFailoverCandidateVisibility)));
            }
        }
    }

    private double _autoProtocolFailoverStandbyCount = 1;
    public double AutoProtocolFailoverStandbyCount
    {
        get => _autoProtocolFailoverStandbyCount;
        set => SetField(ref _autoProtocolFailoverStandbyCount, Math.Clamp(Math.Round(value), 1, 8));
    }

    public Visibility AutoFailoverCandidateVisibility => AutoProtocolFailoverEnabled
        ? Visibility.Visible
        : Visibility.Collapsed;

    private bool _telegramUseLocalSocks;
    public bool TelegramUseLocalSocks
    {
        get => _telegramUseLocalSocks;
        set
        {
            if (SetField(ref _telegramUseLocalSocks, value))
            {
                NotifyTelegramStateChanged();
            }
        }
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

    public string TelegramTrafficSummary => TelegramWsProxyHandler.GetTrafficModeSummary(
        TelegramUseLocalSocks ? QuickRuleConfig.TelegramTrafficModeLocalSocks : QuickRuleConfig.TelegramTrafficModeVpn);

    public string TelegramTrafficModeLabel => TelegramUseLocalSocks ? "Локальный SOCKS5" : "VPN";

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

    private double _interfaceHue = 220;
    public double InterfaceHue
    {
        get => _interfaceHue;
        set
        {
            if (SetField(ref _interfaceHue, value))
            {
                NotifyInterfaceColorStateChanged();
            }
        }
    }

    private double _interfaceSaturation = 0.55;
    public double InterfaceSaturation
    {
        get => _interfaceSaturation;
        set
        {
            if (SetField(ref _interfaceSaturation, value))
            {
                NotifyInterfaceColorStateChanged();
            }
        }
    }

    private double _interfaceValue = 0.18;
    public double InterfaceValue
    {
        get => _interfaceValue;
        set
        {
            if (SetField(ref _interfaceValue, value))
            {
                NotifyInterfaceColorStateChanged();
            }
        }
    }

    public Brush InterfaceBaseBrush => new SolidColorBrush(ColorFromHsv(InterfaceHue, 1, 1));
    public Brush InterfacePreviewBrush => new SolidColorBrush(GetSelectedInterfaceColor());
    public string InterfaceColorHex => $"#{GetSelectedInterfaceColor().R:X2}{GetSelectedInterfaceColor().G:X2}{GetSelectedInterfaceColor().B:X2}";
    public double InterfaceColorCursorLeft => Math.Clamp(InterfaceSaturation * CustomColorPlaneWidth - 6, -6, CustomColorPlaneWidth - 6);
    public double InterfaceColorCursorTop => Math.Clamp((1 - InterfaceValue) * CustomColorPlaneHeight - 6, -6, CustomColorPlaneHeight - 6);

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
        _parallelOpenVpnConfig = ParallelOpenVpnConfigHandler.Load();
        RunningProcessesView = CollectionViewSource.GetDefaultView(RunningProcesses);
        RunningProcessesView.Filter = FilterRunningProcess;

        ViewModel = new MainWindowViewModel((_, _) => Task.FromResult(false));
        DataContext = this;

        AutoRun = _config.GuiItem.AutoRun;
        HideToTrayOnClose = _config.UiItem.Hide2TrayWhenClose;
        BypassPrivate = _quickRules.BypassPrivate;
        ProxyOnlyMode = _quickRules.ProxyOnlyMode;
        UseProxyDomainsPreset = _quickRules.UseProxyDomainsPreset;
        AutoProtocolFailoverEnabled = _config.UiItem.AutoProtocolFailoverEnabled;
        AutoProtocolFailoverStandbyCount = Math.Clamp(_config.UiItem.AutoProtocolFailoverStandbyCount <= 0 ? 1 : _config.UiItem.AutoProtocolFailoverStandbyCount, 1, 8);
        TelegramUseLocalSocks = TelegramWsProxyHandler.IsLocalSocksMode(_quickRules.TelegramTrafficMode);
        TunEnabled = _config.TunModeItem.EnableTun;
        LoadCustomAppearance();
        ApplyAppearance();
        LoadQuickLists();
        LoadParallelOpenVpnProfiles();
        RefreshRunningProcesses();
        VpnEnabled = _config.SystemProxyItem.SysProxyType == ESysProxyType.ForcedChange;
        EncryptAllTraffic = _config.UiItem.PreferFullTrafficVpn || (!VpnEnabled && TunEnabled);
        MainVpnEnabled = VpnEnabled || TunEnabled;
        if (ShouldHideWindowOnStartup())
        {
            WindowState = WindowState.Minimized;
        }

        _ = ApplyQuickRulesAsync(reload: false);
        _ = EnsureTelegramTrafficModeAsync(openInTelegram: false);
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

        foreach (var domain in _quickRules.DirectDomains.Select(NormalizeDomainRule).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(domain))
            {
                DirectDomains.Add(domain);
            }
        }

        foreach (var domain in _quickRules.BlockDomains.Select(NormalizeDomainRule).Distinct(StringComparer.OrdinalIgnoreCase))
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

        foreach (var domain in _quickRules.ProxyDomains.Select(NormalizeDomainRule).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(domain))
            {
                ProxyDomains.Add(domain);
            }
        }
    }

    private void LoadParallelOpenVpnProfiles()
    {
        ParallelOpenVpnProfiles.Clear();
        foreach (var profile in _parallelOpenVpnConfig.Profiles
                     .Where(profile => !profile.Id.IsNullOrEmpty() && !profile.ConfigPath.IsNullOrEmpty()))
        {
            profile.IsRunning = false;
            profile.Status = File.Exists(profile.ConfigPath) ? "Stopped" : "Config file missing";
            if (profile.Name.IsNullOrEmpty())
            {
                profile.Name = Path.GetFileNameWithoutExtension(profile.ConfigPath);
            }

            ParallelOpenVpnProfiles.Add(profile);
        }

        SelectedParallelOpenVpnProfile = _parallelOpenVpnConfig.SelectedProfileId.IsNullOrEmpty()
            ? ParallelOpenVpnProfiles.FirstOrDefault()
            : ParallelOpenVpnProfiles.FirstOrDefault(profile => string.Equals(profile.Id, _parallelOpenVpnConfig.SelectedProfileId, StringComparison.OrdinalIgnoreCase))
              ?? ParallelOpenVpnProfiles.FirstOrDefault();

        ParallelOpenVpnStatus = FindOpenVpnExecutable().IsNullOrEmpty()
            ? "OpenVPN core not found: put openvpn.exe into bin\\openvpn or install OpenVPN."
            : "OpenVPN ready";
    }

    private void LoadSelectedParallelOpenVpnDomains()
    {
        _isLoadingParallelOpenVpnDomains = true;
        try
        {
            ParallelOpenVpnDomains.Clear();
            foreach (var domain in (SelectedParallelOpenVpnProfile?.Domains ?? [])
                         .Select(NormalizeDomainRule)
                         .Where(domain => !domain.IsNullOrEmpty())
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ParallelOpenVpnDomains.Add(domain);
            }
        }
        finally
        {
            _isLoadingParallelOpenVpnDomains = false;
        }
    }

    private void SaveSelectedParallelOpenVpnDomains()
    {
        if (_isLoadingParallelOpenVpnDomains || SelectedParallelOpenVpnProfile == null)
        {
            return;
        }

        SelectedParallelOpenVpnProfile.Domains = ParallelOpenVpnDomains
            .Where(domain => !domain.IsNullOrEmpty())
            .Select(NormalizeDomainRule)
            .Where(domain => !domain.IsNullOrEmpty())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task SaveParallelOpenVpnConfigAsync()
    {
        SaveSelectedParallelOpenVpnDomains();
        _parallelOpenVpnConfig.SelectedProfileId = SelectedParallelOpenVpnProfile?.Id;
        _parallelOpenVpnConfig.Profiles = ParallelOpenVpnProfiles
            .Select(profile => new ParallelOpenVpnProfile
            {
                Id = profile.Id,
                Name = profile.Name,
                ConfigPath = profile.ConfigPath,
                Domains = profile.Domains
                    .Where(domain => !domain.IsNullOrEmpty())
                    .Select(NormalizeDomainRule)
                    .Where(domain => !domain.IsNullOrEmpty())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                IsRunning = false,
                Status = "Stopped"
            })
            .ToList();
        await ParallelOpenVpnConfigHandler.Save(_parallelOpenVpnConfig);
    }

    private async Task RefreshProfilesAsync()
    {
        await ClearAutoFailoverRuntimeGroupAsync();

        _isLoadingAutoFailoverSelection = true;
        List<ProfileItemModel> snapshot;
        try
        {
            var items = await AppManager.Instance.ProfileModels("", "") ?? new List<ProfileItemModel>();
            var rawItems = await AppManager.Instance.ProfileItems("") ?? new List<ProfileItem>();
            var rawItemById = rawItems
                .Where(item => !item.IndexId.IsNullOrEmpty())
                .GroupBy(item => item.IndexId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var profileExs = await ProfileExManager.Instance.GetProfileExs();
            var autoFailoverGroupId = _config.UiItem.AutoProtocolFailoverGroupId;
            if (!autoFailoverGroupId.IsNullOrEmpty())
            {
                items = items
                    .Where(item => !string.Equals(item.IndexId, autoFailoverGroupId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var preferredIndexId = SelectedProfile?.IndexId ?? _config.IndexId;
            var autoFailoverIds = GetAutoFailoverProfileIds();
            var prunedAutoFailoverSelection = false;
            foreach (var item in items)
            {
                item.IsAutoFailoverEligible = rawItemById.TryGetValue(item.IndexId, out var rawItem)
                                              && IsAutoFailoverCompatibleWithSingbox(rawItem, out _);
                item.IsActive = item.IndexId == _config.IndexId
                                || (AutoProtocolFailoverEnabled
                                    && string.Equals(_config.IndexId, autoFailoverGroupId, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(item.IndexId, _config.UiItem.AutoProtocolFailoverPrimaryId, StringComparison.OrdinalIgnoreCase));
                item.IsAutoFailoverCandidate = item.IsAutoFailoverEligible && autoFailoverIds.Contains(item.IndexId);
                if (!item.IsAutoFailoverEligible && autoFailoverIds.Contains(item.IndexId))
                {
                    prunedAutoFailoverSelection = true;
                }
                var profileEx = profileExs.FirstOrDefault(profileExItem => profileExItem.IndexId == item.IndexId);
                item.Delay = profileEx?.Delay ?? 0;
                item.DelayVal = profileEx?.Delay > 0
                    ? $"{profileEx.Delay} ms"
                    : profileEx?.Message.IsNotEmpty() == true ? profileEx.Message : "Not tested";
            }
            if (prunedAutoFailoverSelection)
            {
                var ids = items
                    .Where(item => item.IsAutoFailoverCandidate)
                    .Select(item => item.IndexId)
                    .Where(id => !id.IsNullOrEmpty())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _config.UiItem.AutoProtocolFailoverProfileIds = Utils.List2String(ids);
                if (!_config.UiItem.AutoProtocolFailoverPrimaryId.IsNullOrEmpty()
                    && !ids.Contains(_config.UiItem.AutoProtocolFailoverPrimaryId, StringComparer.OrdinalIgnoreCase))
                {
                    _config.UiItem.AutoProtocolFailoverPrimaryId = ids.FirstOrDefault();
                }
                await ConfigHandler.SaveConfig(_config);
            }

            Profiles.Clear();
            foreach (var item in items.OrderBy(t => t.Sort))
            {
                Profiles.Add(item);
            }

            SelectedProfile = Profiles.FirstOrDefault(t => t.IndexId == preferredIndexId)
                ?? Profiles.FirstOrDefault(t => t.IsActive)
                ?? Profiles.FirstOrDefault();
            snapshot = Profiles.ToList();
        }
        finally
        {
            _isLoadingAutoFailoverSelection = false;
        }

        _ = RefreshProfileCountryInfoAsync(snapshot);
        await UpdateConnectionPingAsync();
        await RefreshSupportSnapshotAsync(false);
    }

    private async Task ClearAutoFailoverRuntimeGroupAsync(bool force = false)
    {
        if (AutoProtocolFailoverEnabled && !force)
        {
            return;
        }

        var groupId = _config.UiItem.AutoProtocolFailoverGroupId;
        if (groupId.IsNullOrEmpty())
        {
            return;
        }

        var runtimeGroup = await AppManager.Instance.GetProfileItem(groupId);
        if (runtimeGroup == null)
        {
            _config.UiItem.AutoProtocolFailoverGroupId = null;
            await ConfigHandler.SaveConfig(_config);
            return;
        }

        if (string.Equals(_config.IndexId, groupId, StringComparison.OrdinalIgnoreCase))
        {
            var fallbackId = _config.UiItem.AutoProtocolFailoverPrimaryId;
            var fallback = fallbackId.IsNullOrEmpty()
                ? null
                : await AppManager.Instance.GetProfileItem(fallbackId);
            if (fallback == null || fallback.ConfigType.IsComplexType())
            {
                fallback = (await AppManager.Instance.ProfileItems("") ?? [])
                    .FirstOrDefault(item => !string.Equals(item.IndexId, groupId, StringComparison.OrdinalIgnoreCase)
                                            && item.IsValid()
                                            && !item.ConfigType.IsComplexType());
            }

            if (fallback != null)
            {
                await ConfigHandler.SetDefaultServerIndex(_config, fallback.IndexId);
            }
        }

        await ConfigHandler.RemoveServers(_config, [runtimeGroup]);
        _config.UiItem.AutoProtocolFailoverGroupId = null;
        await ConfigHandler.SaveConfig(_config);
    }

    private async Task RefreshProfileCountryInfoAsync(IReadOnlyCollection<ProfileItemModel> profiles)
    {
        var refreshVersion = Interlocked.Increment(ref _profileCountryRefreshVersion);
        var profilesByAddress = profiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Address))
            .GroupBy(profile => profile.Address, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var profileGroup in profilesByAddress)
        {
            var countryInfo = await _serverCountryLookup.ResolveAsync(profileGroup.Key);
            if (refreshVersion != _profileCountryRefreshVersion)
            {
                return;
            }

            var countryCode = countryInfo?.CountryCode ?? string.Empty;
            var countryName = countryInfo?.CountryName ?? string.Empty;
            var flagImageUrl = countryInfo?.FlagImageUrl ?? string.Empty;
            var flagText = BuildCountryFlagText(countryCode);

            foreach (var profile in profileGroup)
            {
                var effectiveCountryCode = countryCode;
                var effectiveCountryName = countryName;
                var effectiveFlagImageUrl = flagImageUrl;
                var effectiveFlagText = flagText;
                if (effectiveCountryCode.IsNullOrEmpty())
                {
                    var inferredCountry = TryInferCountryFromProfileName(profile.Remarks);
                    effectiveCountryCode = inferredCountry.CountryCode;
                    effectiveCountryName = inferredCountry.CountryName;
                    effectiveFlagImageUrl = inferredCountry.CountryCode.IsNullOrEmpty()
                        ? string.Empty
                        : $"https://flagcdn.com/24x18/{inferredCountry.CountryCode.ToLowerInvariant()}.png";
                    effectiveFlagText = BuildCountryFlagText(inferredCountry.CountryCode);
                }

                profile.CountryCode = effectiveCountryCode;
                profile.CountryName = effectiveCountryName;
                profile.CountryFlagImageUrl = await GetCountryFlagImagePathAsync(effectiveCountryCode, effectiveFlagImageUrl);
                profile.CountryFlagText = effectiveFlagText;
            }
        }
    }

    private Task<string> GetCountryFlagImagePathAsync(string countryCode, string remoteFlagUrl)
    {
        if (countryCode.Length != 2 || !countryCode.All(char.IsLetter))
        {
            return Task.FromResult(string.Empty);
        }

        var normalizedCode = countryCode.ToLowerInvariant();
        var lazy = _countryFlagCache.GetOrAdd(
            normalizedCode,
            key => new Lazy<Task<string>>(() => DownloadCountryFlagImageAsync(key, remoteFlagUrl)));
        return lazy.Value;
    }

    private static async Task<string> DownloadCountryFlagImageAsync(string countryCode, string remoteFlagUrl)
    {
        var cacheDir = Path.Combine(Utils.GetConfigPath(), "flagCache");
        Directory.CreateDirectory(cacheDir);
        var cachePath = Path.Combine(cacheDir, $"{countryCode}.png");
        if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
        {
            return cachePath;
        }

        var url = remoteFlagUrl.IsNotEmpty()
            ? remoteFlagUrl
            : $"https://flagcdn.com/24x18/{countryCode}.png";
        try
        {
            var bytes = await FlagImageHttpClient.GetByteArrayAsync(url);
            if (bytes.Length == 0)
            {
                return string.Empty;
            }

            var tempPath = $"{cachePath}.tmp";
            await File.WriteAllBytesAsync(tempPath, bytes);
            File.Move(tempPath, cachePath, true);
            Logging.SaveLog($"FlagCache downloaded | country={countryCode.ToUpperInvariant()} | path={cachePath}");
            return cachePath;
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"FlagCache failed | country={countryCode.ToUpperInvariant()} | url={url} | error={ex.GetType().Name}: {ex.Message}");
            return string.Empty;
        }
    }

    private static (string CountryCode, string CountryName) TryInferCountryFromProfileName(string? remarks)
    {
        if (remarks.IsNullOrEmpty())
        {
            return (string.Empty, string.Empty);
        }

        var firstToken = remarks.Trim().Split(' ', '-', '_', '[', ']', '(', ')').FirstOrDefault();
        if (firstToken is null || firstToken.Length != 2 || !firstToken.All(char.IsLetter))
        {
            return (string.Empty, string.Empty);
        }

        var countryCode = firstToken.ToUpperInvariant() == "UK" ? "GB" : firstToken.ToUpperInvariant();
        var countryName = countryCode switch
        {
            "DE" => "Germany",
            "US" => "United States",
            "NL" => "Netherlands",
            "FR" => "France",
            "GB" => "United Kingdom",
            "RU" => "Russia",
            "FI" => "Finland",
            "SE" => "Sweden",
            "PL" => "Poland",
            "TR" => "Turkey",
            "JP" => "Japan",
            "KR" => "South Korea",
            "SG" => "Singapore",
            "HK" => "Hong Kong",
            "CA" => "Canada",
            "AU" => "Australia",
            _ => string.Empty
        };

        return countryName.IsNullOrEmpty() ? (string.Empty, string.Empty) : (countryCode, countryName);
    }

    private static string BuildCountryFlagText(string countryCode)
    {
        if (countryCode.Length != 2 || !countryCode.All(char.IsLetter))
        {
            return string.Empty;
        }

        return countryCode.ToUpperInvariant();
    }

    private async Task ApplyQuickRulesAsync(bool reload)
    {
        var transientDirectDomains = DirectDomains
            .Where(domain => _parallelOpenVpnInjectedDirectDomains.Contains(domain))
            .Select(NormalizeDomainRule)
            .Where(domain => !domain.IsNullOrEmpty())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _quickRules.DirectProcesses = DirectApps
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _quickRules.DirectDomains = DirectDomains
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(NormalizeDomainRule)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Where(t => !transientDirectDomains.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _quickRules.ProxyProcesses = ProxyApps
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _quickRules.BlockDomains = BlockDomains
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(NormalizeDomainRule)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _quickRules.ProxyDomains = ProxyDomains
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(NormalizeDomainRule)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _quickRules.UseProxyDomainsPreset = UseProxyDomainsPreset;
        _quickRules.ProxyOnlyMode = ProxyOnlyMode;
        _quickRules.BypassPrivate = BypassPrivate;
        _quickRules.TelegramTrafficMode = TelegramUseLocalSocks
            ? QuickRuleConfig.TelegramTrafficModeLocalSocks
            : QuickRuleConfig.TelegramTrafficModeVpn;

        if (transientDirectDomains.Count == 0)
        {
            await QuickRuleHandler.Apply(_config, _quickRules);
        }
        else
        {
            var applyRules = new QuickRuleConfig
            {
                DirectProcesses = _quickRules.DirectProcesses.ToList(),
                DirectDomains = _quickRules.DirectDomains
                    .Concat(transientDirectDomains)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                ProxyProcesses = _quickRules.ProxyProcesses.ToList(),
                ProxyDomains = _quickRules.ProxyDomains.ToList(),
                BlockDomains = _quickRules.BlockDomains.ToList(),
                UseProxyDomainsPreset = _quickRules.UseProxyDomainsPreset,
                ProxyOnlyMode = _quickRules.ProxyOnlyMode,
                BypassPrivate = _quickRules.BypassPrivate,
                TelegramTrafficMode = _quickRules.TelegramTrafficMode,
                RoutingId = _quickRules.RoutingId
            };

            await QuickRuleHandler.Apply(_config, applyRules, save: false);
            _quickRules.RoutingId = applyRules.RoutingId;
            await QuickRuleHandler.Save(_quickRules);
        }

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

    private async Task EnsureTelegramTrafficModeAsync(bool openInTelegram)
    {
        string statusMessage;
        if (TelegramUseLocalSocks)
        {
            if (!TelegramWsProxyHandler.TryStart(out var error))
            {
                statusMessage = error.IsNullOrEmpty()
                    ? "Не удалось запустить TG WS Proxy."
                    : $"TG WS Proxy: {error}";
            }
            else
            {
                if (openInTelegram)
                {
                    TelegramWsProxyHandler.OpenInTelegram();
                }

                var address = TelegramWsProxyHandler.GetConfiguredAddress();
                statusMessage = $"Telegram переключён на локальный SOCKS5 {address.Host}:{address.Port}.";
            }
        }
        else
        {
            TelegramWsProxyHandler.Stop();
            statusMessage = "Telegram переключён на VPN-маршрутизацию NetCat.";
        }

        await RefreshSupportSnapshotAsync(false);
        SetStatus(statusMessage);
    }

    private void SetStatus(string message)
    {
        StatusMessage = message;
        UpdateTrayToolTip();
    }

    private void NotifyTelegramStateChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TelegramTrafficSummary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TelegramTrafficModeLabel)));
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
        NotifyTelegramStateChanged();

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
        sb.AppendLine($"TG WS Proxy: embedded in NetCat ({TelegramWsProxyHandler.GetEmbeddedRevisionDisplay()})");
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
        sb.AppendLine($"Telegram mode: {TelegramTrafficModeLabel}");
        sb.AppendLine(TelegramWsProxyHandler.GetRuntimeSummary(_quickRules.TelegramTrafficMode));
        sb.AppendLine($"Updater: {(Utils.UpgradeAppExists(out var updaterPath) ? "ready" : "missing")}");
        sb.AppendLine($"Updater path: {updaterPath}");
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
        var updateCacheStats = Utils.GetGuiUpdateCacheStats();
        var logStats = Utils.GetDirectoryStats(Utils.GetLogPath(), "*", SearchOption.TopDirectoryOnly);
        var updaterLogStats = Utils.GetDirectoryStats(Utils.GetInstallLogPath(), "updater-*.log", SearchOption.TopDirectoryOnly);
        var latestError = Utils.ReadLatestLogError() ?? "none";
        var trustDiagnostics = Utils.GetInstallTrustDiagnostics();

        var sb = new StringBuilder();
        sb.AppendLine(StartupUpdateStatus);
        sb.AppendLine($"Updater: {(updaterReady ? "ready" : "missing")}");
        sb.AppendLine($"Updater path: {updaterPath}");
        sb.AppendLine($"Telegram mode: {TelegramTrafficModeLabel}");
        sb.AppendLine(TelegramWsProxyHandler.GetRuntimeSummary(_quickRules.TelegramTrafficMode));
        sb.AppendLine($"Zapret path: {(resolvedZapretPath.IsNullOrEmpty() ? "missing" : resolvedZapretPath)}");
        sb.AppendLine($"Zapret config: {SelectedZapretConfig?.Name ?? "none"}");
        sb.AppendLine($"Zapret hidden launchers: {hiddenLaunchers}");
        sb.AppendLine($"Stale updater dirs: {staleUpdaterDirs}");
        sb.AppendLine($"Temp folder: {tempStats.FileCount} files, {Utils.HumanFy(tempStats.TotalBytes)}");
        sb.AppendLine($"Update cache: {updateCacheStats.FileCount} archives, {Utils.HumanFy(updateCacheStats.TotalBytes)}");
        sb.AppendLine($"Latest cached package: {updateCacheStats.LatestFileName}");
        if (updateCacheStats.LatestWriteTimeLocal.HasValue)
        {
            sb.AppendLine($"Latest cached at: {updateCacheStats.LatestWriteTimeLocal:yyyy-MM-dd HH:mm:ss}");
        }
        sb.AppendLine($"App logs: {logStats.FileCount} files, {Utils.HumanFy(logStats.TotalBytes)}");
        sb.AppendLine($"Updater logs: {updaterLogStats.FileCount} files, {Utils.HumanFy(updaterLogStats.TotalBytes)}");
        sb.AppendLine($"Latest error: {latestError}");
        sb.AppendLine(trustDiagnostics);
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

    private bool _isShuttingDown = false;

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
            if (!_isShuttingDown)
            {
                e.Cancel = true;
            }
            return;
        }

        _closing = true;
        e.Cancel = true;
        await StopParallelOpenVpnAsync(updateRouting: false, showStatus: false);
        await AppManager.Instance.AppExitAsync(false);
        
        _isShuttingDown = true;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            Application.Current?.Shutdown();
        });
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

    private async void OnTelegramTrafficModeChanged(object sender, RoutedEventArgs e)
    {
        await ApplyQuickRulesAsync(reload: true);
        await EnsureTelegramTrafficModeAsync(openInTelegram: TelegramUseLocalSocks);
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
            await ClearAutoFailoverRuntimeGroupAsync();

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

    private async Task CheckStartupGuiUpdateAsync()
    {
        try
        {
            var updateService = new UpdateService(_config, (_, _) => Task.CompletedTask);
            var result = await updateService.CheckGuiUpdateAvailability();
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

        ApplyAppearance();
        await ConfigHandler.SaveConfig(_config);
        SetStatus($"Accent color set to {CustomPrimaryColorHex}");
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
        ApplyAppearance();
        await ConfigHandler.SaveConfig(_config);
        SetStatus($"Accent color set to {CustomPrimaryColorHex}");
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

        ApplyAppearance();
        await ConfigHandler.SaveConfig(_config);
    }

    private void OnCustomColorPlaneMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isPickingCustomColor = false;
        Mouse.Capture(null);
    }

    private async void OnInterfaceHueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ApplyAppearance();
        await ConfigHandler.SaveConfig(_config);
        SetStatus($"Interface color set to {InterfaceColorHex}");
    }

    private async void OnInterfaceColorPlaneMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isPickingInterfaceColor = true;
        if (sender is IInputElement inputElement)
        {
            Mouse.Capture(inputElement);
        }

        if (sender is IInputElement activeInputElement)
        {
            UpdateInterfaceColorFromPoint(e.GetPosition(activeInputElement));
        }

        ApplyAppearance();
        await ConfigHandler.SaveConfig(_config);
        SetStatus($"Interface color set to {InterfaceColorHex}");
    }

    private async void OnInterfaceColorPlaneMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPickingInterfaceColor)
        {
            return;
        }

        if (sender is IInputElement activeInputElement)
        {
            UpdateInterfaceColorFromPoint(e.GetPosition(activeInputElement));
        }

        ApplyAppearance();
        await ConfigHandler.SaveConfig(_config);
    }

    private void OnInterfaceColorPlaneMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isPickingInterfaceColor = false;
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
            var running = Process.GetProcessesByName("sing-box").Length > 0;
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
                var beforeCount = (await AppManager.Instance.ProfileItems(""))?.Count ?? 0;
                var subscriptionExists = (await AppManager.Instance.SubItems())
                    .Any(t => string.Equals(t.Url, link, StringComparison.OrdinalIgnoreCase));
                
                var ret = await ConfigHandler.AddSubItem(_config, link);
                if (ret == 0)
                {
                    await SubscriptionHandler.UpdateProcess(_config, "", false, (_, _) => Task.CompletedTask);
                    var afterCount = (await AppManager.Instance.ProfileItems(""))?.Count ?? 0;
                    if (afterCount <= beforeCount)
                    {
                        var imported = await TryImportSubscriptionContentDirectlyAsync(link);
                        SetStatus(imported > 0
                            ? $"Subscription imported directly: {imported} config(s)"
                            : "Subscription did not return importable configs");
                    }
                    else
                    {
                        SetStatus(subscriptionExists ? "Subscription updated" : "Subscription added and updated");
                    }
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

    private async void OnImportFromFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Текстовые и конфигурационные файлы (*.json;*.yaml;*.yml;*.txt)|*.json;*.yaml;*.yml;*.txt|Все файлы (*.*)|*.*",
            Title = "Выберите файл с конфигурацией или ссылками",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var fileContent = await System.IO.File.ReadAllTextAsync(dialog.FileName);
                var ret = await ConfigHandler.AddBatchServers(_config, fileContent, _config.SubIndexId, false);
                if (ret > 0)
                {
                    await RefreshProfilesAsync();
                    SetStatus(string.Format(ResUI.SuccessfullyImportedServerViaClipboard, ret));
                }
                else
                {
                    SetStatus(ResUI.OperationFailed);
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Ошибка: {ex.Message}");
                Logging.SaveLog(ex.Message, ex);
            }
        }
    }

    private async Task<int> TryImportSubscriptionContentDirectlyAsync(string link)
    {
        var downloader = new DownloadService();
        var content = await downloader.TryDownloadString(link, false, Global.AppName)
                      ?? await downloader.TryDownloadString(link, true, Global.AppName);
        if (content.IsNullOrEmpty())
        {
            return 0;
        }

        return await ConfigHandler.AddBatchServers(_config, content, _config.SubIndexId, false);
    }

    private void OnCreateConfigClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.ContextMenu == null)
        {
            return;
        }

        element.ContextMenu.PlacementTarget = element;
        element.ContextMenu.IsOpen = true;
    }

    private async void OnCreateConfigMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse<EConfigType>(tag, out var configType))
        {
            SetStatus("Unknown configuration type");
            return;
        }

        ProfileItem item = new()
        {
            Subid = _config.SubIndexId,
            ConfigType = configType,
            IsSub = false,
        };

        bool? result;
        if (configType == EConfigType.Custom)
        {
            result = new AddServer2Window(item).ShowDialog();
        }
        else if (configType.IsGroupType())
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

        SetStatus($"{configType} configuration created");
    }

    private async void OnSetActive(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile == null)
        {
            SetStatus("Select a profile first");
            return;
        }

        await ConfigHandler.SetDefaultServerIndex(_config, SelectedProfile.IndexId);
        if (AutoProtocolFailoverEnabled && SelectedProfile.IsAutoFailoverCandidate)
        {
            _config.UiItem.AutoProtocolFailoverPrimaryId = SelectedProfile.IndexId;
            SaveAutoFailoverProfileIdsFromUi();
            await ConfigHandler.SaveConfig(_config);
            await ApplyAutoFailoverAsync(showStatusWhenIncomplete: true);
            return;
        }

        await ViewModel.Reload();
        await RefreshProfilesAsync();
        await UpdateConnectionPingAsync();
        SetStatus("Active profile updated");
    }

    private async void OnProfilesMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedProfile == null || _isEditingProfile)
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
        if (_isEditingProfile)
        {
            return;
        }
        _isEditingProfile = true;
        try
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
        finally
        {
            _isEditingProfile = false;
        }
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

    private async void OnAutoFailoverEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoFailoverEvents)
        {
            return;
        }

        _config.UiItem.AutoProtocolFailoverEnabled = AutoProtocolFailoverEnabled;
        AutoProtocolFailoverStandbyCount = 1;
        _config.UiItem.AutoProtocolFailoverStandbyCount = 1;

        if (AutoProtocolFailoverEnabled && !GetAutoFailoverProfileIds().Any())
        {
            var first = SelectedProfile is { IsAutoFailoverEligible: true }
                ? SelectedProfile
                : Profiles.FirstOrDefault(profile => profile.IsActive && profile.IsAutoFailoverEligible)
                  ?? Profiles.FirstOrDefault(profile => profile.IsAutoFailoverEligible);
            if (first != null && !first.ConfigType.IsComplexType())
            {
                first.IsAutoFailoverCandidate = true;
                _config.UiItem.AutoProtocolFailoverPrimaryId = first.IndexId;
            }
        }

        SaveAutoFailoverProfileIdsFromUi();
        await ConfigHandler.SaveConfig(_config);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoFailoverCandidateVisibility)));

        if (!AutoProtocolFailoverEnabled)
        {
            await ClearAutoFailoverRuntimeGroupAsync(force: true);
            await ViewModel.Reload();
            await RefreshProfilesAsync();
            SetStatus("Автосмена протокола выключена.");
            return;
        }

        await ApplyAutoFailoverAsync(showStatusWhenIncomplete: true);
    }

    private async void OnAutoFailoverCandidateChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoFailoverEvents
            || _isLoadingAutoFailoverSelection
            || sender is not FrameworkElement { DataContext: ProfileItemModel profile })
        {
            return;
        }
        if (!profile.IsAutoFailoverEligible)
        {
            profile.IsAutoFailoverCandidate = false;
            return;
        }

        if (profile.IsAutoFailoverCandidate && _config.UiItem.AutoProtocolFailoverPrimaryId.IsNullOrEmpty())
        {
            _config.UiItem.AutoProtocolFailoverPrimaryId = profile.IndexId;
        }
        else if (!profile.IsAutoFailoverCandidate
                 && string.Equals(_config.UiItem.AutoProtocolFailoverPrimaryId, profile.IndexId, StringComparison.OrdinalIgnoreCase))
        {
            _config.UiItem.AutoProtocolFailoverPrimaryId = Profiles.FirstOrDefault(item => item.IsAutoFailoverCandidate)?.IndexId;
        }

        SaveAutoFailoverProfileIdsFromUi();
        await ConfigHandler.SaveConfig(_config);

        if (AutoProtocolFailoverEnabled)
        {
            var eligibleCandidates = GetAutoFailoverCandidateModels();
            if (eligibleCandidates.Count < 2)
            {
                var fallback = eligibleCandidates.FirstOrDefault()
                               ?? Profiles.FirstOrDefault(item => item.IsActive
                                                                  && item.ConfigType != EConfigType.Custom
                                                                  && !item.ConfigType.IsComplexType()
                                                                  && !item.Address.IsNullOrEmpty()
                                                                  && item.Port > 0)
                               ?? Profiles.FirstOrDefault(item => item.ConfigType != EConfigType.Custom
                                                                  && !item.ConfigType.IsComplexType()
                                                                  && !item.Address.IsNullOrEmpty()
                                                                  && item.Port > 0);
                await SuspendAutoFailoverRuntimeAndUseProfileAsync(
                    fallback,
                    "Автосмена ожидает второй профиль. VPN оставлен на обычном профиле.");
                return;
            }

            await ApplyAutoFailoverAsync(showStatusWhenIncomplete: true);
            return;
        }

        SetStatus("Profile updated");
    }

    private List<ProfileItemModel> GetAutoFailoverCandidateModels()
    {
        var candidateIds = GetAutoFailoverProfileIds();
        return Profiles
            .Where(profile => candidateIds.Contains(profile.IndexId)
                              && profile.IsAutoFailoverEligible
                              && profile.ConfigType != EConfigType.Custom
                              && !profile.ConfigType.IsComplexType()
                              && !profile.Address.IsNullOrEmpty()
                              && profile.Port > 0)
            .ToList();
    }

    private async Task SuspendAutoFailoverRuntimeAndUseProfileAsync(ProfileItemModel? fallback, string status)
    {
        _config.UiItem.AutoProtocolFailoverPrimaryId = fallback?.IndexId;

        if (fallback != null)
        {
            await ConfigHandler.SetDefaultServerIndex(_config, fallback.IndexId);
        }

        await ClearAutoFailoverRuntimeGroupAsync(force: true);
        await ConfigHandler.SaveConfig(_config);
        await ViewModel.Reload();
        await RefreshProfilesAsync();
        SetStatus(status);
    }

    private async Task DisableAutoFailoverAndUseProfileAsync(ProfileItemModel? fallback, string status)
    {
        _config.UiItem.AutoProtocolFailoverEnabled = false;

        _suppressAutoFailoverEvents = true;
        try
        {
            AutoProtocolFailoverEnabled = false;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoFailoverCandidateVisibility)));
        }
        finally
        {
            _suppressAutoFailoverEvents = false;
        }

        await SuspendAutoFailoverRuntimeAndUseProfileAsync(fallback, status);
    }

    private async Task ApplyAutoFailoverAsync(bool showStatusWhenIncomplete)
    {
        var standbyCount = Math.Clamp((int)Math.Round(AutoProtocolFailoverStandbyCount), 1, 8);
        var requiredCount = standbyCount + 1;
        var candidateModels = GetAutoFailoverCandidateModels();

        if (!AutoProtocolFailoverEnabled)
        {
            await ClearAutoFailoverRuntimeGroupAsync(force: true);
            return;
        }

        if (candidateModels.Count < requiredCount)
        {
            var fallback = candidateModels.FirstOrDefault()
                           ?? Profiles.FirstOrDefault(item => item.IsActive
                                                              && item.ConfigType != EConfigType.Custom
                                                              && !item.ConfigType.IsComplexType()
                                                              && !item.Address.IsNullOrEmpty()
                                                              && item.Port > 0);
            await SuspendAutoFailoverRuntimeAndUseProfileAsync(
                fallback,
                $"Автосмена ожидает минимум {requiredCount} профиля.");
            if (showStatusWhenIncomplete)
            {
                SetStatus($"Автосмена включена: выбери минимум {requiredCount} профиля.");
            }

            return;
        }

        var allItems = await AppManager.Instance.ProfileItems("") ?? [];
        var itemById = allItems
            .Where(item => !item.IndexId.IsNullOrEmpty())
            .GroupBy(item => item.IndexId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var primaryId = ResolveAutoFailoverPrimaryId(candidateModels);
        var orderedItems = candidateModels
            .OrderByDescending(profile => string.Equals(profile.IndexId, primaryId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(profile => profile.Sort)
            .Select(profile => itemById.TryGetValue(profile.IndexId, out var item) ? item : null)
            .Where(item => item != null
                           && item.IsValid()
                           && item.ConfigType != EConfigType.Custom
                           && !item.ConfigType.IsComplexType())
            .Cast<ProfileItem>()
            .ToList();
        var selected = new List<ProfileItem>();
        var skipped = new List<string>();
        foreach (var item in orderedItems)
        {
            if (IsAutoFailoverCompatibleWithSingbox(item, out var reason))
            {
                selected.Add(item);
            }
            else
            {
                skipped.Add($"{item.Remarks}: {reason}");
            }
        }

        if (selected.Count < requiredCount)
        {
            if (showStatusWhenIncomplete)
            {
                SetStatus($"Автосмена: нужно минимум {requiredCount} рабочих профиля, найдено {selected.Count}.");
            }

            return;
        }

        var indexId = _config.UiItem.AutoProtocolFailoverGroupId;
        if (indexId.IsNullOrEmpty())
        {
            indexId = Utils.GetGuid(false);
            _config.UiItem.AutoProtocolFailoverGroupId = indexId;
        }

        var primary = selected.First();
        var profile = new ProfileItem
        {
            IndexId = indexId,
            CoreType = ECoreType.sing_box,
            ConfigType = EConfigType.PolicyGroup,
            Remarks = $"Auto protocol failover - {primary.Remarks}",
            IsSub = false
        };
        if (_config.SubIndexId.IsNotEmpty())
        {
            profile.Subid = _config.SubIndexId;
        }

        profile.SetProtocolExtra(new ProtocolExtraItem
        {
            MultipleLoad = EMultipleLoad.Fallback,
            FailoverStandbyCount = standbyCount,
            GroupType = profile.ConfigType.ToString(),
            ChildItems = Utils.List2String(selected.Select(item => item.IndexId).ToList())
        });

        if (await ConfigHandler.AddServerCommon(_config, profile, true) != 0)
        {
            SetStatus("Не удалось применить автосмену протокола.");
            return;
        }

        _config.UiItem.AutoProtocolFailoverPrimaryId = primary.IndexId;
        _config.UiItem.AutoProtocolFailoverStandbyCount = standbyCount;
        await ConfigHandler.SetDefaultServerIndex(_config, indexId);
        await ConfigHandler.SaveConfig(_config);
        await ViewModel.Reload();
        await RefreshProfilesAsync();
        SelectedProfile = Profiles.FirstOrDefault(item => item.IndexId == primary.IndexId) ?? SelectedProfile;
        Logging.SaveLog($"AutoProtocolFailover active | group={indexId} | primary={primary.Remarks} | candidates={selected.Count} | standby={standbyCount}");
        if (skipped.Count > 0)
        {
            Logging.SaveLog($"AutoProtocolFailover skipped incompatible profiles | {string.Join(" | ", skipped)}");
        }
        SetStatus(skipped.Count > 0
            ? $"Автосмена активна: основной {primary.Remarks}, резервов {standbyCount}, профилей {selected.Count}; пропущено несовместимых {skipped.Count}."
            : $"Автосмена активна: основной {primary.Remarks}, резервов {standbyCount}, профилей {selected.Count}.");
    }

    private static bool IsAutoFailoverCompatibleWithSingbox(ProfileItem item, out string reason)
    {
        if (!item.IsValid())
        {
            reason = "invalid profile";
            return false;
        }

        if (AppManager.Instance.GetCoreType(item, item.ConfigType) != ECoreType.sing_box)
        {
            reason = "only sing-box core is supported";
            return false;
        }

        var result = NodeValidator.Validate(item, ECoreType.sing_box);
        if (!result.Success)
        {
            reason = string.Join("; ", result.Errors);
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private HashSet<string> GetAutoFailoverProfileIds()
    {
        return (Utils.String2List(_config.UiItem.AutoProtocolFailoverProfileIds) ?? [])
            .Where(id => !id.IsNullOrEmpty())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveAutoFailoverProfileIdsFromUi()
    {
        var ids = Profiles
            .Where(profile => profile.IsAutoFailoverCandidate
                              && profile.IsAutoFailoverEligible
                              && profile.ConfigType != EConfigType.Custom
                              && !profile.ConfigType.IsComplexType()
                              && !profile.Address.IsNullOrEmpty()
                              && profile.Port > 0)
            .Select(profile => profile.IndexId)
            .Where(id => !id.IsNullOrEmpty())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _config.UiItem.AutoProtocolFailoverProfileIds = Utils.List2String(ids);
        AutoProtocolFailoverStandbyCount = 1;
        _config.UiItem.AutoProtocolFailoverStandbyCount = 1;
    }

    private string ResolveAutoFailoverPrimaryId(List<ProfileItemModel> candidates)
    {
        var selectedRegular = SelectedProfile is { } selected
                              && candidates.Any(profile => string.Equals(profile.IndexId, selected.IndexId, StringComparison.OrdinalIgnoreCase))
            ? selected.IndexId
            : null;
        if (!selectedRegular.IsNullOrEmpty())
        {
            _config.UiItem.AutoProtocolFailoverPrimaryId = selectedRegular;
            return selectedRegular;
        }

        var savedPrimaryId = _config.UiItem.AutoProtocolFailoverPrimaryId;
        if (!savedPrimaryId.IsNullOrEmpty()
            && candidates.Any(profile => string.Equals(profile.IndexId, savedPrimaryId, StringComparison.OrdinalIgnoreCase)))
        {
            return savedPrimaryId;
        }

        var activeCandidate = candidates.FirstOrDefault(profile => profile.IsActive);
        if (activeCandidate != null)
        {
            _config.UiItem.AutoProtocolFailoverPrimaryId = activeCandidate.IndexId;
            return activeCandidate.IndexId;
        }

        var firstCandidateId = candidates.First().IndexId;
        _config.UiItem.AutoProtocolFailoverPrimaryId = firstCandidateId;
        return firstCandidateId;
    }

    private async void OnAutoTestProfiles(object sender, RoutedEventArgs e)
    {
        var candidates = Profiles
            .Where(profile => profile.ConfigType != EConfigType.Custom
                              && !profile.ConfigType.IsComplexType()
                              && !profile.Address.IsNullOrEmpty()
                              && profile.Port > 0)
            .ToList();
        if (candidates.Count == 0)
        {
            SetStatus("Autotest: No connection");
            return;
        }

        foreach (var profile in candidates)
        {
            profile.Delay = 0;
            profile.DelayVal = "Testing...";
        }

        var items = await GetProfileItemsForModelsAsync(candidates);
        if (items.Count == 0)
        {
            SetStatus("Autotest: No connection");
            return;
        }

        GetProfileSpeedtestService().RunLoop(ESpeedActionType.Realping, items);
        SetStatus($"Autotest: testing {items.Count} configs");
    }

    private SpeedtestService GetProfileSpeedtestService()
    {
        return _profileSpeedtestService ??= new SpeedtestService(_config, async result =>
        {
            await Dispatcher.InvokeAsync(() => ApplyProfileSpeedTestResult(result));
        });
    }

    private async Task<List<ProfileItem>> GetProfileItemsForModelsAsync(IEnumerable<ProfileItemModel> models)
    {
        var ids = models
            .Where(model => model.ConfigType != EConfigType.Custom
                            && !model.ConfigType.IsComplexType()
                            && !model.Address.IsNullOrEmpty()
                            && model.Port > 0)
            .Select(model => model.IndexId)
            .Where(id => !id.IsNullOrEmpty())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        return await AppManager.Instance.GetProfileItemsOrderedByIndexIds(ids);
    }

    private void ApplyProfileSpeedTestResult(SpeedTestResult result)
    {
        if (result.IndexId.IsNullOrEmpty())
        {
            var message = result.Delay.NullIfEmpty() ?? result.Speed.NullIfEmpty();
            if (!message.IsNullOrEmpty())
            {
                SetStatus(message);
            }
            return;
        }

        var profile = Profiles.FirstOrDefault(item => string.Equals(item.IndexId, result.IndexId, StringComparison.OrdinalIgnoreCase));
        if (profile == null)
        {
            return;
        }

        if (result.Delay != null)
        {
            var label = NormalizeDelayLabel(result.Delay);
            profile.DelayVal = label;
            profile.Delay = label.EndsWith(" ms", StringComparison.OrdinalIgnoreCase)
                            && int.TryParse(label[..^3], out var delay)
                ? delay
                : label.Equals("No connection", StringComparison.OrdinalIgnoreCase) ? -1 : 0;
        }

        if (result.Speed.IsNotEmpty())
        {
            profile.SpeedVal = result.Speed ?? string.Empty;
        }
    }

    private static string NormalizeDelayLabel(string? delay)
    {
        if (delay.IsNullOrEmpty())
        {
            return "No connection";
        }

        var value = delay.Trim();
        if (value.Equals(ResUI.Speedtesting, StringComparison.OrdinalIgnoreCase))
        {
            return "Testing...";
        }

        return int.TryParse(value, out var ms) && ms > 0
            ? $"{ms} ms"
            : value;
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

    private async void OnImportParallelOpenVpnProfile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "OpenVPN config (*.ovpn;*.conf)|*.ovpn;*.conf|OpenVPN bundle (*.zip)|*.zip|OpenVPN installer (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Title = "Import OpenVPN config"
        };

        if (dialog.ShowDialog() != true || dialog.FileName.IsNullOrEmpty())
        {
            return;
        }

        try
        {
            var id = Utils.GetGuid(false);
            var importRoot = ParallelOpenVpnConfigHandler.GetImportedProfilesPath();
            var profileDir = Path.Combine(importRoot, id);
            Directory.CreateDirectory(profileDir);
            var sourceName = Path.GetFileName(dialog.FileName);
            var sidecarCount = 0;
            string targetPath;
            if (IsZipArchive(dialog.FileName))
            {
                targetPath = ExtractOpenVpnBundle(dialog.FileName, profileDir);
            }
            else
            {
                var targetName = SanitizeFileName(sourceName.IsNullOrEmpty() ? $"{id}.ovpn" : sourceName);
                targetPath = Path.Combine(profileDir, targetName);
                File.Copy(dialog.FileName, targetPath, overwrite: true);
                sidecarCount = CopyOpenVpnReferencedFiles(dialog.FileName, profileDir);
            }

            var profile = new ParallelOpenVpnProfile
            {
                Id = id,
                Name = Path.GetFileNameWithoutExtension(sourceName).NullIfEmpty() ?? sourceName,
                ConfigPath = targetPath,
                Status = IsOpenVpnConfigFile(targetPath) ? "Stopped" : "Import .ovpn config"
            };

            ParallelOpenVpnProfiles.Add(profile);
            SelectedParallelOpenVpnProfile = profile;
            await SaveParallelOpenVpnConfigAsync();
            ParallelOpenVpnStatus = IsOpenVpnConfigFile(targetPath)
                ? $"OpenVPN config imported. Side files: {sidecarCount}."
                : "OpenVPN installer imported. Import the generated .ovpn config to start.";
            SetStatus("OpenVPN config imported");
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"ParallelOpenVPN import failed: {ex}");
            ParallelOpenVpnStatus = $"Import failed: {ex.Message}";
            SetStatus("OpenVPN import failed");
        }
    }

    private async void OnRemoveParallelOpenVpnProfile(object sender, RoutedEventArgs e)
    {
        var profile = SelectedParallelOpenVpnProfile;
        if (profile == null)
        {
            return;
        }

        if (profile.IsRunning)
        {
            await StopParallelOpenVpnAsync(updateRouting: true, showStatus: false);
        }

        ParallelOpenVpnProfiles.Remove(profile);
        SelectedParallelOpenVpnProfile = ParallelOpenVpnProfiles.FirstOrDefault();
        await SaveParallelOpenVpnConfigAsync();
        SetStatus("OpenVPN profile removed");
    }

    private async void OnStartParallelOpenVpn(object sender, RoutedEventArgs e)
    {
        await StartParallelOpenVpnAsync();
    }

    private async void OnStopParallelOpenVpn(object sender, RoutedEventArgs e)
    {
        await StopParallelOpenVpnAsync(updateRouting: true, showStatus: true);
    }

    private async void OnAddParallelOpenVpnDomain(object sender, RoutedEventArgs e)
    {
        var domain = NewParallelOpenVpnDomain?.Trim();
        if (domain.IsNullOrEmpty())
        {
            SetStatus("Введите домен для OpenVPN");
            return;
        }

        var normalized = NormalizeDomainRule(domain);
        if (ParallelOpenVpnDomains.Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("OpenVPN domain already in list");
            return;
        }

        ParallelOpenVpnDomains.Add(normalized);
        NewParallelOpenVpnDomain = string.Empty;
        SaveSelectedParallelOpenVpnDomains();
        await SaveParallelOpenVpnConfigAsync();

        if (ParallelOpenVpnRunning)
        {
            await InjectParallelOpenVpnDirectDomainsAsync(reload: true);
        }

        SetStatus("OpenVPN domain added");
    }

    private async void OnRemoveParallelOpenVpnDomain(object sender, RoutedEventArgs e)
    {
        if (SelectedParallelOpenVpnDomain == null)
        {
            return;
        }

        var removed = SelectedParallelOpenVpnDomain;
        ParallelOpenVpnDomains.Remove(removed);
        if (_parallelOpenVpnInjectedDirectDomains.Remove(removed))
        {
            DirectDomains.Remove(removed);
        }
        SaveSelectedParallelOpenVpnDomains();
        await SaveParallelOpenVpnConfigAsync();

        if (ParallelOpenVpnRunning)
        {
            await ApplyQuickRulesAsync(reload: true);
        }

        SetStatus("OpenVPN domain removed");
    }

    private void OnOpenParallelOpenVpnFolder(object sender, RoutedEventArgs e)
    {
        OpenPath(ParallelOpenVpnConfigHandler.GetImportedProfilesPath());
    }

    private async Task StartParallelOpenVpnAsync()
    {
        var profile = SelectedParallelOpenVpnProfile;
        if (profile == null)
        {
            ParallelOpenVpnStatus = "Import an OpenVPN config first";
            SetStatus("Import an OpenVPN config first");
            return;
        }

        SaveSelectedParallelOpenVpnDomains();
        await SaveParallelOpenVpnConfigAsync();

        if (!File.Exists(profile.ConfigPath))
        {
            profile.Status = "Config file missing";
            ParallelOpenVpnStatus = "OpenVPN config file is missing";
            SetStatus("OpenVPN config file is missing");
            return;
        }

        if (!IsOpenVpnConfigFile(profile.ConfigPath))
        {
            profile.Status = "Import .ovpn config";
            ParallelOpenVpnStatus = "This file is not an OpenVPN config. Import the generated .ovpn file.";
            SetStatus("Import the generated .ovpn file");
            return;
        }

        var openVpnExe = FindOpenVpnExecutable();
        if (openVpnExe.IsNullOrEmpty())
        {
            ParallelOpenVpnStatus = "OpenVPN core not found: put openvpn.exe into bin\\openvpn or install OpenVPN.";
            SetStatus("OpenVPN core not found");
            return;
        }

        if (_parallelOpenVpnProcess is { HasExited: false })
        {
            await StopParallelOpenVpnAsync(updateRouting: false, showStatus: false);
        }

        try
        {
            var arguments = $"--config {QuoteArgument(profile.ConfigPath)} --pull-filter ignore redirect-gateway --verb 3";
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = openVpnExe,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(profile.ConfigPath) ?? ParallelOpenVpnConfigHandler.GetImportedProfilesPath(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };
            process.OutputDataReceived += (_, args) => LogParallelOpenVpnLine(profile, args.Data);
            process.ErrorDataReceived += (_, args) => LogParallelOpenVpnLine(profile, args.Data);
            process.Exited += OnParallelOpenVpnExited;

            if (!process.Start())
            {
                ParallelOpenVpnStatus = "OpenVPN did not start";
                profile.Status = "Start failed";
                SetStatus("OpenVPN did not start");
                return;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _parallelOpenVpnProcess = process;

            foreach (var item in ParallelOpenVpnProfiles)
            {
                item.IsRunning = false;
                item.Status = item == profile ? "Running" : "Stopped";
            }

            profile.IsRunning = true;
            ParallelOpenVpnRunning = true;
            ParallelOpenVpnStatus = $"OpenVPN running: {profile.Name}";
            Logging.SaveLog($"ParallelOpenVPN started | profile={profile.Name} | config={profile.ConfigPath}");
            await InjectParallelOpenVpnDirectDomainsAsync(reload: true);
            SetStatus("OpenVPN started");
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"ParallelOpenVPN start failed: {ex}");
            profile.IsRunning = false;
            profile.Status = $"Start failed: {ex.Message}";
            ParallelOpenVpnRunning = false;
            ParallelOpenVpnStatus = $"OpenVPN start failed: {ex.Message}";
            SetStatus("OpenVPN start failed");
        }
    }

    private async Task StopParallelOpenVpnAsync(bool updateRouting, bool showStatus)
    {
        var process = _parallelOpenVpnProcess;
        _parallelOpenVpnProcess = null;
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(2500))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(2500);
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"ParallelOpenVPN stop warning: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        foreach (var profile in ParallelOpenVpnProfiles)
        {
            profile.IsRunning = false;
            if (profile.Status.StartsWith("Running", StringComparison.OrdinalIgnoreCase))
            {
                profile.Status = "Stopped";
            }
        }

        ParallelOpenVpnRunning = false;
        ParallelOpenVpnStatus = "OpenVPN stopped";
        await RemoveParallelOpenVpnDirectDomainsAsync(updateRouting);

        if (showStatus)
        {
            SetStatus("OpenVPN stopped");
        }
    }

    private async void OnParallelOpenVpnExited(object? sender, EventArgs e)
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            if (!ReferenceEquals(sender, _parallelOpenVpnProcess))
            {
                return;
            }

            _parallelOpenVpnProcess = null;
            foreach (var profile in ParallelOpenVpnProfiles)
            {
                if (profile.IsRunning)
                {
                    profile.Status = "Stopped unexpectedly";
                }

                profile.IsRunning = false;
            }

            ParallelOpenVpnRunning = false;
            ParallelOpenVpnStatus = "OpenVPN stopped unexpectedly";
            await RemoveParallelOpenVpnDirectDomainsAsync(updateRouting: true);
            SetStatus("OpenVPN stopped unexpectedly");
        });
    }

    private async Task InjectParallelOpenVpnDirectDomainsAsync(bool reload)
    {
        var profile = SelectedParallelOpenVpnProfile;
        if (profile == null)
        {
            return;
        }

        SaveSelectedParallelOpenVpnDomains();
        var normalizedDomains = profile.Domains
            .Select(NormalizeDomainRule)
            .Where(domain => !domain.IsNullOrEmpty())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var domain in normalizedDomains)
        {
            if (DirectDomains.Any(item => string.Equals(item, domain, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            DirectDomains.Add(domain);
            _parallelOpenVpnInjectedDirectDomains.Add(domain);
        }

        await ApplyQuickRulesAsync(reload);
        ParallelOpenVpnStatus = normalizedDomains.Count == 0
            ? $"OpenVPN running: {profile.Name}. No domain rules configured."
            : $"OpenVPN running: {profile.Name}. Direct domains: {normalizedDomains.Count}.";
    }

    private async Task RemoveParallelOpenVpnDirectDomainsAsync(bool updateRouting)
    {
        if (_parallelOpenVpnInjectedDirectDomains.Count == 0)
        {
            return;
        }

        var injected = _parallelOpenVpnInjectedDirectDomains.ToList();
        _parallelOpenVpnInjectedDirectDomains.Clear();
        foreach (var domain in injected)
        {
            DirectDomains.Remove(domain);
        }

        if (updateRouting)
        {
            await ApplyQuickRulesAsync(reload: true);
        }
    }

    private static void LogParallelOpenVpnLine(ParallelOpenVpnProfile profile, string? line)
    {
        if (line.IsNullOrEmpty())
        {
            return;
        }

        Logging.SaveLog($"ParallelOpenVPN[{profile.Name}]: {line}");
    }

    private static string FindOpenVpnExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Utils.GetBinPath("", "openvpn"), "openvpn.exe"),
            Utils.GetBinPath("openvpn.exe"),
            Path.Combine(Utils.StartupPath(), "openvpn", "bin", "openvpn.exe"),
            Path.Combine(Utils.StartupPath(), "openvpn.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenVPN", "bin", "openvpn.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OpenVPN", "bin", "openvpn.exe"),
            SearchExecutableOnPath("openvpn.exe")
        };

        return candidates.FirstOrDefault(path => !path.IsNullOrEmpty() && File.Exists(path)) ?? string.Empty;
    }

    private static bool IsOpenVpnConfigFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".ovpn", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".conf", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsZipArchive(string path)
    {
        return Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractOpenVpnBundle(string archivePath, string targetDirectory)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.IsNullOrEmpty() || entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            var relativeName = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var targetPath = Path.GetFullPath(Path.Combine(targetDirectory, relativeName));
            var root = Path.GetFullPath(targetDirectory);
            if (!targetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"OpenVPN bundle contains unsafe path: {entry.FullName}");
            }

            var parent = Path.GetDirectoryName(targetPath);
            if (!parent.IsNullOrEmpty())
            {
                Directory.CreateDirectory(parent);
            }

            entry.ExtractToFile(targetPath, overwrite: true);
        }

        var configPath = Directory
            .EnumerateFiles(targetDirectory, "*.*", SearchOption.AllDirectories)
            .Where(IsOpenVpnConfigFile)
            .OrderBy(path => path.Length)
            .FirstOrDefault();
        if (configPath.IsNullOrEmpty())
        {
            throw new InvalidOperationException("OpenVPN bundle does not contain .ovpn or .conf config.");
        }

        return configPath;
    }

    private static int CopyOpenVpnReferencedFiles(string sourceConfigPath, string targetDirectory)
    {
        if (!IsOpenVpnConfigFile(sourceConfigPath))
        {
            return 0;
        }

        var sourceDirectory = Path.GetDirectoryName(sourceConfigPath);
        if (sourceDirectory.IsNullOrEmpty())
        {
            return 0;
        }

        var copied = 0;
        foreach (var line in File.ReadLines(sourceConfigPath))
        {
            if (!TryGetOpenVpnReferencedPath(line, out var referencedPath))
            {
                continue;
            }

            var sourcePath = Path.IsPathFullyQualified(referencedPath)
                ? referencedPath
                : Path.Combine(sourceDirectory, referencedPath);
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var targetName = SanitizeFileName(Path.GetFileName(sourcePath));
            if (targetName.IsNullOrEmpty())
            {
                continue;
            }

            File.Copy(sourcePath, Path.Combine(targetDirectory, targetName), overwrite: true);
            copied++;
        }

        return copied;
    }

    private static bool TryGetOpenVpnReferencedPath(string line, out string referencedPath)
    {
        referencedPath = string.Empty;
        var trimmed = line.Trim();
        if (trimmed.IsNullOrEmpty()
            || trimmed.StartsWith("#", StringComparison.Ordinal)
            || trimmed.StartsWith(";", StringComparison.Ordinal)
            || trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            return false;
        }

        var tokens = SplitOpenVpnLine(trimmed);
        if (tokens.Count < 2)
        {
            return false;
        }

        var directive = tokens[0].ToLowerInvariant();
        var pathDirectives = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ca",
            "cert",
            "key",
            "pkcs12",
            "tls-auth",
            "tls-crypt",
            "secret",
            "crl-verify",
            "auth-user-pass"
        };

        if (!pathDirectives.Contains(directive))
        {
            return false;
        }

        referencedPath = tokens[1].Trim();
        return !referencedPath.IsNullOrEmpty()
               && !referencedPath.StartsWith("[[INLINE]]", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> SplitOpenVpnLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static string? SearchExecutableOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path.IsNullOrEmpty())
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static string QuoteArgument(string value)
    {
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return sanitized.IsNullOrEmpty() ? "openvpn.ovpn" : sanitized;
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
                await PersistZapretEnabledAsync(false);
                await ApplyQuickRulesAsync(reload: true);
                SetZapretStatus(error);
                return;
            }

            ZapretRunning = true;
            ZapretEnabled = true;
            await PersistZapretEnabledAsync(true);
            await ApplyQuickRulesAsync(reload: true);
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

            await ApplyQuickRulesAsync(reload: true);
            await RememberLastZapretConfigAsync(SelectedZapretConfig.Name);
            SetZapretStatus($"Started: {SelectedZapretConfig.Name}");
            return;
        }

        if (persistEnabledState)
        {
            await PersistZapretEnabledAsync(false);
        }

        ZapretEnabled = false;
        await ApplyQuickRulesAsync(reload: true);
        SetZapretStatus(error);
    }

    private async Task StopZapretAsync()
    {
        ZapretHandler.Stop();
        await Task.Delay(500);
        ZapretRunning = ZapretHandler.IsRunning();
        ZapretEnabled = ZapretRunning;
        await PersistZapretEnabledAsync(ZapretRunning);
        await ApplyQuickRulesAsync(reload: true);
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
        if (profile == null)
        {
            SetStatus("Ping: No connection");
            return;
        }

        profile.Delay = 0;
        profile.DelayVal = "Testing...";

        var items = await GetProfileItemsForModelsAsync([profile]);
        if (items.Count == 0)
        {
            profile.Delay = -1;
            profile.DelayVal = "No connection";
            SetStatus("Ping: No connection");
            return;
        }

        GetProfileSpeedtestService().RunLoop(ESpeedActionType.Realping, items);
        SetStatus($"Ping: testing {profile.Remarks}");
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

    private void OnOpenUpdateCacheFolder(object sender, RoutedEventArgs e)
    {
        OpenPath(Path.Combine(Utils.GetTempPath(), "updates"));
    }

    private void OnOpenTempFolder(object sender, RoutedEventArgs e)
    {
        OpenPath(Utils.GetTempPath());
    }

    private async void OnRunHousekeeping(object sender, RoutedEventArgs e)
    {
        var removed = await Task.Run(() => Utils.CleanupRuntimeArtifacts(_config));
        await RefreshSupportSnapshotAsync(true);
        SetStatus($"Housekeeping completed: removed {removed} item(s)");
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
        var singboxRunning = Process.GetProcessesByName("sing-box").Length > 0;
        sb.AppendLine($"LocalSocksPort: {localPort}");
        sb.AppendLine($"LocalSocksPortFree: {localPortFree} (sing-box running: {singboxRunning})");
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
        sb.AppendLine($"ParallelOpenVpnRunning: {ParallelOpenVpnRunning}");
        sb.AppendLine($"ParallelOpenVpnProfiles: {ParallelOpenVpnProfiles.Count}");
        sb.AppendLine($"ParallelOpenVpnSelected: {SelectedParallelOpenVpnProfile?.Name}");
        sb.AppendLine($"ParallelOpenVpnDomains: {ParallelOpenVpnDomains.Count}");
        sb.AppendLine($"ParallelOpenVpnCore: {FindOpenVpnExecutable().NullIfEmpty() ?? "MISSING"}");
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

        var singboxDir = Utils.GetBinPath("", ECoreType.sing_box.ToString());
        sb.AppendLine($"SingboxDir: {singboxDir}");
        var binDir = Utils.GetBinPath("");
        sb.AppendLine($"BinRoot: {binDir}");
        foreach (var name in new[] { "geoip.dat", "geosite.dat" })
        {
            var path = Path.Combine(binDir, name);
            sb.AppendLine(File.Exists(path) ? $"BinAsset: {name} OK" : $"BinAsset: {name} MISSING");
        }
        var exeName = Utils.GetExeName("sing-box");
        foreach (var name in new[] { exeName, "geoip.dat", "geosite.dat" })
        {
            var path = Path.Combine(singboxDir, name);
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                sb.AppendLine($"SingboxFile: {name} {info.Length} bytes {info.LastWriteTime}");
            }
            else
            {
                sb.AppendLine($"SingboxFile: {name} MISSING");
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

        sb.AppendLine($"Process sing-box: {Process.GetProcessesByName("sing-box").Length}");
        sb.AppendLine($"Process openvpn: {Process.GetProcessesByName("openvpn").Length}");

        if (!VpnEnabled && Process.GetProcessesByName("sing-box").Length > 0)
        {
            sb.AppendLine("Warning: sing-box is running while VPN/system proxy is OFF");
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



    private Task<bool> EnsureSingboxCoreAsync()
    {
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(ECoreType.sing_box);
        var coreExec = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out _);
        if (!coreExec.IsNullOrEmpty())
        {
            return Task.FromResult(true);
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
            return Task.FromResult(false);
        }

        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, true);
        }

        TryCopyWintun(targetDir, sourceDir);
        return Task.FromResult(File.Exists(Path.Combine(targetDir, exeName)));
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
        var coreType = ECoreType.sing_box;
        var ready = await EnsureSingboxCoreAsync();
        if (!ready)
        {
            SetStatus("sing-box core not found");
            return;
        }

        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
        var coreExec = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out _);
        if (coreExec.IsNullOrEmpty())
        {
            SetStatus("sing-box core not found");
            return;
        }

        var versionArg = coreInfo?.VersionArg.IsNullOrEmpty() == true ? "version" : coreInfo?.VersionArg ?? "version";

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
        if (Process.GetProcessesByName("sing-box").Length > 0)
        {
            SetStatus("Stop VPN before config test");
            return;
        }

        var coreType = ECoreType.sing_box;
        var ready = await EnsureSingboxCoreAsync();
        if (!ready)
        {
            SetStatus("sing-box core not found");
            return;
        }

        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
        var coreExec = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out _);
        if (coreExec.IsNullOrEmpty())
        {
            SetStatus("sing-box core not found");
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
                Arguments = $"run -c \"{configPath}\" --disable-color",
                WorkingDirectory = Path.GetDirectoryName(coreExec) ?? Utils.GetBinPath(""),
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(startInfo);
            if (proc == null)
            {
                SetStatus("Failed to start sing-box");
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

        if (TryExtractHttpUrlHost(trimmed, out var host))
        {
            trimmed = host;
        }
        else if (trimmed.Contains(':'))
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

    private static bool TryExtractHttpUrlHost(string input, out string host)
    {
        host = string.Empty;
        var value = input.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            && uri.Host.IsNotEmpty())
        {
            host = uri.IdnHost.IsNotEmpty() ? uri.IdnHost : uri.Host;
            return true;
        }

        if (value.StartsWith("//", StringComparison.Ordinal)
            && Uri.TryCreate($"https:{value}", UriKind.Absolute, out uri)
            && uri.Host.IsNotEmpty())
        {
            host = uri.IdnHost.IsNotEmpty() ? uri.IdnHost : uri.Host;
            return true;
        }

        return false;
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

        return await EnsureSingboxCoreAsync();
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

    private void LoadCustomAppearance()
    {
        UseCustomPrimaryColor = true;

        var accentColor = TryParseColor(_config.UiItem.CustomPrimaryColor)
            ?? (Color)ColorConverter.ConvertFromString("#4F8CFF");
        var accentHsv = ColorToHsv(accentColor);
        _customHue = accentHsv.Hue;
        _customSaturation = accentHsv.Saturation;
        _customValue = accentHsv.Value;

        var interfaceColor = TryParseColor(_config.UiItem.CustomInterfaceColor)
            ?? GetLegacyInterfaceFallbackColor(_config.UiItem.MainWindowPreset);
        var interfaceHsv = ColorToHsv(interfaceColor);
        _interfaceHue = interfaceHsv.Hue;
        _interfaceSaturation = interfaceHsv.Saturation;
        _interfaceValue = interfaceHsv.Value;
    }

    private void ApplyAppearance()
    {
        var accentColor = GetSelectedPrimaryColor();
        var interfaceColor = GetSelectedInterfaceColor();
        var palette = BuildInterfacePalette(interfaceColor, accentColor);

        _config.UiItem.MainWindowPreset = null;
        _config.UiItem.CurrentTheme = palette.IsLight ? nameof(ETheme.Light) : nameof(ETheme.Dark);
        _config.UiItem.ColorPrimaryName = "Custom";
        _config.UiItem.UseCustomPrimaryColor = true;
        _config.UiItem.CustomPrimaryColor = CustomPrimaryColorHex;
        _config.UiItem.CustomInterfaceColor = InterfaceColorHex;

        var theme = _paletteHelper.GetTheme();
        theme.SetBaseTheme(palette.IsLight ? BaseTheme.Light : BaseTheme.Dark);
        theme.PrimaryLight = new ColorPair(BlendWith(accentColor, Colors.White, 0.18));
        theme.PrimaryMid = new ColorPair(accentColor, palette.OnAccentColor);
        theme.PrimaryDark = new ColorPair(BlendWith(accentColor, Colors.Black, 0.16), palette.OnAccentColor);
        theme.SecondaryLight = new ColorPair(BlendWith(interfaceColor, Colors.White, palette.IsLight ? 0.1 : 0.06), palette.OnSurfaceColor);
        theme.SecondaryMid = new ColorPair(interfaceColor, palette.OnSurfaceColor);
        theme.SecondaryDark = new ColorPair(BlendWith(interfaceColor, Colors.Black, palette.IsLight ? 0.12 : 0.14), palette.OnSurfaceColor);
        _paletteHelper.SetTheme(theme);

        ApplyInterfaceColorResources(palette);
        ApplyAppearanceToOpenWindows();
    }

    private void ApplyInterfaceColorResources(InterfacePalette palette)
    {
        SetThemeResource("NetCatWindowBackgroundBrush", CreateFrozenBrush(palette.WindowBackgroundColor));
        SetThemeResource("NetCatWindowChromeBrush", CreateFrozenBrush(palette.WindowChromeColor));
        SetThemeResource("NetCatSurfaceBrush", CreateFrozenBrush(palette.SurfaceColor));
        SetThemeResource("NetCatSurfaceAltBrush", CreateFrozenBrush(palette.SurfaceAltColor));
        SetThemeResource("NetCatSurfaceHeaderBrush", CreateFrozenBrush(palette.SurfaceHeaderColor));
        SetThemeResource("NetCatMutedTextBrush", CreateFrozenBrush(palette.MutedTextColor));
        SetThemeResource("NetCatStrongTextBrush", CreateFrozenBrush(palette.OnSurfaceColor));
        SetThemeResource("NetCatAccentForegroundBrush", CreateFrozenBrush(palette.OnAccentColor));
        SetThemeResource("NetCatFooterBrush", CreateFrozenBrush(palette.FooterColor));
        SetThemeResource("NetCatAccentBrush", CreateFrozenBrush(palette.AccentColor));
        SetThemeResource("NetCatAccentSoftBrush", CreateFrozenBrush(Color.FromArgb(48, palette.AccentColor.R, palette.AccentColor.G, palette.AccentColor.B)));
        SetThemeResource("NetCatScrollBarTrackBrush", CreateFrozenBrush(Color.FromArgb(48, palette.AccentColor.R, palette.AccentColor.G, palette.AccentColor.B)));
        SetThemeResource("NetCatScrollBarThumbBrush", CreateFrozenBrush(Color.FromArgb(200, palette.AccentColor.R, palette.AccentColor.G, palette.AccentColor.B)));
        SetThemeResource("NetCatScrollBarThumbBorderBrush", CreateFrozenBrush(BlendWith(palette.AccentColor, palette.OnAccentColor, 0.18)));
        SetThemeResource("NetCatScrollBarThumbHoverBrush", CreateFrozenBrush(BlendWith(palette.AccentColor, palette.OnAccentColor, 0.12)));
        SetThemeResource("NetCatScrollBarThumbDragBrush", CreateFrozenBrush(BlendWith(palette.AccentColor, Colors.Black, 0.18)));
        SetThemeResource("NetCatHeroGradientBrush", CreateFrozenGradientBrush(palette.HeroStartColor, palette.HeroEndColor));
        Background = CreateFrozenBrush(palette.WindowBackgroundColor);
    }

    private void ApplyAppearanceToOpenWindows()
    {
        if (Application.Current == null)
        {
            WindowsUtils.SetDarkBorder(this, _config.UiItem.CurrentTheme);
            return;
        }

        foreach (Window window in Application.Current.Windows)
        {
            window.SetResourceReference(Window.BackgroundProperty, "NetCatWindowBackgroundBrush");
            window.SetResourceReference(Window.ForegroundProperty, "NetCatStrongTextBrush");
            WindowsUtils.SetDarkBorder(window, _config.UiItem.CurrentTheme);
        }
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

    private Color GetSelectedInterfaceColor()
    {
        return ColorFromHsv(InterfaceHue, InterfaceSaturation, InterfaceValue);
    }

    private void UpdateCustomColorFromPoint(Point point)
    {
        var x = Math.Clamp(point.X, 0, CustomColorPlaneWidth);
        var y = Math.Clamp(point.Y, 0, CustomColorPlaneHeight);
        CustomSaturation = x / CustomColorPlaneWidth;
        CustomValue = 1 - (y / CustomColorPlaneHeight);
    }

    private void UpdateInterfaceColorFromPoint(Point point)
    {
        var x = Math.Clamp(point.X, 0, CustomColorPlaneWidth);
        var y = Math.Clamp(point.Y, 0, CustomColorPlaneHeight);
        InterfaceSaturation = x / CustomColorPlaneWidth;
        InterfaceValue = 1 - (y / CustomColorPlaneHeight);
    }

    private void NotifyCustomColorStateChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomPrimaryBaseBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomPrimaryPreviewBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomPrimaryColorHex)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomColorCursorLeft)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomColorCursorTop)));
    }

    private void NotifyInterfaceColorStateChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InterfaceBaseBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InterfacePreviewBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InterfaceColorHex)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InterfaceColorCursorLeft)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InterfaceColorCursorTop)));
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

    private static Color GetLegacyInterfaceFallbackColor(string? presetKey)
    {
        return presetKey switch
        {
            "CarbonBlue" => (Color)ColorConverter.ConvertFromString("#0F182A"),
            "SlateMono" => (Color)ColorConverter.ConvertFromString("#151A21"),
            _ => (Color)ColorConverter.ConvertFromString("#111A2B")
        };
    }

    private static InterfacePalette BuildInterfacePalette(Color interfaceColor, Color accentColor)
    {
        var onSurfaceColor = GetContrastingTextColor(interfaceColor);
        var onAccentColor = GetContrastingTextColor(accentColor);
        var isLight = GetRelativeLuminance(interfaceColor) >= 0.52;

        return new InterfacePalette
        {
            IsLight = isLight,
            AccentColor = accentColor,
            OnAccentColor = onAccentColor,
            SurfaceColor = interfaceColor,
            SurfaceAltColor = BlendWith(interfaceColor, isLight ? Colors.White : Colors.White, isLight ? 0.18 : 0.08),
            SurfaceHeaderColor = BlendWith(interfaceColor, isLight ? Colors.Black : Colors.White, isLight ? 0.06 : 0.04),
            WindowBackgroundColor = BlendWith(interfaceColor, Colors.Black, isLight ? 0.1 : 0.24),
            WindowChromeColor = BlendWith(interfaceColor, isLight ? Colors.Black : Colors.White, isLight ? 0.18 : 0.14),
            FooterColor = BlendWith(interfaceColor, Colors.Black, isLight ? 0.14 : 0.18),
            HeroStartColor = BlendWith(interfaceColor, isLight ? Colors.White : Colors.White, isLight ? 0.12 : 0.05),
            HeroEndColor = BlendWith(interfaceColor, Colors.Black, isLight ? 0.12 : 0.1),
            OnSurfaceColor = onSurfaceColor,
            MutedTextColor = BlendWith(onSurfaceColor, interfaceColor, isLight ? 0.52 : 0.46)
        };
    }

    private static Color BlendWith(Color color, Color overlay, double overlayWeight)
    {
        overlayWeight = Math.Clamp(overlayWeight, 0, 1);
        var baseWeight = 1 - overlayWeight;
        return Color.FromRgb(
            (byte)Math.Round((color.R * baseWeight) + (overlay.R * overlayWeight)),
            (byte)Math.Round((color.G * baseWeight) + (overlay.G * overlayWeight)),
            (byte)Math.Round((color.B * baseWeight) + (overlay.B * overlayWeight)));
    }

    private static Color GetContrastingTextColor(Color background)
    {
        var white = Color.FromRgb(248, 250, 252);
        var dark = Color.FromRgb(11, 18, 32);
        var whiteContrast = GetContrastRatio(background, white);
        var darkContrast = GetContrastRatio(background, dark);
        return whiteContrast >= darkContrast ? white : dark;
    }

    private static double GetContrastRatio(Color a, Color b)
    {
        var l1 = GetRelativeLuminance(a);
        var l2 = GetRelativeLuminance(b);
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double GetRelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.03928
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        var r = Linearize(color.R);
        var g = Linearize(color.G);
        var b = Linearize(color.B);
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
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
            NotifyTelegramStateChanged();
        }
    }

    private async Task<string> GetProfilePingLabelAsync(ProfileItemModel? profile, bool includePrefix = false)
    {
        var prefix = includePrefix ? "Connection ping: " : string.Empty;
        if (!VpnEnabled && !TunEnabled)
        {
            return $"{prefix}No connection";
        }

        var proxyPing = await MeasureProxyPingAsync();
        if (proxyPing.HasValue)
        {
            return $"{prefix}{proxyPing.Value} ms via proxy";
        }

        return $"{prefix}No connection";
    }

    private async Task<int?> MeasureProxyPingAsync()
    {
        var coreRunning = Process.GetProcessesByName("sing-box").Length > 0;
        if (!coreRunning || (!VpnEnabled && !TunEnabled))
        {
            return null;
        }

        try
        {
            var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
            if (port <= 0) return null;
            var webProxy = new WebProxy($"{Global.Socks5Protocol}{Global.Loopback}:{port}");
            using var client = new HttpClient(new SocketsHttpHandler
            {
                Proxy = webProxy,
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(2),
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None
            })
            {
                Timeout = TimeSpan.FromSeconds(3)
            };

            var url = _config.SpeedTestItem.SpeedPingTestUrl.NullIfEmpty() ?? "http://cp.cloudflare.com/generate_204";
            var timer = Stopwatch.StartNew();
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            timer.Stop();
            return response.IsSuccessStatusCode ? Math.Max(1, (int)timer.ElapsedMilliseconds) : null;
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

public sealed class InterfacePalette
{
    public bool IsLight { get; init; }
    public Color AccentColor { get; init; }
    public Color OnAccentColor { get; init; }
    public Color WindowBackgroundColor { get; init; }
    public Color WindowChromeColor { get; init; }
    public Color SurfaceColor { get; init; }
    public Color SurfaceAltColor { get; init; }
    public Color SurfaceHeaderColor { get; init; }
    public Color MutedTextColor { get; init; }
    public Color OnSurfaceColor { get; init; }
    public Color HeroStartColor { get; init; }
    public Color HeroEndColor { get; init; }
    public Color FooterColor { get; init; }
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
