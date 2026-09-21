using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using NetCat.Core;

namespace NetCat.Engine;

// Reads the public GeoSiteList / GeoIPList protobuf format. Only selected categories
// are expanded; the resulting native sing-box matches also work with an Xray bridge.
public sealed class Geodata
{
    private readonly Dictionary<string, ReadOnlyMemory<byte>> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool ip;
    public Geodata(string path, bool ip)
    {
        this.ip = ip;
        if (!File.Exists(path)) throw new FileNotFoundException("Установите базу " + (ip ? "GeoIP" : "GeoSite") + " на вкладке «Модули».");
        if (new FileInfo(path).Length is 0 or > 134217728) throw new InvalidDataException("Недопустимый размер базы геоданных.");
        foreach (var field in Fields(File.ReadAllBytes(path)))
        {
            if (field.Number != 1 || field.Wire != 2) continue;
            var code = Fields(field.Data).FirstOrDefault(f => f.Number == 1 && f.Wire == 2).Text;
            if (string.IsNullOrWhiteSpace(code) || !entries.TryAdd(code, field.Data)) throw new InvalidDataException("Повреждён каталог геоданных.");
        }
        if (entries.Count == 0) throw new InvalidDataException("База геоданных пуста.");
    }
    public static string FilePath(string modules, RuleKind kind) => Path.Combine(modules, kind == RuleKind.GeoIp ? "geoip" : "geosite", kind == RuleKind.GeoIp ? "geoip.dat" : "geosite.dat");
    public string[] Categories => entries.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    public JsonObject Match(string category)
    {
        var parts = category.Split('@');
        if (!entries.TryGetValue(parts[0], out var entry)) throw new InvalidDataException("Категория " + category + " отсутствует в " + (ip ? "GeoIP" : "GeoSite") + ".");
        var values = new Dictionary<string, List<string>>(); bool invert = false;
        void Add(string key, string value) { if (!values.TryGetValue(key, out var list)) values[key] = list = []; list.Add(value); }
        foreach (var field in Fields(entry))
        {
            if (ip && field.Number == 3 && field.Wire == 0) invert = field.NumberValue != 0;
            if (field.Number != 2 || field.Wire != 2) continue;
            var item = Fields(field.Data).ToArray();
            if (ip)
            {
                var address = item.FirstOrDefault(f => f.Number == 1 && f.Wire == 2).Data;
                var prefix = item.FirstOrDefault(f => f.Number == 2 && f.Wire == 0).NumberValue;
                if (address.Length is not (4 or 16) || prefix > (ulong)address.Length * 8) throw new InvalidDataException("Некорректная подсеть GeoIP.");
                Add("ip_cidr", new IPAddress(address.Span) + "/" + prefix);
            }
            else
            {
                var attributes = item.Where(f => f.Number == 3 && f.Wire == 2).Select(f => Fields(f.Data).FirstOrDefault(a => a.Number == 1 && a.Wire == 2).Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (parts.Skip(1).Any(a => !attributes.Contains(a))) continue;
                var type = item.FirstOrDefault(f => f.Number == 1 && f.Wire == 0).NumberValue;
                var value = item.FirstOrDefault(f => f.Number == 2 && f.Wire == 2).Text;
                if (string.IsNullOrEmpty(value)) throw new InvalidDataException("Пустой домен GeoSite.");
                Add(type switch { 0 => "domain_keyword", 1 => "domain_regex", 2 => "domain_suffix", 3 => "domain", _ => throw new InvalidDataException("Неизвестный тип домена GeoSite.") }, value);
            }
        }
        if (values.Count == 0) throw new InvalidDataException("Категория " + category + " не содержит подходящих записей.");
        var result = new JsonObject();
        foreach (var pair in values) result[pair.Key] = SingBoxConfig.Array(pair.Value.Distinct());
        if (invert) result["invert"] = true;
        return result;
    }
    public bool Contains(string category, string value)
    {
        var match = Match(category);
        bool any = match.Any(pair => pair.Value is JsonArray list && list.Any(n => pair.Key switch
        {
            "ip_cidr" => RuleValidation.MatchesCidr(value, n!.ToString()),
            "domain" => value.Equals(n!.ToString(), StringComparison.OrdinalIgnoreCase),
            "domain_suffix" => value.Equals(n!.ToString(), StringComparison.OrdinalIgnoreCase) || value.EndsWith("." + n!.ToString(), StringComparison.OrdinalIgnoreCase),
            "domain_keyword" => value.Contains(n!.ToString(), StringComparison.OrdinalIgnoreCase),
            "domain_regex" => System.Text.RegularExpressions.Regex.IsMatch(value, n!.ToString(), System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100)),
            _ => false
        }));
        return match["invert"]?.GetValue<bool>() == true ? !any : any;
    }
    private readonly record struct Field(int Number, int Wire, ulong NumberValue, ReadOnlyMemory<byte> Data)
    { public string Text => Encoding.UTF8.GetString(Data.Span); }
    private static IEnumerable<Field> Fields(ReadOnlyMemory<byte> data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            ulong tag = Varint(data, ref offset); int wire = (int)(tag & 7);
            if (tag >> 3 is 0 or > 536870911) throw new InvalidDataException("Повреждён protobuf геоданных.");
            if (wire == 0) { yield return new((int)(tag >> 3), wire, Varint(data, ref offset), default); continue; }
            ulong length = wire switch { 1 => 8, 2 => Varint(data, ref offset), 5 => 4, _ => throw new InvalidDataException("Неизвестный формат геоданных.") };
            if (length > (ulong)(data.Length - offset)) throw new InvalidDataException("Обрезана база геоданных.");
            yield return new((int)(tag >> 3), wire, 0, data.Slice(offset, (int)length)); offset += (int)length;
        }
    }
    private static ulong Varint(ReadOnlyMemory<byte> data, ref int offset)
    {
        ulong value = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (offset >= data.Length) throw new InvalidDataException("Обрезан protobuf геоданных.");
            byte b = data.Span[offset++];
            if (shift == 63 && b > 1) throw new InvalidDataException("Переполнение protobuf геоданных.");
            value |= (ulong)(b & 127) << shift; if (b < 128) return value;
        }
        throw new InvalidDataException("Повреждён protobuf геоданных.");
    }
}
