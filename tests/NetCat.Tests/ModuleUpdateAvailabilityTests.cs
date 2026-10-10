using System.Windows;
using System.Windows.Controls;
using NetCat.Core;
using NetCat.UI;
using NetCat.Updater;
using Xunit;

namespace NetCat.Tests;

public sealed class ModuleUpdateAvailabilityTests
{
    internal static ModuleCheck Supported(string key = "zapret")
    {
        var release = key == "netcat"
            ? new ModuleRelease(key, "akapustyanik/NetCat", "1.0.4", "NetCat-v1.0.4-win-x64.zip",
                "https://github.com/akapustyanik/NetCat/releases/download/v1.0.4/NetCat-v1.0.4-win-x64.zip", new string('A', 64))
            : new ModuleRelease(key, "Flowseal/zapret-discord-youtube", "1.10.3", "zapret-1.10.3.zip",
                "https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.3/zapret-1.10.3.zip", new string('A', 64));
        return new(key, "1.0.2", release, StatusKind: UpdateStatusKind.UpdateAvailable, VersionStatus: VersionStatus.UpdateAvailable);
    }

    internal static ModuleCheck Unapproved() => new("sing-box", "1.14.2",
        new("sing-box", "SagerNet/sing-box", "1.14.3", "sing-box-1.14.3-windows-amd64.zip",
            "https://github.com/SagerNet/sing-box/releases/download/v1.14.3/sing-box-1.14.3-windows-amd64.zip", new string('B', 64)),
        StatusKind: UpdateStatusKind.AutoUpdateUnsupported, VersionStatus: VersionStatus.UpdateAvailable,
        Installability: InstallabilityStatus.PublisherManifestRequired);

    [Fact]
    public void UpstreamDiscoveryDoesNotAuthorizeCoreUpdate()
    {
        var check = Unapproved();
        Assert.True(check.Available);
        Assert.False(check.InstallableUpdate);
        Assert.Contains("ещё не одобрено", check.Status);
        var row = new ModuleRow(check, false) { Selected = true };
        Assert.False(row.CanSelectUpdate);
        Assert.False(row.Selected);
    }

    [Theory]
    [InlineData(InstallabilityStatus.Unsupported)]
    [InlineData(InstallabilityStatus.BlockedByPolicy)]
    [InlineData(InstallabilityStatus.AwaitingTrustedPackage)]
    [InlineData(InstallabilityStatus.PublisherManifestRequired)]
    public void ClassificationBlocksOtherwiseValidUpstream(InstallabilityStatus status) =>
        Assert.False((Supported() with { Installability = status }).InstallableUpdate);

    [Theory]
    [InlineData(UpdateStatusKind.Checking)]
    [InlineData(UpdateStatusKind.NetworkError)]
    [InlineData(UpdateStatusKind.ProviderError)]
    [InlineData(UpdateStatusKind.IntegrityError)]
    [InlineData(UpdateStatusKind.TemporarilyUnavailable)]
    [InlineData(UpdateStatusKind.AutoUpdateUnsupported)]
    public void CachedReleaseAfterCheckErrorIsNotOffered(UpdateStatusKind status) =>
        Assert.False((Supported() with { StatusKind = status }).InstallableUpdate);

    [Theory]
    [InlineData("", "zapret-1.10.3.zip")]
    [InlineData("https://example.invalid/foreign.zip", "zapret-1.10.3.zip")]
    [InlineData("https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.3/runtime.exe", "runtime.exe")]
    [InlineData("https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.3/file", "")]
    public void MissingOrUnsupportedPackageIsNotInstallable(string url, string asset)
    {
        var check = Supported();
        Assert.False((check with { Release = check.Release! with { Url = url, Asset = asset } }).InstallableUpdate);
    }

