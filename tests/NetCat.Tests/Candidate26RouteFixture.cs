using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using NetCat.Core;
using NetCat.Network;

namespace NetCat.Tests;

// Fake only the privileged OS route-table boundary. TCP/UDP, auth, sidecar,
// DNS parsing, generation handling and journal IO use their production paths.
internal sealed class Candidate26RouteFixture : IAsyncDisposable
{
    public string Root {get;}=Path.Combine(Path.GetTempPath(),"NetCat-C26-Routes-"+Guid.NewGuid().ToString("N"));
    public string Journal=>Path.Combine(Root,"openvpn-route.json");
    public List<RouteRow> Rows {get;}=[];
    public List<string> Commands {get;}=[];
    public OpenVpnDestinationLeases Leases {get;}
    public OpenVpnLink Link {get;private set;}
    public Func<string,CancellationToken,Task>? BeforeCreate;
    public bool FailCreate;
    public bool FailRemove;
    public Candidate26RouteFixture()
    {
        Directory.CreateDirectory(Root);
        var nic=NetworkInterface.GetAllNetworkInterfaces().First(n=>n.NetworkInterfaceType==NetworkInterfaceType.Loopback);
        Link=new(nic.Name,nic.GetIPProperties().GetIPv4Properties()!.Index,"127.0.0.1","127.0.0.1","127.0.0.4",["127.0.0.2/32"]){ProfileId=Guid.NewGuid(),Generation=1};
        Leases=new(Journal,Capture,Run);
    }
    public IReadOnlyList<RouteRow> Capture(){lock(Rows)return Rows.ToArray();}
    public async Task<(int Code,string Output)> Run(string command,CancellationToken ct)
    {
        lock(Commands)Commands.Add(command);
        if(command.StartsWith("New-NetRoute")&&BeforeCreate!=null)await BeforeCreate(command,ct);
        ct.ThrowIfCancellationRequested();
        var prefix=Regex.Match(command,"(?:-DestinationPrefix|DestinationPrefix -eq) '([^']+)'").Groups[1].Value;
        var gateway=Regex.Match(command,"(?:-NextHop|NextHop -eq) '([^']+)'").Groups[1].Value;
        int index=int.Parse(Regex.Match(command,@"(?:-InterfaceIndex|InterfaceIndex -eq) (\d+)").Groups[1].Value);
        uint metric=uint.Parse(Regex.Match(command,@"(?:-RouteMetric|RouteMetric -eq) (\d+)").Groups[1].Value);
        lock(Rows)
        {
            if(command.StartsWith("New-NetRoute"))
            {
                if(FailCreate || Rows.Any(r=>r.InterfaceIndex==index&&r.DestinationPrefix==prefix&&r.NextHop==gateway))return(1,"controlled create failure");
                Rows.Add(new(index,prefix,gateway,metric,"NetMgmt","Manual"));
            }
            else {if(FailRemove)return(1,"controlled remove failure");Rows.RemoveAll(r=>r.InterfaceIndex==index&&r.DestinationPrefix==prefix&&r.NextHop==gateway&&r.RouteMetric==metric);}
        }
        return(0,"");
    }
    public async Task Start(OpenVpnLink? link=null)
    {
        Link=link??Link;
        await OpenVpnRouteJournal.InstallAsync(Journal,Link,Capture,Run,default);
        Leases.Activate(Link,()=>true);
    }
    public async Task Stop(){Leases.Invalidate();await OpenVpnRouteJournal.CleanupAsync(Journal,Run);}
    public async ValueTask DisposeAsync(){BeforeCreate=null;FailRemove=false;await Stop();Directory.Delete(Root,true);}
}
