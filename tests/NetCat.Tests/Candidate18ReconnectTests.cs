using System.Diagnostics;
using System.Text.RegularExpressions;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed class Candidate18ReconnectTests
{
    // One real owned stdout process with a controlled transcript, fake adapter
    // capture and exact route-table boundary. This never creates a Windows TUN.
    internal sealed class Fixture : IAsyncDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "NetCat-C18-Reconnect-" + Guid.NewGuid().ToString("N"));
        public readonly Profile Profile = new() { Protocol = "openvpn", OpenVpnConfig = "client\ndev tun\n", OpenVpnLegacyProviderRequired = true };
        public readonly List<RouteRow> Routes = [];
        public readonly List<string> Commands = [];
        public readonly OpenVpnService Service;
        public string Address = "10.77.0.2";
        public int FailInstallNumber, Installs;
        public bool FailVerification;
        public int Changes;
        private readonly string transcript;
        private sealed class NoAdapter : IDisposable { public void Dispose() { } }
        public Fixture(Func<OpenVpnLink,CancellationToken,Task<bool>>? probe = null,
            Func<OpenVpnLink,OpenVpnAddressState>? addressState = null,
            Func<TimeSpan,CancellationToken,Task>? addressDelay = null,
            TimeProvider? readinessTime = null, TimeSpan? readinessTimeout = null)
        {
            Directory.CreateDirectory(Root); transcript = Path.Combine(Root, "transcript.txt"); File.WriteAllText(transcript, "");
            var script = "$seen=0; while($true) { $rows=@(Get-Content -LiteralPath " + PhysicalNetwork.Literal(transcript) + "); while($seen -lt $rows.Length) { [Console]::WriteLine($rows[$seen]); $seen++ }; Start-Sleep -Milliseconds 20 }";
            Service = new("fixture-no-openvpn.exe", Root)
            {
                DataPathProbe = probe ?? ((_,_) => Task.FromResult(true)),
                CaptureAddressState = addressState ?? (_=>OpenVpnAddressState.Preferred),
                AddressReadinessDelay = addressDelay,
                TimeProvider = readinessTime ?? TimeProvider.System,
                AddressReadyTimeout = readinessTimeout ?? OpenVpnAddressReadiness.DefaultTimeout,
                DataPathProbeTimeout = TimeSpan.FromMilliseconds(150),
                CreateAdapterOverride = () => new NoAdapter(),
                StartProcessOverride = host => host.Start(ProcessHost.PowerShellPath, ["-NoProfile", "-NonInteractive", "-Command", script], log: false),
                CaptureLinkOverride = g => new("Fixture-OpenVPN", 4242, Address, g.Gateway, g.Dns, g.Routes),
                CaptureRouteTableOverride = () => { lock (Routes) return FailVerification && Installs > 0 ? [] : Routes.ToArray(); },
                PowerShellOverride = Run
            };
            Service.LinkChanged += () => Interlocked.Increment(ref Changes);
        }
        public Task<(int Code, string Output)> Run(string cmd, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (Routes)
            {
                Commands.Add(cmd);
                string prefix = Regex.Match(cmd, "-DestinationPrefix '([^']+)'").Groups[1].Value;
                string gateway = Regex.Match(cmd, "(?:-NextHop|NextHop -eq) '([^']+)'").Groups[1].Value;
                if (cmd.StartsWith("New-NetRoute"))
                {
                    if (++Installs == FailInstallNumber) return Task.FromResult((1, "injected failure"));
                    Routes.Add(new(4242, prefix, gateway, uint.Parse(Regex.Match(cmd, @"-RouteMetric (\d+)").Groups[1].Value), "NetMgmt", "Manual"));
                }
                else Routes.RemoveAll(r => r.InterfaceIndex == 4242 && r.DestinationPrefix == prefix && r.NextHop == gateway && (prefix != "0.0.0.0/0" || r.RouteMetric == 9999));
                return Task.FromResult((0, ""));
            }
        }
        public void Push(string prefix = "10.1.0.0", string dns = "10.1.0.53", string gateway = "10.1.0.1")
            => Append($"PUSH: Received control message: 'PUSH_REPLY,route {prefix} 255.255.0.0,route-gateway {gateway},dhcp-option DNS {dns}'", "Initialization Sequence Completed");
        public void Append(params string[] lines) => File.AppendAllLines(transcript, lines);
        public void PrepareNextProcessTranscript() { File.WriteAllText(transcript, ""); Push(); }
        public async Task Start()
        {
            Push(); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await Service.StartAsync(Profile, "", ct.Token);
        }
        public async Task Reconnect()
        {
            Append("SIGUSR1[soft,ping-restart] received, process restarting");
            await Until(() => Service.Link == null);
        }
        public static async Task Until(Func<bool> condition)
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!condition()) await Task.Delay(20, ct.Token);
        }
        public async ValueTask DisposeAsync()
        { try { await Service.StopAsync(); } finally { Service.Dispose(); Directory.Delete(Root, true); } }
    }
    [Fact] public async Task InProcessOpenVpnReconnectIsObservedWithoutProcessExit()
    {
        await using var f = new Fixture(); await f.Start(); int pid = f.Service.ProcessId; long gen = f.Service.Link!.Generation;
        await f.Reconnect(); Assert.True(f.Service.Reconnecting); f.Push("10.2.0.0");
        await Fixture.Until(() => f.Service.Link?.Generation > gen);
        Assert.Equal(pid, f.Service.ProcessId); Assert.True(f.Changes >= 3);
    }
    [Fact] public async Task InProcessReconnectWithChangedPushReplacesRoutes()
    {
        await using var f = new Fixture(); await f.Start(); await f.Reconnect(); f.Push("10.2.0.0");
        await Fixture.Until(() => f.Service.Link?.LearnedRoutes.Contains("10.2.0.0/16") == true);
        lock (f.Routes) { Assert.DoesNotContain(f.Routes, r => r.DestinationPrefix == "10.1.0.0/16"); Assert.Contains(f.Routes, r => r.DestinationPrefix == "10.2.0.0/16"); }
        var journal = await OpenVpnRouteJournal.ReadAsync(Path.Combine(f.Root, "openvpn-route.json"));
        Assert.Equal(f.Service.Link!.Generation, journal!.Generation); Assert.Equal(f.Profile.Id, journal.ProfileId);
    }
    [Fact] public async Task ReconnectReplacesCorporateDnsGeneration()
    {
        await using var f = new Fixture(); await f.Start(); await f.Reconnect(); f.Push(dns: "10.2.0.53");
        await Fixture.Until(() => f.Service.Link != null); Assert.Equal("10.2.0.53", f.Service.Link!.Dns);
    }
    [Fact] public async Task InProcessReconnectWithChangedAddressRefreshesBindOnly()
    {
        await using var f = new Fixture(); await f.Start(); var pid = f.Service.ProcessId; await f.Reconnect();
        f.Address = "10.2.0.2"; f.Push("10.2.0.0", "10.2.0.53", "10.2.0.1");
        await Fixture.Until(() => f.Service.Link != null);
        Assert.Equal("10.2.0.2", f.Service.Link!.Address); Assert.Equal("10.2.0.1", f.Service.Link.Gateway); Assert.Equal(pid, f.Service.ProcessId);
    }
    [Fact] public async Task ReconnectTransitionFailsClosedUntilNewLinkIsVerified()
    {
        await using var f = new Fixture(); await f.Start(); await f.Reconnect();
        Assert.Null(f.Service.Link); Assert.False(f.Service.IsRunning);
        await Fixture.Until(() => { lock (f.Routes) return f.Routes.Count == 0; });
        f.Append("PUSH: 'PUSH_REPLY,route 10.2.0.0 255.255.0.0'");
        await Task.Delay(100); Assert.Null(f.Service.Link);
    }
    [Fact] public async Task RepeatedReconnectDoesNotAccumulateGenerationRoutes()
    {
        await using var f = new Fixture(); await f.Start(); var pid = f.Service.ProcessId;
        for (var i = 2; i <= 5; i++)
        {
            await f.Reconnect(); f.Push($"10.{i}.0.0"); await Fixture.Until(() => f.Service.Link != null);
            Assert.Equal(new[] { $"10.{i}.0.0/16" }, f.Service.Link!.LearnedRoutes);
            lock (f.Routes) Assert.Equal(2, f.Routes.Count);
            Assert.Equal(pid, f.Service.ProcessId);
        }
    }
    [Fact] public async Task UnexpectedOpenVpnExitDoesNotLeaveOrphanOwnedRoutes()
    {
        await using var f = new Fixture(); await f.Start();
        using var process = Process.GetProcessById(f.Service.ProcessId); process.Kill(); await process.WaitForExitAsync();
        await Fixture.Until(() => { lock (f.Routes) return f.Routes.Count == 0; });
        Assert.Null(f.Service.Link); Assert.False(File.Exists(Path.Combine(f.Root, "openvpn-route.json")));
    }
    [Fact] public async Task PartialRouteInstallFailureRollsBackExactJournaledRoutes()
    {
        await using var f = new Fixture(); f.FailInstallNumber = 2;
        f.Append("PUSH: 'PUSH_REPLY,route 10.1.0.0 255.255.0.0,route 10.2.0.0 255.255.0.0,route-gateway 10.1.0.1,dhcp-option DNS 10.1.0.53'", "Initialization Sequence Completed");
        await Assert.ThrowsAsync<IOException>(() => f.Service.StartAsync(f.Profile, "", CancellationToken.None));
        lock (f.Routes) Assert.Empty(f.Routes);
        Assert.Null(f.Service.Link); Assert.False(File.Exists(Path.Combine(f.Root, "openvpn-route.json")));
    }
    [Fact] public async Task UserOffDuringReconnectCannotPublishLateGeneration()
    {
        await using var f = new Fixture(); await f.Start(); await f.Reconnect();
        await f.Service.StopAsync(); f.Push("10.2.0.0"); await Task.Delay(100);
        Assert.Null(f.Service.Link); Assert.Equal(0, f.Service.ProcessId); lock (f.Routes) Assert.Empty(f.Routes);
    }
    [Fact] public async Task VerificationFailureRollsBackInstalledRoutes()
    {
        await using var f = new Fixture(); f.FailVerification = true; f.Push();
        await Assert.ThrowsAsync<IOException>(() => f.Service.StartAsync(f.Profile, "", CancellationToken.None));
        lock (f.Routes) Assert.Empty(f.Routes); Assert.Null(f.Service.Link);
    }
    [Fact] public async Task ReconnectRouteRepairPreservesExactJournalOwnership()
    {
        await using var f = new Fixture(); await f.Start();
        lock (f.Routes) f.Routes.RemoveAll(r => r.DestinationPrefix == "10.1.0.0/16");
        await f.Service.EnsureRoutesAsync(CancellationToken.None);
        var journal = await OpenVpnRouteJournal.ReadAsync(Path.Combine(f.Root, "openvpn-route.json"));
        Assert.Equal(new[] { "10.1.0.0/16" }, journal!.Prefixes);
        await f.Service.StopAsync(); lock (f.Routes) Assert.Empty(f.Routes);
    }
}
