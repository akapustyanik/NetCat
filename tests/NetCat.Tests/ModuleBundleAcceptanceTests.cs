using NetCat.Core;
using NetCat.Engine;
using Xunit;

namespace NetCat.Tests;

public sealed class ModuleBundleAcceptanceTests
{
    [Theory]
    [InlineData("sing-box")]
    [InlineData("xray")]
    [InlineData("openvpn")]
    public void ModuleBundleMatchesCompiledReviewPins(string module)
    {
        using var lease = ModuleIntegrity.Acquire(module, Path.Combine(RoutingTests.ModuleRoot, module));
    }

    [Theory]
    [InlineData("zapret")]
    [InlineData("tg-ws-proxy")]
    [InlineData("tg-runtime")]
    public void FullRuntimeBundleMatchesCompiledReviewInventory(string module)
    {
        using var lease = ReviewedRuntimeTrust.Acquire(module, Path.Combine(RoutingTests.ModuleRoot, module));
    }

    [Fact]
    public async Task TelegramHeadlessCliSupportsProductionArguments()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await ProcessHost.RunAsync(Path.Combine(RoutingTests.ModuleRoot, "tg-runtime", "NetCat.Telegram.exe"),
            ["-I", "-B", "-m", "proxy.tg_ws_proxy", "--help"], timeout.Token);
        Assert.Equal(0, result.Code);
        foreach (var option in new[] { "--host", "--port", "--secret", "--pool-size" })
            Assert.Contains(option, result.Output);
    }

    [Fact]
    public void PreservedOpenVpnBundleRemainsByteIdentical()
    {
        var original = Path.Combine(RoutingTests.FindRoot(), "artifacts", "Protocol-Expansion-Full-Acceptance",
            "candidate-portable-current", "modules", "openvpn");
        // The preservation evidence is available in the local release-preparation pass.
        if (!Directory.Exists(original)) original = Path.Combine(RoutingTests.FindRoot(), "bin", "openvpn");
        var files = Directory.GetFiles(original, "*", SearchOption.AllDirectories);
        var current = Path.Combine(RoutingTests.ModuleRoot, "openvpn");
        Assert.Equal(files.Length, Directory.GetFiles(current, "*", SearchOption.AllDirectories).Length);
        foreach (var file in files)
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)),
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(current, Path.GetRelativePath(original, file)))));
    }
}
