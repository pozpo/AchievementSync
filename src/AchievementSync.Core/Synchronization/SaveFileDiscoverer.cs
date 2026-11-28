using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AchievementSync.Core.Metadata;
using AchievementSync.Core.Platforms;

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
        ".sav", ".save", ".sl2", ".co2", ".dat", ".bin", ".slot", ".profile", ".slt", ".db", ".sqlite",
        ".xml", ".tres", ".rpgsave", ".ess", ".fos", ".srm", ".mcr", ".gpd", ".autosave",
    };

    // A game-folder root such as %APPDATA%\\EldenRing can contain ordinary config files beside its save.
    // Only distinctive save extensions are accepted at that ambiguous level; recognized Save folders use SaveExtensions.
    private static readonly HashSet<string> DirectNativeSaveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sav", ".save", ".sl2", ".co2", ".slot", ".profile", ".slt", ".tres", ".rpgsave", ".ess", ".fos", ".srm", ".mcr", ".gpd", ".autosave",
    };

    private static readonly string[] SaveTokens =
        ["Save", "Saves", "SaveData", "SaveGames", "Saved", "SavedGames", "Slot0", "Slot1", "Slot2", "Slot3", "Slot4", "PlayerData"];

    private static readonly string[] SkipNames =
        ["achievements.json", "achievements.cfg", "achievements.dat", "playtime.txt", "unlock-state.json", "steam_appid.txt", "stats.db", "TROPCONF.SFM", "TROPUSR.DAT", "TROPHY.TRP"];

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Crashes", "CrashReport", "CrashReports", "Crash", "Logs", "Log", "Screenshots",
        "ShaderCache", "Shaders", "D3D12", "D3DDriverByteCodeBlob", "WebCache", "GPUCache",
        "Mozilla", "Firefox", "Google", "Chrome", "Chromium", "BraveSoftware", "Opera Software", "Vivaldi",
        "Yandex", "MicrosoftEdge", "User Data", "Application Data", "Local Storage", "Session Storage",
        "IndexedDB", "Service Worker", "Extensions", "Cookies", "Code Cache", "CrashDumps", "Temp",
        "Settings", "Config", "Configs",
    };

    // Browser and shell profiles contain SQLite databases, JSON and other extensions that also appear in
    // game save folders. They are never treated as unlinked game-save roots.
    private static readonly HashSet<string> SkipNativeAppDataRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mozilla", "Google", "BraveSoftware", "Opera Software", "Vivaldi", "Yandex", "MicrosoftEdge",
        "Packages", "ConnectedDevicesPlatform", "CrashDumps", "Temp", "Microsoft", "Valve", "Steam",
        "Discord", "NVIDIA", "NVIDIA Corporation", "Adobe", "Electronic Arts", "EA Desktop",
        "Ubisoft Game Launcher", "Epic Games", "GOG.com", "Battle.net", "Riot Games", "Riot Client",
    };

    private static readonly HashSet<string> BrowserProfilePathNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mozilla", "Firefox", "Google", "Chrome", "Chromium", "BraveSoftware", "Opera Software",
        "Vivaldi", "Yandex", "MicrosoftEdge", "Edge",
    };

    // Ambiguous AppData folders require a precise save leaf. Generic names like Profiles/Storage are
    // common in browser and application data and are only accepted in platform-specific configured roots.
    private static readonly HashSet<string> LooseNativeSaveLeafFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Save", "Saves", "SaveData", "SaveGames", "Saved", "SavedGames", "Remote", "PlayerData", "Autosave", "Autosaves",
    };

    private static readonly HashSet<string> ConfiguredSaveLeafFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Save", "Saves", "SaveData", "SaveGames", "Saved", "SavedGames", "PlayerData", "Autosave", "Autosaves",
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
        ("AppData Roaming Save", @"%APPDATA%\{title}\Save"),
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

    private static bool IsGameSaveRoot(string rootKey) =>
        !rootKey.Contains("settings", StringComparison.OrdinalIgnoreCase)
        && !rootKey.Contains("achievement", StringComparison.OrdinalIgnoreCase)
        && !rootKey.Equals("CreamApi", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnscopedApplicationDataRoot(string root)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { return true; }

        var segments = fullPath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(BrowserProfilePathNames.Contains)) return true;

        // A whole roaming/local profile is too broad: it contains unrelated application data.
        if (segments.Length > 0 && (segments[^1].Equals("AppData", StringComparison.OrdinalIgnoreCase)
            || segments[^1].Equals("Roaming", StringComparison.OrdinalIgnoreCase)
            || segments[^1].Equals("Local", StringComparison.OrdinalIgnoreCase)
            || segments[^1].Equals("LocalLow", StringComparison.OrdinalIgnoreCase)))
        {
            if (segments[^1].Equals("AppData", StringComparison.OrdinalIgnoreCase)) return true;
            if (segments.Length >= 2 && segments[^2].Equals("AppData", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

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
                    if (!IsGameSaveRoot(key)) continue;
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

    /// <summary>
    /// Finds native saves under known titles and safely identifies previously unknown game folders from
    /// distinctive save files. Unknown folders are returned as temporary native IDs for exact Steam title resolution.
    /// </summary>
    public static Task<IReadOnlyList<DiscoveredSaveFile>> DiscoverUnlinkedNativeSavesAsync(
        IReadOnlyDictionary<string, string> appIdToTitle,
        CancellationToken cancellationToken = default,
        string? roamingRootOverride = null,
        string? localAppDataRootOverride = null)
    {
        ArgumentNullException.ThrowIfNull(appIdToTitle);
        return Task.Run(() =>
        {
            var titleToAppId = appIdToTitle
                .Where(pair => AppIdValidator.IsValid(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .GroupBy(pair => NormalizeAlphaNumeric(pair.Value), StringComparer.Ordinal)
                .Where(group => group.Key.Length > 0 && group.Select(pair => pair.Key).Distinct(StringComparer.Ordinal).Count() == 1)
                .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);
            var roots = new[]
            {
                (Key: "roaming", Path: roamingRootOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
                (Key: "local", Path: localAppDataRootOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            };
            var results = new List<DiscoveredSaveFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (rootKey, root) in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                string[] gameDirectories;
                try
                {
                    gameDirectories = Directory.GetDirectories(root)
                        .Where(directory => !SkipDirectories.Contains(Path.GetFileName(directory))
                            && !SkipNativeAppDataRoots.Contains(Path.GetFileName(directory)))
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .Take(2000)
                        .ToArray();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                   or System.Security.SecurityException) { continue; }

                foreach (var gameDirectory in gameDirectories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if ((File.GetAttributes(gameDirectory) & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                       or System.Security.SecurityException) { continue; }

                    var title = Path.GetFileName(gameDirectory);
                    var normalizedTitle = NormalizeAlphaNumeric(title);
                    if (normalizedTitle.Length < 3) continue;
                    var appId = titleToAppId.GetValueOrDefault(normalizedTitle);
                    var isKnownTitle = appId is not null;
                    appId ??= $"native-{rootKey}-{normalizedTitle}";

                    var platformRootKey = $"native-appdata-{rootKey}";
                    var planId = appId;
                    var nativeRepoPrefix = $"AppID/{appId}/save/{platformRootKey}/";
                    var firstCandidateIndex = results.Count;

                    string[] directFiles;
                    try { directFiles = Directory.GetFiles(gameDirectory, "*", SearchOption.TopDirectoryOnly); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                       or System.Security.SecurityException) { directFiles = []; }
                    foreach (var file in directFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!IsDirectNativeSaveFile(file)) continue;
                        AddNativeSave(gameDirectory, file, platformRootKey, planId, title, null,
                            nativeRepoPrefix, results, seen);
                    }

                    var saveDirectories = (isKnownTitle
                            ? FindLooseNativeSaveLeafDirectories(gameDirectory, cancellationToken)
                                .Concat(FindCandidateNativeSaveLeafDirectories(gameDirectory, cancellationToken))
                            : FindCandidateNativeSaveLeafDirectories(gameDirectory, cancellationToken))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    foreach (var saveDirectory in saveDirectories)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var inferredSaveFolder = !ConfiguredSaveLeafFolderNames.Contains(Path.GetFileName(saveDirectory));
                        CollectLooseNativeSaveTree(gameDirectory, saveDirectory, platformRootKey, planId, title,
                            null, nativeRepoPrefix, results, seen, cancellationToken,
                            distinctiveFilesOnly: inferredSaveFolder);
                    }

                    if (!isKnownTitle)
                    {
                        var candidateSaves = results.Skip(firstCandidateIndex).ToArray();
                        // Generic application .dat/.db/XML files are common at a profile root. A distinctive
                        // extension there, or any eligible file inside an explicitly named save folder, is
                        // enough evidence to try an exact title match.
                        var hasDistinctiveDirectSave = candidateSaves.Any(save =>
                            IsDirectNativeSaveFile(save.SourcePath)
                            && Path.GetDirectoryName(save.SourcePath)?.Equals(gameDirectory, StringComparison.OrdinalIgnoreCase) == true);
                        var hasSaveLeafPayload = saveDirectories.Any(saveDirectory =>
                            candidateSaves.Any(save => IsUnderRoot(saveDirectory, save.SourcePath)));
                        if (!hasDistinctiveDirectSave && !hasSaveLeafPayload)
                        {
                            foreach (var save in candidateSaves) seen.Remove(Path.GetFullPath(save.SourcePath));
                            results.RemoveRange(firstCandidateIndex, candidateSaves.Length);
                        }
                    }
                }
            }

            return (IReadOnlyList<DiscoveredSaveFile>)results;
        }, cancellationToken);
    }

    /// <summary>Resolves a manifest entry back to a local destination on this PC (used by restore).</summary>
    public static string? ResolveLocalPath(string rootKey, string appId, string relativePath)
        => ResolveLocalPath(rootKey, appId, relativePath, null, null);

    /// <summary>Resolves known save roots, configured platform roots, or a portable source path from a manifest.</summary>
    public static string? ResolveLocalPath(string rootKey, string appId, string relativePath,
        string? recordedSourcePath, PlatformSourceOptions? options)
    {
        var template = SaveRoots.FirstOrDefault(root => string.Equals(root.Key, rootKey, StringComparison.Ordinal))
                             .Template;
        if (template is not null)
        {
            var root = Expand(template, appId, null);
            return root is null ? null : CombineSafe(root, relativePath);
        }

        var recordedPath = PathSanitizer.Expand(recordedSourcePath);
        if (rootKey.StartsWith("platform:", StringComparison.OrdinalIgnoreCase) && options is not null)
        {
            var platformRoot = GetConfiguredPlatformRoot(rootKey["platform:".Length..], options);
            var configuredPath = !string.IsNullOrWhiteSpace(platformRoot) && Directory.Exists(platformRoot)
                ? CombineSafe(platformRoot, relativePath)
                : null;
            if (recordedPath is null) return configuredPath;
            if (configuredPath is null) return recordedPath;
            try
            {
                return string.Equals(Path.GetFullPath(configuredPath), Path.GetFullPath(recordedPath), StringComparison.OrdinalIgnoreCase)
                    ? configuredPath
                    : recordedPath;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                return recordedPath;
            }
        }

        // Native game roots and older custom roots use a profile-tokenised per-file path in the manifest.
        return recordedPath;
    }

    /// <summary>Discovers save data beside configured platform achievement sources. Each source game is
    /// stored under its own GitHub backup folder so it does not require a Steam AppID.</summary>
    public static Task<IReadOnlyList<DiscoveredSaveFile>> DiscoverPlatformSavesAsync(
        IEnumerable<PlatformGameSnapshot> snapshots,
        PlatformSourceOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() =>
        {
            var results = new List<DiscoveredSaveFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var snapshot in snapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var (rootKey, configuredRoot) in PlatformRoots(snapshot.Platform, options))
                {
                    if (string.IsNullOrWhiteSpace(configuredRoot) || !Directory.Exists(configuredRoot)) continue;
                    var root = Path.GetFullPath(configuredRoot);
                    var sourceRoot = "platform:" + rootKey;
                    var repoFolder = $"PlatformSave/{SanitizeRepoSegment(snapshot.Platform)}/{SanitizeRepoSegment(rootKey)}/{SanitizeRepoSegment(snapshot.GameId)}";
                    var directories = FindMatchingGameDirectories(root, snapshot, cancellationToken).ToArray();
                    foreach (var gameDirectory in directories)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var isDeclaredSaveRoot = snapshot.Version?.EndsWith("-save-root", StringComparison.OrdinalIgnoreCase) == true
                                                 && IsUnderRoot(root, gameDirectory);
                        if (isDeclaredSaveRoot || IsSaveAreaPath(root, gameDirectory)
                            || (snapshot.Platform.Equals("recomp", StringComparison.OrdinalIgnoreCase)
                                && snapshot.GameId.Equals("LADXHD", StringComparison.OrdinalIgnoreCase)
                                && rootKey.Equals("ladxhd", StringComparison.OrdinalIgnoreCase)))
                        {
                            CollectPlatformFiles(root, gameDirectory, sourceRoot, snapshot.GameId, repoFolder,
                                results, seen, cancellationToken);
                        }

                        foreach (var saveDirectory in FindSaveLeafDirectories(gameDirectory, cancellationToken))
                            CollectPlatformFiles(root, saveDirectory, sourceRoot, snapshot.GameId, repoFolder,
                                results, seen, cancellationToken);
                    }
                }
            }

            // Some emulator roots contain saves even when there is no achievement snapshot yet. Scan only
            // explicitly configured roots and recognized save folders so save backup does not depend on
            // achievement discovery succeeding first.
            foreach (var (platform, rootKey, configuredRoot) in ConfiguredPlatformRoots(options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(configuredRoot)
                    || IsUnscopedApplicationDataRoot(configuredRoot)
                    || !Directory.Exists(configuredRoot)) continue;
                var root = Path.GetFullPath(configuredRoot);
                foreach (var saveDirectory in FindSaveDataDirectories(root, cancellationToken))
                {
                    var gameId = InferSaveGameId(root, saveDirectory);
                    var repositoryFolder = $"PlatformSave/{SanitizeRepoSegment(platform)}/{SanitizeRepoSegment(rootKey)}/{SanitizeRepoSegment(gameId)}";
                    CollectPlatformFiles(root, saveDirectory, "platform:" + rootKey, gameId, repositoryFolder,
                        results, seen, cancellationToken);
                }
            }

            return (IReadOnlyList<DiscoveredSaveFile>)results;
        }, cancellationToken);
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
        Enumerate(root, root, rootKey, appId, 0, results, ref count, cancellationToken,
            filterExtensions: true, allowExtensionless: false);
    }

    private static void CollectFiltered(string root, string current, string rootKey, string appId, List<DiscoveredSaveFile> results, CancellationToken cancellationToken)
    {
        var count = 0;
        Enumerate(current, current, rootKey, appId, 0, results, ref count, cancellationToken,
            filterExtensions: true, allowExtensionless: true);
    }

    /// <summary>Leaf folder names that by definition contain only save data; deep files under them are all
    /// candidates (subject to extension/hidden-name filters).</summary>
    private static readonly HashSet<string> SaveLeafFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Save", "Saves", "SaveData", "SaveGames", "Saved", "Remote", "Storage", "Backup", "Slot0", "Slot1",
        "Slot2", "Slot3", "Slot4", "Profiles", "Profile", "PlayerData", "User", "UserData", "Autosave", "Autosaves",
    };

    private static readonly HashSet<string> SaveAreaPathNames = new(SaveLeafFolderNames, StringComparer.OrdinalIgnoreCase)
    {
        "SaveGames", "SavedGames", "Savegames", "Savedata", "UserData", "UserData", "userdata", "savegames", "savedata"
    };

    private static IEnumerable<(string Key, string? Root)> PlatformRoots(string platform, PlatformSourceOptions options) =>
        platform.ToLowerInvariant() switch
        {
            "xbox360" => [("xbox360", options.Xbox360Root), ("xenia", options.XeniaRoot)],
            "playstation" => [("rpcs3", options.Rpcs3Root), ("ps4", options.Ps4Root), ("ps5", options.Ps5Root)],
            "recomp" => [("recomp", options.RecompRoot), ("ladxhd", options.LadxhdRoot)],
            "uplay" => [("uplayrune", options.UplayRuneRoot), ("uplay", options.UplayRoot), ("uplay-cache", options.UplayCacheRoot)],
            "switch2" => [("lichii", options.LichiiRoot), ("eglantine", options.EglantineRoot)],
            "switch2recomp" => [("switch2recomp", options.Switch2RecompRoot)],
            "xbox" => [("xblen", options.XblenRoot), ("xboxpc", options.XboxPcRoot)],
            "gfwl" => [("gfwl", options.GfwlRoot)],
            "gog" => [("gog", options.GogRoot)],
            "epic" => [("epic", options.EpicRoot)],
            "ea" => [("ea", options.EaRoot)],
            _ => []
        };

    private static IEnumerable<(string Platform, string RootKey, string? Root)> ConfiguredPlatformRoots(PlatformSourceOptions options) =>
    [
        ("xbox", "xblen", options.XblenRoot),
        ("xbox", "xboxpc", options.XboxPcRoot),
        ("xbox360", "xbox360", options.Xbox360Root),
        ("xbox360", "xenia", options.XeniaRoot),
        ("gfwl", "gfwl", options.GfwlRoot),
        ("recomp", "recomp", options.RecompRoot),
        ("recomp", "ladxhd", options.LadxhdRoot),
        ("playstation", "rpcs3", options.Rpcs3Root),
        ("playstation", "ps4", options.Ps4Root),
        ("playstation", "ps5", options.Ps5Root),
        ("uplay", "uplayrune", options.UplayRuneRoot),
        ("uplay", "uplay", options.UplayRoot),
        ("uplay", "uplay-cache", options.UplayCacheRoot),
        ("gog", "gog", options.GogRoot),
        ("epic", "epic", options.EpicRoot),
        ("ea", "ea", options.EaRoot),
        ("switch2", "lichii", options.LichiiRoot),
        ("switch2", "eglantine", options.EglantineRoot),
        ("switch2recomp", "switch2recomp", options.Switch2RecompRoot),
    ];

    private static IEnumerable<string> FindSaveDataDirectories(string root, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        if (ConfiguredSaveLeafFolderNames.Contains(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            || Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Equals("Remote", StringComparison.OrdinalIgnoreCase))
            result.Add(root);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < 40_000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            if (depth >= MaximumDepth) continue;
            string[] children;
            try { children = Directory.GetDirectories(directory).Where(child => !SkipDirectories.Contains(Path.GetFileName(child))).ToArray(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (ConfiguredSaveLeafFolderNames.Contains(name))
                {
                    result.Add(child);
                    continue;
                }
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push((child, depth + 1));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string InferSaveGameId(string root, string saveDirectory)
    {
        try
        {
            var relative = Path.GetRelativePath(root, saveDirectory);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Where(segment => !string.IsNullOrWhiteSpace(segment) && !SaveLeafFolderNames.Contains(segment))
                .ToArray();
            var idSegment = segments.LastOrDefault(segment => Regex.IsMatch(segment,
                @"^(?:[0-9A-Fa-f]{16}|NPWR\d{5,}|CUSA\d{3,}|(?:BLUS|BLES|NPEB|NPUA|NPUB)\d{3,})$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            if (!string.IsNullOrWhiteSpace(idSegment)) return idSegment;
            if (segments.Length > 0) return segments[^1];
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { }
        return Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static string? GetConfiguredPlatformRoot(string key, PlatformSourceOptions options) => key.ToLowerInvariant() switch
    {
        "xbox360" => options.Xbox360Root,
        "xenia" => options.XeniaRoot,
        "recomp" => options.RecompRoot,
        "ladxhd" => options.LadxhdRoot,
        "rpcs3" => options.Rpcs3Root,
        "ps4" => options.Ps4Root,
        "ps5" => options.Ps5Root,
        "uplayrune" => options.UplayRuneRoot,
        "uplay" => options.UplayRoot,
        "uplay-cache" => options.UplayCacheRoot,
        "lichii" => options.LichiiRoot,
        "eglantine" => options.EglantineRoot,
        "switch2recomp" => options.Switch2RecompRoot,
        "xblen" => options.XblenRoot,
        "xboxpc" => options.XboxPcRoot,
        "gfwl" => options.GfwlRoot,
        "gog" => options.GogRoot,
        "epic" => options.EpicRoot,
        "ea" => options.EaRoot,
        _ => null
    };

    private static IEnumerable<string> FindMatchingGameDirectories(string root, PlatformGameSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (snapshot.Version?.EndsWith("-save-root", StringComparison.OrdinalIgnoreCase) == true
            && Directory.Exists(snapshot.SourcePath) && IsUnderRoot(root, snapshot.SourcePath))
            matches.Add(Path.GetFullPath(snapshot.SourcePath));

        var sourceDirectory = Directory.Exists(snapshot.SourcePath)
            ? snapshot.SourcePath
            : Path.GetDirectoryName(snapshot.SourcePath);
        if (!string.IsNullOrWhiteSpace(sourceDirectory) && Directory.Exists(sourceDirectory)
            && IsUnderRoot(root, sourceDirectory)
            && IsGameDirectoryName(Path.GetFileName(sourceDirectory), snapshot))
            matches.Add(Path.GetFullPath(sourceDirectory));

        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < 40_000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            if (depth > 10) continue;
            if (depth > 0 && IsGameDirectoryName(Path.GetFileName(directory), snapshot))
                matches.Add(directory);

            string[] children;
            try { children = Directory.GetDirectories(directory); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

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

        return matches.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsGameDirectoryName(string name, PlatformGameSnapshot snapshot) =>
        string.Equals(name, snapshot.GameId, StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrWhiteSpace(snapshot.Title) && IsTitleMatch(name, snapshot.Title));

    private static IEnumerable<string> FindSaveLeafDirectories(string gameDirectory, CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((gameDirectory, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < 10_000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            if (depth > 8) continue;
            string[] children;
            try { children = Directory.GetDirectories(directory); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (SaveLeafFolderNames.Contains(name))
                {
                    results.Add(child);
                    continue;
                }
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push((child, depth + 1));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
        return results;
    }

    private static IEnumerable<string> FindLooseNativeSaveLeafDirectories(string gameDirectory, CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((gameDirectory, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < 128)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            if (depth >= 4) continue;
            string[] children;
            try
            {
                children = Directory.GetDirectories(directory)
                    .Where(child => !SkipDirectories.Contains(Path.GetFileName(child)))
                    .Take(500)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or System.Security.SecurityException) { continue; }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (LooseNativeSaveLeafFolderNames.Contains(name))
                {
                    results.Add(child);
                    continue;
                }
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push((child, depth + 1));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                   or System.Security.SecurityException) { }
            }
        }

        return results;
    }

    private static IEnumerable<string> FindCandidateNativeSaveLeafDirectories(string gameDirectory, CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((gameDirectory, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < 256)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            if (depth >= 5) continue;
            string[] children;
            try
            {
                children = Directory.GetDirectories(directory)
                    .Where(child => !SkipDirectories.Contains(Path.GetFileName(child)))
                    .Take(256)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or System.Security.SecurityException) { continue; }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (ConfiguredSaveLeafFolderNames.Contains(name))
                {
                    results.Add(child);
                    continue;
                }
                if (ContainsDirectNativeSaveFile(child))
                {
                    // Some native game stores put saves directly in a profile/account subfolder
                    // (for example %APPDATA%\EldenRing\<SteamID>) instead of naming it Save.
                    results.Add(child);
                    continue;
                }
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push((child, depth + 1));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                   or System.Security.SecurityException) { }
            }
        }
        return results;
    }

    private static bool ContainsDirectNativeSaveFile(string directory)
    {
        try { return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Any(IsDirectNativeSaveFile); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or System.Security.SecurityException) { return false; }
    }

    private static bool IsDirectNativeSaveFile(string path)
    {
        var extension = Path.GetExtension(path);
        if (DirectNativeSaveExtensions.Contains(extension)) return true;
        return extension.Equals(".bak", StringComparison.OrdinalIgnoreCase) && IsPairedSaveBackup(path);
    }

    private static bool IsPairedSaveBackup(string path)
    {
        var withoutBackupExtension = Path.GetFileNameWithoutExtension(path);
        var originalExtension = Path.GetExtension(withoutBackupExtension);
        if (DirectNativeSaveExtensions.Contains(originalExtension)) return true;

        var directory = Path.GetDirectoryName(path);
        if (directory is null) return false;
        foreach (var extension in DirectNativeSaveExtensions)
        {
            if (File.Exists(Path.Combine(directory, withoutBackupExtension + extension))) return true;
        }
        return false;
    }

    private static bool IsSaveAreaPath(string configuredRoot, string path)
    {
        var rootName = Path.GetFileName(configuredRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (SaveAreaPathNames.Contains(rootName)) return true;
        try
        {
            var relative = Path.GetRelativePath(configuredRoot, path);
            return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(SaveAreaPathNames.Contains);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static void CollectPlatformFiles(string configuredRoot, string scanRoot, string rootKey, string gameId,
        string repositoryFolder, List<DiscoveredSaveFile> results, HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(scanRoot) || !IsUnderRoot(configuredRoot, scanRoot)) return;
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((scanRoot, 0));
        var visited = 0;
        var count = 0;
        while (pending.Count > 0 && visited++ < 20_000 && count < MaximumFilesPerAppId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            if (depth > MaximumDepth) continue;
            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(directory);
                children = Directory.GetDirectories(directory)
                    .Where(child => !SkipDirectories.Contains(Path.GetFileName(child))).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsSaveFile(file, allowExtensionless: true) || !seen.Add(Path.GetFullPath(file))) continue;
                var info = new FileInfo(file);
                if (info.Length > MaximumFileBytes) continue;
                string hash;
                try
                {
                    using var stream = File.OpenRead(file);
                    hash = Convert.ToHexString(SHA256.HashData(stream));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }

                string relative;
                try { relative = Path.GetRelativePath(configuredRoot, file); }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { continue; }
                if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) continue;
                var repoPath = $"{repositoryFolder}/save/{SanitizeRepoRelative(relative)}";
                results.Add(new DiscoveredSaveFile(gameId, rootKey, relative, file, info.Length, hash, repoPath, repositoryFolder));
                count++;
                if (count >= MaximumFilesPerAppId) break;
            }

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

    private static string? CombineSafe(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return null;
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var full = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
            return IsUnderRoot(fullRoot, full) ? full : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsUnderRoot(string root, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
            return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static string SanitizeRepoSegment(string value) =>
        new string(value.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.').ToArray()) is { Length: > 0 } safe
            ? safe
            : "unknown";

    private static string SanitizeNativeRepoSegment(string value)
    {
        var safe = SanitizeRepoSegment(value);
        while (safe.Contains("..", StringComparison.Ordinal))
            safe = safe.Replace("..", "_", StringComparison.Ordinal);
        return safe is "." or ".." ? "unknown" : safe;
    }

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

    private static void CollectLooseNativeSaveTree(
        string gameDirectory,
        string saveDirectory,
        string rootKey,
        string appId,
        string title,
        string? repositoryFolder,
        string repositoryPrefix,
        List<DiscoveredSaveFile> results,
        HashSet<string> seen,
        CancellationToken cancellationToken,
        bool distinctiveFilesOnly = false)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((saveDirectory, 0));
        var visited = 0;
        var count = 0;
        while (pending.Count > 0 && visited++ < 10_000 && count < MaximumFilesPerAppId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            if (depth > MaximumDepth) continue;
            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(directory);
                children = Directory.GetDirectories(directory)
                    .Where(child => !SkipDirectories.Contains(Path.GetFileName(child))).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or System.Security.SecurityException) { continue; }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (distinctiveFilesOnly ? !IsDirectNativeSaveFile(file) : !IsSaveFile(file, allowExtensionless: true)) continue;
                var before = results.Count;
                AddNativeSave(gameDirectory, file, rootKey, appId, title, repositoryFolder,
                    repositoryPrefix, results, seen);
                if (results.Count > before) count++;
            }

            foreach (var child in children)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push((child, depth + 1));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                   or System.Security.SecurityException) { }
            }
        }
    }

    private static void AddNativeSave(
        string gameDirectory,
        string file,
        string rootKey,
        string appId,
        string title,
        string? repositoryFolder,
        string repositoryPrefix,
        List<DiscoveredSaveFile> results,
        HashSet<string> seen)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(file);
            if (!IsUnderRoot(gameDirectory, fullPath) || !seen.Add(fullPath)) return;
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0) return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                                           or System.Security.SecurityException or NotSupportedException) { return; }

        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length > MaximumFileBytes) return;
        string hash;
        try
        {
            using var stream = File.OpenRead(fullPath);
            hash = Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or System.Security.SecurityException) { return; }

        string relative;
        try { relative = Path.GetRelativePath(gameDirectory, fullPath); }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { return; }
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return;
        var repoPath = repositoryPrefix + SanitizeRepoRelative(relative);
        results.Add(new DiscoveredSaveFile(appId, rootKey, relative, fullPath, info.Length, hash,
            repoPath, repositoryFolder, title));
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

    private static bool IsSaveFile(string path, bool allowExtensionless = false)
    {
        var name = Path.GetFileName(path);
        if (SkipNames.Any(skip => string.Equals(skip, name, StringComparison.OrdinalIgnoreCase))) return false;
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension)) return allowExtensionless;
        if (extension.Equals(".bak", StringComparison.OrdinalIgnoreCase)) return IsPairedSaveBackup(path);
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
        bool filterExtensions,
        bool allowExtensionless)
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
            if (filterExtensions && (string.IsNullOrEmpty(extension)
                    ? !allowExtensionless
                    : !SaveExtensions.Contains(extension))) continue;
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
            Enumerate(root, directory, rootKey, appId, depth + 1, results, ref count, cancellationToken,
                filterExtensions, allowExtensionless);
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
