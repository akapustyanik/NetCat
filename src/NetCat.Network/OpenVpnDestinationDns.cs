using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NetCat.Core;

namespace NetCat.Network;

public sealed record OpenVpnResolvedAddress(IPAddress Address,uint Ttl);

public static class OpenVpnDestinationDns
{
    public static void Bind(Socket socket,OpenVpnLink link)
    {
        socket.Bind(new IPEndPoint(IPAddress.Parse(link.Address),0));
        socket.SetSocketOption(SocketOptionLevel.IP,(SocketOptionName)31,IPAddress.HostToNetworkOrder(link.Index));
    }
    public static async Task<byte[]> ExchangeAsync(OpenVpnLink link,byte[] query,bool tcp,CancellationToken ct)
    {
        var server=IPAddress.Parse(link.Dns);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(3));
        if(!tcp)
        {
            using var udp=new UdpClient(AddressFamily.InterNetwork);Bind(udp.Client,link);udp.Connect(server,link.DnsPort);
            await udp.SendAsync(query,timeout.Token).ConfigureAwait(false);
            while(true){var r=await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);if(r.Buffer.Length>=12 && r.Buffer[0]==query[0] && r.Buffer[1]==query[1])return r.Buffer;}
        }
        using var client=new TcpClient(AddressFamily.InterNetwork);Bind(client.Client,link);
        await client.ConnectAsync(server,link.DnsPort,timeout.Token).ConfigureAwait(false);
        var stream=client.GetStream();var size=new byte[2];BinaryPrimitives.WriteUInt16BigEndian(size,(ushort)query.Length);
        await stream.WriteAsync(size,timeout.Token).ConfigureAwait(false);await stream.WriteAsync(query,timeout.Token).ConfigureAwait(false);
        await stream.ReadExactlyAsync(size,timeout.Token).ConfigureAwait(false);var response=new byte[BinaryPrimitives.ReadUInt16BigEndian(size)];
        await stream.ReadExactlyAsync(response,timeout.Token).ConfigureAwait(false);return response;
    }
    public static byte[] Query(string host)
    {
        var name=new System.Globalization.IdnMapping().GetAscii(host.TrimEnd('.')).ToLowerInvariant();
        if(name.Length is 0 or >253)throw new IOException("Invalid corporate DNS name.");
        using var output=new MemoryStream();byte[] header=[0,0,1,0,0,1,0,0,0,0,0,0];RandomNumberGenerator.Fill(header.AsSpan(0,2));output.Write(header);
        foreach(var label in name.Split('.')){var bytes=Encoding.ASCII.GetBytes(label);if(bytes.Length is 0 or >63)throw new IOException("Invalid corporate DNS label.");output.WriteByte((byte)bytes.Length);output.Write(bytes);}
        output.Write(new byte[]{0,0,1,0,1});return output.ToArray();
    }
    private static ushort U16(byte[] b,int offset) {if(offset<0 || offset+2>b.Length)throw new IOException("Truncated DNS.");return BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(offset,2));}
    private static string Name(byte[] b,ref int offset)
    {
        var labels=new List<string>();int p=offset;bool jumped=false;var visited=new HashSet<int>();
        while(true)
        {
            if(p>=b.Length || !visited.Add(p) || visited.Count>128)throw new IOException("Invalid DNS compression.");
            int len=b[p++];
            if((len&0xc0)==0xc0){if(p>=b.Length)throw new IOException("Truncated DNS pointer.");if(!jumped)offset=p+1;p=((len&63)<<8)|b[p];jumped=true;continue;}
            if(len>63 || p+len>b.Length)throw new IOException("Invalid DNS label.");
            if(len==0){if(!jumped)offset=p;break;}
            labels.Add(Encoding.ASCII.GetString(b,p,len));p+=len;
            if(labels.Sum(x=>x.Length+1)>254)throw new IOException("DNS name too long.");
        }
        return string.Join('.',labels).ToLowerInvariant();
    }
    public static (IReadOnlyList<OpenVpnResolvedAddress> Addresses,string? Alias,uint AliasTtl) Parse(byte[] query,byte[] response)
    {
        if(response.Length<12 || response[0]!=query[0] || response[1]!=query[1] || (response[2]&0xf8)!=0x80 || (response[3]&15)!=0 || U16(response,4)!=1 || (response[2]&2)!=0)throw new IOException("Invalid corporate DNS response.");
        int q=12,p=12;var wanted=Name(query,ref q);var actual=Name(response,ref p);
        if(wanted!=actual || U16(response,p)!=1 || U16(response,p+2)!=1)throw new IOException("Mismatched DNS question.");p+=4;
        var records=new List<(string Owner,ushort Type,uint Ttl,IPAddress? Address,string? Alias)>();
        int answers=U16(response,6);if(answers>4096)throw new IOException("Too many DNS answers.");
        for(int i=0;i<answers;i++)
        {
            var owner=Name(response,ref p);var type=U16(response,p);var cls=U16(response,p+2);if(p+10>response.Length)throw new IOException("Truncated DNS record.");
            uint ttl=BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(p+4,4));int length=U16(response,p+8);p+=10;if(p+length>response.Length)throw new IOException("Truncated DNS data.");
            IPAddress? ip=null;string? alias=null;if(cls==1 && type==1 && length==4)ip=new IPAddress(response.AsSpan(p,4));
            if(cls==1 && type==5){int at=p;alias=Name(response,ref at);if(at!=p+length)throw new IOException("Malformed CNAME.");}
            if(cls==1)records.Add((owner,type,ttl,ip,alias));p+=length;
        }
        var seen=new HashSet<string>();string name=wanted;uint minimum=uint.MaxValue;
        for(int i=0;i<16;i++)
        {
            if(!seen.Add(name))throw new IOException("CNAME cycle.");
            var addresses=records.Where(r=>r.Owner==name && r.Address!=null).Select(r=>new OpenVpnResolvedAddress(r.Address!,Math.Min(minimum,r.Ttl))).DistinctBy(r=>r.Address).ToArray();
            if(addresses.Length>0)return(addresses,null,minimum);
            var aliases=records.Where(r=>r.Owner==name && r.Alias!=null).ToArray();
            if(aliases.Length==0)return([],name==wanted?null:name,minimum);
            if(aliases.Select(x=>x.Alias).Distinct().Count()!=1)throw new IOException("Conflicting CNAME.");
            minimum=Math.Min(minimum,aliases.Min(x=>x.Ttl));name=aliases[0].Alias!;
        }
        throw new IOException("CNAME chain too long.");
    }
    public static async Task<IReadOnlyList<OpenVpnResolvedAddress>> ResolveAsync(string host,OpenVpnLink link,Func<bool> current,CancellationToken ct,
        Func<OpenVpnLink,byte[],bool,CancellationToken,Task<byte[]>>? exchange=null)
    {
        exchange??=ExchangeAsync;var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);uint ttl=uint.MaxValue;
        for(int i=0;i<8;i++)
        {
            if(!current())throw new OperationCanceledException("Obsolete DNS generation.");if(!seen.Add(host))throw new IOException("CNAME cycle.");
            var query=Query(host);byte[] response;
            try{response=await exchange(link,query,false,ct).ConfigureAwait(false);}
            catch(Exception e)when(!ct.IsCancellationRequested && e is SocketException or OperationCanceledException){response=await exchange(link,query,true,ct).ConfigureAwait(false);}
            if(response.Length>=4 && (response[2]&2)!=0)response=await exchange(link,query,true,ct).ConfigureAwait(false);
            if(!current())throw new OperationCanceledException("Obsolete DNS generation.");
            var parsed=Parse(query,response);ttl=Math.Min(ttl,parsed.AliasTtl);
            if(parsed.Addresses.Count>0)return parsed.Addresses.Select(a=>a with{Ttl=Math.Min(ttl,a.Ttl)}).ToArray();
            host=parsed.Alias??throw new IOException("Corporate DNS returned no IPv4 address.");
        }
        throw new IOException("CNAME chain too long.");
    }
}
