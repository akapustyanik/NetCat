using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using NetCat.Engine;
using NetCat.Core;
using NetCat.Updater;
using System.Net;
using System.Net.Sockets;

var root = Path.Combine(Path.GetTempPath(), "NetCat-Receipt-Audit-" + Guid.NewGuid().ToString("N"));
var results = new List<object>();
bool allPassed = true;
var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
if (!admin) throw new InvalidOperationException("This acceptance requires the elevated guest process.");
try
{
    foreach (var module in new[] { "sing-box", "xray", "zapret", "tg-ws-proxy" })
    {
        var folder = Path.Combine(root, module); Directory.CreateDirectory(folder);
        var file = module switch { "sing-box" => "sing-box.exe", "xray" => "xray.exe", "zapret" => "winws.exe", _ => "proxy.py" };
        var bytes = "harmless audit fixture - never executed"u8.ToArray();
        File.WriteAllBytes(Path.Combine(folder, file), bytes);
        var repo = module switch { "sing-box" => "SagerNet/sing-box", "xray" => "XTLS/Xray-core", "zapret" => "Flowseal/zapret-discord-youtube", _ => "Flowseal/tg-ws-proxy" };
        var core = module is "sing-box" or "xray";
        var receipt = Path.Combine(folder, core ? UserApprovedCoreTrust.ReceiptName : UpstreamRuntimeTrust.ReceiptName);
        var metadata = new Dictionary<string, object> { ["Schema"] = 1, ["Module"] = module, ["Repository"] = repo, ["Version"] = "1.2.3",
            [core ? "ArchiveSha256" : "SourceDigest"] = new string('A', module == "tg-ws-proxy" ? 40 : 64),
            ["Files"] = new Dictionary<string, string> { [file] = Convert.ToHexString(SHA256.HashData(bytes)) } };
        File.WriteAllText(receipt, JsonSerializer.Serialize(metadata));
        var user = WindowsIdentity.GetCurrent().User!;
        var acl = new FileSecurity(); acl.SetOwner(user); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(receipt).SetAccessControl(acl);
        bool forgedAccepted;
        try { using var lease = core ? ModuleIntegrity.Acquire(module, folder) : ReviewedRuntimeTrust.Acquire(module, folder); forgedAccepted = true; }
        catch (InvalidDataException) { forgedAccepted = false; }
        var bin = Path.Combine(root, "install-" + module, "modules");
        var prepared = Path.Combine(bin, ".prepared", module); Directory.CreateDirectory(prepared);
        foreach (var source in Directory.GetFiles(folder)) { var destination = Path.Combine(prepared, Path.GetFileName(source)); File.Copy(source, destination); var copyAcl = new FileSecurity(); copyAcl.SetSecurityDescriptorBinaryForm(new FileInfo(source).GetAccessControl().GetSecurityDescriptorBinaryForm()); new FileInfo(destination).SetAccessControl(copyAcl); }
        new FileInfo(Path.Combine(prepared, Path.GetFileName(receipt))).SetAccessControl(acl);
        var asset = module == "tg-ws-proxy" ? "headless source" : "fixture.zip";
        var release = new ModuleRelease(module, repo, "9.0.0", asset, "https://github.com/" + repo +
            (module == "tg-ws-proxy" ? "/tree/" + new string('A', 40) : "/releases/download/v9.0.0/fixture.zip"),
            new string('A', 64), module == "tg-ws-proxy" ? new string('A', 40) : "");
        File.WriteAllText(Path.Combine(prepared, "netcat-source.json"), JsonSerializer.Serialize(release, JsonSettings.Options));
        var files = Directory.GetFiles(prepared).ToDictionary(p => Path.GetFileName(p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        File.WriteAllText(Path.Combine(prepared, "prepared.json"), JsonSerializer.Serialize(new { Release = release, Files = files, SelectedVersion = true }, JsonSettings.Options));
        bool forgedInstalled;
        using (var updater = new ModuleUpdater(bin))
        {
            try { await updater.InstallPreparedAsync(release, new AppSettings(), CancellationToken.None); forgedInstalled = true; }
            catch (InvalidDataException) { forgedInstalled = false; }
        }
        // The legitimate elevated API must establish verifiable local approval.
        if (core) UserApprovedCoreTrust.Write(folder, module, repo, "1.2.3", new string('A', 64));
        else UpstreamRuntimeTrust.Write(folder, module, repo, "1.2.3", new string('A', module == "tg-ws-proxy" ? 40 : 64));
        bool approvedAccepted;
        try { using var lease = core ? ModuleIntegrity.Acquire(module, folder) : ReviewedRuntimeTrust.Acquire(module, folder); approvedAccepted = true; }
        catch (InvalidDataException) { approvedAccepted = false; }
        Directory.Delete(prepared, true); Directory.CreateDirectory(prepared);
        foreach (var source in Directory.GetFiles(folder)) { var destination = Path.Combine(prepared, Path.GetFileName(source)); File.Copy(source, destination); var copyAcl = new FileSecurity(); copyAcl.SetSecurityDescriptorBinaryForm(new FileInfo(source).GetAccessControl().GetSecurityDescriptorBinaryForm()); new FileInfo(destination).SetAccessControl(copyAcl); }
        File.WriteAllText(Path.Combine(prepared, "netcat-source.json"), JsonSerializer.Serialize(release, JsonSettings.Options));
        files = Directory.GetFiles(prepared).ToDictionary(p => Path.GetFileName(p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        File.WriteAllText(Path.Combine(prepared, "prepared.json"), JsonSerializer.Serialize(new { Release = release, Files = files, SelectedVersion = true }, JsonSettings.Options));
        bool approvedInstalled; string? installFailure = null;
        using (var updater = new ModuleUpdater(bin))
        {
            try { await updater.InstallPreparedAsync(release, new AppSettings(), CancellationToken.None); approvedInstalled = true; }
            catch (InvalidDataException error) { approvedInstalled = false; installFailure = error.Message + " | " + error.StackTrace; }
        }
        results.Add(new { Module = module, ForgedAccepted = forgedAccepted, ForgedInstalled = forgedInstalled, ApprovedAccepted = approvedAccepted, ApprovedInstalled = approvedInstalled, InstallFailure = installFailure });
        allPassed &= !forgedAccepted && !forgedInstalled && approvedAccepted && approvedInstalled;
    }
    using var proxy = new TcpListener(IPAddress.Loopback, 0); proxy.Start();
    using var stop = new CancellationTokenSource(); int requests = 0;
    var serving = Task.Run(async () => {
        try { while (!stop.IsCancellationRequested) { using var client = await proxy.AcceptTcpClientAsync(stop.Token); Interlocked.Increment(ref requests); } }
        catch (OperationCanceledException) { }
    });
    bool unsignedAppRejected = false;
    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
    {
        try { await PortableUpdate.PrepareAsync(new("netcat", "akapustyanik/NetCat", "9.0.0", "NetCat-v9.0.0-win-x64.zip",
            "https://github.com/akapustyanik/NetCat/releases/download/v9.0.0/NetCat-v9.0.0-win-x64.zip", new string('A', 64)),
            root, new HashSet<string>(), timeout.Token, ((IPEndPoint)proxy.LocalEndpoint).Port); }
        catch (Exception error) { unsignedAppRejected = error is InvalidDataException && error.Message.Contains("подписанную"); }
    }
    stop.Cancel(); await serving;
    Console.WriteLine(JsonSerializer.Serialize(new { Elevated = admin, Results = results, UnsignedAppRejectedBeforeStaging = unsignedAppRejected, UpdateNetworkRequests = requests }));
    Environment.ExitCode = allPassed && unsignedAppRejected && requests == 0 ? 0 : 1;
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
