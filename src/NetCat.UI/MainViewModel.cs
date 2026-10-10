using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using NetCat.Updater;

namespace NetCat.UI;
public sealed class MainViewModel : ObservableObject, IDisposable, IDesiredRuntimeStateProvider
{
    public AppSettings State { get; }
    public SettingsStore Store { get; }
    public RouterService Router { get; }
    public ZapretService Zapret { get; }
    public TelegramService Telegram { get; }
    public ModuleUpdater Updater { get; }
    public string Bin { get; }
    public ObservableCollection<Profile> Profiles { get; } = [];
    public ObservableCollection<Profile> VpnProfiles { get; } = [];
    public ObservableCollection<Profile> OpenVpnProfiles { get; } = [];
    public ObservableCollection<RoutingRule> Rules { get; } = [];
    public ObservableCollection<StrategyResult> Strategies { get; } = [];
    public BatchedLog Logs { get; } = new();
    private readonly BufferedLog logQueue = new();
    private readonly CancellationTokenSource maintenanceLifetime = new();
    private readonly UiProjection projection;
    private readonly SemaphoreSlim profileCommitGate = new(1, 1);
    private long projectedRevision = -1;
    public PhysicalNetworkMonitor NetworkMonitor { get; }
    private readonly SemanticLogCoalescer coalescer = new();
    private Task logPump = Task.CompletedTask;
    private Task subscriptionPump = Task.CompletedTask;
    private readonly ApplicationMaintenanceScheduler maintenance;
    private readonly AutomaticApplicationUpdate applicationUpdate;
    public event Action<string>? ApplicationUpdatePrepared;
    public bool CanAutomaticallyUpdate => State.AutoUpdateNetCat && !State.PinnedModules.Contains("netcat") &&
        !Busy && !TestsBusy && !CheckingUpdates && !Recovering && !IsStartupRestoring &&
        PendingSelection.IsCompleted && PendingRoutes.IsCompleted && PendingOpenVpnSelection.IsCompleted &&
        !Router.Running && !Router.VpnRequested && !Router.OpenVpn.Running && !Zapret.Running && !Telegram.Running &&
        !DesiredState.MainVpnEnabled && !DesiredState.OpenVpnEnabled && !DesiredState.ZapretEnabled &&
        NetCat.Core.WindowsExecutableTrust.IsElevated && PublisherCertificateTrust.IsEnrolled() &&
        PublisherTrust.IsTrusted(Environment.ProcessPath!);
    public void AutomaticUpdateLaunchFailed() => applicationUpdate.LaunchFailed();
    public string PublisherTrustStatus => PublisherCertificateTrust.IsEnrolled()
        ? "Сертификат NetCat добавлен для текущего пользователя Windows."
        : "Сертификат NetCat ещё не добавлен в доверенные.";
    public string PublisherFingerprint => "SHA256: " + PublisherCertificateTrust.Sha256;
    public void RefreshPublisherTrust() => OnPropertyChanged(nameof(PublisherTrustStatus));
    private readonly SemaphoreSlim subscriptionRefreshGate = new(1);
    public string LogMetrics { get; private set; } = "";

    public NetworkLifecycleState NetworkState => RuntimeCoordinator?.CurrentConvergenceState.Phase switch
    {
        ConvergencePhase.WaitingForPhysicalNetwork => NetworkLifecycleState.NetworkUnavailable,
        ConvergencePhase.Reconciling => NetworkLifecycleState.Rebuilding,
        _ => NetworkLifecycleState.Normal
    };
    public bool InNetworkTransition => NetworkState is NetworkLifecycleState.NetworkUnavailable or NetworkLifecycleState.Rebuilding;
    public bool IsNetworkRebuilding => NetworkState == NetworkLifecycleState.Rebuilding;
    public bool IsNetworkUnavailable => NetworkState == NetworkLifecycleState.NetworkUnavailable;

    public DesiredRuntimeState DesiredState { get; private set; } = new();
    private readonly object desiredLock = new();
    private bool isStartupRestoring;
    public bool IsStartupRestoring => isStartupRestoring;
    public bool VpnUpstreamDegraded { get; private set; }

    public DesiredRuntimeState GetCurrentDesiredState()
    {
        lock (desiredLock)
        {
            return DesiredState;
        }
    }

    public bool TunDesired
    {
        get => DesiredState.TunEnabled;
        set => UserRequestedTunChange(value);
    }

    public void UserRequestedVpnChange(bool enabled)
    {
        UpdateDesiredState(d => d with { MainVpnEnabled = enabled, TunEnabled = State.Tun, SelectedVpnProfileId = State.MainProfileId });
        RuntimeCoordinator?.RequestReconcile(ReconcileReason.UserToggledVpn);
    }

    public void UserRequestedTunChange(bool enabled)
    {
        State.Tun = enabled;
        UpdateDesiredState(d => d with { TunEnabled = enabled });
        RuntimeCoordinator?.RequestReconcile(ReconcileReason.UserToggledTun);
        OnPropertyChanged(nameof(TunDesired));
        OnPropertyChanged(nameof(State));
    }

    public void UserRequestedZapretChange(bool enabled)
    {
        UpdateDesiredState(d => d with { ZapretEnabled = enabled });
        RuntimeCoordinator?.RequestReconcile(ReconcileReason.UserToggledZapret);
    }

    public void UserRequestedOpenVpnChange(bool enabled)
    {
        UpdateDesiredState(d => d with { OpenVpnEnabled = enabled, SelectedOpenVpnProfileId = OpenVpnProfile?.Id ?? State.OpenVpnProfileId });
        if (!enabled) RuntimeCoordinator?.CancelOpenVpnOperation();
        RuntimeCoordinator?.RequestReconcile(ReconcileReason.UserToggledOpenVpn);
    }

    public void CancelCurrentOperation()
    {
        if (CanCancelOpenVpn) { UserRequestedOpenVpnChange(false); return; }
        WorkCancellation.Cancel();
        TestsCancellation.Cancel();

    }

