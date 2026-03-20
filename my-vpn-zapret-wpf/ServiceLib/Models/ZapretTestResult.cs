namespace ServiceLib.Models;

public class ZapretTestResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public long? TimeMs { get; set; }
    public bool YoutubeSuccess { get; set; }
    public long? YoutubePingMs { get; set; }
    public long? YoutubeHttpMs { get; set; }
    public string YoutubeMessage { get; set; } = string.Empty;
    public bool DiscordSuccess { get; set; }
    public long? DiscordPingMs { get; set; }
    public long? DiscordHttpMs { get; set; }
    public string DiscordMessage { get; set; } = string.Empty;
}
