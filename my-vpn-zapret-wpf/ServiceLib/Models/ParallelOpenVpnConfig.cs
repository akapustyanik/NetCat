namespace ServiceLib.Models;

public class ParallelOpenVpnConfig
{
    public List<ParallelOpenVpnProfile> Profiles { get; set; } = new();
    public string? SelectedProfileId { get; set; }
}

[Serializable]
public class ParallelOpenVpnProfile : ReactiveObject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ConfigPath { get; set; } = string.Empty;
    public List<string> Domains { get; set; } = new();

    [Reactive]
    public bool IsRunning { get; set; }

    [Reactive]
    public string Status { get; set; } = "Stopped";

    public string Summary => ConfigPath.IsNullOrEmpty()
        ? "Config file is not selected"
        : ConfigPath;
}
