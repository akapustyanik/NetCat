using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace ServiceLib.Common;

public sealed record ServerCountryInfo(string CountryCode, string CountryName, string FlagImageUrl);

public sealed class ServerCountryLookup
{
    private static readonly HttpClient HttpClient = new()
    {
        BaseAddress = new Uri("https://api.ipapi.is/"),
        Timeout = TimeSpan.FromSeconds(8)
    };

    private readonly ConcurrentDictionary<string, Lazy<Task<ServerCountryInfo?>>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ServerCountryInfo?> ResolveAsync(string? hostOrAddress)
    {
        var normalizedHost = NormalizeHost(hostOrAddress);
        if (string.IsNullOrWhiteSpace(normalizedHost))
        {
            return null;
        }

        var lazyResult = _cache.GetOrAdd(
            normalizedHost,
            static key => new Lazy<Task<ServerCountryInfo?>>(() => ResolveCoreAsync(key)));

        try
        {
            var country = await lazyResult.Value;
            if (country is null)
            {
                _cache.TryRemove(normalizedHost, out _);
            }

            return country;
        }
        catch
        {
            _cache.TryRemove(normalizedHost, out _);
            return null;
        }
    }

    private static async Task<ServerCountryInfo?> ResolveCoreAsync(string hostOrAddress)
    {
        var ipAddress = await ResolveIpAddressAsync(hostOrAddress);
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return null;
        }

        using var response = await HttpClient.GetAsync($"?q={Uri.EscapeDataString(ipAddress)}");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;

        var location = root.TryGetProperty("location", out var locationElement)
            ? locationElement
            : default;
        var asn = root.TryGetProperty("asn", out var asnElement)
            ? asnElement
            : default;

        var countryCode = ReadString(location, "country_code")
            ?? ReadString(root, "country_code")
            ?? ReadString(root, "countryCode")
            ?? ReadString(asn, "country");
        var countryName = ReadString(location, "country")
            ?? ReadString(root, "country_name")
            ?? ReadString(root, "country");

        countryCode = countryCode?.Trim().ToUpperInvariant();
        countryName = countryName?.Trim();
        if (string.IsNullOrWhiteSpace(countryCode) || string.IsNullOrWhiteSpace(countryName))
        {
            return null;
        }

        return new ServerCountryInfo(countryCode, countryName, BuildFlagImageUrl(countryCode));
    }

    private static async Task<string?> ResolveIpAddressAsync(string hostOrAddress)
    {
        if (IPAddress.TryParse(hostOrAddress, out var ipAddress))
        {
            return ipAddress.ToString();
        }

        try
        {
            var hostEntry = await Dns.GetHostAddressesAsync(hostOrAddress);
            var resolved = hostEntry.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)
                ?? hostEntry.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetworkV6)
                ?? hostEntry.FirstOrDefault();
            return resolved?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeHost(string? hostOrAddress)
    {
        if (string.IsNullOrWhiteSpace(hostOrAddress))
        {
            return null;
        }

        var candidate = hostOrAddress.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absoluteUri))
        {
            candidate = absoluteUri.Host;
        }

        if (candidate.StartsWith("[", StringComparison.Ordinal) &&
            candidate.EndsWith("]", StringComparison.Ordinal))
        {
            candidate = candidate[1..^1];
        }

        if (candidate.Count(static ch => ch == ':') == 1 &&
            candidate.Contains('.'))
        {
            var separatorIndex = candidate.LastIndexOf(':');
            candidate = candidate[..separatorIndex];
        }

        return candidate.Trim();
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind != JsonValueKind.Undefined &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string BuildFlagImageUrl(string countryCode)
    {
        if (countryCode.Length != 2 || !countryCode.All(char.IsLetter))
        {
            return string.Empty;
        }

        return $"https://flagcdn.com/24x18/{countryCode.ToLowerInvariant()}.png";
    }
}
