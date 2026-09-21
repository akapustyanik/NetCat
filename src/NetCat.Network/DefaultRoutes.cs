using System.Runtime.InteropServices;
namespace NetCat.Network;
public static class DefaultRoutes
{
    [DllImport("iphlpapi.dll")] private static extern uint GetIpForwardTable(nint table, ref int size, bool sorted);
    [DllImport("iphlpapi.dll")] private static extern int GetIpForwardTable2(ushort family, out nint table);
    [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(nint table);

    public static bool HasIpv6DefaultRoute(int ifIndex)
    {
        if (ifIndex <= 0) return false;
        int err = GetIpForwardTable2(23, out nint table); // AF_INET6 = 23
        if (err != 0 || table == 0) return false;
        try
        {
            int count = Marshal.ReadInt32(table);
            nint row = table + 8;
            const int rowSize = 104;
            for (int i = 0; i < count; i++)
            {
                int rowIfIndex = Marshal.ReadInt32(row + 8);
                short family = Marshal.ReadInt16(row + 12);
                byte prefixLength = Marshal.ReadByte(row + 40);
                if (rowIfIndex == ifIndex && prefixLength == 0 && family == 23) return true;
                row += rowSize;
            }
            return false;
        }
        catch { return false; }
        finally { FreeMibTable(table); }
    }
    public static Dictionary<int,uint> Metrics()
    {
        int size=0; GetIpForwardTable(0,ref size,false);
        for(int attempt=0;attempt<3;attempt++)
        {
            if(size is < 4 or > 67108864) throw new IOException("Некорректная таблица маршрутов Windows.");
            var memory=Marshal.AllocHGlobal(size); int capacity=size;
            try
            {
                uint result=GetIpForwardTable(memory,ref size,false);
                if(result==122) continue;
                if(result!=0) throw new IOException("Не удалось прочитать маршруты Windows: "+result);
                int count=Marshal.ReadInt32(memory);
                if(count<0 || count>(capacity-4)/56) throw new IOException("Повреждена таблица маршрутов.");
                var metrics=new Dictionary<int,uint>();
                for(int i=0;i<count;i++)
                {
                    var row=memory+4+i*56;
                    if(Marshal.ReadInt32(row)!=0 || Marshal.ReadInt32(row,4)!=0 || Marshal.ReadInt32(row,20)==2) continue;
                    int index=Marshal.ReadInt32(row,16); uint metric=unchecked((uint)Marshal.ReadInt32(row,36));
                    // On modern Windows this field includes route + interface metric.
                    if(!metrics.TryGetValue(index,out var old) || metric<old) metrics[index]=metric;
                }
                return metrics;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        throw new IOException("Маршруты Windows изменяются; повторите подключение.");
    }
    public static int Select(IEnumerable<int> physical, IReadOnlyDictionary<int,uint> metrics)
        => physical.Where(metrics.ContainsKey).OrderBy(i=>metrics[i]).ThenBy(i=>i).DefaultIfEmpty(-1).First();
}
