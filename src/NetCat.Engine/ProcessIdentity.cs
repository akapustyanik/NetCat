using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NetCat.Engine;

public static class ProcessIdentity
{
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint process, int informationClass, nint buffer, int length, out int needed);
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public nint Buffer; }

    // Query the opened process handle, never WMI name/path heuristics. Access failure is unknown.
    public static string? CommandFingerprint(Process process)
    {
        try
        {
            var handle = process.SafeHandle.DangerousGetHandle();
            NtQueryInformationProcess(handle, 60, 0, 0, out var needed);
            if (needed <= 0 || needed > 1024 * 1024) return null;
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (NtQueryInformationProcess(handle, 60, buffer, needed, out _) != 0) return null;
                var value = Marshal.PtrToStructure<UnicodeString>(buffer);
                if (value.Buffer == 0 || value.Length == 0) return null;
                var command = Marshal.PtrToStringUni(value.Buffer, value.Length / 2);
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command!)));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { return null; }
    }
}
