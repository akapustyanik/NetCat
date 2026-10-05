using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

public sealed partial class Candidate32UiAuditTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("tg://proxy?server=192.0.2.1&port=1443&secret=dd00000000000000000000000000000000")]
    public void TelegramInvalidProxyLinkDoesNotCrashOrCopy(string link)
    {
        var clipboardCalls=0;
        var launcher=new UnelevatedExplorerShellLauncher();
        var result=launcher.OpenTelegramProxy(link,1443,_=>clipboardCalls++,out var message);
        Assert.Equal(ShellHandoffResult.Failed,result);
        Assert.Equal(0,clipboardCalls);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public void TelegramEmptyClipboardFallbackReportsFailureWithoutThrowing()
    {
        var calls=0;
        Assert.False(JournalActionFeedback.TryCopy("",_=>calls++));
        Assert.Equal(0,calls);
    }

    private sealed class UnexpectedTelegramShell : IUnelevatedShellLauncher
    {
        public int Calls;
        public ShellHandoffResult OpenTelegramProxy(string link,int port,Action<string> copy,out string message)
        { Calls++; UnelevatedExplorerShellLauncher.ValidateTelegramProxyUri(link,port); message="unexpected"; return ShellHandoffResult.OpenedUnelevatedShell; }
        public ShellHandoffResult OpenFile(string path,string root,Action<string> copy,out string message)=>throw new NotSupportedException();
        public ShellHandoffResult OpenFolder(string path,string root,Action<string> copy,out string message)=>throw new NotSupportedException();
    }

    [Fact]
    public async Task TelegramStartupClickDoesNotHandOffAnUnreadyProcess()
    {
        await Sta.Run(async()=>
        {
            using var vm=new MainViewModel(new SettingsStore(root),new AppSettings());
            var launcher=new UnexpectedTelegramShell();
            var window=new MainWindow(vm,launcher);
            try
            {
                var process=(ProcessHost)typeof(NetCat.Network.TelegramService).GetField("process",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(vm.Telegram)!;
                // A real trusted child is alive, but has no listener/link yet:
                // the exact state exposed while the updated proxy imports.
                process.Start(Path.Combine(RoutingTests.ModuleRoot,"tg-runtime","NetCat.Telegram.exe"),["-I","-B","-c","import time; time.sleep(30)"]);
                Assert.True(vm.Telegram.Running);
                Assert.Equal("",vm.Telegram.Link);
                Assert.False(vm.TelegramReady);
                var content=(FrameworkElement)window.Content;
                content.Measure(new Size(1120,890));
                content.Arrange(new Rect(0,0,1120,890));
                content.UpdateLayout();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.DataBind);
                typeof(MainViewModel).GetMethod("NotifyState",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!.Invoke(vm,[]);
                var buttons=new[]{Assert.IsType<Button>(window.FindName("HomeTelegramConnectButton")),Assert.IsType<Button>(window.FindName("ModuleTelegramConnectButton"))};
                foreach(var button in buttons) button.GetBindingExpression(UIElement.IsEnabledProperty)!.UpdateTarget();
                Assert.All(buttons,b=>Assert.False(b.IsEnabled));
                // A queued click must also be rejected after the binding check.
                typeof(MainWindow).GetMethod("OpenTelegram_Click",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[buttons[0],new RoutedEventArgs()]);
                Assert.Equal(0,launcher.Calls);
                Assert.Contains("Telegram",vm.Status);
                vm.PollRuntimeState();
                // Publish the same immutable connection used after native
                // listener readiness (covered separately below). Process PID
                // stays the same, so projection must observe readiness itself.
                var connectionType=typeof(TelegramService).GetNestedType("ProxyConnection",BindingFlags.NonPublic)!;
                var ready=Activator.CreateInstance(connectionType,[1443,"tg://proxy?server=127.0.0.1&port=1443&secret=dd00000000000000000000000000000000"]);
                typeof(TelegramService).GetField("connection",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(vm.Telegram,ready);
                Assert.True(vm.TelegramReady);
                vm.PollRuntimeState();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.DataBind);
                Assert.All(buttons,b=>Assert.True(b.IsEnabled));
                foreach(var button in buttons) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(2,launcher.Calls);
                await vm.Telegram.StopAsync();
                Assert.False(vm.Telegram.Running);
                // A queued module-page click after Stop/update must be harmless.
                typeof(MainWindow).GetMethod("OpenTelegram_Click",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[buttons[1],new RoutedEventArgs()]);
                typeof(MainWindow).GetMethod("TelegramLink_Click",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[buttons[1],new RoutedEventArgs()]);
                Assert.Equal(2,launcher.Calls);
            }
            finally { window.Close(); }
        });
    }
}

[Collection("Candidate18 native resources")]
public sealed class TelegramConnectionReadinessTests
{
    [Fact]
    public async Task TelegramProxyPublishesReadyListenerAndRetiresLinkOnRestart()
    {
        var root=Path.Combine(Path.GetTempPath(),"NetCat-Tg-Ready-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var service=new TelegramService(RoutingTests.ModuleRoot,root);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Assert.False(service.Ready);
            Assert.False(service.TryGetProxyLink(out var empty,out var emptyPort));
            Assert.Equal("",empty); Assert.Equal(0,emptyPort);
            var firstPort=OpenVpnService.FreePort();
            await service.StartAsync(new AppSettings { TelegramWsPort=firstPort,TelegramWsSecret=new string('0',32) },deadline.Token);
            Assert.True(service.Ready);
            Assert.NotEqual(0,LocalListener.Owner(firstPort));
            Assert.True(service.TryGetProxyLink(out var first,out var firstPublishedPort));
            Assert.Equal(firstPort,firstPublishedPort);
            UnelevatedExplorerShellLauncher.ValidateTelegramProxyUri(first,firstPublishedPort);
            var stop=service.StopAsync();
            Assert.False(service.Ready);
            Assert.Equal("",service.Link);
            Assert.False(service.TryGetProxyLink(out _,out _));
            await stop;
            var secondPort=PortStartup.Distinct(OpenVpnService.FreePort,firstPort);
            await service.StartAsync(new AppSettings { TelegramWsPort=secondPort,TelegramWsSecret=new string('1',32) },deadline.Token);
            Assert.True(service.Ready);
            Assert.NotEqual(0,LocalListener.Owner(secondPort));
            Assert.True(service.TryGetProxyLink(out var second,out var secondPublishedPort));
            Assert.Equal(secondPort,secondPublishedPort);
            Assert.NotEqual(first,second);
            UnelevatedExplorerShellLauncher.ValidateTelegramProxyUri(second,secondPublishedPort);
            await service.StopAsync();
            Assert.False(service.Ready); Assert.False(service.Running);
            Assert.Equal(0,LocalListener.Owner(secondPort));
        }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }

    [Fact]
    public async Task TelegramCancelledStartupNeverPublishesProxyLink()
    {
        var root=Path.Combine(Path.GetTempPath(),"NetCat-Tg-Cancel-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var service=new TelegramService(RoutingTests.ModuleRoot,root);
            using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.StartAsync(new AppSettings { TelegramWsPort=OpenVpnService.FreePort() },cancelled.Token));
            Assert.False(service.Running); Assert.False(service.Ready);
            Assert.Equal("",service.Link); Assert.False(service.TryGetProxyLink(out _,out _));
            Assert.False(File.Exists(Path.Combine(root,"telegram.json")));
        }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
}
