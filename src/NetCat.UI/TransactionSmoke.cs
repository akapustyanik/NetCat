using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using System.Windows.Data;
using System.Windows.Threading;

namespace NetCat.UI;

internal static class TransactionSmoke
{
    private sealed class FaultStore(string root) : SettingsStore(root)
    {
        public bool FailNext;
        public override Task SaveAsync(AppSettings settings)
        {
            if (FailNext) { FailNext = false; throw new IOException("Injected disk failure"); }
            return base.SaveAsync(settings);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static async Task VerifyAsync(string destination, string bin)
    {
        if (!App.IsSmoke) throw new InvalidOperationException("Isolated smoke only.");
        var root = Path.Combine(Path.GetTempPath(), "NetCat-Transaction-Smoke-" + Guid.NewGuid().ToString("N"));
        var store = new FaultStore(root);
        var a = ProfileImporter.ParseLink("socks://192.0.2.1:1080#A");
        var b = ProfileImporter.ParseLink("socks://192.0.2.2:1080#B");
        var c = ProfileImporter.ParseLink("socks://192.0.2.3:1080#C");
        var settings = new AppSettings { Profiles = [a,b,c], MainProfileId = a.Id, Tun = false, SocksPort = OpenVpnService.FreePort() };
        await store.SaveAsync(settings);
        bool rejectB = true;
        using var router = new RouterService(bin, Path.Combine(root, "runtime"))
        { PreflightOverride = (profile, _, _) => Task.FromResult(new DelayResult(!(rejectB && profile.Id == b.Id), 1, "Injected preflight failure")) };
        using var vm = new MainViewModel(store, settings, router);
        var view = CollectionViewSource.GetDefaultView(vm.Profiles);
        var projectionThread = Environment.CurrentManagedThreadId;
        vm.Profiles.CollectionChanged += (_, _) => Check(Environment.CurrentManagedThreadId == projectionThread, "WPF collection changed off dispatcher");
        await vm.SetVpnEnabledAsync(true, CancellationToken.None);

        vm.UserSelectedVpnProfile(b.Id); await vm.PendingSelection; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(router.ActiveProfileId == a.Id && vm.DesiredState.SelectedVpnProfileId == a.Id, "Rejected preflight changed runtime or intent");

        rejectB = false;
        vm.UserSelectedVpnProfile(b.Id); vm.UserSelectedVpnProfile(c.Id);
        await vm.PendingSelection; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(router.ActiveProfileId == c.Id && store.Load().MainProfileId == c.Id, "Latest selection not applied");
        vm.AssertCommittedInvariant();

        store.FailNext = true;
        vm.UserSelectedVpnProfile(a.Id); await vm.PendingSelection; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(router.ActiveProfileId == c.Id && vm.DesiredState.SelectedVpnProfileId == c.Id && store.Load().MainProfileId == c.Id, "Disk failure changed active selection");

        vm.ConfigRepository.ConfigurationChanged += _ => throw new IOException("Injected projection failure");
        vm.UserSelectedVpnProfile(a.Id); await vm.PendingSelection; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(router.ActiveProfileId == a.Id && store.Load().MainProfileId == a.Id, "Projection failure prevented convergence");
        vm.AssertCommittedInvariant();

        await Task.Run(() => vm.ConfigRepository.UpdateSettingsAsync(next => { next.BaseColor = "#171D29"; return next; }));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(vm.State.BaseColor == "#171D29", "Background config event not projected");
        await vm.StopComponentsAsync();
        GC.KeepAlive(view);
        await File.WriteAllTextAsync(Path.Combine(destination, "transaction-check.txt"),
            "PASS: real WPF CollectionView/Dispatcher; rejected preflight preserves A; latest B/C selection wins; disk failure preserves active+desired+persisted C; throwing projection subscriber does not prevent A convergence; background settings event projects on UI. Native core uses SOCKS only, no TUN/OS route changes.");
    }
}
