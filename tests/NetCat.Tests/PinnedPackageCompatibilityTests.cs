using NetCat.Engine;
using NetCat.Updater;
using Xunit;

namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed class PinnedPackageCompatibilityTests
{
    private static string Previous => Path.Combine(RoutingTests.FindRoot(), "artifacts", "previous-reviewed-modules");

    [Fact]
    public void PreviouslyReviewedPinnedPackagesRetainTrustAfterAppUpgrade()
    {
        Assert.True(File.Exists(Path.Combine(Previous, "sing-box", "sing-box.exe")), "The reviewed 1.0.2 test bundle is required.");
        using var core = ModuleIntegrity.Acquire("sing-box", Path.Combine(Previous, "sing-box"));
        using var telegram = ReviewedRuntimeTrust.AcquireTelegram(Path.Combine(Previous, "tg-runtime"));
        Assert.Throws<IOException>(() => File.Open(Path.Combine(Previous, "sing-box", "sing-box.exe"), FileMode.Open, FileAccess.Write).Dispose());
        Assert.Throws<IOException>(() => File.Open(Path.Combine(Previous, "tg-ws-proxy", "proxy", "tg_ws_proxy.py"), FileMode.Open, FileAccess.Write).Dispose());
    }

    [Fact]
    public void OldPackageHashesCannotBeMixedWithNewPackageOrTampered()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "NetCat-Pinned-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var key in new[] { "sing-box", "tg-ws-proxy" })
            {
                var source = Path.Combine(Previous, key);
                foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(temporary, key, Path.GetRelativePath(source, path));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target);
                }
            }
            var binary = Path.Combine(temporary, "sing-box", "sing-box.exe");
            using (var file = new FileStream(binary, FileMode.Open, FileAccess.ReadWrite))
            { file.Position = 512; var value = file.ReadByte(); file.Position = 512; file.WriteByte((byte)(value ^ 1)); }
            Assert.Throws<InvalidDataException>(() => ModuleIntegrity.Acquire("sing-box", Path.GetDirectoryName(binary)!).Dispose());
            var oldSource = Path.Combine(Previous, "tg-ws-proxy");
            var relative = Directory.EnumerateFiles(oldSource, "*.py", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(oldSource, path))
                .First(path => File.Exists(Path.Combine(RoutingTests.ModuleRoot, "tg-ws-proxy", path)) &&
                    !File.ReadAllBytes(Path.Combine(oldSource, path)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(RoutingTests.ModuleRoot, "tg-ws-proxy", path))));
            File.Copy(Path.Combine(RoutingTests.ModuleRoot, "tg-ws-proxy", relative), Path.Combine(temporary, "tg-ws-proxy", relative), true);
            Assert.Throws<InvalidDataException>(() => ReviewedRuntimeTrust.Acquire("tg-ws-proxy", Path.Combine(temporary, "tg-ws-proxy")).Dispose());
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    [Fact]
    public void LegacyPackageTrustDoesNotPermitAnUnapprovedUpstreamUpdate()
    {
        var release = new ModuleRelease("sing-box", "SagerNet/sing-box", "1.14.2", "sing-box-1.14.2-windows-amd64.zip",
            "https://github.com/SagerNet/sing-box/releases/download/v1.14.2/sing-box-1.14.2-windows-amd64.zip", new string('A', 64));
        var check = new ModuleCheck("sing-box", "1.14.1", release);
        Assert.True(check.Available);
        Assert.False(check.AutoUpdateSupported);
        Assert.False(check.InstallableUpdate);
    }
}
