using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;

namespace ServiceLib.Common;

public static class FileUtils
{
    private static readonly string _tag = "FileManager";
    private const int DefaultIoRetryCount = 5;
    private const int DefaultIoRetryDelayMs = 150;

    public static bool ByteArrayToFile(string fileName, byte[] content)
    {
        try
        {
            File.WriteAllBytes(fileName, content);
            return true;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
        return false;
    }

    public static void DecompressFile(string fileName, byte[] content)
    {
        try
        {
            using var fs = File.Create(fileName);
            using GZipStream input = new(new MemoryStream(content), CompressionMode.Decompress, false);
            input.CopyTo(fs);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    public static void DecompressFile(string fileName, string toPath, string? toName)
    {
        try
        {
            FileInfo fileInfo = new(fileName);
            using var originalFileStream = fileInfo.OpenRead();
            using var decompressedFileStream = File.Create(toName != null ? Path.Combine(toPath, toName) : toPath);
            using GZipStream decompressionStream = new(originalFileStream, CompressionMode.Decompress);
            decompressionStream.CopyTo(decompressedFileStream);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    public static void DecompressTarFile(string fileName, string toPath)
    {
        try
        {
            using var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read);
            using var gz = new GZipStream(fs, CompressionMode.Decompress, leaveOpen: true);
            TarFile.ExtractToDirectory(gz, toPath, overwriteFiles: true);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    public static string NonExclusiveReadAllText(string path)
    {
        return NonExclusiveReadAllText(path, Encoding.Default);
    }

    private static string NonExclusiveReadAllText(string path, Encoding encoding)
    {
        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader sr = new(fs, encoding);
            return sr.ReadToEnd();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            throw;
        }
    }

    public static bool ZipExtractToFile(string fileName, string toPath, string ignoredName)
    {
        try
        {
            using var archive = ZipFile.OpenRead(fileName);
            foreach (var entry in archive.Entries)
            {
                if (entry.Length == 0)
                {
                    continue;
                }
                try
                {
                    if (ignoredName.IsNotEmpty() && entry.Name.Contains(ignoredName))
                    {
                        continue;
                    }
                    entry.ExtractToFile(Path.Combine(toPath, entry.Name), true);
                }
                catch (IOException ex)
                {
                    Logging.SaveLog(_tag, ex);
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return false;
        }
        return true;
    }

    public static List<string>? GetFilesFromZip(string fileName)
    {
        if (!File.Exists(fileName))
        {
            return null;
        }
        try
        {
            using var archive = ZipFile.OpenRead(fileName);
            return archive.Entries.Select(entry => entry.FullName).ToList();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return null;
        }
    }

    public static bool CreateFromDirectory(string sourceDirectoryName, string destinationArchiveFileName)
    {
        try
        {
            if (File.Exists(destinationArchiveFileName))
            {
                File.Delete(destinationArchiveFileName);
            }

            ZipFile.CreateFromDirectory(sourceDirectoryName, destinationArchiveFileName, CompressionLevel.SmallestSize, true);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return false;
        }
        return true;
    }

    public static async Task WriteAllTextWithRetryAsync(string path, string contents, Encoding? encoding = null, int retryCount = DefaultIoRetryCount, int delayMs = DefaultIoRetryDelayMs)
    {
        encoding ??= new UTF8Encoding(false);

        var directory = Path.GetDirectoryName(path);
        if (!directory.IsNullOrEmpty())
        {
            Directory.CreateDirectory(directory);
        }

        Exception? lastException = null;
        for (var attempt = 0; attempt < retryCount; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                await using var writer = new StreamWriter(stream, encoding);
                await writer.WriteAsync(contents);
                await writer.FlushAsync();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastException = ex;
                if (attempt == retryCount - 1)
                {
                    break;
                }

                await Task.Delay(delayMs);
            }
        }

        throw lastException ?? new IOException($"Failed to write file: {path}");
    }

    public static void CopyDirectory(string sourceDir, string destinationDir, bool recursive, bool overwrite, string? ignoredName = null)
    {
        // Get information about the source directory
        var dir = new DirectoryInfo(sourceDir);

        // Check if the source directory exists
        if (!dir.Exists)
        {
            throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");
        }

        // Cache directories before we start copying
        var dirs = dir.GetDirectories();

        // Create the destination directory
        _ = Directory.CreateDirectory(destinationDir);

        // Get the files in the source directory and copy to the destination directory
        foreach (var file in dir.GetFiles())
        {
            if (ignoredName.IsNotEmpty() && file.Name.Contains(ignoredName))
            {
                continue;
            }
            if (file.Extension == file.Name)
            {
                continue;
            }
            var targetFilePath = Path.Combine(destinationDir, file.Name);
            if (!overwrite && File.Exists(targetFilePath))
            {
                continue;
            }
            _ = file.CopyTo(targetFilePath, overwrite);
        }

        // If recursive and copying subdirectories, recursively call this method
        if (recursive)
        {
            foreach (var subDir in dirs)
            {
                var newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                CopyDirectory(subDir.FullName, newDestinationDir, true, overwrite, ignoredName);
            }
        }
    }

    public static void DeleteExpiredFiles(string sourceDir, DateTime dtLine)
    {
        try
        {
            var files = Directory.GetFiles(sourceDir, "*.*");
            foreach (var filePath in files)
            {
                var file = new FileInfo(filePath);
                if (file.LastWriteTime >= dtLine)
                {
                    continue;
                }
                file.Delete();
            }
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// Creates a Linux shell file with the specified contents.
    /// </summary>
    /// <param name="fileName"></param>
    /// <param name="contents"></param>
    /// <param name="overwrite"></param>
    /// <returns></returns>
    public static async Task<string> CreateLinuxShellFile(string fileName, string contents, bool overwrite)
    {
        var shFilePath = Utils.GetBinConfigPath(fileName);

        // Check if the file already exists and if we should overwrite it
        if (!overwrite && File.Exists(shFilePath))
        {
            return shFilePath;
        }

        File.Delete(shFilePath);
        await File.WriteAllTextAsync(shFilePath, contents);
        await Utils.SetLinuxChmod(shFilePath);

        return shFilePath;
    }

    public static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!directory.IsNullOrEmpty())
        {
            Directory.CreateDirectory(directory);
        }
    }

    public static bool TryDeleteFile(string? path)
    {
        try
        {
            if (!path.IsNullOrEmpty() && File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return false;
        }
    }

    public static bool TryDeleteDirectory(string? path)
    {
        try
        {
            if (!path.IsNullOrEmpty() && Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            return true;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return false;
        }
    }

    public static void CopyFileWithRetry(string sourcePath, string destinationPath, bool overwrite, int retryCount = DefaultIoRetryCount, int delayMs = DefaultIoRetryDelayMs)
    {
        EnsureParentDirectory(destinationPath);

        for (var attempt = 1; attempt <= retryCount; attempt++)
        {
            try
            {
                File.Copy(sourcePath, destinationPath, overwrite);
                return;
            }
            catch when (attempt < retryCount)
            {
                Thread.Sleep(delayMs * attempt);
            }
        }

        File.Copy(sourcePath, destinationPath, overwrite);
    }

    public static bool HasZoneIdentifier(string? path)
    {
        if (!OperatingSystem.IsWindows() || path.IsNullOrEmpty())
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(GetZoneIdentifierPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.Length >= 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static string? TryReadZoneIdentifier(string? path)
    {
        if (!OperatingSystem.IsWindows() || path.IsNullOrEmpty())
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(GetZoneIdentifierPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream, Encoding.UTF8, true);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return null;
        }
    }

    public static bool IsFileDigitallySigned(string? path)
    {
        if (path.IsNullOrEmpty() || !File.Exists(path))
        {
            return false;
        }

        try
        {
            _ = X509Certificate.CreateFromSignedFile(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string DescribeWindowsTrustState(string? path)
    {
        if (path.IsNullOrEmpty())
        {
            return "path=(empty)";
        }

        var exists = File.Exists(path);
        var isSigned = exists && IsFileDigitallySigned(path);
        var zoneIdentifier = exists ? TryReadZoneIdentifier(path) : null;
        var hasZoneIdentifier = !string.IsNullOrWhiteSpace(zoneIdentifier);
        var normalizedZoneIdentifier = zoneIdentifier?
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();

        if (!string.IsNullOrWhiteSpace(normalizedZoneIdentifier) && normalizedZoneIdentifier.Length > 160)
        {
            normalizedZoneIdentifier = normalizedZoneIdentifier[..157] + "...";
        }

        return $"path={path}, exists={exists}, signed={isSigned}, zoneIdentifier={hasZoneIdentifier}, zone={normalizedZoneIdentifier ?? "(none)"}";
    }

    public static bool TryUnblockFile(string? path)
    {
        if (!OperatingSystem.IsWindows() || path.IsNullOrEmpty() || !File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete(GetZoneIdentifierPath(path));
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return false;
        }
    }

    public static (int ScannedFiles, int BlockedFiles, int UnblockedFiles) TryUnblockDirectoryFiles(string? directoryPath)
    {
        if (!OperatingSystem.IsWindows() || directoryPath.IsNullOrEmpty() || !Directory.Exists(directoryPath))
        {
            return (0, 0, 0);
        }

        var scannedFiles = 0;
        var blockedFiles = 0;
        var unblockedFiles = 0;
        try
        {
            foreach (var filePath in Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                scannedFiles++;
                if (!HasZoneIdentifier(filePath))
                {
                    continue;
                }

                blockedFiles++;
                if (TryUnblockFile(filePath))
                {
                    unblockedFiles++;
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }

        return (scannedFiles, blockedFiles, unblockedFiles);
    }

    public static void MoveFileWithRetry(string sourcePath, string destinationPath, bool overwrite, int retryCount = DefaultIoRetryCount, int delayMs = DefaultIoRetryDelayMs)
    {
        EnsureParentDirectory(destinationPath);

        for (var attempt = 1; attempt <= retryCount; attempt++)
        {
            try
            {
                File.Move(sourcePath, destinationPath, overwrite);
                return;
            }
            catch when (attempt < retryCount)
            {
                Thread.Sleep(delayMs * attempt);
            }
        }

        File.Move(sourcePath, destinationPath, overwrite);
    }

    public static void WriteAllTextAtomic(string path, string contents, Encoding? encoding = null)
    {
        encoding ??= new UTF8Encoding(false);
        EnsureParentDirectory(path);

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, contents, encoding);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        File.Move(tempPath, path, true);
    }

    private static string GetZoneIdentifierPath(string path)
    {
        return $"{path}:Zone.Identifier";
    }
}
