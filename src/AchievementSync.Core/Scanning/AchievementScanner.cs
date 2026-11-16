using AchievementSync.Core.Models;
using AchievementSync.Core.Parsing;

namespace AchievementSync.Core.Scanning;

public sealed class AchievementScanner
{
    private static readonly HashSet<string> CandidateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "achievements.json",
        "game-info.json",
        "achievements.ini",
        "achiev.ini"
    };

    private readonly IAchievementParser[] _parsers =
    [
        new JsonAchievementParser(),
        new IniAchievementParser()
    ];

    public Task<ScanResult> ScanAsync(
        IReadOnlyList<ScanLocation> locations,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(locations, cancellationToken), cancellationToken);

    private ScanResult Scan(IReadOnlyList<ScanLocation> locations, CancellationToken cancellationToken)
    {
        var profiles = new List<ParsedProfile>();
        var issues = new List<string>();
        var updatedLocations = new List<ScanLocation>();
        var candidateFiles = 0;

        foreach (var location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!location.IsEnabled)
            {
                updatedLocations.Add(location with { IsAvailable = Directory.Exists(location.Path) });
                continue;
            }

            if (!Directory.Exists(location.Path))
            {
                updatedLocations.Add(location with { IsAvailable = false, FilesFound = 0, Error = null });
                continue;
            }

            var found = new List<string>();
            var locationIssues = new List<string>();
            EnumerateCandidates(location.Path, found, locationIssues, cancellationToken);
            candidateFiles += found.Count;

            foreach (var file in found)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var appId = FindAppId(location.Path, file);
                if (appId is null)
                {
                    issues.Add($"Could not identify an AppID from the folder or steam_appid.txt near {Path.GetFileName(file)}.");
                    continue;
                }

                var parser = _parsers.FirstOrDefault(item => item.SupportsFile(file));
                if (parser is null) continue;
                var result = parser.Parse(file, appId);
                if (result.Profile is not null)
                {
                    profiles.Add(result.Profile);
                }
                else if (result.Error is not null && !result.Error.Contains("does not contain recognizable", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add($"Skipped {Path.GetFileName(file)}: {result.Error}");
                }
            }

            issues.AddRange(locationIssues);
            updatedLocations.Add(location with
            {
                IsAvailable = true,
                FilesFound = found.Count,
                Error = locationIssues.Count == 0 ? null : $"{locationIssues.Count} item(s) could not be read."
            });
        }

        return new ScanResult(
            DateTimeOffset.UtcNow,
            ProfileMerger.Merge(profiles),
            updatedLocations,
            issues,
            candidateFiles);
    }

    private static void EnumerateCandidates(
        string rootPath,
        List<string> candidates,
        List<string> issues,
        CancellationToken cancellationToken)
    {
        const int maxDirectories = 50_000;
        const int maxCandidateFiles = 100_000;
        var pending = new Stack<string>();
        pending.Push(rootPath);
        var visitedDirectories = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visitedDirectories > maxDirectories)
            {
                issues.Add($"Stopped scanning {Path.GetFileName(rootPath)} after reaching the directory safety limit.");
                return;
            }

            var directory = pending.Pop();
            try
            {
                var attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;

                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    var name = Path.GetFileName(file);
                    if (CandidateNames.Contains(name) || string.Equals(Path.GetExtension(file), ".db", StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(file);
                        if (candidates.Count >= maxCandidateFiles)
                        {
                            issues.Add("Stopped after reaching the candidate-file safety limit.");
                            return;
                        }
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        {
                            pending.Push(child);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        issues.Add($"Could not inspect folder {Path.GetFileName(child)}.");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                issues.Add($"Could not read folder {Path.GetFileName(directory)}.");
            }
        }
    }

    private static string? FindAppId(string rootPath, string filePath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = new DirectoryInfo(Path.GetDirectoryName(filePath)!);
        for (var depth = 0; current is not null && depth <= 10; depth++, current = current.Parent)
        {
            if (!IsWithinRoot(root, current.FullName)) break;

            var marker = Path.Combine(current.FullName, "steam_appid.txt");
            if (File.Exists(marker))
            {
                try
                {
                    var markerValue = File.ReadAllText(marker).Trim();
                    if (IsAppId(markerValue)) return markerValue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Continue to the folder convention if the optional marker is unreadable.
                }
            }

            if (IsAppId(current.Name)) return current.Name;
        }

        var fileStem = Path.GetFileNameWithoutExtension(filePath);
        return IsAppId(fileStem) ? fileStem : null;
    }

    private static bool IsWithinRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "."
            || (!Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static bool IsAppId(string value) =>
        value.Length is >= 1 and <= 10 && value.All(char.IsAsciiDigit);
}
