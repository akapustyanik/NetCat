using System.Net;
using System.Net.Sockets;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;
[Collection("Candidate18 native resources")]
public sealed class Candidate29ShutdownTests
{
    [Fact] public async Task GatewayDisposeWaitsForFinalRouteLeaseRelease()
    {
        await using var routes=new Candidate26RouteFixture();await routes.Start();
        var removing=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRemove=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leases=new OpenVpnDestinationLeases(routes.Journal,routes.Capture,async(command,ct)=>
        {
            if(command.Contains("Remove-NetRoute")){removing.TrySetResult();await allowRemove.Task;}
            return await routes.Run(command,ct);
        });
        leases.Activate(routes.Link,()=>true);
        var gateway=new OpenVpnDestinationGateway(leases,()=>Environment.ProcessId);gateway.Activate();
        using var echo=new TcpListener(IPAddress.Parse("127.0.0.3"),0);echo.Start();
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client=await Candidate26EgressTests.Authenticate(gateway,stop.Token);
        int port=((IPEndPoint)echo.LocalEndpoint).Port;
        await client.GetStream().WriteAsync(new byte[]{5,1,0}.Concat(Candidate26EgressTests.Address("127.0.0.3",port)).ToArray(),stop.Token);
        using var peer=await echo.AcceptTcpClientAsync(stop.Token);await client.GetStream().ReadExactlyAsync(new byte[10],stop.Token);
        var generation=leases.Capture();Assert.Equal(1,leases.ReferenceCount(generation,IPAddress.Parse("127.0.0.3")));
        var closing=Task.Run(gateway.Dispose);
        try
        {
            await removing.Task.WaitAsync(stop.Token);
            Assert.False(closing.IsCompleted);
        }
        finally{allowRemove.TrySetResult();await closing.WaitAsync(stop.Token);}
        Assert.Equal(0,leases.ReferenceCount(generation,IPAddress.Parse("127.0.0.3")));
        Assert.DoesNotContain(routes.Capture(),r=>r.DestinationPrefix=="127.0.0.3/32");
    }
}
