using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;

namespace NetCat.UI;
internal static class StateSmoke
{
    private sealed class Store(string root):SettingsStore(root)
    {
        public bool FailNext {get;set;}
        public override Task SaveAsync(AppSettings settings) {if(FailNext){FailNext=false;throw new IOException("Injected disk failure");}return base.SaveAsync(settings);}
    }
    public static async Task VerifyAsync(string destination,string bin)
    {
        if(!App.IsSmoke)throw new InvalidOperationException("Isolated smoke only.");
        var root=Path.Combine(Path.GetTempPath(),"NetCat-State-Smoke-"+Guid.NewGuid().ToString("N")); var store=new Store(root);
        var physical=PhysicalNetwork.Capture("");var p=ProfileImporter.ParseLink("socks://192.0.2.1:1080");
        var settings=new AppSettings {Tun=true,Profiles=[p],MainProfileId=p.Id,SocksPort=OpenVpnService.FreePort(),PhysicalInterface=physical.Name};
        await store.SaveAsync(settings);
        using var router=new RouterService(bin,Path.Combine(root,"runtime"))
        {
            // Preserve the generated desired config; launch only its loopback inbounds.
            // The user's real TUN/OS routes must never be touched by this smoke test.
            StartProcessOverride=(host,exe,args)=>
            {
                var config=JsonNode.Parse(File.ReadAllText(args[2]))!;var inbounds=config["inbounds"]!.AsArray();
                foreach(var tun in inbounds.Where(x=>x?["type"]?.ToString()=="tun").ToArray())inbounds.Remove(tun);
                var safe=args[2]+".socks-only";File.WriteAllText(safe,config.ToJsonString());host.Start(exe,["run","-c",safe]);
            }
        };
        using var vm=new MainViewModel(store,settings,router);
        var check=new CheckBox{DataContext=vm};check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,new Binding("State.Tun"){Mode=BindingMode.TwoWay});
        var adapter=new ComboBox{DataContext=vm,ItemsSource=new[]{physical.Name,"unavailable-test-adapter"}};
        adapter.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,new Binding("State.PhysicalInterface"){Mode=BindingMode.TwoWay});
        await router.SetVpnAsync(settings,true);
        foreach(var tun in new[]{false,true})
        {
            check.IsChecked=tun;await vm.PendingRoutes;
            var config=JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root,"runtime","router.json")))!;
            if(config["inbounds"]!.AsArray().Any(x=>x?["type"]?.ToString()=="tun")!=tun || router.TunActive!=tun || store.Load().Tun!=tun)throw new Exception("TUN checkbox did not apply/persist.");
        }
        adapter.SelectedItem="unavailable-test-adapter";await vm.PendingRoutes;
        if(vm.State.PhysicalInterface!=physical.Name || adapter.SelectedItem?.ToString()!=physical.Name || !router.VpnRunning)throw new Exception("Adapter rollback did not update WPF.");
        store.FailNext=true;check.IsChecked=false;await vm.PendingRoutes;
        if(check.IsChecked!=true || !vm.State.Tun || !store.Load().Tun || !router.TunActive)throw new Exception("Save failure did not roll runtime/UI back.");
        var invalid=JsonSettings.Clone(vm.State.Profiles[0]);invalid.Host="invalid host!";
        try{await vm.EditProfileAsync(invalid,CancellationToken.None);throw new Exception("Invalid profile accepted");}catch(FormatException){}
        if(vm.State.Profiles[0].Host!=p.Host || store.Load().Profiles[0].Host!=p.Host || !router.VpnRunning)throw new Exception("Failed edit changed committed state.");
        var valid=JsonSettings.Clone(vm.State.Profiles.Single(x=>x.Id==p.Id));valid.Name="Retry saved by ID";
        await vm.EditProfileAsync(valid,CancellationToken.None);
        if(store.Load().Profiles[0].Name!=valid.Name)throw new Exception("Retry failed after invalid edit");
        await File.WriteAllTextAsync(Path.Combine(destination,"state-check.txt"),"WPF TUN true/false/true generates matching config and state, persists; invalid adapter and save failure roll back UI/runtime; invalid edit preserves profile and retries by ID. Native cores use loopback only: real TUN/OS routes are deliberately not created.");
    }
}
