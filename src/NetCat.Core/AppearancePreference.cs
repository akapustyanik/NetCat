using System.Text.Json;

namespace NetCat.Core;

// Only presentation values belong here: saving a colour must not commit
// unfinished network settings. The file survives replacement of the EXE.
public sealed record AppearanceValues(string BaseColor, string AccentColor, double PanelBrightness, bool HighContrastText)
{
    public static AppearanceValues From(AppSettings settings) =>
        new(settings.BaseColor, settings.AccentColor, settings.PanelBrightness, settings.HighContrastText);

    public void Apply(AppSettings settings)
    {
        settings.BaseColor = BaseColor;
        settings.AccentColor = AccentColor;
        settings.PanelBrightness = PanelBrightness;
        settings.HighContrastText = HighContrastText;
    }

    public void Validate()
    {
        ThemeColor.Parse(BaseColor); ThemeColor.Parse(AccentColor);
        if (!double.IsFinite(PanelBrightness) || PanelBrightness is < -20 or > 20)
            throw new FormatException("Яркость панелей должна быть от −20 до 20.");
    }
}

public sealed class AppearancePreference(string root)
{
    private readonly string path = Path.Combine(root, "appearance.json");
    private readonly object gate = new();
    private long revision;

    public AppearanceValues Load(AppearanceValues fallback)
    {
        try
        {
            if (new FileInfo(path).Length > 4096) return fallback;
            var value = JsonSerializer.Deserialize<AppearanceValues>(File.ReadAllText(path))
                ?? throw new FormatException("Отсутствуют настройки оформления.");
            value.Validate();
            return value;
        }
        catch (IOException) { return fallback; }
        catch (UnauthorizedAccessException) { return fallback; }
        catch (JsonException) { return fallback; }
        catch (FormatException) { return fallback; }
        catch (ArgumentException) { return fallback; }
    }

    public Task SaveAsync(AppearanceValues value)
    {
        value.Validate();
        var ticket = Interlocked.Increment(ref revision);
        return Task.Run(() => Write(value, ticket));
    }

    public void Flush(AppearanceValues value)
    {
        value.Validate();
        Write(value, Interlocked.Increment(ref revision));
    }

    private void Write(AppearanceValues value, long ticket)
    {
        lock (gate)
        {
            if (ticket != Volatile.Read(ref revision)) return;
            Directory.CreateDirectory(root);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, value);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
