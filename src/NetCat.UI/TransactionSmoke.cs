using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
namespace NetCat.UI;

internal static class TransactionSmoke
{
    private sealed class FaultStore(string root):SettingsStore(root)
    {
        public bool FailNext;
        public TaskCompletionSource? Entered,Continue;
        public override async Task SaveAsync(AppSettings settings)
        {
            if(Entered!=null) {var ready=Entered;var proceed=Continue!;Entered=null;ready.SetResult();await proceed.Task;}
            if(FailNext) {FailNext=false;throw new IOException("Injected save failure");}
            await base.SaveAsync(settings);
        }
    }
    private sealed class FakeZapret():ZapretService("unused","unused")
    {
        private bool running;
        public string? Fail;
        public override bool Running=>running;
        protected override Task StartInternal(AppSettings s,string file,CancellationToken ct)
        {
            running=false;ActiveStrategy=ActiveScenario="";
            if(Path.GetFileName(file)==Fail) {Fail=null;throw new IOException("Injected partial Zapret failure");}
            running=true;ActiveStrategy=Path.GetFileName(file);ActiveScenario=s.Scenario;return Task.CompletedTask;
        }
        public override Task StopAsync() {running=false;ActiveStrategy=ActiveScenario="";return Task.CompletedTask;}
    }
    private static void Check(bool value,string message) {if(!value)throw new InvalidOperationException(message);}
    public static async Task VerifyAsync(string destination,string bin)
    {
        if(!App.IsSmoke)throw new InvalidOperationException("Isolated smoke only.");
        var root=Path.Combine(Path.GetTempPath(),"NetCat-Transaction-Smoke-"+Guid.NewGuid().ToString("N"));var store=new FaultStore(root);
        var a=ProfileImporter.ParseLink("socks://192.0.2.1:1080#A");var b=ProfileImporter.ParseLink("socks://192.0.2.2:1080#B");var c=ProfileImporter.ParseLink("socks://192.0.2.3:1080#C");var d=ProfileImporter.ParseLink("socks://192.0.2.4:1080#D");
        var sub=new Subscription {Name="Smoke subscription"};a.SubscriptionId=sub.Id;
        var settings=new AppSettings {Profiles=[a,b,c,d],Subscriptions=[sub],MainProfileId=a.Id,Tun=false,AutoSwitch=true,SocksPort=OpenVpnService.FreePort(),ZapretStrategy="A.bat"};
        await store.SaveAsync(settings);bool rejectB=true,failStarts=false;TaskCompletionSource? preflightEntered=null,preflightContinue=null;
        using var router=new RouterService(bin,Path.Combine(root,"runtime"))
        {
            StartProcessOverride=(host,exe,args)=>{if(failStarts)throw new IOException("Injected core failure");host.Start(exe,args);},
            PreflightOverride=async (p,_,ct)=>
            {
                if(preflightEntered!=null) {var ready=preflightEntered;preflightEntered=null;ready.SetResult();await preflightContinue!.Task.WaitAsync(ct);}
                return new(!(rejectB && p.Id==b.Id),1,"Injected rejected profile");
            }
        };
        using var zapret=new FakeZapret();using var vm=new MainViewModel(store,settings,router,zapret);
        await vm.SaveAsync();await vm.SetVpnEnabledAsync(true,CancellationToken.None);
        vm.MainProfile=vm.VpnProfiles.Single(p=>p.Id==b.Id);
        vm.State.BaseColor="#171D29";await vm.SaveAsync();await vm.PendingSelection;
        vm.AssertCommittedInvariant();Check(router.ActiveProfileId==a.Id,"Pending selection leaked through unrelated Save");
        vm.MainProfile=vm.VpnProfiles.Single(p=>p.Id==b.Id);vm.MainProfile=vm.VpnProfiles.Single(p=>p.Id==c.Id);vm.MainProfile=vm.VpnProfiles.Single(p=>p.Id==d.Id);await vm.PendingSelection;
        vm.AssertCommittedInvariant();Check(router.ActiveProfileId==d.Id,"Latest selection did not win");
        await vm.CommitFailoverAsync(d.Id,a.Id,router.SessionRevision,CancellationToken.None);
        store.FailNext=true;
        try {await vm.CommitFailoverAsync(a.Id,b.Id,router.SessionRevision,CancellationToken.None);throw new Exception("Save failure ignored");}catch(IOException){}
        vm.AssertCommittedInvariant();Check(router.ActiveProfileId==a.Id,"Failed failover did not restore A");
        var additional=ProfileImporter.ParseLink("socks://192.0.2.8:1080#New subscription profile");
        await vm.UpdateSubscriptionAsync(sub.Id,[a,additional],vm.SettingsRevision,CancellationToken.None);var count=vm.State.Profiles.Count;
        vm.State.PhysicalInterface="nonexistent-smoke-adapter";await vm.PendingRoutes;
        vm.AssertCommittedInvariant();Check(vm.State.Profiles.Count==count && vm.State.Profiles.Any(p=>p.Host==additional.Host),"Routing rollback lost subscription commit");
        await vm.ApplyZapretAsync(true,"A.bat",CancellationToken.None);zapret.Fail="B.bat";
        try {await vm.ApplyZapretAsync(true,"B.bat",CancellationToken.None);throw new Exception("Partial Zapret failure ignored");}catch(IOException){}
        vm.AssertCommittedInvariant();Check(zapret.Running && zapret.ActiveStrategy=="A.bat" && router.VpnRunning && router.ZapretAvailable,"Zapret rollback did not restore coupled runtime");
        await vm.ApplyZapretAsync(false,null,CancellationToken.None);
        await vm.DeleteProfileAsync(c.Id);Check(!vm.State.Profiles.Any(p=>p.Id==c.Id),"Deletion failed");
        // Hold a preflight, queue unrelated changes and a stale subscription refresh, then release.
        rejectB=false;preflightEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);preflightContinue=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered=preflightEntered;var revision=vm.SettingsRevision;
        vm.MainProfile=vm.VpnProfiles.Single(p=>p.Id==b.Id);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.State.AccentColor="#C784E2";var save=vm.SaveAsync();
        var stale=vm.UpdateSubscriptionAsync(sub.Id,[a,additional],revision,CancellationToken.None);
        vm.State.DirectDns="8.8.8.8";var routes=vm.PendingRoutes;
        preflightContinue.SetResult();await vm.PendingSelection;await save;await routes;
        try {await stale;throw new Exception("Stale subscription revision committed");}catch(OperationCanceledException){}
        vm.AssertCommittedInvariant();Check(router.ActiveProfileId==b.Id && vm.State.AccentColor=="#C784E2" && vm.State.DirectDns=="8.8.8.8","Concurrent changes lost newest revision");
        // Force successful B->A apply, then disk failure, then rollback core failure.
        var readySave=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var continueSave=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Entered=readySave;store.Continue=continueSave;store.FailNext=true;
        var failover=vm.CommitFailoverAsync(b.Id,a.Id,router.SessionRevision,CancellationToken.None);
        await readySave.Task.WaitAsync(TimeSpan.FromSeconds(8));failStarts=true;continueSave.SetResult();
        try {await failover;throw new Exception("Double failure ignored");}catch(AggregateException){}
        vm.AssertCommittedInvariant();Check(!router.Running && !router.VpnRequested && router.ActiveProfileId==null && router.ListenPort==0 && router.LatencyPort==0 && router.HealthSourcePort==0 && !router.TunActive,"Fail-safe stop left runtime fields");
        failStarts=false;await vm.DeleteProfileAsync(b.Id);vm.AssertCommittedInvariant();Check(vm.State.MainProfileId!=b.Id,"Dangling selected VPN ID");
        var ovpn=new Profile {Protocol="openvpn",Core="OpenVPN",Name="Office"};await vm.ImportProfilesAsync([ovpn]);await vm.DeleteProfileAsync(ovpn.Id);vm.AssertCommittedInvariant();Check(vm.State.OpenVpnProfileId==null,"Dangling OpenVPN ID");
        await File.WriteAllTextAsync(Path.Combine(destination,"transaction-check.txt"),"PASS: pending invalid profile + unrelated theme Save; latest selection wins; subscription survives failed routes; failover disk failure restores old profile; partial Zapret start restores old strategy and router; queued Save/routing/selection and stale subscription use revisions; failover rollback failure clears runtime; inactive selected VPN/OpenVPN deletion repairs IDs. Native router is loopback only; Zapret is a deterministic fake, no WinDivert/TUN or real user network changes.");
    }
}
