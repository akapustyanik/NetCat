using NetCat.Core;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed partial class Candidate32TrafficAndDisplayTests
{
    [Fact]
    public async Task SuccessfulPayloadAndStunDoNotHideUnavailableWebsite()
    {
        await using var fixture = new TrafficFixture();
        var targets = fixture.Targets with { Web = [new Uri(fixture.Targets.Download, "/web-unavailable")] };
        var result = await ProfileTrafficProbe.MeasureAsync(fixture.SocksPort, CancellationToken.None, targets);
        Assert.True(result.Download.Success);
        Assert.True(result.Upload.Success);
        Assert.Equal(8, result.UdpReceived);
        Assert.False(result.Success);
        Assert.StartsWith("НЕ ПРОЙДЕН", result.Summary);
        Assert.Contains("сайты 0/1", result.Summary);
        Assert.Equal(503, Assert.Single(result.WebChecks!).StatusCode);
        Assert.Equal(3, fixture.ConnectCount); // Website check also goes through the supplied SOCKS.
    }

    [Fact]
    public async Task FreshSocksSuccessDoesNotConfirmFailedActiveConnection()
    {
        await using var fixture = new TrafficFixture();
        var isolated = await ProfileTrafficProbe.MeasureAsync(fixture.SocksPort, CancellationToken.None, fixture.Targets);
        Assert.True(isolated.Success);
        var profile = new Profile(); profile.SetTrafficTestResult(isolated);
        var active = new ActiveTrafficTest(new(profile.Id, 1, 1, 123, true), TrafficTestResult.Failed("current path unavailable"));
        Assert.False(active.Success);
        Assert.Contains("Текущий TUN: НЕ ПРОЙДЕН", active.Summary);
        Assert.Contains("не проверяет системный TUN", profile.TrafficDetails);
    }
}

public sealed partial class Candidate32UiAuditTests
{
    [Theory]
    [InlineData("host", false)]
    [InlineData("port", false)]
    [InlineData("protocol", false)]
    [InlineData("core", false)]
    [InlineData("outbound", false)]
    [InlineData("name", true)]
    public async Task TrafficHistoryRequiresSameConnectionNotJustSameOutboundJson(string change, bool preserves)
    {
        await Sta.Run(() =>
        {
            var profile = new Profile { Name = "synthetic", Host = "fixture.invalid", Core = "sing-box" };
            using var vm = new NetCat.UI.MainViewModel(new SettingsStore(root), new AppSettings { Profiles = [profile] });
            profile.SetTrafficTestResult(new(new(true, 0, 131072), new(true, 16384, 16400), 8, 8));
            var next = JsonSettings.Clone(vm.State); var edited = next.Profiles.Single();
            switch(change)
            {
                case "host": edited.Host = "changed.fixture.invalid"; break;
                case "port": edited.Port++; break;
                case "protocol": edited.Protocol = "trojan"; break;
                case "core": edited.Core = "Xray"; break;
                case "outbound": edited.OutboundJson = "{\"type\":\"trojan\"}"; break;
                default: edited.Name = "new display name"; break;
            }
            typeof(NetCat.UI.MainViewModel).GetMethod("PublishSettingsCore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, [next]);
            Assert.Equal(preserves, vm.Profiles.Single().TrafficResult.StartsWith("ПРОЙДЕН"));
            Assert.False(vm.Router.VpnRunning); Assert.Equal(0, vm.Router.StartCount);
            return Task.CompletedTask;
        });
    }
}

public sealed class Candidate32TrafficTruthTests
{
    private static readonly TrafficTestResult Passed = new(new(true, 0, 131072), new(true, 16384, 16400), 8, 8);
    private static ActiveTrafficStamp Stamp() => new(Guid.NewGuid(), 1, 1, 123, true);

