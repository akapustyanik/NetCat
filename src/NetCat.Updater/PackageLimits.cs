using System.IO.Compression;

namespace NetCat.Updater;

// Resource limits are independent of source identity, signatures and hashes.
public static class PackageLimits
{
    public const long DownloadBytes = 512L * 1024 * 1024;
    public const long ExpandedBytes = 1024L * 1024 * 1024;
    public const int Entries = 20000;

    public static async Task CopyAsync(Stream input, Stream output, long limit, CancellationToken ct)
    {
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        var buffer = new byte[65536]; long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (read > limit - total) throw new InvalidDataException("Пакет превышает допустимый размер.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;
        }
    }

    public static async Task DownloadAsync(HttpResponseMessage response, string path, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > DownloadBytes) throw new InvalidDataException("Пакет превышает допустимый размер загрузки.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await CopyAsync(input, output, DownloadBytes, timeout.Token).ConfigureAwait(false);
    }

    public static async Task ExtractAsync(string archivePath, string destination, CancellationToken ct,
        long byteLimit = ExpandedBytes, int entryLimit = Entries)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5)); ct = timeout.Token;
        using var zip = ZipFile.OpenRead(archivePath);
        if (zip.Entries.Count > entryLimit) throw new InvalidDataException("В архиве слишком много элементов.");
        long total = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Validate the complete directory before creating any output file.
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.TrimEnd('/');
            var path = PortableUpdate.SafePath(destination, name);
            if (!paths.Add(path) || entry.Length < 0 || entry.Length > byteLimit - total ||
                ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                throw new InvalidDataException("Недопустимый состав или размер архива.");
            total += entry.Length;
        }
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = PortableUpdate.SafePath(destination, entry.FullName.TrimEnd('/'));
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyAsync(input, output, entry.Length, ct).ConfigureAwait(false);
            if (output.Length != entry.Length) throw new InvalidDataException("Неполные данные архива.");
        }
    }
}
