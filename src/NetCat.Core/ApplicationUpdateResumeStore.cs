using System.Security.Cryptography;
using System.Text.Json;

namespace NetCat.Core;

public sealed record ApplicationUpdateResume(bool TelegramEnabled);

/// <summary>A short-lived, one-use handoff; it never changes startup preferences or connection intent.</summary>
public sealed class ApplicationUpdateResumeStore
{
    public const string TokenArgument = "--update-resume=";
    public const string JobArgument = "--update-resume-job=";
    private readonly string folder;
    private readonly Func<DateTimeOffset> now;
    private sealed record Ticket(string Root, string JobIdentity, string SourceVersion,
        string TargetVersion, bool TelegramEnabled, DateTimeOffset CreatedUtc);

    public ApplicationUpdateResumeStore(string settingsRoot, Func<DateTimeOffset>? now = null)
    {
        folder = Path.Combine(settingsRoot, "update-resume");
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public static bool IsValidToken(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static string Root(string root) => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
    private string PathFor(string token)
    {
        if (!IsValidToken(token)) throw new InvalidDataException("Недопустимый идентификатор восстановления обновления.");
        return Path.Combine(folder, token + ".dpapi");
    }

    public string Create(string root, string jobIdentity, string sourceVersion, string targetVersion, bool telegramEnabled)
    {
        if (!IsValidToken(jobIdentity) || string.IsNullOrWhiteSpace(sourceVersion) || string.IsNullOrWhiteSpace(targetVersion))
            throw new InvalidDataException("Не определено проверенное задание обновления.");
        PrivateFiles.ProtectDirectory(folder);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var ticket = new Ticket(Root(root), jobIdentity, sourceVersion, targetVersion, telegramEnabled, now());
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(ticket), null, DataProtectionScope.CurrentUser);
        using var file = new FileStream(PathFor(token), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
        file.Flush(true);
        return token;
    }

    public void Cancel(string token)
    {
        var path = PathFor(token);
        if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Ссылка вместо папки восстановления обновления.");
        File.Delete(path);
    }

    public ApplicationUpdateResume? ConsumeArguments(string[] arguments, string root, string runningVersion)
    {
        var tokens = arguments.Where(arg => arg.StartsWith(TokenArgument, StringComparison.Ordinal)).ToArray();
        var jobs = arguments.Where(arg => arg.StartsWith(JobArgument, StringComparison.Ordinal)).ToArray();
        return tokens.Length == 1 && jobs.Length == 1
            ? Consume(tokens[0][TokenArgument.Length..], jobs[0][JobArgument.Length..], root, runningVersion) : null;
    }

    public ApplicationUpdateResume? Consume(string token, string jobIdentity, string root, string runningVersion)
    {
        if (!IsValidToken(token) || !IsValidToken(jobIdentity) || !Directory.Exists(folder)) return null;
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Ссылка вместо папки восстановления обновления.");
        var path = PathFor(token);
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Ссылка вместо состояния восстановления обновления.");
            // Exclusive open + delete-on-close is one atomic claim on Windows. No
            // other process can consume the file between releasing a lock and deleting it.
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.DeleteOnClose);
            if (file.Length > 8192) throw new InvalidDataException("Состояние восстановления обновления повреждено.");
            var bytes = new byte[checked((int)file.Length)];
            file.ReadExactly(bytes);
            var ticket = JsonSerializer.Deserialize<Ticket>(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
            if (ticket == null || !string.Equals(ticket.Root, Root(root), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ticket.JobIdentity, jobIdentity, StringComparison.Ordinal) ||
                runningVersion != ticket.SourceVersion && runningVersion != ticket.TargetVersion ||
                now() - ticket.CreatedUtc > TimeSpan.FromMinutes(30) || ticket.CreatedUtc > now().AddMinutes(1)) return null;
            return new ApplicationUpdateResume(ticket.TelegramEnabled);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { return null; }
    }
}
