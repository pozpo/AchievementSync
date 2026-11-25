using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AchievementSync.Core.Synchronization;

/// <summary>
/// Discovers game save files under AppID-scoped emulator folders, Steam library common folders
/// (resolved through appmanifest_&lt;id&gt;.acf), and under each game's own Documents/SaveGames/LocalLow folders.
/// Everything is bounded by a save-extension list, save-token directory names, depth and file-count limits, so the
/// scanner never walks arbitrary user directories and never guesses between games.
/// </summary>
public static class SaveFileDiscoverer
{
    private const long MaximumFileBytes = 32L * 1024 * 1024;
    private const int MaximumDepth = 8;
    private const int MaximumFilesPerAppId = 2000;
    private const int MaximumTokenScanDepth = 6;

    private static readonly HashSet<string> SaveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sav", ".save", ".sl2", ".co2", ".dat", ".bin", ".slot", ".profile", ".slt", ".db", ".sqlite", ".xml",
        ".cfg", ".ini", ".tres", ".rpgsave", ".ess", ".fos", ".srm", ".mcr", ".bak", ".old", ".autosave",
    };

    private static readonly string[] SaveTokens =
        ["Save", "Saves", "SaveData", "SaveGames", "Saved", "SavedGames", "Remote", "Storage", "Slot0", "Slot1", "Slot2", "Slot3", "Slot4", "Profiles", "PlayerData"];

    private static readonly string[] SkipNames = ["achievements.json", "playtime.txt", "unlock-state.json", "steam_appid.txt", "stats.db"];

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Crashes", "CrashReport", "CrashReports", "Crash", "Logs", "Log", "Screenshots",
        "ShaderCache", "Shaders", "D3D12", "D3DDriverByteCodeBlob", "WebCache", "GPUCache",
    };

    /// <summary>AppID-scoped emulator/launcher roots. Placeholders: {appid}.</summary>
    public static readonly IReadOnlyList<(string Key, string Template)> SaveRoots =
    [
        ("GSE Saves", @"%APPDATA%\GSE Saves\{appid}\remote"),
        ("GSE Saves settings", @"%APPDATA%\GSE Saves\{appid}\settings"),
        ("Goldberg SteamEmu Saves", @"%APPDATA%\Goldberg SteamEmu Saves\{appid}\remote"),
        ("Goldberg SteamEmu Saves settings", @"%APPDATA%\Goldberg SteamEmu Saves\{appid}\settings"),
        ("Goldberg UplayEmu Saves", @"%APPDATA%\Goldberg UplayEmu Saves\{appid}"),
        ("R1 UplayEmu Saves", @"%APPDATA%\R1 UplayEmu Saves\{appid}"),
        ("OnlineFix", @"%PUBLIC%\Documents\OnlineFix\{appid}\Saves\remote"),
        ("OnlineFix out\\save", @"%PUBLIC%\Documents\OnlineFix\{appid}\Saves\out\save"),
        ("OnlineFix LocalAppData", @"%LOCALAPPDATA%\OnlineFix\{appid}\Saves"),
        ("Steam CODEX Remote", @"%PUBLIC%\Documents\Steam\CODEX\{appid}\remote"),
        ("Steam CODEX AppData", @"%APPDATA%\Steam\CODEX\{appid}\remote"),
        ("Steam RUNE", @"%PUBLIC%\Documents\Steam\RUNE\{appid}\remote"),
        ("EMPRESS Remote", @"%APPDATA%\EMPRESS\remote\{appid}"),
        ("EMPRESS Documents", @"%PUBLIC%\Documents\EMPRESS\{appid}"),
        ("FLT", @"%LOCALAPPDATA%\FLT\Steam\{appid}\remote"),
        ("SmartSteamEmu", @"%APPDATA%\SmartSteamEmu\{appid}\remote"),
        ("CreamApi", @"%APPDATA%\CreamApi\{appid}"),
        ("1911", @"%APPDATA%\.1911\{appid}"),
        ("SKIDROW Storage", @"%LOCALAPPDATA%\SKIDROW\{appid}\Storage"),
        ("SKIDROW AppData", @"%APPDATA%\SKIDROW\{appid}"),
        ("SkidRow Documents", @"%USERPROFILE%\Documents\SkidRow\{appid}"),
        ("NemirtingasEpicEmu", @"%APPDATA%\NemirtingasEpicEmu\{appid}"),
        ("AchievementWatcher", @"%LOCALAPPDATA%\anadius\LSX emu\achievement_watcher\{appid}"),
        ("UniverseLAN", @"%LOCALAPPDATA%\UniverseLAN\{appid}"),
        ("RUNE Ubisoft Connect savegames", @"%USERPROFILE%\Documents\RUNE\Ubisoft Connect\savegames\{appid}"),
        ("RUNE Ubisoft Connect achievements", @"%USERPROFILE%\Documents\RUNE\Ubisoft Connect\achievements\{appid}"),
    ];

    /// <summary>Native per-game roots. Placeholders: {title} is the game's own title (sanitized). Only existing
    /// directories under them are scanned, and only files inside token-named save directories are matched.</summary>
    private static readonly (string Key, string Template)[] NativeGameRoots =
    [
        ("LocalAppData Saved SaveGames", @"%LOCALAPPDATA%\{title}\Saved\SaveGames"),
        ("LocalAppData Saved Save", @"%LOCALAPPDATA%\{title}\Save"),
        ("LocalAppData Saved Backup", @"%LOCALAPPDATA%\{title}\Backup"),
        ("LocalLow Save", @"%USERPROFILE%\AppData\LocalLow\*\{title}\Save"),
        ("LocalLow Saves", @"%USERPROFILE%\AppData\LocalLow\*\{title}\Saves"),
        ("LocalLow SaveData", @"%USERPROFILE%\AppData\LocalLow\*\{title}\SaveData"),
        ("Documents My Games Saves", @"%USERPROFILE%\Documents\My Games\*\Saves"),
        ("Documents My Games SaveGames", @"%USERPROFILE%\Documents\My Games\*\SaveGames"),
        ("Documents Rockstar Games SaveGames", @"%USERPROFILE%\Documents\Rockstar Games\*\SaveGames"),
        ("Documents Rockstar Games Saves", @"%USERPROFILE%\Documents\Rockstar Games\*\Saves"),
        ("Saved Games", @"%USERPROFILE%\Saved Games\*\{title}"),
        ("LocalAppData VirtualStore x86", @"%LOCALAPPDATA%\VirtualStore\Program Files (x86)\*\{title}"),
        ("LocalAppData VirtualStore", @"%LOCALAPPDATA%\VirtualStore\Program Files\*\{title}"),
    ];
    private static readonly string[] SteamLibraryRoots =
    [
        @"%ProgramFiles(x86)%\Steam\steamapps",
        @"%ProgramFiles%\Steam\steamapps",
        @"C:\SteamLibrary\steamapps",
        @"D:\SteamLibrary\steamapps",
        @"E:\SteamLibrary\steamapps",
    ];

    /// <summary>Discovers save files for each requested AppID (with optional per-game titles for native roots).</summary>
    public static Task<IReadOnlyList<DiscoveredSaveFile>> DiscoverAsync(
        IReadOnlySet<string> appIds,
        CancellationToken cancellationToken = default) =>
        DiscoverAsync(appIds.ToDictionary(id => id, _ => string.Empty, StringComparer.Ordinal), cancellationToken);

    /// <summary>Discovers save files for each AppID, using each game's title for the native-folder roots.</summary>
    public static Task<IReadOnlyList<DiscoveredSaveFile>> DiscoverAsync(
        IReadOnlyDictionary<string, string> appIdToTitle,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var results = new List<DiscoveredSaveFile>();
            foreach (var appId in appIdToTitle.Keys.Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                appIdToTitle.TryGetValue(appId, out var title);

                foreach (var (key, template) in SaveRoots)
                {
                    var root = Expand(template, appId, null);
                    if (root is null || !Directory.Exists(root)) continue;
                    CollectAll(root, key, appId, results, cancellationToken);
                }

                CollectSteamLibraryGame(appId, results, cancellationToken);

                if (!string.IsNullOrWhiteSpace(title))
                {
                    foreach (var (key, template) in NativeGameRoots)
                    {
                        CollectNativeRoot(template, key, appId, title, results, cancellationToken);
                    }
                }
            }

            return (IReadOnlyList<DiscoveredSaveFile>)results;
        }, cancellationToken);
    }

    /// <summary>Resolves a manifest entry back to a local destination on this PC (used by restore).</summary>
    public static string? ResolveLocalPath(string rootKey, string appId, string relativePath)
    {
        var template = SaveRoots.FirstOrDefault(root => string.Equals(root.Key, rootKey, StringComparison.Ordinal))
                             .Template;
        if (template is not null)
        {
            var root = Expand(template, appId, null);
            return root is null ? null : Path.GetFullPath(Path.Combine(root, relativePath));
        }

        // Steam native / native-folder roots: re-derive from the title-aware roots is not possible here without it;
        // the restore uses the manifest's recorded absolute source path when it matches this machine.
        return null;
    }

    /// <summary>Drives appmanifest_&lt;id&gt;.acf parsing to locate the game's install folder, then token-scans it.</summary>
    private static void CollectSteamLibraryGame(string appId, List<DiscoveredSaveFile> results, CancellationToken cancellationToken)
    {
        foreach (var library in SteamLibraryRoots)
        {
            var steamapps = Environment.ExpandEnvironmentVariables(library);
            var manifest = Path.Combine(steamapps, $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest)) continue;

            string? installDir = null;
            try
            {
                var text = File.ReadAllText(manifest);
                var match = Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success) installDir = match.Groups[1].Value;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }

            if (string.IsNullOrWhiteSpace(installDir)) continue;
            var gameRoot = Path.Combine(steamapps, "common", installDir);
            if (!Directory.Exists(gameRoot)) continue;
            TokenScan(gameRoot, gameRoot, "Steam appmanifest", appId, 0, results, cancellationToken);
        }
    }

    /// <summary>Token-based recursive scan under a game folder (save directory names, save-extension files only).</summary>
    private static void TokenScan(
        string appRoot,
        string current,
        string rootKey,
        string appId,
        int depth,
        List<DiscoveredSaveFile> results,
        CancellationToken cancellationToken)
    {
        if (depth > MaximumTokenScanDepth) return;
        cancellationToken.ThrowIfCancellationRequested();
        string[] directories;
        try { directories = Directory.GetDirectories(current).Where(d => !SkipDirectories.Contains(Path.GetFileName(d))).ToArray(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return; }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            if (IsSaveToken(name))
            {
                CollectFiltered(directory, directory, rootKey, appId, results, cancellationToken);
            }
            else
            {
                TokenScan(appRoot, directory, rootKey, appId, depth + 1, results, cancellationToken);
            }
        }

        // Top-level files inside the game folder are also considered when they look like saves.
        if (depth == 0)
        {
            string[] topFiles;
            try { topFiles = Directory.GetFiles(current); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return; }
            foreach (var file in topFiles)
            {
                if (IsSaveFile(file)) results.Add(MakeDiscovered(appRoot, file, rootKey, appId));
            }
        }
    }

    private static void CollectAll(string root, string rootKey, string appId, List<DiscoveredSaveFile> results, CancellationToken cancellationToken)
    {
        var count = 0;
        Enumerate(root, root, rootKey, appId, 0, results, ref count, cancellationToken, filterExtensions: false);
    }

    private static void CollectFiltered(string root, string current, string rootKey, string appId, List<DiscoveredSaveFile> results, CancellationToken cancellationToken)
    {
        var count = 0;
        Enumerate(current, current, rootKey, appId, 0, results, ref count, cancellationToken, filterExtensions: true);
    }

    /// <summary>Leaf folder names that by definition contain only save data; deep files under them are all
    /// candidates (subject to extension/hidden-name filters).</summary>
    private static readonly HashSet<string> SaveLeafFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Save", "Saves", "SaveData", "SaveGames", "Saved", "Remote", "Storage", "Backup", "Slot0", "Slot1",
        "Slot2", "Slot3", "Slot4", "Profiles", "PlayerData", "Autosave", "Autosaves",
    };

    private static void CollectNativeRoot(
        string template,
        string rootKey,
        string appId,
        string title,
        List<DiscoveredSaveFile> results,
        CancellationToken cancellationToken)
    {
        var sanitized = SanitizeTitle(title);
        var pattern = Expand(template.Replace("{title}", sanitized, StringComparison.Ordinal), appId, null);
        if (pattern is null) return;
        foreach (var directory in ExpandWildcard(pattern, title))
        {
            if (!Directory.Exists(directory)) continue;
            var leaf = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (SaveLeafFolderNames.Contains(leaf))
            {
                // The save-typed leaf already declares the contents of every file inside it; deeply nested files
                // count as saves, filtered only by the save-file extension list.
                CollectFiltered(directory, directory, rootKey, appId, results, cancellationToken);
            }
            else
            {
                // Ambiguous root (e.g. the game folder itself): walk tokens only to avoid vacuuming unrelated files.
                TokenScan(directory, directory, rootKey, appId, 0, results, cancellationToken);
            }
        }
    }

    private static IEnumerable<string> ExpandWildcard(string expandedPattern, string? titleToMatch = null)
    {
        if (!expandedPattern.Contains('*'))
        {
            yield return expandedPattern;
            yield break;
        }

        var wildcardIndex = expandedPattern.IndexOf('*');
        var prefix = expandedPattern[..wildcardIndex];
        var suffix = expandedPattern[(wildcardIndex + 1)..];
        var searchRoot = prefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(searchRoot)) yield break;

        string[] children;
        try { children = Directory.GetDirectories(searchRoot).Where(d => !SkipDirectories.Contains(Path.GetFileName(d))).ToArray(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { yield break; }

        foreach (var child in children)
        {
            var childName = Path.GetFileName(child);
            if (!string.IsNullOrWhiteSpace(titleToMatch) && !suffix.Contains(titleToMatch, StringComparison.OrdinalIgnoreCase))
            {
                var sanitizedTitle = SanitizeTitle(titleToMatch);
                if (!suffix.Contains(sanitizedTitle, StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsTitleMatch(childName, titleToMatch))
                        continue;
                }
            }

            yield return Path.Combine(child, suffix.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
    }

    private static bool IsTitleMatch(string folderName, string title)
    {
        if (string.IsNullOrWhiteSpace(folderName) || string.IsNullOrWhiteSpace(title)) return false;
        if (string.Equals(folderName, title, StringComparison.OrdinalIgnoreCase)) return true;

        var cleanFolder = SanitizeTitle(folderName);
        var cleanTitle = SanitizeTitle(title);
        if (string.Equals(cleanFolder, cleanTitle, StringComparison.OrdinalIgnoreCase)) return true;

        var normFolder = NormalizeAlphaNumeric(cleanFolder);
        var normTitle = NormalizeAlphaNumeric(cleanTitle);
        if (normFolder.Length > 0 && normTitle.Length > 0 && normFolder == normTitle) return true;

        var expandedFolder = ExpandAcronyms(cleanFolder);
        var expandedTitle = ExpandAcronyms(cleanTitle);
        if (string.Equals(expandedFolder, expandedTitle, StringComparison.OrdinalIgnoreCase)) return true;

        var normExpFolder = NormalizeAlphaNumeric(expandedFolder);
        var normExpTitle = NormalizeAlphaNumeric(expandedTitle);
        if (normExpFolder.Length >= 4 && (normExpFolder == normExpTitle || normExpTitle.Contains(normExpFolder) || normExpFolder.Contains(normExpTitle)))
        {
            var minLen = Math.Min(normExpFolder.Length, normExpTitle.Length);
            var maxLen = Math.Max(normExpFolder.Length, normExpTitle.Length);
            if ((double)minLen / maxLen >= 0.55) return true;
        }

        var folderWords = GetSignificantWords(expandedFolder);
        var titleWords = GetSignificantWords(expandedTitle);
        if (folderWords.Count >= 2 && titleWords.Count >= 2)
        {
            var common = folderWords.Intersect(titleWords, StringComparer.OrdinalIgnoreCase).Count();
            if ((double)common / folderWords.Count >= 0.75) return true;
        }

        return false;
    }

    private static HashSet<string> GetSignificantWords(string text)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "the", "a", "an", "of", "and", "in", "to", "for" };
        var matches = Regex.Matches(text, @"\b[a-zA-Z0-9]{2,}\b");
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in matches)
        {
            if (!stopWords.Contains(m.Value)) set.Add(m.Value);
        }
        return set;
    }

    private static string NormalizeAlphaNumeric(string text) =>
        new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string ExpandAcronyms(string text) =>
        Regex.Replace(text, @"\bGrand\s+Theft\s+Auto\b", "GTA", RegexOptions.IgnoreCase);

    private static string SanitizeTitle(string title) =>
        new string(title.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();

    private static bool IsSaveToken(string? directoryName)
    {
        if (string.IsNullOrWhiteSpace(directoryName)) return false;
        return SaveTokens.Any(token => string.Equals(token, directoryName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSaveFile(string path)
    {
        var name = Path.GetFileName(path);
        if (SkipNames.Any(skip => string.Equals(skip, name, StringComparison.OrdinalIgnoreCase))) return false;
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension)) return true; // extensionless container saves (WGS, custom formats)
        return SaveExtensions.Contains(extension);
    }

    private static DiscoveredSaveFile MakeDiscovered(string root, string file, string rootKey, string appId)
    {
        string relative;
        try { relative = Path.GetRelativePath(root, file); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { relative = Path.GetFileName(file); }
        var info = new FileInfo(file);
        string hash;
        try
        {
            using var stream = File.OpenRead(file);
            hash = Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { hash = string.Empty; }
        var repoPath = $"AppID/{appId}/save/{SanitizeRepoRelative(relative)}";
        return new DiscoveredSaveFile(appId, rootKey, relative, file, info.Exists ? info.Length : 0, hash, repoPath);
    }

    private static string SanitizeRepoRelative(string relative) =>
        relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static void Enumerate(
        string root,
        string current,
        string rootKey,
        string appId,
        int depth,
        List<DiscoveredSaveFile> results,
        ref int count,
        CancellationToken cancellationToken,
        bool filterExtensions)
    {
        if (depth > MaximumDepth || count >= MaximumFilesPerAppId) return;
        cancellationToken.ThrowIfCancellationRequested();
        string[] files;
        string[] directories;
        try
        {
            files = Directory.GetFiles(current);
            directories = Directory.GetDirectories(current).Where(d => !SkipDirectories.Contains(Path.GetFileName(d))).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            if (count >= MaximumFilesPerAppId) return;
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            if (SkipNames.Any(skip => string.Equals(skip, name, StringComparison.OrdinalIgnoreCase))) continue;
            var extension = Path.GetExtension(file);
            if (filterExtensions && !string.IsNullOrEmpty(extension) && !SaveExtensions.Contains(extension)) continue;
            string relative;
            try { relative = Path.GetRelativePath(root, file); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { continue; }
            if (relative.StartsWith("..", StringComparison.Ordinal)) continue;
            var info = new FileInfo(file);
            if (info.Length > MaximumFileBytes) continue;
            string hash;
            try
            {
                using var stream = File.OpenRead(file);
                hash = Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            var repoRelative = relative.Replace(Path.DirectorySeparatorChar, '/');
            var repoPath = $"AppID/{appId}/save/{repoRelative}";
            results.Add(new DiscoveredSaveFile(appId, rootKey, relative, file, info.Length, hash, repoPath));
            count++;
        }

        foreach (var directory in directories)
        {
            Enumerate(root, directory, rootKey, appId, depth + 1, results, ref count, cancellationToken, filterExtensions);
        }
    }

    private static string? Expand(string template, string appId, string? uid)
    {
        var expanded = Environment.ExpandEnvironmentVariables(template)
            .Replace("{appid}", appId, StringComparison.Ordinal)
            .Replace("{uid}", uid ?? string.Empty, StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(expanded) ? null : expanded;
    }
}
