using System.Reflection;
using System.Windows.Threading;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

public sealed class DispatcherCompletionTests
{
    private sealed class UiThread : IDisposable
    {
        private readonly Thread thread;
        public Dispatcher Dispatcher { get; }
        public UiThread()
        {
            var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            thread = new Thread(() => { ready.SetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Dispatcher = ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        public void Dispose()
        {
            Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        }
    }
    private static Task Dispatch(Dispatcher dispatcher, Func<Task> operation) =>
        (Task)typeof(App).GetMethod("RunOnDispatcherAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [dispatcher, operation])!;

    [Fact]
    public async Task AsyncUiExitRemainsPendingUntilItsOperationFinishes()
    {
        using var ui = new UiThread();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Dispatch(ui.Dispatcher, async () => { entered.SetResult(); await finish.Task; });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await ui.Dispatcher.InvokeAsync(() => { }).Task;
            Assert.False(operation.IsCompleted);
            finish.SetResult(); await operation.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { finish.TrySetResult(); }
    }

    [Fact]
    public async Task AsyncUiExitFailureIsReportedToIpcCaller()
    {
        using var ui = new UiThread();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Dispatch(ui.Dispatcher, async () => { await finish.Task; throw new InvalidOperationException("Synthetic shutdown failure"); });
        await ui.Dispatcher.InvokeAsync(() => { }).Task;
        finish.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
