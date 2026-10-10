using System.Windows.Threading;
using NetCat.Core;

namespace NetCat.UI;

// Application-owned work must continue even when hidden autostart has not
// created a MainWindow. All callbacks run on the creating UI dispatcher.
public sealed class ApplicationMaintenanceScheduler : IDisposable
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<AppSettings> settings;
    private readonly Func<bool> ready, testsIdle;
    private readonly Func<CancellationToken, Task> testProfiles, checkUpdates;
    private readonly Action<Exception> failed;
    private readonly Func<DateTime> now;
    private readonly Func<CancellationToken, Task> maintain;
    private Task pending = Task.CompletedTask;
    private bool busy, disposed;
    public DateTime NextProfileTest { get; private set; }
    public DateTime NextModuleCheck { get; private set; }

    public ApplicationMaintenanceScheduler(Func<AppSettings> settings, Func<bool> ready, Func<bool> testsIdle,
        Func<CancellationToken, Task> testProfiles, Func<CancellationToken, Task> checkUpdates,
        Action<Exception> failed, Func<DateTime>? now = null, Func<CancellationToken, Task>? maintain = null)
    {
        this.settings = settings; this.ready = ready; this.testsIdle = testsIdle;
        this.testProfiles = testProfiles; this.checkUpdates = checkUpdates; this.failed = failed;
        this.now = now ?? (() => DateTime.Now);
        this.maintain = maintain ?? (_ => Task.CompletedTask);
        NextProfileTest = NextModuleCheck = this.now();
        timer.Tick += async (_, _) => await TickAsync();
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        timer.Start();
    }

    public Task TickAsync()
    {
        if (disposed || busy || !ready()) return Task.CompletedTask;
        busy = true;
        return pending = RunTickAsync();
    }

    private async Task RunTickAsync()
    {
        try
        {
            if (settings().AutoTest && testsIdle() && now() >= NextProfileTest)
            {
                try { await testProfiles(lifetime.Token); }
                finally { NextProfileTest = now().AddSeconds(Math.Max(1, settings().TestIntervalSeconds)); }
            }
            lifetime.Token.ThrowIfCancellationRequested();
            if ((settings().CheckModuleUpdates || settings().AutoUpdateNetCat) && now() >= NextModuleCheck)
            {
                NextModuleCheck = now().AddHours(6);
                await checkUpdates(lifetime.Token);
            }
            await maintain(lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { failed(ex); }
        finally { busy = false; }
    }

    public async Task StopAsync()
    {
        Dispose();
        await pending;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        lifetime.Cancel();
        _ = pending.ContinueWith(_ => lifetime.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
