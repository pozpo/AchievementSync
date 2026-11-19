using System.IO;

namespace AchievementSync.Core.Synchronization;

/// <summary>
/// Converts absolute local paths into portable environment-variable tokens (such as
/// <c>%USERPROFILE%\AppData\Roaming\…</c>) wherever the path lives under a known machine-specific
/// profile root. This keeps the restoration manifest free of personal profile folders and makes
/// the manifest itself safe to commit to a public repository. Publicly documented environment
/// variables are used (%USERPROFILE%, %APPDATA%, %LOCALAPPDATA%, %PUBLIC%, %PROGRAMDATA%,
/// %ProgramFiles%, %ProgramFiles(x86)%); any path that does not match a known root is preserved
/// unchanged (for example paths on a non-system drive such as <c>D:\Games\…</c>).
/// </summary>
public static class PathSanitizer
{
    // Priority order, most specific profile first so deeper roots win over broad user profile root.
    private static readonly (string Root, string Token)[] Mappings = BuildMappings();

    private static (string Root, string Token)[] BuildMappings()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var publicRoot = Environment.ExpandEnvironmentVariables("%PUBLIC%").TrimEnd('\\', '/');
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        // Order: deeper (more specific) profile subtree first so that e.g. %APPDATA% beats %USERPROFILE% for AppData\Roaming.
        return
        [
            (Normalize(roaming), "%APPDATA%"),
            (Normalize(local), "%LOCALAPPDATA%"),
            (Normalize(publicRoot), "%PUBLIC%"),
            (Normalize(programData), "%PROGRAMDATA%"),
            (Normalize(userProfile), "%USERPROFILE%"),
            (Normalize(programFiles), "%ProgramFiles%"),
            (Normalize(programFilesX86), "%ProgramFiles(x86)%"),
        ];
    }

    private static string Normalize(string root) =>
        string.IsNullOrEmpty(root) ? string.Empty : root.TrimEnd('\\', '/');

    /// <summary>
    /// Replaces the longest matching environment root with its token. The comparison is ordinal
    /// case-insensitive. Paths that do not overlap a known root (for example a separate
    /// <c>D:\Games</c> drive) are preserved unchanged.
    /// </summary>
    public static string Sanitize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var fullPath = TryGetFullPath(path) ?? path;
        fullPath = fullPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        var candidates = Mappings
            .Where(m => !string.IsNullOrEmpty(m.Root))
            .ToArray();

        foreach (var (root, token) in candidates)
        {
            if (fullPath.Length >= root.Length
                && fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && (fullPath.Length == root.Length || fullPath[root.Length] == Path.DirectorySeparatorChar || fullPath[root.Length] == Path.AltDirectorySeparatorChar))
            {
                return token + fullPath[root.Length..];
            }
        }

        return fullPath;
    }

    private static string? TryGetFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
