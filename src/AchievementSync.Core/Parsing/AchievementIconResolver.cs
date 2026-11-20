namespace AchievementSync.Core.Parsing;

/// <summary>Resolves local icon files and Steam's hash based community icon references.</summary>
internal static class AchievementIconResolver
{
    private const string LegacySteamIconPath = "/steamcommunity/public/images/apps/";
    private const string CurrentSteamIconPath = "/community_assets/images/apps/";

    public static string? Resolve(string? icon, string? catalogPath, string appId)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        var trimmed = icon.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute)
            && absolute.Scheme is "http" or "https" or "file")
            return NormalizeSteamIconUrl(absolute).ToString();

        string? candidatePath = null;
        try
        {
            candidatePath = Path.IsPathRooted(trimmed)
                ? trimmed
                : catalogPath is not null && Path.IsPathRooted(catalogPath)
                    ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(catalogPath)!, trimmed))
                    : null;
            if (candidatePath is not null && File.Exists(candidatePath))
                return new Uri(candidatePath, UriKind.Absolute).ToString();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException
                                           or System.Security.SecurityException) { }

        if (Path.IsPathRooted(trimmed) || !IsAppId(appId)) return null;
        var normalized = trimmed.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var hash = Path.GetFileNameWithoutExtension(fileName);
        if (hash.Length != 40 || !hash.All(IsHexDigit)
            || extension is not (".jpg" or ".jpeg" or ".png" or ".webp")) return null;

        return $"https://shared.fastly.steamstatic.com{CurrentSteamIconPath}{appId}/{fileName}";
    }

    private static Uri NormalizeSteamIconUrl(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (path.Contains(LegacySteamIconPath, StringComparison.OrdinalIgnoreCase))
        {
            var currentPath = path.Replace(LegacySteamIconPath, CurrentSteamIconPath, StringComparison.OrdinalIgnoreCase);
            return new UriBuilder(uri)
            {
                Scheme = Uri.UriSchemeHttps,
                Host = "shared.fastly.steamstatic.com",
                Port = -1,
                Path = currentPath
            }.Uri;
        }

        if (path.Contains(CurrentSteamIconPath, StringComparison.OrdinalIgnoreCase))
            return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Host = "shared.fastly.steamstatic.com", Port = -1 }.Uri;
        return uri;
    }

    private static bool IsAppId(string value) => value.Length is >= 1 and <= 10
        && value.All(character => character is >= '0' and <= '9')
        && value.Any(character => character != '0');

    private static bool IsHexDigit(char character) => character is >= '0' and <= '9'
        or >= 'a' and <= 'f'
        or >= 'A' and <= 'F';
}
