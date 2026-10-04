using System.Text.Json;
using NetCat.Core;

namespace NetCat.Network;

// Serialized payload prevents a subscriber from changing another reader's configuration.
public sealed record ConfigurationSnapshot(long Revision, string Json)
{
    public AppSettings Read() => JsonSerializer.Deserialize<AppSettings>(Json, JsonSettings.Options)!;
}
public interface IRuntimeConfigurationRepository
{
    AppSettings CurrentSettings { get; }
    event Action<ConfigurationSnapshot>? ConfigurationChanged;
    Task UpdateSettingsAsync(Func<AppSettings, AppSettings> updateAction, CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}
public interface IOpenVpnLearnedRouteRepository
{
    Task UpdateOpenVpnLearnedRoutesAsync(Guid profileId, IReadOnlyList<string> routes, CancellationToken ct = default);
}
public class RuntimeConfigurationRepository : IRuntimeConfigurationRepository, IOpenVpnLearnedRouteRepository
{
    private readonly SettingsStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ConfigurationSnapshot snapshot;
    public ConfigurationSnapshot Snapshot => Volatile.Read(ref snapshot);
    public AppSettings CurrentSettings => Snapshot.Read();
    public event Action<ConfigurationSnapshot>? ConfigurationChanged;
    public Action<Exception>? ProjectionFailed { get; set; }
    public RuntimeConfigurationRepository(SettingsStore store) : this(store.Load(), store) { }
    public RuntimeConfigurationRepository(AppSettings initial, SettingsStore store)
    {
        this.store = store;
        snapshot = new(0, JsonSerializer.Serialize(initial, JsonSettings.Options));
    }
    public async Task UpdateSettingsAsync(Func<AppSettings, AppSettings> updateAction, CancellationToken ct = default)
    {
        ConfigurationSnapshot published;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = updateAction(CurrentSettings);
            var json = JsonSerializer.Serialize(next, JsonSettings.Options);
            ct.ThrowIfCancellationRequested();
            await store.SaveAsync(next).ConfigureAwait(false);
            published = new(snapshot.Revision + 1, json);
            Volatile.Write(ref snapshot, published);
        }
        finally { gate.Release(); }
        foreach (var handler in ConfigurationChanged?.GetInvocationList() ?? [])
        {
            try { ((Action<ConfigurationSnapshot>)handler)(published); }
            catch (Exception ex) { try { ProjectionFailed?.Invoke(ex); } catch { } }
        }
    }
    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        var copy = JsonSettings.Clone(settings);
        return UpdateSettingsAsync(_ => copy, ct);
    }
    public Task UpdateOpenVpnLearnedRoutesAsync(Guid profileId, IReadOnlyList<string> routes, CancellationToken ct = default) =>
        UpdateSettingsAsync(settings =>
        {
            var profile = settings.Profiles.FirstOrDefault(p => p.Id == profileId && p.IsOpenVpn)
                ?? throw new InvalidOperationException("Профиль OpenVPN удалён до сохранения маршрутов.");
            if (routes.Any(route => !OpenVpnRoutes.ValidatePushedRoute(route, profile.AllowPublicPushedRoutes)))
                throw new InvalidDataException("OpenVPN сообщил недопустимый learned route.");
            profile.LearnedRoutes = routes.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            return settings;
        }, ct);
}
