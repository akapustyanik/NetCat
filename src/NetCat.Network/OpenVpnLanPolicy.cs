using System.Buffers.Binary;
using System.Net.Sockets;
namespace NetCat.Network;

public static class OpenVpnLanPolicy
{
    public static bool Overlaps(string first,string second)
    {
        static (uint Value,int Bits) Parse(string prefix)
        {
            var parts=prefix.Split('/');if(parts.Length!=2 || !int.TryParse(parts[1],out var bits) || bits is <0 or >32)throw new InvalidDataException("Некорректный IPv4 prefix");
            return (BinaryPrimitives.ReadUInt32BigEndian(OpenVpnPushParser.Ipv4(parts[0]).GetAddressBytes()),bits);
        }
        var a=Parse(first);var b=Parse(second);int length=Math.Min(a.Bits,b.Bits);uint mask=length==0?0:uint.MaxValue<<(32-length);
        return (a.Value&mask)==(b.Value&mask);
    }
    public static string[] PhysicalConnectedPrefixes() => PhysicalNetwork.Adapters().SelectMany(n=>n.GetIPProperties().UnicastAddresses)
        .Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork)
        .Select(a=>a.Address+"/"+a.PrefixLength).ToArray();
    public static void Validate(IEnumerable<string> corporate,IEnumerable<string> physical)
    {
        var connected=physical.ToArray();
        foreach(var prefix in corporate)
            if(connected.FirstOrDefault(p=>Overlaps(prefix,p)) is {} conflict)
                throw new InvalidDataException($"Конфликт OpenVPN и локальной сети: {prefix} пересекается с {conflict}. Всё подключение OpenVPN отклонено; уточните подсети у администратора. Правило «Напрямую» не отменяет эту проверку.");
    }
}
