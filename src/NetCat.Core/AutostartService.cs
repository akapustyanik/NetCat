using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace NetCat.Core;

public sealed record StartupTask(string Executable, string UserSid, bool Enabled = true,
    bool Highest = true, bool Interactive = true, string Arguments = "");

public interface IStartupTasks
{
    bool HasLegacyEntry => false;
    StartupTask? Read();
    void Write(StartupTask task);
    void Delete();
    void RemoveLegacyEntries();
}

// Persistence is injected so a failed task registration never commits an enabled switch.
public sealed class AutostartService(IStartupTasks tasks, string executable, string sid)
{
    private StartupTask Expected => new(Path.GetFullPath(executable), sid, Arguments: "--autostart");
    public bool Enabled => IsMatch(tasks.Read(), Expected);
    public StartupTask? RegisteredTask => tasks.Read();
    private bool Owns(StartupTask task) => !string.IsNullOrWhiteSpace(task.Executable) &&
        Path.GetFullPath(task.Executable.Trim('"', ' ')).Equals(Expected.Executable, StringComparison.OrdinalIgnoreCase);

    public static bool IsMatch(StartupTask? actual, StartupTask expected)
    {
        if (actual == null) return false;
        var actualExe = Path.GetFullPath(actual.Executable.Trim('"', ' '));
        var expectedExe = Path.GetFullPath(expected.Executable.Trim('"', ' '));
        if (!string.Equals(actualExe, expectedExe, StringComparison.OrdinalIgnoreCase)) return false;
        if (actual.Enabled != expected.Enabled) return false;
        if (actual.Highest != expected.Highest) return false;
        if (actual.Interactive != expected.Interactive) return false;
        if (!string.Equals(actual.Arguments?.Trim() ?? "", expected.Arguments?.Trim() ?? "", StringComparison.Ordinal)) return false;

        if (string.Equals(actual.UserSid, expected.UserSid, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var expectedAccount = new SecurityIdentifier(expected.UserSid).Translate(typeof(NTAccount)).Value;
            if (string.Equals(actual.UserSid, expectedAccount, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        try
        {
            var actualSid = new NTAccount(actual.UserSid).Translate(typeof(SecurityIdentifier)).Value;
            if (string.Equals(actualSid, expected.UserSid, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    public async Task SetAsync(bool enabled, Func<bool, Task> save)
    {
        var previous = tasks.Read();
        if (!enabled && previous != null && !Owns(previous))
            throw new InvalidOperationException("Автозапуск принадлежит другой копии NetCat. Его отключение из этой копии заблокировано.");
        try
        {
            if (enabled) tasks.Write(Expected); else tasks.Delete();
            if (enabled ? !Enabled : tasks.Read() != null) throw new IOException("Проверка задачи автозапуска NetCat не пройдена.");
            await save(enabled);
        }
        catch
        {
            if (previous == null) tasks.Delete(); else tasks.Write(previous);
            throw;
        }
        tasks.RemoveLegacyEntries();
    }

    public async Task ReconcileAsync(bool? desired, Func<bool, Task> save)
    {
        // Compatibility/migration API; observing a foreign owner never transfers
        // or deletes it. UI refresh only reads RegisteredTask/Enabled.
        if (tasks.Read() is { } registered && !Owns(registered)) return;
        if (desired == true || desired == null && (tasks.Read() != null || tasks.HasLegacyEntry))
            await SetAsync(true, save);
        else if (desired == false && (tasks.Read() != null || tasks.HasLegacyEntry))
            await SetAsync(false, save);
    }
}

public sealed class WindowsStartupTasks(Action<string>? log = null, string? taskNameOverride = null, bool userFacingJournal = false) : IStartupTasks
{
    public string TaskName => taskNameOverride ?? ("NetCat_AutoStart_" + WindowsIdentity.GetCurrent().User!.Value);

    public bool HasLegacyEntry
    {
        get
        {
            if (LegacyShortcuts().Any()) return true;
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (new[] { "NetCat", "NetCat.Focus" }.Any(name => IsNetCatCommand(run?.GetValue(name) as string))) return true;
            dynamic service = Connect(); dynamic folder = service.GetFolder("\\");
            try { dynamic old = folder.GetTask("NetCat_AutoStart"); return LegacyTaskOwned(old); }
            catch (Exception e) when ((uint)e.HResult == 0x80070002 || e is FileNotFoundException) { return false; }
            finally { Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
        }
    }

    private static IEnumerable<string> LegacyShortcuts()
    {
        var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        foreach (var name in new[] { "NetCat.lnk", "NetCat.Focus.lnk" })
        {
            var path = Path.Combine(startup, name); if (!File.Exists(path)) continue;
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
            dynamic link = shell.CreateShortcut(path);
            bool owned;
            try { owned = Path.GetFileName((string)link.TargetPath).Equals("NetCat.exe", StringComparison.OrdinalIgnoreCase); }
            finally { Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell); }
            if (owned) yield return path;
        }
    }

    private static bool IsNetCatCommand(string? command) => command != null &&
        System.Text.RegularExpressions.Regex.IsMatch(command, "^\\s*(?:\"[^\"]*[\\\\/]NetCat\\.exe\"|[^\"]*[\\\\/]NetCat\\.exe)(?:\\s|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool LegacyTaskOwned(dynamic old)
    {
        dynamic d = old.Definition; var user = (string)d.Principal.UserId;
        using var current = WindowsIdentity.GetCurrent();
        return (user == current.User!.Value || user.Equals(current.Name, StringComparison.OrdinalIgnoreCase)) &&
            (int)d.Actions.Count == 1 && Path.GetFileName((string)d.Actions.Item(1).Path).Equals("NetCat.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static dynamic Connect()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
        service.Connect(); return service;
    }

    public StartupTask? Read()
    {
        dynamic service = Connect(); dynamic folder = service.GetFolder("\\");
        try
        {
            dynamic task = folder.GetTask(TaskName); dynamic d = task.Definition;
            if ((int)d.Actions.Count != 1 || (int)d.Actions.Item(1).Type != 0 || (int)d.Triggers.Count != 1 || (int)d.Triggers.Item(1).Type != 9) return new("", "", false);
            var path = (string)d.Actions.Item(1).Path;
            var args = (string)d.Actions.Item(1).Arguments;
            if (string.IsNullOrEmpty(args) && path.StartsWith("\"") && path.Contains("\" "))
            {
                var split = path.IndexOf("\" ", StringComparison.Ordinal);
                args = path[(split + 2)..].Trim();
                path = path[1..split];
            }
            return new(path, (string)d.Triggers.Item(1).UserId, (bool)d.Settings.Enabled, (int)d.Principal.RunLevel == 1, (int)d.Principal.LogonType == 3, args);
        }
        catch (Exception e) when ((uint)e.HResult == 0x80070002 || e is FileNotFoundException) { return null; }
        finally { Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }

    public void Write(StartupTask task)
    {
        var stages = new List<string>();
        void Stage(string s)
        {
            stages.Add(s);
            if (!userFacingJournal) log?.Invoke(s);
        }
        Stage("AUTOSTART stage=connect");
        dynamic service;
        try { service = Connect(); }
        catch (Exception e) { LogFailure("connect", e, task, stages); throw; }

        Stage("AUTOSTART stage=get-root-folder");
        dynamic folder;
        try { folder = service.GetFolder("\\"); }
        catch (Exception e) { LogFailure("get-root-folder", e, task, stages); Marshal.FinalReleaseComObject(service); throw; }

        try
        {
            Stage("AUTOSTART stage=new-task");
            dynamic d = service.NewTask(0);

            Stage("AUTOSTART stage=principal");
            d.RegistrationInfo.Description = "NetCat: interactive logon, no stored password";
            d.Principal.UserId = task.UserSid;
            d.Principal.LogonType = 3;
            d.Principal.RunLevel = task.Highest ? 1 : 0;
            d.Settings.Enabled = task.Enabled;
            d.Settings.ExecutionTimeLimit = "PT0S";
            d.Settings.DisallowStartIfOnBatteries = false;
            d.Settings.StopIfGoingOnBatteries = false;
            d.Settings.MultipleInstances = 2;
            d.Settings.StartWhenAvailable = true;

            Stage("AUTOSTART stage=trigger");
            dynamic trigger = d.Triggers.Create(9);
            trigger.UserId = task.UserSid;

            Stage("AUTOSTART stage=action");
            dynamic action = d.Actions.Create(0);
            action.Path = task.Executable;
            action.Arguments = task.Arguments;
            var dir = Path.GetDirectoryName(task.Executable);
            if (!string.IsNullOrEmpty(dir)) action.WorkingDirectory = dir;

            Stage("AUTOSTART stage=register");
            try
            {
                // Passing null for userId and password binds to current user's interactive token
                folder.RegisterTaskDefinition(TaskName, d, 6, null, null, 3, null);
            }
            catch (Exception e) when (e is COMException || e is UnauthorizedAccessException || e is FileNotFoundException || (uint)e.HResult == 0x80070002 || (uint)e.HResult == 0x80070005)
            {
                LogFailure("register", e, task, stages);
                Stage("AUTOSTART stage=register-fallback-schtasks");
                TrySchtasksCreate(TaskName, task.Executable, task.Arguments, task.Highest, Stage);
            }

            Stage("AUTOSTART stage=verify");
            var verified = Read();
            if (verified == null || !AutostartService.IsMatch(verified, task))
            {
                Stage($"AUTOSTART stage=verify failed: verified={(verified == null ? "null" : verified.ToString())}");
                foreach (var st in stages) log?.Invoke(st);
                throw new IOException($"Проверка созданной задачи автозапуска '{TaskName}' не пройдена.");
            }
            log?.Invoke("Автозапуск: задача проверена");
        }
        catch (Exception e)
        {
            LogFailure("general", e, task, stages);
            throw;
        }
        finally
        {
            Marshal.FinalReleaseComObject(folder);
            Marshal.FinalReleaseComObject(service);
        }
    }

    private void LogFailure(string stage, Exception e, StartupTask task, List<string>? stages = null)
    {
        if (stages != null)
        {
            foreach (var st in stages) log?.Invoke(st);
        }
        var dir = Path.GetDirectoryName(task.Executable);
        var dirExists = !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
        log?.Invoke($"AUTOSTART Exception: stage={stage}, HRESULT=0x{e.HResult:X8}, TaskName={TaskName}, Exe={task.Executable}, DirExists={dirExists}, Sid={task.UserSid}");
    }

    private static void TrySchtasksCreate(string taskName, string executable, string arguments, bool highest, Action<string>? log)
    {
        var fullExe = Path.GetFullPath(executable);
        var innerTr = string.IsNullOrWhiteSpace(arguments)
            ? $"\\\"{fullExe}\\\""
            : $"\\\"{fullExe}\\\" {arguments}";

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        psi.ArgumentList.Add("/Create");
        psi.ArgumentList.Add("/F");
        psi.ArgumentList.Add("/TN");
        psi.ArgumentList.Add(taskName);
        psi.ArgumentList.Add("/TR");
        psi.ArgumentList.Add(innerTr);
        psi.ArgumentList.Add("/SC");
        psi.ArgumentList.Add("ONLOGON");
        if (highest)
        {
            psi.ArgumentList.Add("/RL");
            psi.ArgumentList.Add("HIGHEST");
        }

        log?.Invoke($"AUTOSTART schtasks command: schtasks.exe /Create /F /TN \"{taskName}\" /TR \"{innerTr}\" /SC ONLOGON {(highest ? "/RL HIGHEST" : "")}");
        using var executableLease = WindowsExecutableTrust.Acquire(psi.FileName);
        using var proc = Process.Start(psi);
        if (proc == null) throw new IOException("Не удалось запустить schtasks.exe");
        var stderr = proc.StandardError.ReadToEnd();
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            log?.Invoke($"AUTOSTART schtasks failed: code={proc.ExitCode}, err={stderr.Trim()}, out={stdout.Trim()}");
            throw new IOException($"schtasks.exe завершился с кодом {proc.ExitCode}: {stderr.Trim()} {stdout.Trim()}");
        }
    }

    public void Delete()
    {
        dynamic service = Connect(); dynamic folder = service.GetFolder("\\");
        try { folder.DeleteTask(TaskName, 0); }
        catch (Exception e) when ((uint)e.HResult == 0x80070002 || e is FileNotFoundException) { }
        finally { Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }

    public void RemoveLegacyEntries()
    {
        foreach (var shortcut in LegacyShortcuts()) File.Delete(shortcut);
        using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        // Exact names only. Never scan or remove other applications' startup values.
        foreach (var name in new[] { "NetCat", "NetCat.Focus" }) if (IsNetCatCommand(run?.GetValue(name) as string)) run?.DeleteValue(name, false);
        dynamic service = Connect(); dynamic folder = service.GetFolder("\\");
        try
        {
            dynamic old = folder.GetTask("NetCat_AutoStart");
            if (LegacyTaskOwned(old))
                folder.DeleteTask("NetCat_AutoStart", 0);
        }
        catch (Exception e) when ((uint)e.HResult == 0x80070002 || e is FileNotFoundException) { }
        finally { Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }
}
