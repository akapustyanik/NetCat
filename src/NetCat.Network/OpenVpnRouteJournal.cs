using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NetCat.Core;

namespace NetCat.Network;

public sealed record OpenVpnRouteJournal(int Schema, Guid? ProfileId, long Generation, int InterfaceIndex,
    string Gateway, string[] Prefixes, bool HelperDefault, DateTimeOffset UpdatedAt)
{
    public const uint OwnedMetric = 7777;
    public uint Metric { get; init; } = OwnedMetric;
    public Guid RouteOwnerId { get; init; }
    public string[] DnsPrefixes { get; init; } = [];
    public string[] DestinationPrefixes { get; init; } = [];
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,Task> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static SemaphoreSlim Gate(string path) => Gates.GetOrAdd(Path.GetFullPath(path), _ => new(1));
    private static async Task AwaitPendingAsync(string path)
    {
        var key=Path.GetFullPath(path);
        if(!Pending.TryGetValue(key,out var task))return;
        try { await task.WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false); }
        catch when(task.IsCompleted) { } // The caller already received the failure.
        if(!task.IsCompleted)throw new IOException("Previous route mutation has not terminated; journal retained.");
        Pending.TryRemove(key,out _);
    }
    private static async Task<(int Code,string Output)> MutateAsync(string path,
        Func<string,CancellationToken,Task<(int Code,string Output)>> run,string command,CancellationToken ct)
    {
        await AwaitPendingAsync(path).ConfigureAwait(false);
        var key=Path.GetFullPath(path);Task? operation=null;
        try{return await RunBounded((s,token)=>{var task=run(s,token);operation=task;Pending[key]=task;return task;},command,ct).ConfigureAwait(false);}
        finally{if(operation?.IsCompleted==true)Pending.TryRemove(key,out _);}
    }
    public static OpenVpnRouteJournal Empty(OpenVpnLink link) => new(4, link.ProfileId, link.Generation, link.Index, link.Gateway, [], false, DateTimeOffset.UtcNow){RouteOwnerId=link.RouteOwnerId};
    public void Validate()
    {
        if (Schema is not (3 or 4) || Schema == 4 && HelperDefault || Metric != OwnedMetric || InterfaceIndex <= 0 || Generation < 0 ||
            !IPAddress.TryParse(Gateway, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            Prefixes == null || Prefixes.Any(p => !OpenVpnRoutes.ValidatePushedRoute(p, true)) ||
            DnsPrefixes == null || DestinationPrefixes == null || DnsPrefixes.Concat(DestinationPrefixes).Any(p => !p.EndsWith("/32",StringComparison.Ordinal) || !OpenVpnRoutes.ValidatePushedRoute(p,true)))
            throw new InvalidDataException("Недопустимый журнал маршрутов OpenVPN; автоматическая очистка отменена.");
    }
    public static async Task<OpenVpnRouteJournal?> ReadAsync(string path)
    {
        if (!File.Exists(path)) return null;
        CheckPath(path);
        var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        try
        {
            var record = JsonSerializer.Deserialize<OpenVpnRouteJournal>(text) ?? throw new InvalidDataException("Пустой журнал OpenVPN.");
            record.Validate(); return record;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            // Preserve the original to keep startup fail-closed until an operator
            // resolves ownership. Quarantine is a copy, never guessed cleanup.
            var quarantine = path + ".corrupt"; CheckPath(quarantine);
            if (!File.Exists(quarantine)) File.Copy(path, quarantine);
            const string diagnostic = "Журнал OpenVPN повреждён или не содержит точной метрики (старая схема). Сохранена копия .corrupt; требуется проверка собственных маршрутов перед удалением журнала.";
            if (error is JsonException) throw new JsonException(diagnostic, error);
            throw new InvalidDataException(diagnostic, error);
        }
    }
    public async Task SaveAsync(string path, CancellationToken ct)
    {
        Validate(); CheckPath(path); CheckPath(path + ".next");
        await using (var stream = new FileStream(path + ".next", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, this, cancellationToken: ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(path + ".next", path, true);
    }
    public static void CheckPath(string path)
    {
        for (var p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Ссылка в приватном пути OpenVPN.");
    }

    // Journal intent before EACH mutation. A partial install or cancellation is
    // recoverable even if NetCat dies before verification/publishing the link.
    public static async Task InstallAsync(string path, OpenVpnLink link,
        Func<IReadOnlyList<RouteRow>> capture,
        Func<string, CancellationToken, Task<(int Code, string Output)>> run, CancellationToken ct)
    {
        var gate=Gate(path);await gate.WaitAsync(ct).ConfigureAwait(false);
        try { await InstallCoreAsync(path,link,capture,run,ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    public static bool SameGeneration(OpenVpnRouteJournal record, OpenVpnLink link) =>
        record.ProfileId==link.ProfileId && record.Generation==link.Generation && record.RouteOwnerId==link.RouteOwnerId && record.InterfaceIndex==link.Index && record.Gateway==link.Gateway;
    public static bool Covers(IEnumerable<string> prefixes, IPAddress address)
    {
        if(address.AddressFamily!=AddressFamily.InterNetwork)return false;
        uint value=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        return prefixes.Any(p=>{
            var parts=p.Split('/');var ip=IPAddress.Parse(parts[0]);var bits=int.Parse(parts[1]);
            uint mask=bits==0?0:uint.MaxValue<<(32-bits);
            return (value&mask)==(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes())&mask);
        });
    }
    public static string[] RequiredDnsPrefixes(OpenVpnLink link) =>
        IPAddress.TryParse(link.Dns,out var dns) && dns.AddressFamily==AddressFamily.InterNetwork && !Covers(link.LearnedRoutes,dns) ? [dns+"/32"] : [];
    private static async Task InstallCoreAsync(string path, OpenVpnLink link,
        Func<IReadOnlyList<RouteRow>> capture, Func<string,CancellationToken,Task<(int Code,string Output)>> run, CancellationToken ct)
    {
        await AwaitPendingAsync(path).ConfigureAwait(false);
        var prior = await ReadAsync(path).ConfigureAwait(false);
        if (prior != null && !SameGeneration(prior,link))
            throw new InvalidOperationException("Прежнее поколение маршрутов OpenVPN ещё не очищено.");
        var journal = prior ?? Empty(link);
        if(prior==null)await journal.SaveAsync(path, ct).ConfigureAwait(false);
        try
        {
            foreach (var prefix in link.LearnedRoutes.Concat(RequiredDnsPrefixes(link)).Distinct())
            {
                if (!OpenVpnRoutes.ValidatePushedRoute(prefix, true)) throw new InvalidDataException("Недопустимый маршрут OpenVPN.");
                if (capture().Any(r => Matches(r, link, prefix, OwnedMetric))) continue;
                if (capture().Any(r => r.InterfaceIndex == link.Index && r.DestinationPrefix == prefix))
                    throw new IOException("Конфликт с существующим маршрутом OpenVPN: " + prefix + ". Чужой маршрут не изменён.");
                var before=journal;
                journal = RequiredDnsPrefixes(link).Contains(prefix)
                    ? journal with { DnsPrefixes=journal.DnsPrefixes.Append(prefix).Distinct().ToArray() }
                    : journal with { Prefixes = journal.Prefixes.Append(prefix).Distinct().ToArray() };
                await journal.SaveAsync(path, ct).ConfigureAwait(false);
                var result = await MutateAsync(path,run, $"New-NetRoute -InterfaceIndex {link.Index} -DestinationPrefix {PhysicalNetwork.Literal(prefix)} -NextHop {PhysicalNetwork.Literal(link.Gateway)} -RouteMetric {OwnedMetric} -PolicyStore ActiveStore -ErrorAction Stop | Out-Null", ct).ConfigureAwait(false);
                if(result.Code!=0) { journal=before;await journal.SaveAsync(path,CancellationToken.None).ConfigureAwait(false); }
                if (result.Code != 0 || !capture().Any(r => Matches(r, link, prefix, OwnedMetric)))
                    throw new IOException("Не удалось установить/подтвердить маршрут OpenVPN: " + prefix);
            }
        }
        catch { await CleanupCoreAsync(path, run).ConfigureAwait(false); throw; }
    }
    public static async Task CleanupAsync(string path, Func<string, CancellationToken, Task<(int Code, string Output)>> run, Func<IReadOnlyList<RouteRow>>? capture=null)
    {
        var gate=Gate(path);await gate.WaitAsync().ConfigureAwait(false);
        try { await CleanupCoreAsync(path,run,capture).ConfigureAwait(false); } finally {gate.Release();}
    }
    private static string RemovalCommand(int index,string prefix,string gateway,uint metric)
        // Filtered CIM queries return exit 1 for an already absent route even
        // with SilentlyContinue. Enumerate successfully first, then match the
        // exact owned tuple. Real enumeration/removal errors remain terminating.
        => $"Get-NetRoute -PolicyStore ActiveStore -ErrorAction Stop | Where-Object {{ $_.InterfaceIndex -eq {index} -and $_.DestinationPrefix -eq {PhysicalNetwork.Literal(prefix)} -and $_.NextHop -eq {PhysicalNetwork.Literal(gateway)} -and $_.RouteMetric -eq {metric} }} | Remove-NetRoute -Confirm:$false -ErrorAction Stop";
    private static async Task CleanupCoreAsync(string path, Func<string, CancellationToken, Task<(int Code, string Output)>> run, Func<IReadOnlyList<RouteRow>>? capture=null)
    {
        await AwaitPendingAsync(path).ConfigureAwait(false);
        var journal = await ReadAsync(path).ConfigureAwait(false); if (journal == null) return;
        foreach (var prefix in journal.Prefixes.Concat(journal.DnsPrefixes).Concat(journal.DestinationPrefixes).Concat(journal.HelperDefault ? ["0.0.0.0/0"] : Array.Empty<string>()).Distinct())
        {
            var metric = prefix == "0.0.0.0/0" ? 9999u : journal.Metric;
            // An absent route yields an empty pipeline. A failed removal must
            // retain intent even when the caller cannot capture the route table.
            var result = await MutateAsync(path,run, RemovalCommand(journal.InterfaceIndex,prefix,journal.Gateway,metric), CancellationToken.None).ConfigureAwait(false);
            if (result.Code != 0) throw new IOException("Не удалось удалить собственный маршрут OpenVPN; журнал сохранён.");
            if(capture?.Invoke().Any(r=>r.InterfaceIndex==journal.InterfaceIndex && r.DestinationPrefix==prefix && r.NextHop==journal.Gateway && r.RouteMetric==metric)==true)
                throw new IOException("Удаление собственного маршрута OpenVPN не подтверждено; журнал сохранён.");
        }
        File.Delete(path);
    }
    public static async Task EnsureDestinationAsync(string path,OpenVpnLink link,IPAddress address,Func<bool> current,
        Func<IReadOnlyList<RouteRow>> capture,Func<string,CancellationToken,Task<(int Code,string Output)>> run,CancellationToken ct)
    {
        var gate=Gate(path);await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await AwaitPendingAsync(path).ConfigureAwait(false);
            var journal=await ReadAsync(path).ConfigureAwait(false);
            if(journal==null || !SameGeneration(journal,link) || !current())throw new OperationCanceledException("Obsolete route lease.");
            if(Covers(link.LearnedRoutes,address))
            {
                if(!link.LearnedRoutes.Where(p=>Covers([p],address)).Any(p=>capture().Any(r=>Matches(r,link,p,OwnedMetric))))throw new IOException("Pushed route missing.");
                return;
            }
            var prefix=address+"/32";
            if(!OpenVpnRoutes.ValidatePushedRoute(prefix,true))throw new IOException("Invalid destination route.");
            if(capture().Any(r=>Matches(r,link,prefix,OwnedMetric)))return; // Never adopt a pre-existing route.
            if(capture().Any(r=>r.InterfaceIndex==link.Index && r.DestinationPrefix==prefix))throw new IOException("Foreign destination route conflict.");
            var before=journal;
            journal=journal with{DestinationPrefixes=journal.DestinationPrefixes.Append(prefix).Distinct().ToArray()};
            await journal.SaveAsync(path,ct).ConfigureAwait(false);
            (int Code,string Output) result;
            try{result=await MutateAsync(path,run,$"New-NetRoute -InterfaceIndex {link.Index} -DestinationPrefix {PhysicalNetwork.Literal(prefix)} -NextHop {PhysicalNetwork.Literal(link.Gateway)} -RouteMetric {OwnedMetric} -PolicyStore ActiveStore -ErrorAction Stop | Out-Null",ct).ConfigureAwait(false);}
            catch
            {
                // Do not race a canceled PowerShell child. If termination cannot
                // yet be confirmed, retain the intent and block later mutations.
                await AwaitPendingAsync(path).ConfigureAwait(false);
                await RemoveDestinationCoreAsync(path,journal,link,prefix,run).ConfigureAwait(false);throw;
            }
            if(result.Code!=0){await before.SaveAsync(path,CancellationToken.None).ConfigureAwait(false);throw new IOException("Destination route installation failed.");}
            if(!current() || ct.IsCancellationRequested || !capture().Any(r=>Matches(r,link,prefix,OwnedMetric)))
            {await RemoveDestinationCoreAsync(path,journal,link,prefix,run).ConfigureAwait(false);throw new OperationCanceledException("Route lease not current/verified.");}
        }
        finally{gate.Release();}
    }
    public static async Task ReleaseDestinationAsync(string path,OpenVpnLink link,IPAddress address,Func<string,CancellationToken,Task<(int Code,string Output)>> run,Func<bool>? current=null)
    {
        var gate=Gate(path);await gate.WaitAsync().ConfigureAwait(false);
        try{await AwaitPendingAsync(path).ConfigureAwait(false);var journal=await ReadAsync(path).ConfigureAwait(false);if(journal!=null && SameGeneration(journal,link) && current?.Invoke()!=false)await RemoveDestinationCoreAsync(path,journal,link,address+"/32",run).ConfigureAwait(false);}
        finally{gate.Release();}
    }
    private static async Task RemoveDestinationCoreAsync(string path,OpenVpnRouteJournal journal,OpenVpnLink link,string prefix,Func<string,CancellationToken,Task<(int Code,string Output)>> run)
    {
        if(!journal.DestinationPrefixes.Contains(prefix))return;
        // A route serving corporate DNS is generation-static even if repaired by a lease.
        if(!RequiredDnsPrefixes(link).Contains(prefix))
        {
            var result=await MutateAsync(path,run,RemovalCommand(link.Index,prefix,link.Gateway,OwnedMetric),CancellationToken.None).ConfigureAwait(false);
            if(result.Code!=0)throw new IOException("Destination route cleanup failed.");
        }
        await (journal with{DestinationPrefixes=journal.DestinationPrefixes.Except([prefix]).ToArray(),DnsPrefixes=RequiredDnsPrefixes(link).Contains(prefix)?journal.DnsPrefixes.Append(prefix).Distinct().ToArray():journal.DnsPrefixes}).SaveAsync(path,CancellationToken.None).ConfigureAwait(false);
    }
    public static bool Matches(RouteRow row, OpenVpnLink link, string prefix, uint metric) =>
        row.InterfaceIndex == link.Index && row.DestinationPrefix == prefix && row.NextHop == link.Gateway && row.RouteMetric == metric;
    public static async Task<(int Code, string Output)> RunBounded(Func<string, CancellationToken, Task<(int Code, string Output)>> run,
        string script, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        var operation=run(script,stop.Token);
        try { return await operation.WaitAsync(stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // Production ProcessHost kills the owned job on cancellation. Wait
            // for termination as well, bounded even for a broken test boundary.
            try { await operation.WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false); } catch { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("Превышено время операции маршрутов OpenVPN; журнал сохранён для повторной очистки.");
        }
    }
}
