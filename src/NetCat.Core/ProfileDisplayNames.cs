namespace NetCat.Core;

/// <summary>Renames legacy server labels without changing connection identity or user names.</summary>
public static class ProfileDisplayNames
{
    public static string Normalize(string name)
    {
        foreach(var (alias, display) in new[] { ("server-a", "Germany"), ("server a", "Germany"), ("server-b", "Finland"), ("server b", "Finland") })
            if(name.StartsWith(alias, StringComparison.OrdinalIgnoreCase) &&
               (name.Length == alias.Length || char.IsWhiteSpace(name[alias.Length]) || name[alias.Length] is '·' or ':' or '-'))
                return display + name[alias.Length..];
        return name;
    }

    /// <summary>Refreshes generated country labels while retaining custom profile names.</summary>
    public static string Refresh(string savedName, string incomingName)
    {
        var normalized = Normalize(incomingName);
        if (GeneratedSuffix(savedName) is { } savedSuffix &&
            GeneratedSuffix(normalized) is { } incomingSuffix &&
            savedSuffix.Equals(incomingSuffix, StringComparison.Ordinal))
            return normalized;
        return Normalize(savedName);
    }

    private static string? GeneratedSuffix(string name)
    {
        foreach (var prefix in new[] { "server-a", "server a", "server-b", "server b", "Finland", "Germany", "German" })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (name.Length == prefix.Length || char.IsWhiteSpace(name[prefix.Length]) || name[prefix.Length] is '·' or ':' or '-'))
                return name[prefix.Length..];
        return null;
    }
}
