namespace ServiceLib.Models;

public class CheckUpdateModel : ReactiveObject
{
    public bool? IsSelected { get; set; }
    public string? CoreType { get; set; }
    [Reactive] public string? Remarks { get; set; }
    [Reactive] public string? Hint { get; set; }
    [Reactive] public bool CanRetry { get; set; }
    [Reactive] public bool IsRetrying { get; set; }
    public string? FileName { get; set; }
    public bool? IsFinished { get; set; }
}
