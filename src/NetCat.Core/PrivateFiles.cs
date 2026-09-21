using System.Security.AccessControl;
using System.Security.Principal;
namespace NetCat.Core;
public static class PrivateFiles
{
    public static void ProtectDirectory(string path, bool administratorsOnly = false)
    {
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Недопустимая ссылка в папке приватных данных.");
        var owner = administratorsOnly ? new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null) : WindowsIdentity.GetCurrent().User!;
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true,false);
        if(administratorsOnly) acl.SetOwner(owner);
        foreach(var sid in new[]{owner,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})
            acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
    public static void DeleteSecrets(string folder, params string[] names)
    {
        if(Directory.Exists(folder) && (File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0) throw new IOException("Ссылка вместо папки приватных данных.");
        foreach(var name in names) { var path=Path.Combine(folder,name); if(File.Exists(path)) File.Delete(path); }
    }
}