    public void UserSelectedVpnProfile(Guid? profileId)
    {
        selectionChange.Cancel();
        selectionChange.Dispose();
        selectionChange = new();
        var ct = selectionChange.Token;
        var revision = Interlocked.Increment(ref selectionRevision);
        pendingMainProfileId = profileId;
        OnPropertyChanged(nameof(MainProfile));
        PendingSelection = Task.Run(async () =>
        {
            try
            {
                var snapshot = ConfigRepository.CurrentSettings;
                var profile = snapshot.Profiles.FirstOrDefault(p => p.Id == profileId && !p.IsOpenVpn)
                    ?? throw new InvalidOperationException("Выберите существующий VPN-профиль.");
                WriteLog($"VPN_PROFILE intent old={GetCurrentDesiredState().SelectedVpnProfileId} new={profileId}");
                // A broken main router/TUN must not make manual recovery depend on
                // a probe through that same broken network. Healthy switches still
                // validate the replacement before disturbing the active connection.
                var recoveringMain = RuntimeCoordinator.CurrentConvergenceState is { Phase: ConvergencePhase.Degraded } convergence &&
                    convergence.PendingComponents.Any(c => c is ComponentId.MainRouter or ComponentId.Tun);
                if (GetCurrentDesiredState().MainVpnEnabled && !recoveringMain)
                {
                    var tested = Router.PreflightOverride is { } test
                        ? await test(profile, snapshot, ct).ConfigureAwait(false)
                        : await Router.TestProfileAsync(profile, snapshot, ct).ConfigureAwait(false);
                    if (!tested.Success) throw new InvalidDataException("Новый профиль не прошёл проверку: " + TestFeedback.Summary(tested));
                    WriteLog($"VPN_PROFILE preflight result=ok target={profile.Id}");
                }
                await profileCommitGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (revision != Interlocked.Read(ref selectionRevision)) return;
                    await ConfigRepository.UpdateSettingsAsync(next =>
                    {
                        var current = next.Profiles.FirstOrDefault(p => p.Id == profileId);
                        if (ProfileConnectionChanged(profile, current)) throw new OperationCanceledException("Профиль изменён во время проверки.");
                        next.MainProfileId = profileId;
                        return next;
                    }, ct).ConfigureAwait(false);
                    // Once persisted, complete the intent commit even if a newer selection cancels preflight.
                    UpdateDesiredState(d => d with { SelectedVpnProfileId = profileId });
                    RuntimeCoordinator.RequestReconcile(ReconcileReason.UserSelectedVpnProfile);
                    await RuntimeCoordinator.ReconcileAsync(ReconcileReason.UserSelectedVpnProfile, maintenanceLifetime.Token).ConfigureAwait(false);
                }
                finally { profileCommitGate.Release(); }
                projection.Post(() => Status = "Профиль выбран");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                WriteLog("VPN_PROFILE failed: " + ex.Message);
                projection.Post(() => { if (revision == selectionRevision) Status = ProcessHost.Redact(ex.Message); });
            }
            finally
            {
                projection.Post(() =>
                {
                    if (revision != selectionRevision) return;
                    pendingMainProfileId = null;
                    OnPropertyChanged(nameof(MainProfile));
                    NotifyState();
                });
            }
        });
    }

    public void UserSelectedOpenVpnProfile(Guid? profileId)
    {
        PendingOpenVpnSelection = SelectOpenVpnAsync(profileId);
    }
    public Task PendingOpenVpnSelection { get; private set; } = Task.CompletedTask;
    private async Task SelectOpenVpnAsync(Guid? profileId)
    {
        try
        {
            await ConfigRepository.UpdateSettingsAsync(next =>
            {
                if (profileId.HasValue && !next.Profiles.Any(p => p.Id == profileId && p.IsOpenVpn))
                    throw new InvalidOperationException("Профиль OpenVPN не найден.");
                next.OpenVpnProfileId = profileId;
                return next;
            }).ConfigureAwait(false);
            UpdateDesiredState(d => d with { SelectedOpenVpnProfileId = profileId });
            RuntimeCoordinator.RequestReconcile(ReconcileReason.UserChangedSettings);
        }
        catch (Exception ex) { WriteLog("OPENVPN_SELECTION error=" + ex.Message); }
    }

    public void SetVpnUpstreamDegraded(bool degraded)
    {
        if (VpnUpstreamDegraded != degraded)
        {
            VpnUpstreamDegraded = degraded;
            NotifyState();
        }
    }

    public void UpdateDesiredState(Func<DesiredRuntimeState, DesiredRuntimeState> update)
    {
        lock (desiredLock)
        {
            var next = update(DesiredState) with { LastUpdatedUtc = DateTimeOffset.UtcNow };
            Store.SaveDesiredState(next);
            DesiredState = next;
        }
    }

    public void FlushDesiredState()
    {
        lock (desiredLock)
        {
            Store.SaveDesiredState(DesiredState);
        }
    }

    private async Task PumpLogsAsync()
    {
        var file = Path.Combine(Store.Root, "diagnostic.log"); long last = 0, reportedDropped = 0;
        try
        {
            while (!maintenanceLifetime.IsCancellationRequested)
            {
                await Task.Delay(200, maintenanceLifetime.Token).ConfigureAwait(false);
                var rawLines = logQueue.Drain();
                var count = logQueue.Received; var dropped = logQueue.Dropped;
                var now = DateTimeOffset.UtcNow;
                var uiLines = new List<string>();
                var diagLines = new List<string>();

                foreach (var line in rawLines)
                {
                    coalescer.Ingest(line, now, out var uiOut, out var diagOut, out var flushedSummary);
                    if (flushedSummary != null)
                    {
                        uiLines.Add(flushedSummary);
                        diagLines.Add(flushedSummary);
                    }
                    if (uiOut != null) uiLines.Add(uiOut);
                    if (diagOut != null) diagLines.Add(diagOut);
                }

                var expired = coalescer.FlushExpired(now);
                foreach (var summary in expired)
                {
                    uiLines.Add(summary);
                    diagLines.Add(summary);
                }

                if (dropped != reportedDropped)
                {
                    var msg = $"LOG_OVERFLOW dropped={dropped - reportedDropped}; total={dropped}";
                    uiLines.Add(msg);
                    diagLines.Add(msg);
                    reportedDropped = dropped;
                }

                if (diagLines.Count > 0)
                {
                    try
                    {
                        if (File.Exists(file) && new FileInfo(file).Length > 8 * 1024 * 1024) File.Move(file, file + ".1", true);
                        await File.AppendAllLinesAsync(file, diagLines).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }

                if (uiLines.Count > 0)
                {
                    if (Application.Current?.Dispatcher is { } dispatcher)
                    {
                        await dispatcher.InvokeAsync(() =>
                        {
                            Logs.Prepend(uiLines);
                            LogMetrics = $"{(count - last) * 5} строк/с · пропущено {dropped}";
                            OnPropertyChanged(nameof(LogMetrics));
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    }
                    else
                    {
                        Logs.Prepend(uiLines);
                        LogMetrics = $"{(count - last) * 5} строк/с · пропущено {dropped}";
                        OnPropertyChanged(nameof(LogMetrics));
                    }
                }
                last = count;
            }
        }
        catch (OperationCanceledException) { }
    }

    public Task RetryNetworkAsync(CancellationToken ct)
    {
        RuntimeCoordinator.TriggerRetry();
        return Task.CompletedTask;
    }

    public ObservableCollection<string> Adapters { get; } = [];
    public ObservableCollection<ModuleRow> Modules { get; } = [];
    public string[] ModeNames { get; } = ["По правилам", "Весь трафик"];
    public string[] ServiceNames { get; } = ["Через VPN", "Через Zapret"];
    public Profile? SelectedProfile { get; set; }
    public RoutingRule? SelectedRule { get; set; }
    public StrategyResult? SelectedStrategy { get; set; }
    private bool refreshing, disposed;
    private bool publishing;
    private bool interfaceScaleChanged;
    private bool appearanceChanged;
    public Task PendingAppearanceSave { get; private set; } = Task.CompletedTask;
    public void FlushAppearancePreference()
    {
        if (!appearanceChanged) return;
        try { Store.AppearancePreference.Flush(AppearanceValues.From(State)); }
        catch (Exception error) { WriteLog("Сохранение оформления: " + error.GetType().Name); }
    }
    private async Task SaveAppearancePreferenceAsync(AppearanceValues appearance)
    {
        try { await Store.AppearancePreference.SaveAsync(appearance); }
        catch (Exception error) { WriteLog("Сохранение оформления: " + error.GetType().Name); }
    }
    public Task PendingInterfaceScaleSave { get; private set; } = Task.CompletedTask;
    public void FlushInterfaceScalePreference()
    {
        if (!interfaceScaleChanged) return;
        try { Store.InterfaceScalePreference.Flush(State.InterfaceScale); }
        catch (Exception error) { WriteLog("Сохранение размера интерфейса: " + error.GetType().Name); }
    }
    private async Task SaveInterfaceScalePreferenceAsync(double scale)
    {
        try { await Store.InterfaceScalePreference.SaveAsync(scale); }
        catch (Exception error) { WriteLog("Сохранение размера интерфейса: " + error.GetType().Name); }
    }
    private AppSettings committed = new();
    private readonly SemaphoreSlim settingsGate = new(1);
    private int suppressReactiveApplyCount;
    public bool SuppressReactiveApply => suppressReactiveApplyCount > 0;
    public IDisposable CreateStartupBatch()
    {
        Interlocked.Increment(ref suppressReactiveApplyCount);
        return new BatchScope(() => Interlocked.Decrement(ref suppressReactiveApplyCount));
    }
    private sealed class BatchScope(Action onDispose) : IDisposable
    {
        private Action? action = onDispose;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
    private static readonly HashSet<string> RoutingProperties = [nameof(AppSettings.Tun),nameof(AppSettings.PhysicalInterface),nameof(AppSettings.Mode),nameof(AppSettings.YouTube),nameof(AppSettings.Discord),nameof(AppSettings.TelegramSocks),nameof(AppSettings.TelegramVpnDefault),nameof(AppSettings.TelegramSocksHost),nameof(AppSettings.TelegramSocksPort),nameof(AppSettings.DirectDns),nameof(AppSettings.OpenVpnDns),nameof(AppSettings.LocalDomains),nameof(AppSettings.OpenVpnDomains),nameof(AppSettings.SocksPort)];
    private void StateChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e)
    {
        if(publishing || refreshing || disposed) return;
        if (e.PropertyName is nameof(AppSettings.BaseColor) or nameof(AppSettings.AccentColor) or
            nameof(AppSettings.PanelBrightness) or nameof(AppSettings.HighContrastText))
        {
            var appearance = AppearanceValues.From(State);
            try { appearance.Validate(); }
            catch (FormatException) { return; }
            appearanceChanged = true;
            appearance.Apply(committed);
            PendingAppearanceSave = SaveAppearancePreferenceAsync(appearance);
            projection.Post(() => Theme.Apply(State));
            return;
        }
        if (e.PropertyName == nameof(AppSettings.InterfaceScale))
        {
            interfaceScaleChanged = true;
            committed.InterfaceScale = State.InterfaceScale;
            PendingInterfaceScaleSave = SaveInterfaceScalePreferenceAsync(State.InterfaceScale);
            return;
        }
        if(e.PropertyName==nameof(AppSettings.OpenVpnProfileId) && Router.OpenVpn.Running && State.OpenVpnProfileId!=Router.OpenVpn.ActiveProfileId)
        { State.OpenVpnProfileId=Router.OpenVpn.ActiveProfileId; OnPropertyChanged(nameof(OpenVpnProfile)); return; }
        if(e.PropertyName is nameof(AppSettings.YouTube) or nameof(AppSettings.Discord)) InvalidateScenarioTests();
        if(e.PropertyName is nameof(AppSettings.LocalDomains) or nameof(AppSettings.OpenVpnDomains))
        {
            OnPropertyChanged(nameof(HasDomainConflict));
            OnPropertyChanged(nameof(DomainConflictWarning));
        }
        foreach(var name in new[]{nameof(ModeIndex),nameof(YouTubeIndex),nameof(DiscordIndex),nameof(TelegramSocks),nameof(TelegramVpnDefault),nameof(TelegramRouteDescription),nameof(Scenario),nameof(TunDesired)}) OnPropertyChanged(name);
        if (e.PropertyName != null && RoutingProperties.Contains(e.PropertyName))
        {
            if (e.PropertyName == nameof(AppSettings.Tun) && !SuppressReactiveApply)
            {
                UserRequestedTunChange(State.Tun);
            }
            if (!HasDomainConflict) ScheduleRoutesApply();
        }
    }
    private int routesRevision;
    private bool applyingScenario;
    public void ScheduleRoutesApply()
    {
        if (refreshing || disposed || SuppressReactiveApply) return;
        routesRevision++;
        if (!applyingScenario) PendingRoutes = ApplyScenarioAsync();
    }
    private async Task ApplyScenarioAsync()
    {
        if (applyingScenario) return;
        applyingScenario = true;
        try
        {
            await Task.Delay(App.IsSmoke ? 20 : 150);
            while (!disposed)
            {
                while ((Busy || TestsBusy || pendingMainProfileId.HasValue) && !disposed) await Task.Delay(50);
                if (disposed) return;
                var revision = routesRevision;
                await RunAsync(ApplyRoutesAsync);
                if (revision == routesRevision) break;
            }
        }
        finally { applyingScenario = false; }
    }
    private CancellationTokenSource selectionChange = new();
    private Guid? pendingMainProfileId;
    private long selectionRevision;
    public Task PendingSelection { get; private set; } = Task.CompletedTask;
    public long SettingsRevision { get; private set; }
    public AppSettings CommittedSnapshot => ConfigRepository.CurrentSettings;
    public Profile? MainProfile
    {
        get => VpnProfiles.FirstOrDefault(p=>p.Id==(pendingMainProfileId ?? State.MainProfileId));
        set
        {
            if(refreshing || value==null || value.Id==(pendingMainProfileId ?? committed.MainProfileId)) return;
            UserSelectedVpnProfile(value.Id);
            OnPropertyChanged();
        }
    }
    public bool CanSelectOpenVpn => Idle;
    public Profile? OpenVpnProfile { get => OpenVpnProfiles.FirstOrDefault(p => p.Id == (Router.OpenVpn.ActiveProfileId ?? State.OpenVpnProfileId)); set { if(refreshing)return; UserSelectedOpenVpnProfile(value?.Id); OnPropertyChanged(); } }
    public int ModeIndex { get => (int)State.Mode; set { if(value is < 0 or > 1 || value==(int)State.Mode || refreshing) return; State.Mode = value == 1 ? RoutingMode.Global : RoutingMode.Rules; } }
    public int YouTubeIndex { get => (int)State.YouTube; set { if (value is < 0 or > 1 || value == (int)State.YouTube || refreshing) return; State.YouTube = (ServiceRoute)value; } }
    public int DiscordIndex { get => (int)State.Discord; set { if (value is < 0 or > 1 || value == (int)State.Discord || refreshing) return; State.Discord = (ServiceRoute)value; } }
    public Task PendingRoutes { get; set; } = Task.CompletedTask;
    private void InvalidateScenarioTests()
    {
        if (TestsBusy) TestsCancellation.Cancel();
        foreach (var strategy in Strategies) if (strategy.Scenario != State.Scenario) strategy.RestoreHistory(State);
    }
    public bool TelegramSocks { get => State.TelegramSocks; set { State.TelegramSocks = value; OnPropertyChanged(); OnPropertyChanged(nameof(TelegramVpnDefault)); OnPropertyChanged(nameof(TelegramRouteDescription)); } }
    public bool TelegramVpnDefault
    {
        get => State.TelegramVpnDefault;
        set
        {
            if (refreshing) return;
            RoutingPreset.SelectTelegramDefault(State, value);
            OnPropertyChanged(); OnPropertyChanged(nameof(TelegramSocks)); OnPropertyChanged(nameof(TelegramRouteDescription));
        }
    }
    public string TelegramRouteDescription => State.TelegramSocks
        ? $"Выбран внешний SOCKS5: {State.TelegramSocksHost}:{State.TelegramSocksPort}. Галочка ниже переключает на VPN и отключает внешний SOCKS5."
        : State.TelegramVpnDefault ? "Без WS proxy: через VPN. Если VPN отключён, трафик Telegram не перенаправляется скрыто напрямую."
        : "Без WS proxy: напрямую, в том числе в режиме «Весь трафик».";
    public bool HasDomainConflict => !string.IsNullOrEmpty(DomainConflictWarning);
    public string DomainConflictWarning
    {
        get
        {
            try
            {
                var directSet = RuleValidation.Domains(State.LocalDomains).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var ovpnSet = RuleValidation.Domains(State.OpenVpnDomains).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var overlaps = directSet.Intersect(ovpnSet, StringComparer.OrdinalIgnoreCase).ToArray();
                if (overlaps.Length > 0)
                    return $"Конфликт маршрутов: домен «{overlaps[0]}» указан одновременно для прямого выхода и через OpenVPN. Удалите дублирование перед применением.";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            return "";
        }
    }
    private string status = "Готов к настройке";
    public string Status { get => status; set => SetProperty(ref status, value); }
    private string journalActionStatus = "";
    public string JournalActionStatus { get => journalActionStatus; set => SetProperty(ref journalActionStatus, value); }
    private bool busy;
    public bool Busy { get => busy; set { SetProperty(ref busy, value); OnPropertyChanged(nameof(Idle)); OnPropertyChanged(nameof(CanCancelOperation)); } }
    public bool Idle => !Busy;
    private bool testsBusy, checkingUpdates;
    public bool TestsBusy { get => testsBusy; private set { SetProperty(ref testsBusy,value); OnPropertyChanged(nameof(TestsIdle)); } }
    public bool TestsIdle => !TestsBusy;
    private string testStatus = "Тесты не запущены", updateStatus = "Проверка обновлений ещё не выполнялась";
    public string TestStatus { get => testStatus; set => SetProperty(ref testStatus,value); }
    public string UpdateStatus { get => updateStatus; set => SetProperty(ref updateStatus,value); }
    public bool CheckingUpdates { get => checkingUpdates; set { SetProperty(ref checkingUpdates,value); OnPropertyChanged(nameof(CanCheckUpdates)); } }
    public bool CanCheckUpdates => !CheckingUpdates;
    public int AvailableUpdateCount => Modules.Count(m => m.CanSelectUpdate);
    public bool HasUpdates => AvailableUpdateCount > 0;
    public string UpdateNotification => $"Доступны обновления: {AvailableUpdateCount}. Выберите компоненты и нажмите «Скачать выбранные».";
    public string ProfileDetails => Router.VpnRunning && Router.ActiveProfileId != State.MainProfileId ? "Проверяется новый профиль · сейчас работает " + State.Profiles.FirstOrDefault(p=>p.Id==Router.ActiveProfileId)?.Name : MainProfile == null ? "Импортируйте подписку или профиль" : $"{MainProfile.Protocol.ToUpperInvariant()} · {MainProfile.Core}";
    public string LocalEndpoint => Router.Running ? $"HTTP / SOCKS5 · 127.0.0.1:{Router.ListenPort}" : $"HTTP / SOCKS5 · 127.0.0.1:{State.SocksPort}";
    public bool VpnConnected => Router.VpnRunning;
    public bool TelegramRunning => Telegram.Running;
    public bool TelegramReady => Telegram.Ready;
    public bool ZapretRunning => Zapret.Running;
    public string TelegramStatus => Telegram.Ready ? $"Работает · 127.0.0.1:{Telegram.Port}" : Telegram.Running ? "WS proxy запускается…" : "WS proxy выключен";
    public string TelegramButton => Telegram.Running ? "Остановить" : "Включить";
    private string healthDetails = "Подключите VPN для проверки доступа.";
    private int healthPort;
    private readonly TunDiagnosticPolicy tunDiagnostics = new();
    public bool Recovering { get; set; }
    public string HealthDetails =>
        RuntimeCoordinator?.CurrentConvergenceState is { Phase: ConvergencePhase.Degraded } convergence ?
            $"Ожидают восстановления: {string.Join(", ", convergence.PendingComponents)}. {convergence.LastError}" :
        NetworkState == NetworkLifecycleState.NetworkUnavailable ? "Не удалось восстановить сеть. Нажмите «Повторить» на вкладке «Журнал»." :
        Router.RecoveryStatus.Length > 0 ? Router.RecoveryStatus :
        !Router.VpnRequested && !Router.Reconfiguring ?
            (Router.Running && Router.TunActive && DesiredState.OpenVpnEnabled ? "OpenVPN использует локальный TUN; основной VPN отключён." : "Подключите VPN для проверки доступа.") :
        healthDetails + (tunDiagnostics.Detail(RuntimeCoordinator?.CurrentObservedState?.TunStatus ?? TunStructuralStatus.Unknown) is { Length: > 0 } detail ? " · " + detail : "");

    public string VpnStatus
    {
        get
        {
            var convergence = RuntimeCoordinator?.CurrentConvergenceState;
            var observed = RuntimeCoordinator?.CurrentObservedState;
            var status = VpnStatusFor(convergence?.PhysicalNetworkState ?? PhysicalNetworkAvailability.Unknown,
                RuntimeCoordinator?.MainRouterLifecycle ?? MainRouterLifecycle.StoppedByDesired,
                observed?.TunStatus, DesiredState.MainVpnEnabled || Router.VpnRequested,
                DesiredState.TunEnabled, Router.VpnRunning, Router.Reconfiguring, VpnUpstreamDegraded);
            return status == "VPN подключён" && activeTrafficTest?.Validate(Router.CaptureTrafficStamp()) is { Unavailable.Length: 0, Result.Success: false }
                ? "VPN запущен · тест трафика не пройден" : status;
        }
    }

    private ActiveTrafficTest? activeTrafficTest;
    public string ActiveTrafficSummary => activeTrafficTest?.Validate(Router.CaptureTrafficStamp()).Summary ?? "Текущий TUN: передача данных не проверена";
    public string ActiveTrafficDetails => activeTrafficTest?.Validate(Router.CaptureTrafficStamp()).Details ?? "Проверка текущего TUN выполняет загрузку, отправку с проверкой эхо и UDP запросы без временного VPN.";

    public static string VpnStatusFor(PhysicalNetworkAvailability physical, MainRouterLifecycle lifecycle,
        TunStructuralStatus? tun, bool desired, bool tunEnabled, bool vpnRunning, bool reconfiguring, bool upstreamDegraded)
    {
            if (physical == PhysicalNetworkAvailability.Unknown)
            {
                return "Определяю состояние сети…";
            }

            if (physical == PhysicalNetworkAvailability.Unavailable)
            {
                return "Физическая сеть недоступна · ожидание подключения…";
            }

            if (!desired) return "VPN отключён";
            if (lifecycle == MainRouterLifecycle.Starting || reconfiguring) return "VPN подключается · TUN запускается…";
            if (tunEnabled && tun == TunStructuralStatus.StructuralFailure) return "TUN недоступен · восстанавливаю…";
            if (!vpnRunning) return "Нет доступа · ядро остановлено";
            if (upstreamDegraded) return "Основной VPN-сервер не отвечает · Direct и Zapret доступны";

            if (tunEnabled)
            {
                return tun switch
                {
                    TunStructuralStatus.Healthy => "VPN подключён",
                    TunStructuralStatus.Starting => "VPN подключается · TUN запускается…",
                    TunStructuralStatus.TransientDegraded => "VPN подключён · проверяю TUN…",
                    TunStructuralStatus.StructuralFailure => "TUN недоступен · восстанавливаю…",
                    _ => "Проверяю состояние TUN…"
                };
            }

            return "VPN подключён";
    }
    public void SetHealth(DelayResult profile, DelayResult? system)
    {
        tunDiagnostics.Observe(system, Router.TunActive, Router.SessionRevision);
        if (tunDiagnostics.WeakFailures > 0)
            WriteLog($"TUN_DIAGNOSTIC classification=weak count={tunDiagnostics.WeakFailures} error={ProcessHost.Redact(system?.Error ?? "probe unavailable")}");
        healthDetails = "Профиль: " + TestFeedback.Summary(profile);
        NotifyState();
    }
    public string VpnButton => DesiredState.MainVpnEnabled ? "Отключить VPN" : "Подключить VPN";
    public string OpenVpnStatus
    {
        get
        {
            if (!DesiredState.OpenVpnEnabled)
            {
                return Router.OpenVpn.RequiresStop ? "OpenVPN останавливается и очищает маршруты…" : "OpenVPN отключён";
            }

            var retryState = RuntimeCoordinator?.OpenVpnRetryController?.State ?? OpenVpnRetryState.Idle;
            return retryState switch
            {
                OpenVpnRetryState.SuspendedFatal => "OpenVPN: критическая ошибка конфигурации или шифрования. Подключение приостановлено.",
                OpenVpnRetryState.SuspendedAuth => "OpenVPN: ошибка аутентификации. Проверьте логин и пароль.",
                OpenVpnRetryState.WaitingForRelevantChange => "OpenVPN: превышен лимит попыток. Ожидание изменения сети или ручного повтора.",
                OpenVpnRetryState.RetryScheduled => RuntimeCoordinator?.OpenVpnRetryController?.NextAttemptAt is {} next
                    ? $"OpenVPN: ошибка подключения, повтор через {Math.Max(1, (int)Math.Ceiling((next - (RuntimeCoordinator?.Clock.UtcNow ?? DateTimeOffset.UtcNow)).TotalSeconds))} с…"
                    : "OpenVPN: запланирован повтор подключения…",
                OpenVpnRetryState.Starting or OpenVpnRetryState.Connecting => "OpenVPN подключается…",
                OpenVpnRetryState.Connected => Router.TunActive ? "OpenVPN подключён" : "OpenVPN: только IP-маршруты; для корпоративных доменов включите TUN",
                _ => Router.OpenVpn.RuntimePhase switch
                {
                    OpenVpnRuntimePhase.LongReconnect => "OpenVPN долго переподключается; корпоративные маршруты закрыты. Можно выключить или повторить подключение.",
                    OpenVpnRuntimePhase.FailedCleanup => "OpenVPN: требуется повторная очистка маршрутов",
                    OpenVpnRuntimePhase.CleanupPending or OpenVpnRuntimePhase.Stopping => "OpenVPN останавливается и очищает маршруты…",
                    _ => Router.OpenVpn.Reconnecting ? "OpenVPN переподключается; корпоративные маршруты закрыты" : Router.OpenVpn.Running ? (Router.TunActive ? "OpenVPN подключён" : "OpenVPN: только IP-маршруты; для корпоративных доменов включите TUN") : "OpenVPN отключён"
                }
            };
        }
    }

    public bool CanCancelOpenVpn => DesiredState.OpenVpnEnabled && (RuntimeCoordinator.OpenVpnRuntime.Reconnecting || RuntimeCoordinator.OpenVpnRetryController.State is OpenVpnRetryState.Starting or OpenVpnRetryState.Connecting or OpenVpnRetryState.RetryScheduled);
    public bool CanCancelOperation => Busy || CanCancelOpenVpn;
    public string OpenVpnButton
    {
        get
        {
            if (!DesiredState.OpenVpnEnabled)
                return "Подключить OpenVPN";

            if (CanCancelOpenVpn) return "Отменить подключение";
            var retryState = RuntimeCoordinator?.OpenVpnRetryController?.State ?? OpenVpnRetryState.Idle;
            return retryState switch
            {
                OpenVpnRetryState.Starting or OpenVpnRetryState.Connecting or OpenVpnRetryState.RetryScheduled => "Отменить подключение",
                _ => "Отключить OpenVPN"
            };
        }
    }
    public string ZapretStatus => Zapret.Running ? "Запущен: " + Zapret.ActiveStrategy + " · " + (Zapret.FunctionalValidation ?? "обход не подтверждён") : "Zapret остановлен";
    public string Scenario => $"YouTube → {RussianLabels.Of(State.YouTube)}; Discord → {RussianLabels.Of(State.Discord)}";
    public CancellationTokenSource WorkCancellation { get; private set; } = new();
    public CancellationTokenSource TestsCancellation { get; private set; } = new();
    public RuntimeCoordinator RuntimeCoordinator { get; }
    public TrafficMonitorService TrafficMonitor { get; }
    public RuntimeConfigurationRepository ConfigRepository { get; }
    public VpnHealthMonitorService HealthMonitor { get; }
    public double DownloadMbps => TrafficMonitor.DownloadMbps;
    public double UploadMbps => TrafficMonitor.UploadMbps;
    public string ActiveInterfaceName => TrafficMonitor.ActiveInterfaceName;
    public MainViewModel(SettingsStore store, AppSettings settings, RouterService? router = null, ZapretService? zapret = null)
    {
        projection = new UiProjection(ex => WriteLog("UI_PROJECTION error=" + ex.Message));
        Store = store; State = settings; SettingsMigration.Apply(State);
        DesiredState = store.LoadDesiredState();
        store.Diagnostic += WriteLog;
        if (store.DesiredRecoveredFromBackup)
        {
            Status = "Намерения подключения восстановлены из резервной копии; проверьте выбранные подключения.";
            WriteLog("DESIRED_STATE result=recovered-backup");
        }
        if (DesiredState.SelectedVpnProfileId == null && State.MainProfileId.HasValue)
        {
            DesiredState = DesiredState with { SelectedVpnProfileId = State.MainProfileId };
        }
        if (DesiredState.SelectedOpenVpnProfileId == null && State.OpenVpnProfileId.HasValue)
        {
            DesiredState = DesiredState with { SelectedOpenVpnProfileId = State.OpenVpnProfileId };
        }
        Modules.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(AvailableUpdateCount));
            OnPropertyChanged(nameof(HasUpdates));
            OnPropertyChanged(nameof(UpdateNotification));
        };
        Bin = Path.Combine(AppContext.BaseDirectory, "modules");
        if (!Directory.Exists(Bin) && Directory.Exists(Path.Combine(AppContext.BaseDirectory, "bin"))) Bin = Path.Combine(AppContext.BaseDirectory, "bin");
        if (!Directory.Exists(Bin)) { var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "bin")); if (Directory.Exists(candidate)) Bin = candidate; }
        Router = router ?? new(Bin, Path.Combine(store.Root, "runtime")); Zapret = zapret ?? new(Bin, Path.Combine(store.Root, "runtime", "zapret")); Updater = new(Bin,()=>Router.VpnRunning ? Router.LatencyPort : 0);
        TrafficMonitor = new TrafficMonitorService(() => PhysicalNetwork.TryCapture(ConfigRepository?.CurrentSettings.PhysicalInterface ?? ""));

        Telegram = new(Bin,Path.Combine(store.Root,"runtime","telegram")); Telegram.Log += WriteLog;
        Router.Log += WriteLog; Zapret.Log += WriteLog; Updater.Log += WriteLog; Router.Changed += NotifyState;
        Rules.CollectionChanged += (_,_) => { if(!refreshing) { State.Rules=Rules.ToList(); ScheduleRoutesApply(); } };
        Refresh(); foreach (var a in PhysicalNetwork.Adapters()) Adapters.Add(a.Name);
        foreach(var key in ModuleUpdater.Keys)
            Modules.Add(
                CreateModuleRow(
                    new ModuleCheck(
                        key,
                        Updater.InstalledVersion(key),
                        Updater.PreparedRelease(key)),
                    checkedAlready:false));
        committed=JsonSettings.Clone(State); State.PropertyChanged+=StateChanged;

        ConfigRepository = new RuntimeConfigurationRepository(State, store)
        {
            ProjectionFailed = ex => WriteLog("CONFIGURATION_PROJECTION error=" + ex.Message)
        };
        ConfigRepository.ConfigurationChanged += snapshot => projection.Post(() =>
        {
            if (snapshot.Revision <= projectedRevision) return;
            projectedRevision = snapshot.Revision;
            committed = snapshot.Read();
            PublishSettingsCore(snapshot.Read());
        });
        TrafficMonitor.SampleUpdated += _ => projection.Post(() =>
        {
            OnPropertyChanged(nameof(DownloadMbps));
            OnPropertyChanged(nameof(UploadMbps));
            OnPropertyChanged(nameof(ActiveInterfaceName));
        });

        RuntimeCoordinator = new RuntimeCoordinator(this, Router, Zapret, Router.OpenVpn)
        {
            GetSettings = () => ConfigRepository.CurrentSettings,
            CreateBatch = () => CreateStartupBatch(),
            Log = WriteLog,
            OnOpenVpnRoutesLearned = (profileId, routes, ct) => ConfigRepository.UpdateOpenVpnLearnedRoutesAsync(profileId, routes, ct)
        };
        RuntimeCoordinator.OpenVpnRetryController.StateChanged += _ => projection.Post(NotifyState);
        RuntimeCoordinator.StateChanged += state =>
        {
            isStartupRestoring = state is StartupRestoreState.WaitingForNetwork or StartupRestoreState.Restoring;
            NotifyState();
        };
        RuntimeCoordinator.ConvergenceStateChanged += _ => NotifyState();
        RuntimeCoordinator.ObservedStateChanged += _ => NotifyState();
        NetworkMonitor = new PhysicalNetworkMonitor(RuntimeCoordinator, ConfigRepository);

        HealthMonitor = new VpnHealthMonitorService(
            ConfigRepository,
            Router,
            RuntimeCoordinator,
            GetCurrentDesiredState,
            update => UpdateDesiredState(update))
        {
            TestProfileFunc = (profile, s, ct) => Router.TestProfileAsync(profile, s, ct),
            CheckTunnelHealthFunc = (port, ct) => new SystemTunnelHealth().CheckAsync(port, ct),
            Log = WriteLog
        };

        HealthMonitor.HealthUpdated += (profileResult, systemResult) =>
        {
            if (profileResult != null)
            {
                projection.Post(() => SetHealth(profileResult, systemResult));
            }
        };

        applicationUpdate = new AutomaticApplicationUpdate(
            () => CanAutomaticallyUpdate, () => Modules.FirstOrDefault(m => m.Key == "netcat" && m.CanSelectUpdate)?.Check.Release,
            (release, ct) => PortableUpdate.PrepareAsync(release, AppContext.BaseDirectory,
                State.PinnedModules.Concat(ModuleUpdater.Keys.Where(key => key != "netcat")).ToHashSet(StringComparer.OrdinalIgnoreCase), ct),
            job => ApplicationUpdatePrepared?.Invoke(job),
            ex => { UpdateStatus = "Автообновление NetCat отложено: " + ex.Message; WriteLog(UpdateStatus); });
        maintenance = new ApplicationMaintenanceScheduler(
            () => State, () => !Busy && !Recovering && !IsStartupRestoring,
            () => !TestsBusy, RunBackgroundProfileTestsAsync, CheckUpdatesAsync,
            ex => WriteLog("BACKGROUND_MAINTENANCE error=" + ProcessHost.Redact(ex.Message)),
            maintain: applicationUpdate.TickAsync);

        if (!App.IsSmoke)
        {
            maintenance.Start();
            HealthMonitor.Start();
            TrafficMonitor.StartSampling();
            NetworkMonitor.Start();
            var subscriptions = new SubscriptionScheduler(
                () => ConfigRepository.CurrentSettings.Subscriptions,
                async (sub, ct) => { await RefreshSubscriptionAsync(sub.Id, ct).ConfigureAwait(false); },
                () => !Busy && !TestsBusy && !IsStartupRestoring,
                (id, message) => WriteLog($"SUBSCRIPTION id={id} {message}"));
            subscriptionPump = subscriptions.RunAsync(maintenanceLifetime.Token);
        }

        logPump = Task.Run(PumpLogsAsync);

    }
    public void Refresh() => projection.Post(RefreshCore);

    private Task RunBackgroundProfileTestsAsync(CancellationToken ct) => RunTestsAsync(async testToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, testToken);
        var settings = JsonSettings.Clone(State);
        var candidates = settings.Profiles.Where(p => !p.IsOpenVpn && (p.Candidate || p.Id == settings.MainProfileId)).ToArray();
        foreach (var p in candidates)
        {
            TestStatus = "Фоновый тест: " + p.Name;
            var result = await Router.TestProfileAsync(p, settings, linked.Token);
            Profiles.FirstOrDefault(x => x.Id == p.Id)?.SetTestResult(result);
        }
    });

    private void RefreshCore()
    {
        refreshing = true;
        try
        {
            SettingsMigration.Apply(State);
            // Selection IDs must survive collection reset; ComboBox resets SelectedItem while clearing.
            var mainId = State.MainProfileId; var ovpnId = State.OpenVpnProfileId;
            Profiles.Clear(); VpnProfiles.Clear(); OpenVpnProfiles.Clear(); Rules.Clear();
            foreach (var p in State.Profiles)
            {
                if (OpenVpnConfiguration.MigrationDiagnostic(p) is {} diagnostic) { p.Result="Нужна совместимая конфигурация"; p.ResultDetails=diagnostic; }
                Profiles.Add(p); if (p.IsOpenVpn) OpenVpnProfiles.Add(p); else VpnProfiles.Add(p);
            }
            foreach (var r in State.Rules) Rules.Add(r);
            SettingsValidation.RepairSelections(State);
            var previous = Strategies.ToDictionary(s => s.File, StringComparer.OrdinalIgnoreCase);
            Strategies.Clear(); foreach (var s in Zapret.Strategies()) { var item=previous.GetValueOrDefault(s.File,s); if(!TestsBusy) item.RestoreHistory(State); Strategies.Add(item); }
            SelectedStrategy = Strategies.FirstOrDefault(s => Path.GetFileName(s.File) == State.ZapretStrategy) ?? Strategies.FirstOrDefault();
        }
        catch (Exception ex)
        {
            WriteLog("UI_PROJECTION error=" + ex.Message);
        }
        finally
        {
            refreshing = false;
        }
        OnPropertyChanged(nameof(MainProfile)); OnPropertyChanged(nameof(OpenVpnProfile)); OnPropertyChanged(nameof(SelectedStrategy)); NotifyState();
    }
    public void NotifyState()
    {
        if (!projection.CheckAccess()) { projection.Post(NotifyState); return; }
        OnPropertyChanged(nameof(ActiveTrafficSummary)); OnPropertyChanged(nameof(ActiveTrafficDetails));
        if (healthPort != Router.LatencyPort) { healthPort=Router.LatencyPort; healthDetails="Проверяю профиль и системный маршрут. Это займёт несколько секунд."; }
        OnPropertyChanged(nameof(CanSelectOpenVpn)); OnPropertyChanged(nameof(OpenVpnProfile));
        foreach (var property in new[] { nameof(HealthDetails), nameof(VpnStatus), nameof(VpnButton), nameof(OpenVpnStatus), nameof(OpenVpnButton), nameof(CanCancelOperation), nameof(CanCancelOpenVpn), nameof(ZapretStatus), nameof(Scenario), nameof(ProfileDetails), nameof(VpnConnected), nameof(LocalEndpoint), nameof(TelegramStatus), nameof(TelegramButton), nameof(TelegramRunning), nameof(TelegramReady), nameof(ZapretRunning) }) OnPropertyChanged(property);
        foreach(var strategy in Strategies)
        {
            strategy.IsActive = Zapret.Running && Path.GetFileName(strategy.File) == Zapret.ActiveStrategy;
            strategy.IsPrimary = Path.GetFileName(strategy.File) == State.ZapretStrategy;
        }
    }
    public void WriteLog(string text)
    {
        var redacted = ProcessHost.Redact(text);
        logQueue.Add(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + redacted);
        if (text.Contains("The requested address is not valid") || text.Contains("WSAEADDRNOTAVAIL"))
        {
            NetworkMonitor?.Signal();
            logQueue.Add("DIRECT_BIND_STALE · refresh scheduled");
        }
    }
    private (bool Running,bool Requested,bool Tun,int Port,bool OpenVpn,Guid? OpenVpnId,OpenVpnRuntimePhase OpenVpnPhase,OpenVpnRetryState RetryState,bool Zapret,string Strategy,bool Telegram,bool TelegramReady)? runtimeSnapshot;
    public void PollRuntimeState()
    {
        if(activeTrafficTest is { Unavailable.Length: 0 } && activeTrafficTest.Validate(Router.CaptureTrafficStamp()) is { Unavailable.Length: > 0 } invalidated)
        {
            activeTrafficTest = invalidated; NotifyState();
        }
        var next=(Router.Running,Router.VpnRequested,Router.TunActive,Router.LatencyPort,Router.OpenVpn.Running,Router.OpenVpn.ActiveProfileId,Router.OpenVpn.RuntimePhase,RuntimeCoordinator?.OpenVpnRetryController?.State ?? OpenVpnRetryState.Idle,Zapret.Running,Zapret.ActiveStrategy,Telegram.Running,Telegram.Ready);
        if(runtimeSnapshot==next)return;
        runtimeSnapshot=next;NotifyState();
    }
    public async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (Busy) return; Busy = true; WorkCancellation.Dispose(); WorkCancellation = new();
        try { await work(WorkCancellation.Token); Status = "Готово"; }
        catch (OperationCanceledException) { Status = "Операция отменена"; }
        catch (Exception e) { Status = ProcessHost.Redact(e.Message); WriteLog(Status); }
        finally { Busy = false; NotifyState(); }
    }
    public async Task RunTestsAsync(Func<CancellationToken,Task> work)
    {
        if(TestsBusy)
        {
            WriteLog("ZAPRET_TEST_IGNORED · test already running");
            return;
        }
        TestsBusy=true; TestsCancellation.Dispose(); TestsCancellation=new();
        try { await work(TestsCancellation.Token); TestStatus="Проверка завершена"; }
        catch(OperationCanceledException) {
            TestStatus="Проверка отменена";
            foreach(var profile in Profiles.Where(p=>p.Result=="Проверяется…")) profile.SetTestResult(new(false,-1,"Проверка отменена пользователем"));
            foreach(var profile in Profiles.Where(p=>p.TrafficResult=="Проверяется…")) { profile.TrafficResult="Проверка отменена"; profile.TrafficDetails="Проверка отменена пользователем."; }
        }
        catch(TimeoutException te) { TestStatus="Превышено общее время проверки: " + ProcessHost.Redact(te.Message); WriteLog(TestStatus); }
        catch(Exception e) { TestStatus=ProcessHost.Redact(e.Message); WriteLog(TestStatus); }
        finally { TestsBusy=false; }
    }
    public ModuleRow CreateModuleRow(
        ModuleCheck check,
        bool checkedAlready = true)
    {
        var previous =
            Updater.CanRollback(check.Key)
                ? Updater.PreviousVersion(check.Key)
                : "";

        return new ModuleRow(
            check,
            State.PinnedModules.Contains(check.Key),
            checkedAlready,
            previous);
    }
    public async Task CheckUpdatesAsync(CancellationToken ct)
    {
        if(CheckingUpdates) return; CheckingUpdates=true; UpdateStatus="Проверяю обновления компонентов…";
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var checks=
                await Updater.CheckAllAsync(
                    timeout.Token);

            ApplyModuleChecks(checks);
        }
        catch(OperationCanceledException) { UpdateStatus="Проверка обновлений прервана или истёк таймаут"; }
        finally { CheckingUpdates=false; OnPropertyChanged(nameof(HasUpdates)); }
    }
    public void ApplyModuleChecks(IReadOnlyList<ModuleCheck> checks)
    {
        Modules.Clear();
        foreach (var check in checks) Modules.Add(CreateModuleRow(check));
        var errors = checks.Count(c => c.Error.Length > 0);
        UpdateStatus = HasUpdates ? UpdateNotification : errors > 0 ? "Проверка выполнена частично" :
            checks.Any(c => c.Available) ? "Нет обновлений, доступных для установки" : "Все компоненты актуальны";
        if (errors > 0) UpdateStatus += $" · Не удалось проверить: {errors}";
    }
    private static string Json(object? value) => System.Text.Json.JsonSerializer.Serialize(value,JsonSettings.Options);
    private static bool ProfileConnectionChanged(Profile? a,Profile? b) => a?.Id!=b?.Id || a?.Host!=b?.Host || a?.Port!=b?.Port || a?.Core!=b?.Core || a?.OutboundJson!=b?.OutboundJson || a?.OpenVpnConfig!=b?.OpenVpnConfig || a?.Username!=b?.Username || a?.Password!=b?.Password;
    private bool RoutingChanged(AppSettings old,AppSettings next) => RoutingProperties.Any(name=>!Equals(typeof(AppSettings).GetProperty(name)!.GetValue(old),typeof(AppSettings).GetProperty(name)!.GetValue(next))) || Json(old.Rules)!=Json(next.Rules);
    public async Task SetVpnEnabledAsync(bool enabled,CancellationToken ct,string? reason=null)
    {
        await CommitDraftAsync(ct);
        UpdateDesiredState(d => d with { MainVpnEnabled = enabled, TunEnabled = State.Tun, SelectedVpnProfileId = State.MainProfileId });
        await RuntimeCoordinator.ReconcileAsync(ReconcileReason.UserToggledVpn, ct);
    }
    public async Task SetOpenVpnEnabledAsync(bool enabled,CancellationToken ct,string? reason=null)
    {
        if (!enabled)
        {
            UserRequestedOpenVpnChange(false);
            await RuntimeCoordinator.ReconcileAsync(ReconcileReason.UserToggledOpenVpn, ct);
            return;
        }
        await PendingOpenVpnSelection;
        await CommitDraftAsync(ct);
        UserRequestedOpenVpnChange(true);
        await RuntimeCoordinator.ReconcileAsync(ReconcileReason.UserToggledOpenVpn, ct);
    }
    public async Task SetTelegramEnabledAsync(bool enabled,CancellationToken ct)
    {
        if (enabled)
        {
            if (!Telegram.Running) await Telegram.StartAsync(CommittedSnapshot, ct);
        }
        else
        {
            if (Telegram.Running) await Telegram.StopAsync();
        }
        NotifyState();
    }
    public Task SaveAsync() => CommitDraftAsync(CancellationToken.None);
    public async Task InstallPreparedModuleAsync(ModuleRelease release,CancellationToken ct)
    {
        if(release.Key == "zapret" && TestsBusy)
        {
            WriteLog("INSTALL_ZAPRET_REQUESTED · canceling active zapret tests");
            TestsCancellation.Cancel();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while(TestsBusy && sw.ElapsedMilliseconds < 5000)
            {
                await Task.Delay(50, ct);
            }
            if(TestsBusy) throw new InvalidOperationException("Дождитесь завершения теста или отмените его перед установкой.");
        }
        else if (TestsBusy && release.Key != "geoip" && release.Key != "geosite")
        {
            throw new InvalidOperationException("Дождитесь завершения теста или отмените его перед установкой.");
        }
        bool telegramWasRunning=Telegram.Running;
        await RuntimeCoordinator.RunModuleUpdateAsync(release.Key,async(restore,stop,token)=>
        {
            if(release.Key=="tg-ws-proxy"&&telegramWasRunning)await Telegram.StopAsync();
            try
            {
                await Updater.InstallPreparedAsync(release,State,token,async healthToken=>
                {
                    if(release.Key=="tg-ws-proxy"&&telegramWasRunning)
                    {
                        try{await Telegram.StartAsync(CommittedSnapshot,healthToken);if(!Telegram.Running)throw new IOException("Telegram update startup failed.");}
                        catch{await Telegram.StopAsync();throw;}
                    }
                    await restore(healthToken);
                },async()=>{if(release.Key=="tg-ws-proxy")await Telegram.StopAsync();await stop();});
            }
            finally
            {
                if(release.Key=="tg-ws-proxy"&&telegramWasRunning&&!Telegram.Running)
                {using var recovery=new CancellationTokenSource(TimeSpan.FromSeconds(15));await Telegram.StartAsync(CommittedSnapshot,recovery.Token);}
                NotifyState();
            }
        },ct);
    }
    public async Task RollbackModuleAsync(
        string key,
        CancellationToken ct)
    {
        if(!Updater.CanRollback(key))
            throw new InvalidOperationException(
                "Предыдущая версия компонента недоступна.");

        if(key=="openvpn")
            throw new InvalidOperationException(
                "OpenVPN исключён из общего механизма rollback.");

        if(key=="zapret" && TestsBusy)
        {
            WriteLog(
                "ROLLBACK_ZAPRET_REQUESTED · canceling active zapret tests");

            TestsCancellation.Cancel();

            var wait =
                System.Diagnostics.Stopwatch.StartNew();

            while(TestsBusy &&
                  wait.ElapsedMilliseconds < 5000)
            {
                await Task.Delay(
                    50,
                    ct);
            }

            if(TestsBusy)
                throw new InvalidOperationException(
                    "Не удалось остановить активный тест Zapret перед откатом.");
        }
        else if(TestsBusy &&
                key!="geoip" &&
                key!="geosite")
        {
            throw new InvalidOperationException(
                "Дождитесь завершения теста или отмените его перед откатом.");
        }

        var telegramWasRunning =
            Telegram.Running;

        await RuntimeCoordinator.RunModuleUpdateAsync(
            key,
            async(restore,stop,token) =>
            {
                var swapped =
                    false;

                if(key=="tg-ws-proxy" &&
                   telegramWasRunning &&
                   Telegram.Running)
                {
                    await Telegram.StopAsync();
                }

                try
                {
                    Updater.Rollback(
                        key);

                    swapped =
                        true;

                    WriteLog(
                        $"UPDATE_ROLLBACK module={key} result=swapped");

                    if(key=="tg-ws-proxy" &&
                       telegramWasRunning)
                    {
                        try
                        {
                            await Telegram.StartAsync(
                                CommittedSnapshot,
                                token);

                            if(!Telegram.Running)
                                throw new IOException(
                                    "Telegram rollback startup failed.");
                        }
                        catch
                        {
                            await Telegram.StopAsync();
                            throw;
                        }
                    }

                    await restore(
                        token);

                    WriteLog(
                        $"UPDATE_ROLLBACK_RESTART module={key} result=ready");
                }
                catch(Exception rollbackFailure)
                {
                    if(!swapped)
                        throw;

                    WriteLog(
                        $"UPDATE_ROLLBACK module={key} result=health-failed reverting=true error={rollbackFailure.Message}");

                    try
                    {
                        if(key=="tg-ws-proxy" &&
                           Telegram.Running)
                        {
                            await Telegram.StopAsync();
                        }

                        await stop();

                        // Rollback() swaps current <-> .previous.
                        // Calling it again restores the version that
                        // was active before this rollback attempt.
                        Updater.Rollback(
                            key);

                        WriteLog(
                            $"UPDATE_ROLLBACK module={key} result=reverted-to-original");

                        if(key=="tg-ws-proxy" &&
                           telegramWasRunning)
                        {
                            await Telegram.StartAsync(
                                CommittedSnapshot,
                                token);

                            if(!Telegram.Running)
                                throw new IOException(
                                    "Telegram recovery startup failed.");
                        }

                        await restore(
                            token);

                        WriteLog(
                            $"UPDATE_ROLLBACK_RECOVERY module={key} result=ready");
                    }
                    catch(Exception recoveryFailure)
                    {
                        throw new AggregateException(
                            "Откат не прошёл проверку, а автоматическое восстановление исходной версии также завершилось ошибкой.",
                            rollbackFailure,
                            recoveryFailure);
                    }

                    throw;
                }
                finally
                {
                    if(key=="tg-ws-proxy" &&
                       telegramWasRunning &&
                       !Telegram.Running)
                    {
                        try
                        {
                            using var recovery =
                                new CancellationTokenSource(
                                    TimeSpan.FromSeconds(15));

                            await Telegram.StartAsync(
                                CommittedSnapshot,
                                recovery.Token);
                        }
                        catch(Exception ex)
                        {
                            WriteLog(
                                $"UPDATE_ROLLBACK_RECOVERY module={key} result=telegram-restart-failed error={ex.Message}");
                        }
                    }

                    NotifyState();
                }
            },
            ct);
    }
    public async Task ApplyRoutesAsync(CancellationToken ct)
    {
        if (HasDomainConflict) throw new InvalidOperationException(DomainConflictWarning);
        await CommitDraftAsync(ct);
        await RuntimeCoordinator.ReconcileAsync(ReconcileReason.UserChangedSettings, ct);
    }
    private Task CommitDraftAsync(CancellationToken ct)
    {
        var desired=JsonSettings.Clone(State); desired.MainProfileId=committed.MainProfileId;
        desired.Rules=Rules.Select(JsonSettings.Clone).ToList();
        desired.TestIntervalSeconds=Math.Max(1,desired.TestIntervalSeconds); desired.TestTimeoutSeconds=Math.Clamp(desired.TestTimeoutSeconds,2,60); desired.FailureThreshold=Math.Clamp(desired.FailureThreshold,2,10);
        var baseline=JsonSettings.Clone(committed);
        var changes=typeof(AppSettings).GetProperties().Where(p=>p.CanWrite && Json(p.GetValue(desired))!=Json(p.GetValue(baseline))).ToArray();
        return UpdateSettingsAsync(next=>
        {
            foreach(var property in changes)
            {
                if(property.Name==nameof(AppSettings.Profiles)) {next.Profiles=ProfileSettingsMerge.Merge(baseline.Profiles,desired.Profiles,next.Profiles);continue;}
                // Rebase unrelated changes; a stale draft must not overwrite a newer change to the same field.
                if(Json(property.GetValue(next))!=Json(property.GetValue(baseline)) && Json(property.GetValue(next))!=Json(property.GetValue(desired))) throw new OperationCanceledException("Настройки уже изменены другой операцией. Повторите изменение.");
                property.SetValue(next,property.GetValue(desired));
            }
        }, ct: ct, validateExtra: async (s, t) =>
        {
            if (!string.IsNullOrEmpty(s.PhysicalInterface))
            {
                await Task.Run(() => PhysicalNetwork.CaptureExact(s.PhysicalInterface), t);
            }
        });
    }
    public async Task UpdateSettingsAsync(Action<AppSettings> change, CancellationToken ct = default,
        long? expectedRevision = null, Func<bool>? canCommit = null,
        Func<AppSettings, CancellationToken, Task>? validateExtra = null)
    {
        await settingsGate.WaitAsync(ct);
        try
        {
            if (expectedRevision.HasValue && SettingsRevision != expectedRevision || canCommit?.Invoke() == false)
                throw new OperationCanceledException("Настройки изменены во время операции.");
            var old = ConfigRepository.CurrentSettings;
            var next = JsonSettings.Clone(old);
            change(next);
            SettingsValidation.Validate(next);
            if (validateExtra != null) await validateExtra(next, ct);
            await ConfigRepository.UpdateSettingsAsync(current =>
            {
                if (canCommit?.Invoke() == false) throw new OperationCanceledException("Настройки изменены во время операции.");
                foreach (var property in typeof(AppSettings).GetProperties().Where(p => p.CanWrite))
                {
                    if (Json(property.GetValue(old)) == Json(property.GetValue(next))) continue;
                    if (property.Name==nameof(AppSettings.Profiles)) {current.Profiles=ProfileSettingsMerge.Merge(old.Profiles,next.Profiles,current.Profiles);continue;}
                    if (Json(property.GetValue(current)) != Json(property.GetValue(old)))
                        throw new OperationCanceledException("Настройки уже изменены другой операцией.");
                    property.SetValue(current, property.GetValue(next));
                }
                return current;
            }, ct);
            SettingsRevision++;
            if (RoutingChanged(old, next) || ProfileConnectionChanged(old.Profiles.FirstOrDefault(p => p.Id == old.MainProfileId), next.Profiles.FirstOrDefault(p => p.Id == next.MainProfileId)))
                RuntimeCoordinator.RequestReconcile(ReconcileReason.UserChangedSettings);
            projection.Post(() => Theme.Apply(State));
        }
        catch
        {
            await PublishSettingsAsync(ConfigRepository.CurrentSettings);
            throw;
        }
        finally { settingsGate.Release(); }
    }
    public Task UpdateSubscriptionAsync(Guid id,List<Profile> profiles,long expectedRevision,CancellationToken ct,bool approveSecurityChanges=false) => UpdateSettingsAsync(next=>
    {
        next.CopyFrom(SubscriptionMerge.Prepare(next,id,profiles,approveSecurityChanges));
    },ct:ct,expectedRevision:expectedRevision);
    public async Task<string> RefreshSubscriptionAsync(Guid id, CancellationToken ct, bool approveSecurityChanges = false, ImportResult? approvedDocument = null, long? approvedRevision = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, maintenanceLifetime.Token);
        ct = linked.Token;
        await subscriptionRefreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var revision = approvedRevision ?? ConfigRepository.Snapshot.Revision;
            var sub = ConfigRepository.CurrentSettings.Subscriptions.Single(s => s.Id == id);
            var document = approvedDocument ?? ProfileImporter.ParseForImport(await new SubscriptionClient().ReadAsync(sub.Url, sub.AllowInsecureTransport, ct).ConfigureAwait(false), sub.Name);
            string summary = "";
            await UpdateSettingsAsync(next =>
            {
                var merged = SubscriptionMerge.Apply(next, id, document, approveSecurityChanges);
                next.CopyFrom(merged.Settings);
                summary = next.Subscriptions.Single(s => s.Id == id).LastRefreshSummary;
            }, ct:ct, expectedRevision:revision).ConfigureAwait(false);
            WriteLog($"SUBSCRIPTION id={id} {summary}");
            return summary + (document.Errors.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, document.Errors.Take(15)) : "");
        }
        finally { subscriptionRefreshGate.Release(); }
    }
    public async Task CommitFailoverAsync(Guid expectedActiveId,Guid winnerId,long expectedSessionRevision,CancellationToken ct)
    {
        await UpdateSettingsAsync(next=>
        {
            if(!next.AutoSwitch || !State.AutoSwitch) throw new OperationCanceledException("Автопереключение выключено.");
            if(Router.VpnRunning && Router.ActiveProfileId.HasValue && Router.ActiveProfileId.Value != expectedActiveId) throw new OperationCanceledException("Подключение уже изменилось.");
            if(Router.VpnRunning && Router.SessionRevision != expectedSessionRevision) throw new OperationCanceledException("Сессия изменилась.");
            next.MainProfileId=winnerId;
        },ct:ct,canCommit:()=>State.AutoSwitch && !pendingMainProfileId.HasValue);
        UpdateDesiredState(d => d with { SelectedVpnProfileId = winnerId });
        await (RuntimeCoordinator?.ReconcileAsync(ReconcileReason.VpnFailover, ct) ?? Task.CompletedTask);
    }

    public async Task UserRequestedZapretStrategyAsync(string file, bool? enableZapret = null, CancellationToken ct = default)
    {
        var strategyName = Path.GetFileName(file);
        await ConfigRepository.UpdateSettingsAsync(next =>
        {
            next.ZapretStrategy = strategyName;
            next.ApplyBestZapret = false;
            return next;
        }, ct).ConfigureAwait(false);

        if (enableZapret.HasValue)
        {
            UpdateDesiredState(d => d with { ZapretEnabled = enableZapret.Value });
            Store.SaveDesiredState(DesiredState);
        }

        if (RuntimeCoordinator != null)
        {
            await RuntimeCoordinator.ReconcileAsync(ReconcileReason.ZapretConfigurationChanged, ct).ConfigureAwait(false);
        }
    }
    public Task DeleteProfileAsync(Guid id,CancellationToken ct=default) => UpdateSettingsAsync(next=>
    {
        if(Router.VpnRunning && Router.ActiveProfileId==id || Router.OpenVpn.Running && Router.OpenVpn.ActiveProfileId==id) throw new InvalidOperationException("Сначала остановите этот профиль.");
        if (next.Profiles.FirstOrDefault(p => p.Id == id) is { } deleted) SubscriptionMerge.ExcludeProfile(next, deleted);
        next.Profiles.RemoveAll(p=>p.Id==id); SettingsValidation.RepairSelections(next);
    },ct:ct);
    public Task ImportProfilesAsync(IEnumerable<Profile> profiles,Subscription? subscription=null) => UpdateSettingsAsync(next=>
    {
        var incoming=profiles.Select(JsonSettings.Clone).ToArray();
        foreach(var profile in incoming.Where(p=>p.IsOpenVpn)) OpenVpnConfiguration.ValidateNewImport(profile.OpenVpnConfig);
        if(subscription!=null) next.CopyFrom(SubscriptionMerge.Import(next, subscription, incoming));
        else next.Profiles.AddRange(incoming);
        SettingsValidation.RepairSelections(next);
    });
    public async Task ApplyZapretAsync(bool enabled, string? file, CancellationToken ct)
    {
        TestsCancellation.Cancel();
        if (file != null)
            await UserRequestedZapretStrategyAsync(file, enabled, ct);
        else
        {
            UpdateDesiredState(d => d with { ZapretEnabled = enabled });
            await RuntimeCoordinator.ReconcileAsync(ReconcileReason.UserToggledZapret, ct);
        }
    }
    public async Task TestZapretAsync(StrategyResult[] items,IProgress<StrategyResult> progress,CancellationToken ct,Action<string>? statusCallback=null,bool autoSelect=false)
    {
        if(State.YouTube!=ServiceRoute.Zapret && State.Discord!=ServiceRoute.Zapret) throw new InvalidOperationException("Выберите хотя бы один сервис через Zapret.");
        var tested=JsonSettings.Clone(State); tested.ApplyBestZapret=false;
        // Auto Select scans every available strategy, but orders the most promising
        // candidates first. Historical PASS is only a priority hint: every candidate
        // is revalidated on the current network before it can be accepted.
        tested.BestZapretByScenario.TryGetValue(tested.Scenario, out var rememberedBest);

        var candidates = autoSelect
            ? items
                .Select((item, index) => new { Item = item, Index = index })
                .OrderBy(x =>
                {
                    var fileName = Path.GetFileName(x.Item.File);

                    if (!string.IsNullOrWhiteSpace(rememberedBest) &&
                        string.Equals(fileName, rememberedBest, StringComparison.OrdinalIgnoreCase))
                        return 0;

                    if (string.Equals(fileName, State.ZapretStrategy, StringComparison.OrdinalIgnoreCase))
                        return 1;

                    if (x.Item.Passed &&
                        string.Equals(x.Item.Scenario, tested.Scenario, StringComparison.Ordinal))
                        return 2;

                    return 3;
                })
                .ThenBy(x =>
                    x.Item.Passed &&
                    string.Equals(x.Item.Scenario, tested.Scenario, StringComparison.Ordinal)
                        ? x.Item.Delay
                        : int.MaxValue)
                .ThenBy(x => x.Index)
                .Select(x => x.Item)
                .ToArray()
            : items;

        // HTTPS/Gateway targets inside one strategy are executed concurrently.
        // This deadline is only a hard safety ceiling for Auto Select.
        var deadline = autoSelect
            ? TimeSpan.FromSeconds(
                Math.Clamp(
                    20 + candidates.Length * (tested.TestTimeoutSeconds + 5),
                    45,
                    120))
            : (TimeSpan?)null;

        if (autoSelect)
            WriteLog(
                "ZAPRET_AUTO_ORDER " +
                string.Join(",", candidates.Select(x => Path.GetFileName(x.File))));
        var best=await RuntimeCoordinator.RunZapretTestAsync(token => Zapret.TestAsync(tested,candidates,progress,token,statusCallback,deadline,stopAfterFirstAccepted:autoSelect), ct);
        await UpdateSettingsAsync(next=>
        {
            next.ZapretResults=tested.ZapretResults; next.BestZapretByScenario=tested.BestZapretByScenario;
            if(autoSelect && best!=null) next.ZapretStrategy=Path.GetFileName(best.File);
        }, ct: ct);
        if (autoSelect && best != null)
        {
            UpdateDesiredState(d => d with { ZapretEnabled = true });
            await RuntimeCoordinator.ReconcileAsync(ReconcileReason.UserToggledZapret, ct);
            if (!Zapret.ConfirmValidatedStrategy(CommittedSnapshot,best.File))
                throw new IOException("Выбранная стратегия не совпадает с запущенным Zapret. Обход не подтверждён.");
            NotifyState();
        }
        WriteLog(best==null?"Рабочая стратегия для всех HTTPS-проверок не найдена.":"Лучший HTTPS результат: "+best.Name);
    }
    internal void RestoreSmokeDraft()
    {
        if (!App.IsSmoke) throw new InvalidOperationException();
        PublishSettings(committed);
    }
    public void AssertCommittedInvariant()
    {
        if(Json(committed)!=Json(Store.Load()) || Json(committed)!=Json(State)) throw new InvalidOperationException("State, committed и settings.dpapi различаются.");
        if(Router.VpnRunning && Router.ActiveProfileId!=committed.MainProfileId) throw new InvalidOperationException("Runtime использует другой профиль.");
    }
    private async Task PublishSettingsAsync(AppSettings value)
    {
        var copy = JsonSettings.Clone(value);
        await projection.PostAsync(() => PublishSettingsCore(copy));
    }
    private void PublishSettings(AppSettings value)
    {
        var copy = JsonSettings.Clone(value);
        projection.Post(() => PublishSettingsCore(copy));
    }

    private void PublishSettingsCore(AppSettings value)
    {
        try
        {
            value.InterfaceScale = interfaceScaleChanged ? State.InterfaceScale : Store.InterfaceScalePreference.Load(value.InterfaceScale);
            committed.InterfaceScale = value.InterfaceScale;
            var appearance = appearanceChanged ? AppearanceValues.From(State) : Store.AppearancePreference.Load(AppearanceValues.From(value));
            appearance.Apply(value);
            appearance.Apply(committed);
            var results = Profiles.ToDictionary(p => p.Id, p => (p.Host, p.Port, p.Protocol, p.Core, p.OutboundJson, p.Result, p.ResultDetails, p.TrafficResult, p.TrafficDetails));
            publishing = true;
            try
            {
                State.CopyFrom(value);
                foreach (var p in State.Profiles)
                    if (results.TryGetValue(p.Id, out var prior) && p.Host == prior.Host && p.Port == prior.Port &&
                        p.Protocol == prior.Protocol && p.Core == prior.Core && p.OutboundJson == prior.OutboundJson)
                    {
                        p.Result = prior.Result;
                        p.ResultDetails = prior.ResultDetails;
                        p.TrafficResult = prior.TrafficResult;
                        p.TrafficDetails = prior.TrafficDetails;
                    }
                RefreshCore();
            }
            finally
            {
                publishing = false;
            }
            foreach (var name in new[] { nameof(ModeIndex), nameof(YouTubeIndex), nameof(DiscordIndex), nameof(TelegramSocks), nameof(TelegramVpnDefault), nameof(TelegramRouteDescription) })
                OnPropertyChanged(name);
        }
        catch (Exception ex)
        {
            WriteLog("UI_PROJECTION error=" + ex.Message);
        }
    }
    public async Task EditProfileAsync(Profile edited,CancellationToken ct)
    {
        if(Busy)throw new InvalidOperationException("Дождитесь завершения текущей операции.");
        Busy=true;
        try
        {
            await UpdateSettingsAsync(next=>
            {
                if(Router.OpenVpn.Running && Router.OpenVpn.ActiveProfileId==edited.Id) throw new InvalidOperationException("Остановите текущий OpenVPN перед изменением его параметров.");
                var index=next.Profiles.FindIndex(p=>p.Id==edited.Id); if(index<0) throw new InvalidOperationException("Профиль уже удалён.");
                next.Profiles[index]=JsonSettings.Clone(edited);
            },ct:ct,validateExtra:(s,t)=>Router.ValidateProfileConfigurationAsync(edited,s,t));
        }
        finally {Busy=false;}
    }
    public async Task TestAsync(IEnumerable<Profile> profiles, CancellationToken ct)
    {
        var settings=JsonSettings.Clone(State);
        foreach (var p in profiles.ToArray())
        {
            TestStatus = "Тест: " + p.Name; p.Result = "Проверяется…";
            var r = await Router.TestProfileAsync(JsonSettings.Clone(p), settings, ct); p.SetTestResult(r);
            WriteLog(p.Name + ": " + p.Result);
        }
        OnPropertyChanged(nameof(Profiles));
    }
    public async Task TestTrafficAsync(IEnumerable<Profile> profiles, CancellationToken ct)
    {
        var settings = JsonSettings.Clone(State);
        foreach(var p in profiles.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            TestStatus = "Передача данных: " + p.Name; p.TrafficResult = "Проверяется…";
            p.TrafficDetails = "Проверка передачи данных выполняется.";
            var tested = await Router.TestProfileTrafficAsync(JsonSettings.Clone(p), settings, ct);
            p.SetTrafficTestResult(tested);
            WriteLog(p.Name + ": " + p.TrafficResult);
            if(p.Id == Router.ActiveProfileId) await TestActiveTrafficAsync(ct);
        }
    }
    public async Task TestActiveTrafficAsync(CancellationToken ct)
    {
        TestStatus = "Передача данных через текущий TUN…";
        activeTrafficTest = null;
        OnPropertyChanged(nameof(ActiveTrafficSummary));
        activeTrafficTest = await Router.TestActiveTrafficAsync(ct);
        WriteLog(activeTrafficTest.Summary);
        NotifyState();
    }
    public void Dispose()
    {
        maintenance.Dispose();
        maintenanceLifetime.Cancel(); NetworkMonitor.Dispose();
        if(disposed)return; FlushInterfaceScalePreference(); FlushAppearancePreference(); disposed=true; State.PropertyChanged-=StateChanged;
        Store.Diagnostic -= WriteLog;
        HealthMonitor.Dispose();
        TrafficMonitor.Dispose();
        RuntimeCoordinator?.Dispose();
        maintenanceLifetime.Cancel();
        selectionChange.Cancel(); WorkCancellation.Cancel(); TestsCancellation.Cancel();
        try { Zapret.Dispose(); }
        finally { try { Telegram.Dispose(); } finally { try { Router.Dispose(); } finally { Updater.Dispose(); } } }
    }
    public async Task StopForExitAsync()
    {
        WriteLog($"APP_EXIT\n  pid={Environment.ProcessId}\n  reason=normal");
        maintenanceLifetime.Cancel(); NetworkMonitor.Dispose(); HealthMonitor.Stop(); TrafficMonitor.StopSampling();
        await maintenance.StopAsync(); await logPump; await subscriptionPump;
        selectionChange.Cancel();WorkCancellation.Cancel();TestsCancellation.Cancel();
        try {await PendingInterfaceScaleSave;await PendingAppearanceSave;await PendingSelection;await PendingRoutes;await SaveAsync();}
        finally
        {
            FlushDesiredState();
            // Serialize shutdown after any subscription/failover transaction already in flight.
            await settingsGate.WaitAsync();
            try { await RuntimeCoordinator.StopForShutdownAsync(); await Telegram.StopAsync(); }
            finally {settingsGate.Release();}
        }
    }
    public async Task StopComponentsAsync()
    {
        selectionChange.Cancel();
        TestsCancellation.Cancel();
        try { if (Telegram.Running) await Telegram.StopAsync(); } catch { }
        UpdateDesiredState(d => d with { MainVpnEnabled = false, ZapretEnabled = false, OpenVpnEnabled = false });
        await RuntimeCoordinator.ReconcileAsync(ReconcileReason.UserStopAll, CancellationToken.None);
        NotifyState();
    }
}
public sealed class ModuleRow : ObservableObject
{
    public ModuleCheck Check { get; }

