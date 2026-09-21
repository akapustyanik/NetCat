using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NetCat.Network;

/// <summary>Owns the adapter handle while OpenVPN 2.6 uses its driver device.</summary>
internal sealed class WintunAdapter : IDisposable
{
    private IntPtr library, adapter;
    private CloseAdapter? close;
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)]
    private delegate IntPtr CreateAdapter([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string tunnelType, IntPtr requestedGuid);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)]
    private delegate IntPtr OpenAdapter([MarshalAs(UnmanagedType.LPWStr)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void CloseAdapter(IntPtr adapter);
    public WintunAdapter(string dll)
    {
        library = NativeLibrary.Load(Path.GetFullPath(dll));
        try
        {
            close = Marshal.GetDelegateForFunctionPointer<CloseAdapter>(NativeLibrary.GetExport(library, "WintunCloseAdapter"));
            var open = Marshal.GetDelegateForFunctionPointer<OpenAdapter>(NativeLibrary.GetExport(library, "WintunOpenAdapter"));
            adapter = open("NetCat-OpenVPN");
            if (adapter == IntPtr.Zero)
            {
                var create = Marshal.GetDelegateForFunctionPointer<CreateAdapter>(NativeLibrary.GetExport(library, "WintunCreateAdapter"));
                adapter = create("NetCat-OpenVPN", "NetCat", IntPtr.Zero);
                if (adapter == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось создать Wintun-адаптер NetCat-OpenVPN.");
            }
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        if (adapter != IntPtr.Zero) { close?.Invoke(adapter); adapter = IntPtr.Zero; }
        if (library != IntPtr.Zero) { NativeLibrary.Free(library); library = IntPtr.Zero; }
    }
}
