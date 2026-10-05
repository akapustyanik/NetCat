using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NetCat.Core;
using NetCat.Network;

namespace NetCat.Tests;

internal static class Candidate26NativeRoutes
{
    private sealed class Table
    {
        public readonly List<RouteRow> Rows=[];
        public OpenVpnLink? Current;
        public IReadOnlyList<RouteRow> Capture(){lock(Rows)return Rows.ToArray();}
        public Task<(int Code,string Output)> Run(string command,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var prefix=Regex.Match(command,"(?:-DestinationPrefix|DestinationPrefix -eq) '([^']+)'").Groups[1].Value;
            var gateway=Regex.Match(command,"(?:-NextHop|NextHop -eq) '([^']+)'").Groups[1].Value;
            int index=int.Parse(Regex.Match(command,@"(?:-InterfaceIndex|InterfaceIndex -eq) (\d+)").Groups[1].Value);
            uint metric=uint.Parse(Regex.Match(command,@"(?:-RouteMetric|RouteMetric -eq) (\d+)").Groups[1].Value);
            lock(Rows)
            {
                if(command.StartsWith("New-NetRoute"))Rows.Add(new(index,prefix,gateway,metric,"NetMgmt","Manual"));
                else Rows.RemoveAll(r=>r.InterfaceIndex==index&&r.DestinationPrefix==prefix&&r.NextHop==gateway&&r.RouteMetric==metric);
            }
            return Task.FromResult((0,""));
        }
    }
    private static readonly ConditionalWeakTable<OpenVpnDestinationLeases,Table> Tables=new();
    public static async Task Prepare(OpenVpnDestinationLeases leases,string journal,OpenVpnLink? link,
        Func<IReadOnlyList<RouteRow>> capture,Func<string,CancellationToken,Task<(int Code,string Output)>> run)
    {
        var table=Tables.GetOrCreateValue(leases);
        if(table.Current is {} prior && link!=null && prior.ProfileId==link.ProfileId && link.Generation<prior.Generation)return;
        if(table.Current is {} old && link!=null && old.ProfileId==link.ProfileId && old.Generation==link.Generation && old.Index==link.Index && old.Address==link.Address && old.Gateway==link.Gateway && old.Dns==link.Dns && old.LearnedRoutes.SequenceEqual(link.LearnedRoutes))return;
        leases.Invalidate();await OpenVpnRouteJournal.CleanupAsync(journal,run);table.Current=null;
        if(link==null)return;
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        await OpenVpnRouteJournal.InstallAsync(journal,link,capture,run,default);leases.Activate(link,()=>true);table.Current=link;
    }
    public static async Task Prepare(RouterService router,string runtime,OpenVpnLink? link)
    {
        var leases=router.OpenVpn.DestinationLeases;var table=Tables.GetOrCreateValue(leases);
        router.OpenVpn.CaptureRouteTableOverride=table.Capture;router.OpenVpn.PowerShellOverride=table.Run;
        await Prepare(leases,Path.Combine(runtime,"openvpn","openvpn-route.json"),link,table.Capture,table.Run);
    }
}
