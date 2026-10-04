using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using NetCat.UI;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate30AdapterTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "NetCat-C30-Adapter-" + Guid.NewGuid().ToString("N"));

    public Candidate30AdapterTests()
    {
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDir))
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static class DedicatedSta
    {
        private static readonly TaskCompletionSource<System.Windows.Threading.Dispatcher> DispatcherTcs = new();

        static DedicatedSta()
        {
            var thread = new Thread(() =>
            {
                try
                {
                    if (Application.Current == null)
                    {
                        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        { Source = new Uri("pack://application:,,,/NetCat;component/Theme.xaml", UriKind.Absolute) });
                    }
                    DispatcherTcs.SetResult(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                    System.Windows.Threading.Dispatcher.Run();
                }
                catch (Exception error) { DispatcherTcs.TrySetException(error); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        public static System.Windows.Threading.Dispatcher Dispatcher => DispatcherTcs.Task.GetAwaiter().GetResult();
    }

    private static void RunOnStaAsync(Func<Task> action)
    {
        var dispatcher = (Application.Current is { } app && !app.Dispatcher.HasShutdownStarted && !app.Dispatcher.HasShutdownFinished)
            ? app.Dispatcher
            : DedicatedSta.Dispatcher;

        dispatcher.InvokeAsync(async () =>
        {
            await action();
        }).Task.Unwrap().GetAwaiter().GetResult();
    }

    [Fact]
    public void Candidate31StaFixtureLoadsThemeBeforeAnyWindowTest()
    {
        RunOnStaAsync(() =>
        {
            Assert.IsType<Style>(Application.Current.FindResource("Heading"));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void RuntimeResolutionFallsBackWhenPreferredAdapterUnavailable()
    {
        // RUNTIME NETWORK RECOVERY SEMANTICS:
        // When a configured adapter disappears or is not found, Capture must NOT throw.
        // It must safely fall back to the active / best-metric available physical interface.
        var fallback = PhysicalNetwork.Capture("completely-nonexistent-adapter-99999");
        Assert.NotNull(fallback);
        Assert.False(string.IsNullOrWhiteSpace(fallback.Name));
        Assert.False(string.IsNullOrWhiteSpace(fallback.Address));
        Assert.NotEqual("completely-nonexistent-adapter-99999", fallback.Name, StringComparer.OrdinalIgnoreCase);

        var tryFallback = PhysicalNetwork.TryCapture("completely-nonexistent-adapter-99999");
        Assert.NotNull(tryFallback);
        Assert.Equal(fallback.Name, tryFallback.Name);
    }

    [Fact]
    public void RuntimeCaptureStillFallsBackWhenPreferredAdapterDisappears()
    {
        // SystemPhysicalNetworkProvider and coordinator callers rely on ResolveCurrentBinding falling back
        var provider = SystemPhysicalNetworkProvider.Instance;
        var snapshot = provider.ResolveCurrentBinding("phantom-nic-42");
        Assert.NotNull(snapshot);
        Assert.NotEqual("phantom-nic-42", snapshot.Name, StringComparer.OrdinalIgnoreCase);
        Assert.True(provider.IsUsable(snapshot));
    }

    [Fact]
    public void CaptureExactRequiresExactMatch()
    {
        // USER SETTINGS VALIDATION SEMANTICS:
        // CaptureExact must fail closed if the exact named adapter is not found.
        var ex = Assert.Throws<InvalidOperationException>(() => PhysicalNetwork.CaptureExact("unavailable-test-adapter"));
        Assert.Contains("unavailable-test-adapter", ex.Message);

        Assert.Throws<ArgumentException>(() => PhysicalNetwork.CaptureExact(""));
        Assert.Throws<ArgumentException>(() => PhysicalNetwork.CaptureExact("   "));

        // When given an existing physical adapter, CaptureExact must succeed and match exactly.
        var real = PhysicalNetwork.Capture("");
        var exact = PhysicalNetwork.CaptureExact(real.Name);
        Assert.NotNull(exact);
        Assert.Equal(real.Name, exact.Name, ignoreCase: true);
        Assert.Equal(real.Index, exact.Index);
    }

    [Fact]
    public void UnavailableExplicitAdapterIsRejected()
    {
        RunOnStaAsync(async () =>
        {
            var real = PhysicalNetwork.Capture("");
            var store = new SettingsStore(tempDir);
            var p = ProfileImporter.ParseLink("socks://192.0.2.1:1080");
            var settings = new AppSettings
            {
                Tun = false,
                Profiles = [p],
                MainProfileId = p.Id,
                SocksPort = OpenVpnService.FreePort(),
                PhysicalInterface = real.Name
            };
            await store.SaveAsync(settings);

            using var router = new RouterService(RoutingTests.ModuleRoot, Path.Combine(tempDir, "runtime"));
            using var vm = new MainViewModel(store, settings, router);

            var adapter = new ComboBox
            {
                DataContext = vm,
                ItemsSource = new[] { real.Name, "unavailable-test-adapter" }
            };
            adapter.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
                new Binding("State.PhysicalInterface") { Mode = BindingMode.TwoWay });

            Assert.Equal(real.Name, vm.State.PhysicalInterface);
            Assert.Equal(real.Name, adapter.SelectedItem?.ToString());

            // User explicitly selects the unavailable adapter
            adapter.SelectedItem = "unavailable-test-adapter";
            await vm.PendingRoutes;

            // Strict validation must reject the commit and roll back both VM state and ComboBox
            Assert.Equal(real.Name, vm.State.PhysicalInterface);
            Assert.Equal(real.Name, vm.ConfigRepository.CurrentSettings.PhysicalInterface);
            Assert.Equal(real.Name, adapter.SelectedItem?.ToString());
        });
    }

    [Fact]
    public void ExplicitAdapterCommitRequiresExactMatch()
    {
        RunOnStaAsync(async () =>
        {
            var real = PhysicalNetwork.Capture("");
            var store = new SettingsStore(tempDir);
            var p = ProfileImporter.ParseLink("socks://192.0.2.1:1080");
            var settings = new AppSettings
            {
                Tun = false,
                Profiles = [p],
                MainProfileId = p.Id,
                SocksPort = OpenVpnService.FreePort(),
                PhysicalInterface = real.Name
            };
            await store.SaveAsync(settings);

            using var router = new RouterService(RoutingTests.ModuleRoot, Path.Combine(tempDir, "runtime"));
            using var vm = new MainViewModel(store, settings, router);

            // Attempting to commit an unavailable adapter via UI State must rollback
            vm.State.PhysicalInterface = "phantom-test-nic";
            vm.ScheduleRoutesApply();
            await vm.PendingRoutes;

            Assert.Equal(real.Name, vm.State.PhysicalInterface);
            Assert.Equal(real.Name, vm.ConfigRepository.CurrentSettings.PhysicalInterface);
            Assert.Equal(real.Name, store.Load().PhysicalInterface);
        });
    }

    [Fact]
    public void InvalidAdapterCommitDoesNotStopRunningVpn()
    {
        RunOnStaAsync(async () =>
        {
            var real = PhysicalNetwork.Capture("");
            var store = new SettingsStore(tempDir);
            var p = ProfileImporter.ParseLink("socks://192.0.2.1:1080");
            var settings = new AppSettings
            {
                Tun = false,
                Profiles = [p],
                MainProfileId = p.Id,
                SocksPort = OpenVpnService.FreePort(),
                PhysicalInterface = real.Name
            };
            await store.SaveAsync(settings);

            using var router = new RouterService(RoutingTests.ModuleRoot, Path.Combine(tempDir, "runtime"))
            {
                StartProcessOverride = (host, exe, args) =>
                {
                    var config = JsonNode.Parse(File.ReadAllText(args[2]))!;
                    var inbounds = config["inbounds"]!.AsArray();
                    foreach (var tun in inbounds.Where(x => x?["type"]?.ToString() == "tun").ToArray()) inbounds.Remove(tun);
                    var safe = args[2] + ".socks-only";
                    File.WriteAllText(safe, config.ToJsonString());
                    host.Start(exe, ["run", "-c", safe]);
                }
            };
            using var vm = new MainViewModel(store, settings, router);
            vm.UpdateDesiredState(d => d with { MainVpnEnabled = true, SelectedVpnProfileId = p.Id, TunEnabled = false });

            // Start VPN
            await router.SetVpnAsync(settings, true);
            Assert.True(router.VpnRunning);

            var adapter = new ComboBox
            {
                DataContext = vm,
                ItemsSource = new[] { real.Name, "unavailable-test-adapter" }
            };
            adapter.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
                new Binding("State.PhysicalInterface") { Mode = BindingMode.TwoWay });

            // Select unavailable adapter while VPN is running
            adapter.SelectedItem = "unavailable-test-adapter";
            await vm.PendingRoutes;

            // VPN must remain running and state rolled back
            Assert.True(router.VpnRunning);
            Assert.Equal(real.Name, vm.State.PhysicalInterface);
            Assert.Equal(real.Name, vm.ConfigRepository.CurrentSettings.PhysicalInterface);
            Assert.Equal(real.Name, adapter.SelectedItem?.ToString());
        });
    }
}
