using System.Windows;
using NetCat.Core;
using NetCat.Network;
namespace NetCat.UI;
public partial class App : Application
{
    private Mutex? mutex;
    private NetworkOwner? networkOwner;
    private readonly WindowManager windowManager = new();
    private TrayIconService? trayIconService;
    private MainViewModel? appVm;
    public static bool IsSmoke { get; private set; }
    protected override async void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (e.Args is ["--release-driver", var moduleFolder])
        {
            // Explicit maintenance command: no IPC, connection restoration or UI.
            // It refuses a foreign driver path and any remaining WinDivert client.
            var result = NetCat.Engine.WinDivertDriverCleanup.TryCleanup(moduleFolder);
            Shutdown(result is NetCat.Engine.WinDivertCleanupResult.Removed or NetCat.Engine.WinDivertCleanupResult.NotInstalled
                or NetCat.Engine.WinDivertCleanupResult.ForeignInstallation ? 0 : 3);
            return;
        }
        if(e.Args.Contains(SystemTrafficProbeWorker.Argument))
        {
            // This branch precedes all ownership, IPC, recovery and UI startup.
            if(!SystemTrafficProbeWorker.IsValidInvocation(e.Args, Environment.ProcessPath)) { Shutdown(2); return; }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            TrafficTestResult result;
            try { result = await SystemTrafficProbe.MeasureAsync(timeout.Token); }
            catch(Exception ex) { result = TrafficTestResult.Failed("Проверка TUN недоступна (" + ex.GetType().Name + ")"); }
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
            await Console.Out.FlushAsync();
            Shutdown();
            return;
        }
        if (e.Args.Contains("--exit") || e.Args.Contains("--stop"))
        {
            var exited = await NetworkOwner.ExitExistingAsync();
            Shutdown(exited ? 0 : 1);
            return;
        }
        if(e.Args is ["--apply-update",var job])
        {
            try { await NetCat.Updater.UpdateChannel.ReceiveAndApplyAsync(job); Shutdown(0); }
            catch(Exception ex) { MessageBox.Show(ex.Message,"NetCat — обновление не выполнено");Shutdown(1); }
            return;
        }
        IsSmoke=e.Args.Contains("--smoke");
        if(!IsSmoke)
        {
            try {networkOwner=new NetworkOwner();}
            catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException) {MessageBox.Show("Не удалось получить владение сетью: "+ex.Message,"NetCat");Shutdown(1);return;}
            if(!networkOwner.Acquired)
            {
                if(!await NetworkOwner.ShowExistingAsync()) MessageBox.Show("NetCat уже управляет сетью в другой Windows-сессии или от имени другого пользователя.","NetCat");
                Shutdown();return;
            }
        }
        if(!IsSmoke && File.Exists(Path.Combine(AppContext.BaseDirectory,"metadata","update.lock")))
        {
            try { using var probe=new FileStream(Path.Combine(AppContext.BaseDirectory,"metadata","update.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None); }
            catch(IOException) { MessageBox.Show("NetCat обновляется. Дождитесь автоматического перезапуска.","NetCat");Shutdown();return; }
        }
        mutex = new Mutex(true, IsSmoke ? "Local\\NetCat.Focus.Smoke."+Environment.ProcessId : "Local\\NetCat.Focus", out var created);
        if (!created) { MessageBox.Show("Уже запущена предыдущая версия NetCat. Откройте её из системного трея.", "NetCat"); Shutdown(); return; }
        try
        {
            var startupMessages=new List<string>();
            if(!IsSmoke)
            {
                var pending=NetCat.Updater.DurableUpdate.Read(AppContext.BaseDirectory);
                bool restoredExecutable=pending?.Phase==NetCat.Updater.UpdatePhase.Applying && pending.Operations.Any(o=>o.Target=="NetCat.exe");
                if(restoredExecutable)
                {
                    var recoveryJob = await NetCat.Updater.UpdateChannel.PrepareRecoveryAsync(AppContext.BaseDirectory);
                    await NetCat.Updater.UpdateChannel.LaunchAsync(recoveryJob);
                    Shutdown();return;
                }
                NetCat.Updater.DurableUpdate.Recover(AppContext.BaseDirectory);
                NetCat.Updater.UpdateCleanup.RetainRecent(diagnostic:startupMessages.Add);
            }
            if(!IsSmoke)
            {
                using var recoveryUpdater=new NetCat.Updater.ModuleUpdater(Path.Combine(AppContext.BaseDirectory,"modules"));
                recoveryUpdater.RecoverInterruptedInstalls();
            }
            var store = new SettingsStore(e.Args.Contains("--smoke") ? Path.Combine(Path.GetTempPath(), "NetCat-Smoke-" + Environment.ProcessId) : null);
            var settings = store.Load(); Theme.Apply(settings);
            var runtime=Path.Combine(store.Root,"runtime"); PrivateFiles.ProtectDirectory(runtime);
            try { await OpenVpnService.RecoverAsync(Path.Combine(runtime,"openvpn")); }
            finally
            {
                OpenVpnService.CleanupSecrets(Path.Combine(runtime,"openvpn"));
                PrivateFiles.DeleteSecrets(Path.Combine(runtime,"telegram"),"telegram.json");
                PrivateFiles.DeleteSecrets(runtime,"router.json","router.next.json","xray.json");
                foreach(var probe in Directory.GetDirectories(runtime,"probe-*")) PrivateFiles.DeleteSecrets(probe,"test.json","xray.json");
            }
            var vm = new MainViewModel(store, settings);
            appVm = vm;
            vm.ApplicationUpdatePrepared += job => Dispatcher.BeginInvoke(new Action(async () =>
            {
                // Queue after the maintenance callback finishes so shutdown can
                // drain that callback without waiting on its own completion.
                if (!vm.CanAutomaticallyUpdate) { vm.AutomaticUpdateLaunchFailed(); return; }
                try
                {
                    vm.Busy = true;
                    await NetCat.Updater.PortableUpdate.LaunchAsync(job);
                    vm.Busy = false;
                    if (windowManager.CurrentWindow is MainWindow updateWindow) await updateWindow.ExitAsync();
                    else { await vm.StopForExitAsync(); Shutdown(0); }
                }
                catch (Exception ex)
                {
                    vm.Busy = false; vm.AutomaticUpdateLaunchFailed();
                    vm.UpdateStatus = "Автообновление NetCat не выполнено: " + ex.Message; vm.WriteLog(vm.UpdateStatus);
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
            if (!IsSmoke) Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerChanged;
            if (!IsSmoke && settings.RestoreConnectionsOnStartup)
            {
                vm.RuntimeCoordinator.RequestReconcile(ReconcileReason.Startup);
            }
            foreach(var message in startupMessages)vm.WriteLog(message);
            if(!IsSmoke && DesktopIdentity.Warning() is {Length:>0} warning) {vm.Status=warning;vm.WriteLog(warning);}

            bool isAutostart = e.Args.Contains("--autostart");
            var presentation = new WindowPresentationSession(store, isAutostart, settings.MinimizeToTray);
            windowManager.Presentation = presentation;
            bool trayAvailable = IsSmoke;
            const string trayUnavailableStatus = "Значок NetCat в трее недоступен. Окно оставлено открытым для управления.";
            if (!IsSmoke)
            {
                try { trayIconService = new TrayIconService(
                    openWindow: () => Dispatcher.InvokeAsync(() => windowManager.ShowOrActivate(vm)),
                    exitApp: () => Dispatcher.InvokeAsync(async () =>
                    {
                        if (windowManager.CurrentWindow is { } win)
                        {
                            await win.ExitAsync();
                        }
                        else
                        {
                            await vm.StopForExitAsync();
                            Shutdown(0);
                        }
                    }),
                    getCommands: () => new TrayCommand[] {
                        new("VPN", vm.Router.VpnRequested, vm.Idle, () => vm.UserRequestedVpnChange(!vm.Router.VpnRequested)),
                        new("OpenVPN", vm.DesiredState.OpenVpnEnabled, vm.Idle && (vm.OpenVpnProfile != null || vm.DesiredState.OpenVpnEnabled), () => vm.UserRequestedOpenVpnChange(!vm.DesiredState.OpenVpnEnabled)),
                        new("Zapret", vm.Zapret.Running, vm.Idle, () => vm.UserRequestedZapretChange(!vm.Zapret.Running)),
                        new("Telegram · WS proxy", vm.TelegramRunning, vm.Idle, () => _ = vm.SetTelegramEnabledAsync(!vm.TelegramRunning, CancellationToken.None))
                    },
                    diagnostic: vm.WriteLog,
                    availabilityChanged: available =>
                    {
                        presentation.TrayAvailable = available;
                        if (!available)
                        {
                            vm.Status = trayUnavailableStatus;
                            windowManager.ShowForTrayRecovery(vm);
                        }
                        else if (vm.Status == trayUnavailableStatus) vm.Status = "Готово";
                    }
                ); trayAvailable = trayIconService.TrayVisible; }
                catch (Exception ex)
                {
                    vm.WriteLog("TRAY state=failed reason=" + ex.GetType().Name);
                    vm.Status = trayUnavailableStatus;
                }
            }

            networkOwner?.Listen(
                () => Dispatcher.InvokeAsync(() => windowManager.ShowMainWindow(vm)).Task,
                () => RunOnDispatcherAsync(Dispatcher, async () =>
                {
                    if (windowManager.CurrentWindow is MainWindow mw) await mw.ExitAsync();
                    else { await vm.StopForExitAsync(); Shutdown(0); }
                }));

            presentation.TrayAvailable = trayAvailable;
            var presentationState = presentation.PreferredState;

            var d = vm.DesiredState;
            vm.WriteLog($"APP_START\n  version={BuildIdentity.Version}\n  candidate={BuildIdentity.Candidate}\n  pid={Environment.ProcessId}\n  exe={Environment.ProcessPath}\n  mode={(isAutostart ? "autostart" : "manual")}\n  presentation={presentationState}\n  desired vpn={(d.MainVpnEnabled ? "on" : "off")} tun={(d.TunEnabled ? "on" : "off")} zapret={(d.ZapretEnabled ? "on" : "off")} openvpn={(d.OpenVpnEnabled ? "on" : "off")}");

            if (e.Args.Contains("--smoke"))
            {
                var window = windowManager.GetOrCreateMainWindow(vm);
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -20000;
                window.Top = -20000;
                window.Show();

                await Task.Delay(900);
                var destination = e.Args.FirstOrDefault(a => a.StartsWith("--snapshots="))?[12..];
                if (destination != null) await window.CaptureScreensAsync(destination);
                await window.ExitAsync();
            }
            else if (trayAvailable && isAutostart && presentationState == WindowPresentationState.HiddenToTray && presentation.StartHidden)
            {
                // Headless hidden autostart: MainWindow is not instantiated until tray or IPC show
                vm.WriteLog("WINDOW state=hidden reason=autostart tray=available");
            }
            else
            {
                var window = windowManager.GetOrCreateMainWindow(vm);
                window.WindowState = presentationState == WindowPresentationState.VisibleMaximized ? WindowState.Maximized : WindowState.Normal;
                if (trayAvailable) windowManager.ShowMainWindow(vm);
                else { vm.Status = trayUnavailableStatus; windowManager.ShowForTrayRecovery(vm); }
            }
        }
        catch (Exception ex) { if (e.Args.Contains("--smoke")) Console.Error.WriteLine(ex); else MessageBox.Show(ex.Message, "NetCat — ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
    private static Task RunOnDispatcherAsync(System.Windows.Threading.Dispatcher dispatcher, Func<Task> operation) =>
        dispatcher.InvokeAsync(operation).Task.Unwrap();

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (appVm is { } vm && !IsSmoke)
        {
            var finalState = windowManager.CurrentWindow == null || !windowManager.CurrentWindow.IsVisible ? WindowPresentationState.HiddenToTray :
                             windowManager.CurrentWindow.WindowState == WindowState.Maximized ? WindowPresentationState.VisibleMaximized :
                             WindowPresentationState.VisibleNormal;
            windowManager.Presentation?.EndSession(finalState);
            vm.FlushDesiredState();
            vm.FlushInterfaceScalePreference();
            vm.FlushAppearancePreference();
        }
    }
    private void OnPowerChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    { if (e.Mode == Microsoft.Win32.PowerModes.Resume) appVm?.NetworkMonitor.Signal(); }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerChanged;
            trayIconService?.Dispose();
            appVm?.Dispose();
        }
        finally
        {
            // Secondary SHOW/EXIT helpers and smoke windows must not touch drivers.
            if (!IsSmoke && networkOwner?.Acquired == true)
                NetCat.Engine.WinDivertDriverCleanup.TryCleanup(Path.Combine(AppContext.BaseDirectory, "modules"));
            networkOwner?.Dispose();mutex?.Dispose();base.OnExit(e);
        }
    }
}
