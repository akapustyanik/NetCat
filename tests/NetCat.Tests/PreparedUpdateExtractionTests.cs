using System.IO.Compression;
using System.Security.Cryptography;
using NetCat.Updater;
using Xunit;

namespace NetCat.Tests;

public sealed class PreparedUpdateExtractionTests : IDisposable
{
    private readonly string stage = RoutingTests.TestArtifacts("prepared-update-" + Guid.NewGuid().ToString("N"));

    private string Archive()
    {
        Directory.CreateDirectory(stage);
        using (var zip = ZipFile.Open(Path.Combine(stage, "package.zip"), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("NetCat.exe").Open());
            writer.Write("Synthetic signed-payload fixture");
        }
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(stage, "package.zip"))));
    }

    [Fact]
    public async Task HelperExtractsFreshArchiveWhenPreparationAlreadyCreatedPayload()
    {
        var hash = Archive();
        await PortableUpdate.ExtractVerifiedAsync(stage, hash, CancellationToken.None);
        var prepared = Path.Combine(stage, "payload", "NetCat.exe");
        File.WriteAllText(prepared, "Changed after preparation");
        var applied = await PortableUpdate.ExtractForApplyAsync(stage, hash, CancellationToken.None);
        Assert.NotEqual(Path.Combine(stage, "payload"), applied);
        Assert.Equal("Synthetic signed-payload fixture", File.ReadAllText(Path.Combine(applied, "NetCat.exe")));
        Assert.Equal("Changed after preparation", File.ReadAllText(prepared));
        var second = await PortableUpdate.ExtractForApplyAsync(stage, hash, CancellationToken.None);
        Assert.NotEqual(applied, second);
    }

    [Fact]
    public async Task HelperRejectsChangedArchiveEvenWhenPreparedFilesExist()
    {
        var hash = Archive();
        await PortableUpdate.ExtractVerifiedAsync(stage, hash, CancellationToken.None);
        File.AppendAllText(Path.Combine(stage, "package.zip"), "Changed after preparation");
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableUpdate.ExtractForApplyAsync(stage, hash, CancellationToken.None));
        Assert.Single(Directory.GetDirectories(stage));
    }

    public void Dispose() { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
}
