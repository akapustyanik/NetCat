using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NetCat.Network;
public static class LocalListener
{
    public static int Owner(int port)
    {
        int bytes = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref bytes, false, 2, 3, 0);
        var memory = Marshal.AllocHGlobal(bytes);
        try
        {
            var code = GetExtendedTcpTable(memory, ref bytes, false, 2, 3, 0);
            if (code != 0) throw new Win32Exception((int)code);
            var count = Marshal.ReadInt32(memory);
            for (int i = 0; i < count; i++)
            {
                var row = IntPtr.Add(memory, 4 + i * 24);
                if (Marshal.ReadByte(row, 8) * 256 + Marshal.ReadByte(row, 9) == port)
                    return Marshal.ReadInt32(row, 20);
            }
            return 0;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
}