    [Theory]
    [InlineData(false, true, 8)]
    [InlineData(true, false, 8)]
    [InlineData(true, true, 7)]
    public void PartialTransferCannotBePresentedAsPassed(bool download, bool upload, int udp)
    {
        var result = new TrafficTestResult(new(download, 0, 100), new(upload, 100, 100), 8, udp);
        Assert.False(result.Success); Assert.StartsWith("НЕ ПРОЙДЕН", result.Summary);
    }
    [Theory]
    [InlineData("profile")]
    [InlineData("session")]
    [InlineData("network")]
    [InlineData("process")]
    [InlineData("tun")]
    public void ChangedActiveSessionCannotReuseSuccessfulResult(string change)
    {
        var stamp = Stamp(); var tested = new ActiveTrafficTest(stamp, Passed);
        var changed = change switch
        {
            "profile" => stamp with { ProfileId = Guid.NewGuid() },
            "session" => stamp with { SessionRevision = 2 },
            "network" => stamp with { NetworkRevision = 2 },
            "process" => stamp with { ProcessId = 124 },
            _ => stamp with { TunActive = false }
        };
        Assert.True(tested.Success);
        var stale = tested.Validate(changed);
        Assert.False(stale.Success); Assert.Contains("НЕ ПОДТВЕРЖДЁН", stale.Summary);
        Assert.Contains("повторите", stale.Unavailable);
    }
    [Fact]
    public async Task StoppedRouterCannotReportActiveTrafficSuccessOrStartMeasurement()
    {
        using var router = new RouterService("unused", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var before = router.CaptureTrafficStamp(); var measured = false;
        var result = await router.TestActiveTrafficAsync(CancellationToken.None, _ => { measured = true; return Task.FromResult(Passed); });
        Assert.False(result.Success); Assert.Null(result.Result); Assert.False(measured);
        Assert.Equal(before, router.CaptureTrafficStamp()); Assert.Equal(0, router.StartCount);
    }
    [Fact]
    public async Task RunningSocksWithoutTunDoesNotClaimSystemTrafficOrReconcile()
    {
        using var fixture = new Candidate21PortTests.Fixture(); await fixture.Start();
        var before = fixture.Router.CaptureTrafficStamp(); var starts = fixture.Router.StartCount; var measured = false;
        Assert.True(fixture.Router.VpnRunning);
        var result = await fixture.Router.TestActiveTrafficAsync(CancellationToken.None, _ => { measured = true; return Task.FromResult(Passed); });
        Assert.False(measured); Assert.False(result.Success); Assert.Null(result.Result);
        Assert.Equal(starts, fixture.Router.StartCount); Assert.Equal(before, fixture.Router.CaptureTrafficStamp());
    }
    [Theory]
    [InlineData("NetCat.exe", false)]
    [InlineData("NetCat.TrafficProbe.exe", true)]
    [InlineData("xray.exe", false)]
    [InlineData(null, false)]
    public void TunWorkerRequiresIndependentProcessIdentity(string? name, bool valid) =>
        Assert.Equal(valid, SystemTrafficProbeWorker.IsValidInvocation([SystemTrafficProbeWorker.Argument], name));
    [Fact]
    public void TunWorkerCannotCombineMeasurementWithExitUpdateOrUiStartup()
    {
        foreach(var other in new[] { "--exit", "--apply-update", "--smoke", "--stop" })
            Assert.False(SystemTrafficProbeWorker.IsValidInvocation([SystemTrafficProbeWorker.Argument, other], SystemTrafficProbeWorker.ExecutableName));
    }
    [Fact]
    public async Task InProcessTunMeasurementRejectsCoreBypassIdentityBeforeNetworkAccess()
    {
        var result = await SystemTrafficProbe.MeasureAsync(CancellationToken.None);
        Assert.False(result.Success); Assert.Equal(0, result.Download.ReceivedBytes); Assert.Equal(0, result.UdpSent);
        Assert.Contains("отдельного процесса", result.Download.Error);
    }
    [Fact]
    public void UdpRouteValidationFailureCannotPassEvenAfterAllReplies()
    {
        var result = Passed with { UdpError = "TUN changed" };
        Assert.False(result.Success); Assert.StartsWith("НЕ ПРОЙДЕН", result.Summary);
    }
    [Fact]
    public void OldPointInTimeSuccessCannotClaimCurrentAvailability()
    {
        var stamp = Stamp(); var tested = new ActiveTrafficTest(stamp, Passed);
        var expired = tested.Validate(stamp, tested.TestedAt.AddMinutes(3));
        Assert.False(expired.Success); Assert.Contains("устарел", expired.Summary);
        Assert.True(tested.Validate(stamp, tested.TestedAt.AddSeconds(30)).Success);
    }
    [Fact]
    public async Task CancelledActiveCheckDoesNotStartAnything()
    {
        using var router = new RouterService("unused", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.TestActiveTrafficAsync(cancelled.Token));
        Assert.Equal(0, router.StartCount);
    }
    private static NetCat.Engine.IExecutableTrustPolicy WorkerTrust(string path, byte[] expected) =>
        (NetCat.Engine.IExecutableTrustPolicy)Activator.CreateInstance(
            typeof(SystemTrafficProbeWorker).GetNestedType("VerifiedWorker", System.Reflection.BindingFlags.NonPublic)!, path, expected)!;
    [Fact]
    public void ChangedWorkerOrDifferentExecutableCannotUseVerifiedCopyTrust()
    {
        var folder = Path.Combine(Path.GetTempPath(), "NetCat-Worker-Trust-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, SystemTrafficProbeWorker.ExecutableName);
        try
        {
            var original = new byte[] { 1, 2, 3 }; File.WriteAllBytes(file, original);
            var policy = WorkerTrust(file, System.Security.Cryptography.SHA256.HashData(original));
            Assert.Throws<InvalidDataException>(() => policy.AcquireExecutable(Path.Combine(folder, "other.exe")));
            File.WriteAllBytes(file, [4, 5, 6]);
            Assert.Throws<InvalidDataException>(() => policy.AcquireExecutable(file));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public void VerifiedWorkerLeasePreventsCheckToLaunchReplacement()
    {
        var folder = Path.Combine(Path.GetTempPath(), "NetCat-Worker-Lease-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, SystemTrafficProbeWorker.ExecutableName);
        try
        {
            var original = new byte[] { 1, 2, 3 }; File.WriteAllBytes(file, original);
            var policy = WorkerTrust(file, System.Security.Cryptography.SHA256.HashData(original));
            using(var lease = policy.AcquireExecutable(file)) Assert.Throws<IOException>(() => File.WriteAllBytes(file, [4, 5, 6]));
            File.WriteAllBytes(file, [4, 5, 6]); // Lease is released after the owned process stops.
        }
        finally { Directory.Delete(folder, true); }
    }
}
