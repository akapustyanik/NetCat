namespace ServiceLib.Models;

public class UpdateResult
{
    public bool Success { get; set; }
    public string? Msg { get; set; }
    public SemanticVersion? Version { get; set; }
    public string? Url { get; set; }
    public GitHubRelease? Release { get; set; }
    public GitHubReleaseAsset? Asset { get; set; }
    public EUpdateAvailabilityStatus Status { get; set; } = EUpdateAvailabilityStatus.Unknown;
    public EUpdateFailureStage FailureStage { get; set; } = EUpdateFailureStage.None;
    public string? LocalArchivePath { get; set; }
    public bool UsedCachedArchive { get; set; }

    public UpdateResult(bool success, string? msg)
    {
        Success = success;
        Msg = msg;
    }

    public UpdateResult(bool success, SemanticVersion? version)
    {
        Success = success;
        Version = version;
    }
}
