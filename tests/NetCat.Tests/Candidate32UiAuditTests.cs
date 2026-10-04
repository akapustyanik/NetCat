using System.Windows;
using System.Windows.Controls;
using NetCat.Core;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

public sealed partial class Candidate32UiAuditTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NetCat-C32-UI-" + Guid.NewGuid().ToString("N"));

    public Candidate32UiAuditTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { }
    }

    private sealed class ShellFixture : IUnelevatedShellLauncher
    {
        public int FileCalls { get; private set; }
        public int FolderCalls { get; private set; }
        public ShellHandoffResult OpenFile(string path, string allowedRoot, Action<string> copyToClipboard, out string message)
        { FileCalls++; Assert.True(File.Exists(path)); Assert.Equal(allowedRoot, Path.GetDirectoryName(path)); message = "opened file through fixture shell"; return ShellHandoffResult.OpenedUnelevatedShell; }
        public ShellHandoffResult OpenFolder(string path, string allowedRoot, Action<string> copyToClipboard, out string message)
        { FolderCalls++; Assert.Equal(allowedRoot, path); message = "opened folder through fixture shell"; return ShellHandoffResult.OpenedUnelevatedShell; }
        public ShellHandoffResult OpenTelegramProxy(string generatedUri, int localPort, Action<string> copyToClipboard, out string message)
        { UnelevatedExplorerShellLauncher.ValidateTelegramProxyUri(generatedUri, localPort); message = "opened Telegram through fixture shell"; return ShellHandoffResult.OpenedUnelevatedShell; }
    }

    private static class Sta
    {
        private static readonly TaskCompletionSource<System.Windows.Threading.Dispatcher> dispatcher = new();
        static Sta()
        {
            var thread = new Thread(() =>
            {
                try
                {
                    if (Application.Current == null)
                    {
                        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                        Application.Current!.Resources.MergedDictionaries.Add(new ResourceDictionary
                        { Source = new Uri("pack://application:,,,/NetCat;component/Theme.xaml", UriKind.Absolute) });
                    }
                    dispatcher.SetResult(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                    System.Windows.Threading.Dispatcher.Run();
                }
                catch (Exception error) { dispatcher.TrySetException(error); }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public static Task Run(Func<Task> action)
        {
            var current = Application.Current?.Dispatcher;
            var target = current is { HasShutdownStarted: false, HasShutdownFinished: false } ? current : dispatcher.Task.GetAwaiter().GetResult();
            return target.InvokeAsync(action).Task.Unwrap();
        }
    }

    [Fact]
    public void ClipboardHandoffRetriesAndRemainsBounded()
    {
        var attempts = 0;
        var waits = 0;
        var copied = JournalActionFeedback.TryCopy("safe path", _ =>
        {
            attempts++;
            if (attempts < 3) throw new InvalidOperationException("clipboard busy");
        }, attempts: 3, wait: _ => waits++);

        Assert.True(copied);
        Assert.Equal(3, attempts);
        Assert.Equal(2, waits);
    }

    [Fact]
    public void ClipboardHandoffReportsFailureAfterBoundedRetry()
    {
        var attempts = 0;
        var copied = JournalActionFeedback.TryCopy("safe path", _ =>
        {
            attempts++;
            throw new InvalidOperationException("clipboard busy");
        }, attempts: 3, wait: _ => { });

        Assert.False(copied);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task JournalDnsButtonClickProducesObservableCompletion()
    {
        await Sta.Run(async () =>
        {
            using var vm =
                new MainViewModel(
                    new SettingsStore(root),
                    new AppSettings());

            var window =
                new MainWindow(vm);

            try
            {
                var button =
                    Assert.IsType<Button>(
                        window.FindName(
                            "JournalDnsButton"));

                Assert.True(
                    button.IsEnabled);

                button.RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));

                const string begin =
                    "UI_ACTION page=log action=check-physical-dns begin";

                const string completed =
                    "UI_ACTION page=log action=check-physical-dns result=ok";

                // Wait for the actual terminal event rather than merely
                // waiting for the first log entry. The "begin" record is
                // intentionally written before the async action completes.
                for(
                    var attempt = 0;
                    attempt < 80 &&
                    !vm.Logs.Any(
                        line =>
                            line.Contains(
                                completed,
                                StringComparison.Ordinal));
                    attempt++)
                {
                    await Task.Delay(25);
                }

                Assert.Contains(
                    vm.Logs,
                    line =>
                        line.Contains(
                            begin,
                            StringComparison.Ordinal));

                Assert.Contains(
                    vm.Logs,
                    line =>
                        line.Contains(
                            completed,
                            StringComparison.Ordinal));

                Assert.Equal(
                    "Готово",
                    vm.Status);
            }
            finally
            {
                window.Close();
            }
        });
    }
    [Fact]
    public async Task JournalOpenButtonsClickUseRestrictedUnelevatedShell()
    {
        await Sta.Run(async () =>
        {
            using var vm = new MainViewModel(new SettingsStore(root), new AppSettings());
            var shell = new ShellFixture(); var window = new MainWindow(vm, shell);
            try
            {
                Assert.IsType<Button>(window.FindName("JournalOpenLogButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsType<Button>(window.FindName("JournalOpenFolderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                for (var attempt = 0; attempt < 20 && vm.Logs.Count < 4; attempt++) await Task.Delay(25);
                Assert.Equal(1, shell.FileCalls); Assert.Equal(1, shell.FolderCalls);
                Assert.Contains("opened folder through fixture shell", vm.JournalActionStatus);
                Assert.Contains(vm.Logs, x => x.Contains("action=open-log result=ok method=unelevated-shell", StringComparison.Ordinal));
                Assert.Contains(vm.Logs, x => x.Contains("action=open-folder result=ok method=unelevated-shell", StringComparison.Ordinal));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task JournalRetryAndStopClicksHaveExactlyOneTerminalResult()
    {
        await Sta.Run(async () =>
        {
            using var vm = new MainViewModel(new SettingsStore(root), new AppSettings());
            var window = new MainWindow(vm);
            try
            {
                Assert.IsType<Button>(window.FindName("JournalRetryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsType<Button>(window.FindName("JournalStopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                for (var attempt = 0; attempt < 120 && (vm.Busy || !vm.Logs.Any(x => x.Contains("action=stop-all result=", StringComparison.Ordinal))); attempt++) await Task.Delay(25);
                Assert.False(vm.Busy);
                Assert.Contains(vm.Logs, x => x.Contains("action=retry-network begin", StringComparison.Ordinal));
                Assert.Contains(vm.Logs, x => x.Contains("action=retry-network result=", StringComparison.Ordinal));
                Assert.Contains(vm.Logs, x => x.Contains("action=stop-all begin", StringComparison.Ordinal));
                Assert.Contains(vm.Logs, x => x.Contains("action=stop-all result=", StringComparison.Ordinal));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void JournalMarkupExposesAutomationIdsAndVisibleActionFeedback()
    {
        var xaml = File.ReadAllText(Path.Combine(RoutingTests.FindRoot(), "src", "NetCat.UI", "MainWindow.xaml"));
        foreach (var id in new[] { "journal-check-dns", "journal-retry-network", "journal-stop-all", "journal-open-log", "journal-open-folder", "journal-action-status" })
            Assert.Contains($"AutomationId=\"{id}\"", xaml);
        Assert.Contains("Text=\"{Binding JournalActionStatus}\"", xaml);
    }
}
