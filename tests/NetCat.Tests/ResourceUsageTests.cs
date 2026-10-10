using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using NetCat.Core;
using NetCat.Network;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

public sealed partial class Candidate32UiAuditTests
{
    private sealed class ProbeHandler : HttpMessageHandler
    {
        public bool Released { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
        protected override void Dispose(bool disposing) { Released = true; base.Dispose(disposing); }
    }

    [Fact]
    public async Task ResourceAuditTunnelProbeHasOneOwnerAndReleasesHttpPool()
    {
        await Sta.Run(() =>
        {
            using var vm = new MainViewModel(new SettingsStore(root), new AppSettings());
            var probe = Assert.IsType<SystemTunnelHealth>(vm.HealthMonitor.CheckTunnelHealthFunc!.Target);
            var handler = new ProbeHandler();
            typeof(SystemTunnelHealth).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(probe, new HttpClient(handler));
            var original = vm.HealthMonitor.CheckTunnelHealthFunc;
            vm.NotifyState(); vm.PollRuntimeState();
            Assert.Same(original, vm.HealthMonitor.CheckTunnelHealthFunc);
            vm.Dispose(); vm.Dispose();
            Assert.True(handler.Released);
            Assert.Null(typeof(SystemTunnelHealth).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(probe));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ResourceAuditTrafficTicksDoNotDecodeAllProfilesAndObserveCommittedChanges()
    {
        await Sta.Run(async () =>
        {
            var settings = new AppSettings { PhysicalInterface = "synthetic-interface-a" };
            settings.Profiles = Enumerable.Range(0, 1000).Select(i => new Profile { Name = "Synthetic " + i, Host = "example.invalid" }).ToList();
            using var vm = new MainViewModel(new SettingsStore(root), settings);
            var read = (Func<string>)typeof(MainViewModel).GetMethod("GetTrafficPhysicalInterface", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate(typeof(Func<string>), vm);
            Assert.Equal(settings.PhysicalInterface, read());
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++) Assert.Equal(settings.PhysicalInterface, read());
            var cachedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10; i++) _ = vm.ConfigRepository.CurrentSettings.PhysicalInterface;
            var fullBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (Environment.GetEnvironmentVariable("NETCAT_RESOURCE_METRICS") is { } output)
                File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(new { Profiles = 1000, CachedReads = 1000, CachedBytes = cachedBytes, FullReads = 10, FullBytes = fullBytes }));
            Assert.True(cachedBytes < 64 * 1024, $"Cached sampler allocated {cachedBytes} bytes.");
            Assert.True(fullBytes > 100 * cachedBytes, $"Full settings: {fullBytes}; cached: {cachedBytes}.");
            await vm.ConfigRepository.UpdateSettingsAsync(next => { next.PhysicalInterface = "synthetic-interface-b"; return next; });
            Assert.Equal("synthetic-interface-b", read());
            var copy = vm.ConfigRepository.CurrentSettings;
            copy.PhysicalInterface = "uncommitted-draft";
            Assert.Equal("synthetic-interface-b", read());
        });
    }
}

public sealed class ResourceUsageTests
{
    [Fact]
    public async Task DisposedTunnelHealthCannotRecreateItsHttpPool()
    {
        using var health = new SystemTunnelHealth();
        health.Dispose(); health.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => health.CheckAsync(12345, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.5)]
    public void TrafficSamplerRejectsIntervalsThatCouldSpinOrFault(double milliseconds)
    {
        using var monitor = new TrafficMonitorService(() => null);
        try { Assert.Throws<ArgumentOutOfRangeException>(() => monitor.StartSampling(TimeSpan.FromMilliseconds(milliseconds))); }
        finally { monitor.StopSampling(); }
    }

    [Fact]
    public void DisposedTrafficSamplerCannotCreateNewBackgroundWork()
    {
        using var monitor = new TrafficMonitorService(() => null);
        monitor.Dispose();
        Assert.Throws<ObjectDisposedException>(() => monitor.StartSampling());
        monitor.StopSampling();
    }

    [Fact]
    public async Task RestartingTrafficSamplerWaitsForPreviousCallback()
    {
        using var monitor = new TrafficMonitorService(() => null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        monitor.SampleUpdated += _ =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); }
            else restarted.TrySetResult();
        };
        try
        {
            monitor.StartSampling(TimeSpan.FromMilliseconds(5));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            monitor.StopSampling(); monitor.StartSampling(TimeSpan.FromMilliseconds(5));
            await Task.Delay(100);
            Assert.Equal(1, Volatile.Read(ref calls));
            release.TrySetResult();
            await restarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.TrySetResult(); monitor.StopSampling(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RetiredActivation(OpenVpnDestinationGateway gateway)
    {
        gateway.Activate();
        var source = (CancellationTokenSource)typeof(OpenVpnDestinationGateway)
            .GetField("activation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gateway)!;
        var reference = new WeakReference(source);
        gateway.Invalidate();
        Assert.True(source.IsCancellationRequested);
        return reference;
    }

    [Fact]
    public void RetiredOpenVpnActivationsDoNotRemainRootedByGatewayLifetime()
    {
        var leases = new OpenVpnDestinationLeases(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"),
            () => [], (_, _) => Task.FromResult((0, "")));
        using var gateway = new OpenVpnDestinationGateway(leases, () => 0);
        var references = Enumerable.Range(0, 64).Select(_ => RetiredActivation(gateway)).ToArray();
        for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.DoesNotContain(references, reference => reference.IsAlive);
        GC.KeepAlive(gateway);
    }

    [Fact]
    public void ReplacingOpenVpnActivationCancelsAndUnlinksPreviousSource()
    {
        var leases = new OpenVpnDestinationLeases(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"),
            () => [], (_, _) => Task.FromResult((0, "")));
        using var gateway = new OpenVpnDestinationGateway(leases, () => 0);
        gateway.Activate();
        var source = (CancellationTokenSource)typeof(OpenVpnDestinationGateway).GetField("activation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gateway)!;
        var token = source.Token;
        gateway.Activate();
        Assert.True(token.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => source.Token);
        gateway.Dispose();
        Assert.Throws<ObjectDisposedException>(gateway.Activate);
        gateway.Invalidate();
    }
}
