using System.Security.Cryptography;
using System.Text.Json;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Updater;
using Xunit;

namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed class Candidate32ForcedCoreInstallTests
    : IDisposable
{
    private readonly string appRoot =
        Path.Combine(
            Path.GetTempPath(),
            "NetCat-C32-ForceCore-" +
            Guid.NewGuid().ToString("N"));

    private string Bin =>
        Path.Combine(
            appRoot,
            "modules");

    private sealed record PreparedShape(
        ModuleRelease Release,
        Dictionary<string,string> Files,
        bool SelectedVersion=false);

    public void Dispose()
    {
        if(Directory.Exists(appRoot))
            Directory.Delete(
                appRoot,
                true);
    }

    private static string Repository(
        string key) =>
        key=="sing-box"
            ? "SagerNet/sing-box"
            : "XTLS/Xray-core";

    private static string Asset(
        string key,
        string version)
    {
        var normalized =
            version.TrimStart('v','V');

        return key=="sing-box"
            ? $"sing-box-{normalized}-windows-amd64.zip"
            : "Xray-windows-64.zip";
    }

    private static ModuleRelease Release(
        string key,
        string version,
        char hash) =>
        new(
            key,
            Repository(key),
            version,
            Asset(key,version),
            $"https://github.com/{Repository(key)}/releases/download/{version}/{Asset(key,version)}",
            new string(hash,64));

    private static string Hash(
        string path)
    {
        using var stream =
            File.OpenRead(path);

        return Convert.ToHexString(
            SHA256.HashData(stream));
    }

    private void CreateInstalled(
        ModuleRelease release,
        string marker)
    {
        var folder =
            Path.Combine(
                Bin,
                release.Key);

        Directory.CreateDirectory(folder);

        var executable =
            release.Key=="sing-box"
                ? "sing-box.exe"
                : "xray.exe";

        File.WriteAllText(
            Path.Combine(
                folder,
                executable),
            "exe-" + marker);

        File.WriteAllText(
            Path.Combine(
                folder,
                "support.dll"),
            "dll-" + marker);

        if(release.Key=="sing-box")
        {
            File.WriteAllText(
                Path.Combine(
                    folder,
                    "wintun.dll"),
                "wintun-" + marker);
        }

        File.WriteAllText(
            Path.Combine(
                folder,
                "marker.txt"),
            marker);

        UserApprovedCoreTrust.Write(
            folder,
            release.Key,
            release.Repository,
            release.Version,
            release.Sha256);

        File.WriteAllText(
            Path.Combine(
                folder,
                "netcat-source.json"),
            JsonSerializer.Serialize(
                release,
                JsonSettings.Options));
    }

    private string CreatePrepared(
        ModuleRelease release,
        string marker,
        bool selectedVersion=true)
    {
        var folder =
            Path.Combine(
                Bin,
                ".prepared",
                release.Key);

        if(Directory.Exists(folder))
        {
            Directory.Delete(
                folder,
                true);
        }

        Directory.CreateDirectory(folder);

        var executable =
            release.Key=="sing-box"
                ? "sing-box.exe"
                : "xray.exe";

        File.WriteAllText(
            Path.Combine(
                folder,
                executable),
            "exe-" + marker);

        File.WriteAllText(
            Path.Combine(
                folder,
                "support.dll"),
            "dll-" + marker);

        if(release.Key=="sing-box")
        {
            File.WriteAllText(
                Path.Combine(
                    folder,
                    "wintun.dll"),
                "wintun-" + marker);
        }

        File.WriteAllText(
            Path.Combine(
                folder,
                "marker.txt"),
            marker);

        UserApprovedCoreTrust.Write(
            folder,
            release.Key,
            release.Repository,
            release.Version,
            release.Sha256);

        File.WriteAllText(
            Path.Combine(
                folder,
                "netcat-source.json"),
            JsonSerializer.Serialize(
                release,
                JsonSettings.Options));

        var files =
            Directory.GetFiles(
                    folder,
                    "*",
                    SearchOption.AllDirectories)
                .ToDictionary(
                    path =>
                        Path.GetRelativePath(
                                folder,
                                path)
                            .Replace('\\','/'),
                    Hash,
                    StringComparer.OrdinalIgnoreCase);

        // Same wire shape as ModuleUpdater.PreparedModule.
        File.WriteAllText(
            Path.Combine(
                folder,
                "prepared.json"),
            JsonSerializer.Serialize(
                new PreparedShape(
                    release,
                    files,
                    selectedVersion),
                JsonSettings.Options));

        return folder;
    }


    [Theory]
    [InlineData("sing-box")]
    [InlineData("xray")]
    public void CoreSupportsExplicitVersionSelection(
        string key)
    {
        Assert.True(
            ModuleUpdater.SupportsVersionSelection(
                key));

        var release =
            Release(
                key,
                "v1.2.3",
                'A');

        Assert.True(
            ModuleUpdater.IsTrustedCoreUpstreamRelease(
                release));

        Assert.True(
            ModuleUpdater.RequiresForceUnreviewed(
                release));
    }


    [Theory]
    [InlineData("sing-box")]
    [InlineData("xray")]
    public void MissingDigestCannotBeForceTrusted(
        string key)
    {
        var release =
            Release(
                key,
                "v1.2.3",
                'A')
            with
            {
                Sha256=""
            };

        Assert.False(
            ModuleUpdater.IsTrustedCoreUpstreamRelease(
                release));
    }


    [Fact]
    public void ForeignRepositoryCannotBeForceTrusted()
    {
        var release =
            new ModuleRelease(
                "sing-box",
                "evil/sing-box",
                "v1.2.3",
                "sing-box-1.2.3-windows-amd64.zip",
                "https://github.com/evil/sing-box/releases/download/v1.2.3/sing-box-1.2.3-windows-amd64.zip",
                new string('A',64));

        Assert.False(
            ModuleUpdater.IsTrustedCoreUpstreamRelease(
                release));

        Assert.False(
            ModuleUpdater.RequiresForceUnreviewed(
                release));
    }


    [Theory]
    [InlineData("sing-box")]
    [InlineData("xray")]
    public async Task SelectedUnreviewedCoreRequiresExplicitForce(
        string key)
    {
        Directory.CreateDirectory(Bin);

        var release =
            Release(
                key,
                "v9.9.9",
                'A');

        using var updater =
            new ModuleUpdater(Bin);

        var error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () =>
                    updater.InstallAsync(
                        release,
                        new AppSettings(),
                        CancellationToken.None,
                        prepareOnly:true,
                        selectedVersion:true,
                        forceUnreviewed:false));

        Assert.Contains(
            "принуд",
            error.Message.ToLowerInvariant());
    }


    [Theory]
    [InlineData("sing-box")]
    [InlineData("xray")]
    public async Task ForceCannotBypassSelectedVersionFlow(
        string key)
    {
        Directory.CreateDirectory(Bin);

        var release =
            Release(
                key,
                "v9.9.9",
                'A');

        using var updater =
            new ModuleUpdater(Bin);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                updater.InstallAsync(
                    release,
                    new AppSettings(),
                    CancellationToken.None,
                    prepareOnly:true,
                    selectedVersion:false,
                    forceUnreviewed:true));
    }


    [Fact]
    public async Task ForceFlagCannotBeUsedForZapret()
    {
        Directory.CreateDirectory(Bin);

        var release =
            new ModuleRelease(
                "zapret",
                "Flowseal/zapret-discord-youtube",
                "1.10.3",
                "zapret-1.10.3.zip",
                "https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.3/zapret-1.10.3.zip",
                new string('A',64));

        using var updater =
            new ModuleUpdater(Bin);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                updater.InstallAsync(
                    release,
                    new AppSettings(),
                    CancellationToken.None,
                    prepareOnly:true,
                    selectedVersion:true,
                    forceUnreviewed:true));
    }


    [Theory]
    [InlineData("sing-box")]
    [InlineData("xray")]
    public async Task UserApprovedPreparedCoreCanDowngradeWhilePinned(
        string key)
    {
        Directory.CreateDirectory(Bin);

        var current =
            Release(
                key,
                "v2.0.0",
                'A');

        var selected =
            Release(
                key,
                "v1.2.3",
                'B');

        CreateInstalled(
            current,
            "current-v2");

        CreatePrepared(
            selected,
            "selected-v123");

        var settings =
            new AppSettings();

        settings.PinnedModules.Add(key);

        using var updater =
            new ModuleUpdater(Bin);

        await updater.InstallPreparedAsync(
            selected,
            settings,
            CancellationToken.None);

        Assert.Equal(
            "v1.2.3",
            updater.InstalledVersion(key));

        Assert.True(
            updater.CanRollback(key));

        Assert.Equal(
            "v2.0.0",
            updater.PreviousVersion(key));

        // Explicit selection changes the installed version but does
        // not silently remove the user's pin.
        Assert.Contains(
            key,
            settings.PinnedModules);

        using var lease =
            ModuleIntegrity.Acquire(
                key,
                Path.Combine(
                    Bin,
                    key));
    }


    [Theory]
    [InlineData("sing-box")]
    [InlineData("xray")]
    public async Task PreparedReceiptWithoutSelectedIntentCannotAuthorizeCore(
        string key)
    {
        Directory.CreateDirectory(Bin);

        var current =
            Release(
                key,
                "v1.0.0",
                'A');

        var next =
            Release(
                key,
                "v2.0.0",
                'B');

        CreateInstalled(
            current,
            "current");

        CreatePrepared(
            next,
            "next",
            selectedVersion:false);

        using var updater =
            new ModuleUpdater(Bin);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                updater.InstallPreparedAsync(
                    next,
                    new AppSettings(),
                    CancellationToken.None));

        Assert.Equal(
            "v1.0.0",
            updater.InstalledVersion(key));
    }


    [Theory]
    [InlineData("sing-box")]
    [InlineData("xray")]
    public async Task TamperedPreparedCoreNeverReplacesCurrent(
        string key)
    {
        Directory.CreateDirectory(Bin);

        var current =
            Release(
                key,
                "v2.0.0",
                'A');

        var next =
            Release(
                key,
                "v2.1.0",
                'B');

        CreateInstalled(
            current,
            "must-survive");

        var prepared =
            CreatePrepared(
                next,
                "next");

        var executable =
            key=="sing-box"
                ? "sing-box.exe"
                : "xray.exe";

        File.AppendAllText(
            Path.Combine(
                prepared,
                executable),
            "tampered");

        using var updater =
            new ModuleUpdater(Bin);

        await Assert.ThrowsAsync<InvalidDataException>(
            () =>
                updater.InstallPreparedAsync(
                    next,
                    new AppSettings(),
                    CancellationToken.None));

        Assert.Equal(
            "v2.0.0",
            updater.InstalledVersion(key));

        Assert.Equal(
            "must-survive",
            File.ReadAllText(
                Path.Combine(
                    Bin,
                    key,
                    "marker.txt")));

        Assert.False(
            Directory.Exists(
                Path.Combine(
                    Bin,
                    key + ".previous")));
    }


    [Theory]
    [InlineData("sing-box")]
    [InlineData("xray")]
    public async Task UserApprovedCoreRollbackIsVerifiedAndReversible(
        string key)
    {
        Directory.CreateDirectory(Bin);

        var v2 =
            Release(
                key,
                "v2.0.0",
                'A');

        var v1 =
            Release(
                key,
                "v1.2.3",
                'B');

        CreateInstalled(
            v2,
            "v2");

        CreatePrepared(
            v1,
            "v123");

        using var updater =
            new ModuleUpdater(Bin);

        await updater.InstallPreparedAsync(
            v1,
            new AppSettings(),
            CancellationToken.None);

        Assert.Equal(
            "v1.2.3",
            updater.InstalledVersion(key));

        Assert.Equal(
            "v2.0.0",
            updater.PreviousVersion(key));

        updater.Rollback(key);

        Assert.Equal(
            "v2.0.0",
            updater.InstalledVersion(key));

        Assert.Equal(
            "v2",
            File.ReadAllText(
                Path.Combine(
                    Bin,
                    key,
                    "marker.txt")));

        Assert.Equal(
            "v1.2.3",
            updater.PreviousVersion(key));

        // A second rollback is the reverse swap. No third copy is created.
        updater.Rollback(key);

        Assert.Equal(
            "v1.2.3",
            updater.InstalledVersion(key));

        Assert.Equal(
            "v2.0.0",
            updater.PreviousVersion(key));

        Assert.False(
            Directory.Exists(
                Path.Combine(
                    Bin,
                    key + ".previous.previous")));
    }


    [Theory]
    [InlineData("sing-box")]
    [InlineData("xray")]
    public async Task TamperedPreviousCoreCannotRollback(
        string key)
    {
        Directory.CreateDirectory(Bin);

        var v2 =
            Release(
                key,
                "v2.0.0",
                'A');

        var v1 =
            Release(
                key,
                "v1.2.3",
                'B');

        CreateInstalled(
            v2,
            "v2");

        CreatePrepared(
            v1,
            "v1");

        using var updater =
            new ModuleUpdater(Bin);

        await updater.InstallPreparedAsync(
            v1,
            new AppSettings(),
            CancellationToken.None);

        // previous is v2 here
        var previous =
            Path.Combine(
                Bin,
                key + ".previous");

        var executable =
            key=="sing-box"
                ? "sing-box.exe"
                : "xray.exe";

        File.AppendAllText(
            Path.Combine(
                previous,
                executable),
            "tampered");

        Assert.Throws<InvalidDataException>(
            () =>
                updater.Rollback(key));

        // The active version must remain untouched.
        Assert.Equal(
            "v1.2.3",
            updater.InstalledVersion(key));

        Assert.Equal(
            "v1",
            File.ReadAllText(
                Path.Combine(
                    Bin,
                    key,
                    "marker.txt")));
    }
}