using NetCat.Core;
using NetCat.Network;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32FunctionalRecoveryTests
{
    [Fact]
    public void CachedZapretPassIsNeverDisplayedAsFreshGreen()
    {
        var settings = new AppSettings { YouTube = ServiceRoute.Zapret };
        settings.ZapretResults.Add(new ZapretTestRecord("general.bat", settings.Scenario, DateTimeOffset.UtcNow,
            "HTTPS: доступен", "Через VPN", true, 1, 100, "HTTPS only"));
        var row = new StrategyResult { File = "general.bat" };
        row.RestoreHistory(settings);
        Assert.True(row.Passed); // Preserve historical data for the detail view.
        Assert.False(row.Fresh); // Only this property drives green card styling.
        Assert.Contains("Cached", row.TestDepth);
    }

    [Fact]
    public void GeneratedTelegramProxyUriAcceptsOnlyCurrentLocalEndpoint()
    {
        const string secret = "dd0123456789abcdef0123456789abcdef";
        UnelevatedExplorerShellLauncher.ValidateTelegramProxyUri(
            $"tg://proxy?server=127.0.0.1&port=1443&secret={secret}", 1443);
        Assert.Throws<InvalidOperationException>(() => UnelevatedExplorerShellLauncher.ValidateTelegramProxyUri(
            $"tg://proxy?server=127.0.0.1&port=1444&secret={secret}", 1443));
        Assert.Throws<InvalidOperationException>(() => UnelevatedExplorerShellLauncher.ValidateTelegramProxyUri(
            $"tg://resolve?domain=test&server=127.0.0.1&port=1443&secret={secret}", 1443));
        Assert.Throws<InvalidOperationException>(() => UnelevatedExplorerShellLauncher.ValidateTelegramProxyUri(
            $"tg://proxy?server=192.0.2.1&port=1443&secret={secret}", 1443));
        Assert.Throws<InvalidOperationException>(() => UnelevatedExplorerShellLauncher.ValidateTelegramProxyUri(
            $"tg://proxy?server=127.0.0.1&port=1443&secret={secret}&extra=1", 1443));
    }

    [Fact]
    public void JournalBrokerAllowsOnlyLogAndItsFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "NetCat-C32-Broker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = Path.Combine(root, "diagnostic.log");
            var other = Path.Combine(root, "other.txt");
            File.WriteAllText(log, ""); File.WriteAllText(other, "");
            Assert.Equal(log, UnelevatedExplorerShellLauncher.ValidateOwnedPath(log, root, false));
            Assert.Equal(root, UnelevatedExplorerShellLauncher.ValidateOwnedPath(root, root, true));
            Assert.Throws<InvalidOperationException>(() => UnelevatedExplorerShellLauncher.ValidateOwnedPath(other, root, false));
        }
        finally { Directory.Delete(root, true); }
    }
}
