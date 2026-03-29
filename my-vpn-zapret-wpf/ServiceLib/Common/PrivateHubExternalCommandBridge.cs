using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ServiceLib.Common;

public sealed class PrivateHubExternalCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Command { get; set; } = PrivateHubExternalCommandNames.RefreshSubscriptions;
    public bool UseProxy { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public static class PrivateHubExternalCommandNames
{
    public const string RefreshSubscriptions = "refresh-subscriptions";
}

public static class PrivateHubExternalCommandBridge
{
    public static string GetSignalName()
    {
        return $"{ComputeMd5(Utils.GetExePath())}-privatehub-command";
    }

    public static string GetCommandsDirectoryPath()
    {
        var commandsDirectoryPath = Utils.GetTempPath(Path.Combine("privatehub", "commands"));
        Directory.CreateDirectory(commandsDirectoryPath);
        return commandsDirectoryPath;
    }

    public static List<PrivateHubExternalCommand> TakePendingCommands()
    {
        var commands = new List<PrivateHubExternalCommand>();
        foreach (var commandFilePath in Directory.GetFiles(GetCommandsDirectoryPath(), "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var json = File.ReadAllText(commandFilePath);
                var command = JsonSerializer.Deserialize<PrivateHubExternalCommand>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (command != null)
                {
                    commands.Add(command);
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"PrivateHub command parse failed: {commandFilePath}", ex);
            }
            finally
            {
                try
                {
                    File.Delete(commandFilePath);
                }
                catch (Exception ex)
                {
                    Logging.SaveLog($"PrivateHub command cleanup failed: {commandFilePath}", ex);
                }
            }
        }

        return commands;
    }

    private static string ComputeMd5(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = MD5.HashData(bytes);
        StringBuilder sb = new(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }
}
