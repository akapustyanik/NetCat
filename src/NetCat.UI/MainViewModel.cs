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
public sealed class MainViewModel : ObservableObject, IDisposable
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
    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<string> Adapters { get; } = [];
    public ObservableCollection<ModuleRow> Modules { get; } = [];
    public string[] ModeNames { get; } = ["По правилам", "Весь трафик"];
    public string[] ServiceNames { get; } = ["Через VPN", "Через Zapret"];
    public Profile? SelectedProfile { get; set; }
    public RoutingRule? SelectedRule { get; set; }
    public StrategyResult? SelectedStrategy { get; set; }
    private bool refreshing, disposed;
    private bool publishing;
    private AppSettings committed = new();
    private readonly SettingsTransaction transaction = new();
    private readonly SemaphoreSlim settingsGate = new(1);
    private static readonly HashSet<string> RoutingProperties = [nameof(AppSettings.Tun),nameof(AppSettings.PhysicalInterface),nameof(AppSettings.Mode),nameof(AppSettings.YouTube),nameof(AppSettings.Discord),nameof(AppSettings.TelegramSocks),nameof(AppSettings.TelegramVpnDefault),nameof(AppSettings.TelegramSocksHost),nameof(AppSettings.TelegramSocksPort),nameof(AppSettings.DirectDns),nameof(AppSettings.OpenVpnDns),nameof(AppSettings.LocalDomains),nameof(AppSettings.OpenVpnDomains),nameof(AppSettings.SocksPort)];
    private void StateChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e)
    {
        if(publishing || refreshing || disposed) return;
        if(e.PropertyName==nameof(AppSettings.OpenVpnProfileId) && Router.OpenVpn.Running && State.OpenVpnProfileId!=Router.OpenVpn.ActiveProfileId)
        { State.OpenVpnProfileId=Router.OpenVpn.ActiveProfileId; OnPropertyChanged(nameof(OpenVpnProfile)); return; }
        if(e.PropertyName is nameof(AppSettings.YouTube) or nameof(AppSettings.Discord)) InvalidateScenarioTests();
        foreach(var name in new[]{nameof(ModeIndex),nameof(YouTubeIndex),nameof(DiscordIndex),nameof(TelegramSocks),nameof(TelegramVpnDefault),nameof(TelegramRouteDescription),nameof(Scenario)}) OnPropertyChanged(name);
        if(e.PropertyName!=null && RoutingProperties.Contains(e.PropertyName)) ScheduleRoutesApply();
    }
    private CancellationTokenSource selectionChange = new();
    private Guid? pendingMainProfileId;
    private long selectionRevision;
    public Task PendingSelection { get; private set; } = Task.CompletedTask;
    public long SettingsRevision { get; private set; }
    public AppSettings CommittedSnapshot => JsonSettings.Clone(committed);
    public Profile? MainProfile
    {
        get => VpnProfiles.FirstOrDefault(p=>p.Id==(pendingMainProfileId ?? State.MainProfileId));
        set
        {
            if(refreshing || value==null || value.Id==(pendingMainProfileId ?? committed.MainProfileId)) return;
            selectionChange.Cancel(); selectionChange.Dispose(); selectionChange=new();
            pendingMainProfileId=value.Id; var revision=++selectionRevision;
            OnPropertyChanged();
            PendingSelection=ApplySelectedProfileAsync(value.Id,revision,selectionChange.Token);
        }
    }
    private async Task ApplySelectedProfileAsync(Guid id,long revision,CancellationToken ct)
    {
        try
        {
            await Task.Delay(180,ct);
            while(Busy && !disposed) await Task.Delay(50,ct);
            ct.ThrowIfCancellationRequested(); if(disposed)return;
            await UpdateSettingsAsync(next=>next.MainProfileId=id,true,true,ct,canCommit:()=>selectionRevision==revision);
            Status="Профиль выбран";
        }
        catch(Exception ex)
        {
            if(revision==selectionRevision) {Status=ex is OperationCanceledException ? "Смена профиля отменена" : "Новый профиль не применён: "+ProcessHost.Redact(ex.Message);WriteLog(Status);}
        }
        finally {if(revision==selectionRevision) {pendingMainProfileId=null; OnPropertyChanged(nameof(MainProfile)); NotifyState();}}
    }
    public bool CanSelectOpenVpn => !Router.OpenVpn.Running;
    public Profile? OpenVpnProfile { get => OpenVpnProfiles.FirstOrDefault(p => p.Id == (Router.OpenVpn.ActiveProfileId ?? State.OpenVpnProfileId)); set { if(refreshing)return; if(Router.OpenVpn.Running && value?.Id!=Router.OpenVpn.ActiveProfileId) {OnPropertyChanged(); return;} State.OpenVpnProfileId = value?.Id; OnPropertyChanged(); } }
    public int ModeIndex { get => (int)State.Mode; set { if(value is < 0 or > 1 || value==(int)State.Mode || refreshing) return; State.Mode = value == 1 ? RoutingMode.Global : RoutingMode.Rules; } }
    public int YouTubeIndex { get => (int)State.YouTube; set { if (value is < 0 or > 1 || value == (int)State.YouTube || refreshing) return; State.YouTube = (ServiceRoute)value; } }
    public int DiscordIndex { get => (int)State.Discord; set { if (value is < 0 or > 1 || value == (int)State.Discord || refreshing) return; State.Discord = (ServiceRoute)value; } }
    private bool applyingScenario;
    private int routesRevision;
    public Task PendingRoutes { get; private set; } = Task.CompletedTask;
    private void ScheduleRoutesApply() { if(refreshing || disposed) return; routesRevision++; if(!applyingScenario) PendingRoutes = ApplyScenarioAsync(); }
    private async Task ApplyScenarioAsync()
    {
        if (applyingScenario) return; applyingScenario = true;
        try
        {
            await Task.Delay(200);
            while (!disposed)
            {
                while ((Busy || TestsBusy || pendingMainProfileId.HasValue) && !disposed) await Task.Delay(100);
                if (disposed) return;
                var revision = routesRevision;
                await RunAsync(ApplyRoutesAsync);
                if (revision == routesRevision) break;
            }
        }
        finally { applyingScenario = false; }
    }
    private void InvalidateScenarioTests()
    {
        if (TestsBusy) TestsCancellation.Cancel();
        foreach (var strategy in Strategies) if (strategy.Scenario != State.Scenario) strategy.RestoreHistory(State);
    }
    public bool TelegramSocks { get => State.TelegramSocks; set { State.TelegramSocks = value; OnPropertyChanged(); OnPropertyChanged(nameof(TelegramVpnDefault)); OnPropertyChanged(nameof(TelegramRouteDescription)); } }
    public bool TelegramVpnDefault
    {
        get => State.TelegramVpnDefault && !State.TelegramSocks;
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
    private string status = "Готов к настройке";
    public string Status { get => status; set => SetProperty(ref status, value); }
    private bool busy;
    public bool Busy { get => busy; set { SetProperty(ref busy, value); OnPropertyChanged(nameof(Idle)); } }
    public bool Idle => !Busy;
    private bool testsBusy, checkingUpdates;
    public bool TestsBusy { get => testsBusy; private set { SetProperty(ref testsBusy,value); OnPropertyChanged(nameof(TestsIdle)); } }
    public bool TestsIdle => !TestsBusy;
    private string testStatus = "Тесты не запущены", updateStatus = "Проверка обновлений ещё не выполнялась";
    public string TestStatus { get => testStatus; set => SetProperty(ref testStatus,value); }
    public string UpdateStatus { get => updateStatus; set => SetProperty(ref updateStatus,value); }
    public bool CheckingUpdates { get => checkingUpdates; set { SetProperty(ref checkingUpdates,value); OnPropertyChanged(nameof(CanCheckUpdates)); } }
    public bool CanCheckUpdates => !CheckingUpdates;
    public bool HasUpdates => Modules.Any(m => m.Available && !m.Pinned);
    public string ProfileDetails => Router.VpnRunning && Router.ActiveProfileId != State.MainProfileId ? "Проверяется новый профиль · сейчас работает " + State.Profiles.FirstOrDefault(p=>p.Id==Router.ActiveProfileId)?.Name : MainProfile == null ? "Импортируйте подписку или профиль" : $"{MainProfile.Protocol.ToUpperInvariant()} · {MainProfile.Core}";
    public string LocalEndpoint => Router.Running ? $"HTTP / SOCKS5 · 127.0.0.1:{Router.ListenPort}" : $"HTTP / SOCKS5 · 127.0.0.1:{State.SocksPort}";
    public bool VpnConnected => Router.VpnRunning;
    public bool TelegramRunning => Telegram.Running;
    public bool ZapretRunning => Zapret.Running;
    public string TelegramStatus => Telegram.Running ? $"Работает · 127.0.0.1:{Telegram.Port}" : "WS proxy выключен";
    public string TelegramButton => Telegram.Running ? "Остановить" : "Включить";
    private string healthStatus = "Проверяется доступ", healthDetails = "Подключите VPN для проверки доступа.";
    private int healthPort;
    public bool Recovering { get; set; }
    public string HealthDetails => !Router.VpnRequested && !Router.Reconfiguring ? "Подключите VPN для проверки доступа." : healthDetails;
    public string VpnStatus => Recovering && Router.VpnRequested ? "Восстанавливается…" : Router.Reconfiguring ? (Router.VpnRequested ? "Подключается…" : "Отключается…") : !Router.VpnRequested ? "VPN отключён" : !Router.VpnRunning ? "Нет доступа · ядро остановлено" : healthStatus;
    public void SetHealth(DelayResult profile, DelayResult? system)
    {
        healthStatus = ConnectionHealth.Status(profile, system, Router.TunActive);
        healthDetails = "Профиль: " + TestFeedback.Summary(profile) + (system == null ? (Router.TunActive ? " · Проверка TUN отложена" : " · Системный TUN выключен") : " · Системный TUN: " + (system.Success ? "доступ подтверждён" : system.Error));
        NotifyState();
    }
    public string VpnButton => Router.VpnRequested ? "Отключить VPN" : "Подключить VPN";
    public string OpenVpnStatus => Router.OpenVpn.Running ? "OpenVPN подключён параллельно" : "OpenVPN отключён";
    public string OpenVpnButton => Router.OpenVpn.Running ? "Отключить OpenVPN" : "Подключить OpenVPN";
    public string ZapretStatus => Zapret.Running ? "Работает: " + Zapret.ActiveStrategy : "Zapret остановлен";
    public string Scenario => $"YouTube → {RussianLabels.Of(State.YouTube)}; Discord → {RussianLabels.Of(State.Discord)}";
    public CancellationTokenSource WorkCancellation { get; private set; } = new();
    public CancellationTokenSource TestsCancellation { get; private set; } = new();
    public MainViewModel(SettingsStore store, AppSettings settings, RouterService? router = null, ZapretService? zapret = null)
    {
        Store = store; State = settings; SettingsMigration.Apply(State);
        Bin = Path.Combine(AppContext.BaseDirectory, "modules");
        if (!Directory.Exists(Bin)) Bin = Path.Combine(AppContext.BaseDirectory, "bin");
        if (!Directory.Exists(Bin)) { var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "bin")); if (Directory.Exists(candidate)) Bin = candidate; }
        Router = router ?? new(Bin, Path.Combine(store.Root, "runtime")); Zapret = zapret ?? new(Bin, Path.Combine(store.Root, "runtime", "zapret")); Updater = new(Bin,()=>Router.VpnRunning ? Router.LatencyPort : 0);
        Telegram = new(Bin,Path.Combine(store.Root,"runtime","telegram")); Telegram.Log += WriteLog;
        Router.Log += WriteLog; Zapret.Log += WriteLog; Router.Changed += NotifyState;
        Rules.CollectionChanged += (_,_) => { if(!refreshing) { State.Rules=Rules.ToList(); ScheduleRoutesApply(); } };
        Refresh(); foreach (var a in PhysicalNetwork.Adapters()) Adapters.Add(a.Name);
        foreach(var key in ModuleUpdater.Keys) Modules.Add(new ModuleRow(new ModuleCheck(key,Updater.InstalledVersion(key),Updater.PreparedRelease(key)),State.PinnedModules.Contains(key),false));
        committed=JsonSettings.Clone(State); State.PropertyChanged+=StateChanged;
    }
    public void Refresh()
    {
        refreshing = true;
        SettingsMigration.Apply(State);
        // Selection IDs must survive collection reset; ComboBox resets SelectedItem while clearing.
        var mainId = State.MainProfileId; var ovpnId = State.OpenVpnProfileId;
        Profiles.Clear(); VpnProfiles.Clear(); OpenVpnProfiles.Clear(); Rules.Clear();
        foreach (var p in State.Profiles) { Profiles.Add(p); if (p.IsOpenVpn) OpenVpnProfiles.Add(p); else VpnProfiles.Add(p); }
        foreach (var r in State.Rules) Rules.Add(r);
        SettingsValidation.RepairSelections(State);
        var previous = Strategies.ToDictionary(s => s.File, StringComparer.OrdinalIgnoreCase);
        Strategies.Clear(); foreach (var s in Zapret.Strategies()) { var item=previous.GetValueOrDefault(s.File,s); if(!TestsBusy) item.RestoreHistory(State); Strategies.Add(item); }
        SelectedStrategy = Strategies.FirstOrDefault(s => Path.GetFileName(s.File) == State.ZapretStrategy) ?? Strategies.FirstOrDefault();
        refreshing = false;
        OnPropertyChanged(nameof(MainProfile)); OnPropertyChanged(nameof(OpenVpnProfile)); OnPropertyChanged(nameof(SelectedStrategy)); NotifyState();
    }
    public void NotifyState()
    {
        if (!Application.Current.Dispatcher.CheckAccess()) { Application.Current.Dispatcher.Invoke(NotifyState); return; }
        if (healthPort != Router.LatencyPort) { healthPort=Router.LatencyPort; healthStatus="Проверяется доступ…"; healthDetails="Проверяю профиль и системный маршрут. Это займёт несколько секунд."; }
        OnPropertyChanged(nameof(CanSelectOpenVpn)); OnPropertyChanged(nameof(OpenVpnProfile));
        foreach (var property in new[] { nameof(HealthDetails), nameof(VpnStatus), nameof(VpnButton), nameof(OpenVpnStatus), nameof(OpenVpnButton), nameof(ZapretStatus), nameof(Scenario), nameof(ProfileDetails), nameof(VpnConnected), nameof(LocalEndpoint), nameof(TelegramStatus), nameof(TelegramButton), nameof(TelegramRunning), nameof(ZapretRunning) }) OnPropertyChanged(property);
        foreach(var strategy in Strategies)
        {
            strategy.IsActive = Zapret.Running && Path.GetFileName(strategy.File) == Zapret.ActiveStrategy;
            strategy.IsPrimary = Path.GetFileName(strategy.File) == State.ZapretStrategy;
        }
    }
    public void WriteLog(string text)
    {
        if (!Application.Current.Dispatcher.CheckAccess()) { Application.Current.Dispatcher.BeginInvoke(() => WriteLog(text)); return; }
        Logs.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + ProcessHost.Redact(text)); while (Logs.Count > 300) Logs.RemoveAt(Logs.Count - 1);
    }
    private (bool Running,bool Requested,bool Tun,int Port,bool OpenVpn,Guid? OpenVpnId,bool Zapret,string Strategy,bool Telegram)? runtimeSnapshot;
    public void PollRuntimeState()
    {
        var next=(Router.Running,Router.VpnRequested,Router.TunActive,Router.LatencyPort,Router.OpenVpn.Running,Router.OpenVpn.ActiveProfileId,Zapret.Running,Zapret.ActiveStrategy,Telegram.Running);
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
        if(TestsBusy) return;
        TestsBusy=true; TestsCancellation.Dispose(); TestsCancellation=new();
        try { await work(TestsCancellation.Token); TestStatus="Проверка завершена"; }
        catch(OperationCanceledException) { TestStatus="Проверка отменена"; foreach(var profile in Profiles.Where(p=>p.Result=="Проверяется…")) profile.SetTestResult(new(false,-1,"Проверка отменена пользователем")); }
        catch(Exception e) { TestStatus=ProcessHost.Redact(e.Message); WriteLog(TestStatus); }
        finally { TestsBusy=false; }
    }
    public async Task CheckUpdatesAsync(CancellationToken ct)
    {
        if(CheckingUpdates) return; CheckingUpdates=true; UpdateStatus="Проверяю обновления компонентов…";
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var checks=await Updater.CheckAllAsync(timeout.Token); Modules.Clear(); foreach(var check in checks) Modules.Add(new ModuleRow(check,State.PinnedModules.Contains(check.Key)));
            var count=Modules.Count(m=>m.Available&&!m.Pinned); var errors=checks.Count(c=>c.Error.Length>0);
            UpdateStatus=count>0 ? $"Доступны обновления: {count}. Выберите компоненты и нажмите «Скачать выбранные»." : errors==0 ? "Все компоненты актуальны" : "Проверка выполнена частично";
            if(errors>0) UpdateStatus+=$" · Не удалось проверить: {errors}";
        }
        catch(OperationCanceledException) { UpdateStatus="Проверка обновлений прервана или истёк таймаут"; }
        finally { CheckingUpdates=false; OnPropertyChanged(nameof(HasUpdates)); }
    }
    private static string Json(object? value) => System.Text.Json.JsonSerializer.Serialize(value,JsonSettings.Options);
    private static bool ProfileConnectionChanged(Profile? a,Profile? b) => a?.Id!=b?.Id || a?.Host!=b?.Host || a?.Port!=b?.Port || a?.Core!=b?.Core || a?.OutboundJson!=b?.OutboundJson || a?.OpenVpnConfig!=b?.OpenVpnConfig || a?.Username!=b?.Username || a?.Password!=b?.Password;
    private bool RoutingChanged(AppSettings old,AppSettings next) => RoutingProperties.Any(name=>!Equals(typeof(AppSettings).GetProperty(name)!.GetValue(old),typeof(AppSettings).GetProperty(name)!.GetValue(next))) || Json(old.Rules)!=Json(next.Rules);
    public Task SetVpnEnabledAsync(bool enabled,CancellationToken ct) => CommitDraftAsync(true,ct,vpnEnabled:enabled);
    public Task SetOpenVpnEnabledAsync(bool enabled,CancellationToken ct) => CommitDraftAsync(true,ct,openVpnEnabled:enabled);
    public Task SetTelegramEnabledAsync(bool enabled,CancellationToken ct) => CommitDraftAsync(false,ct,telegramEnabled:enabled);
    public Task SaveAsync() => CommitDraftAsync(false,CancellationToken.None);
    public Task ApplyRoutesAsync(CancellationToken ct) => CommitDraftAsync(true,ct);
    private Task CommitDraftAsync(bool apply,CancellationToken ct,bool? vpnEnabled=null,bool? openVpnEnabled=null,bool? telegramEnabled=null)
    {
        var desired=JsonSettings.Clone(State); desired.MainProfileId=committed.MainProfileId;
        desired.Rules=Rules.Select(JsonSettings.Clone).ToList();
        desired.TestIntervalSeconds=Math.Max(15,desired.TestIntervalSeconds); desired.TestTimeoutSeconds=Math.Clamp(desired.TestTimeoutSeconds,2,60); desired.FailureThreshold=Math.Clamp(desired.FailureThreshold,2,10);
        var baseline=JsonSettings.Clone(committed);
        var changes=typeof(AppSettings).GetProperties().Where(p=>p.CanWrite && Json(p.GetValue(desired))!=Json(p.GetValue(baseline))).ToArray();
        return UpdateSettingsAsync(next=>
        {
            foreach(var property in changes)
            {
                // Rebase unrelated changes; a stale draft must not overwrite a newer change to the same field.
                if(Json(property.GetValue(next))!=Json(property.GetValue(baseline)) && Json(property.GetValue(next))!=Json(property.GetValue(desired))) throw new OperationCanceledException("Настройки уже изменены другой операцией. Повторите изменение.");
                property.SetValue(next,property.GetValue(desired));
            }
        },apply,ct:ct,vpnEnabled:vpnEnabled,openVpnEnabled:openVpnEnabled,telegramEnabled:telegramEnabled);
    }
    public async Task UpdateSettingsAsync(Action<AppSettings> change,bool applyRuntime=false,bool preflight=false,CancellationToken ct=default,
        long? expectedRevision=null,Func<bool>? canCommit=null,bool? zapretEnabled=null,string? runtimeStrategy=null,
        Func<AppSettings,CancellationToken,Task>? validateExtra=null,Func<AppSettings,CancellationToken,Task>? beforeRuntime=null,bool? vpnEnabled=null,bool? openVpnEnabled=null,bool? telegramEnabled=null,Func<bool?>? testZapretIntent=null)
    {
        await settingsGate.WaitAsync(ct);
        var draftAtStart=JsonSettings.Clone(State); var old=JsonSettings.Clone(committed);
        var affected=new HashSet<string>();
        bool vpn=Router.VpnRequested, wasZapret=Zapret.Running, wasOpenVpn=Router.OpenVpn.Running, wasTelegram=Telegram.Running; var oldStrategy=Zapret.ActiveStrategy;
        bool applyRouting=applyRuntime, telegramTouched=false;
        async Task Apply(AppSettings value,CancellationToken token,bool rollback)
        {
            if(!rollback && beforeRuntime!=null) await beforeRuntime(value,token);
            bool tg=rollback?wasTelegram:telegramEnabled ?? wasTelegram;
            if(Telegram.Running && (!tg || Telegram.Port!=value.TelegramWsPort || rollback && telegramTouched || !rollback && old.TelegramWsSecret!=value.TelegramWsSecret)) {telegramTouched=true;await Telegram.StopAsync();}
            if(tg && !Telegram.Running) {telegramTouched=true;await Telegram.StartAsync(value,token);}
            if(!applyRouting)return;
            bool enabled=rollback?wasZapret:testZapretIntent?.Invoke() ?? zapretEnabled ?? wasZapret;
            var strategy=rollback?oldStrategy:runtimeStrategy ?? (value.ZapretStrategy.Length>0?value.ZapretStrategy:oldStrategy);
            if(enabled)
            {
                if(!Zapret.Running || Zapret.ActiveScenario!=value.Scenario || Zapret.ActiveStrategy!=Path.GetFileName(strategy))
                    await Zapret.ApplyAsync(value,Path.Combine(Bin,"zapret",strategy),token);
            }
            else if(Zapret.Running) await Zapret.StopAsync();
            Router.ZapretAvailable=Zapret.Running;
            bool ovpn=rollback?wasOpenVpn:openVpnEnabled ?? wasOpenVpn;
            bool rebuilt=Router.OpenVpn.Running!=ovpn;
            if(rebuilt)await Router.SetOpenVpnAsync(value,ovpn,token);
            bool enabledVpn=rollback?vpn:vpnEnabled ?? vpn;
            if(!rollback && preflight && enabledVpn && Router.VpnRequested) await Router.SwitchProfileAsync(value,token,canCommit:canCommit);
            else if(!rebuilt || Router.VpnRequested!=enabledVpn) await Router.SetVpnAsync(value,enabledVpn,token);
        }
        async Task Stop()
        {
            try {await Telegram.StopAsync();} finally {try {await Zapret.StopAsync();} finally {Router.ZapretAvailable=false;await Router.StopAllAsync();}}
        }
        try
        {
            if(expectedRevision.HasValue && SettingsRevision!=expectedRevision || canCommit?.Invoke()==false) throw new OperationCanceledException("Настройки изменены во время операции.");
            var next=JsonSettings.Clone(committed); change(next);
            foreach(var property in typeof(AppSettings).GetProperties().Where(p=>p.CanWrite && Json(p.GetValue(old))!=Json(p.GetValue(next)))) affected.Add(property.Name);
            var activeChanged=Router.VpnRequested && ProfileConnectionChanged(old.Profiles.FirstOrDefault(p=>p.Id==old.MainProfileId),next.Profiles.FirstOrDefault(p=>p.Id==next.MainProfileId));
            if(Router.OpenVpn.Running && ProfileConnectionChanged(old.Profiles.FirstOrDefault(p=>p.Id==Router.OpenVpn.ActiveProfileId),next.Profiles.FirstOrDefault(p=>p.Id==Router.OpenVpn.ActiveProfileId))) throw new InvalidOperationException("Остановите OpenVPN перед изменением его активного профиля.");
            applyRouting=applyRuntime || RoutingChanged(old,next) || activeChanged || zapretEnabled.HasValue || vpnEnabled.HasValue || openVpnEnabled.HasValue;
            applyRuntime=applyRouting || telegramEnabled.HasValue || wasTelegram && (old.TelegramWsPort!=next.TelegramWsPort || old.TelegramWsSecret!=next.TelegramWsSecret);
            var result=await transaction.ExecuteAsync(old,next,async (s,t)=>{SettingsValidation.Validate(s);if(validateExtra!=null)await validateExtra(s,t);},
                applyRuntime?(s,t)=>Apply(s,t,false):null,Store.SaveAsync,(s,t)=>Apply(s,t,true),Stop,ct,canCommit);
            committed=JsonSettings.Clone(result); SettingsRevision++;
            foreach(var property in typeof(AppSettings).GetProperties().Where(p=>p.CanWrite && Json(p.GetValue(old))!=Json(p.GetValue(result)))) affected.Add(property.Name);
            PublishMerged(result,draftAtStart,affected); Theme.Apply(result.BaseColor,result.AccentColor);
        }
        catch {PublishMerged(old,draftAtStart,affected);throw;}
        finally {settingsGate.Release();NotifyState();}
    }
    public Task UpdateSubscriptionAsync(Guid id,List<Profile> profiles,long expectedRevision,CancellationToken ct) => UpdateSettingsAsync(next=>
    {
        next.CopyFrom(SubscriptionMerge.Prepare(next,id,profiles));
    },preflight:true,ct:ct,expectedRevision:expectedRevision);
    public Task CommitFailoverAsync(Guid expectedActiveId,Guid winnerId,long expectedSessionRevision,CancellationToken ct) => UpdateSettingsAsync(next=>
    {
        if(!next.AutoSwitch || !State.AutoSwitch || !Router.VpnRequested || Router.ActiveProfileId!=expectedActiveId || Router.SessionRevision!=expectedSessionRevision) throw new OperationCanceledException("Подключение уже изменилось.");
        next.MainProfileId=winnerId;
    },true,ct:ct,canCommit:()=>State.AutoSwitch && !pendingMainProfileId.HasValue);
    public Task DeleteProfileAsync(Guid id,CancellationToken ct=default) => UpdateSettingsAsync(next=>
    {
        if(Router.VpnRunning && Router.ActiveProfileId==id || Router.OpenVpn.Running && Router.OpenVpn.ActiveProfileId==id) throw new InvalidOperationException("Сначала остановите этот профиль.");
        next.Profiles.RemoveAll(p=>p.Id==id); SettingsValidation.RepairSelections(next);
    },ct:ct);
    public Task ImportProfilesAsync(IEnumerable<Profile> profiles,Subscription? subscription=null) => UpdateSettingsAsync(next=>
    {
        if(subscription!=null) next.Subscriptions.Add(JsonSettings.Clone(subscription));
        next.Profiles.AddRange(profiles.Select(JsonSettings.Clone));SettingsValidation.RepairSelections(next);
    });
    public Task ApplyZapretAsync(bool enabled,string? file,CancellationToken ct)
    {
        TestsCancellation.Cancel();
        return UpdateSettingsAsync(next=>
        {
        if(enabled && next.ZapretStrategy.Length==0 && file!=null) next.ZapretStrategy=Path.GetFileName(file);
        },true,ct:ct,zapretEnabled:enabled,runtimeStrategy:file);
    }
    public Task TestZapretAsync(StrategyResult[] items,IProgress<StrategyResult> progress,CancellationToken ct)
    {
        bool? enableBest=null;
        return UpdateSettingsAsync(_=>{},true,ct:ct,testZapretIntent:()=>enableBest,beforeRuntime:async (next,token)=>
        {
        if(next.YouTube!=ServiceRoute.Zapret && next.Discord!=ServiceRoute.Zapret) throw new InvalidOperationException("Выберите хотя бы один сервис через Zapret.");
        var tested=JsonSettings.Clone(next);tested.ApplyBestZapret=false;
        var best=await Zapret.TestAsync(tested,items,progress,token);
        next.ZapretResults=tested.ZapretResults; next.BestZapretByScenario=tested.BestZapretByScenario;
        if(next.ApplyBestZapret && best!=null) {next.ZapretStrategy=Path.GetFileName(best.File);enableBest=true;}
        WriteLog(best==null?"Рабочая стратегия для всех HTTPS-проверок не найдена.":"Лучший HTTPS результат: "+best.Name);
        });
    }
    public void AssertCommittedInvariant()
    {
        if(Json(committed)!=Json(Store.Load()) || Json(committed)!=Json(State)) throw new InvalidOperationException("State, committed и settings.dpapi различаются.");
        if(Router.VpnRunning && Router.ActiveProfileId!=committed.MainProfileId) throw new InvalidOperationException("Runtime использует другой профиль.");
    }
    private void PublishMerged(AppSettings result,AppSettings draftAtStart,ISet<string> affected)
    {
        var draft=JsonSettings.Clone(State);
        foreach(var property in typeof(AppSettings).GetProperties().Where(p=>p.CanWrite && affected.Contains(p.Name)))
            if(System.Text.Json.JsonSerializer.Serialize(property.GetValue(draft),JsonSettings.Options)==System.Text.Json.JsonSerializer.Serialize(property.GetValue(draftAtStart),JsonSettings.Options))
                property.SetValue(draft,property.GetValue(result));
        PublishSettings(draft);
    }
    private void PublishSettings(AppSettings value)
    {
        var results=Profiles.ToDictionary(p=>p.Id,p=>(p.OutboundJson,p.Result,p.ResultDetails));
        publishing=true;
        try {State.CopyFrom(value); foreach(var p in State.Profiles) if(results.TryGetValue(p.Id,out var prior) && p.OutboundJson==prior.OutboundJson) {p.Result=prior.Result;p.ResultDetails=prior.ResultDetails;} Refresh();}
        finally {publishing=false;}
        foreach(var name in new[]{nameof(ModeIndex),nameof(YouTubeIndex),nameof(DiscordIndex),nameof(TelegramSocks),nameof(TelegramVpnDefault),nameof(TelegramRouteDescription)}) OnPropertyChanged(name);
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
            },preflight:true,ct:ct,validateExtra:(s,t)=>Router.ValidateProfileConfigurationAsync(edited,s,t));
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
    public void Dispose()
    {
        if(disposed)return; disposed=true; State.PropertyChanged-=StateChanged;
        selectionChange.Cancel(); WorkCancellation.Cancel(); TestsCancellation.Cancel();
        try { Zapret.Dispose(); }
        finally { try { Telegram.Dispose(); } finally { try { Router.Dispose(); } finally { Updater.Dispose(); } } }
    }
    public async Task StopForExitAsync()
    {
        selectionChange.Cancel();WorkCancellation.Cancel();TestsCancellation.Cancel();
        try {await PendingSelection;await PendingRoutes;await SaveAsync();}
        finally
        {
            // Serialize shutdown after any subscription/failover transaction already in flight.
            await settingsGate.WaitAsync();
            try {try {await Zapret.StopAsync();} finally {try {await Telegram.StopAsync();} finally {await Router.StopAllAsync();}}}
            finally {settingsGate.Release();}
        }
    }
    public async Task StopComponentsAsync()
    {
        selectionChange.Cancel();TestsCancellation.Cancel();
        await settingsGate.WaitAsync();
        try {try {await Zapret.StopAsync();} finally {try {await Telegram.StopAsync();} finally {await Router.StopAllAsync();}}}
        finally {settingsGate.Release();NotifyState();}
    }
}
public sealed class ModuleRow : ObservableObject
{
    public ModuleCheck Check { get; }
    public string Key => Check.Key;
    public string Installed => Check.Installed;
    public string Latest => Check.Latest;
    public bool Available => Check.Available;
    public bool CanSelectUpdate => Available && !Pinned;
    public string DisplayName => Key switch { "geoip" => "GeoIP", "geosite" => "GeoSite", "netcat" => "NetCat", "xray" => "Xray", "openvpn" => "OpenVPN", "wintun" => "Wintun", "zapret" => "Zapret", _ => Key };
    public string Description => Key switch { "geoip" => "Геоданные · страны и сети · Loyalsoldier", "geosite" => "Геоданные · группы доменов · Loyalsoldier", "netcat" => "Программа · перезапуск после обновления", "sing-box" => "Маршрутизация", "xray" => "VPN-ядро", "zapret" => "Обход DPI", "tg-ws-proxy" => "Telegram", "openvpn" => "Корпоративный VPN", "wintun" => "Сетевой адаптер", _ => "" };
    public string Glyph => Key switch { "sing-box" => "S", "xray" => "X", "zapret" => "Z", "tg-ws-proxy" => "T", "openvpn" => "O", _ => "W" };
    public bool Pinned { get; }
    public string Status { get; }
    private bool selected;
    public bool Selected { get=>selected; set=>SetProperty(ref selected,value); }
    public ModuleRow(ModuleCheck check,bool pinned,bool checkedAlready=true) { Check=check; Pinned=pinned; Status=pinned?"Версия закреплена":!checkedAlready?"Ожидает проверки":check.Status; Selected=check.Available&&!pinned; }
}
