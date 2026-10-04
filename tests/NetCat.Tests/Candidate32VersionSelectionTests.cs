using System.Security.Cryptography;
using System.Text.Json;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Updater;
using Xunit;

namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed class Candidate32VersionSelectionTests
    : IDisposable
{
    private readonly string appRoot=
        Path.Combine(
            Path.GetTempPath(),
            "NetCat-C32-VersionSelect-" +
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
            Directory.Delete(appRoot,true);
    }

    private static ModuleRelease Release(
        string version,
        char hash) =>
        new(
            "zapret",
            "Flowseal/zapret-discord-youtube",
            version,
            $"zapret-{version}.zip",
            $"https://github.com/Flowseal/zapret-discord-youtube/releases/download/{version}/zapret-{version}.zip",
            new string(hash,64));

    private static string Hash(string path)
    {
        using var stream=
            File.OpenRead(path);

        return Convert.ToHexString(
            SHA256.HashData(stream));
    }

    private void CreateTrustedInstalled(
        ModuleRelease release,
        string marker)
    {
        var folder=
            Path.Combine(
                Bin,
                "zapret");

        Directory.CreateDirectory(
            Path.Combine(
                folder,
                "bin"));

        File.WriteAllText(
            Path.Combine(
                folder,
                "bin",
                "winws.exe"),
            "winws-" + marker);

        File.WriteAllText(
            Path.Combine(
                folder,
                "bin",
                "WinDivert.dll"),
            "dll-" + marker);

        File.WriteAllText(
            Path.Combine(
                folder,
                "bin",
                "WinDivert64.sys"),
            "sys-" + marker);

        File.WriteAllText(
            Path.Combine(
                folder,
                "general (ALT).bat"),
            "@echo off\r\nREM " + marker);

        File.WriteAllText(
            Path.Combine(
                folder,
                "marker.txt"),
            marker);

        UpstreamRuntimeTrust.Write(
            folder,
            "zapret",
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

    private void CreatePrepared(
        ModuleRelease release,
        string marker,
        bool selectedVersion)
    {
        var folder=
            Path.Combine(
                Bin,
                ".prepared",
                "zapret");

        if(Directory.Exists(folder))
            Directory.Delete(
                folder,
                true);

        Directory.CreateDirectory(
            Path.Combine(
                folder,
                "bin"));

        File.WriteAllText(
            Path.Combine(
                folder,
                "bin",
                "winws.exe"),
            "winws-" + marker);

        File.WriteAllText(
            Path.Combine(
                folder,
                "bin",
                "WinDivert.dll"),
            "dll-" + marker);

        File.WriteAllText(
            Path.Combine(
                folder,
                "bin",
                "WinDivert64.sys"),
            "sys-" + marker);

        File.WriteAllText(
            Path.Combine(
                folder,
                "general (ALT).bat"),
            "@echo off\r\nREM " + marker);

        File.WriteAllText(
            Path.Combine(
                folder,
                "marker.txt"),
            marker);

        UpstreamRuntimeTrust.Write(
            folder,
            "zapret",
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

        var files=
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
    }

    [Fact]
    public async Task ExplicitSelectedVersionMayDowngradePinnedZapret()
    {
        Directory.CreateDirectory(Bin);

        var current=
            Release("2.0.0",'A');

        var selected=
            Release("1.1.3",'B');

        CreateTrustedInstalled(
            current,
            "v2");

        CreatePrepared(
            selected,
            "v113",
            selectedVersion:true);

        var settings=
            new AppSettings();

        settings.PinnedModules.Add(
            "zapret");

        using var updater=
            new ModuleUpdater(Bin);

        await updater.InstallPreparedAsync(
            selected,
            settings,
            CancellationToken.None);

        Assert.Equal(
            "1.1.3",
            updater.InstalledVersion("zapret"));

        Assert.Contains(
            "zapret",
            settings.PinnedModules);

        Assert.Equal(
            "2.0.0",
            updater.PreviousVersion("zapret"));

        Assert.True(
            updater.CanRollback("zapret"));
    }

    [Fact]
    public async Task NormalPreparedDowngradeRemainsBlocked()
    {
        Directory.CreateDirectory(Bin);

        var current=
            Release("2.0.0",'A');

        var older=
            Release("1.1.3",'B');

        CreateTrustedInstalled(
            current,
            "v2");

        CreatePrepared(
            older,
            "v113",
            selectedVersion:false);

        using var updater=
            new ModuleUpdater(Bin);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                updater.InstallPreparedAsync(
                    older,
                    new AppSettings(),
                    CancellationToken.None));

        Assert.Equal(
            "2.0.0",
            updater.InstalledVersion("zapret"));
    }

    [Fact]
    public async Task SelectedVersionCanRollbackToPreviousTrustedVersion()
    {
        Directory.CreateDirectory(Bin);

        var current=
            Release("2.0.0",'A');

        var selected=
            Release("1.1.3",'B');

        CreateTrustedInstalled(
            current,
            "v2");

        CreatePrepared(
            selected,
            "v113",
            selectedVersion:true);

        using var updater=
            new ModuleUpdater(Bin);

        await updater.InstallPreparedAsync(
            selected,
            new AppSettings(),
            CancellationToken.None);

        Assert.Equal(
            "1.1.3",
            updater.InstalledVersion("zapret"));

        updater.Rollback("zapret");

        Assert.Equal(
            "2.0.0",
            updater.InstalledVersion("zapret"));

        Assert.Equal(
            "v2",
            File.ReadAllText(
                Path.Combine(
                    Bin,
                    "zapret",
                    "marker.txt")));

        // Rollback is a swap: the version we left becomes the one previous copy.
        Assert.Equal(
            "1.1.3",
            updater.PreviousVersion("zapret"));
    }

    [Fact]
    public void TamperedPreviousZapretCannotRollback()
    {
        Directory.CreateDirectory(Bin);

        var current=
            Release("2.0.0",'A');

        var previous=
            Release("1.1.3",'B');

        CreateTrustedInstalled(
            current,
            "current");

        var previousFolder=
            Path.Combine(
                Bin,
                "zapret.previous");

        Directory.Move(
            Path.Combine(
                Bin,
                "zapret"),
            previousFolder);

        CreateTrustedInstalled(
            current,
            "current");

        // Rewrite previous release identity to the older version and create
        // a fresh exact receipt for it.
        File.WriteAllText(
            Path.Combine(
                previousFolder,
                "netcat-source.json"),
            JsonSerializer.Serialize(
                previous,
                JsonSettings.Options));

        File.Delete(
            Path.Combine(
                previousFolder,
                UpstreamRuntimeTrust.ReceiptName));

        UpstreamRuntimeTrust.Write(
            previousFolder,
            "zapret",
            previous.Repository,
            previous.Version,
            previous.Sha256);

        File.AppendAllText(
            Path.Combine(
                previousFolder,
                "bin",
                "winws.exe"),
            "tampered");

        using var updater=
            new ModuleUpdater(Bin);

        Assert.Throws<InvalidDataException>(
            () =>
                updater.Rollback("zapret"));

        Assert.Equal(
            "2.0.0",
            updater.InstalledVersion("zapret"));
    }

    [Fact]
    public void OpenVpnDoesNotExposeGenericRollback()
    {
        Directory.CreateDirectory(
            Path.Combine(
                Bin,
                "openvpn"));

        Directory.CreateDirectory(
            Path.Combine(
                Bin,
                "openvpn.previous"));

        using var updater=
            new ModuleUpdater(Bin);

        Assert.False(
            updater.CanRollback("openvpn"));

        Assert.Throws<InvalidOperationException>(
            () =>
                updater.Rollback("openvpn"));
    }
}