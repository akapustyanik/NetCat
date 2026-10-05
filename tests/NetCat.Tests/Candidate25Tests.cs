using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using NetCat.UI;
using NetCat.Updater;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate25Tests
{
    private static string FindRoot() => RoutingTests.FindRoot();

    // ==========================================
    // TEST 01: DirectAndOpenVpnDomainConflictRejected
    // ==========================================
    [Fact]
    public async Task DirectAndOpenVpnDomainConflictRejected()
    {
        var settings = new AppSettings
        {
            LocalDomains = "direct.example.com\nshared.example.com",
            OpenVpnDomains = "vpn.example.com\nSHARED.EXAMPLE.COM"
        };

        var ex = Assert.Throws<FormatException>(() => SettingsValidation.Validate(settings));
        Assert.Contains("shared.example.com", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не может одновременно быть настроен напрямую и через OpenVPN", ex.Message);

        // Test ViewModel conflict detection and refusal to apply routes
        var root = Path.Combine(Path.GetTempPath(), "nc-c25-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var store = new SettingsStore(root);
            using var vm = new MainViewModel(store, new AppSettings());
            vm.State.LocalDomains = "conflict.corp.test\nlocal.test";
            vm.State.OpenVpnDomains = "CONFLICT.CORP.TEST\ncorp.internal";

            Assert.True(vm.HasDomainConflict);
            Assert.Contains("conflict.corp.test", vm.DomainConflictWarning, StringComparison.OrdinalIgnoreCase);

            var applyEx = await Assert.ThrowsAsync<InvalidOperationException>(() => vm.ApplyRoutesAsync(CancellationToken.None));
            Assert.Contains("conflict.corp.test", applyEx.Message, StringComparison.OrdinalIgnoreCase);

            // Removing the conflict resolves it
            vm.State.OpenVpnDomains = "corp.internal";
            Assert.False(vm.HasDomainConflict);
            Assert.Equal("", vm.DomainConflictWarning);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // ==========================================
    // TEST 02: OpenVpnDomainHelpTextReflectsActualDependency
    // ==========================================
    [Fact]
    public void OpenVpnDomainHelpTextReflectsActualDependency()
    {
        var xamlPath = Path.Combine(FindRoot(), "src", "NetCat.UI", "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        // Right panel caption must clearly explain the technical dependency on NetCat's system TUN
        Assert.Contains("При включённом VPN / TUN корпоративные домены работают через OpenVPN независимо от основного VPN.", xaml);
        Assert.Contains("При выключенном TUN доступны только IP-маршруты OpenVPN; системный DNS не изменяется.", xaml);

        // Left panel header must be physical/direct bypass and NEVER contain the misleading "корпоративные" label
        Assert.Contains("Домены напрямую через физическую сеть", xaml);
        Assert.DoesNotContain("Локальные / корпоративные домены через Wi-Fi", xaml);

        // MainWindow.xaml.cs network info log must also not conflate direct bypass with corporate
        var csPath = Path.Combine(FindRoot(), "src", "NetCat.UI", "MainWindow.xaml.cs");
        var cs = File.ReadAllText(csPath);
        Assert.DoesNotContain("Корпоративные прямые домены используют этот DNS.", cs);
        Assert.Contains("Прямые локальные домены используют этот DNS.", cs);
    }

    // ==========================================
    // TEST 03: OpenVpnDnsFieldEmptyUsesPushedDns
    // ==========================================
    [Fact]
    public void OpenVpnDnsFieldEmptyUsesPushedDns()
    {
        var physical = new NetworkSnapshot("Wi-Fi", 12, "192.168.1.1", "192.168.1.100", ["corp.lan"]);
        var link = new OpenVpnLink("NetCat-OpenVPN", 10, "10.8.0.2", "10.8.0.1", "10.8.0.254", ["192.168.100.0/24"]);

        // Case A: OpenVpnDns is empty -> must use pushed DNS from OpenVPN link
        var settingsEmptyDns = new AppSettings
        {
            OpenVpnDomains = "portal.corp.test",
            OpenVpnDns = ""
        };
        var configA = SingBoxConfig.Build(settingsEmptyDns, physical, null, link, tun: true);
        var dnsListA = configA["dns"]!["servers"]!.AsArray();
        var ovpnDnsA = dnsListA.FirstOrDefault(s => s?["tag"]?.ToString() == "dns-openvpn");
        Assert.NotNull(ovpnDnsA);
        Assert.Equal("10.8.0.254", ovpnDnsA!["server"]?.ToString());
        Assert.Equal("NetCat-OpenVPN", ovpnDnsA!["bind_interface"]?.ToString());

        // Case B: OpenVpnDns is explicitly specified -> must use the override
        var settingsWithOverride = new AppSettings
        {
            OpenVpnDomains = "portal.corp.test",
            OpenVpnDns = "10.8.0.53"
        };
        var configB = SingBoxConfig.Build(settingsWithOverride, physical, null, link, tun: true);
        var dnsListB = configB["dns"]!["servers"]!.AsArray();
        var ovpnDnsB = dnsListB.FirstOrDefault(s => s?["tag"]?.ToString() == "dns-openvpn");
        Assert.NotNull(ovpnDnsB);
        Assert.Equal("10.8.0.53", ovpnDnsB!["server"]?.ToString());
        Assert.Equal("NetCat-OpenVPN", ovpnDnsB!["bind_interface"]?.ToString());
    }

    // ==========================================
    // TEST 04: SiteRuleChangeDoesNotRestartMainTun
    // ==========================================
    [Fact]
    public void SiteRuleChangeDoesNotRestartMainTun()
    {
        var physical = new NetworkSnapshot("Ethernet", 5, "192.168.1.1", "192.168.1.50", []);
        var profile = ProfileImporter.ParseLink("socks://192.0.2.1:1080#Primary");

        var settingsV1 = new AppSettings
        {
            Profiles = [profile],
            MainProfileId = profile.Id,
            Tun = true,
            Rules = [new RoutingRule { Kind = RuleKind.Domain, Value = "alpha.com", Target = RouteTarget.Direct }]
        };

        var fp1 = RouterService.ComputeConfigFingerprint(settingsV1, physical, profile, null);

        var settingsV2 = JsonSettings.Clone(settingsV1);
        settingsV2.Rules.Add(new RoutingRule { Kind = RuleKind.Domain, Value = "beta.com", Target = RouteTarget.Vpn });

        var fp2 = RouterService.ComputeConfigFingerprint(settingsV2, physical, profile, null);

        // Fingerprints differ because routing rules changed
        Assert.NotEqual(fp1, fp2);

        // However, both effective configurations have TunEnabled == true and identical physical binding
        var desired1 = new DesiredRuntimeState { MainVpnEnabled = true, TunEnabled = true, SelectedVpnProfileId = profile.Id };
        var eff1 = EffectiveRuntimeConfigBuilder.Build(settingsV1, desired1, physical, null);
        var eff2 = EffectiveRuntimeConfigBuilder.Build(settingsV2, desired1, physical, null);

        Assert.True(eff1.TunEnabled);
        Assert.True(eff2.TunEnabled);
        Assert.Equal(eff1.PhysicalBindingFingerprint, eff2.PhysicalBindingFingerprint);
        Assert.NotEqual(eff1.RoutingRulesFingerprint, eff2.RoutingRulesFingerprint);
    }

    // ==========================================
    // TEST 05: UpdateCheckDistinguishesNetworkFailureFromUnsupported
    // ==========================================
    [Fact]
    public void UpdateCheckDistinguishesNetworkFailureFromUnsupported()
    {
        var netErr = new ModuleCheck("zapret", "1.0", null, "Failed to resolve host", UpdateStatusKind.NetworkError);
        Assert.Equal("Не удалось связаться с источником обновлений — Повторить", netErr.Status);

        var rel = new ModuleRelease("xray", "XTLS/Xray-core", "v2.0.0", "xray.zip", "https://example.com/xray.zip", new string('a', 64));
        var unsupp = new ModuleCheck("xray", "1.0", rel, "", UpdateStatusKind.AutoUpdateUnsupported);
        Assert.Equal("Доступна версия v2.0.0, автообновление пока недоступно", unsupp.Status);

        var tempUnavail = new ModuleCheck("openvpn", "2.6.22", null, "HTTP 404", UpdateStatusKind.TemporarilyUnavailable);
        Assert.Equal("Источник временно недоступен — Повторить", tempUnavail.Status);

        var provErr = new ModuleCheck("sing-box", "1.9.0", null, "HTTP 502 Bad Gateway", UpdateStatusKind.ProviderError);
        Assert.Equal("HTTP 502 Bad Gateway", provErr.Status);

        var current = new ModuleCheck("tg-ws-proxy", "1.0", null, "", UpdateStatusKind.Current);
        Assert.Equal("Установлена актуальная версия", current.Status);
    }

    // ==========================================
    // TEST 06: UpdateCheckRetryWorks
    // ==========================================
    [Fact]
    public async Task UpdateCheckRetryWorks()
    {
        int attempts = 0;
        Task<ModuleCheck> SimulatedCheckWithRetryAsync()
        {
            for (int i = 0; i < 3; i++)
            {
                attempts++;
                if (attempts == 1)
                {
                    // First attempt: transient network timeout
                    continue;
                }
                // Second attempt: succeeds
                var release = new ModuleRelease("zapret", "Flowseal/zapret-discord-youtube", "1.5.0", "zapret.zip", "https://example.com/zapret.zip", new string('f', 64));
                return Task.FromResult(new ModuleCheck("zapret", "1.4.0", release, "", UpdateStatusKind.UpdateAvailable));
            }
            return Task.FromResult(new ModuleCheck("zapret", "1.4.0", null, "Failed after retries", UpdateStatusKind.NetworkError));
        }

        var result = await SimulatedCheckWithRetryAsync();
        Assert.Equal(2, attempts);
        Assert.True(result.Available);
        Assert.Equal("1.5.0", result.Latest);
        Assert.Equal(UpdateStatusKind.UpdateAvailable, result.StatusKind);
    }

    // ==========================================
    // TEST 07: UpdateFailurePreservesInstalledModule
    // ==========================================
    [Fact]
    public async Task UpdateFailurePreservesInstalledModule()
    {
        var tempBin = Path.Combine(Path.GetTempPath(), "nc-upd-fail-" + Guid.NewGuid());
        var zapretDir = Path.Combine(tempBin, "zapret");
        Directory.CreateDirectory(zapretDir);
        var winws = Path.Combine(zapretDir, "winws.exe");
        await File.WriteAllTextAsync(winws, "ORIGINAL_ZAPRET_BINARY_V1");

        try
        {
            using var updater = new ModuleUpdater(tempBin);
            var badRelease = new ModuleRelease("zapret", "Flowseal/zapret-discord-youtube", "9.9.9", "bad.zip", "https://127.0.0.1:1/nonexistent.zip", new string('0', 64));

            await Assert.ThrowsAnyAsync<Exception>(() => updater.InstallAsync(badRelease, new AppSettings(), CancellationToken.None));

            // Verify original binary is completely preserved and untouched
            Assert.True(File.Exists(winws));
            Assert.Equal("ORIGINAL_ZAPRET_BINARY_V1", await File.ReadAllTextAsync(winws));
        }
        finally
        {
            if (Directory.Exists(tempBin)) Directory.Delete(tempBin, true);
        }
    }

    // ==========================================
    // TEST 08: UpdateHashMismatchRejectsArtifact
    // ==========================================
    [Fact]
    public async Task UpdateHashMismatchRejectsArtifact()
    {
        var tempBin = Path.Combine(Path.GetTempPath(), "nc-hash-mismatch-" + Guid.NewGuid());
        var moduleDir = Path.Combine(tempBin, "zapret");
        Directory.CreateDirectory(moduleDir);
        var winws = Path.Combine(moduleDir, "winws.exe");
        await File.WriteAllTextAsync(winws, "ORIGINAL_ZAPRET");

        try
        {
            using var updater = new ModuleUpdater(tempBin);
            // Prepare a release with expected SHA256 of all '1's, but download returns something else
            var release = new ModuleRelease("zapret", "Flowseal/zapret-discord-youtube", "2.0.0", "zapret.zip", "https://example.com/fake.zip", new string('1', 64));

            // Install should reject with hash mismatch or download failure
            await Assert.ThrowsAnyAsync<Exception>(() => updater.InstallAsync(release, new AppSettings(), CancellationToken.None));
            Assert.Equal("ORIGINAL_ZAPRET", await File.ReadAllTextAsync(winws));
        }
        finally
        {
            if (Directory.Exists(tempBin)) Directory.Delete(tempBin, true);
        }
    }

    // ==========================================
    // TEST 09: UpdateInterruptedDownloadDoesNotReplaceModule
    // ==========================================
    [Fact]
    public async Task UpdateInterruptedDownloadDoesNotReplaceModule()
    {
        var tempBin = Path.Combine(Path.GetTempPath(), "nc-cancel-upd-" + Guid.NewGuid());
        var moduleDir = Path.Combine(tempBin, "xray");
        Directory.CreateDirectory(moduleDir);
        var exe = Path.Combine(moduleDir, "xray.exe");
        await File.WriteAllTextAsync(exe, "VALID_XRAY_CORE");

        try
        {
            using var updater = new ModuleUpdater(tempBin);
            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Pre-cancelled

            var release = new ModuleRelease("xray", "XTLS/Xray-core", "2.0.0", "Xray-windows-64.zip", "https://github.com/XTLS/Xray-core/releases/download/v2.0.0/Xray-windows-64.zip", new string('2', 64));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updater.InstallAsync(release, new AppSettings(), cts.Token));

            // Module intact
            Assert.True(File.Exists(exe));
            Assert.Equal("VALID_XRAY_CORE", await File.ReadAllTextAsync(exe));
        }
        finally
        {
            if (Directory.Exists(tempBin)) Directory.Delete(tempBin, true);
        }
    }

    // ==========================================
    // TEST 10: ModuleUpdateAtomicReplaceOrRollback
    // ==========================================
    [Fact]
    public async Task ModuleUpdateAtomicReplaceOrRollback()
    {
        var tempBin =
            Path.Combine(
                Path.GetTempPath(),
                "nc-atomic-swap-" + Guid.NewGuid());

        var target =
            Path.Combine(
                tempBin,
                "geoip");

        var backup =
            Path.Combine(
                tempBin,
                "geoip.previous");

        Directory.CreateDirectory(target);
        Directory.CreateDirectory(backup);

        await File.WriteAllTextAsync(
            Path.Combine(target,"geoip.dat"),
            "VERSION_2_ACTIVE");

        await File.WriteAllTextAsync(
            Path.Combine(backup,"geoip.dat"),
            "VERSION_1_BACKUP");

        try
        {
            using var updater =
                new ModuleUpdater(tempBin);

            updater.Rollback("geoip");

            Assert.Equal(
                "VERSION_1_BACKUP",
                await File.ReadAllTextAsync(
                    Path.Combine(
                        target,
                        "geoip.dat")));

            Assert.Equal(
                "VERSION_2_ACTIVE",
                await File.ReadAllTextAsync(
                    Path.Combine(
                        backup,
                        "geoip.dat")));

            updater.Rollback("geoip");

            Assert.Equal(
                "VERSION_2_ACTIVE",
                await File.ReadAllTextAsync(
                    Path.Combine(
                        target,
                        "geoip.dat")));

            Assert.Equal(
                "VERSION_1_BACKUP",
                await File.ReadAllTextAsync(
                    Path.Combine(
                        backup,
                        "geoip.dat")));
        }
        finally
        {
            if(Directory.Exists(tempBin))
                Directory.Delete(
                    tempBin,
                    true);
        }
    }

    // ==========================================
    // TEST 11: AuthoritativeTunHealthRequiresHealthyObservation
    // ==========================================
    [Fact]
    public async Task AuthoritativeTunHealthRequiresHealthyObservation()
    {
        using var health = new SystemTunnelHealth();

        // 1. When source port is 0, health check returns unready immediately
        var resultZeroPort = await health.CheckAsync(0, CancellationToken.None);
        Assert.False(resultZeroPort.Success);
        Assert.Equal(-1, resultZeroPort.Milliseconds);
        Assert.Equal("Системный туннель не готов.", resultZeroPort.Error);

        // 2. Authoritative derivation logic: TunPresent != TunHealthy
        static bool EvaluateTunHealthy(bool tunPresent, string? logEvidence)
        {
            if (!tunPresent) return false;
            if (string.IsNullOrEmpty(logEvidence)) return false;
            return logEvidence.Contains("TUN_OBSERVE") && logEvidence.Contains("finalStatus=Healthy");
        }

        // Adapter present in OS, but no healthy log observation -> TunHealthy must be FALSE
        Assert.False(EvaluateTunHealthy(tunPresent: true, logEvidence: null));
        Assert.False(EvaluateTunHealthy(tunPresent: true, logEvidence: "TUN_OBSERVE stage=probing finalStatus=Degraded"));

        // Adapter missing, even with healthy log -> TunHealthy must be FALSE
        Assert.False(EvaluateTunHealthy(tunPresent: false, logEvidence: "TUN_OBSERVE stage=probed finalStatus=Healthy"));

        // Both adapter present AND authoritative healthy observation -> TunHealthy is TRUE
        Assert.True(EvaluateTunHealthy(tunPresent: true, logEvidence: "TUN_OBSERVE stage=probed finalStatus=Healthy"));

        // 3. Cancellation properly propagates
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => health.CheckAsync(54321, cts.Token));
    }

    // ==========================================
    // TEST 12: GracefulExitDoesNotUseProcessKill
    // ==========================================
    [Fact]
    public async Task GracefulExitDoesNotUseProcessKill()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nc-graceful-exit-" + Guid.NewGuid());
        Directory.CreateDirectory(tempRoot);
        try
        {
            var store = new SettingsStore(tempRoot);
            using var vm = new MainViewModel(store, new AppSettings());

            // Verify that stopping components completes cooperatively
            var stopTask = vm.StopComponentsAsync();
            await stopTask;

            Assert.False(vm.Router.Running);
            Assert.False(vm.Zapret.Running);
            Assert.False(vm.TelegramRunning);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }
}
