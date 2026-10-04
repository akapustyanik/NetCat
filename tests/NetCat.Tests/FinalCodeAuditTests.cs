using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class FinalCodeAuditTests
{
    private sealed class ProbeTrust(string probe) : IExecutableTrustPolicy
    {
        public IDisposable AcquireExecutable(string path)
        {
            Assert.Equal(probe, path);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        public IDisposable AcquirePackage(string key, string folder) => throw new NotSupportedException();
    }

    [Fact]
    public async Task UnsignedSelfUpdateIsRejectedBeforeDownloadOrStaging()
    {
        var probe = Path.Combine(RoutingTests.FindRoot(), "tests", "NetCat.LifetimeProbe", "bin",
            new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net8.0-windows", "NetCat.LifetimeProbe.exe");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await ProcessHost.RunAsync(probe, ["unsigned-update-admission"], deadline.Token, trustPolicy: new ProbeTrust(probe));
        Assert.Equal(0, result.Code);
        Assert.Contains("unsigned-update-rejected-before-work", result.Output);
    }

    [Theory]
    [InlineData(WellKnownSidType.BuiltinUsersSid, FileSystemRights.Write)]
    [InlineData(WellKnownSidType.AuthenticatedUserSid, FileSystemRights.ChangePermissions)]
    [InlineData(WellKnownSidType.WorldSid, FileSystemRights.TakeOwnership)]
    [InlineData(WellKnownSidType.CreatorOwnerSid, FileSystemRights.Delete)]
    public void PrivilegedReceiptRejectsUnprivilegedMutationRights(WellKnownSidType sid, FileSystemRights rights)
    {
        var security = new FileSecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), rights, AccessControlType.Allow));
        Assert.Throws<InvalidDataException>(() => RuntimeTrustReceipt.RequireAdministratorControl(security));
    }

    [Fact]
    public void PrivilegedReceiptRejectsUserOwnerEvenWithAdministrativeDacl()
    {
        var security = new FileSecurity();
        security.SetOwner(WindowsIdentity.GetCurrent().User!);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        Assert.Throws<InvalidDataException>(() => RuntimeTrustReceipt.RequireAdministratorControl(security));
    }

    [Fact]
    public void PrivilegedReceiptAcceptsAdministrativeWritesAndPublicReadOnlyAccess()
    {
        var security = new FileSecurity();
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.SetOwner(administrators);
        security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        RuntimeTrustReceipt.RequireAdministratorControl(security);
    }

    [Fact]
    public void PrivilegedReceiptRejectsNullDacl()
    {
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm("O:BAD:NO_ACCESS_CONTROL");
        Assert.Throws<InvalidDataException>(() => RuntimeTrustReceipt.RequireAdministratorControl(security));
    }

    // Exercise the production isolated-probe lifecycle with real sing-box and
    // Windows file sharing. Measurement itself needs no external VPN endpoint.
    private static Task<int> Probe(RouterService router, Func<int, CancellationToken, Task<int>> measure, CancellationToken ct)
    {
        var method = typeof(RouterService).GetMethod("RunIsolatedProfileProbeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(int));
        return (Task<int>)method.Invoke(router, [ProfileImporter.ParseLink("socks://192.0.2.1:1080"),
            new AppSettings { Tun = false }, ct, false, 10, measure, (Func<string, int>)(_ => -1), false])!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedProbeFileDoesNotExhaustSlotsOrBlockLaterChecks(bool cancelMeasurement)
    {
        var root = Path.Combine(Path.GetTempPath(), "NetCat-AuditProbe-" + Guid.NewGuid().ToString("N"));
        var locks = new List<FileStream>();
        var children = new List<Process>();
        try
        {
            using var router = new RouterService(RoutingTests.ModuleRoot, root);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await Assert.ThrowsAsync<IOException>(() => Probe(router, (port, token) =>
                {
                    children.Add(Process.GetProcessById(LocalListener.Owner(port)));
                    var folder = Directory.GetDirectories(root, "probe-*").Single(p =>
                        !locks.Any(f => Path.GetDirectoryName(f.Name) == p));
                    locks.Add(new FileStream(Path.Combine(folder, "locked.fixture"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
                    if (cancelMeasurement) { deadline.Cancel(); token.ThrowIfCancellationRequested(); }
                    return Task.FromResult(42);
                }, deadline.Token));
            }

            foreach (var file in locks) file.Dispose();
            using var followupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.Equal(42, await Probe(router, (_, _) => Task.FromResult(42), followupDeadline.Token));
            Assert.All(children, child => Assert.True(child.WaitForExit(3000)));
            Assert.Equal(0, router.StartCount);
            Assert.False(router.VpnRequested);
            Assert.False(router.TunActive);
        }
        finally
        {
            foreach (var file in locks) file.Dispose();
            foreach (var child in children) child.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProbeCompletionDrainsOwnedCoreAndRemovesItsWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "NetCat-AuditDrain-" + Guid.NewGuid().ToString("N"));
        Process? child = null;
        int listenPort = 0;
        try
        {
            using var router = new RouterService(RoutingTests.ModuleRoot, root);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            Assert.Equal(42, await Probe(router, (port, _) =>
            {
                listenPort = port;
                child = Process.GetProcessById(LocalListener.Owner(port));
                Assert.False(child.HasExited);
                return Task.FromResult(42);
            }, deadline.Token));
            Assert.NotNull(child);
            Assert.True(child.HasExited);
            Assert.Equal(0, LocalListener.Owner(listenPort));
            Assert.Empty(Directory.GetDirectories(root, "probe-*"));
            Assert.False(router.Running);
            Assert.False(router.TunActive);
        }
        finally
        {
            child?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
