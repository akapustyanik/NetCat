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
    private bool exiting;
    private readonly WindowPresentationSession? presentation;
    private readonly CancellationTokenSource lifetime = new();
    private readonly IUnelevatedShellLauncher journalShell;
    private int journalActionRunning;
    private int backgroundTicks;
    private readonly Queue<double> traffic = new();
    private sealed record ModuleVersionChoice(
        string Version,
        bool PinAfterInstall);
    public MainWindow(MainViewModel vm, IUnelevatedShellLauncher? journalShell = null, WindowPresentationSession? presentation = null)
    {
        this.presentation = presentation;
        InitializeComponent(); VM = vm; this.journalShell = journalShell ?? new UnelevatedExplorerShellLauncher(); DataContext = vm;
        WindowFrame.Apply(this);
        Closing += WindowClosing;
        StateChanged += (_, _) => SaveCurrentPresentationState();
        IsVisibleChanged += (_, _) => { if (IsVisible) ShowInTaskbar = true; SaveCurrentPresentationState(); };
        timer.Tick += TimerTick; timer.Start();
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
        };
    }
    private void Vpn_Click(object sender, RoutedEventArgs e) => VM.UserRequestedVpnChange(!VM.DesiredState.MainVpnEnabled);
    private void OpenVpn_Click(object sender, RoutedEventArgs e) => VM.UserRequestedOpenVpnChange(!VM.DesiredState.OpenVpnEnabled);
    private async void Save_Click(object sender, RoutedEventArgs e) => await VM.RunAsync(_ => VM.SaveAsync());
    private async void Apply_Click(object sender, RoutedEventArgs e) => await VM.RunAsync(VM.ApplyRoutesAsync);
    private void Cancel_Click(object sender, RoutedEventArgs e) => VM.CancelCurrentOperation();
    private void CancelTests_Click(object sender, RoutedEventArgs e) => VM.TestsCancellation.Cancel();
    private void ImportOvpn_Click(object sender, RoutedEventArgs e) => ImportFiles(true);
    private void ImportFiles(bool ovpn = false)
    {
        var open = new OpenFileDialog { Filter = ovpn ? "OpenVPN|*.ovpn" : "Конфигурации|*.ovpn;*.json;*.yaml;*.yml;*.txt;*.conf|Все файлы|*.*", Multiselect = true };
        if (open.ShowDialog(this) != true) return;
        _ = VM.RunAsync(async _ =>
        {
            var profiles = new List<Profile>(); var errors = new List<string>();
            foreach (var f in open.FileNames)
            {
                if (new FileInfo(f).Length > 8 * 1024 * 1024) throw new InvalidDataException("Конфигурация слишком большая.");
                if (f.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase)) { profiles.Add(OpenVpnBundle.Read(f).ToProfile(System.IO.Path.GetFileNameWithoutExtension(f))); continue; }
                var raw = await File.ReadAllTextAsync(f);
                var result = ProfileImporter.ParseForImport(raw, System.IO.Path.GetFileNameWithoutExtension(f)); profiles.AddRange(result.Profiles); errors.AddRange(result.Errors);
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
        var insecure = dialog.Check("Дополнительно: разрешить HTTP и локальную сеть для этой подписки (небезопасно)", false);
        var file = new Button { Content = "Выбрать файлы (.ovpn, .json, .txt)", HorizontalAlignment = HorizontalAlignment.Left }; dialog.Insert(file);
        file.Click += (_, _) => { dialog.Close(); ImportFiles(); };
        dialog.Note("После чтения откроется список найденных профилей. Адрес подписки хранится зашифрованно для текущего пользователя Windows.");
        dialog.Accept.Content = "Прочитать и проверить";
        dialog.OnAccept = async () =>
        {
            var raw = text.Text.Trim(); Subscription? subscription = null;
            if (Uri.TryCreate(raw, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
            {
                var source = raw; raw = await ReadSubscriptionAsync(source, CancellationToken.None, insecure.IsChecked == true);
                if (subscribe.IsChecked == true) subscription = new Subscription { Name = name.Text, Url = source, AllowInsecureTransport = insecure.IsChecked == true, UpdatedAt = DateTimeOffset.Now };
            }
            var result = ProfileImporter.ParseForImport(raw, name.Text);
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
        var choices = profiles.Select(p => (p, box: dialog.Check(p.ToString() + (p.SecurityWarning.Length > 0 ? " — " + p.SecurityWarning : ""), true))).ToArray();
        foreach (var e in errors.Take(15)) dialog.Note(e);
        dialog.Accept.Content = "Импортировать выбранные";
        dialog.OnAccept = () => { profiles.RemoveAll(p => choices.First(c => c.p == p).box.IsChecked != true); if (profiles.Count == 0) throw new InvalidOperationException("Выберите хотя бы один профиль."); return Task.FromResult(true); };
        return dialog.ShowDialog() == true;
    }
    private static Task<string> ReadSubscriptionAsync(string url, CancellationToken ct, bool allowInsecureTransport = false) =>
        new SubscriptionClient().ReadAsync(url, allowInsecureTransport, ct);
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
        ShowTestDetails(profile.Name,profile.ResultDetails + "\n\nПередача данных\n" + profile.TrafficDetails);
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
    private async void TestTraffic_Click(object sender, RoutedEventArgs e) { if(VM.SelectedProfile is { } p) await VM.RunTestsAsync(ct => VM.TestTrafficAsync([p], ct)); }
    private async void TestAllTraffic_Click(object sender, RoutedEventArgs e) => await VM.RunTestsAsync(ct => VM.TestTrafficAsync(VM.Profiles.Where(p => !p.IsOpenVpn).ToArray(), ct));
    private void Subscriptions_Click(object sender, RoutedEventArgs e)
    {
        if (VM.State.Subscriptions.Count == 0) { VM.Status = "Сначала импортируйте URL подписки."; return; }
        var d = new EditorDialog(this, "Подписки"); var choice = d.Choice("Подписка", VM.State.Subscriptions, VM.State.Subscriptions[0]); var interval = d.Text("Интервал обновления, часов (0 — вручную)", VM.State.Subscriptions[0].UpdateHours.ToString());
        var insecure = d.Check("Дополнительно: разрешить HTTP и локальную сеть (небезопасно)", VM.State.Subscriptions[0].AllowInsecureTransport);
        var restoreDeleted = d.Check("Сбросить исключения вручную удалённых профилей", false);
        choice.SelectionChanged += (_, _) => { var selected=(Subscription)choice.SelectedItem; interval.Text=selected.UpdateHours.ToString(); insecure.IsChecked=selected.AllowInsecureTransport; restoreDeleted.IsChecked=false; };
        var update = new Button { Content = "Обновить выбранную сейчас" }; d.Insert(update);
        update.Click += async (_, _) => { if(VM.Busy) { d.Error.Text="Дождитесь текущей операции."; return; } try { VM.Busy=true; update.IsEnabled = false; d.Error.Text = await RefreshSubscriptionAsync((Subscription)choice.SelectedItem, CancellationToken.None, interactive:true); } catch (Exception ex) { d.Error.Text = ProcessHost.Redact(ex.Message); } finally { VM.Busy=false; update.IsEnabled = true; } };
        d.Note("Изменения небезопасного режима сначала сохраните кнопкой принятия, затем откройте подписки и обновите. URL не показывается. Локальные имена и участие в автосмене сохраняются. Манифест сохраняет ID даже при смене параметров. Полный корректный манифест удаляет отсутствующие профили; выбранный профиль сохраняется с отметкой до смены выбора. При ошибках удаление запрещено.");
        d.Note("Вручную удалённые профили не возвращаются при обновлении. Чтобы разрешить их повторное добавление, отметьте сброс исключений, сохраните и затем обновите выбранную подписку.");
        d.OnAccept = async () => { var id=((Subscription)choice.SelectedItem).Id;if (!int.TryParse(interval.Text, out var hours) || hours is < 0 or > 8760) throw new InvalidDataException("Укажите интервал от 0 до 8760 часов."); await VM.UpdateSettingsAsync(next=>{var selected=next.Subscriptions.Single(p=>p.Id==id);selected.UpdateHours=hours;selected.AllowInsecureTransport=insecure.IsChecked==true;if(restoreDeleted.IsChecked==true)selected.ExcludedProfileKeys.Clear();}); return true; }; d.ShowDialog();
    }
    private async Task<string> RefreshSubscriptionAsync(Subscription sub, CancellationToken ct, bool interactive=false)
    {
        var revision=VM.SettingsRevision;
        var result=ProfileImporter.ParseForImport(await ReadSubscriptionAsync(sub.Url,ct,sub.AllowInsecureTransport),sub.Name);
        try { return await VM.RefreshSubscriptionAsync(sub.Id,ct,approvedDocument:result,approvedRevision:revision); }
        catch (SubscriptionSecurityApprovalRequiredException) when (interactive)
        {
            var names=string.Join(Environment.NewLine,result.Profiles.Where(ProfileSecurity.CertificateValidationDisabled).Select(p=>p.Name+": проверка TLS-сертификата отключена"));
            if(MessageBox.Show(this,names+Environment.NewLine+"Применить именно эти небезопасные изменения?", "Изменение безопасности подписки",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes) throw;
            return await VM.RefreshSubscriptionAsync(sub.Id,ct,approveSecurityChanges:true,approvedDocument:result,approvedRevision:revision);
        }
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
        return VM.UserRequestedZapretStrategyAsync(selected.File, enableZapret: true, ct);
    });
    private async void MakeZapretActive_Click(object sender,RoutedEventArgs e) => await VM.RunAsync(async ct=>
    {
        var file=VM.SelectedStrategy?.File ?? throw new InvalidOperationException("Выберите конфигурацию Zapret.");
        await VM.UserRequestedZapretStrategyAsync(file, enableZapret: null, ct);
        AutoBestZapret.IsChecked=false;
    });
    private void ToggleZapret_Click(object sender, RoutedEventArgs e) => VM.UserRequestedZapretChange(!VM.Zapret.Running);
    private void StopZapret_Click(object sender, RoutedEventArgs e) => VM.UserRequestedZapretChange(false);
    private async void TestZapret_Click(object sender,RoutedEventArgs e) {if(VM.SelectedStrategy is {} selected)await TestStrategies([selected]);}
    private async void AutoZapret_Click(object sender,RoutedEventArgs e) => await TestStrategies(VM.Strategies.ToArray(),autoSelect:true);
    private async void TestAllZapret_Click(object sender,RoutedEventArgs e) => await TestStrategies(VM.Strategies.ToArray());
    private Task TestStrategies(StrategyResult[] items,bool autoSelect=false) => VM.RunTestsAsync(ct=>VM.TestZapretAsync(items,new Progress<StrategyResult>(r=>VM.TestStatus="Zapret: "+r.Name),ct,status=>VM.TestStatus=status,autoSelect));
    private ModuleVersionChoice? ShowVersionPicker(
        ModuleRow module,
        IReadOnlyList<string> versions)
    {
        var initial =
            versions.FirstOrDefault()
            ?? "";

        var dialog =
            new EditorDialog(
                this,
                $"Версия {module.DisplayName}",
                600)
            {
                Height = 520,
                MinHeight = 420,
                ShowInTaskbar = false
            };

        dialog.Note(
            $"Установлена: {module.Installed}");

        var selector =
            dialog.Choice(
                "Версия из официальных releases",
                versions,
                initial);

        selector.MinWidth =
            470;

        if(versions.Count > 0)
            selector.SelectedIndex = 0;

        TextBox? exact =
            null;

        dialog.Advanced(
            () =>
            {
                exact =
                    dialog.Text(
                        "Точный тег / номер версии",
                        "");

                dialog.Note(
                    "Заполняйте это поле только если нужного release нет в видимой части списка. Если поле заполнено, оно имеет приоритет.");
            });

        var pin =
            dialog.Check(
                "Закрепить выбранную версию после установки",
                module.Pinned);

        dialog.Note(
            "NetCat хранит текущую и одну предыдущую версию. Старый release при необходимости скачивается заново.");

        dialog.Accept.Content =
            "Установить";

        ModuleVersionChoice? result =
            null;

        dialog.OnAccept =
            () =>
            {
                var manual =
                    exact?.Text.Trim()
                    ?? "";

                var selected =
                    selector.SelectedItem as string
                    ?? "";

                var version =
                    !string.IsNullOrWhiteSpace(manual)
                        ? manual
                        : selected;

                if(string.IsNullOrWhiteSpace(version))
                {
                    dialog.Error.Text =
                        "Выберите версию из списка или укажите точный тег.";

                    return Task.FromResult(false);
                }

                dialog.Error.Text = "";

                result =
                    new ModuleVersionChoice(
                        version,
                        pin.IsChecked == true);

                return Task.FromResult(true);
            };

        return
            dialog.ShowDialog() == true
                ? result
                : null;
    }


    private bool ConfirmForcedCoreInstall(
        ModuleRow module,
        ModuleRelease candidate)
    {
        var dialog =
            new EditorDialog(
                this,
                "Принудительная установка VPN-core",
                640)
            {
                Height = 500,
                MinHeight = 420,
                ShowInTaskbar = false
            };

        dialog.Note(
            $"{module.DisplayName} {candidate.Version} получен из официального upstream.");

        var warning =
            new TextBlock
            {
                Text =
                    "Для этой версии нет подписанного compatibility-пакета NetCat.",

                TextWrapping =
                    TextWrapping.Wrap,

                FontWeight =
                    FontWeights.SemiBold,

                Margin =
                    new Thickness(0,8,0,10)
            };

        warning.SetResourceReference(
            TextBlock.ForegroundProperty,
            "DangerBrush");

        dialog.Insert(warning);

        dialog.Note(
            "Перед заменой NetCat проверит SHA-256 upstream-архива, состав staging runtime и совместимость с генерируемой конфигурацией.");

        dialog.Note(
            "Текущая версия останется единственной предыдущей версией для отката.");

        dialog.Accept.Content =
            "Установить принудительно";

        dialog.Accept.MinWidth =
            190;

        return
            dialog.ShowDialog() == true;
    }

    private async void SelectModuleVersion_Click(
        object sender,
        RoutedEventArgs e)
    {
        if(ModulesGrid.SelectedItem is not ModuleRow module ||
           !module.SupportsVersionSelection)
        {
            return;
        }

        await VM.RunAsync(
            async ct =>
            {
                VM.UpdateStatus =
                    $"Получаю список версий {module.DisplayName}…";

                var versions =
                    await VM.Updater.ListVersionsAsync(
                        module.Key,
                        200,
                        ct);

                var choice =
                    ShowVersionPicker(
                        module,
                        versions);

                if(choice == null)
                {
                    VM.UpdateStatus =
                        "Выбор версии отменён.";

                    return;
                }

                VM.UpdateStatus =
                    $"Проверяю {module.DisplayName} {choice.Version}…";

                var candidate =
                    await VM.Updater.CheckVersionAsync(
                        module.Key,
                        choice.Version,
                        ct);

                var force =
                    ModuleUpdater.RequiresForceUnreviewed(
                        candidate);

                if(force &&
                   !ConfirmForcedCoreInstall(
                       module,
                       candidate))
                {
                    VM.UpdateStatus =
                        "Принудительная установка отменена.";

                    return;
                }

                VM.UpdateStatus =
                    $"Скачиваю и проверяю {module.DisplayName} {candidate.Version}…";

                var release =
                    await VM.Updater.PrepareSpecificReleaseAsync(
                        candidate,
                        VM.State,
                        ct,
                        forceUnreviewed:force);

                VM.UpdateStatus =
                    $"Устанавливаю {module.DisplayName} {release.Version}…";

                await VM.InstallPreparedModuleAsync(
                    release,
                    ct);

                if(choice.PinAfterInstall)
                    VM.State.PinnedModules.Add(module.Key);
                else
                    VM.State.PinnedModules.Remove(module.Key);

                await VM.SaveAsync();

                VM.WriteLog(
                    $"MODULE_VERSION_SELECTED module={module.Key} version={release.Version} force={force} pinned={choice.PinAfterInstall}");

                VM.UpdateStatus =
                    $"{module.DisplayName} {release.Version} установлен.";

                await VM.CheckUpdatesAsync(ct);
            });
    }

    private void OpenModuleGitHub_Click(object sender, RoutedEventArgs e)
    {
        const string url =
            "https://github.com/akapustyanik/NetCat";

        try
        {
            ExplorerDesktopShell.Open(url);

            VM.UpdateStatus =
                "Открыт GitHub NetCat.";

            VM.WriteLog(
                "UI_ACTION page=modules action=open-github result=ok");
        }
        catch(Exception ex)
        {
            var copied =
                JournalActionFeedback.TryCopy(
                    url,
                    Clipboard.SetText);

            VM.UpdateStatus =
                copied
                    ? "Не удалось открыть браузер. Ссылка на GitHub скопирована."
                    : "Не удалось открыть GitHub: " +
                      ProcessHost.Redact(ex.Message);

            VM.WriteLog(
                "UI_ACTION page=modules action=open-github result=" +
                (copied ? "copied" : "failed"));
        }
    }
    private async void CheckModule_Click(object sender, RoutedEventArgs e) => await VM.CheckUpdatesAsync(lifetime.Token);
    private async void TrustPublisher_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
            "Добавить собственный сертификат NetCat в доверенные корневые сертификаты и издатели текущего пользователя Windows?\n\n" +
            VM.PublisherFingerprint + "\n\nЭто разрешает Windows доверять программам, подписанным этим ключом. Сверьте отпечаток с официальным README NetCat. Сертификат не будет добавлен для других пользователей.",
            "Доверие издателю NetCat", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await VM.RunAsync(async _ =>
        {
            await Task.Run(() => PublisherCertificateTrust.Install(Environment.ProcessPath!));
            VM.RefreshPublisherTrust(); VM.UpdateStatus = "Сертификат добавлен. Автоустановку можно включить отдельно.";
        });
    }
    private void ExportPublisher_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Сертификат (*.cer)|*.cer", FileName = "NetCat-Publisher.cer" };
        if (dialog.ShowDialog(this) != true) return;
        try { PublisherCertificateTrust.Export(dialog.FileName); VM.Status = "Сохранён публичный сертификат NetCat."; }
        catch (Exception ex) { VM.Status = "Не удалось сохранить сертификат: " + ex.Message; }
    }
    private async void RemovePublisherTrust_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Удалить доверие к сертификату NetCat для текущего пользователя Windows? Автоустановка будет выключена.",
            "Доверие издателю NetCat", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await VM.RunAsync(async _ =>
        {
            await Task.Run(() => PublisherCertificateTrust.Remove()); VM.State.AutoUpdateNetCat = false;
            await VM.SaveAsync(); VM.RefreshPublisherTrust();
        });
    }
    private void RequireStopped() { if (VM.Router.Running || VM.Zapret.Running || telegram.Running || VM.TestsBusy) throw new InvalidOperationException("Перед установкой остановите подключения и тесты. Проверять обновления можно в любое время."); }
    private string? pendingNetcatUpdate;
    private async void InstallModule_Click(object sender, RoutedEventArgs e) => await VM.RunAsync(async ct =>
    {
        var selected=VM.Modules.Where(m=>m.Selected&&m.CanSelectUpdate).ToArray();
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
        VM.UpdateStatus="Скачанные файлы готовы. При установке нужные компоненты будут перезапущены автоматически.";
        if(errors.Count>0) VM.UpdateStatus+=" Не удалось скачать: "+string.Join(", ",errors);
    });
    private async void InstallDownloaded_Click(object sender, RoutedEventArgs e)
    {
        string? launch=null;
        await VM.RunAsync(async ct =>
        {
            var errors=new List<string>(); int installed=0;
            foreach(var module in VM.Modules.Where(m=>m.Selected&&m.CanSelectUpdate).ToArray())
            {
                try
                {
                    if(module.Key=="netcat") { RequireStopped(); if(pendingNetcatUpdate!=null) launch=pendingNetcatUpdate; continue; }
                    if(!VM.Updater.HasPrepared(module.Check.Release!)) continue;
                    await VM.InstallPreparedModuleAsync(module.Check.Release!,ct); installed++; VM.WriteLog("Обновлён модуль "+module.DisplayName);
                }
                catch(Exception ex) when(ex is not OperationCanceledException) { errors.Add(module.DisplayName); VM.WriteLog(ex.Message); }
            }
            var rows =
                VM.Modules.ToArray();

            VM.ApplyModuleChecks(rows.Select(row => row.Check with
            {
                Installed = VM.Updater.InstalledVersion(row.Key)
            }).ToArray());
            VM.Refresh(); VM.UpdateStatus=installed>0 ? $"Установлено компонентов: {installed}." : launch!=null ? "NetCat готов к перезапуску." : "Нет выбранных скачанных обновлений.";
            if(errors.Count>0) VM.UpdateStatus+=" Ошибки: "+string.Join(", ",errors);
        });
        if(launch!=null && !VM.WorkCancellation.IsCancellationRequested)
        { try { await PortableUpdate.LaunchAsync(launch); await ExitAsync(); } catch(Exception ex) { VM.Status="Не удалось запустить обновление: "+ex.Message; } }
    }
    private async void RollbackModule_Click(
        object sender,
        RoutedEventArgs e)
    {
        if(ModulesGrid.SelectedItem is not ModuleRow module ||
           !module.CanRollback)
        {
            return;
        }

        var rollbackVersion =
            module.PreviousVersion;

        var confirm =
            MessageBox.Show(
                this,
                $"Откатить {module.DisplayName} с {module.Installed} на {rollbackVersion}?" +
                "\n\nNetCat временно остановит только связанный компонент и автоматически запустит его снова." +
                "\n\nЕсли откат не сможет нормально запустить компонент, NetCat попытается автоматически вернуть исходную версию.",
                "Откат компонента",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

        if(confirm != MessageBoxResult.Yes)
            return;

        await VM.RunAsync(
            async ct =>
            {
                VM.UpdateStatus =
                    $"Откатываю {module.DisplayName} на {rollbackVersion}…";

                await VM.RollbackModuleAsync(
                    module.Key,
                    ct);

                VM.Refresh();

                VM.UpdateStatus =
                    $"{module.DisplayName}: откат выполнен; компонент перезапущен автоматически.";

                await VM.CheckUpdatesAsync(
                    ct);
            });
    }
    private async void PinModule_Click(
        object sender,
        RoutedEventArgs e)
    {
        if(ModulesGrid.SelectedItem is not ModuleRow module)
            return;

        await VM.RunAsync(
            async ct =>
            {
                var pinned =
                    !VM.State.PinnedModules.Contains(
                        module.Key);

                if(pinned)
                    VM.State.PinnedModules.Add(module.Key);
                else
                    VM.State.PinnedModules.Remove(module.Key);

                await VM.SaveAsync();

                VM.UpdateStatus =
                    pinned
                        ? $"{module.DisplayName} {VM.Updater.InstalledVersion(module.Key)} закреплён."
                        : $"{module.DisplayName}: закрепление снято.";

                await VM.CheckUpdatesAsync(ct);
            });
    }
    private async void StartTelegram_Click(
        object sender,
        RoutedEventArgs e)
    {
        var enable =
            !telegram.Running;

        await VM.RunAsync(
            ct =>
                VM.SetTelegramEnabledAsync(
                    enable,
                    ct));
    }
    private async void StopTelegram_Click(object sender, RoutedEventArgs e) => await VM.RunAsync(ct=>VM.SetTelegramEnabledAsync(false,ct));
    private void TelegramLink_Click(object sender, RoutedEventArgs e)
    {
        if (!telegram.TryGetProxyLink(out var link, out _)) { VM.Status="Telegram WS proxy ещё не готов. Дождитесь запуска."; return; }
        VM.Status = JournalActionFeedback.TryCopy(link, Clipboard.SetText)
            ? "Ссылка подключения скопирована. Откройте её в Telegram."
            : "Буфер обмена недоступен. Повторите копирование ссылки Telegram.";
    }
    private void OpenTelegram_Click(object sender, RoutedEventArgs e) => OpenTelegramProxy();
    private void OpenTelegramProxy()
    {
        if (!telegram.TryGetProxyLink(out var link, out var port))
        {
            VM.Status="Telegram WS proxy ещё не готов. Дождитесь запуска.";
            VM.WriteLog("UI_ACTION page=telegram action=open-proxy result=not-ready");
            return;
        }
        var result = journalShell.OpenTelegramProxy(link, port, Clipboard.SetText, out var message);
        VM.Status = message;
        VM.WriteLog("UI_ACTION page=telegram action=open-proxy result=" + JournalResult(result));
    }
    private void ModulesPage_Click(object sender,RoutedEventArgs e) => Pages.SelectedIndex=4;
    private void RoutingPage_Click(object sender,RoutedEventArgs e) => Pages.SelectedIndex=2;
    private void ZapretPage_Click(object sender,RoutedEventArgs e) => Pages.SelectedIndex=3;
    private void OpenModules_Click(object sender, RoutedEventArgs e) => OpenExternal(VM.Bin);
    private bool OpenExternal(
        string target,
        string? journalAction = null)
    {
        if(journalAction != null)
        {
            VM.WriteLog(
                $"UI_ACTION page=log action={journalAction} begin");
        }

        try
        {
            var canonical =
                System.IO.Path.GetFullPath(target);

            var allowed =
                System.IO.Path.GetFullPath(VM.Bin);

            if(!string.Equals(
                   canonical,
                   allowed,
                   StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Открытие внешнего пути не разрешено.");
            }

            if(!Directory.Exists(canonical))
            {
                throw new DirectoryNotFoundException(
                    "Папка модулей не найдена.");
            }

            ExplorerDesktopShell.Open(
                canonical);

            VM.Status =
                "Папка модулей открыта в Проводнике.";

            if(journalAction != null)
            {
                VM.WriteLog(
                    $"UI_ACTION page=log action={journalAction} result=ok method=user-shell");
            }

            return true;
        }
        catch(Exception ex)
        {
            VM.Status =
                "Не удалось открыть: " +
                ProcessHost.Redact(
                    ex.Message);

            if(journalAction != null)
            {
                VM.WriteLog(
                    $"UI_ACTION page=log action={journalAction} result=failed");
            }

            return false;
        }
    }
    private void BaseColor_Click(object sender, RoutedEventArgs e) => PickColor(true);
    private void AccentColor_Click(object sender, RoutedEventArgs e) => PickColor(false);
    private void PickColor(bool primary)
    {
        var picker=new ColorPickerWindow(this,primary?"Основной цвет интерфейса":"Акцентный цвет",primary?VM.State.BaseColor:VM.State.AccentColor,primary);
        if(picker.ShowDialog()==true) { if(primary) VM.State.BaseColor=picker.SelectedHex; else VM.State.AccentColor=picker.SelectedHex; }
    }
    private void Light_Click(object sender, RoutedEventArgs e) => VM.State.BaseColor = "#F4F6F8";
    private void Dark_Click(object sender, RoutedEventArgs e) => VM.State.BaseColor = "#151A22";
    private void AppearancePreview_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Theme.Apply(VM.State);
    }
    private void ResetAppearance_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        VM.State.BaseColor = defaults.BaseColor;
        VM.State.AccentColor = defaults.AccentColor;
        VM.State.PanelBrightness = defaults.PanelBrightness;
        VM.State.HighContrastText = defaults.HighContrastText;
        VM.State.InterfaceScale = defaults.InterfaceScale;
    }
    private AutostartService Startup => new(new WindowsStartupTasks(msg => VM.WriteLog(msg), userFacingJournal: true), Environment.ProcessPath!, System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
    private async Task RefreshAutostartAsync()
    {
        AutostartSwitch.IsEnabled = false;
        try
        {
            var startup = Startup;
            var registered = await Task.Run(() => startup.RegisteredTask);
            AutostartSwitch.IsChecked = await Task.Run(() => startup.Enabled);
            AutostartStatus.Text = registered == null ? "Автозапуск выключен" :
                AutostartSwitch.IsChecked == true ? "Автозапуск: " + registered.Executable :
                "Автозапуск другой копии: " + registered.Executable + ". Включите переключатель, чтобы выбрать эту копию.";
        }
        catch (Exception ex) { AutostartSwitch.IsChecked = false; AutostartStatus.Text = "Автозапуск: " + ex.Message; VM.WriteLog(AutostartStatus.Text); }
        finally { AutostartSwitch.IsEnabled = true; }
    }
    private async void Autostart_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = AutostartSwitch.IsChecked == true;
        if (VM.Busy) { await RefreshAutostartAsync(); return; }
        await VM.RunAsync(async _ =>
        {
            AutostartSwitch.IsEnabled = false;
            try
            {
                var startup = Startup;
                await Task.Run(() => startup.SetAsync(enabled, value =>
                    Dispatcher.InvokeAsync(() => VM.UpdateSettingsAsync(s => s.Autostart = value)).Task.Unwrap()));
                AutostartStatus.Text = enabled ? "Автозапуск включён" : "Автозапуск выключен";
            }
            finally
            {
                await RefreshAutostartAsync();
            }
        });
    }
    private void OpenDiagnosticLog_Click(object sender, RoutedEventArgs e)
    {
        VM.WriteLog("UI_ACTION page=log action=open-log begin");
        try
        {
            var file = System.IO.Path.Combine(VM.Store.Root, "diagnostic.log");
            if (!File.Exists(file)) File.WriteAllText(file, "");
            var result = journalShell.OpenFile(file, VM.Store.Root, Clipboard.SetText, out var message);
            VM.JournalActionStatus = message;
            VM.WriteLog($"UI_ACTION page=log action=open-log result={JournalResult(result)}");
        }
        catch (Exception ex)
        {
            VM.JournalActionStatus = "Не удалось открыть журнал: " + ProcessHost.Redact(ex.Message);
            VM.WriteLog("UI_ACTION page=log action=open-log result=failed");
        }
    }
    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        VM.WriteLog("UI_ACTION page=log action=open-folder begin");
        try
        {
            Directory.CreateDirectory(VM.Store.Root);
            var result = journalShell.OpenFolder(VM.Store.Root, VM.Store.Root, Clipboard.SetText, out var message);
            VM.JournalActionStatus = message;
            VM.WriteLog($"UI_ACTION page=log action=open-folder result={JournalResult(result)}");
        }
        catch (Exception ex)
        {
            VM.JournalActionStatus = "Не удалось открыть папку журнала: " + ProcessHost.Redact(ex.Message);
            VM.WriteLog("UI_ACTION page=log action=open-folder result=failed");
        }
    }
    private async void ApplyTelegramPort_Click(object sender, RoutedEventArgs e) => await VM.RunAsync(async ct =>
    {
        await VM.SaveAsync();
        VM.WriteLog("Порт Telegram применён. Подключите Telegram по новой ссылке.");
    });
    private async Task RunJournalActionAsync(string action, Func<CancellationToken, Task> work)
    {
        if (Interlocked.CompareExchange(ref journalActionRunning, 1, 0) != 0) return;
        VM.WriteLog($"UI_ACTION page=log action={action} begin");
        try
        {
            await VM.RunAsync(work);
            VM.JournalActionStatus = VM.Status;
            VM.WriteLog($"UI_ACTION page=log action={action} result={(VM.Status == "Готово" ? "ok" : "failed")}");
        }
        finally { Interlocked.Exchange(ref journalActionRunning, 0); }
    }
    private static string JournalResult(ShellHandoffResult result) => result switch { ShellHandoffResult.OpenedUnelevatedShell => "ok method=unelevated-shell", ShellHandoffResult.FallbackCopied => "fallback-copied", _ => "failed" };
    private async void StopAll_Click(object sender, RoutedEventArgs e) => await RunJournalActionAsync("stop-all", _ => VM.StopComponentsAsync());
    private async void RetryNetwork_Click(object sender, RoutedEventArgs e) => await RunJournalActionAsync("retry-network", VM.RetryNetworkAsync);
    private async void NetworkInfo_Click(object sender, RoutedEventArgs e) => await RunJournalActionAsync("check-physical-dns", _ => { var n = PhysicalNetwork.Capture(VM.State.PhysicalInterface); VM.WriteLog($"Физический адаптер: {n.Name}; DNS: {n.Dns}; суффиксы: {string.Join(", ", n.Suffixes)}. Прямые локальные домены используют этот DNS."); return Task.CompletedTask; });
    private void TimerTick(object? sender, EventArgs e)
    {
        backgroundTicks++;
        UpdateTraffic(); UpdatePingDisplay(); VM.PollRuntimeState();
    }
    private void UpdateTraffic()
    {
        // Traffic is sampled by the application lifetime service.
        if (!IsVisible || WindowState == WindowState.Minimized || Pages.SelectedIndex != 0) return;

        var snap = VM.TrafficMonitor.CurrentSnapshot;
        var down = snap.DownloadMbps;
        var up = snap.UploadMbps;

        DownloadValue.Text = snap.HasBaseline ? $"{down:F2} Мбит/с" : "— Мбит/с";
        UploadValue.Text = snap.HasBaseline ? $"{up:F2} Мбит/с" : "— Мбит/с";

        var ifaceLabel = string.IsNullOrEmpty(snap.InterfaceName) ? "СЕТЬ" : snap.InterfaceName;
        TrafficText.Text = $"{ifaceLabel}   ↓ {down:F2} Мбит/с     ↑ {up:F2} Мбит/с     Последние 60 секунд";

        traffic.Enqueue(down);
        while (traffic.Count > 60) traffic.Dequeue();

        if (!TrafficCanvas.IsVisible || TrafficCanvas.ActualWidth <= 0 || TrafficCanvas.ActualHeight <= 0) return;
        TrafficCanvas.Children.Clear();
        var values = traffic.ToArray();
        var max = Math.Max(1, values.Max());
        var line = new Polyline { StrokeThickness = 2 };
        line.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        for (int i = 0; i < values.Length; i++)
            line.Points.Add(new Point(i * Math.Max(1, TrafficCanvas.ActualWidth) / 59, 65 - values[i] / max * 60));
        TrafficCanvas.Children.Add(line);
    }
    private void UpdatePingDisplay()
    {
        if (App.IsSmoke || exiting) return;
        if (!VM.Router.VpnRunning || VM.Router.LatencyPort == 0 || VM.InNetworkTransition)
        {
            PingValue.Text = VM.InNetworkTransition ? "… перестройка" : "— мс";
            return;
        }

        if (VM.HealthMonitor?.LastHealthResult is { } result)
        {
            PingValue.Text = result.Success ? $"{result.Milliseconds} мс" : "Нет ответа";
            PingValue.ToolTip = result.Success ? $"HTTP-задержка через подключённый VPN. Интервал между проверками: {Math.Max(1, VM.State.TestIntervalSeconds)} с после завершения предыдущей." : "Проверка через подключённый VPN: " + result.Error;
        }
        else
        {
            PingValue.Text = "… мс";
        }
    }
    private void SaveCurrentPresentationState()
    {
        if (App.IsSmoke || exiting || presentation?.SessionEnding == true || !IsLoaded) return;
        var state = !IsVisible || !ShowInTaskbar ? WindowPresentationState.HiddenToTray :
                    WindowState == WindowState.Maximized ? WindowPresentationState.VisibleMaximized :
                    WindowPresentationState.VisibleNormal;
        if (presentation != null) presentation.SaveActualState(state);
        else VM.Store.SavePresentationState(state);
    }
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (exiting || presentation?.SessionEnding == true) return; e.Cancel = true;
        if (VM.State.MinimizeToTray)
        {
            presentation?.UserRequestedHide();
            if (presentation is { TrayAvailable: false })
            {
                // A transient shell failure must neither strand the window nor change user settings.
                ShowInTaskbar = true;
                if (!IsVisible) Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                VM.Status = "Значок NetCat в трее недоступен. Окно оставлено открытым для управления.";
                return;
            }
            ShowInTaskbar = false;
            Hide();
            if (presentation == null) VM.Store.SavePresentationState(WindowPresentationState.HiddenToTray);
        }
        else _ = ExitAsync();
    }
    public async Task ExitAsync()
    {
        if (exiting) return;
        SaveCurrentPresentationState();
        exiting = true; timer.Stop(); lifetime.Cancel(); VM.WorkCancellation.Cancel(); VM.TestsCancellation.Cancel();
        VM.FlushDesiredState();
        var exitCode = 0;
        try { for (int i = 0; i < 100 && (VM.Busy||VM.TestsBusy); i++) await Task.Delay(100); await VM.StopForExitAsync(); }
        catch (Exception ex) { exitCode = 1; VM.WriteLog("Завершение: " + ex.Message); if (App.IsSmoke) Console.Error.WriteLine(ex); }
        finally
        {
            try { VM.Dispose(); } catch (Exception ex) { VM.WriteLog("Очистка при завершении: " + ex.Message); }
            try {Close();}finally{Application.Current.Shutdown(exitCode);}
        }
    }
    public async Task CaptureScreensAsync(string destination)
    {
        if (!App.IsSmoke) throw new InvalidOperationException("Снимки доступны только в режиме smoke.");
        Directory.CreateDirectory(destination); HeaderSmoke.VerifyDragCaption(this);
        await LiveRulesSmoke.VerifyAsync(VM,destination);
        await StateSmoke.VerifyAsync(destination,VM.Bin);
        await TransactionSmoke.VerifyAsync(destination,VM.Bin);
        var children=TrafficCanvas.Children.Count; var ticks=backgroundTicks;
        Hide(); await Task.Delay(2200);
        if(TrafficCanvas.Children.Count!=children || backgroundTicks<=ticks) throw new InvalidOperationException("Фоновый таймер рисует скрытый график или остановил обслуживание.");
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
        VM.RestoreSmokeDraft();
        // A real WPF dispatcher stress test: producers must not queue one dispatcher operation per line.
        var started = Stopwatch.StartNew();
        await Task.Run(() => Parallel.For(0, 10000, n => VM.WriteLog("Synthetic network error " + n)));
        HeaderSmoke.ClickTab(this, Pages, 6);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
        if (started.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException("UI blocked during log flood.");
        await Task.Delay(500);
        if (VM.Logs.Count > 2000) throw new InvalidOperationException("Unbounded log UI.");
        await File.WriteAllTextAsync(System.IO.Path.Combine(destination,"log-stress-check.txt"), $"10000 lines; UI input dispatched after {started.ElapsedMilliseconds} ms; retained {VM.Logs.Count}; {VM.LogMetrics}");
    }
}
