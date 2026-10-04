using NetCat.Engine;
using Xunit;

namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed class Candidate32UserApprovedCoreTrustTests
    : IDisposable
{
    private readonly string root =
        Path.Combine(
            Path.GetTempPath(),
            "NetCat-C32-UserCore-" +
            Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if(Directory.Exists(root))
            Directory.Delete(
                root,
                true);
    }

    private string CreateCore(
        string module)
    {
        var folder =
            Path.Combine(
                root,
                module);

        Directory.CreateDirectory(folder);

        var executable =
            module=="sing-box"
                ? "sing-box.exe"
                : "xray.exe";

        File.WriteAllText(
            Path.Combine(
                folder,
                executable),
            module + "-binary");

        File.WriteAllText(
            Path.Combine(
                folder,
                "support.dll"),
            module + "-support");

        // sing-box currently receives the independently updated Wintun
        // dependency in its module directory. It must also remain covered
        // by the exact inventory.
        if(module=="sing-box")
        {
            File.WriteAllText(
                Path.Combine(
                    folder,
                    "wintun.dll"),
                "verified-wintun");
        }

        return folder;
    }

    private static string Repo(
        string module) =>
        module=="sing-box"
            ? "SagerNet/sing-box"
            : "XTLS/Xray-core";

    [Theory]
    [InlineData("sing-box")]
    [InlineData("xray")]
    public void ExactUserApprovedCoreIsAccepted(
        string module)
    {
        var folder=
            CreateCore(module);

        UserApprovedCoreTrust.Write(
            folder,
            module,
            Repo(module),
            "1.2.3",
            new string('A',64));

        using var lease=
            ModuleIntegrity.Acquire(
                module,
                folder);
    }

    [Theory]
    [InlineData("sing-box")]
    [InlineData("xray")]
    public void ModifiedUserApprovedCoreIsRejected(
        string module)
    {
        var folder=
            CreateCore(module);

        UserApprovedCoreTrust.Write(
            folder,
            module,
            Repo(module),
            "1.2.3",
            new string('A',64));

        File.AppendAllText(
            Path.Combine(
                folder,
                "support.dll"),
            "tampered");

        Assert.Throws<InvalidDataException>(
            () =>
                ModuleIntegrity.Acquire(
                    module,
                    folder));
    }

    [Theory]
    [InlineData("sing-box")]
    [InlineData("xray")]
    public void ExtraExecutableIsRejected(
        string module)
    {
        var folder=
            CreateCore(module);

        UserApprovedCoreTrust.Write(
            folder,
            module,
            Repo(module),
            "1.2.3",
            new string('A',64));

        File.WriteAllText(
            Path.Combine(
                folder,
                "unexpected.exe"),
            "unexpected");

        Assert.Throws<InvalidDataException>(
            () =>
                ModuleIntegrity.Acquire(
                    module,
                    folder));
    }

    [Fact]
    public void ForeignSingBoxRepositoryCannotCreateReceipt()
    {
        var folder=
            CreateCore("sing-box");

        Assert.Throws<InvalidDataException>(
            () =>
                UserApprovedCoreTrust.Write(
                    folder,
                    "sing-box",
                    "evil/sing-box",
                    "1.2.3",
                    new string('A',64)));
    }

    [Fact]
    public void InvalidArchiveDigestCannotCreateReceipt()
    {
        var folder=
            CreateCore("xray");

        Assert.Throws<InvalidDataException>(
            () =>
                UserApprovedCoreTrust.Write(
                    folder,
                    "xray",
                    "XTLS/Xray-core",
                    "1.2.3",
                    "not-a-sha256"));
    }

    [Fact]
    public void OpenVpnCannotUseUserApprovedCoreTrust()
    {
        var folder=
            Path.Combine(
                root,
                "openvpn");

        Directory.CreateDirectory(folder);

        Assert.False(
            UserApprovedCoreTrust.Supports(
                "openvpn"));

        Assert.Throws<InvalidDataException>(
            () =>
                UserApprovedCoreTrust.Write(
                    folder,
                    "openvpn",
                    "OpenVPN/openvpn",
                    "2.6.99",
                    new string('A',64)));
    }

    [Fact]
    public void VerifiedDependencyRefreshRebindsExactInventory()
    {
        var folder=
            CreateCore("sing-box");

        UserApprovedCoreTrust.Write(
            folder,
            "sing-box",
            "SagerNet/sing-box",
            "1.2.3",
            new string('A',64));

        File.WriteAllText(
            Path.Combine(
                folder,
                "wintun.dll"),
            "new-verified-wintun");

        Assert.Throws<InvalidDataException>(
            () =>
                ModuleIntegrity.Acquire(
                    "sing-box",
                    folder));

        UserApprovedCoreTrust
            .RefreshAfterVerifiedDependencyUpdate(
                "sing-box",
                folder);

        using var lease=
            ModuleIntegrity.Acquire(
                "sing-box",
                folder);
    }
}