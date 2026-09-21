using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetCat.Core;
public class SettingsStore
{
    public string Root { get; }
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1);
    public SettingsStore(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetCat");
        Directory.CreateDirectory(Root);
        path = Path.Combine(Root, "settings.dpapi");
    }
    public AppSettings Load()
    {
        if (!File.Exists(path)) return new();
        try { var settings=JsonSerializer.Deserialize<AppSettings>(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser), JsonSettings.Options) ?? new(); SettingsValidation.RepairSelections(settings); return settings; }
        catch (Exception e) { throw new InvalidDataException("Не удалось прочитать настройки. Исходный файл сохранён; автоматический сброс не выполняется.", e); }
    }
    public virtual async Task SaveAsync(AppSettings settings)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings, JsonSettings.Options)), null, DataProtectionScope.CurrentUser);
        await gate.WaitAsync();
        try { await File.WriteAllBytesAsync(path + ".new", bytes); File.Move(path + ".new", path, true); }
        finally { gate.Release(); }
    }
}
