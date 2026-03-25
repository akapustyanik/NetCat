namespace ServiceLib.Models;

public class CheckUpdateModel : ReactiveObject
{
    public bool? IsSelected { get; set; }
    public string? CoreType { get; set; }
    public string? DisplayName { get; set; }
    public bool CanUseLocalPackage { get; set; }
    [Reactive] public string? Remarks { get; set; }
    [Reactive] public string? Hint { get; set; }
    [Reactive] public bool CanRetry { get; set; }
    [Reactive] public bool IsRetrying { get; set; }
    [Reactive] public string? CurrentVersion { get; set; }
    [Reactive] public string? LatestVersion { get; set; }
    [Reactive] public string? StatusLabel { get; set; }
    [Reactive] public string? StatusTone { get; set; }
    [Reactive] public bool ShowStatusLabel { get; set; }
    [Reactive] public string? ReleaseUrl { get; set; }
    [Reactive] public bool CanOpenReleaseUrl { get; set; }
    [Reactive] public bool ShowLatestVersion { get; set; }
    [Reactive] public string? ActionLabel { get; set; }
    [Reactive] public bool CanRunUpdate { get; set; }
    public string? FileName { get; set; }
    public bool? IsFinished { get; set; }
}
