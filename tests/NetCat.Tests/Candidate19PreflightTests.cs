using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NetCat.Core;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;
public sealed class Candidate19PreflightTests
{
    [Fact] public async Task OfflinePreflightNeverLaunchesEvenHarmlessClient()
    {
        var root=Path.Combine(Path.GetTempPath(),"NetCat-C19-offline-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var probe=Path.Combine(RoutingTests.FindRoot(),"tests","NetCat.LifetimeProbe","bin","Release","net8.0-windows","NetCat.LifetimeProbe.exe");
            var marker=Path.Combine(root,"launched");using var service=new OpenVpnService(probe,Path.Combine(root,"runtime"));
            var profile=new Profile{Protocol="openvpn",OpenVpnConfig="client\ndev tun\n# NETCAT_TEST_PREFLIGHT_MARKER "+marker};
            var result=await service.PreflightAsync(profile,default);Assert.True(result.Success);Assert.False(File.Exists(marker));
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task OpenVpnPreflightDoesNotOpenNetworkConnection()
    {
        var root=Path.Combine(Path.GetTempPath(),"NetCat-C19-preflight-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            using var server=new TcpListener(IPAddress.Loopback,0);server.Start();int port=((IPEndPoint)server.LocalEndpoint).Port;
            using var rsa=RSA.Create(2048);var request=new CertificateRequest("CN=NetCat-Test",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
            var profile=new Profile {Protocol="openvpn",OpenVpnConfig=$"client\ndev tun\nproto tcp-client\nremote 127.0.0.1 {port}\n<pkcs12>\n{Convert.ToBase64String(cert.Export(X509ContentType.Pkcs12,""))}\n</pkcs12>\n<ca>\n{cert.ExportCertificatePem()}\n</ca>\n"};
            using var service=new OpenVpnService(Path.Combine(RoutingTests.ModuleRoot, "openvpn/openvpn.exe"),root);
            var result=await service.PreflightAsync(profile,CancellationToken.None);Assert.True(result.Success);
            Assert.False(server.Pending());
        }
        finally{Directory.Delete(root,true);}
    }
}
