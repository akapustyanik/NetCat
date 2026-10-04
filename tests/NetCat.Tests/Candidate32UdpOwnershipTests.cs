using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate32UdpOwnershipTests
{
    private const int Port=40123;
    private static LocalListener.TableQuery Table(bool ipv6,params (string Address,int Pid)[] rows)
        => (nint memory,ref int bytes)=>
        {
            int width=ipv6?28:12;
            if(memory==0){bytes=4+width*rows.Length;return 122;}
            Marshal.Copy(new byte[bytes],0,memory,bytes);Marshal.WriteInt32(memory,rows.Length);
            for(int i=0;i<rows.Length;i++)
            {
                var row=IntPtr.Add(memory,4+i*width);var address=IPAddress.Parse(rows[i].Address).GetAddressBytes();
                Marshal.Copy(address,0,row,address.Length);
                int portOffset=ipv6?20:4;
                Marshal.WriteByte(row,portOffset,Port>>8);Marshal.WriteByte(row,portOffset+1,Port&255);
                Marshal.WriteInt32(row,ipv6?24:8,rows[i].Pid);
            }
            return 0;
        };
    [Fact] public void UdpOwnershipMatchesSourceAddressRatherThanFirstSamePort()
        => Assert.Equal(32,LocalListener.UdpOwner(new(IPAddress.Loopback,Port),Table(false,("127.0.0.2",19),("127.0.0.1",32)),Table(true)));
    [Fact] public void UdpOverlappingExactAndWildcardOwnersFailClosed()
        => Assert.Equal(0,LocalListener.UdpOwner(new(IPAddress.Loopback,Port),Table(false,("0.0.0.0",19),("127.0.0.1",32)),Table(true)));
    [Fact] public void UdpOwnershipCannotAuthorizeAnUnrelatedBoundAddress()
        => Assert.Equal(0,LocalListener.UdpOwner(new(IPAddress.Loopback,Port),Table(false,("127.0.0.2",32)),Table(true)));
    [Fact] public void UdpAmbiguousExactOwnersFailClosed()
        => Assert.Equal(0,LocalListener.UdpOwner(new(IPAddress.Loopback,Port),Table(false,("127.0.0.1",19),("127.0.0.1",32)),Table(true)));
    [Fact] public void UdpDualStackWildcardOwnerIsFound()
        => Assert.Equal(32,LocalListener.UdpOwner(new(IPAddress.Loopback,Port),Table(false),Table(true,("::",32))));
    [Fact] public void UdpAmbiguousCrossFamilyWildcardOwnersFailClosed()
        => Assert.Equal(0,LocalListener.UdpOwner(new(IPAddress.Loopback,Port),Table(false,("0.0.0.0",19)),Table(true,("::",32))));
    [Fact] public void UdpMappedIpv6AddressMatchesIpv4Peer()
        => Assert.Equal(32,LocalListener.UdpOwner(new(IPAddress.Loopback,Port),Table(false),Table(true,("::ffff:127.0.0.1",32))));
    [Fact] public void WindowsUdpEndpointOwnerMatchesRealSocket()
    {
        using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        Assert.Equal(Environment.ProcessId,LocalListener.UdpOwner((IPEndPoint)udp.Client.LocalEndPoint!));
    }
    [Fact] public void UdpOwnershipRetriesWhenTableGrows()
    {
        int calls=0;var valid=Table(false,("127.0.0.1",32));
        LocalListener.TableQuery growing=(nint memory,ref int bytes)=>
        {
            calls++;if(memory==0){bytes=4;return 122;}
            if(calls==2){bytes=16;return 122;}
            return valid(memory,ref bytes);
        };
        Assert.Equal(32,LocalListener.UdpOwner(new(IPAddress.Loopback,Port),growing,Table(true)));
        Assert.Equal(3,calls);
    }
}
