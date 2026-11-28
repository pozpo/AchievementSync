using System.Text.RegularExpressions;

namespace AchievementSync.Core.Platforms;

/// <summary>Reads per-title save folders from the Lichii and Eglantine user profiles.
/// Neither emulator documents an achievements API, so only an explicitly present, generic
/// achievements.json file is imported.</summary>
public sealed class Switch2EmulatorDataAdapter(PlatformSourceOptions? defaults = null)
    : PlatformDataAdapter("switch2", "Nintendo Switch 2 (Lichii / Eglantine)")
{
    private readonly PlatformSourceOptions _defaults = defaults ?? PlatformSourceOptions.CreateDefault();

    public override bool IsConfigured => IsConfiguredFor(_defaults);

    public override bool IsConfiguredFor(PlatformSourceOptions options) =>
        new[] { options.LichiiRoot, options.EglantineRoot }.Any(path =>
            !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));

    public override string? ConfigurationHint =>
        "Reads Lichii/Eglantine nand\\user\\save profiles. Their documented builds do not expose native achievement data.";

    public override Task<PlatformCollectResult> CollectAsync(PlatformSourceOptions options,
        CancellationToken cancellationToken = default)
    {
        var games = new Dictionary<string, PlatformGameSnapshot>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<string>();
        foreach (var (rootKey, configuredRoot) in new[]
                 {
                     ("lichii", options.LichiiRoot),
                     ("eglantine", options.EglantineRoot)
                 })
        {
            if (string.IsNullOrWhiteSpace(configuredRoot) || !Directory.Exists(configuredRoot)) continue;
            var root = Path.GetFullPath(configuredRoot);
            var saveRoot = Switch2ProfilePaths.ResolveSaveRoot(root);
            if (saveRoot is not null)
            {
                foreach (var titleDirectory in Switch2ProfilePaths.FindTitleSaveDirectories(saveRoot, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var gameId = Path.GetFileName(titleDirectory);
                    if (string.IsNullOrWhiteSpace(gameId)) continue;
                    var snapshot = new PlatformGameSnapshot(Platform, gameId, null, titleDirectory, [], $"{rootKey}-save-root");
                    Upsert(games, snapshot);
                }
            }

            foreach (var file in Switch2ProfilePaths.FindAchievementJson(root, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var achievements = XboxDataAdapter.ReadJsonStates(file);
                    if (achievements.Count == 0) continue;
                    var gameId = Switch2ProfilePaths.FindTitleIdInPath(root, file)
                                 ?? Path.GetFileName(Path.GetDirectoryName(file))
                                 ?? Path.GetFileName(root);
                    var snapshot = new PlatformGameSnapshot(Platform, gameId, null, file, achievements,
                        $"{rootKey}-achievements-json");
                    Upsert(games, snapshot);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                   or System.Text.Json.JsonException)
                {
                    messages.Add($"Switch 2: skipped {Path.GetFileName(file)} ({exception.GetType().Name}).");
                }
            }
        }

        if (games.Count == 0)
            messages.Add("No Lichii/Eglantine save profiles or recognized achievements.json files were found.");
        return Task.FromResult(new PlatformCollectResult(Platform, games.Values.ToArray(), messages));
    }

    private static void Upsert(Dictionary<string, PlatformGameSnapshot> games, PlatformGameSnapshot incoming)
    {
        if (!games.TryGetValue(incoming.GameId, out var existing))
        {
            games[incoming.GameId] = incoming;
            return;
        }

        var merged = existing.Achievements.Concat(incoming.Achievements)
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.Earned)
                .ThenByDescending(item => item.EarnedTimeUnix).First())
            .ToArray();
        games[incoming.GameId] = existing with
        {
            Title = existing.Title ?? incoming.Title,
            SourcePath = incoming.Achievements.Count > 0 ? incoming.SourcePath : existing.SourcePath,
            Achievements = merged,
            Version = incoming.Achievements.Count > 0 ? incoming.Version : existing.Version
        };
    }
}

