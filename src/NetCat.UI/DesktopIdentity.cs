using System.Runtime.InteropServices;
using System.Security.Principal;
namespace NetCat.UI;
internal static class DesktopIdentity
{
    [DllImport("wtsapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool WTSQuerySessionInformation(nint server,int session,int kind,out nint value,out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint value);
    private static string Read(int kind)
    {
        if(!WTSQuerySessionInformation(0,-1,kind,out var value,out _))return "";
        try{return Marshal.PtrToStringUni(value) ?? "";}finally{WTSFreeMemory(value);}
    }
    public static string Warning()
    {
        var user=Read(5);var domain=Read(7);if(user.Length==0)return "";
        try
        {
            var sid=(SecurityIdentifier)new NTAccount(domain,user).Translate(typeof(SecurityIdentifier));
            using var current=WindowsIdentity.GetCurrent();
            return sid==current.User?"":"NetCat запущен от другого пользователя Windows. Настройки и DPAPI-профили принадлежат этой учётной записи, а не пользователю рабочего стола.";
        }
        catch(IdentityNotMappedException){return "";}
        catch(System.Security.SecurityException){return "";}
    }
}
