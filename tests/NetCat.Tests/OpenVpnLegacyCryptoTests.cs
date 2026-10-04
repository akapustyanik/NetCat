using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

[Collection("Candidate18 native resources")]
public sealed class OpenVpnLegacyCryptoTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"NetCat-crypto-"+Guid.NewGuid().ToString("N"));
    private readonly string bundle=Path.Combine(RoutingTests.ModuleRoot,"openvpn");
    private string Exe=>Path.Combine(bundle,"openvpn.exe");
    private string Ssl=>Path.Combine(bundle,"openssl.exe");
    public OpenVpnLegacyCryptoTests()=>Directory.CreateDirectory(root);
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
    private async Task<string> Container(bool legacy)
    {
        using var rsa=RSA.Create(2048);
        var request=new CertificateRequest("CN=NetCat synthetic fixture",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
        var key=Path.Combine(root,"key.pem");var cert=Path.Combine(root,"cert.pem");var p12=Path.Combine(root,legacy?"legacy.p12":"modern.p12");
        await File.WriteAllTextAsync(key,rsa.ExportPkcs8PrivateKeyPem());await File.WriteAllTextAsync(cert,certificate.ExportCertificatePem());
        var args=new List<string>{"pkcs12","-export","-inkey",key,"-in",cert,"-out",p12,"-passout","pass:synthetic"};
        if(legacy)args.AddRange(["-legacy","-certpbe","PBE-SHA1-RC2-40","-keypbe","PBE-SHA1-3DES"]);
        using var scope=OpenVpnCryptoScope.Create(Exe,root);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result=await ProcessHost.RunAsync(Ssl,args,timeout.Token,new Dictionary<string,string>(scope.Environment));
        Assert.Equal(0,result.Code);Assert.True(File.Exists(p12));return p12;
    }
    private Dictionary<string,string> DefaultOnly()
    {
        var config=Path.Combine(root,"default.cnf");File.WriteAllText(config,"openssl_conf = init\n[init]\nproviders = providers\n[providers]\ndefault = default_sect\n[default_sect]\nactivate = 1\n");
        return new(){["OPENSSL_CONF"]=config,["OPENSSL_MODULES"]=Path.Combine(bundle,"ossl-modules")};
    }
    private async Task<(int Code,string Output)> Parse(string path,IDictionary<string,string> env)
    {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));return await ProcessHost.RunAsync(Ssl,["pkcs12","-in",path,"-passin","pass:synthetic","-info","-noout"],timeout.Token,env);}
    [Fact] public async Task LegacyPkcs12FailsWithoutLegacyProvider()
    {var path=await Container(true);var result=await Parse(path,DefaultOnly());Assert.NotEqual(0,result.Code);Assert.True(result.Output.Contains("RC2-40-CBC"),result.Output);}
    [Fact] public async Task LegacyPkcs12ParsesWithScopedLegacyProvider()
    {var path=await Container(true);using var scope=OpenVpnCryptoScope.Create(Exe,root);var result=await Parse(path,new Dictionary<string,string>(scope.Environment));Assert.Equal(0,result.Code);Assert.Contains("pbeWithSHA1And40BitRC2-CBC",result.Output);}
    [Fact] public async Task ModernPkcs12DoesNotRequireLegacyProvider()
    {var path=await Container(false);var result=await Parse(path,DefaultOnly());Assert.True(result.Code==0,result.Output);Assert.Contains("AES-256-CBC",result.Output);}
    [Fact] public async Task GlobalOpenSslEnvironmentIsNotModified()
    {
        var before=(Environment.GetEnvironmentVariable("OPENSSL_CONF"),Environment.GetEnvironmentVariable("OPENSSL_MODULES"));
        var path=await Container(true);using(var scope=OpenVpnCryptoScope.Create(Exe,root)){Assert.Equal(0,(await Parse(path,new Dictionary<string,string>(scope.Environment))).Code);}
        Assert.Equal(before,(Environment.GetEnvironmentVariable("OPENSSL_CONF"),Environment.GetEnvironmentVariable("OPENSSL_MODULES")));
    }
    private string CopyBundle()
    {var copy=Path.Combine(root,"openvpn");foreach(var file in Directory.EnumerateFiles(bundle,"*",SearchOption.AllDirectories)){var target=Path.Combine(copy,Path.GetRelativePath(bundle,file));Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target);}return copy;}
    [Fact] public void MissingLegacyProviderFailsClosed()
    {var copy=CopyBundle();foreach(var file in Directory.GetFiles(copy,"legacy.dll",SearchOption.AllDirectories))File.Delete(file);Assert.Throws<InvalidDataException>(()=>OpenVpnCryptoScope.Create(Path.Combine(copy,"openvpn.exe"),Path.Combine(root,"rt")));}
    [Fact] public void TamperedLegacyProviderIsRejected()
    {var copy=CopyBundle();File.AppendAllText(Directory.GetFiles(copy,"legacy.dll",SearchOption.AllDirectories)[0],"tampered");Assert.Throws<InvalidDataException>(()=>OpenVpnCryptoScope.Create(Path.Combine(copy,"openvpn.exe"),Path.Combine(root,"rt")));}
    [Fact] public void PrivateOpenSslConfigurationIsLockedAndMatchesCompiledPolicy()
    {using var scope=OpenVpnCryptoScope.Create(Exe,root);var path=scope.Environment["OPENSSL_CONF"];Assert.Equal(OpenVpnCryptoScope.Configuration,File.ReadAllText(path));Assert.Throws<IOException>(()=>File.WriteAllText(path,"modified"));}
    private sealed class NoAdapter:IDisposable{public void Dispose(){}}
    [Fact] public async Task OpenVpnUnsupportedCryptoDoesNotRetryChild()
    {
        int starts=0;
        using var service=new OpenVpnService(Exe,Path.Combine(root,"fatal"))
        {
            CreateAdapterOverride=()=>new NoAdapter(),PowerShellOverride=(_,_)=>Task.FromResult((0,"")),CaptureRouteTableOverride=()=>[],
            StartProcessOverride=host=>{starts++;host.Start(ProcessHost.PowerShellPath,["-NoProfile","-NonInteractive","-Command","[Console]::WriteLine('Algorithm (RC2-40-CBC : 0) unsupported'); Start-Sleep -Seconds 20"],log:false);}
        };
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var profile=new Profile{Protocol="openvpn",OpenVpnConfig="client\ndev tun\nremote 127.0.0.1 9\n"};
        var error=await Assert.ThrowsAsync<OpenVpnLegacyCryptoException>(()=>service.StartAsync(profile,"",timeout.Token));
        Assert.Equal(OpenVpnFailureClass.DeterministicLocalFatal,OpenVpnFailureClassifier.Classify(null,error));
        Assert.Equal(1,starts);Assert.False(profile.OpenVpnLegacyProviderRequired);Assert.False(service.IsRunning);
    }
    [Fact] public async Task OpenVpnChildReceivesPrivateOpenSslEnvironment()
    {
        var path=await Container(true);bool childStarted=false;string? output=null;string[]? arguments=null;
        using var service=new OpenVpnService(Exe,Path.Combine(root,"runtime"))
        {
            CreateAdapterOverride=()=>new NoAdapter(),PowerShellOverride=(_,_)=>Task.FromResult((0,"")),CaptureRouteTableOverride=()=>[],
            StartChildOverride=(host,args,environment)=>
            {
                childStarted=true;arguments=args.ToArray();Assert.NotNull(environment);
                Assert.Equal(Path.Combine(root,"runtime","openssl-netcat.cnf"),environment["OPENSSL_CONF"]);
                Assert.StartsWith(bundle,environment["OPENSSL_MODULES"]);
                var fullConfig=File.ReadAllText(args[1]);
                // Cipher policy is configuration syntax, not random PKCS#12
                // Base64. Prove that the container is preserved byte-for-byte
                // before testing every directive outside that single block.
                var containers=System.Text.RegularExpressions.Regex.Matches(fullConfig,@"(?s)<pkcs12>\s*(.*?)\s*</pkcs12>");
                Assert.Single(containers);
                Assert.Equal(File.ReadAllBytes(path),Convert.FromBase64String(containers[0].Groups[1].Value));
                var prepared=fullConfig.Remove(containers[0].Index,containers[0].Length);
                Assert.Contains("data-ciphers AES-256-GCM:AES-128-GCM",prepared);
                Assert.DoesNotContain("data-ciphers-fallback",prepared);
                Assert.DoesNotContain("RC2",prepared);
                host.Line+=line=>output+=line+"\n";
                // Actual owned child consumes the exact environment prepared by StartAsync.
                // This only parses a generated container, never opens a VPN or TUN.
                host.Start(Ssl,["pkcs12","-in",path,"-passin","pass:synthetic","-info","-noout"],bundle,false,environment);
            }
        };
        var profile=new Profile{Protocol="openvpn",OpenVpnConfig="client\ndev tun\nremote 127.0.0.1 9\ndata-ciphers AES-256-GCM:AES-128-GCM\n<pkcs12>\n"+Convert.ToBase64String(await File.ReadAllBytesAsync(path))+"\n</pkcs12>\n"};
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<IOException>(()=>service.StartAsync(profile,"",timeout.Token));
        Assert.True(childStarted);Assert.Contains("pbeWithSHA1And40BitRC2-CBC",output);Assert.DoesNotContain("unsupported",output);
        Assert.NotNull(arguments);Assert.DoesNotContain("--data-ciphers",arguments);Assert.DoesNotContain("--cipher",arguments);
    }
    [Fact] public async Task LegacyProviderDoesNotChangeDataCipherPolicy()
    {
        const string input="client\ndev tun\nremote 127.0.0.1 9\ndata-ciphers AES-256-GCM:AES-128-GCM\n";
        var before=OpenVpnConfiguration.Prepare(input);var path=await Container(true);
        using var scope=OpenVpnCryptoScope.Create(Exe,root);Assert.Equal(0,(await Parse(path,new Dictionary<string,string>(scope.Environment))).Code);
        Assert.Equal(before,OpenVpnConfiguration.Prepare(input));Assert.Contains("data-ciphers AES-256-GCM:AES-128-GCM",before);
        Assert.DoesNotContain("cipher",OpenVpnCryptoScope.Configuration,StringComparison.OrdinalIgnoreCase);
    }
}
