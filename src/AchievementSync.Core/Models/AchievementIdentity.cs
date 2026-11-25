namespace AchievementSync.Core.Models;

public static class AchievementIdentity
{
    public static string FromDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return string.Empty;
        var normalized = string.Concat(displayName.Normalize(System.Text.NormalizationForm.FormKC)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant));
        return normalized.Length == 0 ? string.Empty : "name:" + normalized;
    }

    public static string NormalizeDisplayName(string? displayName) =>
        string.Concat((displayName ?? string.Empty).Normalize(System.Text.NormalizationForm.FormKC)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant));

    public static string HumanizeApiName(string? apiName)
    {
        if (string.IsNullOrWhiteSpace(apiName)) return "Achievement";
        var value = apiName.Trim();
        if (value.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            value = value[5..];
        else
            value = System.Text.RegularExpressions.Regex.Replace(value, "^(ACHIEVEMENT|ACH)[_-]+", string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        value = System.Text.RegularExpressions.Regex.Replace(value, "([a-z0-9])([A-Z])", "$1 $2",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        value = System.Text.RegularExpressions.Regex.Replace(value, "[_-]+", " ",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        value = System.Text.RegularExpressions.Regex.Replace(value, "\\s+", " ").Trim();
        return value.Length == 0 ? "Achievement"
            : System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());
    }

    public static bool IsIdentifierLikeDisplayName(string? apiName, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return true;
        if (displayName.StartsWith("name:", StringComparison.OrdinalIgnoreCase)
            || displayName.StartsWith("ACH_", StringComparison.OrdinalIgnoreCase)
            || displayName.StartsWith("ACHIEVEMENT_", StringComparison.OrdinalIgnoreCase)) return true;
        var normalizedDisplay = NormalizeDisplayName(displayName);
        return normalizedDisplay.Length == 0
            || normalizedDisplay == NormalizeDisplayName(apiName)
            || normalizedDisplay == NormalizeDisplayName(HumanizeApiName(apiName));
    }

    public static bool IsMetadataImageUri(string? imageUri)
    {
        if (!Uri.TryCreate(imageUri, UriKind.Absolute, out var uri) || !uri.IsFile) return false;
        var imagesDirectory = Path.GetDirectoryName(uri.LocalPath);
        if (!string.Equals(Path.GetFileName(imagesDirectory), "images", StringComparison.OrdinalIgnoreCase)) return false;
        var appDirectory = Path.GetDirectoryName(imagesDirectory);
        var appId = Path.GetFileName(appDirectory);
        return !string.IsNullOrWhiteSpace(appId) && appId.Length <= 10 && appId.All(char.IsAsciiDigit);
    }
}
