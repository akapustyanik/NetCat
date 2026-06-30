using System.IO;
using ServiceLib.Common;
using ServiceLib.Models;

namespace ServiceLib.Handler;

public static class ParallelOpenVpnConfigHandler
{
    private const string FileName = "parallel-openvpn.json";
    private const string ProfilesFolderName = "openvpn";

    public static ParallelOpenVpnConfig Load()
    {
        try
        {
            var path = Utils.GetConfigPath(FileName);
            if (!File.Exists(path))
            {
                return new ParallelOpenVpnConfig();
            }

            var text = FileUtils.NonExclusiveReadAllText(path);
            return JsonUtils.Deserialize<ParallelOpenVpnConfig>(text) ?? new ParallelOpenVpnConfig();
        }
        catch
        {
            return new ParallelOpenVpnConfig();
        }
    }

    public static async Task Save(ParallelOpenVpnConfig config)
    {
        var path = Utils.GetConfigPath(FileName);
        var content = JsonUtils.Serialize(config, true, true);
        await FileUtils.WriteAllTextWithRetryAsync(path, content ?? "{}");
    }

    public static string GetImportedProfilesPath()
    {
        var path = Path.Combine(Utils.GetConfigPath(), ProfilesFolderName);
        Directory.CreateDirectory(path);
        return path;
    }
}
