using System.Buffers.Binary;
using System.Net;
using System.Text;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;
public sealed class Candidate26ResolverTests
{
    private static byte[] Name(string name)=>name.Split('.').SelectMany(s=>new[]{(byte)s.Length}.Concat(Encoding.ASCII.GetBytes(s))).Append((byte)0).ToArray();
    internal static byte[] Response(byte[] query,bool cname=false,bool multiple=false)
    {
        using var m=new MemoryStream();var header=query.ToArray();header[2]=0x81;header[3]=0x80;header[7]=(byte)((cname?1:0)+(multiple?2:1));m.Write(header);
        void Record(byte[] owner,ushort type,byte[] data,uint ttl)
        {m.Write(owner);var rr=new byte[10];BinaryPrimitives.WriteUInt16BigEndian(rr,type);rr[3]=1;BinaryPrimitives.WriteUInt32BigEndian(rr.AsSpan(4),ttl);BinaryPrimitives.WriteUInt16BigEndian(rr.AsSpan(8),(ushort)data.Length);m.Write(rr);m.Write(data);}
        if(cname)Record([0xc0,12],5,Name("alias.example"),30);
        byte[] owner=cname?Name("alias.example"):[0xc0,12];Record(owner,1,IPAddress.Parse("127.0.0.3").GetAddressBytes(),60);
        if(multiple)Record(owner,1,IPAddress.Parse("127.0.0.5").GetAddressBytes(),40);
        return m.ToArray();
    }
    [Fact] public void CnameTtlBoundsEachFinalAddress()
    {var q=OpenVpnDestinationDns.Query("corporate.example");var a=OpenVpnDestinationDns.Parse(q,Response(q,true,true)).Addresses;Assert.Equal(2,a.Count);Assert.All(a,x=>Assert.Equal(30u,x.Ttl));}
    [Fact] public void TransactionMismatchRejected()
    {var q=OpenVpnDestinationDns.Query("corporate.example");var r=Response(q);r[0]^=1;Assert.Throws<IOException>(()=>OpenVpnDestinationDns.Parse(q,r));}
    [Fact] public void CompressionCycleRejected()
    {var q=OpenVpnDestinationDns.Query("corporate.example");var r=Response(q);r[12]=0xc0;r[13]=12;Assert.Throws<IOException>(()=>OpenVpnDestinationDns.Parse(q,r));}
    [Fact] public async Task TruncatedUdpFallsBackToCorporateTcp()
    {
        await using var f=new Candidate26RouteFixture();var calls=new List<bool>();
        var a=await OpenVpnDestinationDns.ResolveAsync("corporate.example",f.Link,()=>true,default,(link,q,tcp,_)=>{Assert.Equal(f.Link,link);calls.Add(tcp);var r=Response(q);if(!tcp)r[2]|=2;return Task.FromResult(r);});
        Assert.Equal(new[]{false,true},calls);Assert.Equal(IPAddress.Parse("127.0.0.3"),Assert.Single(a).Address);
    }
    [Fact] public async Task AddressRotationIsNotPinnedToFirstAnswer()
    {
        await using var f=new Candidate26RouteFixture();int call=0;
        Task<byte[]> Exchange(NetCat.Core.OpenVpnLink _,byte[] q,bool tcp,CancellationToken ct){var r=Response(q);r[^1]=(byte)(++call==1?3:5);return Task.FromResult(r);}
        var first=await OpenVpnDestinationDns.ResolveAsync("corporate.example",f.Link,()=>true,default,Exchange);
        var second=await OpenVpnDestinationDns.ResolveAsync("corporate.example",f.Link,()=>true,default,Exchange);
        Assert.NotEqual(first[0].Address,second[0].Address);Assert.Equal(2,call);
    }
}
