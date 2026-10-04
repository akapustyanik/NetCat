using System.Net;
using System.Net.Sockets;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;
[Collection("Candidate18 native resources")]
public sealed class Candidate19UdpTests
{
    private sealed class ManualTime:TimeProvider
    {public long Ticks;public override long TimestampFrequency=>TimeSpan.TicksPerSecond;public override long GetTimestamp()=>Ticks;}
    [Fact] public async Task IdleUdpSessionExpiresAndGenerationInvalidationClosesAllSessions()
    {
        var type=typeof(OpenVpnSidecar).Assembly.GetType("NetCat.Network.OpenVpnGatewayLease")!;using var lease=(IDisposable)Activator.CreateInstance(type)!;
        var time=new ManualTime();type.GetProperty("TimeProvider")!.SetValue(lease,time);
        using var server=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));type.GetMethod("SetBackend")!.Invoke(lease,[((IPEndPoint)server.Client.LocalEndPoint!).Port]);
        int port=(int)type.GetProperty("Port")!.GetValue(lease)!;int Count()=>(int)type.GetProperty("UdpSessionCount")!.GetValue(lease)!;
        using var client=new UdpClient();using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await client.SendAsync(new byte[]{19},new IPEndPoint(IPAddress.Loopback,port),ct.Token);await server.ReceiveAsync(ct.Token);Assert.Equal(1,Count());
        time.Ticks=TimeSpan.FromSeconds(31).Ticks;type.GetMethod("ExpireIdle")!.Invoke(lease,[]);Assert.Equal(0,Count());
        await client.SendAsync(new byte[]{20},new IPEndPoint(IPAddress.Loopback,port),ct.Token);await server.ReceiveAsync(ct.Token);Assert.Equal(1,Count());
        type.GetMethod("SetBackend")!.Invoke(lease,[null]);Assert.Equal(0,Count());
    }
    [Fact] public async Task MoreThan256SequentialUdpClientsContinueWorking()
    {
        using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var echo=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        var worker=Task.Run(async()=>{try{while(true){var p=await echo.ReceiveAsync(lifetime.Token);await echo.SendAsync(p.Buffer,p.RemoteEndPoint,lifetime.Token);}}catch(OperationCanceledException){}},CancellationToken.None);
        var type=typeof(OpenVpnSidecar).Assembly.GetType("NetCat.Network.OpenVpnGatewayLease")!;
        using var lease=(IDisposable)Activator.CreateInstance(type)!;
        type.GetMethod("SetBackend")!.Invoke(lease,[((IPEndPoint)echo.Client.LocalEndPoint!).Port]);
        int port=(int)type.GetProperty("Port")!.GetValue(lease)!;
        try
        {
            for(int i=0;i<300;i++)
            {
                // Candidate19 default idle policy is 30 seconds; give every
                // prior endpoint time to expire before testing historical cap.
                if(i==256)await Task.Delay(TimeSpan.FromSeconds(31),lifetime.Token);
                using var client=new UdpClient();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var bytes=BitConverter.GetBytes(i);await client.SendAsync(bytes,new IPEndPoint(IPAddress.Loopback,port),deadline.Token);
                var result=await client.ReceiveAsync(deadline.Token);Assert.Equal(bytes,result.Buffer);
            }
        }
        finally{lifetime.Cancel();await worker;}
    }
}
