using System.Diagnostics;
using System.Runtime.InteropServices;
using NetCat.Core;

namespace NetCat.UI;

public enum ShellHandoffResult { OpenedUnelevatedShell, FallbackCopied, Failed }

/// <summary>Restricted operations available to the elevated Journal only.</summary>
public interface IUnelevatedShellLauncher
{
    ShellHandoffResult OpenFile(string path, string allowedRoot, Action<string> copyToClipboard, out string message);
    ShellHandoffResult OpenFolder(string path, string allowedRoot, Action<string> copyToClipboard, out string message);
    ShellHandoffResult OpenTelegramProxy(string generatedUri, int localPort, Action<string> copyToClipboard, out string message);
}

public sealed class UnelevatedExplorerShellLauncher : IUnelevatedShellLauncher
{
    public ShellHandoffResult OpenFile(string path, string allowedRoot, Action<string> copyToClipboard, out string message) => Open(path, allowedRoot, false, copyToClipboard, out message);
    public ShellHandoffResult OpenFolder(string path, string allowedRoot, Action<string> copyToClipboard, out string message) => Open(path, allowedRoot, true, copyToClipboard, out message);

    public ShellHandoffResult OpenTelegramProxy(string generatedUri, int localPort, Action<string> copyToClipboard, out string message)
    {
        try { ValidateTelegramProxyUri(generatedUri, localPort); }
        catch (InvalidOperationException)
        {
            // A startup/restart link is not a shell failure and must never be
            // passed to Explorer or offered as a clipboard fallback.
            message = "Локальный прокси Telegram ещё не готов или ссылка недействительна.";
            return ShellHandoffResult.Failed;
        }
        try
        {
            OpenVerifiedTarget(generatedUri);
            message = "Telegram открыт для подключения локального прокси.";
            return ShellHandoffResult.OpenedUnelevatedShell;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or COMException or System.ComponentModel.Win32Exception)
        {
            if (JournalActionFeedback.TryCopy(generatedUri, copyToClipboard))
            {
                message = "Не удалось открыть Telegram автоматически; ссылка скопирована.";
                return ShellHandoffResult.FallbackCopied;
            }
            message = "Не удалось открыть Telegram. Повторите действие.";
            return ShellHandoffResult.Failed;
        }
    }

    public static void ValidateTelegramProxyUri(string link, int localPort)
    {
        if (localPort is < 1 or > 65535 || !Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
            uri.Scheme != "tg" || uri.Host != "proxy" || (uri.AbsolutePath != "" && uri.AbsolutePath != "/") || uri.Fragment.Length != 0)
            throw new InvalidOperationException("Недопустимая ссылка Telegram proxy.");
        var parts = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) throw new InvalidOperationException("Недопустимые параметры Telegram proxy.");
        var values = parts.Select(part => part.Split('=', 2)).ToArray();
        if (values.Any(pair => pair.Length != 2) ||
            values.Select(pair => pair[0]).Distinct(StringComparer.Ordinal).Count() != 3)
            throw new InvalidOperationException("Недопустимые параметры Telegram proxy.");
        var args = values.ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
        if (args.Count != 3 || !args.TryGetValue("server", out var host) || host != "127.0.0.1" ||
            !args.TryGetValue("port", out var port) || port != localPort.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            !args.TryGetValue("secret", out var secret) ||
            !System.Text.RegularExpressions.Regex.IsMatch(secret, "^dd[a-fA-F0-9]{32}$"))
            throw new InvalidOperationException("Telegram proxy не соответствует запущенному локальному сервису.");
    }

    private static ShellHandoffResult Open(string path, string allowedRoot, bool folder, Action<string> copyToClipboard, out string message)
    {
        try
        {
            var canonical = ValidateOwnedPath(path, allowedRoot, folder);
            OpenVerifiedTarget(canonical);
            message = "Открыто через обычную оболочку Windows.";
            return ShellHandoffResult.OpenedUnelevatedShell;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or COMException)
        {
            if (JournalActionFeedback.TryCopy(path, copyToClipboard))
            {
                message = "Не удалось открыть автоматически; путь скопирован. Откройте его в обычном приложении.";
                return ShellHandoffResult.FallbackCopied;
            }
            message = "Не удалось открыть объект, и буфер обмена недоступен. Повторите действие.";
            return ShellHandoffResult.Failed;
        }
    }

    private static void OpenVerifiedTarget(string target)
    {
        if (!WindowsExecutableTrust.IsElevated)
        {
            using var opened = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            if (opened == null) throw new InvalidOperationException("Windows не создала обработчик для объекта.");
            return;
        }
        ExplorerDesktopShell.Open(target);
    }

    public static string ValidateOwnedPath(string path, string allowedRoot, bool folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot));
        var target = Path.GetFullPath(path);
        var expected = folder ? root : Path.Combine(root, "diagnostic.log");
        if (!string.Equals(target, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Объект не принадлежит журналу NetCat.");
        if ((folder && !Directory.Exists(target)) || (!folder && !File.Exists(target))) throw new IOException("Объект журнала не найден.");
        for (var current = target; !string.Equals(current, root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current) ?? throw new IOException("Недопустимый путь журнала."))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылка в пути журнала заблокирована.");
        return target;
    }

    internal static int IntegrityLevel(int processId)
    {
        using var process = Process.GetProcessById(processId);
        if (!OpenProcessToken(process.Handle, 0x0008, out var token)) throw new System.ComponentModel.Win32Exception();
        try
        {
            GetTokenInformation(token, 25, IntPtr.Zero, 0, out var bytes);
            var memory = Marshal.AllocHGlobal((int)bytes);
            try
            {
                if (!GetTokenInformation(token, 25, memory, bytes, out _)) throw new System.ComponentModel.Win32Exception();
                var sid = Marshal.ReadIntPtr(memory); var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        finally { CloseHandle(token); }
    }

    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr info, uint size, out uint needed);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Bounded clipboard handoff used when an elevated NetCat process cannot safely
/// ask Explorer or a user shell to open a file on the user's behalf.
/// </summary>
public static class JournalActionFeedback
{
    public static bool TryCopy(string text, Action<string> setClipboard, int attempts = 3, Action<int>? wait = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        ArgumentNullException.ThrowIfNull(setClipboard);
        if (attempts < 1) throw new ArgumentOutOfRangeException(nameof(attempts));

        wait ??= milliseconds => Thread.Sleep(milliseconds);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                setClipboard(text);
                return true;
            }
            catch when (attempt < attempts)
            {
                // Another desktop application can briefly own the STA clipboard.
                // Retry a bounded number of times; never leave a UI action hanging.
                wait(40);
            }
            catch
            {
                return false;
            }
        }
        return false;
    }
}
