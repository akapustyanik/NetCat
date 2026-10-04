using NetCat.Updater;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32UpdaterPolicyTests
{
    [Fact]
    public void OfficialZapretReleaseUsesDirectUpstreamPolicy()
    {
        var release=new ModuleRelease(
            "zapret",
            "Flowseal/zapret-discord-youtube",
            "1.10.3",
            "zapret-1.10.3.zip",
            "https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.3/zapret-1.10.3.zip",
            new string('A',64));

        Assert.True(
            ModuleUpdater.IsTrustedDirectUpstreamRelease(release));

        Assert.True(
            new ModuleCheck(
                "zapret",
                "1.10.2",
                release)
            .AutoUpdateSupported);
    }

    [Fact]
    public void ForeignZapretRepositoryIsRejected()
    {
        var release=new ModuleRelease(
            "zapret",
            "evil/zapret",
            "9.0.0",
            "runtime.zip",
            "https://github.com/evil/zapret/releases/download/9.0.0/runtime.zip",
            new string('A',64));

        Assert.False(
            ModuleUpdater.IsTrustedDirectUpstreamRelease(release));

        Assert.False(
            new ModuleCheck(
                "zapret",
                "1.0.0",
                release)
            .AutoUpdateSupported);
    }

    [Fact]
    public void TelegramRequiresOfficialRepositoryAndGitTree()
    {
        var good=new ModuleRelease(
            "tg-ws-proxy",
            "Flowseal/tg-ws-proxy",
            "1.2.3",
            "headless source",
            "https://github.com/Flowseal/tg-ws-proxy/tree/1.2.3",
            "",
            new string('a',40));

        Assert.True(
            ModuleUpdater.IsTrustedDirectUpstreamRelease(good));

        Assert.True(
            new ModuleCheck(
                "tg-ws-proxy",
                "1.2.2",
                good)
            .AutoUpdateSupported);

        var noTree=good with { SourceTree="" };

        Assert.False(
            ModuleUpdater.IsTrustedDirectUpstreamRelease(noTree));
    }

    [Theory]
    [InlineData("geoip","geoip.dat")]
    [InlineData("geosite","geosite.dat")]
    public void GeodataKeepsDirectUpstreamUpdates(
        string key,
        string asset)
    {
        var release=new ModuleRelease(
            key,
            "Loyalsoldier/v2ray-rules-dat",
            "20260929",
            asset,
            $"https://github.com/Loyalsoldier/v2ray-rules-dat/releases/download/20260929/{asset}",
            new string('B',64));

        Assert.True(
            new ModuleCheck(
                key,
                "20260928",
                release)
            .AutoUpdateSupported);
    }

    [Fact]
    public void WintunUsesOfficialDistributionWithoutNetCatApproval()
    {
        var release=new ModuleRelease(
            "wintun",
            "WireGuard/wintun",
            "0.14.1",
            "wintun-0.14.1.zip",
            "https://www.wintun.net/builds/wintun-0.14.1.zip",
            "");

        Assert.True(
            ModuleUpdater.IsTrustedDirectUpstreamRelease(release));

        Assert.True(
            new ModuleCheck(
                "wintun",
                "0.14.0",
                release)
            .AutoUpdateSupported);
    }

    [Theory]
    [InlineData("sing-box","SagerNet/sing-box")]
    [InlineData("xray","XTLS/Xray-core")]
    public void VpnCoresRemainReviewedByDefault(
        string key,
        string repository)
    {
        var release=new ModuleRelease(
            key,
            repository,
            "9.0.0",
            "runtime.zip",
            $"https://github.com/{repository}/releases/download/9.0.0/runtime.zip",
            new string('C',64));

        Assert.False(
            new ModuleCheck(
                key,
                "1.0.0",
                release)
            .AutoUpdateSupported);
    }

    [Fact]
    public void OpenVpnRemainsExcluded()
    {
        var release=new ModuleRelease(
            "openvpn",
            "OpenVPN/openvpn",
            "2.6.99",
            "OpenVPN-2.6.99-I001-amd64.msi",
            "https://swupdate.openvpn.org/community/releases/OpenVPN-2.6.99-I001-amd64.msi",
            "");

        Assert.False(
            new ModuleCheck(
                "openvpn",
                "2.6.22",
                release)
            .AutoUpdateSupported);
    }
}