using System.Net;
using System.Net.Sockets;
using NetCat.Network;
using Xunit;
namespace NetCat.Tests;
public sealed class Candidate21AllocatorTests
{
    [Fact] public void ExcludedListenerBindIsRecoverableButDriverAccessDenialIsNot()
    {
        Assert.True(PortStartup.IsCollision("listen udp 127.0.0.1:53918: bind: An attempt was made to access a socket in a way forbidden by its access permissions."));
        Assert.False(PortStartup.IsCollision("access denied loading wintun"));
        Assert.False(PortStartup.IsCollision("open configuration: Access is denied"));
    }
    [Fact] public void TcpFreeButUdpOccupiedIsRejected()
    {
        var held=PortStartup.BindTcpUdp();int blocked=((IPEndPoint)held.Tcp.LocalEndpoint).Port;
        held.Tcp.Stop();
        using(var tcpProof=new TcpListener(IPAddress.Loopback,blocked)){tcpProof.Start();}
        int tries=0;var pair=PortStartup.BindTcpUdp(()=>++tries==1?blocked:OpenVpnService.FreeTcpUdpPort());
        try{Assert.Equal(2,tries);Assert.NotEqual(blocked,((IPEndPoint)pair.Tcp.LocalEndpoint).Port);}
        finally{held.Udp.Dispose();pair.Tcp.Stop();pair.Udp.Dispose();}
    }
    [Fact] public void UdpFreeButTcpOccupiedIsRejected()
    {
        var held=PortStartup.BindTcpUdp();int blocked=((IPEndPoint)held.Tcp.LocalEndpoint).Port;
        held.Udp.Dispose();
        using(var udpProof=new UdpClient(new IPEndPoint(IPAddress.Loopback,blocked))){Assert.Equal(blocked,((IPEndPoint)udpProof.Client.LocalEndPoint!).Port);}
        int tries=0;var pair=PortStartup.BindTcpUdp(()=>++tries==1?blocked:OpenVpnService.FreeTcpUdpPort());
        try{Assert.Equal(2,tries);Assert.NotEqual(blocked,((IPEndPoint)pair.Tcp.LocalEndpoint).Port);}
        finally{held.Tcp.Stop();pair.Tcp.Stop();pair.Udp.Dispose();}
    }
    [Fact] public void RejectedDualTransportLeaseDoesNotLeavePartialTcpListener()
    {
        var held=PortStartup.BindTcpUdp();int blocked=((IPEndPoint)held.Tcp.LocalEndpoint).Port;held.Tcp.Stop();
        int tries=0;
        try{Assert.Throws<PortCollisionException>(()=>PortStartup.BindTcpUdp(()=>{tries++;return blocked;}));Assert.Equal(100,tries);
            using var tcp=new TcpListener(IPAddress.Loopback,blocked);tcp.Start();Assert.True(tcp.Server.IsBound);}
        finally{held.Udp.Dispose();}
    }
}
