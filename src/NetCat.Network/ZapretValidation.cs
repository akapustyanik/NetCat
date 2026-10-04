namespace NetCat.Network;

public sealed record ZapretValidation(bool Accepted, string Reason, int ImprovedTargets, int AvailableGroups);

public static class ZapretStrategyValidation
{
    // Each selected service needs usable HTTPS evidence. Optional API/WebSocket
    // observations describe coverage; they cannot veto another healthy endpoint.
    // A healthy baseline is availability evidence, not proof of DPI bypass.
    public static ZapretValidation Evaluate(IReadOnlyList<ProbeOutcome> baseline,
        IReadOnlyList<ProbeOutcome> enabled, IEnumerable<string> requiredServices)
    {
        var services = requiredServices.Distinct().ToArray();
        if (services.Length == 0) return new(false, "no-selected-services", 0, 0);
        var groups = services.Count(service => enabled.Any(p => p.Service == service && p.Success && p.Name != "WebSocket · Hello"));
        int improved = enabled.Count(p => p.Success && baseline.Any(b => b.Service == p.Service && b.Name == p.Name && !b.Success));
        if (groups != services.Length) return new(false, "service-group-unavailable", improved, groups);
        return new(true, improved > 0 ? "physical-https-improved" : "available-bypass-unconfirmed", improved, groups);
    }

    public static string Diagnostic(string strategyAlias, int interfaceIndex, ProbeOutcome? baseline, ProbeOutcome outcome)
    {
        // Only built-in target aliases are emitted. Detail/URL/exception text is
        // deliberately excluded, including when a caller supplies a custom URL.
        string target = outcome.Service switch { "YouTube" => "youtube", "Discord" => "discord", _ => "custom" };
        string probe = outcome.Name switch { "HTTPS" => "https", "HTTPS · Player API" => "player", "Gateway discovery" => "gateway", "WebSocket · Hello" => "websocket", _ => "custom" };
        return $"ZAPRET_PROBE strategy={strategyAlias} target={target}-{probe} baseline={(baseline == null ? "unknown" : baseline.Success ? "available" : "failed")} enabled={(outcome.Success ? "available" : "failed")} dns={outcome.DnsClass} tcp={outcome.TcpClass} tls={outcome.TlsClass} http={outcome.HttpStatus?.ToString() ?? "unknown"} elapsedMs={outcome.Milliseconds} failure={outcome.FailureClass} ifIndex={interfaceIndex}";
    }
}
