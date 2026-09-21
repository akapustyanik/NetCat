using System.IO;
using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;

namespace NetCat.UI;
internal static class LiveRulesSmoke
{
    public static async Task VerifyAsync(MainViewModel vm, string destination)
    {
        if(!App.IsSmoke) throw new InvalidOperationException("Only isolated smoke settings are allowed.");
        var profile=ProfileImporter.ParseLink("socks://192.0.2.1:1080#Smoke");
        await vm.UpdateSettingsAsync(next=>{next.Profiles=[profile];next.MainProfileId=profile.Id;next.Tun=false;next.SocksPort=OpenVpnService.FreePort();});
        try
        {
            await vm.Router.SetVpnAsync(vm.State,true);
            async Task Check(string expected, string? absent=null)
            {
                await vm.PendingRoutes.WaitAsync(TimeSpan.FromSeconds(15));
                if(!vm.Router.VpnRunning) throw new InvalidOperationException("Router stopped while applying rules.");
                var config=await File.ReadAllTextAsync(Path.Combine(vm.Store.Root,"runtime","router.json"));
                if(!config.Contains(expected) || absent!=null && config.Contains(absent)) throw new InvalidOperationException("Live rules were not applied: "+vm.Status);
                if(vm.Store.Load().Rules.Count!=vm.Rules.Count) throw new InvalidOperationException("Rules were not persisted.");
            }
            var first=new RoutingRule {Kind=RuleKind.Domain,Value="first.example",Target=RouteTarget.Block};
            vm.Rules.Add(first); await Check("first.example");
            vm.Rules[0]=new RoutingRule {Id=first.Id,Kind=RuleKind.Domain,Value="edited.example",Target=RouteTarget.Direct};
            await Check("edited.example","first.example");
            vm.Rules.Add(new RoutingRule {Kind=RuleKind.Domain,Value="second.example",Target=RouteTarget.Vpn});
            vm.Rules.Move(1,0); await Check("second.example");
            var config=JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(vm.Store.Root,"runtime","router.json")))!;
            var rules=config["route"]!["rules"]!.ToJsonString();
            if(rules.IndexOf("second.example",StringComparison.Ordinal)>rules.IndexOf("edited.example",StringComparison.Ordinal)) throw new InvalidOperationException("Rule order was not applied.");
            vm.Rules.RemoveAt(0); await Check("edited.example","second.example");
            vm.YouTubeIndex=0; await vm.PendingRoutes; vm.YouTubeIndex=1; await vm.PendingRoutes;
            config=JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(vm.Store.Root,"runtime","router.json")))!;
            var youtube=config["route"]!["rules"]!.AsArray().First(r=>r?["domain_suffix"]?.ToJsonString().Contains("youtube.com")==true);
            if(youtube?["outbound"]?.ToString()!="direct") throw new InvalidOperationException("Zapret scenario failed to return to direct.");
            vm.ModeIndex=1; await vm.PendingRoutes;
            vm.TelegramSocks=true; vm.TelegramVpnDefault=true; await vm.PendingRoutes;
            async Task CheckTelegram(string target)
            {
                var current=JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(vm.Store.Root,"runtime","router.json")))!;
                var rule=current["route"]!["rules"]!.AsArray().First(r=>r?["process_name"] is JsonArray names && names.Any(n=>n?.ToString()=="Telegram.exe"));
                if(rule?["outbound"]?.ToString()!=target || vm.Store.Load().TelegramSocks || vm.Store.Load().TelegramVpnDefault!=(target=="vpn"))
                    throw new InvalidOperationException("Telegram default did not apply/persist.");
            }
            await CheckTelegram("vpn");
            vm.TelegramVpnDefault=false; await vm.PendingRoutes; await CheckTelegram("direct");
            var missing=RoutingPreset.MissingStandardRules(vm.Rules); foreach(var rule in missing)vm.Rules.Add(rule);
            await Check("edited.example");
            await File.WriteAllTextAsync(Path.Combine(destination,"live-rules-check.txt"),"Real sing-box process, isolated SOCKS-only session (no TUN/system routes): add, edit, reorder, remove, save, VPN/Zapret round trip, Telegram VPN/direct checkbox with external SOCKS reset/persistence, and standard preset all applied automatically.");
        }
        finally
        {
            await vm.Router.StopAllAsync(); await vm.UpdateSettingsAsync(next=>{next.Profiles=[];next.MainProfileId=null;next.Rules=[];next.Tun=true;});
        }
    }
}
