using System.Net.NetworkInformation;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate22Tests
{
    private const string SampleOvpnWithNcp = @"client
dev tun
proto udp
remote 198.51.100.1 1194
resolv-retry infinite
nobind
persist-key
persist-tun
remote-cert-tls server
auth SHA256
cipher AES-256-CBC
ncp-ciphers AES-256-GCM:AES-128-GCM:CHACHA20-POLY1305
verb 3
<ca>
-----BEGIN CERTIFICATE-----
TESTCA
-----END CERTIFICATE-----
</ca>";

    private const string SampleOvpnWithDataCiphers = @"client
dev tun
proto udp
remote 198.51.100.1 1194
resolv-retry infinite
nobind
persist-key
persist-tun
remote-cert-tls server
auth SHA256
data-ciphers AES-256-GCM:AES-128-GCM
verb 3
<ca>
-----BEGIN CERTIFICATE-----
TESTCA
-----END CERTIFICATE-----
</ca>";

    [Fact]
    public void LegacyNcpCiphersProfileImportsSuccessfully()
    {
        var result = ProfileImporter.Parse(SampleOvpnWithNcp, "Corporate-Legacy");
        Assert.Empty(result.Errors);
        var profile = Assert.Single(result.Profiles);
        Assert.True(profile.IsOpenVpn);
        Assert.Equal("198.51.100.1", profile.Host);
        Assert.Null(OpenVpnConfiguration.MigrationDiagnostic(profile));
    }

    [Fact]
    public void LegacyNcpCiphersIsNormalizedDeterministically()
    {
        string normalized1 = OpenVpnConfiguration.Normalize(SampleOvpnWithNcp);
        string normalized2 = OpenVpnConfiguration.Normalize(SampleOvpnWithNcp);
        Assert.Equal(normalized1, normalized2);

        Assert.Contains("data-ciphers AES-256-GCM:AES-128-GCM:CHACHA20-POLY1305", normalized1);
        Assert.DoesNotContain("ncp-ciphers", normalized1);

        string prepared = OpenVpnConfiguration.Prepare(SampleOvpnWithNcp);
        Assert.Contains("data-ciphers AES-256-GCM:AES-128-GCM:CHACHA20-POLY1305", prepared);
        Assert.DoesNotContain("ncp-ciphers", prepared);
    }

    [Fact]
    public void LegacyNcpCiphersValuesArePreserved()
    {
        const string customCiphers = "CHACHA20-POLY1305:AES-256-GCM";
        string ovpn = $"client\ndev tun\nremote 1.2.3.4 1194\nncp-ciphers {customCiphers}\n<ca>\nCA\n</ca>";
        string normalized = OpenVpnConfiguration.Normalize(ovpn);
        Assert.Contains($"data-ciphers {customCiphers}", normalized);
    }

    [Fact]
    public void ModernDataCiphersProfileRemainsUnchanged()
    {
        string normalized = OpenVpnConfiguration.Normalize(SampleOvpnWithDataCiphers);
        Assert.Contains("data-ciphers AES-256-GCM:AES-128-GCM", normalized);
        Assert.DoesNotContain("ncp-ciphers", normalized);

        // When both modern and legacy are present, modern data-ciphers takes precedence and redundant ncp-ciphers is removed
        string mixed = SampleOvpnWithDataCiphers.Replace("data-ciphers AES-256-GCM:AES-128-GCM",
            "data-ciphers AES-256-GCM:AES-128-GCM\nncp-ciphers AES-128-CBC");
        string mixedNormalized = OpenVpnConfiguration.Normalize(mixed);
        Assert.Contains("data-ciphers AES-256-GCM:AES-128-GCM", mixedNormalized);
        Assert.DoesNotContain("ncp-ciphers", mixedNormalized);
        Assert.DoesNotContain("AES-128-CBC", mixedNormalized);
    }

    [Theory]
    [InlineData("client\ndev tun\nup /usr/bin/touch\n<ca>\nCA\n</ca>")]
    [InlineData("client\ndev tun\ndown /bin/sh\n<ca>\nCA\n</ca>")]
    [InlineData("client\ndev tun\nplugin /evil.so\n<ca>\nCA\n</ca>")]
    [InlineData("client\ndev tun\nroute-up /script.bat\n<ca>\nCA\n</ca>")]
    [InlineData("client\ndev tun\nmanagement 127.0.0.1 9999\n<ca>\nCA\n</ca>")]
    public void UnsupportedDangerousDirectiveStillRejected(string maliciousOvpn)
    {
        Assert.Throws<InvalidDataException>(() => OpenVpnConfiguration.Validate(maliciousOvpn));
    }

    [Fact]
    public void NormalizationDoesNotEnableScripts()
    {
        string prepared = OpenVpnConfiguration.Prepare(SampleOvpnWithNcp);
        Assert.Contains("script-security 1", prepared);
        Assert.DoesNotContain("script-security 2", prepared);
        Assert.DoesNotContain("script-security 3", prepared);
    }

    [Fact]
    public void NormalizationDoesNotChangeSystemDnsPolicy()
    {
        string prepared = OpenVpnConfiguration.Prepare(SampleOvpnWithNcp);
        Assert.Contains("pull-filter ignore \"dns \"", prepared);
        Assert.Contains("pull-filter ignore redirect-gateway", prepared);
        Assert.Contains("pull-filter ignore block-outside-dns", prepared);
        Assert.Contains("route-nopull", prepared);
        Assert.Contains("route-noexec", prepared);
    }

    [Fact]
    public void WeakSecondaryDiagnosticDoesNotOverrideHealthyAuthoritativeTun()
    {
        var policy = new TunDiagnosticPolicy();
        // Simulate multiple 8-second probe timeouts
        for (int i = 0; i < 5; i++)
        {
            policy.Observe(new DelayResult(false, -1, "Туннель не ответил за 8 секунд."), tunActive: true, sessionRevision: 1);
        }

        Assert.Equal(5, policy.WeakFailures);
        // Headline status detail for Healthy MUST NOT report structural failure or recovery
        string detail = policy.Detail(TunStructuralStatus.Healthy);
        Assert.DoesNotContain("восстанавливаю", detail);
        Assert.Contains("Дополнительная проверка TUN временно недоступна", detail);
    }

    [Fact]
    public async Task SecondaryProbeTimeoutDoesNotRestartMainRouter()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "NetCat-C23-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var f = new Candidate12Tests.Fixture(new SettingsStore(tempDir));
            await f.Coordinator.ReconcileAsync(ReconcileReason.Startup);
            Assert.True(f.Router.IsRunning);
            Assert.Equal(1, f.Router.Starts);
            Assert.Equal(0, f.Router.Stops);
            Assert.Equal(1, f.Router.SessionRevision);

            f.Tunnel.Status = TunStructuralStatus.Healthy;

            var policy = new TunDiagnosticPolicy();
            policy.Observe(new DelayResult(false, -1, "Туннель не ответил за 8 секунд."), tunActive: true, sessionRevision: f.Router.SessionRevision);
            Assert.Equal(1, policy.WeakFailures);

            await f.Coordinator.ReconcileAsync(ReconcileReason.TunStructuralFailure);

            Assert.True(f.Router.IsRunning);
            Assert.Equal(1, f.Router.Starts);
            Assert.Equal(0, f.Router.Stops);
            Assert.Equal(1, f.Router.SessionRevision);
            Assert.True(f.Coordinator.CurrentConvergenceState.DesiredSatisfied);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void HyperVGuestSyntheticUplinkPolicyEnforcesAllInvariants()
    {
        // Must be Up, Ethernet/WiFi, have usable IPv4, and have default gateway
        Assert.True(PhysicalNetwork.IsEligibleUplink("Ethernet", "Microsoft Hyper-V Network Adapter",
            NetworkInterfaceType.Ethernet, OperationalStatus.Up, hasUsableIpv4: true, hasDefaultGateway: true, isHyperVGuest: true));

        // Missing default gateway -> ineligible
        Assert.False(PhysicalNetwork.IsEligibleUplink("Ethernet", "Microsoft Hyper-V Network Adapter",
            NetworkInterfaceType.Ethernet, OperationalStatus.Up, hasUsableIpv4: true, hasDefaultGateway: false, isHyperVGuest: true));

        // On host (not guest) -> ineligible
        Assert.False(PhysicalNetwork.IsEligibleUplink("Ethernet", "Microsoft Hyper-V Network Adapter",
            NetworkInterfaceType.Ethernet, OperationalStatus.Up, hasUsableIpv4: true, hasDefaultGateway: true, isHyperVGuest: false));

        // Host vEthernet -> ineligible
        Assert.False(PhysicalNetwork.IsEligibleUplink("vEthernet (VM-Direct)", "Hyper-V Virtual Ethernet Adapter",
            NetworkInterfaceType.Ethernet, OperationalStatus.Up, hasUsableIpv4: true, hasDefaultGateway: true, isHyperVGuest: false));

        // VPN/TUN on guest -> ineligible
        Assert.False(PhysicalNetwork.IsEligibleUplink("NetCat-TUN", "Wintun Userspace Tunnel",
            NetworkInterfaceType.Ethernet, OperationalStatus.Up, hasUsableIpv4: true, hasDefaultGateway: true, isHyperVGuest: true));
    }

    [Fact]
    public async Task ChangedMainConfigAllowsLegitimateRestart()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "NetCat-C22-retry-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var f = new Candidate12Tests.Fixture(new SettingsStore(tempRoot));
            await f.Reconcile(ReconcileReason.Startup);
            Assert.Equal(1, f.Router.Starts);
            Assert.Equal(f.A.Id, f.Router.ActiveProfileId);

            // Switching profile triggers legitimate restart
            f.Desired.Current = f.Desired.Current with { SelectedVpnProfileId = f.B.Id };
            await f.Reconcile(ReconcileReason.UserSelectedVpnProfile);
            Assert.Equal(2, f.Router.Starts);
            Assert.Equal(f.B.Id, f.Router.ActiveProfileId);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }
}
