using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using NetCat.Core;

namespace NetCat.UI;

/// <summary>
/// Hands a validated UI action from elevated NetCat to the normal
/// medium-integrity desktop of the same Windows user/session.
///
/// Primary path:
///   duplicate the token of the user's Explorer and create a tiny
///   Windows shell-dispatch process with that token.
///
/// Fallback:
///   use the existing Explorer desktop COM dispatch.
///
/// The caller remains responsible for validating the target.
/// </summary>
internal static class ExplorerDesktopShell
{
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint MaximumAllowed = 0x02000000;

    private const uint LogonWithProfile = 0x00000001;

    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    private static readonly Guid TopLevelBrowser =
        new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    private enum TargetKind
    {
        Folder,
        File,
        TelegramProtocol,
        WebProtocol
    }

    private readonly record struct LaunchPlan(
        string Application,
        string Arguments);


    public static void Open(
        string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            target);

        var kind = ClassifyTarget(target);

        // Non-elevated NetCat can safely use its own user shell.
        if(!WindowsExecutableTrust.IsElevated)
        {
            OpenUsingCurrentShell(target);
            return;
        }

        if(!IsUacEnabled())
        {
            // With UAC disabled Windows does not maintain the normal
            // filtered/elevated administrator token split. Explorer
            // therefore already runs in the user's current full-token
            // security context. Trying to manufacture an "unelevated"
            // shell is both pointless and unreliable in this mode.
            //
            // The caller has already restricted/validated the target:
            //   - diagnostic.log or its exact folder;
            //   - exact NetCat module directory;
            //   - generated localhost tg://proxy URI.
            OpenUsingCurrentShell(target);
            return;
        }

        Exception? tokenFailure =
            null;

        try
        {
            OpenUsingExplorerUserToken(
                target,
                kind);

            return;
        }
        catch(Exception ex)
            when(IsExpectedShellFailure(ex))
        {
            tokenFailure =
                ex;
        }

