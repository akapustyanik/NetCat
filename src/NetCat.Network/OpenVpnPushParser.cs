using System.Net;
using System.Net.Sockets;
using System.Globalization;

namespace NetCat.Network;

public enum OpenVpnGatewayMode { DefaultTunnel, VpnGateway, NetGateway, RemoteHost, Explicit, None }
public enum OpenVpnRouteDisposition { TunnelOwned, PhysicalBypass, ExplicitUnsupported, Rejected }
public sealed record ParsedOpenVpnRoute(IPAddress? Destination, int PrefixLength, string? OriginalNetmask,
    OpenVpnGatewayMode GatewayMode, IPAddress? ExplicitGateway, uint? Metric, string Source, OpenVpnRouteDisposition Disposition)
{ public string Prefix => $"{Destination}/{PrefixLength}"; }
public sealed record OpenVpnDnsEndpoint(IPAddress Address, int Port, int ServerId);
public sealed record OpenVpnPush(string Gateway, ParsedOpenVpnRoute[] Routes, OpenVpnDnsEndpoint[] Dns);

public static class OpenVpnPushParser
{
    internal static IPAddress Ipv4(string value)
    {
        if(value.Split('.').Length!=4||!IPAddress.TryParse(value,out var ip)||ip.AddressFamily!=AddressFamily.InterNetwork||ip.ToString()!=value)
            throw new InvalidDataException("OpenVPN PUSH: требуется канонический IPv4-адрес.");
        return ip;
    }
    public static ParsedOpenVpnRoute ParseRoute(string option)
    {
        var t=Tokens(option);
        if(t.Length<2||t.Length>5||t[0]!="route")throw new InvalidDataException("OpenVPN PUSH: неверная директива route.");
        if(t[1]=="remote_host")return new(null,32,null,OpenVpnGatewayMode.RemoteHost,null,null,"PUSH",OpenVpnRouteDisposition.ExplicitUnsupported);
        var dest=Ipv4(t[1]);var mask=t.Length>2?Ipv4(t[2]):IPAddress.Parse("255.255.255.255");
        if(!OpenVpnService.TryGetContiguousCidr(mask,out var prefix))throw new InvalidDataException("OpenVPN PUSH: непрерывная маска обязательна.");
        var mode=OpenVpnGatewayMode.DefaultTunnel;IPAddress? gateway=null;
        if(t.Length>3)mode=t[3] switch {"vpn_gateway"=>OpenVpnGatewayMode.VpnGateway,"net_gateway"=>OpenVpnGatewayMode.NetGateway,"remote_host"=>OpenVpnGatewayMode.RemoteHost,_=>OpenVpnGatewayMode.Explicit};
        if(mode==OpenVpnGatewayMode.Explicit)gateway=Ipv4(t[3]);
        uint? metric=null;
        if(t.Length==5){if(!uint.TryParse(t[4],NumberStyles.None,CultureInfo.InvariantCulture,out var n))throw new InvalidDataException("OpenVPN PUSH: недопустимая метрика.");metric=n;}
        var disposition=(mode is OpenVpnGatewayMode.DefaultTunnel or OpenVpnGatewayMode.VpnGateway)&&metric==null?OpenVpnRouteDisposition.TunnelOwned:OpenVpnRouteDisposition.ExplicitUnsupported;
        return new(OpenVpnService.GetCanonicalNetwork(dest,mask),prefix,t.Length>2?t[2]:null,mode,gateway,metric,"PUSH",disposition);
    }
    internal static string[] Tokens(string s)=>s.Split([' ','\t'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
    public static OpenVpnPush Parse(IEnumerable<string> options,bool allowPublic)
    {
        var routes=new List<ParsedOpenVpnRoute>();var legacy=new List<OpenVpnDnsEndpoint>();var modern=new List<OpenVpnDnsEndpoint>();string gateway="";
        foreach(var option in options)
        {
            var t=Tokens(option);if(t.Length==0)continue;
            switch(t[0])
            {
                case "route":
                    var route=ParseRoute(option);
                    if(route.Disposition!=OpenVpnRouteDisposition.TunnelOwned)throw new InvalidDataException($"OpenVPN PUSH: {route.GatewayMode}/metric не поддерживается; маршрут не преобразован в корпоративный. Требуется split-route профиль без bypass/явной метрики.");
                    if(!OpenVpnRoutes.ValidatePushedRoute(route.Prefix,allowPublic))throw new InvalidDataException("OpenVPN PUSH: маршрут запрещён политикой или перехватывает default route.");
                    routes.Add(route);break;
                case "route-gateway":
                    if(t.Length!=2)throw new InvalidDataException("OpenVPN PUSH: неверный route-gateway.");gateway=Ipv4(t[1]).ToString();break;
                case "route-ipv6":case "route-ipv6-gateway":case "route-metric":
                    throw new InvalidDataException("OpenVPN PUSH: IPv6/route-metric не поддерживается.");
                case "redirect-gateway":case "redirect-private":
                    throw new InvalidDataException("OpenVPN: сервер запросил full-tunnel (redirect-gateway/def1); поддерживается только split-route режим. Основной default route не изменён.");
                case "dhcp-option":
                    if(t.Length>1&&t[1] is "DNS" or "DNS6"){if(t.Length!=3)throw new InvalidDataException("OpenVPN PUSH: неверный DNS.");legacy.Add(new(Ipv4(t[2]),53,0));}break;
                case "dns":
                    if(t.Length<5||t[1]!="server"||!int.TryParse(t[2],out var id)||id<0||id>127)throw new InvalidDataException("OpenVPN PUSH: неподдерживаемая DNS-директива/ID.");
                    if(t[3]=="transport"&&t.Length==5&&t[4]=="plain")break;
                    if(t[3]!="address")throw new InvalidDataException("OpenVPN PUSH: DoH/DoT/DNSSEC/SNI/resolve-domains не поддерживаются; понижение до UDP запрещено.");
                    if(t.Length>12)throw new InvalidDataException("OpenVPN PUSH: слишком много DNS-адресов.");
                    foreach(var text in t.Skip(4))
                    {
                        var parts=text.Split(':');if(parts.Length>2)throw new InvalidDataException("OpenVPN PUSH: IPv6 DNS не поддерживается.");
                        int port=53;if(parts.Length==2&&(!int.TryParse(parts[1],out port)||port<1||port>65535))throw new InvalidDataException("OpenVPN PUSH: недопустимый DNS-порт.");
                        modern.Add(new(Ipv4(parts[0]),port,id));
                    }break;
            }
        }
        return new(gateway,routes.Distinct().ToArray(),(modern.Count>0?modern:legacy).OrderBy(d=>d.ServerId).Distinct().ToArray());
    }
}
