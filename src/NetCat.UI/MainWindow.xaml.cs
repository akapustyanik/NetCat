using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using NetCat.Updater;

namespace NetCat.UI;
public partial class MainWindow : Window
{
    public MainViewModel VM { get; }
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private TelegramService telegram => VM.Telegram;
    private TrayIntegration? tray;
    private bool exiting, timerBusy;
    private DateTime nextTest = DateTime.Now, nextSubscriptions = DateTime.Now.AddMinutes(1);
    private readonly FailoverPolicy failover = new();
    private readonly CancellationTokenSource lifetime = new();
    private DateTime nextModuleCheck = DateTime.Now.AddHours(6);
    private long previousReceive, previousSend;
    private NetworkInterface? trafficAdapter;
    private DateTime nextAdapterRefresh;
    private long trafficRevision=-1, previousTrafficStamp;
    private int adapterEnumerations, backgroundTicks;
    private bool pingBusy;
    private DateTime nextPing = DateTime.MinValue;
    private int measuredLatencyPort;
    private readonly SystemTunnelHealth tunnelHealth = new();
    private readonly Queue<double> traffic = new();
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent(); VM = vm; DataContext = vm;
        VM.Router.Changed += () => failover.ObserveSession(VM.Router.SessionRevision);
        WindowFrame.Apply(this);
        SourceInitialized += (_, _) => tray = new TrayIntegration(this, () => _ = ExitAsync(), () => new TrayCommand[] {
            new("VPN", VM.Router.VpnRequested, VM.Idle, () => Vpn_Click(this,new RoutedEventArgs())),
            new("OpenVPN", VM.Router.OpenVpn.Running, VM.Idle && (VM.OpenVpnProfile != null || VM.Router.OpenVpn.Running), () => OpenVpn_Click(this,new RoutedEventArgs())),
            new("Zapret", VM.Zapret.Running, VM.Idle, () => ToggleZapret_Click(this,new RoutedEventArgs())),
            new("Telegram · WS proxy", telegram.Running, VM.Idle, () => StartTelegram_Click(this,new RoutedEventArgs()))
        });
        Closing += WindowClosing; timer.Tick += TimerTick; timer.Start();
        Loaded += async (_,_) =>
        {
            if (App.IsSmoke) return;
            await RefreshAutostartAsync();
            if (VM.State.ProfileFormatVersion < 1)
            {
                bool repaired = true;
                foreach (var subscription in VM.State.Subscriptions.ToArray())
                {
                    try { await RefreshSubscriptionAsync(subscription, lifetime.Token); }
                    catch (Exception ex) { repaired = false; VM.WriteLog("Восстановление параметров подписки: " + ProcessHost.Redact(ex.Message)); }
                }
                if (repaired) { VM.State.ProfileFormatVersion = 1; await VM.SaveAsync(); }
            }
            if (VM.State.CheckModuleUpdates) await VM.CheckUpdatesAsync(lifetime.Token);
        };
    }
    private async void Vpn_Click(object sender, RoutedEventArgs e) => await VM.RunAsync(ct=>VM.SetVpnEnabledAsync(!VM.Router.VpnRequested,ct));
    private async void OpenVpn_Click(object sender, RoutedEventArgs e) => await VM.RunAsync(ct=>VM.SetOpenVpnEnabledAsync(!VM.Router.OpenVpn.Running,ct));
    private async void Save_Click(object sender, RoutedEventArgs e) => await VM.RunAsync(_ => VM.SaveAsync());
    private async void Apply_Click(object sender, RoutedEventArgs e) => await VM.RunAsync(VM.ApplyRoutesAsync);
    private void Cancel_Click(object sender, RoutedEventArgs e) { VM.WorkCancellation.Cancel(); VM.TestsCancellation.Cancel(); }
    private void CancelTests_Click(object sender, RoutedEventArgs e) => VM.TestsCancellation.Cancel();
    private async void StopAll_Click(object sender, RoutedEventArgs e) => await VM.RunAsync(_=>VM.StopComponentsAsync());
    private void ImportOvpn_Click(object sender, RoutedEventArgs e) => ImportFiles(true);
    private void ImportFiles(bool ovpn = false)
    {
        var open = new OpenFileDialog { Filter = ovpn ? "OpenVPN|*.ovpn" : "Конфигурации|*.ovpn;*.json;*.txt;*.conf|Все файлы|*.*", Multiselect = true };
        if (open.ShowDialog(this) != true) return;
        _ = VM.RunAsync(async _ =>
        {
            var profiles = new List<Profile>(); var errors = new List<string>();
            foreach (var f in open.FileNames)
            {
                if (new FileInfo(f).Length > 8 * 1024 * 1024) throw new InvalidDataException("Конфигурация слишком большая.");
                var raw = f.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase) ? OpenVpnConfiguration.ReadWithCertificates(f) : await File.ReadAllTextAsync(f);
                var result = ProfileImporter.Parse(raw, System.IO.Path.GetFileNameWithoutExtension(f)); profiles.AddRange(result.Profiles); errors.AddRange(result.Errors);
            }
            if (profiles.Count == 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
            if (PreviewImport(profiles, errors)) await VM.ImportProfilesAsync(profiles);
        });
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new EditorDialog(this, "Импорт профилей");
        var name = dialog.Text("Название группы / подписки", "Моя подписка");
        var text = dialog.Text("URL подписки, ссылки или sing-box JSON", "", true);
        var subscribe = dialog.Check("Сохранить URL как обновляемую подписку", true);
        var file = new Button { Content = "Выбрать файлы (.ovpn, .json, .txt)", HorizontalAlignment = HorizontalAlignment.Left }; dialog.Insert(file);
        file.Click += (_, _) => { dialog.Close(); ImportFiles(); };
        dialog.Note("После чтения откроется список найденных профилей. Адрес подписки хранится зашифрованно для текущего пользователя Windows.");
        dialog.Accept.Content = "Прочитать и проверить";
        dialog.OnAccept = async () =>
        {
            var raw = text.Text.Trim(); Subscription? subscription = null;
            if (Uri.TryCreate(raw, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
            {
                var source = raw; raw = await ReadSubscriptionAsync(source, CancellationToken.None);
                if (subscribe.IsChecked == true) subscription = new Subscription { Name = name.Text, Url = source, UpdatedAt = DateTimeOffset.Now };
            }
            var result = ProfileImporter.Parse(raw, name.Text);
            if (result.Profiles.Count == 0) throw new InvalidDataException(string.Join(Environment.NewLine, result.Errors.DefaultIfEmpty("Профили не найдены.")));
            if (!PreviewImport(result.Profiles, result.Errors, dialog)) return false;
            if (subscription != null) foreach (var p in result.Profiles) p.SubscriptionId = subscription.Id;
            await VM.ImportProfilesAsync(result.Profiles,subscription); return true;
        };
        dialog.ShowDialog();
    }
    private bool PreviewImport(List<Profile> profiles, List<string> errors, Window? owner = null)
    {
        var dialog = new EditorDialog(owner ?? this, "Предпросмотр импорта"); dialog.Note($"Найдено профилей: {profiles.Count}. Ошибок: {errors.Count}.");
        var choices = profiles.Select(p => (p, box: dialog.Check(p.ToString(), true))).ToArray();
        foreach (var e in errors.Take(15)) dialog.Note(e);
        dialog.Accept.Content = "Импортировать выбранные";
        dialog.OnAccept = () => { profiles.RemoveAll(p => choices.First(c => c.p == p).box.IsChecked != true); if (profiles.Count == 0) throw new InvalidOperationException("Выберите хотя бы один профиль."); return Task.FromResult(true); };
        return dialog.ShowDialog() == true;
    }
    private static async Task<string> ReadSubscriptionAsync(string url, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) }; client.DefaultRequestHeaders.UserAgent.ParseAdd("NetCat/0.5.0");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new InvalidDataException("Подписка больше 8 МБ.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream(); var chunk = new byte[16384]; int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0) { if (buffer.Length + read > 8 * 1024 * 1024) throw new InvalidDataException("Подписка больше 8 МБ."); buffer.Write(chunk, 0, read); }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
    private void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedProfile is not { } original) { VM.Status = "Выберите профиль."; return; }
        var copy = JsonSettings.Clone(original); var dialog = new EditorDialog(this, "Настройка профиля");
        var name = dialog.Text("Название", copy.Name); var host = dialog.Text("Сервер", copy.Host); var port = dialog.Text("Порт", copy.Port.ToString());
        var core = dialog.Choice("Ядро", copy.IsOpenVpn ? new[] { "OpenVPN" } : copy.Protocol == "trojan" ? new[] { "Xray" } : new[] { "sing-box", "Xray" }, copy.Core);
        var config = dialog.Text(copy.IsOpenVpn ? "Конфигурация OpenVPN" : "Конфигурация outbound (JSON)", copy.IsOpenVpn ? copy.OpenVpnConfig : copy.OutboundJson, true);
        TextBox? user = null; PasswordBox? password = null;
        if (copy.IsOpenVpn) { user = dialog.Text("Логин, если сервер требует", copy.Username); dialog.Insert(new TextBlock { Text = "Пароль (пусто — сохранить прежний)" }); password = new PasswordBox(); dialog.Insert(password); }
        var candidate = dialog.Check("Участвовать в автосмене основного VPN", copy.Candidate && !copy.IsOpenVpn); candidate.IsEnabled = !copy.IsOpenVpn;
        dialog.Note("Активный VPN-профиль сначала проверяется отдельно. Изменения сохраняются только после успешного переключения. Работающий OpenVPN перед редактированием нужно остановить.");
        dialog.OnAccept = async () =>
        {
            if (!int.TryParse(port.Text, out var p) || p is < 1 or > 65535) throw new FormatException("Порт должен быть от 1 до 65535.");
            if (string.IsNullOrWhiteSpace(name.Text)) throw new FormatException("Введите название.");
            if (copy.IsOpenVpn) OpenVpnConfiguration.Validate(config.Text); else if (JsonNode.Parse(config.Text) is not JsonObject) throw new FormatException("Нужен объект JSON.");
            copy.Name = name.Text.Trim(); copy.Host = host.Text.Trim(); copy.Port = p; copy.Core = (string)core.SelectedItem; copy.Candidate = candidate.IsChecked == true;
            if (copy.IsOpenVpn) { copy.OpenVpnConfig = config.Text; copy.Username = user!.Text; if (password!.Password.Length > 0) copy.Password = password.Password; } else copy.OutboundJson = config.Text;
            SettingsMigration.NormalizeCore(copy);
            await VM.EditProfileAsync(copy,lifetime.Token);
            return true;
        };
        dialog.ShowDialog();
    }
    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedProfile is not { } p) return;
        await VM.RunAsync(ct=>VM.DeleteProfileAsync(p.Id,ct));
    }
    private void ProfileDetails_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Profile profile) return;
        ShowTestDetails(profile.Name,profile.ResultDetails);
    }
    private void ShowTestDetails(string title,string details)
    {
        var d=new EditorDialog(this,title); var text=d.Text("Результат проверки",string.Join("\n",details.Split('\n').Select(ProcessHost.Redact)),true);
        text.IsReadOnly=true; text.TextWrapping=TextWrapping.Wrap; text.MinHeight=300; d.Accept.Content="Закрыть"; d.ShowDialog();
    }
    private void ZapretDetails_Click(object sender,RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StrategyResult strategy) return;
        ShowTestDetails(strategy.Name,strategy.Details);
    }
    private async void TestProfile_Click(object sender, RoutedEventArgs e) { if (VM.SelectedProfile is { } p) await VM.RunTestsAsync(ct => VM.TestAsync([p], ct)); }
    private async void TestAll_Click(object sender, RoutedEventArgs e) => await VM.RunTestsAsync(ct => VM.TestAsync(VM.Profiles.Where(p => !p.IsOpenVpn).ToArray(), ct));
    private void Subscriptions_Click(object sender, RoutedEventArgs e)
    {
        if (VM.State.Subscriptions.Count == 0) { VM.Status = "Сначала импортируйте URL подписки."; return; }
        var d = new EditorDialog(this, "Подписки"); var choice = d.Choice("Подписка", VM.State.Subscriptions, VM.State.Subscriptions[0]); var interval = d.Text("Интервал обновления, часов (0 — вручную)", VM.State.Subscriptions[0].UpdateHours.ToString());
        choice.SelectionChanged += (_, _) => interval.Text = ((Subscription)choice.SelectedItem).UpdateHours.ToString();
        var update = new Button { Content = "Обновить выбранную сейчас" }; d.Insert(update);
        update.Click += async (_, _) => { if(VM.Busy) { d.Error.Text="Дождитесь текущей операции."; return; } try { VM.Busy=true; update.IsEnabled = false; await RefreshSubscriptionAsync((Subscription)choice.SelectedItem, CancellationToken.None); d.Error.Text = "Подписка обновлена."; } catch (Exception ex) { d.Error.Text = ProcessHost.Redact(ex.Message); } finally { VM.Busy=false; update.IsEnabled = true; } };
        d.Note("URL не показывается. Локальные имена и участие в автосмене сохраняются при совпадении сервера, транспорта и учётных данных. Рабочий профиль не удаляется автоматически.");
        d.OnAccept = async () => { var id=((Subscription)choice.SelectedItem).Id;var hours=int.Parse(interval.Text); await VM.UpdateSettingsAsync(next=>next.Subscriptions.Single(p=>p.Id==id).UpdateHours=hours); return true; }; d.ShowDialog();
    }
    private async Task RefreshSubscriptionAsync(Subscription sub, CancellationToken ct)
    {
        var revision=VM.SettingsRevision;
        var result=ProfileImporter.Parse(await ReadSubscriptionAsync(sub.Url,ct),sub.Name);
        if(result.Profiles.Count==0 || result.Errors.Count>0) throw new InvalidDataException("Подписка содержит ошибки; прежние профили сохранены.");
        await VM.UpdateSubscriptionAsync(sub.Id,result.Profiles,revision,ct);
        VM.WriteLog("Обновлена подписка: "+sub.Name);
    }
    private void AddRule_Click(object sender,RoutedEventArgs e) => EditRule(null);
    private async void StandardPreset_Click(object sender, RoutedEventArgs e) => await VM.RunAsync(ct =>
    {
        var missing = RoutingPreset.MissingStandardRules(VM.Rules);
        if (missing.Count == 0) { VM.Status = "Стандартный пресет уже добавлен"; return Task.CompletedTask; }
        var database = GeodataCache.Shared.Get(Geodata.FilePath(VM.Bin, RuleKind.GeoSite), false);
        foreach (var rule in missing.Where(r => r.Kind == RuleKind.GeoSite)) _ = database.Match(rule.Value);
        foreach (var rule in missing) VM.Rules.Add(rule);
        VM.Status = $"Добавлено правил: {missing.Count}. Реклама блокируется; RU-сайты идут напрямую. Ваши прежние правила выше в списке.";
        return Task.CompletedTask;
    });
    private void EditRule_Click(object sender, RoutedEventArgs e) { if (VM.SelectedRule is { } r) EditRule(r); }
    private void EditRule(RoutingRule? original)
    {
        var r = original == null ? new RoutingRule() : JsonSettings.Clone(original);
        var d=RuleEditor.Create(this,r,saved=>
        {
            if(original==null) VM.Rules.Add(saved); else VM.Rules[VM.Rules.IndexOf(original)]=saved;
            VM.Status="Правило изменено. Применяю маршрутизацию…";
        });
        d.ShowDialog();
    }
    private void DeleteRule_Click(object sender, RoutedEventArgs e) { if (VM.SelectedRule is { } r) VM.Rules.Remove(r); }
    private void UpRule_Click(object sender, RoutedEventArgs e) => MoveRule(-1);
    private void DownRule_Click(object sender, RoutedEventArgs e) => MoveRule(1);
    private void MoveRule(int shift) { if (VM.SelectedRule is not { } r) return; var index = VM.Rules.IndexOf(r); if (index + shift >= 0 && index + shift < VM.Rules.Count) VM.Rules.Move(index, index + shift); }
    private void ExeRule_Click(object sender, RoutedEventArgs e) { var open = new OpenFileDialog { Filter = "Приложения|*.exe", Multiselect = true }; if (open.ShowDialog(this) == true) foreach (var f in open.FileNames) VM.Rules.Add(new RoutingRule { Kind = RuleKind.ExecutablePath, Value = f }); }
    private void RunningRule_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ProcessPickerWindow(this);
        if (picker.ShowDialog() == true) foreach(var app in picker.SelectedApps) VM.Rules.Add(new RoutingRule { Kind=RuleKind.ExecutablePath,Value=app.Path,Target=picker.Target });
    }
    private void DomainList_Click(object sender, RoutedEventArgs e)
    {
        var d = new EditorDialog(this, "Список доменов"); var text = d.Text("Один домен / URL на строку", "", true); var target = d.Choice("Маршрут", Enum.GetValues<RouteTarget>(), RouteTarget.Direct); var load = new Button { Content = "Прочитать domains.lst / txt" }; d.Insert(load);
        load.Click += (_, _) => { var f = new OpenFileDialog { Filter = "Списки|*.lst;*.txt|Все файлы|*.*" }; if (f.ShowDialog(d) == true) text.Text = File.ReadAllText(f.FileName); };
        d.Note("Можно добавить категории: geosite:youtube, geosite:category-ru, geoip:ru.");
        d.OnAccept = () =>
        {
            var rules = text.Text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(v => new RoutingRule { Kind = RuleKind.Domain, Value = v, Target = (RouteTarget)target.SelectedItem }).ToArray();
            var databases = new Dictionary<RuleKind, Geodata>();
            foreach (var rule in rules)
            {
                RuleValidation.Validate(rule);
                if (rule.Kind is RuleKind.GeoSite or RuleKind.GeoIp)
                {
                    if (!databases.TryGetValue(rule.Kind, out var database)) databases[rule.Kind] = database = GeodataCache.Shared.Get(Geodata.FilePath(VM.Bin, rule.Kind), rule.Kind == RuleKind.GeoIp);
                    _ = database.Match(rule.Value);
                }
            }
            foreach (var rule in rules) if (!VM.Rules.Any(r => r.Kind == rule.Kind && r.Value == rule.Value)) VM.Rules.Add(rule);
            return Task.FromResult(true);
        }; d.ShowDialog();
    }
    private async void PreviewConfig_Click(object sender, RoutedEventArgs e) => await VM.RunAsync(_ =>
    {
        VM.State.Rules = VM.Rules.ToList(); var json = SingBoxConfig.Build(VM.State, PhysicalNetwork.Capture(VM.State.PhysicalInterface), VM.MainProfile, VM.Router.OpenVpn.Link, VM.State.Tun, geodataDirectory: VM.Bin).ToJsonString(JsonSettings.Options);
        var d = new EditorDialog(this, "Предпросмотр конфигурации", 850); d.Note("Конфигурация содержит параметры профиля. Она не записывается в репозиторий и не отправляется в журнал."); var box = d.Text("sing-box JSON", json, true); box.IsReadOnly = true; box.MinHeight = 350; d.Accept.Content = "Закрыть"; d.ShowDialog(); return Task.CompletedTask;
    });
    private void ExplainRoute_Click(object sender, RoutedEventArgs e)
    {
        var d = new EditorDialog(this, "Проверить правило"); var host = d.Text("Домен / IP", "youtube.com"); var app = d.Text("Приложение / полный путь", ""); var network = d.Choice("Транспорт", new[] { "tcp", "udp" }, "tcp"); var port = d.Text("Порт", "443"); d.Accept.Content = "Проверить";
        d.OnAccept = () => { var h = RuleValidation.Domain(host.Text); var p = app.Text; var s = VM.State; string route = "";
            bool DomainMatch(string v) => h.Equals(v, StringComparison.OrdinalIgnoreCase) || h.EndsWith("." + v, StringComparison.OrdinalIgnoreCase);
            if (route.Length == 0) foreach (var r in VM.Rules.Where(r => r.Enabled)) { var match = r.Kind switch { RuleKind.GeoSite or RuleKind.GeoIp => GeodataCache.Shared.Get(Geodata.FilePath(VM.Bin, r.Kind), r.Kind == RuleKind.GeoIp).Contains(r.Value, h), RuleKind.Domain => DomainMatch(r.Value), RuleKind.ExactDomain => h == r.Value, RuleKind.Process => System.IO.Path.GetFileName(p).Equals(r.Value, StringComparison.OrdinalIgnoreCase), RuleKind.ExecutablePath => p.Equals(r.Value, StringComparison.OrdinalIgnoreCase), _ => RuleValidation.MatchesCidr(h, r.Value) }; if (match && (r.Network.Length == 0 || r.Network == (string)network.SelectedItem) && (r.Port.Length == 0 || r.Port == port.Text)) { route = $"Правило #{VM.Rules.IndexOf(r)+1}: {r.Value} → {RussianLabels.Of(r.Target)}"; break; } }
            if (route.Length == 0 && RuleValidation.Domains(s.OpenVpnDomains).Any(DomainMatch)) route = "OpenVPN (отключённый туннель → блокировка)";
            if (route.Length == 0 && RuleValidation.Domains(s.LocalDomains).Concat(PhysicalNetwork.Capture(s.PhysicalInterface).Suffixes).Concat(["local", "lan", "home.arpa"]).Any(DomainMatch)) route = "Напрямую; DNS физического адаптера";
                if (route.Length == 0 && ServiceDomains.YouTube.Any(DomainMatch)) route = "YouTube → " + (s.YouTube == ServiceRoute.Zapret ? "Zapret (напрямую)" : "VPN");
                if (route.Length == 0 && (ServiceDomains.Discord.Any(DomainMatch) || p.EndsWith("Discord.exe", StringComparison.OrdinalIgnoreCase))) route = "Discord → " + (s.Discord == ServiceRoute.Zapret ? "Zapret (напрямую)" : "VPN");
                if (route.Length == 0 && (ServiceDomains.Telegram.Any(DomainMatch) || ServiceDomains.TelegramIps.Any(c=>RuleValidation.MatchesCidr(h,c)) || p.EndsWith("Telegram.exe", StringComparison.OrdinalIgnoreCase))) route = s.TelegramSocks ? "Telegram → внешний SOCKS5 (при недоступности — ошибка соединения)" : s.TelegramVpnDefault ? "Telegram → VPN" : "Telegram → напрямую";
            if (route.Length == 0 && (!h.Contains('.') || new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "fc00::/7", "fe80::/10" }.Any(c => RuleValidation.MatchesCidr(h, c)))) route = "Напрямую (локальная сеть)";
            if (route.Length == 0) route = "Без совпадения → " + (s.Mode == RoutingMode.Global ? "Через VPN" : "Напрямую");
            d.Error.Text = route + ". Это расчёт правил, не проверка сетевой доступности."; return Task.FromResult(false); }; d.ShowDialog();
    }
    private async void StartZapret_Click(object sender,RoutedEventArgs e) => await VM.RunAsync(ct=>
    {
        var selected=VM.SelectedStrategy ?? throw new InvalidOperationException("Выберите стратегию.");
        return VM.ApplyZapretAsync(true,selected.File,ct);
    });
    private async void MakeZapretActive_Click(object sender,RoutedEventArgs e) => await VM.RunAsync(async ct=>
    {
        var file=VM.SelectedStrategy?.File ?? throw new InvalidOperationException("Выберите конфигурацию Zapret.");
        await VM.UpdateSettingsAsync(next=>{next.ZapretStrategy=System.IO.Path.GetFileName(file);next.ApplyBestZapret=false;},ct:ct);
        AutoBestZapret.IsChecked=false;
    });
    private async void ToggleZapret_Click(object sender,RoutedEventArgs e) => await VM.RunAsync(ct=>
    {
        var file=VM.State.ZapretStrategy.Length>0 ? VM.State.ZapretStrategy : VM.Strategies.FirstOrDefault()?.File;
        if(!VM.Zapret.Running && file==null) throw new InvalidOperationException("Нет конфигураций Zapret.");
        return VM.ApplyZapretAsync(!VM.Zapret.Running,file,ct);
    });
    private async void StopZapret_Click(object sender,RoutedEventArgs e) => await VM.RunAsync(ct=>VM.ApplyZapretAsync(false,null,ct));
    private async void TestZapret_Click(object sender,RoutedEventArgs e) {if(VM.SelectedStrategy is {} selected)await TestStrategies([selected]);}
    private async void AutoZapret_Click(object sender,RoutedEventArgs e) => await TestStrategies(VM.Strategies.ToArray());
    private Task TestStrategies(StrategyResult[] items) => VM.RunTestsAsync(ct=>VM.TestZapretAsync(items,new Progress<StrategyResult>(r=>VM.TestStatus="Zapret: "+r.Name),ct));
    private async void CheckModule_Click(object sender, RoutedEventArgs e) => await VM.CheckUpdatesAsync(lifetime.Token);
    private void RequireStopped() { if (VM.Router.Running || VM.Zapret.Running || telegram.Running || VM.TestsBusy) throw new InvalidOperationException("Перед установкой остановите подключения и тесты. Проверять обновления можно в любое время."); }
    private string? pendingNetcatUpdate;
    private async void InstallModule_Click(object sender, RoutedEventArgs e) => await VM.RunAsync(async ct =>
    {
        var selected=VM.Modules.Where(m=>m.Selected&&m.Available&&!m.Pinned).ToArray();
        if(selected.Length==0) { VM.UpdateStatus="Выберите доступные обновления."; return; }
        var errors=new List<string>();
        foreach(var module in selected)
        {
            try
            {
                VM.UpdateStatus="Скачиваю и проверяю " + module.DisplayName + (VM.Router.VpnRunning ? " через VPN…" : "…");
                if(module.Key=="netcat") pendingNetcatUpdate ??= await PortableUpdate.PrepareAsync(module.Check.Release!,AppContext.BaseDirectory,VM.State.PinnedModules,ct,VM.Router.VpnRunning ? VM.Router.LatencyPort : 0);
                else if(!VM.Updater.HasPrepared(module.Check.Release!)) await VM.Updater.InstallAsync(module.Check.Release!,VM.State,ct,prepareOnly:true);
                VM.WriteLog("Подготовлено обновление: " + module.DisplayName);
            }
            catch(Exception ex) when(ex is not OperationCanceledException) { errors.Add(module.DisplayName); VM.WriteLog(module.Key+": "+ex.Message); }
        }
        VM.UpdateStatus="Скачанные файлы готовы. Остановите подключения и нажмите «Установить скачанное».";
        if(errors.Count>0) VM.UpdateStatus+=" Не удалось скачать: "+string.Join(", ",errors);
    });
    private async void InstallDownloaded_Click(object sender, RoutedEventArgs e)
    {
        string? launch=null;
        await VM.RunAsync(async ct =>
        {
            RequireStopped(); var errors=new List<string>(); int installed=0;
            foreach(var module in VM.Modules.Where(m=>m.Selected&&m.Available&&!m.Pinned).ToArray())
            {
                try
                {
                    if(module.Key=="netcat") { if(pendingNetcatUpdate!=null) launch=pendingNetcatUpdate; continue; }
                    if(!VM.Updater.HasPrepared(module.Check.Release!)) continue;
                    await VM.Updater.InstallPreparedAsync(module.Check.Release!,VM.State,ct); installed++; VM.WriteLog("Обновлён модуль "+module.DisplayName);
                }
                catch(Exception ex) when(ex is not OperationCanceledException) { errors.Add(module.DisplayName); VM.WriteLog(ex.Message); }
            }
            var rows=VM.Modules.ToArray(); VM.Modules.Clear(); foreach(var row in rows) VM.Modules.Add(new ModuleRow(new ModuleCheck(row.Key,VM.Updater.InstalledVersion(row.Key),row.Check.Release),row.Pinned));
            VM.Refresh(); VM.UpdateStatus=installed>0 ? $"Установлено компонентов: {installed}." : launch!=null ? "NetCat готов к перезапуску." : "Нет выбранных скачанных обновлений.";
            if(errors.Count>0) VM.UpdateStatus+=" Ошибки: "+string.Join(", ",errors);
        });
        if(launch!=null && !VM.WorkCancellation.IsCancellationRequested)
        { try { await PortableUpdate.LaunchAsync(launch); await ExitAsync(); } catch(Exception ex) { VM.Status="Не удалось запустить обновление: "+ex.Message; } }
    }
    private async void RollbackModule_Click(object sender, RoutedEventArgs e) { if(ModulesGrid.SelectedItem is ModuleRow module) await VM.RunAsync(async ct=> { RequireStopped(); VM.Updater.Rollback(module.Key); VM.Refresh(); await VM.CheckUpdatesAsync(ct); }); }
    private async void PinModule_Click(object sender, RoutedEventArgs e) { if(ModulesGrid.SelectedItem is ModuleRow module) await VM.RunAsync(async ct=> { if(!VM.State.PinnedModules.Add(module.Key)) VM.State.PinnedModules.Remove(module.Key); await VM.SaveAsync(); await VM.CheckUpdatesAsync(ct); }); }
    private async void StartTelegram_Click(object sender, RoutedEventArgs e) => await VM.RunAsync(ct=>VM.SetTelegramEnabledAsync(!telegram.Running,ct));
    private async void StopTelegram_Click(object sender, RoutedEventArgs e) => await VM.RunAsync(ct=>VM.SetTelegramEnabledAsync(false,ct));
    private void TelegramLink_Click(object sender, RoutedEventArgs e) { if(telegram.Running) { Clipboard.SetText(telegram.Link); VM.Status="Ссылка подключения скопирована. Откройте её в Telegram."; } }
    private void OpenTelegram_Click(object sender, RoutedEventArgs e) { if(telegram.Running) Process.Start(new ProcessStartInfo(telegram.Link) { UseShellExecute=true }); }
    private void ModulesPage_Click(object sender,RoutedEventArgs e) => Pages.SelectedIndex=4;
    private void RoutingPage_Click(object sender,RoutedEventArgs e) => Pages.SelectedIndex=2;
    private void ZapretPage_Click(object sender,RoutedEventArgs e) => Pages.SelectedIndex=3;
    private void OpenModules_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(VM.Bin) { UseShellExecute = true });
    private void BaseColor_Click(object sender, RoutedEventArgs e) => PickColor(true);
    private void AccentColor_Click(object sender, RoutedEventArgs e) => PickColor(false);
    private void PickColor(bool primary)
    {
        var picker=new ColorPickerWindow(this,primary?"Основной цвет интерфейса":"Акцентный цвет",primary?VM.State.BaseColor:VM.State.AccentColor,primary);
        if(picker.ShowDialog()==true) { if(primary) VM.State.BaseColor=picker.SelectedHex; else VM.State.AccentColor=picker.SelectedHex; _=VM.RunAsync(_=>VM.SaveAsync()); }
    }
    private async void Light_Click(object sender, RoutedEventArgs e) { VM.State.BaseColor = "#F4F6F8"; await VM.RunAsync(_=>VM.SaveAsync()); }
    private async void Dark_Click(object sender, RoutedEventArgs e) { VM.State.BaseColor = "#151A22"; await VM.RunAsync(_=>VM.SaveAsync()); }
    private async Task RefreshAutostartAsync()
    {
        AutostartSwitch.IsEnabled = false;
        try
        {
            var result = await PhysicalNetwork.PowerShell("$t=Get-ScheduledTask -TaskName 'NetCat_AutoStart' -ErrorAction SilentlyContinue; if($t -and $t.State -ne 'Disabled') { 'enabled' } else { 'disabled' }");
            if(result.Code != 0) throw new IOException("Не удалось проверить автозапуск.");
            AutostartSwitch.IsChecked = result.Output.Trim() == "enabled";
            AutostartStatus.Text = AutostartSwitch.IsChecked == true ? "Автозапуск включён" : "Автозапуск выключен";
        }
        catch(Exception ex) { AutostartStatus.Text = ex.Message; }
        finally { AutostartSwitch.IsEnabled = true; }
    }
    private async void Autostart_Click(object sender, RoutedEventArgs e)
    {
        if(VM.Busy) { await RefreshAutostartAsync(); return; }
        bool enabled = AutostartSwitch.IsChecked == true;
        await VM.RunAsync(async _ => { try { await Autostart(enabled); } finally { await RefreshAutostartAsync(); } });
    }
    private async void ApplyTelegramPort_Click(object sender, RoutedEventArgs e) => await VM.RunAsync(async ct =>
    {
        await VM.SaveAsync();
        VM.WriteLog("Порт Telegram применён. Подключите Telegram по новой ссылке.");
    });
    private static async Task Autostart(bool enabled)
    {
        var exe = System.IO.Path.Combine(AppContext.BaseDirectory, "NetCat.exe");
        var script = enabled ? $"$a=New-ScheduledTaskAction -Execute {PhysicalNetwork.Literal(exe)}; $t=New-ScheduledTaskTrigger -AtLogOn -User ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name); $p=New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest; Register-ScheduledTask -TaskName 'NetCat_AutoStart' -Action $a -Trigger $t -Principal $p -Force | Out-Null" : "Unregister-ScheduledTask -TaskName 'NetCat_AutoStart' -Confirm:$false -ErrorAction SilentlyContinue";
        var result = await PhysicalNetwork.PowerShell(script); if (result.Code != 0) throw new IOException("Не удалось изменить задачу автозапуска: " + ProcessHost.Redact(result.Output));
    }
    private async void NetworkInfo_Click(object sender, RoutedEventArgs e) => await VM.RunAsync(_ => { var n = PhysicalNetwork.Capture(VM.State.PhysicalInterface); VM.WriteLog($"Физический адаптер: {n.Name}; DNS: {n.Dns}; суффиксы: {string.Join(", ", n.Suffixes)}. Корпоративные прямые домены используют этот DNS."); return Task.CompletedTask; });
    private async void TimerTick(object? sender, EventArgs e)
    {
        backgroundTicks++;
        UpdateTraffic(); _ = UpdatePingAsync(); VM.PollRuntimeState(); if (timerBusy || VM.Busy || exiting) return;
        timerBusy = true;
        try
        {
            if (!VM.TestsBusy && VM.Router.ZapretAvailable != VM.Zapret.Running)
                await VM.RunAsync(async ct => { await VM.UpdateSettingsAsync(_=>{},true,ct:ct); VM.WriteLog("Маршрут сервисов обновлён по состоянию Zapret."); });
            if (!App.IsSmoke && VM.State.AutoTest && !VM.Recovering && !VM.TestsBusy && DateTime.Now >= nextTest)
            {
                await VM.RunTestsAsync(async ct =>
                {
                    var settings=JsonSettings.Clone(VM.State); var revision=VM.Router.SessionRevision;
                    var candidates = settings.Profiles.Where(p => !p.IsOpenVpn && (p.Candidate || p.Id == settings.MainProfileId)).ToArray(); var results = new List<(Profile Profile, DelayResult Result)>();
                    foreach (var p in candidates)
                    {
                        if (!settings.AutoTest && p.Id != settings.MainProfileId) continue;
                        VM.TestStatus="Фоновый тест: "+p.Name;
                        var result = await VM.Router.TestProfileAsync(p, settings, ct);
                        var current=VM.Profiles.FirstOrDefault(x=>x.Id==p.Id); current?.SetTestResult(result);
                        results.Add((p, result));
                    }

                });
                nextTest = DateTime.Now.AddSeconds(Math.Max(15, VM.State.TestIntervalSeconds));
            }
            if (DateTime.Now >= nextSubscriptions && !VM.Busy)
            {
                nextSubscriptions = DateTime.Now.AddMinutes(5);
                foreach (var sub in VM.State.Subscriptions.Where(s => s.UpdateHours > 0 && (!s.UpdatedAt.HasValue || DateTimeOffset.Now - s.UpdatedAt.Value >= TimeSpan.FromHours(s.UpdateHours))).ToArray()) await VM.RunAsync(ct => RefreshSubscriptionAsync(sub, ct));
            }
            if(!App.IsSmoke && VM.State.CheckModuleUpdates && DateTime.Now>=nextModuleCheck)
            { nextModuleCheck=DateTime.Now.AddHours(6); await VM.CheckUpdatesAsync(lifetime.Token); }
        }
        catch (Exception ex) { VM.WriteLog(ex.Message); }
        finally { timerBusy = false; }
    }
    private async Task RecoverConnectionAsync(Guid activeId, long revision)
    {
        if (VM.Recovering || !VM.Router.VpnRequested) return;
        VM.Recovering = true; VM.NotifyState();
        using var probes = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        bool Current() => VM.State.AutoSwitch && VM.Router.VpnRequested && VM.Router.SessionRevision == revision && VM.State.MainProfileId == activeId && !VM.Busy;
        try
        {
            var settings = JsonSettings.Clone(VM.State); settings.TestTimeoutSeconds = Math.Min(4, settings.TestTimeoutSeconds);
            var candidates = new Queue<Profile>(settings.Profiles.Where(p => p.Candidate && !p.IsOpenVpn && p.Id != activeId));
            var pending = new List<Task<Profile?>>();
            async Task<Profile?> Probe(Profile profile)
            {
                for (int sample = 0; sample < 2; sample++)
                {
                    if (!Current()) return null;
                    var result = await VM.Router.TestProfileAsync(profile, settings, probes.Token);
                    VM.Profiles.FirstOrDefault(p => p.Id == profile.Id)?.SetTestResult(result);
                    if (!result.Success) return null;
                }
                return profile;
            }
            Profile? winner = null;
            try
            {
                while (Current() && (candidates.Count > 0 || pending.Count > 0))
                {
                    while (pending.Count < 2 && candidates.TryDequeue(out var candidate)) pending.Add(Probe(candidate));
                    var completed = await Task.WhenAny(pending); pending.Remove(completed);
                    winner = await completed; if (winner != null) break;
                }
            }
            finally { probes.Cancel(); try { await Task.WhenAll(pending); } catch (OperationCanceledException) { } }
            if (winner == null || !Current()) return;
            await VM.CommitFailoverAsync(activeId,winner.Id,revision,lifetime.Token);
            if(VM.Router.ActiveProfileId==winner.Id) {failover.Switched(DateTimeOffset.UtcNow);VM.WriteLog("Автосмена: "+winner.Name);}
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { VM.WriteLog("Автосмена: " + ex.Message); }
        finally { VM.Recovering = false; VM.NotifyState(); }
    }
    private void UpdateTraffic()
    {
        if(!IsVisible || WindowState==WindowState.Minimized || Pages.SelectedIndex!=0)
        { previousReceive=previousSend=previousTrafficStamp=0; return; }
        try
        {
            if(!VM.Router.Running || !VM.Router.TunActive) { previousReceive=previousSend=previousTrafficStamp=0; trafficAdapter=null; DownloadValue.Text=UploadValue.Text="— Мбит/с"; return; }
            if(trafficRevision!=VM.Router.SessionRevision || DateTime.UtcNow>=nextAdapterRefresh)
            {
                adapterEnumerations++; trafficAdapter=NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n=>n.Name=="NetCat-TUN");
                trafficRevision=VM.Router.SessionRevision; nextAdapterRefresh=DateTime.UtcNow.AddSeconds(30); previousReceive=previousSend=previousTrafficStamp=0;
            }
            if(trafficAdapter==null) return;
            var stats=trafficAdapter.GetIPStatistics(); var now=Stopwatch.GetTimestamp();
            var seconds=previousTrafficStamp==0?0:Stopwatch.GetElapsedTime(previousTrafficStamp,now).TotalSeconds;
            var down=seconds==0?0:Math.Max(0,stats.BytesReceived-previousReceive)*8d/1_000_000/seconds;
            var up=seconds==0?0:Math.Max(0,stats.BytesSent-previousSend)*8d/1_000_000/seconds;
            previousReceive=stats.BytesReceived; previousSend=stats.BytesSent; previousTrafficStamp=now;
            TrafficText.Text = $"TUN   ↓ {down:F2} Мбит/с     ↑ {up:F2} Мбит/с     Последние 60 секунд"; traffic.Enqueue(down); while (traffic.Count > 60) traffic.Dequeue();
            DownloadValue.Text=$"{down:F2} Мбит/с"; UploadValue.Text=$"{up:F2} Мбит/с";
            if(!TrafficCanvas.IsVisible || TrafficCanvas.ActualWidth<=0 || TrafficCanvas.ActualHeight<=0) return;
            TrafficCanvas.Children.Clear(); var values = traffic.ToArray(); var max = Math.Max(1, values.Max()); var line = new Polyline { StrokeThickness = 2 }; line.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            for (int i = 0; i < values.Length; i++) line.Points.Add(new Point(i * Math.Max(1, TrafficCanvas.ActualWidth) / 59, 65 - values[i] / max * 60)); TrafficCanvas.Children.Add(line);
        }
        catch (NetworkInformationException) { trafficAdapter=null; nextAdapterRefresh=DateTime.MinValue; }
    }
    private async Task UpdatePingAsync()
    {
        if (App.IsSmoke || exiting) return;
        if (!VM.Router.VpnRunning || VM.Router.LatencyPort == 0) { PingValue.Text = "— мс"; nextPing = DateTime.MinValue; measuredLatencyPort = 0; return; }
        if (measuredLatencyPort != VM.Router.LatencyPort) { nextPing = DateTime.MinValue; PingValue.Text = "… мс"; }
        if (pingBusy || DateTime.UtcNow < nextPing || VM.Busy) return;
        pingBusy = true; nextPing = DateTime.UtcNow.AddSeconds(VM.State.AutoSwitch ? 5 : 10);
        var revision = VM.Router.SessionRevision; var port = VM.Router.LatencyPort; measuredLatencyPort = port;
        try
        {
            var result = await ConnectionLatency.MeasureAsync(port, VM.State.TestUrl, lifetime.Token, VM.State.AutoSwitch ? 4 : VM.State.TestTimeoutSeconds);
            if (VM.Router.SessionRevision == revision && VM.Router.ActiveProfileId is Guid activeId)
            {
                failover.Record(activeId, result, DateTimeOffset.UtcNow);
                if (!result.Success && VM.State.AutoSwitch && !VM.TestsBusy && !VM.Busy && failover.ShouldRecover(activeId, VM.State.FailureThreshold, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2)))
                { await RecoverConnectionAsync(activeId, revision); if (VM.Router.SessionRevision != revision) return; }
            }
            DelayResult? system = VM.Router.TunActive && (result.Success || !VM.State.AutoSwitch) ? await tunnelHealth.CheckAsync(VM.Router.HealthSourcePort,lifetime.Token) : null;
            if (!exiting && VM.Router.VpnRunning && VM.Router.LatencyPort == port && VM.Router.SessionRevision == revision)
            {
                VM.SetHealth(result,system);
                PingValue.Text = result.Success ? $"{result.Milliseconds} мс" : "Нет ответа";
                PingValue.ToolTip = result.Success ? "HTTP-задержка через подключённый VPN. Обновляется каждые 10 секунд." : "Проверка через подключённый VPN: " + result.Error;
            }
        }
        catch (OperationCanceledException) { }
        finally { pingBusy = false; }
    }
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (exiting) return; e.Cancel = true; if (VM.State.MinimizeToTray) Hide(); else _ = ExitAsync();
    }
    public async Task ExitAsync()
    {
        if (exiting) return; exiting = true; timer.Stop(); lifetime.Cancel(); VM.WorkCancellation.Cancel(); VM.TestsCancellation.Cancel();
        try { for (int i = 0; i < 100 && (VM.Busy||VM.TestsBusy); i++) await Task.Delay(100); await VM.StopForExitAsync(); }
        catch (Exception ex) { VM.WriteLog("Завершение: " + ex.Message); }
        finally
        {
            foreach(var cleanup in new Action[]{VM.Dispose,()=>tray?.Dispose(),tunnelHealth.Dispose})
                try {cleanup();}catch(Exception ex){VM.WriteLog("Очистка при завершении: "+ex.Message);}
            try {Close();}finally{Application.Current.Shutdown();}
        }
    }
    public async Task CaptureScreensAsync(string destination)
    {
        if (!App.IsSmoke) throw new InvalidOperationException("Снимки доступны только в режиме smoke.");
        Directory.CreateDirectory(destination); HeaderSmoke.VerifyDragCaption(this);
        await LiveRulesSmoke.VerifyAsync(VM,destination);
        await StateSmoke.VerifyAsync(destination,VM.Bin);
        await TransactionSmoke.VerifyAsync(destination,VM.Bin);
        var children=TrafficCanvas.Children.Count; var enumerations=adapterEnumerations; var ticks=backgroundTicks;
        Hide(); await Task.Delay(2200);
        if(TrafficCanvas.Children.Count!=children || adapterEnumerations!=enumerations || backgroundTicks<=ticks) throw new InvalidOperationException("Фоновый таймер рисует скрытый график или остановил обслуживание.");
        Show();
        await File.WriteAllTextAsync(System.IO.Path.Combine(destination,"hidden-timer-check.txt"),"Hidden window: canvas and adapter enumeration unchanged; background timer continues.");
        for (int i = 0; i < Pages.Items.Count; i++)
        {
            HeaderSmoke.ClickTab(this,Pages,i); await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render); await Task.Delay(150);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = File.Create(System.IO.Path.Combine(destination, $"page-{i}.png")); encoder.Save(output);
        }
        Theme.Apply("#171D29", "#C784E2"); Pages.SelectedIndex = 0;
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render); await Task.Delay(150);
        var dark = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); dark.Render(this);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(dark)); using var stream = File.Create(System.IO.Path.Combine(destination, "dark-purple.png")); png.Save(stream);
        void Capture(Window w,string name)
        {
            w.UpdateLayout(); var image=new System.Windows.Media.Imaging.RenderTargetBitmap((int)w.ActualWidth,(int)w.ActualHeight,96,96,PixelFormats.Pbgra32); image.Render(w);
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image)); using var output=File.Create(System.IO.Path.Combine(destination,name)); encoder.Save(output);
        }
        VM.State.Profiles.AddRange([new Profile { Name="Europe · Reality", Core="Xray", Result="HTTP: 48 мс" }, new Profile { Name="Europe · WebSocket", Result="Не проверен" }, new Profile { Name="Офис", Protocol="openvpn", Core="OpenVPN", Candidate=false }]);
        VM.State.Rules.AddRange([new RoutingRule { Value="intranet.example", Kind=RuleKind.Domain, Target=RouteTarget.Direct }, new RoutingRule { Value="Discord.exe", Kind=RuleKind.Process, Target=RouteTarget.Vpn, Network="udp", Port="443" }]);
        VM.Refresh();
        if (VM.Strategies.Count > 2) { VM.Strategies[0].Passed = true; VM.Strategies[1].Passed = true; VM.State.ZapretStrategy = System.IO.Path.GetFileName(VM.Strategies[1].File); VM.NotifyState(); }
        foreach (var page in new[] { 1,2,3,4 }) { Pages.SelectedIndex=page; await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render); await Task.Delay(150); Capture(this,$"populated-{page}.png"); }
        Pages.SelectedIndex=3; await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render); await Task.Delay(150);
        var viewer=SmoothScroll.FindViewer(ZapretGrid) ?? throw new InvalidOperationException("Нет прокрутки Zapret.");
        if (viewer.ScrollableHeight>48)
        {
            viewer.ScrollToTop(); UpdateLayout(); var selection=ZapretGrid.SelectedItem;
            SmoothScroll.ScrollBy(ZapretGrid,48); await Task.Delay(60); UpdateLayout(); var middle=viewer.VerticalOffset;
            await Task.Delay(230); UpdateLayout();
            if (Math.Abs(viewer.VerticalOffset-48)>1 || !Equals(selection,ZapretGrid.SelectedItem)) throw new InvalidOperationException("Пиксельная прокрутка нарушена.");
            if (SystemParameters.ClientAreaAnimation && (middle<=0 || middle>=48)) throw new InvalidOperationException("Нет промежуточного кадра прокрутки.");
            Capture(this,"zapret-scrolled.png");
            await File.WriteAllTextAsync(System.IO.Path.Combine(destination,"scroll-check.txt"),$"Middle: {middle:F2}px; final: {viewer.VerticalOffset:F2}px; selection unchanged");
        }
        Theme.Apply("#090909","#AA0000"); Pages.SelectedIndex=4; await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render); await Task.Delay(150); Capture(this,"modules-red.png");
        Width=820; Height=690; await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render); await Task.Delay(150);
        for (var i=0;i<Pages.Items.Count;i++) HeaderSmoke.ClickTab(this,Pages,i);
        HeaderSmoke.ClickTab(this,Pages,4); await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);
        await ScrollSmoke.CheckModulesAsync(ModulesGrid,destination);
        HeaderSmoke.ClickTab(this,Pages,1); await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render); Capture(this,"compact.png");
        await File.WriteAllTextAsync(System.IO.Path.Combine(destination,"header-check.txt"),"Native header: HTCAPTION drag; all 7 tabs: native HTCLIENT, no overlay, mouse event switches page. Verified at widths 1120 and 820.");
        Width=1120; Height=890; Theme.Apply("#171D29", "#C784E2");
        var colors=new ColorPickerWindow(this,"Акцентный цвет","#C784E2",false) { WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000 };
        colors.Show(); await Task.Delay(150); Capture(colors,"color-picker.png"); colors.Close();
        var ruleDialog=RuleEditor.Create(this,new RoutingRule(),_=>{});
        ruleDialog.WindowStartupLocation=WindowStartupLocation.Manual; ruleDialog.Left=-20000; ruleDialog.Top=-20000;
        ruleDialog.Show(); await Task.Delay(150); Capture(ruleDialog,"rule-simple.png");
        var advanced=ruleDialog.Fields.Children.OfType<Expander>().Single();
        if(advanced.IsExpanded) throw new InvalidOperationException("New rule must start with advanced fields collapsed.");
        advanced.IsExpanded=true; await Task.Delay(150); Capture(ruleDialog,"rule-advanced.png"); ruleDialog.Close();
        var apps=new ProcessPickerWindow(this) { WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000 };
        apps.Show(); await apps.Initialization; await Task.Delay(150); Capture(apps,"process-picker.png"); apps.Close();
        var trayActions=0;
        var trayMenu=TrayIntegration.CreateMenu(()=>trayActions++,()=>trayActions++,new[]{new TrayCommand("VPN",true,true,()=>trayActions++),new TrayCommand("OpenVPN",false,false,()=>trayActions++),new TrayCommand("Zapret",true,true,()=>trayActions++),new TrayCommand("Telegram",false,true,()=>trayActions++)});
        trayMenu.ApplyTemplate(); trayMenu.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity)); trayMenu.Arrange(new Rect(trayMenu.DesiredSize)); trayMenu.UpdateLayout();
        var trayImage=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(trayMenu.ActualWidth),(int)Math.Ceiling(trayMenu.ActualHeight),96,96,PixelFormats.Pbgra32); trayImage.Render(trayMenu);
        var trayPng=new System.Windows.Media.Imaging.PngBitmapEncoder(); trayPng.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(trayImage));
        using(var output=File.Create(System.IO.Path.Combine(destination,"tray-menu.png"))) trayPng.Save(output);
        ((MenuItem)trayMenu.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); trayMenu.IsOpen=false;
        if(trayActions!=1) throw new InvalidOperationException("Tray action was not dispatched exactly once.");
    }
}
