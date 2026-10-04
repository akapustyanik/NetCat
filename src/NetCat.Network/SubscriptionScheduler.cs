using NetCat.Core;

namespace NetCat.Network;

// Owned by the application ViewModel, including startup hidden in the tray.
public sealed class SubscriptionScheduler(
    Func<IReadOnlyList<Subscription>> read,
    Func<Subscription, CancellationToken, Task> refresh,
    Func<bool> ready,
    Action<Guid, string> failed)
{
    private readonly Dictionary<Guid, DateTimeOffset> attempts = [];
    private readonly SemaphoreSlim gate = new(1);
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;
    public async Task TickAsync(CancellationToken ct)
    {
        if (!ready() || !await gate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            var now = Now();
            foreach (var sub in read())
            {
                ct.ThrowIfCancellationRequested();
                if (sub.UpdateHours <= 0 || sub.UpdatedAt is { } last && now - last < TimeSpan.FromHours(Math.Min(sub.UpdateHours, 8760))) continue;
                if (attempts.TryGetValue(sub.Id, out var tried) && now - tried < TimeSpan.FromMinutes(5)) continue;
                attempts[sub.Id] = now;
                try { await refresh(sub, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { failed(sub.Id, "Не удалось обновить подписку; прежние профили сохранены."); }
            }
        }
        finally { gate.Release(); }
    }
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            do { await TickAsync(ct).ConfigureAwait(false); } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
