using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using NetCat.Core;

namespace NetCat.Engine;

// Local approval is not a publisher signature. An elevated NetCat may only
// consume approvals whose owner and write permissions were already privileged
// BEFORE reading them. Tightening an attacker's ACL after reading is too late.
public static class RuntimeTrustReceipt
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);

    public static void RequireAdministratorControl(FileSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        const FileSystemRights mutable = FileSystemRights.Write | FileSystemRights.Delete |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        if (!(Administrators.Equals(owner) || System.Equals(owner)) || descriptor.DiscretionaryAcl == null ||
            security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(rule =>
                rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & mutable) != 0 &&
                !Administrators.Equals(rule.IdentityReference) && !System.Equals(rule.IdentityReference)))
            throw new InvalidDataException("Разрешение модуля доступно для изменения без прав администратора. Повторно установите модуль из проверенного источника.");
    }

    public static string Read(string path)
    {
        ModuleIntegrity.CheckPath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (WindowsExecutableTrust.IsElevated) RequireAdministratorControl(file.GetAccessControl());
        if (file.Length > 8 * 1024 * 1024) throw new InvalidDataException("Слишком большой файл разрешения модуля.");
        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }

    public static void Write(string path, string json)
    {
        ModuleIntegrity.CheckPath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            FileSecurity? security = null;
            if (WindowsExecutableTrust.IsElevated)
            {
                security = new FileSecurity();
                security.SetOwner(Administrators);
                security.SetAccessRuleProtection(true, false);
                foreach (var sid in new[] { Administrators, System })
                    security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            }
            // Apply the privileged descriptor at creation, not after writing.
            using (var file = security == null
                ? new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                : new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                    FileShare.None, 4096, FileOptions.WriteThrough, security))
            {
                file.Write(Encoding.UTF8.GetBytes(json));
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
