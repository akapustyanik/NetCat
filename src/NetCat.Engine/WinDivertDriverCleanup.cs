using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace NetCat.Engine;

public enum WinDivertCleanupResult { NotInstalled, ForeignInstallation, InUse, Removed, Unavailable }
public sealed record WinDivertRegistration(string ImagePath, uint Type);
public interface IWinDivertCleanupPlatform
{
    WinDivertRegistration? ReadRegistration();
    IReadOnlyList<int> ActiveClients(string library);
    bool StopAndDelete(string expectedDriver);
}

// Only the departing network owner calls this after all owned runtimes stop.
// An explicit removal can also call it before deleting a closed installation.
public static class WinDivertDriverCleanup
{
    public static WinDivertCleanupResult TryCleanup(string modules, Action<string>? diagnostic = null)
    {
        try
        {
            using var mutex = new Mutex(false, "WinDivertDriverInstallMutex");
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromMilliseconds(250)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return WinDivertCleanupResult.Unavailable;
            try { return Cleanup(modules, new NativePlatform()); }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception error)
        {
            diagnostic?.Invoke("WINDIVERT_CLEANUP result=unavailable reason=" + error.GetType().Name);
            return WinDivertCleanupResult.Unavailable;
        }
    }

    public static WinDivertCleanupResult Cleanup(string modules, IWinDivertCleanupPlatform platform)
    {
        var registration = platform.ReadRegistration();
        if (registration == null) return WinDivertCleanupResult.NotInstalled;
        var expected = Path.GetFullPath(Path.Combine(modules, "zapret", "bin", "WinDivert64.sys"));
        if (registration.Type != 1 || !IsSameDriver(registration.ImagePath, expected))
            return WinDivertCleanupResult.ForeignInstallation;
        if (platform.ActiveClients(Path.Combine(Path.GetDirectoryName(expected)!, "WinDivert.dll")).Count != 0)
            return WinDivertCleanupResult.InUse;
        return platform.StopAndDelete(expected) ? WinDivertCleanupResult.Removed : WinDivertCleanupResult.Unavailable;
    }

    private static bool IsSameDriver(string imagePath, string expected)
    {
        var path = imagePath.Trim().Trim('"');
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return Path.IsPathFullyQualified(path) &&
            string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NativePlatform : IWinDivertCleanupPlatform
    {
        // A cleanup operation never loads arbitrary code from a mutable installation.
        private const string LibraryHash = "C1E060EE19444A259B2162F8AF0F3FE8C4428A1C6F694DCE20DE194AC8D7D9A2";
        public WinDivertRegistration? ReadRegistration()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WinDivert");
            return key == null ? null : new(key.GetValue("ImagePath") as string ?? "", Convert.ToUInt32(key.GetValue("Type") ?? 0));
        }
        public IReadOnlyList<int> ActiveClients(string library)
        {
            ModuleIntegrity.CheckPath(library);
            using var directory = OpenDirectory(Path.GetDirectoryName(library)!, 0, 3, 0, 3, 0x02000000, 0);
            if (directory.IsInvalid) throw new Win32Exception();
            using var file = new FileStream(library, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Convert.ToHexString(SHA256.HashData(file)) != LibraryHash) throw new InvalidDataException("Unknown WinDivert cleanup library.");
            var dll = LoadLibraryEx(library, 0, 0x100 | 0x800);
            if (dll == 0) throw new Win32Exception();
            try
            {
                T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(dll, name));
                var open = Export<Open>("WinDivertOpen"); var close = Export<Close>("WinDivertClose");
                var shutdown = Export<Shutdown>("WinDivertShutdown"); var receive = Export<Receive>("WinDivertRecv");
                // REFLECT only observes handle owners. NO_INSTALL never installs a driver.
                var handle = open("true", 4, 0, 0x15);
                if (handle == -1)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 1060) return [];
                    throw new Win32Exception(error);
                }
                try
                {
                    if (!shutdown(handle, 3)) throw new Win32Exception();
                    var clients = new HashSet<int>(); var address = new byte[80]; var packet = new byte[65535];
                    for (var count = 0; count < 16384; count++)
                    {
                        if (!receive(handle, packet, (uint)packet.Length, out _, address))
                        {
                            var error = Marshal.GetLastWin32Error();
                            if (error == 232) return clients.ToArray();
                            throw new Win32Exception(error);
                        }
                        if (address[9] == 8) clients.Add(BitConverter.ToInt32(address, 24));
                    }
                    throw new IOException("WinDivert owner snapshot exceeds its safe limit.");
                }
                finally { if (!close(handle)) throw new Win32Exception(); }
            }
            finally { NativeLibrary.Free(dll); }
        }
        public bool StopAndDelete(string expectedDriver)
        {
            var manager = OpenSCManager(null, null, 1);
            if (manager == 0) throw new Win32Exception();
            try
            {
                var service = OpenService(manager, "WinDivert", 1 | 4 | 0x20 | 0x10000);
                if (service == 0) throw new Win32Exception();
                try
                {
                    QueryServiceConfig(service, 0, 0, out var needed);
                    if (needed == 0 || needed > 65536) throw new Win32Exception();
                    var buffer = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        if (!QueryServiceConfig(service, buffer, needed, out _)) throw new Win32Exception();
                        var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
                        if (config.Type != 1 || !IsSameDriver(Marshal.PtrToStringUni(config.BinaryPath) ?? "", expectedDriver)) return false;
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                    if (!QueryServiceStatus(service, out var status)) throw new Win32Exception();
                    if (status.State != 1 && !ControlService(service, 1, out status))
                    {
                        if (Marshal.GetLastWin32Error() != 1062) return false;
                    }
                    var deadline = Environment.TickCount64 + 2000;
                    while (status.State != 1 && Environment.TickCount64 < deadline)
                    {
                        Thread.Sleep(20);
                        if (!QueryServiceStatus(service, out status)) throw new Win32Exception();
                    }
                    if (status.State != 1) return false;
                    return DeleteService(service) || Marshal.GetLastWin32Error() == 1072;
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi, SetLastError = true)] private delegate nint Open(string filter, int layer, short priority, ulong flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private delegate bool Close(nint handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private delegate bool Shutdown(nint handle, int how);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private delegate bool Receive(nint handle, [Out] byte[] packet, uint length, out uint received, [Out] byte[] address);
        [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint; }
        [StructLayout(LayoutKind.Sequential)] private struct ServiceConfig { public uint Type, Start, Error; public nint BinaryPath, LoadGroup; public uint Tag; public nint Dependencies, Account, DisplayName; }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle OpenDirectory(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibraryEx(string path, nint reserved, uint flags);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManager(string? machine, string? database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenService(nint manager, string name, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfig(nint service, nint buffer, uint size, out uint needed);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(nint service, out ServiceStatus status);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ControlService(nint service, uint control, out ServiceStatus status);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DeleteService(nint service);
        [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(nint handle);
    }
}
