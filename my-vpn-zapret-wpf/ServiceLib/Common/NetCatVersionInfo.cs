using System.Reflection;

namespace ServiceLib.Common;

public static class NetCatVersionInfo
{
    private const string VersionFamilyKey = "NetCatVersionFamily";
    private const string TelegramWsProxyImplementationKey = "NetCatTelegramWsProxyImplementation";
    private const string TelegramWsProxySchemaVersionKey = "NetCatTelegramWsProxySchemaVersion";
    private const string TelegramWsProxyVersionFamilyKey = "NetCatTelegramWsProxyVersionFamily";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Metadata = new(LoadMetadata);

    public static string VersionFamily => GetMetadataValue(VersionFamilyKey, GetAssemblyVersionFallback());

    public static string TelegramWsProxyImplementation => GetMetadataValue(TelegramWsProxyImplementationKey, "tg-ws-proxy-main");

    public static int TelegramWsProxySchemaVersion => int.TryParse(
        GetMetadataValue(TelegramWsProxySchemaVersionKey, "1"),
        out var schemaVersion)
            ? schemaVersion
            : 1;

    public static string TelegramWsProxyVersionFamily => GetMetadataValue(
        TelegramWsProxyVersionFamilyKey,
        $"netcat-v{VersionFamily}+schema.{TelegramWsProxySchemaVersion}");

    public static string TelegramWsProxyDisplay => $"{TelegramWsProxyImplementation}/{TelegramWsProxyVersionFamily}";

    private static string GetMetadataValue(string key, string fallback)
    {
        return Metadata.Value.TryGetValue(key, out var value) && value.IsNotEmpty()
            ? value
            : fallback;
    }

    private static IReadOnlyDictionary<string, string> LoadMetadata()
    {
        return typeof(NetCatVersionInfo).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => attribute.Key.IsNotEmpty())
            .GroupBy(attribute => attribute.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
    }

    private static string GetAssemblyVersionFallback()
    {
        return typeof(NetCatVersionInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