    [Fact]
    public void CurrentPinnedAndErrorChecksCannotBeSelected()
    {
        var check = Supported();
        Assert.False((check with { Installed = check.Release!.Version }).InstallableUpdate);
        Assert.False(new ModuleRow(check, true).CanSelectUpdate);
        Assert.False((check with { Error = "download failed" }).InstallableUpdate);
        Assert.False((check with { VersionStatus = VersionStatus.CheckFailed }).InstallableUpdate);
        Assert.False((check with { Release = check.Release! with { Key = "xray" } }).InstallableUpdate);
        Assert.True(check.InstallableUpdate);
        Assert.True(Supported("netcat").InstallableUpdate);
    }
}

public sealed partial class Candidate32UiAuditTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public Task BannerCountsOnlySelectableUpdates(int supported, bool unapproved) => Sta.Run(async () =>
    {
        using var vm = new MainViewModel(new SettingsStore(root), new AppSettings());
        var window = new MainWindow(vm);
        try
        {
            var checks = new List<ModuleCheck>();
            if (supported > 0) checks.Add(ModuleUpdateAvailabilityTests.Supported());
            if (supported > 1) checks.Add(ModuleUpdateAvailabilityTests.Supported("netcat"));
            if (unapproved)
            {
                checks.Add(ModuleUpdateAvailabilityTests.Unapproved());
                checks.Add(new("openvpn", "2.6.22", new("openvpn", "OpenVPN/openvpn", "2.6.23", "installer.msi", "https://example.invalid/installer.msi", ""),
                    StatusKind: UpdateStatusKind.AutoUpdateUnsupported, Installability: InstallabilityStatus.Unsupported));
            }
            vm.ApplyModuleChecks(checks);
            window.Show(); window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var banner = (Border)window.FindName("UpdateNotificationBanner");
            Assert.Equal(supported, vm.AvailableUpdateCount);
            Assert.Equal(supported > 0, vm.HasUpdates);
            Assert.Equal(supported > 0 ? Visibility.Visible : Visibility.Collapsed, banner.Visibility);
            Assert.Equal(supported, vm.Modules.Count(m => m.CanSelectUpdate));
            Assert.Equal(supported, vm.Modules.Count(m => m.Selected));
            if (unapproved) Assert.Contains("ещё не одобрено", vm.Modules.Single(m => m.Key == "sing-box").Status);
            if (supported == 0) Assert.DoesNotContain("Доступны обновления:", vm.UpdateStatus);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task RecheckInstallationAndErrorRefreshBannerWithoutRestart() => Sta.Run(async () =>
    {
        using var vm = new MainViewModel(new SettingsStore(root), new AppSettings());
        var window = new MainWindow(vm);
        try
        {
            window.Show();
            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
            vm.ApplyModuleChecks([ModuleUpdateAvailabilityTests.Unapproved()]);
            Assert.False(vm.HasUpdates);
            var available = ModuleUpdateAvailabilityTests.Supported();
            vm.ApplyModuleChecks([available, ModuleUpdateAvailabilityTests.Unapproved()]);
            Assert.True(vm.HasUpdates);
            Assert.StartsWith("Доступны обновления: 1.", vm.UpdateNotification);
            vm.UpdateStatus = "Не удалось скачать: Zapret";
            Assert.Equal(1, vm.AvailableUpdateCount); // Download failure is retryable; discovery remains valid.
            vm.ApplyModuleChecks([available with { Installed = available.Release!.Version }, ModuleUpdateAvailabilityTests.Unapproved()]);
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.False(vm.HasUpdates);
            Assert.Equal(Visibility.Collapsed, ((Border)window.FindName("UpdateNotificationBanner")).Visibility);
            vm.ApplyModuleChecks([available]);
            vm.ApplyModuleChecks([available with { Error = "unavailable", VersionStatus = VersionStatus.CheckFailed, Installability = InstallabilityStatus.Unsupported }]);
            Assert.False(vm.HasUpdates);
            Assert.Contains("частично", vm.UpdateStatus);
            Assert.Contains(nameof(MainViewModel.HasUpdates), changed);
            Assert.Contains(nameof(MainViewModel.AvailableUpdateCount), changed);
            Assert.Contains(nameof(MainViewModel.UpdateNotification), changed);
        }
        finally { window.Close(); }
    });
}