        try
        {
            // Keep the Candidate32 COM implementation as a secondary
            // compatibility path for systems where token creation is
            // restricted by local policy.
            OpenViaDesktopCom(
                target);

            return;
        }
        catch(Exception comFailure)
            when(IsExpectedShellFailure(comFailure))
        {
            throw new InvalidOperationException(
                "Не удалось передать действие обычному Explorer. " +
                $"Token handoff: {Describe(tokenFailure!)}; " +
                $"Explorer COM: {Describe(comFailure)}.",
                new AggregateException(
                    tokenFailure!,
                    comFailure));
        }
    }


    internal static bool IsUacEnabled()
    {
        try
        {
            using var key =
                Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",
                    writable: false);

            var raw =
                key?.GetValue(
                    "EnableLUA");

            // Missing/unreadable state fails toward the safer UAC-enabled
            // path rather than silently weakening the handoff.
            return raw is not int value ||
                   value != 0;
        }
        catch
        {
            return true;
        }
    }


    private static void OpenUsingCurrentShell(
        string target)
    {
        var startInfo =
            new ProcessStartInfo(target)
            {
                UseShellExecute = true
            };

        // Do not use verb=runas. We are explicitly asking Windows to
        // perform its normal registered "open" action in the security
        // context that this Windows configuration already provides.
        using var opened =
            Process.Start(startInfo);

        // ShellExecute can legally complete the handoff without giving
        // us a durable child-process object for every shell target.
        // A thrown Win32Exception is the authoritative failure signal.
    }

    private static TargetKind ClassifyTarget(
        string target)
    {
        if(target.Any(char.IsControl))
            throw new InvalidOperationException("Недопустимый символ в адресе.");
        if(Path.IsPathFullyQualified(target))
        {
            var canonical =
                Path.GetFullPath(target);

            if(Directory.Exists(canonical))
                return TargetKind.Folder;

            if(File.Exists(canonical))
                return TargetKind.File;

            throw new IOException(
                "Объект для открытия не существует.");
        }

        if(Uri.TryCreate(
               target,
               UriKind.Absolute,
               out var uri) &&
           uri.Scheme.Equals(
               "tg",
               StringComparison.OrdinalIgnoreCase))
        {
            return TargetKind.TelegramProtocol;
        }

        if(uri != null && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
           !string.IsNullOrWhiteSpace(uri.Host) && string.IsNullOrEmpty(uri.UserInfo))
            return TargetKind.WebProtocol;

        throw new InvalidOperationException(
            "Тип объекта не разрешён для передачи пользовательской оболочке.");
    }


    private static void OpenUsingExplorerUserToken(
        string target,
        TargetKind kind)
    {
        using var explorer =
            FindExplorerProcess();

        if(!OpenProcessToken(
               explorer.Handle,
               TokenAssignPrimary |
               TokenDuplicate |
               TokenQuery,
               out var sourceToken))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Не удалось открыть токен пользовательского Explorer.");
        }

        try
        {
            if(!DuplicateTokenEx(
                   sourceToken,
                   MaximumAllowed,
                   IntPtr.Zero,
                   SecurityImpersonation,
                   TokenPrimary,
                   out var primaryToken))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Не удалось создать пользовательский primary token.");
            }

            try
            {
                var plan =
                    BuildLaunchPlan(
                        target,
                        kind);

                var commandLine =
                    new StringBuilder();

                commandLine.Append(
                    QuoteArgument(
                        plan.Application));

                if(!string.IsNullOrWhiteSpace(
                       plan.Arguments))
                {
                    commandLine
                        .Append(' ')
                        .Append(plan.Arguments);
                }

                var startup =
                    new StartupInfo
                    {
                        Size =
                            Marshal.SizeOf<StartupInfo>(),

                        Desktop =
                            @"winsta0\default"
                    };

                if(!CreateProcessWithTokenW(
                       primaryToken,
                       LogonWithProfile,
                       plan.Application,
                       commandLine,
                       0,
                       IntPtr.Zero,
                       null,
                       ref startup,
                       out var process))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Не удалось запустить действие в пользовательской сессии.");
                }

                try
                {
                    // Creation itself is enough. We do not wait for Explorer,
                    // the default log viewer or Telegram to terminate.
                }
                finally
                {
                    if(process.Thread != IntPtr.Zero)
                        CloseHandle(process.Thread);

                    if(process.Process != IntPtr.Zero)
                        CloseHandle(process.Process);
                }
            }
            finally
            {
                CloseHandle(primaryToken);
            }
        }
        finally
        {
            CloseHandle(sourceToken);
        }
    }


    private static LaunchPlan BuildLaunchPlan(
        string target,
        TargetKind kind)
    {
        if(kind == TargetKind.Folder)
        {
            var explorer =
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.Windows),
                    "explorer.exe");

            if(!File.Exists(explorer))
            {
                throw new FileNotFoundException(
                    "explorer.exe не найден.",
                    explorer);
            }

            return new LaunchPlan(
                explorer,
                QuoteArgument(
                    Path.GetFullPath(target)));
        }

        // FileProtocolHandler asks the normal user's shell to resolve
        // the default association. It works for diagnostic.log and tg://
        // without starting cmd.exe or constructing a shell command.
        var rundll32 =
            Path.Combine(
                Environment.SystemDirectory,
                "rundll32.exe");

        if(!File.Exists(rundll32))
        {
            throw new FileNotFoundException(
                "rundll32.exe не найден.",
                rundll32);
        }

        return new LaunchPlan(
            rundll32,
            "url.dll,FileProtocolHandler " +
            QuoteArgument(target));
    }


    private static Process FindExplorerProcess()
    {
        var session =
            Process.GetCurrentProcess()
                .SessionId;

        using var caller =
            WindowsIdentity.GetCurrent();

        Process? selected =
            null;

        foreach(var candidate in
                Process.GetProcessesByName(
                    "explorer"))
        {
            if(selected != null)
            {
                candidate.Dispose();
                continue;
            }

            try
            {
                if(candidate.SessionId != session)
                {
                    candidate.Dispose();
                    continue;
                }

                if(UnelevatedExplorerShellLauncher
                       .IntegrityLevel(candidate.Id) !=
                   0x2000)
                {
                    candidate.Dispose();
                    continue;
                }

                if(!OpenProcessToken(
                       candidate.Handle,
                       TokenQuery,
                       out var token))
                {
                    candidate.Dispose();
                    continue;
                }

                var sameUser =
                    false;

                try
                {
                    using var owner =
                        new WindowsIdentity(token);

                    sameUser =
                        owner.User != null &&
                        caller.User != null &&
                        owner.User.Equals(
                            caller.User);
                }
                finally
                {
                    CloseHandle(token);
                }

                if(!sameUser)
                {
                    candidate.Dispose();
                    continue;
                }

                selected =
                    candidate;
            }
            catch
            {
                candidate.Dispose();
            }
        }

        return selected ??
            throw new InvalidOperationException(
                "Обычный Explorer текущего пользователя не найден.");
    }


    private static string QuoteArgument(
        string value)
    {
        // Windows CommandLineToArgvW-compatible quoting.
        if(value.Length == 0)
            return "\"\"";

        if(!value.Any(
               c =>
                   char.IsWhiteSpace(c) ||
                   c == '"'))
        {
            return value;
        }

        var result =
            new StringBuilder();

        result.Append('"');

        var slashes =
            0;

        foreach(var c in value)
        {
            if(c == '\\')
            {
                slashes++;
                continue;
            }

            if(c == '"')
            {
                result.Append(
                    '\\',
                    slashes * 2 + 1);

                result.Append('"');

                slashes =
                    0;

                continue;
            }

            if(slashes > 0)
            {
                result.Append(
                    '\\',
                    slashes);

                slashes =
                    0;
            }

            result.Append(c);
        }

        if(slashes > 0)
        {
            result.Append(
                '\\',
                slashes * 2);
        }

        result.Append('"');

        return result.ToString();
    }


    private static bool IsExpectedShellFailure(
        Exception error) =>
        error is
            IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            COMException or
            Win32Exception;


    private static string Describe(
        Exception error) =>
        $"{error.GetType().Name}: {error.Message}";


    #region Explorer COM fallback

    private static void OpenViaDesktopCom(
        string target)
    {
        object? windows = null;
        object? provider = null;
        object? browser = null;
        object? view = null;
        object? folder = null;
        object? dispatch = null;

        try
        {
            windows =
                new ShellWindows();

            object desktop =
                0;

            object unused =
                new object();

            provider =
                ((IShellWindows)windows)
                    .FindWindowSW(
                        ref desktop,
                        ref unused,
                        8,
                        out var hwnd,
                        1);

            if(hwnd == 0 ||
               provider is not IServiceProvider service)
            {
                throw new InvalidOperationException(
                    "Рабочий стол Explorer недоступен.");
            }

            VerifyDesktopOwner(
                new IntPtr(hwnd));

            var serviceId =
                TopLevelBrowser;

            var browserId =
                typeof(IShellBrowser).GUID;

            browser =
                service.QueryService(
                    ref serviceId,
                    ref browserId);

            view =
                ((IShellBrowser)browser)
                    .QueryActiveShellView();

            var dispatchId =
                typeof(IDispatch).GUID;

            folder =
                ((IShellView)view)
                    .GetItemObject(
                        0,
                        ref dispatchId);

            dispatch =
                ((IShellFolderViewDual)folder)
                    .Application;

            ((IShellDispatch2)dispatch)
                .ShellExecute(
                    target,
                    "",
                    "",
                    "open",
                    1);
        }
        finally
        {
            foreach(var item in
                    new[]
                    {
                        dispatch,
                        folder,
                        view,
                        browser,
                        provider,
                        windows
                    })
            {
                if(item != null &&
                   Marshal.IsComObject(item))
                {
                    Marshal.ReleaseComObject(
                        item);
                }
            }
        }
    }


    private static void VerifyDesktopOwner(
        IntPtr hwnd)
    {
        GetWindowThreadProcessId(
            hwnd,
            out var pid);

        if(pid == 0)
        {
            throw new InvalidOperationException(
                "Владелец рабочего стола не найден.");
        }

        using var process =
            Process.GetProcessById(
                (int)pid);

        if(!string.Equals(
               process.ProcessName,
               "explorer",
               StringComparison.OrdinalIgnoreCase) ||
           process.SessionId !=
               Process.GetCurrentProcess()
                   .SessionId ||
           UnelevatedExplorerShellLauncher
               .IntegrityLevel(process.Id) !=
               0x2000)
        {
            throw new InvalidOperationException(
                "Рабочий стол не принадлежит обычному Explorer текущей сессии.");
        }

        if(!OpenProcessToken(
               process.Handle,
               TokenQuery,
               out var token))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error());
        }

        try
        {
            using var owner =
                new WindowsIdentity(token);

            using var caller =
                WindowsIdentity.GetCurrent();

            if(owner.User == null ||
               caller.User == null ||
               !owner.User.Equals(
                   caller.User))
            {
                throw new InvalidOperationException(
                    "Explorer принадлежит другому пользователю.");
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    #endregion


    #region Native process/token API

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;

        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;

        public short ShowWindow;
        public short Reserved2Size;

        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;

        public int ProcessId;
        public int ThreadId;
    }


    [DllImport(
        "advapi32.dll",
        SetLastError = true)]
    private static extern bool OpenProcessToken(
        IntPtr process,
        uint desiredAccess,
        out IntPtr token);


    [DllImport(
        "advapi32.dll",
        SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr newToken);


    [DllImport(
        "advapi32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(
        IntPtr token,
        uint logonFlags,
        string applicationName,
        [In, Out] StringBuilder commandLine,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);


    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    private static extern bool CloseHandle(
        IntPtr handle);

    #endregion


    #region Explorer COM API

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr hwnd,
        out uint processId);


    [ComImport]
    [Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")]
    [ClassInterface(ClassInterfaceType.None)]
    private class ShellWindows
    {
    }


    [ComImport]
    [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellWindows
    {
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(
            [MarshalAs(UnmanagedType.Struct)]
            ref object location,

            [MarshalAs(UnmanagedType.Struct)]
            ref object root,

            int windowClass,
            out int hwnd,
            int options);
    }


    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object QueryService(
            ref Guid service,
            ref Guid iid);
    }


    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void Gap01();
        void Gap02();
        void Gap03();
        void Gap04();
        void Gap05();
        void Gap06();
        void Gap07();
        void Gap08();
        void Gap09();
        void Gap10();
        void Gap11();
        void Gap12();

        IShellView QueryActiveShellView();
    }


    [ComImport]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void Gap01();
        void Gap02();
        void Gap03();
        void Gap04();
        void Gap05();
        void Gap06();
        void Gap07();
        void Gap08();
        void Gap09();
        void Gap10();
        void Gap11();
        void Gap12();

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetItemObject(
            uint aspect,
            ref Guid iid);
    }


    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IDispatch
    {
    }


    [ComImport]
    [Guid("E7A1AF80-4D96-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellFolderViewDual
    {
        object Application
        {
            [return: MarshalAs(UnmanagedType.IDispatch)]
            get;
        }
    }


    [ComImport]
    [Guid("A4C6892C-3BA9-11D2-9DEA-00C04FB16162")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellDispatch2
    {
        void ShellExecute(
            [MarshalAs(UnmanagedType.BStr)]
            string file,

            [MarshalAs(UnmanagedType.Struct)]
            object args,

            [MarshalAs(UnmanagedType.Struct)]
            object directory,

            [MarshalAs(UnmanagedType.Struct)]
            object operation,

            [MarshalAs(UnmanagedType.Struct)]
            object show);
    }

    #endregion
}
