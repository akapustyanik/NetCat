using System.Net;
using System.Net.Sockets;
using System.Reflection;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;
public sealed class Candidate19GatewayTests
{
    internal sealed class Lease : IDisposable
    {
        private readonly object value=Activator.CreateInstance(typeof(OpenVpnSidecar).Assembly.GetType("NetCat.Network.OpenVpnGatewayLease")!)!;
        public int Port=>(int)value.GetType().GetProperty("Port")!.GetValue(value)!;
        public void Backend(int? port)=>value.GetType().GetMethod("SetBackend")!.Invoke(value,[port]);
        public void Dispose()=>((IDisposable)value).Dispose();
    }
    [Fact] public async Task TcpHalfCloseStillReceivesServerResponse()
    {
        using var timer=new CancellationTokenSource(TimeSpan.FromSeconds(5));var ct=timer.Token;
        using var backend=new TcpListener(IPAddress.Loopback,0);backend.Start();using var lease=new Lease();lease.Backend(((IPEndPoint)backend.LocalEndpoint).Port);
        using var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,lease.Port,ct);
        using var server=await backend.AcceptTcpClientAsync(ct);var inputStream=client.GetStream();var outputStream=server.GetStream();await inputStream.WriteAsync(new byte[]{19},ct);client.Client.Shutdown(SocketShutdown.Send);
        var input=new byte[1];await outputStream.ReadExactlyAsync(input,ct);Assert.Equal(19,input[0]);Assert.Equal(0,await outputStream.ReadAsync(input,ct));
        await outputStream.WriteAsync(new byte[]{42},ct);server.Client.Shutdown(SocketShutdown.Send);
        await inputStream.ReadExactlyAsync(input,ct);Assert.Equal(42,input[0]);
    }
    [Fact] public async Task ServerHalfCloseStillAllowsClientDrain()
    {
        using var timer=new CancellationTokenSource(TimeSpan.FromSeconds(5));var ct=timer.Token;
        using var backend=new TcpListener(IPAddress.Loopback,0);backend.Start();using var lease=new Lease();lease.Backend(((IPEndPoint)backend.LocalEndpoint).Port);
        using var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,lease.Port,ct);using var server=await backend.AcceptTcpClientAsync(ct);var inputStream=client.GetStream();var outputStream=server.GetStream();
        server.Client.Shutdown(SocketShutdown.Send);var input=new byte[1];Assert.Equal(0,await inputStream.ReadAsync(input,ct));
        await inputStream.WriteAsync(new byte[]{19},ct);client.Client.Shutdown(SocketShutdown.Send);await outputStream.ReadExactlyAsync(input,ct);Assert.Equal(19,input[0]);
    }
}
