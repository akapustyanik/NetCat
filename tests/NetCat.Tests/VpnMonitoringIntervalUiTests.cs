using System.Reflection;
using System.Windows.Controls;
using NetCat.Core;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

public sealed partial class Candidate32UiAuditTests
{
    private static ApplicationMaintenanceScheduler Maintenance(MainViewModel vm) =>
        (ApplicationMaintenanceScheduler)typeof(MainViewModel).GetField("maintenance", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public Task ViewModelNormalizesInvalidDraftIntervalToOneSecond(int seconds) => Sta.Run(async () =>
    {
        var store = new SettingsStore(root);
        using var vm = new MainViewModel(store, new AppSettings());
        vm.State.TestIntervalSeconds = seconds;
        await vm.SaveAsync();
        Assert.Equal(1, vm.State.TestIntervalSeconds);
        Assert.Equal(1, new SettingsStore(root).Load().TestIntervalSeconds);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(15)]
    public Task SavedMonitorIntervalRestoresThroughActualViewModel(int seconds) => Sta.Run(async () =>
    {
        var store = new SettingsStore(root);
        using (var vm = new MainViewModel(store, new AppSettings()))
        {
            vm.State.TestIntervalSeconds = seconds;
            await vm.SaveAsync();
            Assert.Equal(seconds, vm.State.TestIntervalSeconds);
            Assert.Equal(seconds, vm.ConfigRepository.CurrentSettings.TestIntervalSeconds);
        }
        var restartedStore = new SettingsStore(root);
        using var restarted = new MainViewModel(restartedStore, restartedStore.Load());
        Assert.Equal(seconds, restarted.State.TestIntervalSeconds);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(15)]
    public Task BackgroundProfileTestSchedulesSelectedIntervalAfterCompletion(int seconds) => Sta.Run(async () =>
    {
        using var vm = new MainViewModel(new SettingsStore(root), new AppSettings { AutoTest = true, TestIntervalSeconds = seconds, CheckModuleUpdates = false });
        var before = DateTime.Now;
        await Maintenance(vm).TickAsync();
        var after = DateTime.Now;
        Assert.InRange(Maintenance(vm).NextProfileTest, before.AddSeconds(seconds), after.AddSeconds(seconds));
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(15)]
    public Task BackgroundTicksAndManualRequestsDoNotQueueWhileProfileBatchIsRunning(int seconds) => Sta.Run(async () =>
    {
        using var vm = new MainViewModel(new SettingsStore(root), new AppSettings { AutoTest = true, TestIntervalSeconds = seconds, CheckModuleUpdates = false });
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var running = vm.RunTestsAsync(async ct => { calls++; await complete.Task.WaitAsync(ct); });
        try
        {
            var due = Maintenance(vm).NextProfileTest;
            for (int i = 0; i < 20; i++)
            {
                await Maintenance(vm).TickAsync();
                await vm.RunTestsAsync(_ => { calls++; return Task.CompletedTask; });
            }
            Assert.True(vm.TestsBusy); Assert.Equal(1, calls); Assert.Equal(due, Maintenance(vm).NextProfileTest);
            complete.SetResult(); await running;
            Assert.False(vm.TestsBusy); Assert.Equal(1, calls);
        }
        finally { complete.TrySetResult(); await running; }
    });

    [Fact]
    public Task IntervalTextBoxCommitsOneSecondThroughBindingAndSave() => Sta.Run(async () =>
    {
        using var vm = new MainViewModel(new SettingsStore(root), new AppSettings());
        var window = new MainWindow(vm);
        try
        {
            var pages = (TabControl)window.FindName("Pages"); pages.SelectedIndex = 1;
            ArrangeProbeUx(window, 820);
            var input = Assert.Single(ProbeUxChildren<TextBox>(window.Content as System.Windows.DependencyObject ?? window),
                t => t.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == "State.TestIntervalSeconds");
            input.Text = "1"; input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            Assert.False(System.Windows.Controls.Validation.GetHasError(input));
            await vm.SaveAsync(); Assert.Equal(1, new SettingsStore(root).Load().TestIntervalSeconds);
        }
        finally { window.Close(); }
    });
}
