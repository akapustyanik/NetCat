using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace NetCat.Core;

public static class WindowsExecutableTrust
{
    public static bool IsElevated
    {
        get { using var identity=WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
    }
    public static void RequireShellAssociationAllowed(bool elevated)
    {
        if(elevated) throw new InvalidOperationException("Открытие пользовательской программы из NetCat с правами администратора заблокировано. Используйте скопированный путь или ссылку в обычном приложении.");
    }
    public static IDisposable Acquire(string executable)
    {
        var system=Environment.GetFolderPath(Environment.SpecialFolder.System);
        string[] allowed=[Path.Combine(system,"WindowsPowerShell","v1.0","powershell.exe"),Path.Combine(system,"msiexec.exe"),Path.Combine(system,"schtasks.exe")];
        if(!Path.IsPathFullyQualified(executable) || !allowed.Contains(Path.GetFullPath(executable),StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Исполняемый файл не входит в доверенный список NetCat.");
        for(var path=Path.GetFullPath(executable);path!=null;path=Path.GetDirectoryName(path))
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Ссылка в пути системного исполняемого файла.");
        var lease=new FileStream(executable,FileMode.Open,FileAccess.Read,FileShare.Read);
        try
        {
            var file=new TrustFile {Size=(uint)Marshal.SizeOf<TrustFile>(),Path=executable};
            var ptr=Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());Marshal.StructureToPtr(file,ptr,false);
            try
            {
                var action=new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
                var data=new TrustData {Size=(uint)Marshal.SizeOf<TrustData>(),Ui=2,Choice=1,File=ptr,Flags=0x1000};
                var embedded=WinVerifyTrust(0,ref action,ref data)==0;
                if(!embedded && !VerifyCatalog(executable,lease))throw new InvalidDataException("Подпись системной программы не прошла проверку.");
                if(embedded && !MicrosoftPublisher(executable))
                    throw new InvalidDataException("Издатель системной программы не Microsoft.");
            }
            finally {Marshal.DestroyStructure<TrustFile>(ptr);Marshal.FreeHGlobal(ptr);}
            return lease;
        }
        catch {lease.Dispose();throw;}
    }
    private static bool MicrosoftPublisher(string path)
    {
        using var certificate=new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
        return certificate.Subject.Split(',').Any(part=>part.Trim()=="O=Microsoft Corporation");
    }
    private static bool VerifyCatalog(string executable,FileStream lease)
    {
        // Windows inbox executables often use catalog signatures, not embedded
        // signatures. Verify actual file membership as well as the catalog signer.
        if(!CryptCATAdminAcquireContext2(out var admin,0,"SHA256",0,0))return false;
        try
        {
            uint length=0;
            if(!CryptCATAdminCalcHashFromFileHandle2(admin,lease.SafeFileHandle.DangerousGetHandle(),ref length,null,0))return false;
            var hash=new byte[length];
            if(!CryptCATAdminCalcHashFromFileHandle2(admin,lease.SafeFileHandle.DangerousGetHandle(),ref length,hash,0))return false;
            var catalog=CryptCATAdminEnumCatalogFromHash(admin,hash,length,0,0);
            if(catalog==0)return false;
            try
            {
                var info=new CatalogInfo {Size=(uint)Marshal.SizeOf<CatalogInfo>(),Path=""};
                if(!CryptCATCatalogInfoFromContext(catalog,ref info,0) || !MicrosoftPublisher(info.Path))return false;
                var member=new CatalogMember {Size=(uint)Marshal.SizeOf<CatalogMember>(),Catalog=info.Path,Tag=Convert.ToHexString(hash),Path=executable,Handle=lease.SafeFileHandle.DangerousGetHandle(),Admin=admin};
                var ptr=Marshal.AllocHGlobal(Marshal.SizeOf<CatalogMember>());Marshal.StructureToPtr(member,ptr,false);
                try
                {
                    var action=new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
                    var data=new TrustData {Size=(uint)Marshal.SizeOf<TrustData>(),Ui=2,Choice=2,File=ptr,Flags=0x1000};
                    return WinVerifyTrust(0,ref action,ref data)==0;
                }
                finally {Marshal.DestroyStructure<CatalogMember>(ptr);Marshal.FreeHGlobal(ptr);}
            }
            finally {CryptCATAdminReleaseCatalogContext(admin,catalog,0);}
        }
        finally {CryptCATAdminReleaseContext(admin,0);}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct CatalogInfo {public uint Size;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Path;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct CatalogMember {public uint Size,Version;[MarshalAs(UnmanagedType.LPWStr)]public string Catalog;[MarshalAs(UnmanagedType.LPWStr)]public string Tag;[MarshalAs(UnmanagedType.LPWStr)]public string Path;public nint Handle,Hash;public uint HashLength;public nint Context,Admin;}
    [DllImport("wintrust.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern bool CryptCATAdminAcquireContext2(out nint admin,nint subsystem,string algorithm,nint policy,uint flags);
    [DllImport("wintrust.dll",ExactSpelling=true)] private static extern bool CryptCATAdminCalcHashFromFileHandle2(nint admin,nint file,ref uint size,[Out]byte[]? hash,uint flags);
    [DllImport("wintrust.dll",ExactSpelling=true)] private static extern nint CryptCATAdminEnumCatalogFromHash(nint admin,byte[] hash,uint length,uint flags,nint previous);
    [DllImport("wintrust.dll",ExactSpelling=true)] private static extern bool CryptCATCatalogInfoFromContext(nint catalog,ref CatalogInfo info,uint flags);
    [DllImport("wintrust.dll",ExactSpelling=true)] private static extern bool CryptCATAdminReleaseCatalogContext(nint admin,nint catalog,uint flags);
    [DllImport("wintrust.dll",ExactSpelling=true)] private static extern bool CryptCATAdminReleaseContext(nint admin,uint flags);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct TrustFile {public uint Size;[MarshalAs(UnmanagedType.LPWStr)]public string Path;public nint Handle,Subject;}
    [StructLayout(LayoutKind.Sequential)] private struct TrustData {public uint Size;public nint Policy,Sip;public uint Ui,Revocation,Choice;public nint File;public uint StateAction;public nint State,Url;public uint Flags,Context;}
    [DllImport("wintrust.dll",ExactSpelling=true)] private static extern int WinVerifyTrust(nint hwnd,ref Guid action,ref TrustData data);
}
