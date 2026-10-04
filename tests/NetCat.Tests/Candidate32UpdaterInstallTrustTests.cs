using System.Security.Cryptography;
using System.Text.Json;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Updater;
using Xunit;

namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed class Candidate32UpdaterInstallTrustTests
    : IDisposable
{
    private readonly string appRoot =
        Path.Combine(
            Path.GetTempPath(),
            "NetCat-C32-InstallTrust-" +
            Guid.NewGuid().ToString("N"));

    private string Bin =>
        Path.Combine(appRoot,"modules");

    private sealed record PreparedShape(
        ModuleRelease Release,
        Dictionary<string,string> Files);

    public void Dispose()
    {
        if(Directory.Exists(appRoot))
            Directory.Delete(appRoot,true);
    }

    private static string Hash(string path)
    {
        using var stream=File.OpenRead(path);

        return Convert.ToHexString(
            SHA256.HashData(stream));
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

    private void CreateCurrent(
        ModuleRelease release,
        string marker)
    {
        var folder=
            Path.Combine(Bin,"zapret");

        Directory.CreateDirectory(folder);

        File.WriteAllText(
            Path.Combine(
                folder,
                "netcat-source.json"),
            JsonSerializer.Serialize(
                release,
                JsonSettings.Options));

        File.WriteAllText(
            Path.Combine(
                folder,
                "marker.txt"),
            marker);
    }

    private string Prepare(
        ModuleRelease release,
        string marker)
    {
        var folder=
            Path.Combine(
                Bin,
                ".prepared",
                "zapret");

        Directory.CreateDirectory(
            Path.Combine(folder,"bin"));

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
                    p =>
                        Path.GetRelativePath(
                                folder,
                                p)
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
                    files),
                JsonSettings.Options));

        return folder;
    }

    [Fact]
    public async Task VerifiedPreparedZapretInstallsAtomically()
    {
        Directory.CreateDirectory(Bin);

        var oldRelease=Release("1.0.0",'1');
        var newRelease=Release("1.1.0",'A');

        CreateCurrent(
            oldRelease,
            "old");

        Prepare(
            newRelease,
            "new");

        using var updater=
            new ModuleUpdater(Bin);

        await updater.InstallPreparedAsync(
            newRelease,
            new AppSettings(),
            CancellationToken.None);

        Assert.Equal(
            "1.1.0",
            updater.InstalledVersion("zapret"));

        Assert.Equal(
            "old",
            File.ReadAllText(
                Path.Combine(
                    Bin,
                    "zapret.previous",
                    "marker.txt")));

        Assert.True(
            File.Exists(
                Path.Combine(
                    Bin,
                    "zapret",
                    UpstreamRuntimeTrust.ReceiptName)));

        using var lease=
            ReviewedRuntimeTrust.Acquire(
                "zapret",
                Path.Combine(Bin,"zapret"));
    }

    [Fact]
    public async Task TamperedPreparedZapretDoesNotReplaceCurrent()
    {
        Directory.CreateDirectory(Bin);

        var oldRelease=Release("1.0.0",'1');
        var newRelease=Release("1.1.0",'A');

        CreateCurrent(
            oldRelease,
            "must-survive");

        var prepared=
            Prepare(
                newRelease,
                "new");

        File.AppendAllText(
            Path.Combine(
                prepared,
                "bin",
                "winws.exe"),
            "tampered");

        using var updater=
            new ModuleUpdater(Bin);

        await Assert.ThrowsAsync<InvalidDataException>(
            () =>
                updater.InstallPreparedAsync(
                    newRelease,
                    new AppSettings(),
                    CancellationToken.None));

        Assert.Equal(
            "must-survive",
            File.ReadAllText(
                Path.Combine(
                    Bin,
                    "zapret",
                    "marker.txt")));

        Assert.False(
            Directory.Exists(
                Path.Combine(
                    Bin,
                    "zapret.previous")));
    }

    [Fact]
    public async Task NewSuccessfulUpdateReplacesOldPreviousOnly()
    {
        Directory.CreateDirectory(Bin);

        var v1=Release("1.0.0",'1');
        var v2=Release("1.1.0",'A');
        var v3=Release("1.2.0",'B');

        CreateCurrent(v1,"v1");
        Prepare(v2,"v2");

        using var updater=
            new ModuleUpdater(Bin);

        await updater.InstallPreparedAsync(
            v2,
            new AppSettings(),
            CancellationToken.None);

        Prepare(v3,"v3");

        await updater.InstallPreparedAsync(
            v3,
            new AppSettings(),
            CancellationToken.None);

        Assert.Equal(
            "1.2.0",
            updater.InstalledVersion("zapret"));

        using var document=
            JsonDocument.Parse(
                File.ReadAllText(
                    Path.Combine(
                        Bin,
                        "zapret.previous",
                        "netcat-source.json")));

        Assert.Equal(
            "1.1.0",
            document.RootElement
                .GetProperty("Version")
                .GetString());

        Assert.False(
            Directory.Exists(
                Path.Combine(
                    Bin,
                    "zapret.previous.previous")));
    }

    [Theory]
    [InlineData("sing-box","SagerNet/sing-box")]
    [InlineData("xray","XTLS/Xray-core")]
    public async Task UnreviewedCoreCannotUseOrdinaryInstallApi(
        string key,
        string repository)
    {
        Directory.CreateDirectory(Bin);

        var release=
            new ModuleRelease(
                key,
                repository,
                "9.0.0",
                "runtime.zip",
                $"https://github.com/{repository}/releases/download/9.0.0/runtime.zip",
                new string('C',64));

        using var updater=
            new ModuleUpdater(Bin);

        var error=
            await Assert.ThrowsAsync<InvalidOperationException>(
                () =>
                    updater.InstallAsync(
                        release,
                        new AppSettings(),
                        CancellationToken.None));

        Assert.Contains(
            "не подтверждена",
            error.Message);
    }

    [Fact]
    public async Task ForeignZapretCannotUseInstallApi()
    {
        Directory.CreateDirectory(Bin);

        var release=
            new ModuleRelease(
                "zapret",
                "evil/zapret",
                "9.0.0",
                "runtime.zip",
                "https://github.com/evil/zapret/releases/download/9.0.0/runtime.zip",
                new string('D',64));

        using var updater=
            new ModuleUpdater(Bin);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                updater.InstallAsync(
                    release,
                    new AppSettings(),
                    CancellationToken.None));
    }
}