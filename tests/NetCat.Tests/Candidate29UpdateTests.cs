using System.Security.Cryptography;
using System.Text.Json;
using NetCat.Engine;
using NetCat.Updater;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace NetCat.Tests;
public sealed class Candidate29UpdateTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"NetCat-C29-Update-"+Guid.NewGuid().ToString("N"));
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
    private static (byte[] Bytes,byte[] Signature,byte[] Key) Sign(ComponentManifest manifest)
    {
        var key=new Ed25519PrivateKeyParameters(RandomNumberGenerator.GetBytes(32),0);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(manifest);var signer=new Ed25519Signer();signer.Init(true,key);signer.BlockUpdate(bytes,0,bytes.Length);
        return(bytes,signer.GenerateSignature(),key.GeneratePublicKey().GetEncoded());
    }
    private static ComponentManifest Manifest(Dictionary<string,string> files)=>new(1,"sing-box","v9.0.0","SagerNet/sing-box","sing-box-v9.0.0-win-x64.zip",new string('A',64),1,"win-x64",files);
    [Fact] public void SignedComponentManifestBindsCompleteExeDllVersion()
    {
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"sing-box.exe"),"new-exe");File.WriteAllText(Path.Combine(root,"libcronet.dll"),"new-dll");
        var files=Directory.GetFiles(root).ToDictionary(p=>Path.GetFileName(p)!,p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        var signed=Sign(Manifest(files!));var verified=ComponentTrust.Verify(signed.Bytes,signed.Signature,"sing-box",signed.Key);
        ComponentTrust.VerifyFiles(root,verified);
        File.WriteAllText(Path.Combine(root,"libcronet.dll"),"old-dll");Assert.Throws<InvalidDataException>(()=>ComponentTrust.VerifyFiles(root,verified));
    }
    [Fact] public void UnsignedOrForeignPublisherManifestCannotAuthorizeRuntime()
    {
        var signed=Sign(Manifest(new(){["sing-box.exe"]=new string('A',64)}));
        Assert.Throws<InvalidDataException>(()=>ComponentTrust.Verify(signed.Bytes,signed.Signature,"sing-box"));
        signed.Bytes[^1]^=1;Assert.Throws<InvalidDataException>(()=>ComponentTrust.Verify(signed.Bytes,signed.Signature,"sing-box",signed.Key));
    }
    [Theory][InlineData("../escape.dll")][InlineData("C:/escape.dll")][InlineData("nested/../../escape.dll")]
    [InlineData("NETCAT-COMPONENT.JSON")][InlineData("NUL.dll")][InlineData("nested/COM1.txt")][InlineData("bad?.dll")]
    public void SignedManifestCannotEscapeComponentDirectory(string path)
    {
        var signed=Sign(Manifest(new(){["sing-box.exe"]=new string('A',64),[path]=new string('B',64)}));
        Assert.Throws<InvalidDataException>(()=>ComponentTrust.Verify(signed.Bytes,signed.Signature,"sing-box",signed.Key));
    }
    [Fact] public void UnsignedUpstreamUpdateRemainsExplicitlyBlocked()
    {
        var release=new ModuleRelease(
            "sing-box",
            "SagerNet/sing-box",
            "v9.0.0",
            "asset.zip",
            "https://example.test/asset.zip",
            new string('A',64));

        var check=new ModuleCheck(
            "sing-box",
            "1.0.0",
            release);

        Assert.False(check.AutoUpdateSupported);

        var row=new NetCat.UI.ModuleRow(
            check,
            false);

        Assert.False(row.CanSelectUpdate);
        Assert.False(row.Selected);

        // Candidate32 separates version availability from installability.
        // Keep this test about the security policy rather than one old
        // presentation string.
        Assert.Contains("автообновление",row.Status);
        Assert.Contains("недоступно",row.Status);

        Assert.Throws<InvalidDataException>(
            ()=>ModuleUpdater.VerifyComponentAuthorization(release));
    }
}
