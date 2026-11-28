using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AchievementSync.Core.Tracking;

/// <summary>Finds installed Steam game executables so playtime tracking can recover after its local state is reset.</summary>
[SupportedOSPlatform("windows")]
public static class SteamInstalledExecutableDiscovery
{
    private static readonly Regex LibraryPathPattern = new(
        "\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex InstallDirectoryPattern = new(
        "\\\"installdir\\\"\\s+\\\"(?<path>[^\\\"]+)\\\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "_CommonRedist", "CommonRedist", "Redist", "Redistributables", "Prerequisites", "Support",
        "Tools", "Docs", "DirectX", "EasyAntiCheat", "BattlEye", "CrashReportClient", "CrashReports",
        "__Installer", "Uninstall", "Uninstaller", "ThirdParty"
    };
    private static readonly string[] ExcludedExecutablePrefixes =
    [
        "unins", "uninstall", "setup", "install", "crashreport", "crashpad", "unitycrashhandler",
        "ue4prereq", "eac", "easyanticheat", "battleye", "benchmark", "redist",
        "epicwebhelper", "steamwebhelper", "gameoverlayrenderer", "eosoverlayrenderer",
        "checkenv", "8za", "7za"
    ];

    /// <summary>Returns likely game executables for AppIDs that have an installed Steam manifest.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Discover(
        IEnumerable<string> appIds,
        string? configuredSteamRoot = null)
    {
        ArgumentNullException.ThrowIfNull(appIds);
        var requested = appIds.Where(IsAppId).ToHashSet(StringComparer.Ordinal);
        if (requested.Count == 0) return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var steamApps in FindSteamAppsDirectories(configuredSteamRoot))
        {
            foreach (var appId in requested)
            {
                if (result.ContainsKey(appId)) continue;
                var manifestPath = Path.Combine(steamApps, $"appmanifest_{appId}.acf");
                if (!File.Exists(manifestPath)) continue;

                try
                {
                    var manifest = File.ReadAllText(manifestPath);
                    var installMatch = InstallDirectoryPattern.Match(manifest);
                    if (!installMatch.Success) continue;
                    var installDirectory = installMatch.Groups["path"].Value.Replace("\\\\", "\\", StringComparison.Ordinal);
                    if (string.IsNullOrWhiteSpace(installDirectory)
                        || installDirectory.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;

                    var gameDirectory = Path.GetFullPath(Path.Combine(steamApps, "common", installDirectory));
                    if (!IsWithin(Path.Combine(steamApps, "common"), gameDirectory) || !Directory.Exists(gameDirectory)) continue;
                    var title = ReadManifestValue(manifest, "name") ?? installDirectory;
                    var executables = FindLikelyExecutables(gameDirectory, title, installDirectory);
                    if (executables.Count > 0) result[appId] = executables;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                   or ArgumentException or System.Security.SecurityException)
                {
                    // A missing, locked, or malformed manifest must not stop discovery for other games.
                }
            }
        }

        return result;
    }

    /// <summary>Returns AppID-to-title mappings from installed Steam manifests, including games with no discovered executable.</summary>
    public static IReadOnlyDictionary<string, string> DiscoverInstalledGameTitles(string? configuredSteamRoot = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var steamApps in FindSteamAppsDirectories(configuredSteamRoot))
        {
            string[] manifests;
            try { manifests = Directory.GetFiles(steamApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or System.Security.SecurityException) { continue; }

            foreach (var manifestPath in manifests)
            {
                var match = Regex.Match(Path.GetFileName(manifestPath), @"^appmanifest_(?<id>\d+)\.acf$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success) continue;
                var appId = match.Groups["id"].Value;
                try
                {
                    var text = File.ReadAllText(manifestPath);
                    var name = ReadManifestValue(text, "name");
                    if (!string.IsNullOrWhiteSpace(name) && !result.ContainsKey(appId))
                        result[appId] = name.Trim();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                   or System.Security.SecurityException) { }
            }
        }

        return result;
    }

    private static IReadOnlyList<string> FindSteamAppsDirectories(string? configuredSteamRoot)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddSteamRoot(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                if (!Path.IsPathRooted(expanded)) return;
                expanded = Path.GetFullPath(expanded);
                var steamApps = string.Equals(Path.GetFileName(expanded), "steamapps", StringComparison.OrdinalIgnoreCase)
                    ? expanded
                    : Path.Combine(expanded, "steamapps");
                if (Directory.Exists(steamApps)) roots.Add(steamApps);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                                               or System.Security.SecurityException or NotSupportedException) { }
        }

        AddSteamRoot(configuredSteamRoot);
        AddSteamRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        AddSteamRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));
        AddSteamRoot(ReadSteamPath(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam"));
        AddSteamRoot(ReadSteamPath(RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Valve\Steam"));

        // Each Steam installation lists secondary libraries in steamapps/libraryfolders.vdf.
        foreach (var mainSteamApps in roots.ToArray())
        {
            var libraryFile = Path.Combine(mainSteamApps, "libraryfolders.vdf");
            try
            {
                if (!File.Exists(libraryFile)) continue;
                foreach (Match match in LibraryPathPattern.Matches(File.ReadAllText(libraryFile)))
                {
                    var libraryPath = match.Groups["path"].Value.Replace("\\\\", "\\", StringComparison.Ordinal);
                    if (string.IsNullOrWhiteSpace(libraryPath)) continue;
                    var steamApps = Path.Combine(libraryPath, "steamapps");
                    if (Directory.Exists(steamApps)) roots.Add(steamApps);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                                               or System.Security.SecurityException) { }
        }

        return roots.ToArray();
    }

    private static string? ReadSteamPath(RegistryHive hive, RegistryView view, string keyPath)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(keyPath, writable: false);
            return key?.GetValue("SteamPath") as string ?? key?.GetValue("InstallPath") as string;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or System.Security.SecurityException) { return null; }
    }

    private static string? ReadManifestValue(string manifest, string key)
    {
        var pattern = new Regex($"\\\"{Regex.Escape(key)}\\\"\\s+\\\"(?<value>[^\\\"]+)\\\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var match = pattern.Match(manifest);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static IReadOnlyList<string> FindLikelyExecutables(
        string gameDirectory,
        string gameTitle,
        string installDirectory)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((gameDirectory, 0));
        var candidates = new List<(string Path, int Score)>();
        var inspectedDirectories = 0;
        var normalizedTitle = NormalizeName(gameTitle);
        var normalizedInstallDirectory = NormalizeName(installDirectory);

        while (pending.Count > 0 && inspectedDirectories < 600 && candidates.Count < 100)
        {
            var (directory, depth) = pending.Pop();
            inspectedDirectories++;
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var file in Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    var filename = Path.GetFileNameWithoutExtension(file);
                    if (IsExcludedExecutable(filename)) continue;
                    var score = ScoreExecutable(file, filename, gameDirectory, normalizedTitle, normalizedInstallDirectory);
                    if (score > 0) candidates.Add((Path.GetFullPath(file), score));
                }

                if (depth >= 4) continue;
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(child);
                    if (SkippedDirectories.Contains(name)
                        || name.StartsWith("_CommonRedist", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("EasyAntiCheat", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("BattlEye", StringComparison.OrdinalIgnoreCase)) continue;
                    pending.Push((child, depth + 1));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or System.Security.SecurityException or DirectoryNotFoundException) { }
        }

        if (candidates.Count == 0) return [];
        var ordered = candidates
            .GroupBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.Score).First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Link only candidates with a positive game-name/shipping signal. A generic executable
        // under Binaries\Win64 may be a helper (for example EpicWebHelper.exe), not the game.
        var primary = ordered.Where(candidate => candidate.Score >= 7).Take(6).ToArray();
        return primary.Select(candidate => candidate.Path).ToArray();
    }

    private static int ScoreExecutable(
        string path,
        string filename,
        string gameDirectory,
        string normalizedTitle,
        string normalizedInstallDirectory)
    {
        var normalizedFilename = NormalizeName(filename);
        var score = 1;
        if (normalizedTitle.Length >= 4 && normalizedFilename.Contains(normalizedTitle, StringComparison.Ordinal)) score += 7;
        if (normalizedInstallDirectory.Length >= 3
            && normalizedFilename.Contains(normalizedInstallDirectory, StringComparison.Ordinal)) score += 8;
        if (filename.Contains("Shipping", StringComparison.OrdinalIgnoreCase)) score += 7;
        if (filename.Contains("Win64", StringComparison.OrdinalIgnoreCase)) score += 4;
        if (filename.Contains("Game", StringComparison.OrdinalIgnoreCase)) score += 2;
        var relative = Path.GetRelativePath(gameDirectory, path);
        var depth = relative.Count(character => character is '\\' or '/');
        if (depth == 0) score += 2;
        var directory = Path.GetDirectoryName(relative) ?? string.Empty;
        if (directory.Contains("Binaries\\Win64", StringComparison.OrdinalIgnoreCase)
            || directory.Contains("Binaries/Win64", StringComparison.OrdinalIgnoreCase)) score += 4;
        if (directory.Contains("Launcher", StringComparison.OrdinalIgnoreCase)
            || filename.Contains("Launcher", StringComparison.OrdinalIgnoreCase)) score -= 5;
        if (filename.Contains("Crash", StringComparison.OrdinalIgnoreCase)
            || filename.Contains("Server", StringComparison.OrdinalIgnoreCase)
            || filename.Contains("Editor", StringComparison.OrdinalIgnoreCase)) score -= 8;
        return score;
    }

    private static bool IsExcludedExecutable(string filename) =>
        ExcludedExecutablePrefixes.Any(prefix => filename.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        || filename.Contains("CrashReportClient", StringComparison.OrdinalIgnoreCase)
        || filename.Contains("UnrealCEFSubProcess", StringComparison.OrdinalIgnoreCase);

    /// <summary>Identifies process helpers that must never be kept as a game executable link.</summary>
    public static bool IsExcludedExecutablePath(string executablePath) =>
        IsExcludedExecutable(Path.GetFileNameWithoutExtension(executablePath));

    private static string NormalizeName(string? value) => string.Concat((value ?? string.Empty)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant));

    private static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return relative == "." || (!Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static bool IsAppId(string value) => value.Length is >= 1 and <= 10 && value.All(char.IsAsciiDigit);
}
