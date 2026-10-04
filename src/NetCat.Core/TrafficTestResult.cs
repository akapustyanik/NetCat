namespace NetCat.Core;

public sealed record TrafficTransfer(bool Success, long SentBytes, long ReceivedBytes, string Error = "");
public sealed record TrafficWebCheck(string Service, bool Success, int StatusCode, string Error = "");
public sealed record TrafficTestResult(TrafficTransfer Download, TrafficTransfer Upload, int UdpSent, int UdpReceived, string UdpError = "", IReadOnlyList<TrafficWebCheck>? WebChecks = null)
{
    public bool Success => Download.Success && Upload.Success && UdpSent > 0 && UdpReceived == UdpSent && UdpError.Length == 0 && (WebChecks == null || WebChecks.All(w => w.Success));
    public string Summary => $"{(Success ? "ПРОЙДЕН" : "НЕ ПРОЙДЕН")} · TCP ↓ {(Download.Success ? "✓" : "✕")} ↑ {(Upload.Success ? "✓" : "✕")} · UDP {UdpReceived}/{UdpSent}" +
        (WebChecks is { Count: > 0 } ? $" · сайты {WebChecks.Count(w => w.Success)}/{WebChecks.Count}" : "");
    public string WebDetails => WebChecks == null ? "" : string.Join("\n", WebChecks.Select(w => $"{w.Service}: {(w.Success ? "ответ получен" : w.Error)} (HTTP {w.StatusCode})"));
    public string Details =>
        $"Загрузка: {Download.ReceivedBytes} байт; {(Download.Success ? "получены полностью" : Download.Error)}\n" +
        $"Отправка: {Upload.SentBytes} байт; {(Upload.Success ? "эхо сервера совпало с отправленными данными" : Upload.Error)}\n" +
        $"UDP (STUN): {UdpReceived}/{UdpSent} ответов с совпавшими случайными идентификаторами. {UdpError}\n" +
        WebDetails + "\n" +
        "Проверка через отдельный SOCKS выбранного профиля. Она не проверяет системный TUN, правила приложений или доступность каждого сайта. " +
        "Внешние тестовые сервисы тоже могут быть недоступны. UDP STUN не подтверждает работу всех UDP-приложений.";
    public static TrafficTestResult Failed(string error) => new(new(false, 0, 0, error), new(false, 0, 0, error), 0, 0, error);
}

public sealed record ActiveTrafficStamp(Guid? ProfileId, long SessionRevision, long NetworkRevision, int ProcessId, bool TunActive);
public sealed record ActiveTrafficTest(ActiveTrafficStamp Stamp, TrafficTestResult? Result, string Unavailable = "")
{
    public DateTimeOffset TestedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Success => Unavailable.Length == 0 && Result?.Success == true;
    public string Summary => "Текущий TUN: " + (Unavailable.Length > 0 ? "НЕ ПОДТВЕРЖДЁН · " + Unavailable : Result?.Summary ?? "не проверен");
    public string Details => Result == null ? Unavailable :
        TestedAt.ToLocalTime().ToString("dd.MM HH:mm:ss") + " · IPv4\n" +
        $"Загрузка: {Result.Download.ReceivedBytes} байт; {Result.Download.Error}\n" +
        $"Отправка с проверкой эхо: {Result.Upload.SentBytes} байт; {Result.Upload.Error}\n" +
        $"UDP STUN: {Result.UdpReceived}/{Result.UdpSent}; {Result.UdpError}\n" +
        Result.WebDetails + "\n" +
        "Запросы отдельного процесса через текущий системный TUN, системный DNS и действующие правила. " +
        "Прокси Windows и прямой запасной путь отключены. Проверяются только указанные тестовые сервисы. " + Unavailable;
    public ActiveTrafficTest Validate(ActiveTrafficStamp current, DateTimeOffset? now = null) => current != Stamp
        ? this with { Unavailable = "сессия или сеть изменились; повторите проверку" }
        : (now ?? DateTimeOffset.UtcNow) - TestedAt > TimeSpan.FromMinutes(2)
            ? this with { Unavailable = "результат устарел; повторите проверку" } : this;
}
