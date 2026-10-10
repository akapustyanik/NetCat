using System.Globalization;

namespace NetCat.Core;

// Presentation preference lives outside the installation directory and does
// not commit unrelated network drafts when the user moves the scale slider.
public sealed class InterfaceScalePreference(string root)
{
    private readonly string path = Path.Combine(root, "interface-scale.txt");
    private readonly object gate = new();
    private long revision;
    private static bool Valid(double scale) => double.IsFinite(scale) && scale is >= .7 and <= 1.3;
    public double Load(double fallback)
    {
        try
        {
            if (new FileInfo(path).Length > 128) return fallback;
            var text = File.ReadAllText(path);
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && Valid(value) ? value : fallback;
        }
        catch (IOException) { return fallback; }
        catch (UnauthorizedAccessException) { return fallback; }
    }
    public Task SaveAsync(double scale)
    {
        if (!Valid(scale)) throw new ArgumentOutOfRangeException(nameof(scale));
        var ticket = Interlocked.Increment(ref revision);
        return Task.Run(() => Write(scale, ticket));
    }
    public void Flush(double scale)
    {
        if (!Valid(scale)) throw new ArgumentOutOfRangeException(nameof(scale));
        Write(scale, Interlocked.Increment(ref revision));
    }
    private void Write(double scale, long ticket)
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
                    var bytes = System.Text.Encoding.UTF8.GetBytes(scale.ToString("R", CultureInfo.InvariantCulture));
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
