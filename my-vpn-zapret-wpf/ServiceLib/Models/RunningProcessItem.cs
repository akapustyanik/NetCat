namespace ServiceLib.Models;

[Serializable]
public class RunningProcessItem
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    public override string ToString()
    {
        return DisplayName;
    }
}