    public string Key =>
        Check.Key;

    public string Installed =>
        Check.Installed;

    public string Latest =>
        Check.Latest;

    public bool Available =>
        Check.Available;

    public VersionStatus VersionStatus =>
        Check.VersionStatus;

    public InstallabilityStatus Installability =>
        Check.Installability;

    public bool CanSelectUpdate =>
        Check.InstallableUpdate &&
        !Pinned;

    public string DisplayName =>
        Key switch
        {
            "geoip" => "GeoIP",
            "geosite" => "GeoSite",
            "netcat" => "NetCat",
            "xray" => "Xray",
            "openvpn" => "OpenVPN",
            "wintun" => "Wintun",
            "zapret" => "Zapret",
            "tg-ws-proxy" => "Telegram WS proxy",
            _ => Key
        };

    public string Description =>
        Key switch
        {
            "geoip" =>
                "Геоданные · страны и сети · Loyalsoldier",

            "geosite" =>
                "Геоданные · группы доменов · Loyalsoldier",

            "netcat" =>
                "Программа · перезапуск после обновления",

            "sing-box" =>
                "Маршрутизация · VPN-core",

            "xray" =>
                "VPN-core",

            "zapret" =>
                "Обход DPI",

            "tg-ws-proxy" =>
                "Telegram · WS proxy",

            "openvpn" =>
                "Корпоративный VPN · обновляется отдельно",

            "wintun" =>
                "Сетевой адаптер",

            _ => ""
        };

