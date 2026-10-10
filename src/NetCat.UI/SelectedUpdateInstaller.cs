using NetCat.Updater;

namespace NetCat.UI;

public sealed record SelectedUpdateResult(int InstalledModules, string? ApplicationJob,
    IReadOnlyList<(ModuleRelease Release, Exception Error)> Errors);

public sealed class SelectedUpdateInstaller(
    Func<ModuleRelease, CancellationToken, Task<string>> prepareApplication,
    Func<ModuleRelease, CancellationToken, Task> prepareModule,
    Func<ModuleRelease, CancellationToken, Task> installModule)
{
    public async Task<SelectedUpdateResult> RunAsync(IEnumerable<ModuleRelease> selected, CancellationToken ct)
    {
        var ready = new List<ModuleRelease>();
        var errors = new List<(ModuleRelease Release, Exception Error)>();
        string? applicationJob = null;
        // Finish network downloads and verification while existing connections remain available.
        foreach (var release in selected)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (release.Key == "netcat") applicationJob = await prepareApplication(release, ct);
                else { await prepareModule(release, ct); ready.Add(release); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add((release, ex)); }
        }
        int installed = 0;
        foreach (var release in ready)
        {
            ct.ThrowIfCancellationRequested();
            try { await installModule(release, ct); installed++; }
            catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add((release, ex)); }
        }
        return new SelectedUpdateResult(installed, applicationJob, errors);
    }
}
