using System.Windows;
using NetCat.Core;
using NetCat.Network;
namespace NetCat.UI;
public partial class App : Application
{
    private Mutex? mutex;
    private NetworkOwner? networkOwner;
    public static bool IsSmoke { get; private set; }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
                NetCat.Updater.DurableUpdate.Recover(AppContext.BaseDirectory);
                if(restoredExecutable)
                {
                    networkOwner?.Dispose();networkOwner=null;mutex?.Dispose();mutex=null;
                    using var restarted=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"NetCat.exe")){UseShellExecute=false});
                    Shutdown();return;
                }
                NetCat.Updater.UpdateCleanup.RetainRecent(diagnostic:startupMessages.Add);
            }
            var store = new SettingsStore(e.Args.Contains("--smoke") ? Path.Combine(Path.GetTempPath(), "NetCat-Smoke-" + Environment.ProcessId) : null);
            var settings = store.Load(); Theme.Apply(settings.BaseColor, settings.AccentColor);
            var runtime=Path.Combine(store.Root,"runtime"); PrivateFiles.ProtectDirectory(runtime);
            try { await OpenVpnService.RecoverAsync(Path.Combine(runtime,"openvpn")); }
            finally
            {
                OpenVpnService.CleanupSecrets(Path.Combine(runtime,"openvpn"));
                PrivateFiles.DeleteSecrets(Path.Combine(runtime,"telegram"),"telegram.json");
                PrivateFiles.DeleteSecrets(runtime,"router.json","router.next.json","xray.json");
                foreach(var probe in Directory.GetDirectories(runtime,"probe-*")) PrivateFiles.DeleteSecrets(probe,"test.json","xray.json");
            }
            var vm = new MainViewModel(store, settings); var window = new MainWindow(vm); MainWindow = window;
            foreach(var message in startupMessages)vm.WriteLog(message);
            if(!IsSmoke && DesktopIdentity.Warning() is {Length:>0} warning) {vm.Status=warning;vm.WriteLog(warning);}
            networkOwner?.Listen(()=>Dispatcher.InvokeAsync(()=>{window.Show();window.WindowState=WindowState.Normal;window.Activate();}).Task);
            if (e.Args.Contains("--smoke")) { window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -20000; window.Top = -20000; }
            window.Show();
            if (e.Args.Contains("--smoke"))
            {
                await Task.Delay(900);
                var destination = e.Args.FirstOrDefault(a => a.StartsWith("--snapshots="))?[12..];
                if (destination != null) await window.CaptureScreensAsync(destination);
                await window.ExitAsync();
            }
        }
        catch (Exception ex) { if (e.Args.Contains("--smoke")) Console.Error.WriteLine(ex); else MessageBox.Show(ex.Message, "NetCat — ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        try {if(MainWindow is MainWindow window)window.VM.Dispose();}
        finally {networkOwner?.Dispose();mutex?.Dispose();base.OnExit(e);}
    }
}
