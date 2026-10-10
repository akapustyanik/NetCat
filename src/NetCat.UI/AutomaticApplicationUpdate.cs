using NetCat.Engine;
using NetCat.Updater;

namespace NetCat.UI;

public sealed class AutomaticApplicationUpdate
{
    private readonly Func<bool> allowed;
    private readonly Func<ModuleRelease?> release;
    private readonly Func<ModuleRelease, CancellationToken, Task<string>> prepare;
    private readonly Action<string> ready;
    private readonly Action<Exception> failed;
    private readonly Func<DateTime> now;
    private string? prepared;
    private DateTime retry;
    private bool busy, queued;

    public AutomaticApplicationUpdate(Func<bool> allowed, Func<ModuleRelease?> release,
        Func<ModuleRelease, CancellationToken, Task<string>> prepare, Action<string> ready,
        Action<Exception> failed, Func<DateTime>? now = null)
    {
        this.allowed = allowed; this.release = release; this.prepare = prepare;
        this.ready = ready; this.failed = failed; this.now = now ?? (() => DateTime.UtcNow);
    }

    public async Task TickAsync(CancellationToken ct)
    {
        if (busy || queued || now() < retry || !allowed()) return;
        var candidate = release(); if (candidate == null) return;
        busy = true;
        try
        {
            ct.ThrowIfCancellationRequested();
            prepared ??= await prepare(candidate, ct);
            ct.ThrowIfCancellationRequested();
            if (!allowed()) return;
            queued = true; ready(prepared);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { retry = now().AddMinutes(5); failed(ex); }
        finally { busy = false; }
    }

    public void LaunchFailed() { queued = false; retry = now().AddMinutes(5); }
}
