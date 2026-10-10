using NetCat.Core;
using NetCat.Updater;

namespace NetCat.UI;

/// <summary>Authenticates the updater before shutdown; failed preparation leaves live connections untouched.</summary>
public sealed class ApplicationUpdateHandoff
{
    private readonly ApplicationUpdateResumeStore resumeStore;
    private readonly Func<string, CancellationToken, Task<UpdateJob>> inspect;
    private readonly Func<string, string, Task> launch;
    private UpdateJob? activeJob;

    public ApplicationUpdateHandoff(ApplicationUpdateResumeStore resumeStore,
        Func<string, CancellationToken, Task<UpdateJob>>? inspect = null,
        Func<string, string, Task>? launch = null)
    {
        this.resumeStore = resumeStore;
        this.inspect = inspect ?? UpdateChannel.ReadLaunchJobAsync;
        this.launch = launch ?? PortableUpdate.LaunchAsync;
    }

    public void RecordSuccessfulShutdown() => PortableUpdate.RecordSuccessfulShutdown(
        activeJob ?? throw new InvalidOperationException("Нет активного задания обновления."));

    public async Task RunAsync(string jobPath, string sourceVersion, bool telegramEnabled,
        Func<Task> persistIntent, Func<Task> exit, CancellationToken ct)
    {
        var job = await inspect(jobPath, ct);
        await persistIntent();
        ct.ThrowIfCancellationRequested();
        var token = resumeStore.Create(job.Root, PortableUpdate.ResumeIdentity(job), sourceVersion, job.Version, telegramEnabled);
        activeJob = job with { ResumeToken = token };
        try
        {
            ct.ThrowIfCancellationRequested();
            // Launch returns only after the authenticated helper has accepted the verified package.
            await launch(jobPath, token);
            await exit();
        }
        catch
        {
            resumeStore.Cancel(token);
            throw;
        }
        finally { activeJob = null; }
    }
}
