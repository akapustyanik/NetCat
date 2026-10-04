using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Net;

namespace NetCat.Network;
public static class LocalListener
{
    // Validate the client side of the connected loopback pair, not a PID supplied
    // by the caller over SOCKS. The peer must be the exact owned native sidecar.
    public static int ConnectedOwner(int localPort, int remotePort)
        => LookupOwner(false, localPort, remotePort);
    public static int UdpOwner(int localPort)
    {
        // Port-only diagnostics must also reject ambiguous process ownership.
        // Peer authorization uses the endpoint overload below.
        var owners=ReadUdpRows((nint p,ref int bytes)=>GetExtendedUdpTable(p,ref bytes,false,2,1,0),false)
            .Concat(ReadUdpRows((nint p,ref int bytes)=>GetExtendedUdpTable(p,ref bytes,false,23,1,0),true))
            .Where(r=>r.Port==localPort).Select(r=>r.Pid).Distinct().ToArray();
        return owners.Length==1?owners[0]:0;
    }
    public static int UdpOwner(IPEndPoint local)
        => UdpOwner(local,
            (nint p,ref int bytes)=>GetExtendedUdpTable(p,ref bytes,false,2,1,0),
            (nint p,ref int bytes)=>GetExtendedUdpTable(p,ref bytes,false,23,1,0));
    public static int UdpOwner(IPEndPoint local, TableQuery ipv4, TableQuery ipv6)
    {
        var address=local.Address.IsIPv4MappedToIPv6?local.Address.MapToIPv4():local.Address;
        var owners=ReadUdpRows(ipv4,false).Concat(ReadUdpRows(ipv6,true))
            .Where(r=>r.Port==local.Port)
            .Where(r=>
            {
                var bound=r.Address.IsIPv4MappedToIPv6?r.Address.MapToIPv4():r.Address;
                return bound.Equals(address) || bound.Equals(IPAddress.IPv6Any) ||
                    address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork && bound.Equals(IPAddress.Any);
            }).Select(r=>r.Pid).Distinct().ToArray();
        // Overlapping exact/wildcard bindings can emit the same source tuple.
        // The UDP table cannot identify which process sent a particular packet;
        // never turn table order (or the expected PID) into authorization.
        return owners.Length==1?owners[0]:0;
    }
    private static IReadOnlyList<(IPAddress Address,int Port,int Pid)> ReadUdpRows(TableQuery query,bool ipv6)
    {
        int size=0;var code=query(0,ref size);
        if(code is not (0 or 122))throw new Win32Exception((int)code);
        for(int attempt=0;attempt<5;attempt++)
        {
            if(size<4 || size>64*1024*1024)throw new IOException("Invalid UDP ownership table.");
            int allocated=size,width=ipv6?28:12;var memory=Marshal.AllocHGlobal(allocated);
            try
            {
                code=query(memory,ref size);if(code==122)continue;if(code!=0)throw new Win32Exception((int)code);
                int count=Marshal.ReadInt32(memory);
                if(count<0 || count>(allocated-4)/width)throw new IOException("Invalid UDP ownership table.");
                var rows=new List<(IPAddress Address,int Port,int Pid)>();
                for(int i=0;i<count;i++)
                {
                    var row=IntPtr.Add(memory,4+i*width);var address=new byte[ipv6?16:4];Marshal.Copy(row,address,0,address.Length);
                    int offset=ipv6?20:4,port=Marshal.ReadByte(row,offset)*256+Marshal.ReadByte(row,offset+1);
                    rows.Add((new IPAddress(address),port,Marshal.ReadInt32(row,ipv6?24:8)));
                }
                return rows;
            }
            finally{Marshal.FreeHGlobal(memory);}
        }
        throw new IOException("UDP ownership table changed repeatedly.");
    }
    private static int LookupOwner(bool udp, int localPort, int remotePort,bool ipv6=false)
    {
        int size=0;
        uint Query(nint p,ref int bytes)=>udp?GetExtendedUdpTable(p,ref bytes,false,ipv6?23:2,1,0):GetExtendedTcpTable(p,ref bytes,false,2,5,0);
        var code=Query(0,ref size);if(code is not (0 or 122))throw new Win32Exception((int)code);
        for(int attempt=0;attempt<5;attempt++)
        {
            if(size<4 || size>64*1024*1024)throw new IOException("Invalid socket table.");
            int allocated=size;var memory=Marshal.AllocHGlobal(allocated);
            try
            {
                code=Query(memory,ref size);if(code==122)continue;if(code!=0)throw new Win32Exception((int)code);
                int width=udp?(ipv6?28:12):24,count=Marshal.ReadInt32(memory);
                if(count<0 || count>(allocated-4)/width)throw new IOException("Invalid socket table.");
                for(int i=0;i<count;i++)
                {
                    var row=IntPtr.Add(memory,4+i*width);int portOffset=udp?(ipv6?20:4):8;
                    if(Marshal.ReadByte(row,portOffset)*256+Marshal.ReadByte(row,portOffset+1)!=localPort)continue;
                    if(!udp && (Marshal.ReadInt32(row)!=5 || Marshal.ReadByte(row,16)*256+Marshal.ReadByte(row,17)!=remotePort))continue;
                    return Marshal.ReadInt32(row,udp?(ipv6?24:8):20);
                }
                return 0;
            }
            finally{Marshal.FreeHGlobal(memory);}
        }
        throw new IOException("Socket ownership table changed repeatedly.");
    }
    public static int Owner(int port)
        => Owner(port, (nint table, ref int bytes) => GetExtendedTcpTable(table, ref bytes, false, 2, 3, 0));
    public delegate uint TableQuery(nint table, ref int bytes);
    public static int Owner(int port, TableQuery query)
    {
        int bytes = 0;
        var sized = query(IntPtr.Zero, ref bytes);
        if (sized is not (0 or 122)) throw new Win32Exception((int)sized);
        for (var attempt=0;attempt<5;attempt++)
        {
            if (bytes < 4 || bytes > 64 * 1024 * 1024) throw new IOException("Некорректный размер таблицы TCP.");
            var allocated=bytes;
            var memory = Marshal.AllocHGlobal(allocated);
            try
            {
            var code = query(memory, ref bytes);
            if (code == 122) continue; // Table grew between sizing and capture.
            if (code != 0) throw new Win32Exception((int)code);
            var count = Marshal.ReadInt32(memory);
            if (count < 0 || count > (allocated-4)/24) throw new IOException("Некорректная таблица TCP.");
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
        throw new IOException("Таблица TCP продолжает изменяться; повторите проверку порта.");
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
}
