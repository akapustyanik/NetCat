using System.Net;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate31SubscriptionTests
{
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> reply):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(reply(request));}
    [Fact] public async Task HttpSubscriptionIsRejectedBeforeNetworkIoByDefault()
    {
        int network=0;var client=new SubscriptionClient{Resolve=(_,_)=>{network++;return Task.FromResult(new[]{IPAddress.Parse("8.8.8.8")});}};
        await Assert.ThrowsAsync<InvalidDataException>(()=>client.ReadAsync("http://subscription.example/list",false,default));Assert.Equal(0,network);
    }
    [Theory]
    [InlineData("http://subscription.example/next")]
    [InlineData("https://127.0.0.1/next")]
    [InlineData("https://169.254.169.254/next")]
    [InlineData("https://10.1.2.3/next")]
    [InlineData("https://[::1]/next")]
    [InlineData("https://[::ffff:127.0.0.1]/next")]
    [InlineData("https://internal.example/next")]
    public async Task PublicSubscriptionRejectsUnsafeRedirectBeforeSecondConnection(string location)
    {
        int connections=0;
        var client=new SubscriptionClient{
            Resolve=(host,_)=>Task.FromResult(new[]{IPAddress.Parse(host=="internal.example"?"192.168.1.9":"8.8.8.8")}),
            CreateHandler=(_,_)=>new Handler(_=>{connections++;var response=new HttpResponseMessage(HttpStatusCode.Redirect);response.Headers.Location=new Uri(location);return response;})};
        await Assert.ThrowsAsync<InvalidDataException>(()=>client.ReadAsync("https://subscription.example/list",false,default));
        Assert.Equal(1,connections);
    }
    [Fact] public async Task ExplicitInsecureOverrideAllowsLocalHttpWithoutChangingDefault()
    {
        var client=new SubscriptionClient{CreateHandler=(_,_)=>new Handler(_=>new(HttpStatusCode.OK){Content=new StringContent("local fixture")})};
        Assert.Equal("local fixture",await client.ReadAsync("http://127.0.0.1/list",true,default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>client.ReadAsync("http://127.0.0.1/list",false,default));
    }
    [Fact] public async Task VettedDnsAddressesArePassedToConnectionHandler()
    {
        int dns=0,requests=0;
        var client=new SubscriptionClient{Resolve=(_,_)=>{dns++;return Task.FromResult(new[]{IPAddress.Parse("8.8.8.8")});},
            CreateHandler=(uri,addresses)=>{Assert.Equal("subscription.example",uri.DnsSafeHost);Assert.Equal("8.8.8.8",Assert.Single(addresses).ToString());return new Handler(_=>{requests++;return new(HttpStatusCode.OK){Content=new StringContent("fixture")};});}};
        Assert.Equal("fixture",await client.ReadAsync("https://subscription.example/list",false,default));Assert.Equal(1,dns);Assert.Equal(1,requests);
    }
    [Fact] public async Task RedirectLoopIsBounded()
    {
        int requests=0;
        var client=new SubscriptionClient{Resolve=(_,_)=>Task.FromResult(new[]{IPAddress.Parse("8.8.8.8")}),CreateHandler=(_,_)=>new Handler(_=>{requests++;var response=new HttpResponseMessage(HttpStatusCode.Redirect);response.Headers.Location=new Uri("/loop",UriKind.Relative);return response;})};
        await Assert.ThrowsAsync<InvalidDataException>(()=>client.ReadAsync("https://subscription.example/list",false,default));Assert.Equal(6,requests);
    }
    [Fact] public async Task LocalTransportOverrideDoesNotDisableTlsCertificateValidation()
    {
        using var key=System.Security.Cryptography.RSA.Create(2048);
        var request=new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost",key,System.Security.Cryptography.HashAlgorithmName.SHA256,System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddMinutes(5));
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);listener.Start();
        try
        {
            int received=0;
            var server=Task.Run(async()=>
            {
                using var socket=await listener.AcceptTcpClientAsync(stop.Token);
                using var tls=new System.Net.Security.SslStream(socket.GetStream());
                try
                {
                    await tls.AuthenticateAsServerAsync(new System.Net.Security.SslServerAuthenticationOptions {ServerCertificate=cert},stop.Token);
                    received=await tls.ReadAsync(new byte[256],stop.Token);
                }
                catch(Exception e)when(e is IOException or System.Security.Authentication.AuthenticationException){}
            });
            var port=((IPEndPoint)listener.LocalEndpoint).Port;
            await Assert.ThrowsAsync<HttpRequestException>(()=>new SubscriptionClient().ReadAsync($"https://127.0.0.1:{port}/list",true,stop.Token));
            await server;Assert.Equal(0,received);
        }
        finally {listener.Stop();}
    }
}