/// <summary>Reads the documented Ocarina Recomp profile\\save directory for save backup.
/// The linked project does not document an achievements format; a generic local achievements.json
/// is accepted only if one is explicitly present.</summary>
public sealed class OcarinaRecompDataAdapter(PlatformSourceOptions? defaults = null)
    : PlatformDataAdapter("switch2recomp", "Nintendo Switch 2 Recomp ports")
{
    private readonly PlatformSourceOptions _defaults = defaults ?? PlatformSourceOptions.CreateDefault();

    public override bool IsConfigured => IsConfiguredFor(_defaults);

    public override bool IsConfiguredFor(PlatformSourceOptions options) =>
        !string.IsNullOrWhiteSpace(options.Switch2RecompRoot) && Directory.Exists(options.Switch2RecompRoot);

    public override string? ConfigurationHint =>
        "Select one or more Switch 2 recomp install roots; saves are read from perfil\\save. Native achievement file formats are not documented.";

    public override Task<PlatformCollectResult> CollectAsync(PlatformSourceOptions options,
        CancellationToken cancellationToken = default)
    {
        var root = options.Switch2RecompRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Task.FromResult(new PlatformCollectResult(Platform, [], ["The Ocarina Recomp folder was not found."]));

        var fullRoot = Path.GetFullPath(root);
        var games = new Dictionary<string, PlatformGameSnapshot>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<string>();
        foreach (var saveDirectory in FindRecompSaveDirectories(fullRoot, cancellationToken))
        {
            var gameId = InferRecompGameId(fullRoot, saveDirectory);
            var title = gameId.Equals("ocarina-recomp", StringComparison.OrdinalIgnoreCase)
                ? "The Legend of Zelda: Ocarina of Time"
                : null;
            games[gameId] = new PlatformGameSnapshot(Platform, gameId, title, saveDirectory, [], "switch2recomp-save-root");
        }

        foreach (var file in Switch2ProfilePaths.FindAchievementJson(fullRoot, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var achievements = XboxDataAdapter.ReadJsonStates(file);
                if (achievements.Count == 0) continue;
                var gameId = InferRecompGameId(fullRoot, Path.GetDirectoryName(file) ?? fullRoot);
                var current = games.GetValueOrDefault(gameId);
                var title = gameId.Equals("ocarina-recomp", StringComparison.OrdinalIgnoreCase)
                    ? "The Legend of Zelda: Ocarina of Time"
                    : current?.Title;
                games[gameId] = new PlatformGameSnapshot(Platform, gameId, title, current?.SourcePath ?? file, achievements,
                    "switch2recomp-achievements-json");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or System.Text.Json.JsonException)
            {
                messages.Add($"Ocarina Recomp: skipped {Path.GetFileName(file)} ({exception.GetType().Name}).");
            }
        }

        if (games.Count == 0)
            messages.Add("No perfil\\save folder or recognized achievements.json file was found.");
        return Task.FromResult(new PlatformCollectResult(Platform, games.Values.ToArray(), messages));
    }

    private static IEnumerable<string> FindRecompSaveDirectories(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < 20_000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            string[] children;
            try { children = Directory.GetDirectories(directory); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (string.Equals(Path.GetFileName(child), "save", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetFileName(Path.GetDirectoryName(child)), "perfil", StringComparison.OrdinalIgnoreCase))
                {
                    yield return child;
                    continue;
                }
                if (depth >= 5) continue;
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push((child, depth + 1));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
    }

    private static string InferRecompGameId(string root, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(root, path);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Where(segment => !string.IsNullOrWhiteSpace(segment) && segment != ".")
                .ToArray();
            var profileIndex = Array.FindIndex(segments, segment =>
                segment.Equals("perfil", StringComparison.OrdinalIgnoreCase));
            if (profileIndex >= 0) return profileIndex > 0 ? segments[profileIndex - 1] : "ocarina-recomp";
            if (segments.Length > 0 && !segments[^1].Equals("achievements", StringComparison.OrdinalIgnoreCase))
                return segments[^1];
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { }
        return "ocarina-recomp";
    }
}

internal static class Switch2ProfilePaths
{
    private static readonly Regex TitleIdPattern = new("^[0-9A-Fa-f]{16}$", RegexOptions.Compiled);

    public static string? ResolveSaveRoot(string profileRoot)
    {
        var candidates = new[]
        {
            Path.Combine(profileRoot, "nand", "user", "save"),
            Path.Combine(profileRoot, "user", "nand", "user", "save")
        };
        return candidates.FirstOrDefault(Directory.Exists)
               ?? (string.Equals(Path.GetFileName(profileRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                   "save", StringComparison.OrdinalIgnoreCase) && Directory.Exists(profileRoot) ? profileRoot : null);
    }

    public static IEnumerable<string> FindTitleSaveDirectories(string saveRoot, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Path, int Depth)>();
        var titleDirectories = new List<string>();
        pending.Push((saveRoot, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < 50_000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            if (depth > 5) continue;
            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(directory);
                children = Directory.GetDirectories(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            if (depth >= 2 && TitleIdPattern.IsMatch(Path.GetFileName(directory)) && files.Length > 0)
                titleDirectories.Add(directory);

            foreach (var child in children)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push((child, depth + 1));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }

        return titleDirectories
            .Where(candidate => !titleDirectories.Any(other => other.Length > candidate.Length
                && other.StartsWith(candidate.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IEnumerable<string> FindAchievementJson(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < 20_000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(directory, "achievements.json");
                children = depth >= 7 ? [] : Directory.GetDirectories(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var file in files) yield return file;
            foreach (var child in children)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push((child, depth + 1));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
    }

    public static string? FindTitleIdInPath(string root, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(root, Path.GetDirectoryName(path) ?? root);
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         .Reverse())
                if (TitleIdPattern.IsMatch(segment)) return segment;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { }
        return null;
    }
}
