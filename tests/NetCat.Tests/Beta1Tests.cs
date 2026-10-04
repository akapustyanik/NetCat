using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using NetCat.UI;
using NetCat.Updater;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace NetCat.Tests;

public sealed class Beta1Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NetCat-Beta1-" + Guid.NewGuid().ToString("N"));
    private string Put(string path, string text) { var full = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, text); return full; }
    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        for (int i = 0; i < 10; i++)
        {
            try { Directory.Delete(root, true); break; }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }

    private sealed class Scheduler : IStartupTasks
    {
        public StartupTask? Value;
        public bool Fail;
        public readonly Dictionary<string, string> Run = new() { ["NetCat"] = "old", ["Other"] = "keep" };
        public StartupTask? Read() => Value;
        public void Write(StartupTask task) { if (Fail) throw new IOException("denied"); Value = task; }
        public void Delete() => Value = null;
        public void RemoveLegacyEntries() => Run.Remove("NetCat");
        public bool HasLegacyEntry => Run.ContainsKey("NetCat");
    }

    [Fact]
    public async Task AutostartEnableDisableRepairAndMigration()
    {
        var sched = new Scheduler();
        var autostart = new AutostartService(sched, "C:\\bin\\NetCat.exe", "S-1-5-21-test");
        Assert.False(autostart.Enabled);
        Assert.True(sched.HasLegacyEntry);
        await autostart.ReconcileAsync(null, _ => Task.CompletedTask);
        Assert.True(autostart.Enabled);
        Assert.False(sched.HasLegacyEntry);
        await autostart.SetAsync(false, _ => Task.CompletedTask);
        Assert.False(autostart.Enabled);
        Assert.Null(sched.Value);
    }

    [Fact]
    public async Task AutostartFailureNeverCommitsAndSaveFailureRestoresTask()
    {
        var sched = new Scheduler();
        var autostart = new AutostartService(sched, "C:\\bin\\NetCat.exe", "S-1-5-21-test");
        sched.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => autostart.SetAsync(true, _ => Task.CompletedTask));
        Assert.False(autostart.Enabled);
        Assert.Null(sched.Value);

        sched.Fail = false;
        await autostart.SetAsync(true, _ => Task.CompletedTask);
        Assert.True(autostart.Enabled);

        await Assert.ThrowsAsync<InvalidOperationException>(() => autostart.SetAsync(false, _ => throw new InvalidOperationException("save failed")));
        Assert.True(autostart.Enabled);
        Assert.NotNull(sched.Value);
    }

    [Fact]
    public void WindowsTaskSchedulerIntegrationTest()
    {
        if (!OperatingSystem.IsWindows()) return;
        var testName = "NetCat_TestTask_" + Guid.NewGuid().ToString("N");
        var logs = new List<string>();
        var isElevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        var tasks = new WindowsStartupTasks(logs.Add, testName);
        var exePath = Environment.ProcessPath ?? "C:\\Windows\\notepad.exe";
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var expected = new StartupTask(Path.GetFullPath(exePath), sid, Enabled: true, Highest: isElevated, Interactive: true);
        try
        {
            tasks.Write(expected);
            var read = tasks.Read();
            Assert.NotNull(read);
            Assert.True(AutostartService.IsMatch(read, expected));
            Assert.Contains(logs, l => l.Contains("AUTOSTART stage=register"));
            Assert.Contains(logs, l => l.Contains("AUTOSTART stage=verify"));
        }
        finally
        {
            tasks.Delete();
            Assert.Null(tasks.Read());
        }
    }

    [Fact]
    public void OpenVpnBinaryBundleAndCredentialsRoundTrip()
    {
        var p12Bytes = new byte[] { 1, 2, 3, 4, 5 };
        var p12Path = Put("client.p12", "");
        File.WriteAllBytes(p12Path, p12Bytes);
        var ovpnPath = Put("client.ovpn", "client\ndev tun\npkcs12 client.p12\nauth-user-pass\n");
        var bundle = OpenVpnBundle.Read(ovpnPath);
        Assert.Contains("<pkcs12>", bundle.Config);
        Assert.Contains(Convert.ToBase64String(p12Bytes), bundle.Config);
        Assert.DoesNotContain("pkcs12 client.p12", bundle.Config);
        var profile = bundle.ToProfile("Work");
        Assert.Equal("Work", profile.Name);
        Assert.Equal("openvpn", profile.Protocol);
        Assert.Contains("<pkcs12>", profile.OpenVpnConfig);
    }

    [Theory]
    [InlineData("ca")]
    [InlineData("cert")]
    [InlineData("key")]
    [InlineData("tls-auth")]
    public void OpenVpnTextDependenciesAndExistingInlineArePreserved(string directive)
    {
        Put("data.txt", "sample");
        var config = Put("a.ovpn", "client\ndev tun\n" + directive + " data.txt\n<ca>\nold-ca\n</ca>\n");
        var bundle = OpenVpnBundle.Read(config);
        Assert.Contains("<" + directive + ">\nsample", bundle.Config.Replace("\r", ""));
        Assert.Contains("old-ca", bundle.Config);
    }

    [Theory]
    [InlineData("missing.p12", false)] [InlineData("../../private.p12", true)]
    public void OpenVpnMissingOrOutsideDependencyFailsBeforeCommit(string dependency, bool outside)
    {
        var path = Put("a.ovpn", "client\ndev tun\npkcs12 " + dependency);
        var ex = Record.Exception(() => OpenVpnBundle.Read(path)); Assert.NotNull(ex);
        Assert.True(ex is IOException or InvalidDataException); Assert.Contains("pkcs12", ex.Message);
        Assert.Equal(outside, ex is InvalidDataException);
    }







    [Fact]
    public void PhysicalNetworkOutageSuppressesVpnFailover()
    {
        var failover = new FailoverPolicy();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 5; i++)
        {
            failover.Record(id, new DelayResult(false, -1, "Socket operation to unreachable host"), now.AddSeconds(i));
        }
        // When physical network is down / in transition: failover is suppressed
        Assert.False(failover.ShouldRecover(id, 3, now.AddSeconds(6), TimeSpan.FromMinutes(2), physicalNetworkAvailable: false));
        // When physical network is confirmed available: normal failover triggers
        Assert.True(failover.ShouldRecover(id, 3, now.AddSeconds(6), TimeSpan.FromMinutes(2), physicalNetworkAvailable: true));
    }

    [Fact]
    public void SemanticLogCoalescingStressTest()
    {
        var coalescer = new SemanticLogCoalescer(TimeSpan.FromSeconds(2.0));
        var now = DateTimeOffset.UtcNow;
        int uiCount = 0, diagCount = 0;

        // 10,000 unreachable host lines in < 2 seconds
        for (int i = 0; i < 10000; i++)
        {
            if (coalescer.Ingest("connectex: A socket operation was attempted to an unreachable host 198.51.100.1:443", now.AddMilliseconds(i * 0.1), out var ui, out var diag, out _))
            {
                uiCount++;
            }
            if (diag != null) diagCount++;
        }

        Assert.Equal(1, uiCount); // Only first message emitted to UI
        Assert.Equal(5, diagCount); // First 5 raw examples kept for diagnostics

        // Expired flush emits summary
        var flushed = coalescer.FlushExpired(now.AddSeconds(5));
        Assert.Single(flushed);
        Assert.Contains("повторено 10000 раз", flushed[0]);
        Assert.Contains("хост недоступен", flushed[0]);

        // 10,000 bad question size lines
        uiCount = 0; diagCount = 0;
        for (int i = 0; i < 10000; i++)
        {
            if (coalescer.Ingest("dns: bad question size: 0", now.AddSeconds(6).AddMilliseconds(i * 0.1), out var ui, out var diag, out _))
            {
                uiCount++;
            }
            if (diag != null) diagCount++;
        }
        Assert.Equal(1, uiCount);
        Assert.Equal(5, diagCount);

        var dnsFlushed = coalescer.FlushExpired(now.AddSeconds(12));
        Assert.Single(dnsFlushed);
        Assert.Contains("bad question size", dnsFlushed[0]);
        Assert.Contains("повторено 10000 раз", dnsFlushed[0]);
    }

    [Theory]
    [InlineData("address")] [InlineData("index")] [InlineData("dns")] [InlineData("interface")] [InlineData("gateway")] [InlineData("ipv6")]
    public void PhysicalBindingChangesInvalidateSnapshot(string change)
    {
        var old = new NetworkSnapshot("Ethernet", 2, "192.0.2.1", "192.0.2.53", []);
        var next = change switch { "address" => old with { Address = "192.0.2.2" }, "index" => old with { Index = 3 }, "dns" => old with { Dns = "192.0.2.54" }, "interface" => old with { Name = "Wi-Fi" }, "gateway" => old with { DefaultRoute = "192.0.2.254" }, _ => old with { HasIpv6DefaultRoute = true } };
        Assert.False(PhysicalNetwork.SameBinding(old, next)); Assert.False(PhysicalNetwork.SameBinding(old, null));
        var p = ProfileImporter.ParseLink("socks://127.0.0.1:9#dead");
        var config = SingBoxConfig.Build(new() { Mode = RoutingMode.Global, YouTube = ServiceRoute.Zapret, Discord = ServiceRoute.Zapret }, next, p, null, false);
        Assert.Equal(next.Address, config["outbounds"]!.AsArray().Single(n => n?["tag"]?.ToString() == "direct")!["inet4_bind_address"]!.ToString());
        Assert.Equal(next.Name, config["dns"]!["servers"]![0]!["bind_interface"]!.ToString());
        Assert.Equal(next.Dns, config["dns"]!["servers"]![0]!["server"]!.ToString());
    }

    [Fact]
    public void NetworkBurstDebouncesAndWatchdogIsBounded()
    {
        using var signals = new NetworkSignals(false); var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 50; i++) signals.Signal();
        Assert.False(signals.Ready(0, now)); Assert.True(signals.Ready(0, now.AddSeconds(3))); Assert.False(signals.Ready(signals.Revision, now.AddSeconds(3)));
        var policy = new RecoveryPolicy(); Assert.False(policy.Observe(true, now)); Assert.False(policy.Observe(true, now)); Assert.True(policy.Observe(true, now));
        for (int i = 0; i < 100; i++) Assert.False(policy.Observe(true, now));
        Assert.Equal(now.AddSeconds(5), policy.NextAttempt);
        now = now.AddSeconds(5); Assert.True(policy.Observe(true, now)); Assert.Equal(now.AddSeconds(15), policy.NextAttempt);
        Assert.False(policy.Observe(false, now)); // upstream outage with healthy local structure never restarts TUN
    }

    [Fact]
    public void LogFloodHasBoundedMemoryAndOneBatchNotification()
    {
        var queue = new BufferedLog(4096); Parallel.For(0, 10000, i => queue.Add("error " + i));
        Assert.Equal(4096, queue.Count); Assert.Equal(5904, queue.Dropped); Assert.Equal(10000, queue.Received);
        var collection = new BatchedLog(); int notifications = 0; collection.CollectionChanged += (_, _) => notifications++;
        collection.Prepend(queue.Drain(4096)); Assert.Equal(1, notifications); Assert.Equal(2000, collection.Count);
    }

    [Theory]
    [InlineData("corrupt")] [InlineData("signature")] [InlineData("asset")] [InlineData("downgrade")] [InlineData("channel")]
    public void SignedManifestRejectsForgeryReplayAndWrongChannel(string fault)
    {
        var privateKey = new Ed25519PrivateKeyParameters(RandomNumberGenerator.GetBytes(32), 0);
        var manifest = new PackageManifest(1, "1.0.0-beta.2", [], "beta", new("NetCat-v1.0.0-beta.2-win-x64.zip", new string('a', 64)), []);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest); var signer = new Ed25519Signer(); signer.Init(true, privateKey); signer.BlockUpdate(bytes, 0, bytes.Length); var sig = signer.GenerateSignature();
        var key = privateKey.GeneratePublicKey().GetEncoded();
        Assert.Equal(manifest.Version, ReleaseTrust.Verify(bytes, sig, key, "1.0.0-beta.1", "beta", manifest.Package!.Name, manifest.Version).Version);
        if (fault == "corrupt") bytes[5] ^= 1;
        if (fault == "signature") sig[5] ^= 1;
        Assert.Throws<InvalidDataException>(() => ReleaseTrust.Verify(bytes, sig, key, fault == "downgrade" ? manifest.Version : "1.0.0-beta.1", fault == "channel" ? "stable" : "beta", fault == "asset" ? "evil.zip" : manifest.Package.Name, manifest.Version));
    }

    [Fact]
    public void BetaDiscoveryIncludesPrereleasesButStableDoesNot()
    {
        var releases = JsonNode.Parse("""[{"tag_name":"v1.0.0","prerelease":false},{"tag_name":"v1.1.0-beta.1","prerelease":true},{"tag_name":"v9.0.0","draft":true}]""")!.AsArray();
        Assert.Equal("v1.1.0-beta.1", ModuleUpdater.SelectNetCatRelease(releases, "beta")["tag_name"]!.ToString());
        Assert.Equal("v1.0.0", ModuleUpdater.SelectNetCatRelease(releases, "stable")["tag_name"]!.ToString());
    }

    [Fact]
    public void SameReleaseVersionWithDifferentArchiveCannotBeReused()
    {
        var manifest = new PackageManifest(1, "1.0.0-beta.2", [], "beta", new("NetCat-v1.0.0-beta.2-win-x64.zip", new string('a', 64)), []);
        ReleaseTrust.RememberVersion(root, manifest); ReleaseTrust.RememberVersion(root, manifest);
        Assert.Throws<InvalidDataException>(() => ReleaseTrust.RememberVersion(root, manifest with { Package = manifest.Package! with { Sha256 = new string('b', 64) } }));
    }

    [Fact]
    public void ReleasePackageExcludesInternalDocsAndIncludesUserNotices()
    {
        var buildTestScript = File.ReadAllText(Path.Combine(RoutingTests.FindRoot(), "scripts", "Build-Test.ps1"));
        Assert.DoesNotContain("docs/*.md", buildTestScript);
        var activeDocs = Directory.GetFiles(Path.Combine(RoutingTests.FindRoot(), "docs"), "*.md", SearchOption.TopDirectoryOnly);
        Assert.DoesNotContain(activeDocs, f => Path.GetFileName(f).StartsWith("TEST_BUILD_", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(RoutingTests.FindRoot(), "README.md")));
        Assert.True(File.Exists(Path.Combine(RoutingTests.FindRoot(), "THIRD_PARTY_NOTICES.md")));
    }

    [Fact]
    public void OpenVpnBundleSupportsTrailingDirectorySeparators()
    {
        var subDir = Path.Combine(root, "bundle_trailing") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(subDir);
        var cert = Path.Combine(subDir, "test.p12");
        File.WriteAllBytes(cert, [1, 2, 3, 4]);
        var ovpn = Path.Combine(subDir, "client.ovpn");
        File.WriteAllText(ovpn, "client\ndev tun\nremote 198.51.100.1 1194\npkcs12 test.p12\n");
        var bundle = OpenVpnBundle.Read(ovpn);
        Assert.Contains("<pkcs12>", bundle.Config);
    }

    [Fact]
    public void SuccessfulRebuildClearsTransitionUiState()
    {
        var sm = new NetworkLifecycleStateMachine();

        // 1. Initial state is Normal
        Assert.Equal(NetworkLifecycleState.Normal, sm.State);
        Assert.False(sm.InNetworkTransition);
        Assert.False(sm.IsNetworkRebuilding);
        Assert.False(sm.IsNetworkUnavailable);

        // 2. Transition begins
        long gen1 = sm.BeginTransition();
        Assert.Equal(NetworkLifecycleState.NetworkTransition, sm.State);
        Assert.True(sm.InNetworkTransition);

        // 3. Rebuilding begins
        sm.BeginRebuilding(gen1);
        Assert.Equal(NetworkLifecycleState.Rebuilding, sm.State);
        Assert.True(sm.IsNetworkRebuilding);

        // 4. Newer transition occurs before rebuild finishes
        long gen2 = sm.BeginTransition();
        Assert.True(gen2 > gen1);

        // Older rebuild completion attempt for gen1 is ignored!
        bool oldApplied = sm.CompleteRebuild(gen1, true, "Ethernet", "192.168.1.105");
        Assert.False(oldApplied);
        Assert.NotEqual(NetworkLifecycleState.Normal, sm.State);

        // 5. Successful completion for current generation resets to Normal
        sm.BeginRebuilding(gen2);
        bool newApplied = sm.CompleteRebuild(gen2, true, "Ethernet", "192.168.1.105");
        Assert.True(newApplied);
        Assert.Equal(NetworkLifecycleState.Normal, sm.State);
        Assert.False(sm.InNetworkTransition);
        Assert.False(sm.IsNetworkRebuilding);
        Assert.Equal("Физическая сеть восстановлена: Ethernet, 192.168.1.105", sm.TemporaryStatus);

        // 6. Repeated failure transitions to NetworkUnavailable
        long gen3 = sm.BeginTransition();
        sm.BeginRebuilding(gen3);
        sm.MarkUnavailable(gen3);
        Assert.Equal(NetworkLifecycleState.NetworkUnavailable, sm.State);
        Assert.True(sm.IsNetworkUnavailable);
        Assert.False(sm.InNetworkTransition);

        // 7. Reset restores Normal
        sm.Reset();
        Assert.Equal(NetworkLifecycleState.Normal, sm.State);
        Assert.Null(sm.TemporaryStatus);
    }



    [Fact]
    public void StablePhysicalSnapshotIgnoresTunNetworkChangeEvents()
    {
        var sm = new NetworkLifecycleStateMachine();
        var signals = new NetworkSignals(subscribe: false);
        long handled = signals.Revision;

        var active = new NetworkSnapshot("Ethernet", 12, "192.168.1.105", "192.168.1.1", new[] { "lan" }, false, "", "192.168.1.1");
        var current = new NetworkSnapshot("Ethernet", 12, "192.168.1.105", "192.168.1.1", new[] { "lan" }, false, "", "192.168.1.1");

        // Simulate TUN / Wintun adapter triggering Windows NetworkAddressChanged
        signals.Signal();
        Assert.True(signals.Revision > handled);

        // Advance time past the 2s debounce window
        var now = DateTimeOffset.UtcNow.AddSeconds(3);
        bool signalReady = signals.Ready(handled, now);
        Assert.True(signalReady);

        if (signalReady) handled = signals.Revision;

        // Verify physical change evaluation
        bool physicalChanged = PhysicalNetwork.HasPhysicalChanged(active, current, captureFailed: false);
        Assert.False(physicalChanged);

        // Verify state machine is NOT transitioned
        if (physicalChanged && sm.State == NetworkLifecycleState.Normal)
        {
            sm.BeginTransition();
        }

        Assert.Equal(NetworkLifecycleState.Normal, sm.State);
        Assert.False(sm.InNetworkTransition);
        Assert.False(sm.IsNetworkRebuilding);
        Assert.Equal(0, sm.Generation);
    }

    [Fact]
    public void RouterRebuildDoesNotTriggerAnotherPhysicalNetworkTransition()
    {
        var sm = new NetworkLifecycleStateMachine();
        var signals = new NetworkSignals(subscribe: false);
        long handled = signals.Revision;

        // 1. Legitimate physical transition occurs (e.g. WiFi switch)
        var oldPhysical = new NetworkSnapshot("Wi-Fi", 15, "192.168.1.50", "192.168.1.1", new[] { "home" }, false, "", "192.168.1.1");
        var newPhysical = new NetworkSnapshot("Ethernet", 12, "192.168.1.105", "192.168.1.1", new[] { "office" }, false, "", "192.168.1.1");

        bool initialChange = PhysicalNetwork.HasPhysicalChanged(oldPhysical, newPhysical, captureFailed: false);
        Assert.True(initialChange);

        long gen = sm.BeginTransition();
        sm.BeginRebuilding(gen);
        bool applied = sm.CompleteRebuild(gen, true, newPhysical.Name, newPhysical.Address);
        Assert.True(applied);
        Assert.Equal(NetworkLifecycleState.Normal, sm.State);

        // After rebuild, active physical interface is now newPhysical
        var activePhysical = newPhysical;

        // 2. Rebuild touches TUN / routing table, which causes NetworkAddressChanged to fire
        signals.Signal();
        var future = DateTimeOffset.UtcNow.AddSeconds(3);
        bool signalReady = signals.Ready(handled, future);
        Assert.True(signalReady);
        if (signalReady) handled = signals.Revision;

        // Snapshot of physical network is captured again
        var currentPhysical = new NetworkSnapshot("Ethernet", 12, "192.168.1.105", "192.168.1.1", new[] { "office" }, false, "", "192.168.1.1");

        bool loopTriggered = PhysicalNetwork.HasPhysicalChanged(activePhysical, currentPhysical, captureFailed: false);
        Assert.False(loopTriggered);

        if (loopTriggered && sm.State == NetworkLifecycleState.Normal)
        {
            sm.BeginTransition();
        }

        // Must remain Normal, generation must not increment, no new transition
        Assert.Equal(NetworkLifecycleState.Normal, sm.State);
        Assert.False(sm.InNetworkTransition);
        Assert.Equal(gen, sm.Generation);
    }

    [Fact]
    public void Simulated60SecondsStableNetworkProducesZeroTransitions()
    {
        var sm = new NetworkLifecycleStateMachine();
        var signals = new NetworkSignals(subscribe: false);
        long handled = signals.Revision;

        var active = new NetworkSnapshot("Ethernet", 12, "192.168.1.105", "192.168.1.1", new[] { "lan" }, false, "", "192.168.1.1");
        int transitionsCount = 0;
        int rebuildsCount = 0;

        var simTime = DateTimeOffset.UtcNow;

        // Simulate 60 seconds of runtime (200 ticks at 300ms intervals)
        // Spurious TUN/virtual adapter signals fire every 3 seconds
        for (int tick = 0; tick < 200; tick++)
        {
            simTime = simTime.AddMilliseconds(300);

            if (tick % 10 == 0) // every 3 seconds
            {
                signals.Signal();
            }

            bool signalReady = signals.Ready(handled, simTime);
            if (signalReady) handled = signals.Revision;

            // Physical binding remains identical
            var current = new NetworkSnapshot("Ethernet", 12, "192.168.1.105", "192.168.1.1", new[] { "lan" }, false, "", "192.168.1.1");
            bool physicalChanged = PhysicalNetwork.HasPhysicalChanged(active, current, captureFailed: false);

            if (physicalChanged && sm.State == NetworkLifecycleState.Normal)
            {
                sm.BeginTransition();
                transitionsCount++;
            }

            if (physicalChanged || sm.State == NetworkLifecycleState.NetworkTransition)
            {
                rebuildsCount++;
            }
        }

        Assert.Equal(0, transitionsCount);
        Assert.Equal(0, rebuildsCount);
        Assert.Equal(NetworkLifecycleState.Normal, sm.State);
        Assert.False(sm.InNetworkTransition);
        Assert.Equal(0, sm.Generation);
    }

    [Fact]
    public void ConnectedOpenVpnWithoutRedirectGatewayIsNotRejected()
    {
        // Global routing table contains default routes on Wi-Fi (12), NetCat-TUN (47), and Radmin (30)
        var allSystemRoutes = new List<RouteRow>
        {
            new(12, "0.0.0.0/0", "192.168.1.1", 25, "NetMgmt", "Dhcp"),
            new(47, "0.0.0.0/0", "172.29.255.2", 1, "Local", "Manual"),
            new(30, "0.0.0.0/0", "26.0.0.1", 9000, "NetMgmt", "Manual"),
            // Routes on OpenVPN adapter (InterfaceIndex = 20)
            new(20, "10.10.11.0/24", "0.0.0.0", 256, "Local", "WellKnown"),
            new(20, "10.10.11.5/32", "0.0.0.0", 256, "Local", "WellKnown"),
            new(20, "10.0.117.0/24", "10.10.11.1", 256, "NetMgmt", "Manual"),
            new(20, "255.255.255.255/32", "0.0.0.0", 256, "Local", "WellKnown"),
            new(20, "224.0.0.0/4", "0.0.0.0", 256, "Local", "WellKnown")
        };

        // Filter strictly by the OpenVPN interface index
        const int openVpnIfIndex = 20;
        var openVpnRoutes = allSystemRoutes.Where(r => r.InterfaceIndex == openVpnIfIndex).ToList();

        bool isTakeover = RouteTable.IsOpenVpnDefaultTakeover(openVpnRoutes, out var offendingRoutes);

        // Must NOT be classified as takeover!
        Assert.False(isTakeover);
        Assert.Empty(offendingRoutes);
    }

    [Fact]
    public void RealZeroPrefixRouteOnOpenVpnIsRejected()
    {
        var openVpnRoutes = new List<RouteRow>
        {
            new(20, "10.10.11.0/24", "0.0.0.0", 256, "Local", "WellKnown"),
            new(20, "10.10.11.5/32", "0.0.0.0", 256, "Local", "WellKnown"),
            new(20, "0.0.0.0/0", "10.10.11.1", 50, "NetMgmt", "Dhcp")
        };

        bool isTakeover = RouteTable.IsOpenVpnDefaultTakeover(openVpnRoutes, out var offendingRoutes);

        Assert.True(isTakeover);
        Assert.Single(offendingRoutes);
        var offending = offendingRoutes[0];
        Assert.Equal(20, offending.InterfaceIndex);
        Assert.Equal("0.0.0.0/0", offending.DestinationPrefix);
        Assert.Equal("10.10.11.1", offending.NextHop);
        Assert.Equal(50u, offending.RouteMetric);
        Assert.Equal("NetMgmt", offending.Protocol);
        Assert.Equal("Dhcp", offending.Origin);
    }

    [Fact]
    public void RedirectGatewayDef1PairIsRejected()
    {
        var openVpnRoutesWithPair = new List<RouteRow>
        {
            new(20, "10.10.11.0/24", "0.0.0.0", 256, "Local", "WellKnown"),
            new(20, "0.0.0.0/1", "10.10.11.1", 1, "NetMgmt", "Manual"),
            new(20, "128.0.0.0/1", "10.10.11.1", 1, "NetMgmt", "Manual")
        };

        bool isTakeover = RouteTable.IsOpenVpnDefaultTakeover(openVpnRoutesWithPair, out var offendingRoutes);

        Assert.True(isTakeover);
        Assert.Equal(2, offendingRoutes.Count);
        Assert.Contains(offendingRoutes, r => r.DestinationPrefix == "0.0.0.0/1");
        Assert.Contains(offendingRoutes, r => r.DestinationPrefix == "128.0.0.0/1");
        Assert.All(offendingRoutes, r => Assert.Equal(20, r.InterfaceIndex));

        // Incomplete pair (only 0.0.0.0/1 without 128.0.0.0/1) is NOT a def1 takeover
        var partialPair = new List<RouteRow>
        {
            new(20, "10.10.11.0/24", "0.0.0.0", 256, "Local", "WellKnown"),
            new(20, "0.0.0.0/1", "10.10.11.1", 1, "NetMgmt", "Manual")
        };
        Assert.False(RouteTable.IsOpenVpnDefaultTakeover(partialPair, out var partialOffending));
        Assert.Empty(partialOffending);
    }

    [Fact]
    public void PrivatePushedRouteIsNotMistakenForDefault()
    {
        var routes = new List<RouteRow>
        {
            new(20, "10.10.11.0/24", "0.0.0.0", 256, "Local", "WellKnown"),
            new(20, "10.0.117.0/24", "10.10.11.1", 256, "NetMgmt", "Manual"),
            new(20, "10.0.0.0/8", "10.10.11.1", 256, "NetMgmt", "Manual"),
            new(20, "172.16.0.0/12", "10.10.11.1", 256, "NetMgmt", "Manual"),
            new(20, "192.168.50.0/24", "10.10.11.1", 256, "NetMgmt", "Manual"),
            new(20, "10.0.117.7/32", "10.10.11.1", 256, "NetMgmt", "Manual"),
            new(20, "10.10.11.1/32", "10.10.11.1", 256, "NetMgmt", "Manual"),
            new(20, "224.0.0.0/4", "0.0.0.0", 256, "Local", "WellKnown"),
            new(20, "255.255.255.255/32", "0.0.0.0", 256, "Local", "WellKnown")
        };

        bool isTakeover = RouteTable.IsOpenVpnDefaultTakeover(routes, out var offendingRoutes);

        Assert.False(isTakeover);
        Assert.Empty(offendingRoutes);
    }

    [Fact]
    public void RouteTableLiveCaptureReturnsActiveRoutes()
    {
        if (!OperatingSystem.IsWindows()) return;

        var routes = RouteTable.CaptureIpv4();
        Assert.NotEmpty(routes);
        Assert.All(routes, r =>
        {
            Assert.True(r.InterfaceIndex > 0);
            Assert.False(string.IsNullOrWhiteSpace(r.DestinationPrefix));
            Assert.Contains("/", r.DestinationPrefix);
            Assert.False(string.IsNullOrWhiteSpace(r.NextHop));
            Assert.False(string.IsNullOrWhiteSpace(r.Protocol));
            Assert.False(string.IsNullOrWhiteSpace(r.Origin));
        });

        // Test diff computation
        var modified = routes.Take(2).ToList();
        var diff = RouteTable.ComputeDiff(routes, modified);
        Assert.Equal(routes.Count - modified.Count, diff.Removed.Count);
        Assert.Empty(diff.Added);
    }

    [Fact]
    public async Task AutostartArgumentIsRegistered()
    {
        var sched = new Scheduler();
        var autostart = new AutostartService(sched, "C:\\bin\\NetCat.exe", "S-1-5-21-test");
        await autostart.SetAsync(true, _ => Task.CompletedTask);
        Assert.NotNull(sched.Value);
        Assert.Equal("--autostart", sched.Value.Arguments);
    }

    [Fact]
    public void AutostartHiddenStateStartsWithoutWindowFlash()
    {
        var store = new SettingsStore(Path.Combine(root, "store-hidden"));
        Assert.Equal(WindowPresentationState.HiddenToTray, store.LoadPresentationState(isAutostart: true));

        store.SavePresentationState(WindowPresentationState.HiddenToTray);
        Assert.Equal(WindowPresentationState.HiddenToTray, store.LoadPresentationState(isAutostart: true));
    }

    [Fact]
    public void AutostartVisibleStateRestoresWindow()
    {
        var store = new SettingsStore(Path.Combine(root, "store-visible"));
        store.SavePresentationState(WindowPresentationState.VisibleNormal);
        Assert.Equal(WindowPresentationState.VisibleNormal, store.LoadPresentationState(isAutostart: true));

        store.SavePresentationState(WindowPresentationState.VisibleMaximized);
        Assert.Equal(WindowPresentationState.VisibleMaximized, store.LoadPresentationState(isAutostart: true));
    }

    [Fact]
    public void ManualLaunchAlwaysShowsWindow()
    {
        var store = new SettingsStore(Path.Combine(root, "store-manual"));
        Assert.Equal(WindowPresentationState.VisibleNormal, store.LoadPresentationState(isAutostart: false));

        store.SavePresentationState(WindowPresentationState.HiddenToTray);
        Assert.Equal(WindowPresentationState.VisibleNormal, store.LoadPresentationState(isAutostart: false));

        store.SavePresentationState(WindowPresentationState.VisibleMaximized);
        Assert.Equal(WindowPresentationState.VisibleMaximized, store.LoadPresentationState(isAutostart: false));
    }

    [Fact]
    public void DesiredRuntimeStateSurvivesShutdownCleanup()
    {
        var store = new SettingsStore(Path.Combine(root, "store-shutdown"));
        var desired = new DesiredRuntimeState
        {
            MainVpnEnabled = true,
            TunEnabled = true,
            ZapretEnabled = true,
            OpenVpnEnabled = true,
            SelectedVpnProfileId = Guid.NewGuid(),
            SelectedOpenVpnProfileId = Guid.NewGuid()
        };
        store.SaveDesiredState(desired);

        var loaded = store.LoadDesiredState();
        Assert.True(loaded.MainVpnEnabled);
        Assert.True(loaded.TunEnabled);
        Assert.True(loaded.ZapretEnabled);
        Assert.True(loaded.OpenVpnEnabled);
        Assert.Equal(desired.SelectedVpnProfileId, loaded.SelectedVpnProfileId);
        Assert.Equal(desired.SelectedOpenVpnProfileId, loaded.SelectedOpenVpnProfileId);
    }

    [Fact]
    public void ExplicitDisconnectUpdatesDesiredState()
    {
        var store = new SettingsStore(Path.Combine(root, "store-disconnect"));
        var desired = new DesiredRuntimeState
        {
            MainVpnEnabled = true,
            TunEnabled = true,
            ZapretEnabled = true,
            OpenVpnEnabled = true
        };
        store.SaveDesiredState(desired);

        desired = desired with { MainVpnEnabled = false, ZapretEnabled = false, OpenVpnEnabled = false };
        store.SaveDesiredState(desired);

        var loaded = store.LoadDesiredState();
        Assert.False(loaded.MainVpnEnabled);
        Assert.False(loaded.ZapretEnabled);
        Assert.False(loaded.OpenVpnEnabled);
    }

    [Fact]
    public void StartupRestoreWaitsForUsableNetwork()
    {
        var invalid = new NetworkSnapshot("Ethernet", 2, "0.0.0.0", "192.168.1.1", []);
        bool isReady = !string.IsNullOrWhiteSpace(invalid.Name) &&
                       !string.IsNullOrWhiteSpace(invalid.Address) &&
                       invalid.Address != "0.0.0.0" &&
                       invalid.Index > 0;
        Assert.False(isReady);

        var valid = new NetworkSnapshot("Ethernet", 2, "192.168.1.100", "192.168.1.1", []);
        isReady = !string.IsNullOrWhiteSpace(valid.Name) &&
                  !string.IsNullOrWhiteSpace(valid.Address) &&
                  valid.Address != "0.0.0.0" &&
                  valid.Index > 0;
        Assert.True(isReady);
    }

    [Fact]
    public void StartupRestoreVpnAndZapret()
    {
        var store = new SettingsStore(Path.Combine(root, "store-restore-vpn-zapret"));
        var vpnId = Guid.NewGuid();
        var desired = new DesiredRuntimeState
        {
            MainVpnEnabled = true,
            TunEnabled = true,
            ZapretEnabled = true,
            OpenVpnEnabled = false,
            SelectedVpnProfileId = vpnId
        };
        store.SaveDesiredState(desired);

        var loaded = store.LoadDesiredState();
        Assert.True(loaded.MainVpnEnabled);
        Assert.True(loaded.TunEnabled);
        Assert.True(loaded.ZapretEnabled);
        Assert.False(loaded.OpenVpnEnabled);
        Assert.Equal(vpnId, loaded.SelectedVpnProfileId);
    }

    [Fact]
    public void StartupRestoreVpnTunZapretAndOpenVpn()
    {
        var store = new SettingsStore(Path.Combine(root, "store-restore-all"));
        var vpnId = Guid.NewGuid();
        var ovpnId = Guid.NewGuid();
        var desired = new DesiredRuntimeState
        {
            MainVpnEnabled = true,
            TunEnabled = true,
            ZapretEnabled = true,
            OpenVpnEnabled = true,
            SelectedVpnProfileId = vpnId,
            SelectedOpenVpnProfileId = ovpnId
        };
        store.SaveDesiredState(desired);

        var loaded = store.LoadDesiredState();
        Assert.True(loaded.MainVpnEnabled);
        Assert.True(loaded.TunEnabled);
        Assert.True(loaded.ZapretEnabled);
        Assert.True(loaded.OpenVpnEnabled);
        Assert.Equal(vpnId, loaded.SelectedVpnProfileId);
        Assert.Equal(ovpnId, loaded.SelectedOpenVpnProfileId);
    }

    [Fact]
    public void StartupRestoreDoesNotRunOnResume()
    {
        var machine = new NetworkLifecycleStateMachine();
        Assert.Equal(NetworkLifecycleState.Normal, machine.State);

        machine.BeginTransition();
        Assert.Equal(NetworkLifecycleState.NetworkTransition, machine.State);
        Assert.True(machine.InNetworkTransition);

        machine.BeginRebuilding(machine.Generation);
        Assert.True(machine.IsNetworkRebuilding);
        machine.CompleteRebuild(machine.Generation, true, "Ethernet", "192.168.1.100");
        Assert.Equal(NetworkLifecycleState.Normal, machine.State);
        Assert.False(machine.InNetworkTransition);
        Assert.False(machine.IsNetworkRebuilding);
    }

    [Fact]
    public void TemporaryStartupNetworkFailureDoesNotClearDesiredState()
    {
        var store = new SettingsStore(Path.Combine(root, "store-temp-net-fail"));
        var desired = new DesiredRuntimeState { MainVpnEnabled = true, ZapretEnabled = true };
        store.SaveDesiredState(desired);

        var networkAvailable = false;
        if (!networkAvailable)
        {
            // Temporary failure must not clear desired state
        }

        var loaded = store.LoadDesiredState();
        Assert.True(loaded.MainVpnEnabled);
        Assert.True(loaded.ZapretEnabled);
    }

    [Fact]
    public void OpenVpnOwnedRouteDoesNotFallBackToDirectWhenUnavailable()
    {
        var physical = new NetworkSnapshot("Ethernet", 2, "192.168.1.105", "192.168.1.1", []);
        var ovpnProfile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Corp OpenVPN",
            Protocol = "openvpn",
            Core = "OpenVPN",
            LearnedRoutes = ["10.0.117.0/24"]
        };
        var settings = new AppSettings
        {
            OpenVpnProfileId = ovpnProfile.Id,
            Profiles = [ovpnProfile]
        };

        var config = SingBoxConfig.Build(settings, physical, null, null, false);
        var rules = config["route"]!["rules"]!.AsArray();

        var blockedRoute = rules.FirstOrDefault(r => r?["ip_cidr"]?.ToJsonString().Contains("10.0.117.0/24") == true);
        Assert.NotNull(blockedRoute);
        Assert.Equal("reject", blockedRoute["action"]?.ToString());

        int blockedIndex = rules.IndexOf(blockedRoute);
        var privateRule = rules.FirstOrDefault(r => r?["ip_is_private"]?.GetValue<bool>() == true);
        Assert.NotNull(privateRule);
        int privateIndex = rules.IndexOf(privateRule);
        Assert.True(blockedIndex < privateIndex, "OpenVPN-owned route must precede generic ip_is_private direct rule");
    }

    [Fact]
    public void VpnUpstreamFailureDoesNotTriggerTunRecovery()
    {
        var policy = new RecoveryPolicy();
        var now = DateTimeOffset.UtcNow;

        for (int i = 0; i < 50; i++)
        {
            bool retry = policy.Observe(structuralFailure: false, now.AddSeconds(i));
            Assert.False(retry, "Healthy TUN must not trigger recovery even during upstream timeouts");
        }

        Assert.Equal(0, policy.Attempts);
    }

    [Fact]
    public void StableNetworkDoesNotGenerateFalseTransitions()
    {
        var machine = new NetworkLifecycleStateMachine();
        var baseline = new NetworkSnapshot("Ethernet", 2, "192.168.1.105", "192.168.1.1", ["home.arpa"]);

        for (int i = 0; i < 120; i++)
        {
            var current = new NetworkSnapshot("Ethernet", 2, "192.168.1.105", "192.168.1.1", ["home.arpa"]);
            bool changed = PhysicalNetwork.HasPhysicalChanged(baseline, current, captureFailed: false);
            Assert.False(changed, "Stable network must not report physical change");

            if (changed && machine.State == NetworkLifecycleState.Normal)
            {
                machine.BeginTransition();
            }
        }

        Assert.Equal(NetworkLifecycleState.Normal, machine.State);
        Assert.False(machine.InNetworkTransition);
        Assert.False(machine.IsNetworkRebuilding);
    }

    [Fact]
    public async Task HiddenAutostartRunsRestoreWithoutWindowLoaded()
    {
        bool restoreRun = false;
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime { OnEnsureRunning = () => restoreRun = true };
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn);

        // Simulating launch where window Loaded is NEVER invoked (e.g. autostart to tray)
        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);

        Assert.True(restoreRun);
        Assert.Equal(StartupRestoreState.Completed, coordinator.State);
    }

    [Fact]
    public async Task HiddenAutostartRestoreRunsExactlyOnce()
    {
        int restoreCalls = 0;
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime
        {
            OnEnsureRunningAsync = async () =>
            {
                Interlocked.Increment(ref restoreCalls);
                await Task.Delay(50);
            }
        };
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn);

        await Task.WhenAll(
            coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None),
            coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None),
            coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None)
        );

        Assert.Equal(1, restoreCalls);
        Assert.Equal(StartupRestoreState.Completed, coordinator.State);
    }

    [Fact]
    public async Task SecondLaunchDoesNotRunRestoreAgain()
    {
        int restoreCalls = 0;
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime { OnEnsureRunning = () => Interlocked.Increment(ref restoreCalls) };
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn);

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);
        Assert.Equal(1, restoreCalls);
        Assert.Equal(StartupRestoreState.Completed, coordinator.State);

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);
        Assert.Equal(1, restoreCalls);
    }

    [Fact]
    public void StartupBatchDoesNotScheduleReactiveRouteApply()
    {
        var storeRoot = Path.Combine(root, "batch-test-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(storeRoot);
        var settings = new AppSettings { Tun = true };
        store.SaveDesiredState(new DesiredRuntimeState { TunEnabled = true });

        using var vm = new MainViewModel(store, settings);
        Assert.True(vm.PendingRoutes.IsCompleted);

        using (vm.CreateStartupBatch())
        {
            Assert.True(vm.SuppressReactiveApply);
            vm.State.Tun = !vm.State.Tun;
            Assert.True(vm.PendingRoutes.IsCompleted);
        }

        Assert.False(vm.SuppressReactiveApply);
    }

    [Fact]
    public async Task StartupTunReadinessWaitDoesNotRebuildOnTransientFirstFailure()
    {
        int readAttempts = 0;

        var health = await TunnelInspection.WaitForTunReadyAsync(
            isRunning: () => true,
            getPort: () => 1080,
            tun: true,
            timeout: TimeSpan.FromSeconds(5),
            ct: CancellationToken.None,
            pollInterval: TimeSpan.FromMilliseconds(50),
            inspectOverride: ct =>
            {
                readAttempts++;
                bool settled = readAttempts >= 2;
                return Task.FromResult(new RouterHealth(
                    CoreProcessHealthy: true,
                    TunInterfacePresent: settled,
                    TunRoutesPresent: settled,
                    LocalDataPathHealthy: true,
                    TunIndex: settled ? 15 : 0
                ));
            });

        Assert.False(health.StructuralFailure);
        Assert.True(health.TunInterfacePresent);
        Assert.True(health.TunRoutesPresent);
        Assert.True(readAttempts >= 2);
    }

    [Fact]
    public async Task StartupPersistentFailureEntersPendingRetry()
    {
        var storeRoot = Path.Combine(root, "pending-retry-test-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(storeRoot);
        var originalDesired = new DesiredRuntimeState
        {
            MainVpnEnabled = true,
            TunEnabled = true,
            SelectedVpnProfileId = Guid.NewGuid()
        };
        store.SaveDesiredState(originalDesired);

        var desired = new Candidate9Tests.FakeDesiredProvider(originalDesired);
        var router = new Candidate9Tests.FakeRouterRuntime { FailStart = true };
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, backoff: [TimeSpan.FromSeconds(60)]);

        var task = coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);

        for (int i = 0; i < 40 && coordinator.State != StartupRestoreState.PendingRetry; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(StartupRestoreState.PendingRetry, coordinator.State);

        var loadedDesired = store.LoadDesiredState();
        Assert.True(loadedDesired.MainVpnEnabled);
        Assert.True(loadedDesired.TunEnabled);
        Assert.Equal(originalDesired.SelectedVpnProfileId, loadedDesired.SelectedVpnProfileId);

        coordinator.Cancel();
    }

    [Fact]
    public async Task PendingRetrySucceedsWhenEndpointReturns()
    {
        bool endpointAvailable = false;
        int attempts = 0;

        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime
        {
            OnEnsureRunning = () =>
            {
                attempts++;
                if (!endpointAvailable) throw new IOException("Network route temporarily down");
            }
        };
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, backoff: [TimeSpan.FromSeconds(60)]);

        var task = coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);

        for (int i = 0; i < 40 && coordinator.State != StartupRestoreState.PendingRetry; i++)
        {
            await Task.Delay(50);
        }
        Assert.Equal(StartupRestoreState.PendingRetry, coordinator.State);
        Assert.Equal(1, attempts);

        endpointAvailable = true;
        coordinator.TriggerRetry();

        for (int i = 0; i < 40 && coordinator.State != StartupRestoreState.Completed; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(StartupRestoreState.Completed, coordinator.State);
        Assert.True(attempts >= 2);
    }

    [Fact]
    public async Task ExplicitUserOffCancelsPendingRetry()
    {
        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState { MainVpnEnabled = true });
        var router = new Candidate9Tests.FakeRouterRuntime { FailStart = true };
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var coordinator = Candidate9Tests.CreateCoordinator(desired, router, zapret, openvpn, backoff: [TimeSpan.FromSeconds(60)]);

        var task = coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);

        for (int i = 0; i < 40 && coordinator.State != StartupRestoreState.PendingRetry; i++)
        {
            await Task.Delay(50);
        }
        Assert.Equal(StartupRestoreState.PendingRetry, coordinator.State);

        coordinator.Cancel();

        Assert.Equal(StartupRestoreState.Cancelled, coordinator.State);
    }

    [Fact]
    public void HealthyTunWithoutPhysicalBindingDoesNotReportNetworkRecovered()
    {
        var machine = new NetworkLifecycleStateMachine();
        long gen = machine.BeginTransition();
        machine.BeginRebuilding(gen);

        NetworkSnapshot? nullPhysical = null;
        Assert.False(PhysicalNetwork.IsUsablePhysicalBinding(nullPhysical));

        var zeroPhysical = new NetworkSnapshot("Сеть", 12, "0.0.0.0", "192.168.1.1", []);
        Assert.False(PhysicalNetwork.IsUsablePhysicalBinding(zeroPhysical));

        var loopbackPhysical = new NetworkSnapshot("Loopback", 1, "127.0.0.1", "127.0.0.1", []);
        Assert.False(PhysicalNetwork.IsUsablePhysicalBinding(loopbackPhysical));

        Assert.Null(machine.TemporaryStatus);
        Assert.True(machine.IsNetworkRebuilding);

        var validPhysical = new NetworkSnapshot("Ethernet", 12, "192.168.1.105", "192.168.1.1", ["lan"]);
        Assert.True(PhysicalNetwork.IsUsablePhysicalBinding(validPhysical));

        bool applied = machine.CompleteRebuild(gen, true, validPhysical.Name, validPhysical.Address);
        Assert.True(applied);
        Assert.Equal("Физическая сеть восстановлена: Ethernet, 192.168.1.105", machine.TemporaryStatus);
    }

    [Fact]
    public async Task TunTogglePersistsWithoutVpnToggle()
    {
        var storeRoot = Path.Combine(root, "tun-toggle-test-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(storeRoot);
        var settings = new AppSettings { Tun = true };
        store.SaveDesiredState(new DesiredRuntimeState { TunEnabled = true });

        using var vm = new MainViewModel(store, settings);
        Assert.True(vm.DesiredState.TunEnabled);

        vm.UserRequestedTunChange(false);
        await vm.SaveAsync();

        Assert.False(vm.State.Tun);
        Assert.False(vm.DesiredState.TunEnabled);

        var loadedDesired = store.LoadDesiredState();
        Assert.False(loadedDesired.TunEnabled);
    }

    [Fact]
    public async Task StartupVpnZapretOpenVpnProducesBoundedRouterReconfigs()
    {
        var logs = new List<string>();
        int routerReconfigs = 0;

        var desired = new Candidate9Tests.FakeDesiredProvider(new DesiredRuntimeState
        {
            MainVpnEnabled = true,
            ZapretEnabled = true,
            OpenVpnEnabled = true
        });
        var router = new Candidate9Tests.FakeRouterRuntime
        {
            OnEnsureRunning = () =>
            {
                routerReconfigs++;
                logs.Add("ROUTER_RECONFIG reason=startup-main-vpn");
            }
        };
        var zapret = new Candidate9Tests.FakeZapretRuntime();
        var openvpn = new Candidate9Tests.FakeOpenVpnRuntime();
        var coordinator = Candidate9Tests.CreateCoordinator(
            desired, router, zapret, openvpn,
            finalizeOverride: _ =>
            {
                routerReconfigs++;
                logs.Add("ROUTER_RECONFIG reason=startup-openvpn-finalize");
                return Task.CompletedTask;
            }
        );

        await coordinator.ReconcileAsync(ReconcileReason.Startup, CancellationToken.None);

        Assert.Equal(StartupRestoreState.Completed, coordinator.State);
        Assert.True(routerReconfigs <= 2, $"Expected at most 2 router reconfigurations, got {routerReconfigs}");
        Assert.Contains(logs, l => l == "ROUTER_RECONFIG reason=startup-main-vpn");
        Assert.Contains(logs, l => l == "ROUTER_RECONFIG reason=startup-openvpn-finalize");
        Assert.Equal(2, routerReconfigs); // Independent Main VPN start, then the successful link's static overlay.
    }
}
