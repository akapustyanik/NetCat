using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using NetCat.Core;
using NetCat.Updater;

if (args is ["--self-recovery"])
{
    var installed = Path.GetDirectoryName(Environment.ProcessPath!)!;
    bool blocked = false;
    try { DurableUpdate.Recover(installed); }
    catch (IOException error) { blocked = error.Message.Contains("внешний помощник"); }
    bool present = File.Exists(Path.Combine(installed, "NetCat.exe"));
    bool displaced = File.Exists(Path.Combine(installed, "NetCat.exe.netcat-displaced"));
    Console.WriteLine(JsonSerializer.Serialize(new { SelfRecoveryBlocked = blocked, TargetPresent = present, Displaced = displaced }));
    Environment.ExitCode = blocked && present && !displaced ? 0 : 1;
    return;
}
if (args is not ["run"]) return; // a fixture task can never start real NetCat
if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
    throw new InvalidOperationException("Run only in the elevated disposable guest.");
var root = Path.Combine(Path.GetTempPath(), "NetCat-IndependentNative-" + Guid.NewGuid().ToString("N"));
var tasks = new FixtureTasks(new WindowsStartupTasks(taskNameOverride: "NetCat_Audit_" + Guid.NewGuid().ToString("N")));
try
{
    Directory.CreateDirectory(root);
    var installed = Path.Combine(root, "installed"); Directory.CreateDirectory(installed);
    var exe = Path.Combine(installed, "NetCat.exe"); File.Copy(Environment.ProcessPath!, exe);
    var id = Guid.NewGuid().ToString("N"); var backup = Path.Combine(installed, "metadata", "rollback", id, "NetCat.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(exe, backup);
    using (var file = new FileStream(backup, FileMode.Append)) file.Write("old-fixture-overlay"u8);
    string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    var before = Hash(exe); var expected = Hash(backup);
    var journal = new UpdateJournal(1, installed, id, null, UpdatePhase.Applying,
        new() { ["netcat"] = "1.0.0" }, new() { ["netcat"] = "2.0.0" }, [new("NetCat.exe", true, expected)]);
    File.WriteAllText(Path.Combine(installed, DurableUpdate.JournalPath), JsonSerializer.Serialize(journal, JsonSettings.Options));
    var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true };
    start.ArgumentList.Add("--self-recovery");
    using var child = Process.Start(start)!;
    var details = await child.StandardOutput.ReadToEndAsync();
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
    var selfBlocked = child.ExitCode == 0 && Hash(exe) == before;
    if (!selfBlocked) throw new InvalidOperationException("Running-image recovery changed or removed launch path.");
    DurableUpdate.Recover(installed); DurableUpdate.Recover(installed);
    var externalRecovered = Hash(exe) == expected && DurableUpdate.Read(installed) == null;

    var sid = WindowsIdentity.GetCurrent().User!.Value;
    var a = new AutostartService(tasks, exe, sid);
    var b = new AutostartService(tasks, Environment.ProcessPath!, sid);
    await a.SetAsync(true, _ => Task.CompletedTask);
    await b.ReconcileAsync(true, _ => Task.CompletedTask);
    var ownerPreserved = a.Enabled && !b.Enabled;
    await b.ReconcileAsync(false, _ => Task.CompletedTask);
    ownerPreserved &= a.Enabled;
    await b.SetAsync(true, _ => Task.CompletedTask);
    var explicitTransfer = b.Enabled && !a.Enabled;
    await b.SetAsync(false, _ => Task.CompletedTask);
    var taskRemoved = tasks.Read() == null;

    var store = new SettingsStore(Path.Combine(root, "settings")); var messages = new List<string>(); store.Diagnostic += messages.Add;
    store.SaveDesiredState(new() { MainVpnEnabled = true, OpenVpnEnabled = true });
    File.WriteAllText(Path.Combine(store.Root, "desired-state.json"), "{broken");
    var restored = store.LoadDesiredState();
    var intentRecovered = restored.MainVpnEnabled && restored.OpenVpnEnabled && store.DesiredRecoveredFromBackup && messages.Count == 1;
    Console.WriteLine(JsonSerializer.Serialize(new { Elevated = true, SelfRecoveryBlocked = selfBlocked, SelfRecovery = JsonDocument.Parse(details).RootElement,
        ExternalRecoveryRestoredBytes = externalRecovered, ForeignAutostartOwnerPreserved = ownerPreserved, ExplicitAutostartTransfer = explicitTransfer,
        OwnedTestTaskRemoved = taskRemoved, DesiredIntentRecovered = intentRecovered, SignedRecoveryIpcEndToEnd = "NOT VERIFIED: Authenticode signing identity unavailable" }));
    Environment.ExitCode = externalRecovered && ownerPreserved && explicitTransfer && taskRemoved && intentRecovered ? 0 : 1;
}
finally { tasks.Delete(); if (Directory.Exists(root)) Directory.Delete(root, true); }

sealed class FixtureTasks(WindowsStartupTasks owned) : IStartupTasks
{
    public StartupTask? Read() => owned.Read();
    public void Write(StartupTask task) => owned.Write(task);
    public void Delete() => owned.Delete();
    public void RemoveLegacyEntries() { } // do not touch any real user's registration
}
