using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
namespace NetCat.Updater;
public static class PublisherTrust
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct FileInfo { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public nint Handle,Subject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData { public uint Size;public nint Policy,Sip;public uint Ui,Revocation,Choice;public nint File;public uint StateAction;public nint State,Url;public uint Flags,Context; }
    [DllImport("wintrust.dll",ExactSpelling=true)] private static extern int WinVerifyTrust(nint hwnd,ref Guid action,ref TrustData data);
    public static bool IsTrusted(string path)
    {
        var file=new FileInfo { Size=(uint)Marshal.SizeOf<FileInfo>(),Path=path };var ptr=Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());Marshal.StructureToPtr(file,ptr,false);
        var action=new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        var data=new TrustData { Size=(uint)Marshal.SizeOf<TrustData>(),Ui=2,Choice=1,File=ptr,Flags=0x1000 };
        try { return WinVerifyTrust(0,ref action,ref data)==0; } finally { Marshal.DestroyStructure<FileInfo>(ptr);Marshal.FreeHGlobal(ptr); }
    }
    public static void RequireSamePublisher(string current,string next)
    {
        try { using var certificate=X509Certificate.CreateFromSignedFile(current); }
        catch(System.Security.Cryptography.CryptographicException) { throw new InvalidDataException("Самообновление NetCat требует подписанную сборку. Для неподписанного тестового EXE используйте новую тестовую папку."); }
        if(!IsTrusted(current))throw new InvalidDataException("Подпись установленной программы не прошла проверку. Автообновление остановлено.");
        if(!IsTrusted(next))throw new InvalidDataException("Обновление подписанной программы должно иметь доверенную подпись.");
        using var oldCert=new X509Certificate2(X509Certificate.CreateFromSignedFile(current));using var newCert=new X509Certificate2(X509Certificate.CreateFromSignedFile(next));
        RequireIdentity(true,true,oldCert.GetPublicKeyString(),newCert.GetPublicKeyString());
    }
    public static void RequireIdentity(bool currentTrusted,bool nextTrusted,string currentKey,string nextKey)
    {
        if(!currentTrusted || !nextTrusted || string.IsNullOrEmpty(currentKey) || !currentKey.Equals(nextKey,StringComparison.Ordinal))
            throw new InvalidDataException("Обновление требует доверенную подпись того же издателя.");
    }
}
