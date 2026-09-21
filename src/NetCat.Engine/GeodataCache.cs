namespace NetCat.Engine;

public sealed class GeodataCache
{
    public static GeodataCache Shared { get; } = new();
    private sealed record Entry(long Length,DateTime Written,Geodata Data);
    private readonly Dictionary<(string Path,bool Ip),Entry> entries = new();
    private readonly object gate=new();
    public int ParseCount { get; private set; }
    public Geodata Get(string path,bool ip)
    {
        var full=Path.GetFullPath(path); var key=(full.ToUpperInvariant(),ip);
        lock(gate)
        {
            for(int attempt=0;attempt<3;attempt++)
            {
                var info=new FileInfo(full);
                if(!info.Exists) {entries.Remove(key);throw new FileNotFoundException("Установите базу "+(ip?"GeoIP":"GeoSite")+" на вкладке «Модули».",full);}
                var length=info.Length; var written=info.LastWriteTimeUtc;
                if(entries.TryGetValue(key,out var entry) && entry.Length==length && entry.Written==written) return entry.Data;
                entries.Remove(key); // Release the old database, including its backing protobuf buffer.
                var data=new Geodata(full,ip); ParseCount++;
                info.Refresh(); if(!info.Exists || info.Length!=length || info.LastWriteTimeUtc!=written) continue;
                // Bound memory when several portable installations are inspected in one process.
                if(entries.Count>=8) entries.Remove(entries.Keys.First());
                entries[key]=new(length,written,data); return data;
            }
            throw new IOException("База геоданных изменяется во время чтения. Повторите операцию.");
        }
    }
}
