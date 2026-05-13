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
    private static readonly HttpClient FallbackHttpClient = new()
    {
        BaseAddress = new Uri("https://ipwho.is/"),
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
        var knownHostCountry = TryResolveKnownHostCountry(hostOrAddress);
        if (knownHostCountry != null)
        {
            return knownHostCountry;
        }

        var ipAddress = await ResolveIpAddressAsync(hostOrAddress);
        if (ipAddress is null || IsPrivateOrSpecialUse(ipAddress))
        {
            return null;
        }

        try
        {
            var primaryResult = await ResolveViaPrimaryApiAsync(ipAddress);
            if (primaryResult != null)
            {
                return primaryResult;
            }
        }
        catch
        {
        }

        try
        {
            return await ResolveViaFallbackApiAsync(ipAddress);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ServerCountryInfo?> ResolveViaPrimaryApiAsync(IPAddress ipAddress)
    {
        using var response = await HttpClient.GetAsync($"?q={Uri.EscapeDataString(ipAddress.ToString())}");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

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

    private static async Task<ServerCountryInfo?> ResolveViaFallbackApiAsync(IPAddress ipAddress)
    {
        using var response = await FallbackHttpClient.GetAsync(Uri.EscapeDataString(ipAddress.ToString()));
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;

        if (root.TryGetProperty("success", out var successElement)
            && successElement.ValueKind == JsonValueKind.False)
        {
            return null;
        }

        var countryCode = ReadString(root, "country_code")?.Trim().ToUpperInvariant();
        var countryName = ReadString(root, "country")?.Trim();
        if (string.IsNullOrWhiteSpace(countryCode) || string.IsNullOrWhiteSpace(countryName))
        {
            return null;
        }

        return new ServerCountryInfo(countryCode, countryName, BuildFlagImageUrl(countryCode));
    }

    private static async Task<IPAddress?> ResolveIpAddressAsync(string hostOrAddress)
    {
        if (IPAddress.TryParse(hostOrAddress, out var ipAddress))
        {
            return ipAddress;
        }

        try
        {
            var hostEntry = await Dns.GetHostAddressesAsync(hostOrAddress);
            var resolved = hostEntry.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)
                ?? hostEntry.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetworkV6)
                ?? hostEntry.FirstOrDefault();
            return resolved;
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

    private static ServerCountryInfo? TryResolveKnownHostCountry(string hostOrAddress)
    {
        var host = hostOrAddress.Trim().TrimEnd('.').ToLowerInvariant();
        return host switch
        {
            "private.catbox.co" => new ServerCountryInfo("DE", "Germany", BuildFlagImageUrl("DE")),
            _ => null
        };
    }

    private static bool IsPrivateOrSpecialUse(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                   || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254)
                   || bytes[0] == 127
                   || bytes[0] >= 224;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal
                   || address.IsIPv6SiteLocal
                   || bytes[0] == 0xfc
                   || bytes[0] == 0xfd
                   || bytes.All(static value => value == 0);
        }

        return true;
    }
}
