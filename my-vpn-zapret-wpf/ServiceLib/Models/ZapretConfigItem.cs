namespace ServiceLib.Models;

[Serializable]
public class ZapretConfigItem : ReactiveObject
{
    public string Name { get; set; } = string.Empty;

    [Reactive]
    public bool IsPassing { get; set; }

    [Reactive]
    public bool HasTestResult { get; set; }

    [Reactive]
    public string YouTubeLabel { get; set; } = "YouTube: not tested";

    [Reactive]
    public string DiscordLabel { get; set; } = "Discord: not tested";

    public override string ToString()
    {
        return Name;
    }
}
