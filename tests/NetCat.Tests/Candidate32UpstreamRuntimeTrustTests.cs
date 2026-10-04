using NetCat.Engine;
using Xunit;

namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed class Candidate32UpstreamRuntimeTrustTests
    : IDisposable
{
    private readonly string root =
        Path.Combine(
            Path.GetTempPath(),
            "NetCat-C32-UpstreamTrust-" +
            Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root,true);
    }

    private string CreateZapret()
    {
        var folder =
            Path.Combine(root,"zapret");

        Directory.CreateDirectory(
            Path.Combine(folder,"bin"));

        File.WriteAllText(
            Path.Combine(
                folder,
                "bin",
                "winws.exe"),
            "candidate32-winws");

        File.WriteAllText(
            Path.Combine(
                folder,
                "bin",
                "WinDivert.dll"),
            "candidate32-windivert");

        File.WriteAllText(
            Path.Combine(
                folder,
                "bin",
                "WinDivert64.sys"),
            "candidate32-driver");

        File.WriteAllText(
            Path.Combine(
                folder,
                "general (ALT).bat"),
            "@echo off");

        return folder;
    }

    private string CreateTelegram()
    {
        var folder =
            Path.Combine(
                root,
                "tg-ws-proxy");

        Directory.CreateDirectory(
            Path.Combine(folder,"proxy"));

        File.WriteAllText(
            Path.Combine(
                folder,
                "proxy",
                "tg_ws_proxy.py"),
            "VALUE = 1");

        File.WriteAllText(
            Path.Combine(
                folder,
                "LICENSE"),
            "test");

        return folder;
    }

    [Fact]
    public void VerifiedZapretReceiptAuthorizesExactRuntime()
    {
        var folder=CreateZapret();

        UpstreamRuntimeTrust.Write(
            folder,
            "zapret",
            "Flowseal/zapret-discord-youtube",
            "1.10.3",
            new string('A',64));

        using var lease =
            ReviewedRuntimeTrust.Acquire(
                "zapret",
                folder);
    }

    [Fact]
    public void ModifiedZapretFileIsRejected()
    {
        var folder=CreateZapret();

        UpstreamRuntimeTrust.Write(
            folder,
            "zapret",
            "Flowseal/zapret-discord-youtube",
            "1.10.3",
            new string('A',64));

        File.AppendAllText(
            Path.Combine(
                folder,
                "general (ALT).bat"),
            " changed");

        Assert.Throws<InvalidDataException>(
            () =>
                ReviewedRuntimeTrust.Acquire(
                    "zapret",
                    folder));
    }

    [Fact]
    public void ExtraZapretExecutableIsRejected()
    {
        var folder=CreateZapret();

        UpstreamRuntimeTrust.Write(
            folder,
            "zapret",
            "Flowseal/zapret-discord-youtube",
            "1.10.3",
            new string('A',64));

        File.WriteAllText(
            Path.Combine(
                folder,
                "bin",
                "unexpected.exe"),
            "unexpected");

        Assert.Throws<InvalidDataException>(
            () =>
                ReviewedRuntimeTrust.Acquire(
                    "zapret",
                    folder));
    }

    [Fact]
    public void TelegramReceiptAuthorizesExactTree()
    {
        var folder=CreateTelegram();

        UpstreamRuntimeTrust.Write(
            folder,
            "tg-ws-proxy",
            "Flowseal/tg-ws-proxy",
            "1.2.3",
            new string('b',40));

        using var lease =
            ReviewedRuntimeTrust.Acquire(
                "tg-ws-proxy",
                folder);
    }

    [Fact]
    public void ModifiedTelegramSourceIsRejected()
    {
        var folder=CreateTelegram();

        UpstreamRuntimeTrust.Write(
            folder,
            "tg-ws-proxy",
            "Flowseal/tg-ws-proxy",
            "1.2.3",
            new string('b',40));

        File.AppendAllText(
            Path.Combine(
                folder,
                "proxy",
                "tg_ws_proxy.py"),
            "\nVALUE = 2");

        Assert.Throws<InvalidDataException>(
            () =>
                ReviewedRuntimeTrust.Acquire(
                    "tg-ws-proxy",
                    folder));
    }

    [Fact]
    public void ForeignRepositoryCannotCreateReceipt()
    {
        var folder=CreateZapret();

        Assert.Throws<InvalidDataException>(
            () =>
                UpstreamRuntimeTrust.Write(
                    folder,
                    "zapret",
                    "evil/zapret",
                    "9.0.0",
                    new string('A',64)));
    }

    [Fact]
    public void WrongDigestKindCannotCreateReceipt()
    {
        var folder=CreateTelegram();

        Assert.Throws<InvalidDataException>(
            () =>
                UpstreamRuntimeTrust.Write(
                    folder,
                    "tg-ws-proxy",
                    "Flowseal/tg-ws-proxy",
                    "1.2.3",
                    new string('A',64)));
    }
}