    public string Glyph =>
        Key switch
        {
            "sing-box" => "S",
            "xray" => "X",
            "zapret" => "Z",
            "tg-ws-proxy" => "T",
            "openvpn" => "O",
            _ => "W"
        };

    public bool Pinned { get; }

    public string PreviousVersion { get; }

    public bool CanRollback =>
        !string.IsNullOrWhiteSpace(
            PreviousVersion);

    public bool SupportsVersionSelection =>
        ModuleUpdater.SupportsVersionSelection(
            Key);

    public bool CanPin =>
        Key != "openvpn";

    public string PinButtonText =>
        Pinned
            ? "Снять закрепление"
            : "Закрепить текущую версию";

    public string RollbackButtonText =>
        CanRollback
            ? $"Откатить на {PreviousVersion}"
            : "Откат недоступен";

    public string Status { get; }

    private bool selected;

    public bool Selected
    {
        get => selected;

        set =>
            SetProperty(
                ref selected,
                value && CanSelectUpdate);
    }

    public ModuleRow(
        ModuleCheck check,
        bool pinned,
        bool checkedAlready = true,
        string previousVersion = "")
    {
        Check = check;
        Pinned = pinned;
        PreviousVersion = previousVersion;

        if(pinned)
        {
            Status =
                $"Версия {Installed} закреплена";
        }
        else if(!checkedAlready)
        {
            Status =
                "Ожидает проверки";
        }
        else
        {
            Status =
                check.Status;
        }

        Selected =
            CanSelectUpdate;
    }
}
