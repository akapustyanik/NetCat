using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetCat.Core;
public class SettingsStore
{
    public string Root { get; }
    private readonly string path;
    private readonly string backupPath;
    public bool RecoveredFromBackup { get; private set; }
    private readonly string uiPath;
    private readonly string desiredPath;
    private readonly SemaphoreSlim gate = new(1);
    private readonly object stateLock = new();
    public event Action<string>? Diagnostic;
    public bool DesiredRecoveredFromBackup { get; private set; }
    private void ReportStorage(string message)
    {
        System.Diagnostics.Trace.WriteLine(message);
        if (Diagnostic is { } handlers)
            foreach (Action<string> handler in handlers.GetInvocationList())
                try { handler(message); } catch { /* Diagnostics cannot break shutdown. */ }
    }

    public SettingsStore(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetCat");
        InterfaceScalePreference = new InterfaceScalePreference(Root);
        AppearancePreference = new AppearancePreference(Root);
        Directory.CreateDirectory(Root);
        path = Path.Combine(Root, "settings.dpapi");
        backupPath = path + ".bak";
        uiPath = Path.Combine(Root, "ui-state.json");
        desiredPath = Path.Combine(Root, "desired-state.json");
    }
    public InterfaceScalePreference InterfaceScalePreference { get; }
    public AppearancePreference AppearancePreference { get; }
    private static void TryDeleteTemporary(string temporary)
    {
        try { File.Delete(temporary); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static AppSettings ReadProtectedSettings(string file)
    {
        var plaintext = ProtectedData.Unprotect(
            File.ReadAllBytes(file),
            null,
            DataProtectionScope.CurrentUser);

        var settings = JsonSerializer.Deserialize<AppSettings>(
            plaintext,
            JsonSettings.Options)
            ?? throw new InvalidDataException(
                "Защищённый файл настроек содержит null.");

        SettingsValidation.RepairSelections(settings);
        return settings;
    }

    private AppSettings RestoreBackup()
    {
        // Validate and decrypt the backup before touching the primary.
        var restored = ReadProtectedSettings(backupPath);

        var temporary = path + ".restore-" +
            Guid.NewGuid().ToString("N") + ".new";

        try
        {
            File.Copy(backupPath, temporary);

            if (File.Exists(path))
            {
                // Keep the damaged original for diagnostics.
                var damaged = path + ".corrupt-" +
                    DateTime.UtcNow.ToString("yyyyMMddHHmmss") +
                    "-" + Guid.NewGuid().ToString("N");

                File.Replace(
                    temporary,
                    path,
                    damaged,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, path);
            }

            RecoveredFromBackup = true;
            return restored;
        }
        finally
        {
            TryDeleteTemporary(temporary);
        }
    }

    public AppSettings Load()
    {
        var settings = LoadSettings();
        settings.InterfaceScale = InterfaceScalePreference.Load(settings.InterfaceScale);
        AppearancePreference.Load(AppearanceValues.From(settings)).Apply(settings);
        return settings;
    }
    private AppSettings LoadSettings()
    {
        gate.Wait();

        try
        {
            RecoveredFromBackup = false;

            if (!File.Exists(path))
            {
                if (!File.Exists(backupPath))
                    return new AppSettings();

                try
                {
                    return RestoreBackup();
                }
                catch (Exception error)
                {
                    throw new InvalidDataException(
                        "Основной файл настроек отсутствует, " +
                        "а восстановить резервную копию не удалось. " +
                        "Автоматический сброс не выполняется.",
                        error);
                }
            }

            try
            {
                return ReadProtectedSettings(path);
            }
            catch (Exception primaryError)
            {
                if (!File.Exists(backupPath))
                {
                    throw new InvalidDataException(
                        "Не удалось прочитать settings.dpapi. " +
                        "Исправная резервная копия отсутствует. " +
                        "Исходный файл сохранён.",
                        primaryError);
                }

                try
                {
                    return RestoreBackup();
                }
                catch (Exception backupError)
                {
                    throw new InvalidDataException(
                        "Не удалось прочитать настройки и восстановить " +
                        "settings.dpapi.bak. Оба исходных файла сохранены; " +
                        "автоматический сброс не выполняется.",
                        new AggregateException(
                            primaryError,
                            backupError));
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public virtual async Task SaveAsync(AppSettings settings)
    {
        var serialized = JsonSerializer.Serialize(
            settings,
            JsonSettings.Options);

        var bytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(serialized),
            null,
            DataProtectionScope.CurrentUser);

        await gate.WaitAsync().ConfigureAwait(false);

        var temporary = path + "." +
            Guid.NewGuid().ToString("N") + ".new";

        try
        {
            if (!File.Exists(path) && File.Exists(backupPath))
                throw new InvalidDataException(
                    "Основной файл настроек отсутствует, но резервная копия существует. " +
                    "Сначала восстановите настройки через Load(); сохранение не выполняется.");

            // Write through to disk before publishing the new version.
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous |
                FileOptions.WriteThrough))
            {
                await stream.WriteAsync(
                    bytes.AsMemory()).ConfigureAwait(false);

                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                // Never replace a damaged primary and overwrite a
                // potentially valid backup with damaged bytes.
                try
                {
                    _ = ReadProtectedSettings(path);
                }
                catch (Exception error)
                {
                    throw new InvalidDataException(
                        "Текущий settings.dpapi повреждён или недоступен. " +
                        "Сначала восстановите его через Load(). " +
                        "Резервная копия не перезаписана.",
                        error);
                }

                // Atomic replacement; the previous valid primary
                // becomes the recovery backup.
                File.Replace(
                    temporary,
                    path,
                    backupPath,
                    ignoreMetadataErrors: true);
            }
            else
            {
                // A first successful save should also have a backup.
                // If a previous backup exists, preserve it.
                if (!File.Exists(backupPath))
                    File.Copy(temporary, backupPath);

                File.Move(temporary, path);
            }
        }
        finally
        {
            TryDeleteTemporary(temporary);
            gate.Release();
        }
    }
    public virtual WindowPresentationState LoadPresentationState(bool isAutostart = false)
    {
        try
        {
            if (!StateFileExists(uiPath)) return isAutostart ? WindowPresentationState.HiddenToTray : WindowPresentationState.VisibleNormal;
            var text = File.ReadAllText(uiPath).Trim();
            if (Enum.TryParse<WindowPresentationState>(text, true, out var state) && Enum.IsDefined(state))
                return (!isAutostart && state == WindowPresentationState.HiddenToTray) ? WindowPresentationState.VisibleNormal : state;
            ReportStorage("PRESENTATION_STATE operation=read result=invalid");
            return isAutostart ? WindowPresentationState.HiddenToTray : WindowPresentationState.VisibleNormal;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ReportStorage("PRESENTATION_STATE operation=read result=failed error=" + error.GetType().Name);
            return isAutostart ? WindowPresentationState.HiddenToTray : WindowPresentationState.VisibleNormal;
        }
    }

    public virtual void SavePresentationState(WindowPresentationState state)
    {
        lock (stateLock)
        {
            var temp = uiPath + "." + Guid.NewGuid().ToString("N") + ".new";
            try
            {
                if (!Enum.IsDefined(state)) throw new InvalidDataException("Некорректное состояние окна.");
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(Encoding.UTF8.GetBytes(state.ToString())); file.Flush(true); }
                File.Move(temp, uiPath, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { ReportStorage("PRESENTATION_STATE operation=write result=failed error=" + error.GetType().Name); }
            finally { TryDeleteTemporary(temp); }
        }
    }

    private static bool StateFileExists(string file)
    {
        try { _ = File.GetAttributes(file); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static DesiredRuntimeState ReadDesired(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 65536) throw new InvalidDataException("Слишком большой файл намерений подключения.");
        using var document = JsonDocument.Parse(stream);
        var json = document.RootElement;
        if (json.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Некорректное состояние подключений.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in json.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException("Повторяющееся поле состояния подключений.");
        foreach (var name in new[] { "MainVpnEnabled", "TunEnabled", "ZapretEnabled", "OpenVpnEnabled" })
            if (!json.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Неполное состояние подключений.");
        if (!json.TryGetProperty("LastUpdatedUtc", out var stamp) || !stamp.TryGetDateTimeOffset(out _))
            throw new InvalidDataException("Нет времени сохранения состояния подключений.");
        return json.Deserialize<DesiredRuntimeState>(JsonSettings.Options)!;
    }

    public virtual DesiredRuntimeState LoadDesiredState()
    {
        lock (stateLock)
        {
            DesiredRecoveredFromBackup = false;
            var backup = desiredPath + ".bak";
            try
            {
                if (!StateFileExists(desiredPath) && !StateFileExists(backup)) return new();
                return ReadDesired(desiredPath);
            }
            catch (Exception primaryError) when (primaryError is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or InvalidDataException)
            {
                try
                {
                    var recovered = ReadDesired(backup);
                    var temp = desiredPath + ".restore-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.Copy(backup, temp);
                        if (StateFileExists(desiredPath)) File.Replace(temp, desiredPath, desiredPath + ".corrupt-" + Guid.NewGuid().ToString("N"), true);
                        else File.Move(temp, desiredPath);
                    }
                    finally { TryDeleteTemporary(temp); }
                    DesiredRecoveredFromBackup = true;
                    ReportStorage("DESIRED_STATE operation=read result=recovered-backup");
                    return recovered;
                }
                catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or InvalidDataException)
                {
                    ReportStorage("DESIRED_STATE operation=read result=blocked");
                    throw new InvalidDataException("Не удалось восстановить сохранённые намерения подключения. Автоматический запуск остановлен, состояние VPN не сброшено. Проверьте desired-state.json и резервную копию. Защита сетевого трафика сейчас не подтверждена.", new AggregateException(primaryError, backupError));
                }
            }
        }
    }

    public virtual void SaveDesiredState(DesiredRuntimeState state)
    {
        lock (stateLock)
        {
            var temp = desiredPath + "." + Guid.NewGuid().ToString("N") + ".new";
            var backup = desiredPath + ".bak";
            try
            {
                if (StateFileExists(desiredPath)) _ = ReadDesired(desiredPath);
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(file, state, JsonSettings.Options); file.Flush(true); }
                _ = ReadDesired(temp);
                if (StateFileExists(desiredPath)) File.Replace(temp, desiredPath, backup, true);
                else
                {
                    if (!StateFileExists(backup)) File.Copy(temp, backup);
                    File.Move(temp, desiredPath);
                }
            }
            finally { TryDeleteTemporary(temp); }
        }
    }
}
