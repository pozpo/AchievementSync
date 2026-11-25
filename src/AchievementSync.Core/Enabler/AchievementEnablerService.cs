using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AchievementSync.Core.Metadata;

namespace AchievementSync.Core.Enabler;

public enum EnablerTarget
{
    SteamEmulator,
    SteamColdClient,
    SteamColdLoaderWithRegularEmulator,
    UplayR2,
    UplayR1,
    EpicEmulator,
    GogUniverseLan,
    EaOrigin
}

public sealed record EnablerDetection(
    string GameDirectory,
    string? Executable,
    string? SteamAppId,
    EnablerTarget SuggestedTarget,
    IReadOnlyList<string> Evidence);

public sealed record EnablerPlanItem(
    string Kind,
    string Destination,
    string? Source,
    byte[]? Content,
    string Description);

public sealed record EnablerPlan(
    EnablerDetection Detection,
    EnablerTarget Target,
    string? MetadataGameId,
    IReadOnlyList<EnablerPlanItem> Items,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Blockers);

public sealed record EnablerApplyResult(int FilesWritten, int FilesSkipped, IReadOnlyList<string> Messages);
public sealed record EnablerGeneratorRefreshResult(bool Attempted, bool Succeeded, string Message);

/// <summary>Native, PowerShell-free Achievement Enabler. Detects how a game is distributed, then
/// prepares the emulator configuration and copies bundled payload files from the local adapter
/// archive. Existing files are backed up once as <c>.achievementsync-backup</c> before replacement.</summary>
public sealed class AchievementEnablerService
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex NumericSteamId = new(@"^[1-9][0-9]{0,9}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SafeGameId = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string _adaptersRoot;
    private readonly string _metadataRoot;

    public AchievementEnablerService(string? adaptersRoot = null, string? metadataRoot = null)
    {
        _adaptersRoot = ResolveAdaptersRoot(adaptersRoot);
        _metadataRoot = Path.GetFullPath(metadataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AchievementSync", "metadata"));
    }

    public string AdaptersRoot => _adaptersRoot;

    private static string ResolveAdaptersRoot(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return Path.GetFullPath(configured);
        var baseDirectory = AppContext.BaseDirectory;
        foreach (var candidate in new[] { Path.Combine(baseDirectory, "adapters"), @"C:\AchievementSync\adapters" })
            if (Directory.Exists(candidate)) return candidate;
        return Path.Combine(baseDirectory, "adapters");
    }

    public EnablerDetection Detect(string gamePathOrExecutable) =>
        DetectAsync(gamePathOrExecutable).GetAwaiter().GetResult();

    public Task<EnablerDetection> DetectAsync(string gamePathOrExecutable) =>
        DetectAsync(gamePathOrExecutable, explicitExecutablePath: null);

    public async Task<EnablerDetection> DetectAsync(string gamePathOrExecutable, string? explicitExecutablePath)
    {
        var full = Path.GetFullPath(gamePathOrExecutable);
        var isFile = File.Exists(full) && !Directory.Exists(full);
        var executable = !string.IsNullOrWhiteSpace(explicitExecutablePath) && File.Exists(explicitExecutablePath)
            ? Path.GetFullPath(explicitExecutablePath)
            : (isFile ? full : null);
        var originalRoot = isFile ? Path.GetDirectoryName(full)! : full;
        var directory = originalRoot;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);

        var evidence = new List<string>();

        // Check if steam_api64.dll or steam_api.dll exists directly or in a child directory
        var dllFolder = FindSteamApiFolder(directory);
        if (dllFolder is not null && !string.Equals(dllFolder, directory, StringComparison.OrdinalIgnoreCase))
        {
            evidence.Add($"Steam API DLL found in '{Path.GetRelativePath(directory, dllFolder)}'. Target folder set to DLL location.");
            directory = dllFolder;
        }

        var files = SafeListFiles(directory);
        bool Has(string name) => files.Contains(name, StringComparer.OrdinalIgnoreCase);

        var target = EnablerTarget.SteamEmulator;
        if (Has("upc_r2_loader.dll") || Has("upc_r2_loader64.dll") || Has("uplay_r2_loader.dll") || Has("uplay_r2_loader64.dll"))
        {
            target = EnablerTarget.UplayR2;
            evidence.Add("Ubisoft UPC R2 loader found.");
        }
        else if (Has("upc_r1_loader.dll") || Has("upc_r1_loader64.dll") || Has("upc_r1.dll")
            || Has("uplay_r1_loader.dll") || Has("uplay_r1_loader64.dll") || Has("UbisoftGameLauncher64_Load.dll"))
        {
            target = EnablerTarget.UplayR1;
            evidence.Add("Ubisoft UPC R1 loader found.");
        }
        else if (Has("galaxy64.dll") || Has("galaxy.dll") || files.Any(name => name.StartsWith("goggame-", StringComparison.OrdinalIgnoreCase)))
        {
            target = EnablerTarget.GogUniverseLan;
            evidence.Add("GOG Galaxy library found.");
        }
        else if (Has("EOSSDK-Win64-Shipping.dll") || Has("EOSSDK-Win32-Shipping.dll") || Directory.Exists(Path.Combine(directory, ".egstore")))
        {
            target = EnablerTarget.EpicEmulator;
            evidence.Add("Epic Games Store marker found.");
        }
        else if ((Directory.Exists(Path.Combine(directory, "__Installer")) && File.Exists(Path.Combine(directory, "__Installer", "installerdata.xml")))
                 || File.Exists(Path.Combine(directory, "installerdata.xml")))
        {
            target = EnablerTarget.EaOrigin;
            evidence.Add("EA/Origin installer marker found.");
        }
        else
        {
            target = EnablerTarget.SteamEmulator;
            evidence.Add("Steam game detected; regular Steam emulator (Goldberg steam_api replacement + generated catalog) is selected.");
        }

        // Auto-discover game executable if a folder was selected (needed by ColdClient, Epic, GOG, EA)
        if (executable is null)
        {
            var candidates = FindCandidateExecutables(originalRoot, target);
            if (candidates.Count > 0)
            {
                executable = candidates[0];
                evidence.Add(candidates.Count == 1
                    ? $"Game executable auto-selected: '{Path.GetRelativePath(originalRoot, executable)}'."
                    : $"Found {candidates.Count} executable(s); auto-selected '{Path.GetRelativePath(originalRoot, executable)}' (you can change it in Game executable).");
            }
        }
        else
        {
            evidence.Add($"Selected game executable: '{Path.GetFileName(executable)}'.");
        }

        string? steamAppId = null;
        if (target == EnablerTarget.GogUniverseLan)
        {
            steamAppId = FindGogAppId(originalRoot) ?? FindGogAppId(directory);
            if (steamAppId is not null)
                evidence.Add($"GOG App ID resolved from goggame-*.info: {steamAppId}.");
        }
        else if (target == EnablerTarget.EpicEmulator)
        {
            steamAppId = AdapterOnlineMetadataService.FindLocalEpicNamespace(originalRoot)
                         ?? AdapterOnlineMetadataService.FindLocalEpicNamespace(directory);
            if (steamAppId is not null)
            {
                evidence.Add($"Epic namespace resolved from .egstore manifest: {steamAppId}.");
            }
            else
            {
                var epicTitle = InferGameTitle(originalRoot, executable);
                if (!string.IsNullOrWhiteSpace(epicTitle))
                {
                    var (ns, matchedTitle) = await AdapterOnlineMetadataService.LookupEpicNamespaceAsync(epicTitle).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(ns))
                    {
                        steamAppId = ns;
                        evidence.Add($"Epic namespace resolved via egdata.app ({matchedTitle ?? epicTitle}): {ns}.");
                    }
                }
            }
        }
        else if (target == EnablerTarget.EaOrigin)
        {
            var xmlPath = ResolveInstallerDataPath(directory) ?? ResolveInstallerDataPath(originalRoot);
            var (xmlTitle, _, xmlContentId, _, _) = ParseInstallerDataSummary(xmlPath);
            var lookupTitle = !string.IsNullOrWhiteSpace(xmlTitle) ? xmlTitle : InferGameTitle(originalRoot, executable);
            var cachedOrOnline = await AdapterOnlineMetadataService.LookupEaCatalogAsync(lookupTitle, xmlContentId).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cachedOrOnline?.SteamAppId))
            {
                steamAppId = cachedOrOnline.SteamAppId;
                evidence.Add($"Cross-listed Steam AppID resolved via ITAD/EA catalog: {steamAppId}.");
            }
            if (!string.IsNullOrWhiteSpace(cachedOrOnline?.OfrId))
            {
                evidence.Add($"EA Offer ID resolved: {cachedOrOnline.OfrId}.");
            }
        }

        if (steamAppId is null && target is not (EnablerTarget.GogUniverseLan or EnablerTarget.EpicEmulator))
        {
            steamAppId = await ReadSteamAppIdAsync(directory);
            if (steamAppId is null && Directory.GetParent(directory)?.FullName is { } parentDir)
            {
                steamAppId = await ReadSteamAppIdAsync(parentDir);
            }
        }

        // Uplay→Steam AppID mapping: if a Uplay loader was detected, try to resolve the Steam AppID.
        if (steamAppId is null && (Has("upc_r2_loader.dll") || Has("upc_r2_loader64.dll") || Has("uplay_r2_loader.dll") || Has("uplay_r2_loader64.dll")
            || Has("upc_r1_loader.dll") || Has("upc_r1_loader64.dll") || Has("upc_r1.dll")
            || Has("uplay_r1_loader.dll") || Has("uplay_r1_loader64.dll") || Has("UbisoftGameLauncher64_Load.dll")))
        {
            var uplayId = ReadUplayAppId(directory);
            if (uplayId is not null)
            {
                steamAppId = UplaySteamMapper.LookupSteamAppId(uplayId);
                if (steamAppId is not null)
                    evidence.Add($"Uplay ID {uplayId} mapped to Steam AppID {steamAppId} via Uplay→Steam mapping.");
            }
        }

        if (steamAppId is not null && target is not (EnablerTarget.GogUniverseLan or EnablerTarget.EpicEmulator))
            evidence.Add($"Steam AppID resolved: {steamAppId}.");
        if (Has("steam_api64.dll")) evidence.Add("64-bit Steam API library found (steam_api64.dll).");
        if (Has("steam_api.dll")) evidence.Add("32-bit Steam API library found (steam_api.dll).");

        return new EnablerDetection(directory, executable, steamAppId, target, evidence);
    }

    public IReadOnlyList<string> FindCandidateExecutables(string rootDirectory, EnablerTarget? target = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            return [];

        var fullRoot = Path.GetFullPath(rootDirectory);
        var folderName = Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var results = new List<string>();

        if (target is null or EnablerTarget.GogUniverseLan)
        {
            var shortcutTarget = ResolveGogLaunchShortcutTarget(fullRoot);
            if (!string.IsNullOrWhiteSpace(shortcutTarget) && File.Exists(shortcutTarget))
                results.Add(Path.GetFullPath(shortcutTarget));
        }

        var excludedSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_ColdClient", "release", "generate_emu_config", "parse_achievements_schema",
            "parse_controller_vdf", "nepice_settings", "epic_output", "adapters",
            "core", "__Installer", "__overlay"
        };

        var discovered = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(fullRoot, "*.exe", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MaxRecursionDepth = 6
            }))
            {
                var rel = Path.GetRelativePath(fullRoot, file);
                var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (segments.Take(segments.Length - 1).Any(s => excludedSegments.Contains(s)))
                    continue;

                var fileName = Path.GetFileName(file);
                if (IsIgnoredSetupOrEmulatorExecutable(fileName))
                    continue;

                discovered.Add(Path.GetFullPath(file));
            }
        }
        catch { }

        var ordered = discovered
            .OrderByDescending(p => Path.GetFileName(p).Contains("Shipping", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => !string.IsNullOrWhiteSpace(folderName) &&
                                    Path.GetFileNameWithoutExtension(p).Contains(folderName, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.Contains("Binaries", StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var path in ordered)
        {
            if (!results.Contains(path, StringComparer.OrdinalIgnoreCase))
                results.Add(path);
        }

        return results;
    }

    private static bool IsIgnoredSetupOrEmulatorExecutable(string fileName)
    {
        if (IsUtilityExecutable(fileName)) return true;
        var lower = fileName.ToLowerInvariant();
        return lower.EndsWith("_trial.exe", StringComparison.Ordinal)
               || lower.Contains("unitycrashhandler", StringComparison.Ordinal)
               || lower.StartsWith("steamclient_loader_", StringComparison.Ordinal)
               || lower.StartsWith("universallanserver", StringComparison.Ordinal)
               || lower == "origin_unwrapper.exe"
               || lower == "generate_emu_config.exe"
               || lower == "ueprereqsetup_x64.exe"
               || lower == "ue4prereqsetup_x64.exe";
    }

    private static string? ResolveGogLaunchShortcutTarget(string rootDirectory)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var lnk = Directory.EnumerateFiles(rootDirectory, "Launch *.lnk", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (lnk is null) return null;
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnk);
            string? target = shortcut.TargetPath as string;
            return !string.IsNullOrWhiteSpace(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? target
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? InferGameTitle(string directory, string? executable)
    {
        if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(executable);
                if (!string.IsNullOrWhiteSpace(info.ProductName) && !IsGenericTitle(info.ProductName))
                    return CleanGameTitle(info.ProductName);
            }
            catch { }
        }
        var folder = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(folder) ? null : CleanGameTitle(folder);
    }

    private static string? FindSteamApiFolder(string root)
    {
        if (File.Exists(Path.Combine(root, "steam_api64.dll")) || File.Exists(Path.Combine(root, "steam_api.dll")))
            return root;

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "steam_api*.dll", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 3,
                IgnoreInaccessible = true
            }))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("steam_api64.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("steam_api.dll", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetDirectoryName(file);
                }
            }
        }
        catch { }

        return null;
    }

    public EnablerPlan BuildPlan(EnablerDetection detection, EnablerTarget? target = null, string? gameIdOverride = null,
        bool includeSteamStubHelpers = false)
    {
        var chosen = target ?? detection.SuggestedTarget;
        var items = new List<EnablerPlanItem>();
        var warnings = new List<string>();
        var notes = new List<string>();
        var blockers = new List<string>();
        var normalizedDirectory = Path.GetFullPath(detection.GameDirectory);
        if (!Directory.Exists(normalizedDirectory)) throw new DirectoryNotFoundException(normalizedDirectory);
        detection = detection with { GameDirectory = normalizedDirectory };
        var gameId = string.IsNullOrWhiteSpace(gameIdOverride) ? null : gameIdOverride.Trim();
        if (gameId is null)
        {
            if (chosen == EnablerTarget.GogUniverseLan)
                gameId = FindGogAppId(detection.GameDirectory) ?? detection.SteamAppId;
            else
                gameId = detection.SteamAppId;
        }
        if (!string.IsNullOrWhiteSpace(gameId))
        {
            if (!SafeGameId.IsMatch(gameId) || gameId is "." or "..")
                throw new ArgumentException("The game ID can contain only letters, numbers, periods, underscores, and dashes.", nameof(gameIdOverride));
            if ((chosen is EnablerTarget.SteamEmulator or EnablerTarget.SteamColdClient or EnablerTarget.SteamColdLoaderWithRegularEmulator) && !NumericSteamId.IsMatch(gameId))
                throw new ArgumentException("Steam AppIDs must contain 1 to 10 digits and cannot be zero.", nameof(gameIdOverride));
        }
        switch (chosen)
        {
            case EnablerTarget.SteamEmulator:
                BuildSteamRegularPlan(detection, gameId, items, notes, blockers);
                break;
            case EnablerTarget.SteamColdClient:
                BuildSteamColdClientPlan(detection, gameId, items, notes, blockers, includeSteamStubHelpers);
                break;
            case EnablerTarget.SteamColdLoaderWithRegularEmulator:
                BuildSteamColdLoaderWithRegularEmulatorPlan(detection, gameId, items, notes, warnings, blockers);
                break;
            case EnablerTarget.UplayR1:
            case EnablerTarget.UplayR2:
                BuildUplayPlan(detection, chosen, gameId, items, notes, warnings, blockers);
                break;
            case EnablerTarget.EpicEmulator:
                BuildEpicPlan(detection, gameId, items, notes, warnings, blockers);
                break;
            case EnablerTarget.GogUniverseLan:
                BuildGogPlan(detection, gameId, items, notes, warnings, blockers);
                break;
            case EnablerTarget.EaOrigin:
                BuildEaPlan(detection, gameId, items, notes, warnings, blockers);
                break;
        }

        var plan = new EnablerPlan(detection, chosen, gameId, items, warnings, notes, blockers);
        var overwriteCount = items
            .Where(item => !item.Kind.Equals("delete", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Destination)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(File.Exists);
        if (overwriteCount > 0)
            warnings.Add($"{overwriteCount} existing file(s) will be backed up with a timestamp before replacement.");
        ValidatePlanPaths(plan);
        return plan;
    }

    public EnablerApplyResult Apply(EnablerPlan plan)
    {
        if (plan.Blockers.Count > 0)
            throw new InvalidOperationException("This plan cannot be applied until its required files or settings are available: " + string.Join("; ", plan.Blockers));

        var written = 0;
        var skipped = 0;
        var messages = new List<string>();
        var allowedSourceRoots = GetAllowedCopySourceRoots(plan);
        string? lastPatchedEaExe = null;

        foreach (var item in plan.Items)
        {
            try
            {
                ValidateItemPath(plan, item, allowedSourceRoots);

                if (item.Kind.Equals("delete", StringComparison.OrdinalIgnoreCase))
                {
                    if (!File.Exists(item.Destination))
                    {
                        skipped++;
                        continue;
                    }
                    File.Delete(item.Destination);
                    messages.Add($"Removed unneeded file: {Path.GetFileName(item.Destination)}");
                    written++;
                    continue;
                }

                if (item.Kind.Equals("patch-ea-exe", StringComparison.OrdinalIgnoreCase))
                {
                    var sourcePath = item.Source ?? item.Destination;
                    if (!File.Exists(sourcePath))
                    {
                        skipped++;
                        messages.Add($"Skipped EA executable patch ({Path.GetFileName(item.Destination)}): source not found.");
                        continue;
                    }

                    var bakPath = item.Destination + ".BAK";
                    if (!File.Exists(bakPath) && File.Exists(item.Destination))
                    {
                        File.Move(item.Destination, bakPath);
                        messages.Add($"Backed up original executable → {Path.GetFileName(bakPath)}");
                        sourcePath = bakPath;
                    }

                    var patchedBytes = OriginExePatcher.PatchExecutable(sourcePath, plan.Detection.GameDirectory);
                    WritePreparedFile(item.Destination, null, patchedBytes, AllowedRootFor(plan, item.Destination));
                    lastPatchedEaExe = item.Destination;
                    messages.Add($"Patched EA executable with .anadius import: {Path.GetFileName(item.Destination)}");
                    written++;
                    continue;
                }

                if (item.Kind.Equals("rename", StringComparison.OrdinalIgnoreCase))
                {
                    if (item.Source is null || !File.Exists(item.Source))
                    {
                        skipped++;
                        messages.Add($"Skipped rename of {Path.GetFileName(item.Source ?? "?")}: file not found.");
                        continue;
                    }
                    if (File.Exists(item.Destination))
                    {
                        skipped++;
                        messages.Add($"Skipped rename: {Path.GetFileName(item.Destination)} already exists.");
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
                    File.Move(item.Source, item.Destination);
                    messages.Add($"Renamed {Path.GetFileName(item.Source)} → {Path.GetFileName(item.Destination)}");
                    written++;
                    continue;
                }

                if (item.Kind.Equals("copy", StringComparison.OrdinalIgnoreCase))
                {
                    if (item.Source is null || !File.Exists(item.Source)
                        || !allowedSourceRoots.Any(root => IsPathWithin(root, item.Source)))
                    {
                        skipped++;
                        messages.Add($"Skipped {Path.GetFileName(item.Destination)}: payload not present.");
                        continue;
                    }

                    var backup = WritePreparedFile(item.Destination, item.Source, null, AllowedRootFor(plan, item.Destination));
                    if (backup is not null) messages.Add($"Backup saved: {Path.GetFileName(backup)}");
                    written++;
                    continue;
                }

                if (item.Content is null) { skipped++; continue; }
                var contentToWrite = item.Content;
                if (lastPatchedEaExe is not null &&
                    Path.GetFileName(item.Destination).Equals("anadius.cfg", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(lastPatchedEaExe))
                {
                    var cfgText = Utf8WithoutBom.GetString(contentToWrite);
                    if (cfgText.Contains("\"DENUVO_EXE_HASH\"", StringComparison.Ordinal))
                    {
                        var exeSha1 = ComputeFileSha1Hex(lastPatchedEaExe);
                        if (!string.IsNullOrEmpty(exeSha1))
                        {
                            cfgText = cfgText.Replace("\"DENUVO_EXE_HASH\"", $"\"{exeSha1}\"", StringComparison.Ordinal);
                            contentToWrite = Utf8WithoutBom.GetBytes(cfgText);
                        }
                    }
                }
                var savedBackup = WritePreparedFile(item.Destination, null, contentToWrite, AllowedRootFor(plan, item.Destination));
                if (savedBackup is not null) messages.Add($"Backup saved: {Path.GetFileName(savedBackup)}");
                written++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or InvalidOperationException)
            {
                skipped++;
                messages.Add($"{Path.GetFileName(item.Destination)} could not be written ({exception.GetType().Name}: {exception.Message}).");
            }
        }

        return new EnablerApplyResult(written, skipped, messages);
    }

    /// <summary>
    /// Runs the configured generate_emu_config executable for an AppID. Its existing login and
    /// refresh-token files stay in the generator folder; neither is read or copied by this app.
    /// The returned output is only used to prepare a reviewable plan.
    /// </summary>
    public async Task<EnablerGeneratorRefreshResult> RefreshSteamMetadataAsync(
        string appId,
        CancellationToken cancellationToken = default)
    {
        if (!NumericSteamId.IsMatch(appId))
            return new EnablerGeneratorRefreshResult(false, false, "A valid Steam AppID is required to refresh generated metadata.");

        EmbeddedGeneratorRuntime.EnsureReady();
        var generatorRoot = EmbeddedGeneratorRuntime.IsReady
            ? EmbeddedGeneratorRuntime.RootPath
            : FindGeneratorRoots().FirstOrDefault(root => File.Exists(Path.Combine(root, "generate_emu_config.exe")));
        if (generatorRoot is null)
            return new EnablerGeneratorRefreshResult(false, false,
                "The configured generator was not found. The plan will use existing generated output, if available.");

        var executable = Path.Combine(generatorRoot, "generate_emu_config.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = generatorRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-aw", "-cdx", "-clean", "-token", "-skip_inv", appId })
            startInfo.ArgumentList.Add(argument);

        startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

        var credentialStore = new SteamCredentialStore();
        EmbeddedGeneratorRuntime.RestoreRefreshToken(credentialStore);
        var credentials = credentialStore.Read();
        if (credentials is not null)
        {
            startInfo.EnvironmentVariables["GSE_CFG_USERNAME"] = credentials.Username;
            startInfo.EnvironmentVariables["GSE_CFG_PASSWORD"] = credentials.Password;
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return new EnablerGeneratorRefreshResult(true, false, "The metadata generator could not be started; existing output will be used if available.");

            var standardOutputDrain = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var standardErrorDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(standardOutputDrain, standardErrorDrain).ConfigureAwait(false);
                EmbeddedGeneratorRuntime.SyncRefreshTokenToStore(credentialStore);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                if (cancellationToken.IsCancellationRequested) throw;
                return new EnablerGeneratorRefreshResult(true, false, "Metadata refresh timed out; existing output will be used if available.");
            }

            if (process.ExitCode != 0 && FindSteamOutput(string.Empty, appId) is null)
                return new EnablerGeneratorRefreshResult(true, false,
                    $"The metadata generator exited with code {process.ExitCode}; existing output will be used if available.");
            if (FindSteamOutput(string.Empty, appId) is null)
                return new EnablerGeneratorRefreshResult(true, false,
                    "The generator finished but did not produce an AppID achievement catalog; existing output will be used if available.");

            var tokenPath = Path.Combine(generatorRoot, "refresh_tokens.json");
            if (File.Exists(tokenPath) && credentials is not null)
            {
                try
                {
                    credentials.RefreshToken = File.ReadAllText(tokenPath);
                    credentialStore.Write(credentials);
                }
                catch { }
            }

            return new EnablerGeneratorRefreshResult(true, true,
                "Fresh AppID metadata is ready. The generator's sign-in files were left in their original folder.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EnablerGeneratorRefreshResult(true, false, "Metadata refresh was canceled. Existing output will be used if available.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or System.ComponentModel.Win32Exception or InvalidOperationException
                                           or System.Security.SecurityException)
        {
            return new EnablerGeneratorRefreshResult(true, false,
                $"The metadata generator could not complete ({exception.GetType().Name}); existing output will be used if available.");
        }
    }

    private void BuildSteamRegularPlan(EnablerDetection detection, string? gameId, List<EnablerPlanItem> items,
        List<string> notes, List<string> blockers)
    {
        var directory = detection.GameDirectory;
        var settings = Path.Combine(directory, "steam_settings");

        // Ensure embedded regular emulator runtime is ready
        string? bundledRuntime = null;
        try
        {
            bundledRuntime = EmbeddedRegularEmulatorRuntime.EnsureReady();
        }
        catch (InvalidOperationException exception)
        {
            blockers.Add(exception.Message);
        }

        // Replace steam_api64.dll and steam_api.dll with Goldberg emulator
        if (bundledRuntime is not null)
        {
            var steamApi64Source = Path.Combine(bundledRuntime, "steam_api64.dll");
            var steamApi32Source = Path.Combine(bundledRuntime, "steam_api.dll");
            if (File.Exists(steamApi64Source))
                items.Add(CopyItem(Path.Combine(directory, "steam_api64.dll"), steamApi64Source, "Goldberg Steam emulator (64-bit)"));
            if (File.Exists(steamApi32Source))
                items.Add(CopyItem(Path.Combine(directory, "steam_api.dll"), steamApi32Source, "Goldberg Steam emulator (32-bit)"));
        }
        else
        {
            blockers.Add("The bundled Goldberg Steam emulator could not be extracted. Reinstall or update AchievementSync before creating this plan.");
        }

        if (!string.IsNullOrWhiteSpace(gameId))
        {
            items.Add(TextItem(Path.Combine(settings, "steam_appid.txt"), gameId, "Steam AppID used by the emulator"));
            var output = FindSteamOutput(detection.GameDirectory, gameId);
            var outputSettings = output is null ? null : Path.Combine(output, "steam_settings");
            if (outputSettings is not null && File.Exists(Path.Combine(outputSettings, "achievements.json")))
            {
                foreach (var name in new[] { "achievements.json", "configs.app.ini", "stats.json" })
                {
                    var source = Path.Combine(outputSettings, name);
                    if (File.Exists(source)) items.Add(CopyItem(Path.Combine(settings, name), source, $"Generated Steam metadata: {name}"));
                }
                foreach (var name in new[] { "img", "controller" })
                {
                    var source = Path.Combine(outputSettings, name);
                    if (Directory.Exists(source)) AddDirectoryCopyPlan(source, Path.Combine(settings, name), items,
                        name == "img" ? "Generated achievement artwork" : "Generated controller metadata");
                }
                notes.Add($"Steam definitions, INI, and artwork will be copied from {outputSettings}. AchievementSync's {Path.Combine(_metadataRoot, gameId, "achievements.json")} stores earned state only and is not the emulator catalog.");
            }
            else
            {
                blockers.Add($"No generated Steam catalog was found for AppID {gameId}. Generate or refresh its Steam metadata before applying; achievements.json and the optional img artwork folder are required by the adapter.");
            }
        }
        else
        {
            blockers.Add("No Steam AppID was detected. Set the numeric AppID in the wizard before generating a plan.");
        }

        items.Add(TextItem(Path.Combine(settings, "configs.user.ini"),
            "[user::general]\r\naccount_name=AchievementSync\r\nlanguage=english\r\n",
            "Steam emulator user configuration"));

        notes.Add("The regular Steam emulator replaces the game's steam_api64.dll/steam_api.dll with Goldberg's build and uses steam_settings/ for configuration and catalog files.");
    }

    private void BuildSteamColdLoaderWithRegularEmulatorPlan(EnablerDetection detection, string? gameId, List<EnablerPlanItem> items,
        List<string> notes, List<string> warnings, List<string> blockers)
    {
        if (ReadExecutableIs64Bit(detection.Executable) != true)
        {
            blockers.Add("The bundled ColdLoader and version.dll proxy are x64 only. Select a 64-bit game executable for this target.");
            return;
        }
        var directory = detection.GameDirectory;
        var settings = Path.Combine(directory, "steam_settings");

        AddColdLoaderPayloadItems(detection, gameId, items, blockers, combinedWithRegular: true);

        // Ensure embedded Regular Emulator runtime is ready (for steam_api64.dll/steam_api.dll replacement)
        string? regularRuntime = null;
        try { regularRuntime = EmbeddedRegularEmulatorRuntime.EnsureReady(); }
        catch (InvalidOperationException exception) { blockers.Add(exception.Message); }

        if (regularRuntime is not null)
        {
            var steamApi64Source = Path.Combine(regularRuntime, "steam_api64.dll");
            var steamApi32Source = Path.Combine(regularRuntime, "steam_api.dll");

            var originalSteamApi64 = Path.Combine(directory, "steam_api64.dll");
            var originalSteamApi32 = Path.Combine(directory, "steam_api.dll");

            if (File.Exists(originalSteamApi64))
            {
                if (!File.Exists(originalSteamApi64 + ".bak"))
                    items.Add(RenameItem(originalSteamApi64, originalSteamApi64 + ".bak", "Backup original steam_api64.dll to .bak"));
                if (File.Exists(steamApi64Source))
                    items.Add(CopyItem(originalSteamApi64, steamApi64Source, "Goldberg Steam emulator (64-bit) replacing steam_api64.dll (loaded directly by game)"));
            }

            if (File.Exists(originalSteamApi32))
            {
                if (!File.Exists(originalSteamApi32 + ".bak"))
                    items.Add(RenameItem(originalSteamApi32, originalSteamApi32 + ".bak", "Backup original steam_api.dll to .bak"));
                if (File.Exists(steamApi32Source))
                    items.Add(CopyItem(originalSteamApi32, steamApi32Source, "Goldberg Steam emulator (32-bit) replacing steam_api.dll (loaded directly by game)"));
            }
        }
        else
        {
            blockers.Add("The bundled Regular Emulator (steam_api64.dll/steam_api.dll) could not be extracted.");
        }

        AddSteamCatalogItems(detection.GameDirectory, settings, gameId, items, notes, blockers);

        items.Add(TextItem(Path.Combine(settings, "configs.user.ini"),
            "[user::general]\r\naccount_name=AchievementSync\r\nlanguage=english\r\n",
            "Steam emulator user configuration"));

        warnings.Add("The bundled version.dll proxy loads coldloader.dll when the game loads version.dll. If this game does not load version.dll, the ColdLoader hook will not start automatically.");
        notes.Add(
            "Combined load chain: The game's version.dll proxy loads coldloader.dll beside the executable; ColdLoader reads coldloader.ini, loads steamclient64.dll and GameOverlayRenderer64.dll, while the game's replaced steam_api64.dll reads steam_settings\\.");
    }

    private static void AddColdLoaderPayloadItems(EnablerDetection detection, string? gameId, List<EnablerPlanItem> items,
        List<string> blockers, bool combinedWithRegular)
    {
        var directory = detection.Executable is not null
            ? Path.GetDirectoryName(detection.Executable) ?? detection.GameDirectory
            : detection.GameDirectory;

        string? coldLoaderRuntime = null;
        try { coldLoaderRuntime = EmbeddedColdLoaderRuntime.EnsureReady(); }
        catch (InvalidOperationException exception) { blockers.Add(exception.Message); }

        string? coldClientRuntime = null;
        try { coldClientRuntime = EmbeddedColdClientRuntime.EnsureReady(); }
        catch (InvalidOperationException exception) { blockers.Add(exception.Message); }

        if (coldLoaderRuntime is not null)
        {
            var coldLoaderDllSource = Path.Combine(coldLoaderRuntime, "coldloader.dll");
            var coldLoaderIniSource = Path.Combine(coldLoaderRuntime, "coldloader.ini");
            if (File.Exists(coldLoaderDllSource))
                items.Add(CopyItem(Path.Combine(directory, "coldloader.dll"), coldLoaderDllSource,
                    combinedWithRegular
                        ? "ColdLoader DLL loaded by the adjacent version.dll proxy"
                        : "ColdLoader hook DLL (must be injected/loaded at game launch; sets Steam ActiveProcess & loads steamclient)"));
            if (combinedWithRegular)
            {
                var proxySource = Path.Combine(coldLoaderRuntime, "version.dll");
                if (File.Exists(proxySource))
                    items.Add(CopyItem(Path.Combine(directory, "version.dll"), proxySource,
                        "ColdLoader version.dll proxy (loads adjacent coldloader.dll)"));
                else
                    blockers.Add("The bundled ColdLoader version.dll proxy is missing. Reinstall or update AchievementSync before creating this plan.");
            }
            if (File.Exists(coldLoaderIniSource))
            {
                var ini = File.ReadAllText(coldLoaderIniSource);
                if (!string.IsNullOrWhiteSpace(gameId)) ini = SetIniValue(ini, "AppId", gameId);
                items.Add(TextItem(Path.Combine(directory, "coldloader.ini"), ini,
                    "ColdLoader config (appid + paths instructing coldloader.dll to load steamclient64.dll / steamclient.dll)"));
            }
        }
        else
        {
            blockers.Add("The bundled ColdLoader runtime could not be extracted. Reinstall or update AchievementSync before creating this plan.");
        }

        if (coldClientRuntime is not null)
        {
            var has64Api = File.Exists(Path.Combine(directory, "steam_api64.dll"));
            var has32Api = File.Exists(Path.Combine(directory, "steam_api.dll"));
            var exeIs64 = ReadExecutableIs64Bit(detection.Executable);
            bool include64 = combinedWithRegular || exeIs64 == true || has64Api || (!has32Api && exeIs64 != false);
            bool include32 = has32Api || exeIs64 == false || (!has64Api && exeIs64 is null);

            if (include64)
            {
                var overlay64 = Path.Combine(coldClientRuntime, "GameOverlayRenderer64.dll");
                var client64 = Path.Combine(coldClientRuntime, "steamclient64.dll");
                if (File.Exists(overlay64))
                    items.Add(CopyItem(Path.Combine(directory, "GameOverlayRenderer64.dll"), overlay64,
                        "Steam overlay renderer compatibility DLL (loaded by coldloader.dll)"));
                if (File.Exists(client64))
                    items.Add(CopyItem(Path.Combine(directory, "steamclient64.dll"), client64,
                        "Goldberg steamclient64 runtime (loaded by coldloader.dll via coldloader.ini)"));
            }

            if (include32)
            {
                var overlay32 = Path.Combine(coldClientRuntime, "GameOverlayRenderer.dll");
                var client32 = Path.Combine(coldClientRuntime, "steamclient.dll");
                if (File.Exists(overlay32))
                    items.Add(CopyItem(Path.Combine(directory, "GameOverlayRenderer.dll"), overlay32,
                        "Steam overlay renderer compatibility DLL 32-bit (loaded by coldloader.dll)"));
                if (File.Exists(client32))
                    items.Add(CopyItem(Path.Combine(directory, "steamclient.dll"), client32,
                        "Goldberg steamclient 32-bit runtime (loaded by coldloader.dll via coldloader.ini)"));
            }
        }
        else
        {
            blockers.Add("The bundled ColdClient support files (steamclient64.dll / GameOverlayRenderer64.dll) could not be extracted.");
        }
    }

    private void AddSteamCatalogItems(string gameDirectory, string settingsDirectory, string? gameId,
        List<EnablerPlanItem> items, List<string> notes, List<string> blockers)
    {
        if (!string.IsNullOrWhiteSpace(gameId))
        {
            items.Add(TextItem(Path.Combine(settingsDirectory, "steam_appid.txt"), gameId, "Steam AppID used by the emulator"));
            var output = FindSteamOutput(gameDirectory, gameId);
            var outputSettings = output is null ? null : Path.Combine(output, "steam_settings");
            if (outputSettings is not null && File.Exists(Path.Combine(outputSettings, "achievements.json")))
            {
                foreach (var name in new[] { "achievements.json", "configs.app.ini", "stats.json" })
                {
                    var source = Path.Combine(outputSettings, name);
                    if (File.Exists(source)) items.Add(CopyItem(Path.Combine(settingsDirectory, name), source, $"Generated Steam metadata: {name}"));
                }
                foreach (var name in new[] { "img", "controller" })
                {
                    var source = Path.Combine(outputSettings, name);
                    if (Directory.Exists(source)) AddDirectoryCopyPlan(source, Path.Combine(settingsDirectory, name), items,
                        name == "img" ? "Generated achievement artwork" : "Generated controller metadata");
                }
                notes.Add($"Steam definitions, INI, and artwork will be copied from {outputSettings}. AchievementSync's {Path.Combine(_metadataRoot, gameId, "achievements.json")} stores earned state only and is not the emulator catalog.");
            }
            else
            {
                blockers.Add($"No generated Steam catalog was found for AppID {gameId}. Generate or refresh its Steam metadata before applying; achievements.json and the optional img artwork folder are required by the adapter.");
            }
        }
        else
        {
            blockers.Add("No Steam AppID was detected. Set the numeric AppID in the wizard before generating a plan.");
        }
    }

    private void BuildSteamColdClientPlan(EnablerDetection detection, string? gameId, List<EnablerPlanItem> items,
        List<string> notes, List<string> blockers, bool includeSteamStubHelpers)
    {
        var coldClient = Path.Combine(detection.GameDirectory, "_ColdClient");
        var settings = Path.Combine(coldClient, "steam_settings");
        var extraDlls = Path.Combine(coldClient, "extra_dlls");
        var gameTemplate = Path.Combine(detection.GameDirectory, "release", "steamclient_experimental");
        string? template = IsCompleteColdClientTemplate(gameTemplate) ? gameTemplate : null;
        string? bundledTemplate = null;

        if (template is null || includeSteamStubHelpers)
        {
            try
            {
                bundledTemplate = EmbeddedColdClientRuntime.EnsureReady();
            }
            catch (InvalidOperationException exception)
            {
                if (template is null || includeSteamStubHelpers) blockers.Add(exception.Message);
            }
        }

        template ??= bundledTemplate;
        if (template is null || !IsCompleteColdClientTemplate(template))
        {
            if (blockers.Count == 0)
                blockers.Add("The bundled ColdClient launcher template is incomplete. Reinstall or update AchievementSync before creating this plan.");
        }
        else
        {
            foreach (var name in new[]
                     {
                         "steamclient_loader_x64.exe", "steamclient_loader_x86.exe",
                         "steamclient.dll", "steamclient64.dll"
                     })
            {
                items.Add(CopyItem(Path.Combine(coldClient, name), Path.Combine(template, name), "Bundled ColdClient launcher files"));
            }

            var version = typeof(AchievementEnablerService).Assembly.GetName().Version?.ToString() ?? "unknown";
            items.Add(TextItem(Path.Combine(coldClient, "version.txt"), $"AchievementSync ColdClient template {version}\r\n",
                "ColdClient template version marker"));
            items.Add(TextItem(Path.Combine(coldClient, "link_steam_userdata.ps1"), ColdClientUserDataLinkScript,
                "ColdClient helper to link Steam userdata safely"));

            foreach (var name in new[] { "GameOverlayRenderer.dll", "GameOverlayRenderer64.dll" })
            {
                var source = Path.Combine(template, name);
                if (File.Exists(source)) items.Add(CopyItem(Path.Combine(coldClient, name), source, "ColdClient overlay compatibility file"));
            }

            if (detection.Executable is null)
            {
                blockers.Add("Select the game executable as well as its folder so the ColdClient launcher can be configured.");
            }
            else
            {
                var loaderIni = Path.Combine(template, "ColdClientLoader.ini");
                var relativeExe = Path.GetRelativePath(coldClient, detection.Executable);
                var ini = File.ReadAllText(loaderIni);
                ini = SetIniValue(ini, "Exe", relativeExe);
                if (!string.IsNullOrWhiteSpace(gameId)) ini = SetIniValue(ini, "AppId", gameId);
                ini = SetIniValue(ini, "DllsToInjectFolder", includeSteamStubHelpers ? "extra_dlls" : string.Empty);
                items.Add(TextItem(Path.Combine(coldClient, "ColdClientLoader.ini"), ini,
                    $"ColdClient launcher config pointing to '{Path.GetFileName(detection.Executable)}'"));
                var is64 = ReadExecutableIs64Bit(detection.Executable) ?? !File.Exists(Path.Combine(detection.GameDirectory, "steam_api.dll"));
                var recommendedLoader = is64 ? "steamclient_loader_x64.exe" : "steamclient_loader_x86.exe";
                var launcherPath = Path.Combine(coldClient, "Launch ColdClient.bat");
                var launcherContent = "@echo off\r\n" +
                    "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0link_steam_userdata.ps1\"\r\n" +
                    "if errorlevel 1 exit /b %errorlevel%\r\n" +
                    $"\"%~dp0{recommendedLoader}\"\r\n";
                items.Add(TextItem(launcherPath, launcherContent, "ColdClient launcher with Steam userdata linking"));
                notes.Add($"Launch the game via '{launcherPath}' (links Steam userdata, then starts the {recommendedLoader} configured for '{relativeExe}').");
            }

            if (includeSteamStubHelpers)
            {
                var helperRoot = Path.Combine(EmbeddedColdClientRuntime.RootPath, "extra_dlls");
                foreach (var name in new[] { "steamstub_x32.dll", "steamstub_x64.dll" })
                {
                    var source = Path.Combine(helperRoot, name);
                    if (File.Exists(source))
                        items.Add(CopyItem(Path.Combine(extraDlls, name), source, "Optional SteamStub compatibility helper (injected at startup)"));
                    else
                        blockers.Add($"The selected SteamStub helper {name} is missing from AchievementSync's bundled payload.");
                }
                notes.Add("SteamStub compatibility helpers are selected and will be injected at startup from _ColdClient\\extra_dlls.");
            }
            else
            {
                notes.Add("Optional SteamStub compatibility helpers are bundled but disabled by default. Select the helper option only if this game requires it.");
            }
        }

        if (!string.IsNullOrWhiteSpace(gameId))
        {
            items.Add(TextItem(Path.Combine(settings, "steam_appid.txt"), gameId, "Steam AppID used by the emulator"));
            var output = FindSteamOutput(detection.GameDirectory, gameId);
            var outputSettings = output is null ? null : Path.Combine(output, "steam_settings");
            if (outputSettings is not null && File.Exists(Path.Combine(outputSettings, "achievements.json")))
            {
                foreach (var name in new[] { "achievements.json", "configs.app.ini", "stats.json" })
                {
                    var source = Path.Combine(outputSettings, name);
                    if (File.Exists(source)) items.Add(CopyItem(Path.Combine(settings, name), source, $"Generated Steam metadata: {name}"));
                }
                foreach (var name in new[] { "img", "controller" })
                {
                    var source = Path.Combine(outputSettings, name);
                    if (Directory.Exists(source)) AddDirectoryCopyPlan(source, Path.Combine(settings, name), items,
                        name == "img" ? "Generated achievement artwork" : "Generated controller metadata");
                }
                notes.Add($"Steam definitions, INI, and artwork will be copied from {outputSettings}. AchievementSync's {Path.Combine(_metadataRoot, gameId, "achievements.json")} stores earned state only and is not the emulator catalog.");
            }
            else
            {
                blockers.Add($"No generated Steam catalog was found for AppID {gameId}. Generate or refresh its Steam metadata before applying; achievements.json and the optional img artwork folder are required by the adapter.");
            }
        }
        else
        {
            blockers.Add("No Steam AppID was detected. Set the numeric AppID in the wizard before generating a plan.");
        }

        items.Add(TextItem(Path.Combine(settings, "configs.user.ini"),
            "[user::general]\r\naccount_name=AchievementSync\r\nlanguage=english\r\n",
            "Steam emulator user configuration"));

    }

    private static bool IsCompleteColdClientTemplate(string directory) =>
        Directory.Exists(directory)
        && RequiredColdClientFiles.All(name => File.Exists(Path.Combine(directory, name)) && new FileInfo(Path.Combine(directory, name)).Length > 0);

    private const string ColdClientUserDataLinkScript =
        "$ErrorActionPreference = 'Stop'\r\n" +
        "$steamPath = (Get-ItemProperty -Path 'HKLM:\\SOFTWARE\\Valve\\Steam' -ErrorAction SilentlyContinue).InstallPath\r\n" +
        "if (-not $steamPath) { $steamPath = (Get-ItemProperty -Path 'HKLM:\\SOFTWARE\\WOW6432Node\\Valve\\Steam' -ErrorAction SilentlyContinue).InstallPath }\r\n" +
        "if (-not $steamPath) { Write-Error 'Steam install path was not found in the registry.'; exit 1 }\r\n" +
        "$source = Join-Path $steamPath 'userdata'\r\n" +
        "$target = Join-Path $PSScriptRoot 'userdata'\r\n" +
        "if (-not (Test-Path -LiteralPath $source)) { Write-Error \"Steam userdata folder not found: $source\"; exit 1 }\r\n" +
        "if (Test-Path -LiteralPath $target) {\r\n" +
        "  $item = Get-Item -LiteralPath $target -Force\r\n" +
        "  if ($item.LinkType) { Remove-Item -LiteralPath $target -Force }\r\n" +
        "  else { $backup = 'userdata_backup_' + (Get-Date -Format 'yyyyMMdd_HHmmss'); Rename-Item -LiteralPath $target -NewName $backup }\r\n" +
        "}\r\n" +
        "New-Item -ItemType Junction -Path $target -Target $source | Out-Null\r\n" +
        "if ((Get-Item -LiteralPath $target -Force).LinkType -ne 'Junction') { Write-Error 'Steam userdata junction could not be verified.'; exit 1 }\r\n";

    private static readonly string[] RequiredColdClientFiles =
    [
        "ColdClientLoader.ini",
        "steamclient_loader_x64.exe",
        "steamclient_loader_x86.exe",
        "steamclient.dll",
        "steamclient64.dll"
    ];

    private void BuildUplayPlan(EnablerDetection detection, EnablerTarget target, string? gameId,
        List<EnablerPlanItem> items, List<string> notes, List<string> warnings, List<string> blockers)
    {
        var isR2 = target == EnablerTarget.UplayR2;
        var variantName = isR2 ? "Ubisoft Connect R2" : "Ubisoft Connect R1";

        string? bundledRuntime = null;
        try
        {
            bundledRuntime = isR2 ? EmbeddedUplayR2Runtime.EnsureReady() : EmbeddedUplayR1Runtime.EnsureReady();
        }
        catch (Exception ex)
        {
            blockers.Add($"Failed to extract bundled {variantName} emulator: {ex.Message}");
            return;
        }

        var loaderNames = isR2
            ? new[] { "upc_r2_loader.dll", "upc_r2_loader64.dll", "uplay_r2_loader.dll", "uplay_r2_loader64.dll" }
            : new[] { "upc_r1_loader.dll", "upc_r1_loader64.dll", "uplay_r1_loader.dll", "uplay_r1_loader64.dll" };
        var loaderPath = FindExistingLoader(detection.GameDirectory, detection.Executable, loaderNames, out var ambiguous);

        if (loaderPath is null)
        {
            if (ambiguous)
            {
                blockers.Add("More than one Ubisoft loader DLL was found. Select a game executable to disambiguate the loader location.");
                return;
            }

            var is64 = ReadExecutableIs64Bit(detection.Executable) ?? true;
            var defaultDir = detection.Executable is not null ? Path.GetDirectoryName(detection.Executable)! : detection.GameDirectory;
            var defaultLoaderName = isR2
                ? (is64 ? "uplay_r2_loader64.dll" : "uplay_r2_loader.dll")
                : (is64 ? "uplay_r1_loader64.dll" : "uplay_r1_loader.dll");
            loaderPath = Path.Combine(defaultDir, defaultLoaderName);
        }

        var destination = Path.GetDirectoryName(loaderPath)!;
        var loaderName = Path.GetFileName(loaderPath);

        var isUpc = loaderName.StartsWith("upc_", StringComparison.OrdinalIgnoreCase);
        var is64Bit = Is64BitLoader(loaderName);
        var replacementDllName = isR2
            ? (isUpc ? (is64Bit ? "upc_r2_loader64.dll" : "upc_r2_loader.dll") : (is64Bit ? "uplay_r2_loader64.dll" : "uplay_r2_loader.dll"))
            : (isUpc ? (is64Bit ? "upc_r1_loader64.dll" : "upc_r1_loader.dll") : (is64Bit ? "uplay_r1_loader64.dll" : "uplay_r1_loader.dll"));
        var iniName = isUpc ? (isR2 ? "upc_r2.ini" : "upc_r1.ini") : (isR2 ? "uplay_r2.ini" : "uplay_r1.ini");

        var loaderSource = Path.Combine(bundledRuntime, replacementDllName);
        var iniSource = Path.Combine(bundledRuntime, iniName);

        if (!File.Exists(loaderSource) || !File.Exists(iniSource))
        {
            blockers.Add($"The bundled {variantName} payload is missing {replacementDllName} or {iniName}.");
            return;
        }

        if (File.Exists(loaderPath))
        {
            items.Add(RenameItem(loaderPath, loaderPath + ".BAK", $"Backup original Ubisoft loader ({loaderName})"));
        }
        items.Add(CopyItem(loaderPath, loaderSource, $"Bundled {variantName} loader ({replacementDllName})"));

        var achievementPrefix = string.Empty;
        var schemaPath = Path.Combine(destination, "achievements_schema.json");
        var rootSchemaPath = Path.Combine(detection.GameDirectory, "achievements_schema.json");

        if (!string.IsNullOrWhiteSpace(gameId))
        {
            var achievementFile = FindSteamAchievementsFile(detection.GameDirectory, gameId);
            var schema = achievementFile is null ? null : BuildUplaySchema(achievementFile, out achievementPrefix);
            if (schema is not null)
            {
                items.Add(new EnablerPlanItem("write", rootSchemaPath, null, schema, "Uplay achievements schema generated from Steam catalog definitions"));
                if (!string.Equals(destination, detection.GameDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(new EnablerPlanItem("write", schemaPath, null, schema, "Uplay achievements schema beside loader INI"));
                }
                notes.Add($"Uplay achievements schema generated with prefix '{achievementPrefix}' for Steam AppID {gameId}.");
            }
            else
            {
                notes.Add($"No existing Steam catalog output found for AppID {gameId}. If achievements are needed, use 'Refresh this AppID with generator'.");
            }
        }
        else
        {
            warnings.Add("No cross-listed Steam AppID was provided. Enter a Steam AppID in Game ID to generate achievements_schema.json.");
        }

        var iniText = File.ReadAllText(iniSource);
        var existingIniPath = Path.Combine(destination, iniName);
        if (File.Exists(existingIniPath))
        {
            try
            {
                var existingContent = File.ReadAllText(existingIniPath);
                var dlcMatch = Regex.Match(existingContent, @"(?s)\[DLC\].*$", RegexOptions.IgnoreCase);
                if (dlcMatch.Success)
                {
                    iniText = Regex.Replace(iniText, @"(?s)\[DLC\].*$", dlcMatch.Value.TrimEnd(), RegexOptions.IgnoreCase);
                    notes.Add($"Preserved existing [DLC] / [Items] / [Chunks] sections from {iniName}.");
                }
            }
            catch { }
        }

        var savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GSE Saves", gameId ?? "unknown");
        iniText = SetIniValue(iniText, "Achievements", "1");
        iniText = SetIniValue(iniText, "SaveType", "2");
        iniText = SetIniValue(iniText, "AchSaveType", "1");
        iniText = SetIniValue(iniText, "SavePath", savePath);
        iniText = SetIniValue(iniText, "AchSavePath", savePath);
        iniText = SetIniValue(iniText, "AchKeyPrefix", achievementPrefix);
        items.Add(TextItem(Path.Combine(destination, iniName), iniText, $"Matching Ubisoft INI ({iniName}) with achievements enabled and save/schema paths set"));

        notes.Add($"{variantName} emulator configured in '{destination}'. Save data and earned achievements will be stored in '{savePath}'.");
    }

    public async Task<EnablerGeneratorRefreshResult> RefreshEpicMetadataAsync(
        string? gameIdOrNamespace, string? gameDirectory = null, CancellationToken cancellationToken = default)
    {
        var ns = gameIdOrNamespace?.Trim();
        if (string.IsNullOrWhiteSpace(ns) && !string.IsNullOrWhiteSpace(gameDirectory))
        {
            ns = AdapterOnlineMetadataService.FindLocalEpicNamespace(gameDirectory);
            if (string.IsNullOrWhiteSpace(ns))
            {
                var title = InferGameTitle(gameDirectory, null);
                if (!string.IsNullOrWhiteSpace(title))
                    (ns, _) = await AdapterOnlineMetadataService.LookupEpicNamespaceAsync(title, cancellationToken).ConfigureAwait(false);
            }
        }

        if (string.IsNullOrWhiteSpace(ns))
            return new EnablerGeneratorRefreshResult(false, false, "Enter or detect an Epic namespace / sandboxId to fetch Epic achievements.");

        if (NumericSteamId.IsMatch(ns))
        {
            return await RefreshSteamMetadataAsync(ns, cancellationToken).ConfigureAwait(false);
        }

        return await AdapterOnlineMetadataService.RefreshEpicAchievementsAsync(ns, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EnablerGeneratorRefreshResult> RefreshGogMetadataAsync(
        EnablerDetection detection, CancellationToken cancellationToken = default)
    {
        var galaxyNames = new[] { "Galaxy64.dll", "Galaxy.dll" };
        var galaxyDll = FindExistingLoader(detection.GameDirectory, detection.Executable, galaxyNames, out _);
        string? sdkVer = null;
        bool is64 = ReadExecutableIs64Bit(detection.Executable) ?? true;

        if (galaxyDll is not null && File.Exists(galaxyDll))
        {
            is64 = Path.GetFileName(galaxyDll).Equals("Galaxy64.dll", StringComparison.OrdinalIgnoreCase);
            sdkVer = ReadDllVersionString(galaxyDll);
        }

        return await AdapterOnlineMetadataService.RefreshGogUniverseLanAsync(sdkVer, is64, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EnablerGeneratorRefreshResult> RefreshEaMetadataAsync(
        EnablerDetection detection, string? steamAppIdOverride, CancellationToken cancellationToken = default)
    {
        var xmlPath = ResolveInstallerDataPath(detection.GameDirectory);
        var (xmlTitle, _, xmlContentId, _, _) = ParseInstallerDataSummary(xmlPath);
        var title = !string.IsNullOrWhiteSpace(xmlTitle) ? xmlTitle : InferGameTitle(detection.GameDirectory, detection.Executable);
        var catalog = await AdapterOnlineMetadataService.LookupEaCatalogAsync(title, xmlContentId, cancellationToken).ConfigureAwait(false);

        var resolvedSteamId = !string.IsNullOrWhiteSpace(steamAppIdOverride) && NumericSteamId.IsMatch(steamAppIdOverride.Trim())
            ? steamAppIdOverride.Trim()
            : catalog?.SteamAppId ?? detection.SteamAppId;

        if (!string.IsNullOrWhiteSpace(resolvedSteamId) && NumericSteamId.IsMatch(resolvedSteamId))
        {
            var steamRes = await RefreshSteamMetadataAsync(resolvedSteamId, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(catalog?.OfrId))
                return new EnablerGeneratorRefreshResult(true, steamRes.Succeeded, $"EA Offer {catalog.OfrId} ready. {steamRes.Message}");
            return steamRes;
        }

        return catalog is not null
            ? new EnablerGeneratorRefreshResult(true, true, $"EA catalog info cached ({catalog.OfrId ?? catalog.ContentId}).")
            : new EnablerGeneratorRefreshResult(true, false, "Could not resolve EA offer or cross-listed Steam AppID; using local installerdata.xml.");
    }

    private void BuildEpicPlan(EnablerDetection detection, string? gameId,
        List<EnablerPlanItem> items, List<string> notes, List<string> warnings, List<string> blockers)
    {
        string? epicRuntime = null;
        try
        {
            epicRuntime = EmbeddedEpicRuntime.EnsureReady();
        }
        catch (Exception ex)
        {
            blockers.Add($"Failed to extract bundled Nemirtingas Epic Emulator: {ex.Message}");
            return;
        }

        var executable = detection.Executable ?? FindCandidateExecutables(detection.GameDirectory, EnablerTarget.EpicEmulator).FirstOrDefault();
        var sdkNames = new[] { "EOSSDK-Win64-Shipping.dll", "EOSSDK-Win32-Shipping.dll" };
        var sdkDll = FindExistingLoader(detection.GameDirectory, executable, sdkNames, out var ambiguous);

        if (sdkDll is null)
        {
            if (ambiguous)
            {
                blockers.Add("More than one EOSSDK loader DLL was found. Select a specific game executable to disambiguate.");
                return;
            }

            var is64 = ReadExecutableIs64Bit(executable) ?? true;
            var defaultDir = executable is not null ? Path.GetDirectoryName(executable)! : detection.GameDirectory;
            var defaultDllName = is64 ? "EOSSDK-Win64-Shipping.dll" : "EOSSDK-Win32-Shipping.dll";
            sdkDll = Path.Combine(defaultDir, defaultDllName);
        }

        var destination = Path.GetDirectoryName(sdkDll)!;
        var isWin32 = Path.GetFileName(sdkDll).Contains("Win32", StringComparison.OrdinalIgnoreCase);
        var sourceDllName = isWin32 ? "EOSSDK-Win32-Shipping.dll" : "EOSSDK-Win64-Shipping.dll";
        var sourceDllPath = Path.Combine(epicRuntime, sourceDllName);

        if (!File.Exists(sourceDllPath))
        {
            blockers.Add($"Bundled Nemirtingas EOSSDK DLL '{sourceDllName}' not found in runtime.");
            return;
        }

        if (File.Exists(sdkDll) && !File.Exists(sdkDll + ".BAK"))
        {
            items.Add(RenameItem(sdkDll, sdkDll + ".BAK", $"Backup original {Path.GetFileName(sdkDll)} to .BAK"));
        }
        items.Add(CopyItem(sdkDll, sourceDllPath, $"Nemirtingas Epic Emulator (_no_network) EOSSDK replacement ({sourceDllName})"));

        var settingsDir = Path.Combine(destination, "nepice_settings");
        var templateJsonPath = Path.Combine(epicRuntime, "NemirtingasEpicEmu.json");
        var namespaceOrAppId = !string.IsNullOrWhiteSpace(gameId)
            ? gameId
            : AdapterOnlineMetadataService.FindLocalEpicNamespace(detection.GameDirectory) ?? "00000000000000000000000000000000";

        var epicUserId = "17380c2a51230d05a341a2319657af57";
        if (File.Exists(templateJsonPath))
        {
            try
            {
                var jsonNode = JsonNode.Parse(File.ReadAllBytes(templateJsonPath));
                if (jsonNode?["EOSEmu"]?["Application"] is JsonObject appObj)
                    appObj["AppId"] = namespaceOrAppId;
                if (jsonNode?["EOSEmu"]?["User"]?["EpicId"]?.ToString() is { Length: > 0 } eid)
                    epicUserId = eid;
                var configBytes = JsonSerializer.SerializeToUtf8Bytes(jsonNode, JsonOptions);
                items.Add(new EnablerPlanItem("write", Path.Combine(settingsDir, "NemirtingasEpicEmu.json"), null, configBytes,
                    "Nemirtingas Epic Emulator configuration (nepice_settings/NemirtingasEpicEmu.json)"));
            }
            catch
            {
                items.Add(CopyItem(Path.Combine(settingsDir, "NemirtingasEpicEmu.json"), templateJsonPath,
                    "Nemirtingas Epic Emulator configuration template"));
            }
        }

        // 1. Prefer native Epic achievements_db.json + achievements_images fetched from api.epicgames.dev
        bool copiedEpicAchievements = false;
        if (!string.IsNullOrWhiteSpace(namespaceOrAppId) && namespaceOrAppId != "00000000000000000000000000000000")
        {
            var epicOutDir = AdapterOnlineMetadataService.GetEpicNamespaceOutputDir(namespaceOrAppId);
            var epicDbFile = Path.Combine(epicOutDir, "achievements_db.json");
            var epicImgDir = Path.Combine(epicOutDir, "achievements_images");
            if (File.Exists(epicDbFile))
            {
                items.Add(CopyItem(Path.Combine(settingsDir, "achievements_db.json"), epicDbFile,
                    "Epic achievements database fetched from api.epicgames.dev"));
                if (Directory.Exists(epicImgDir))
                {
                    AddDirectoryCopyPlan(epicImgDir, Path.Combine(settingsDir, "achievements_images"), items,
                        "Epic achievement icons fetched from api.epicgames.dev");
                }
                notes.Add($"Epic localized achievements_db.json and icons copied from '{epicOutDir}'.");
                copiedEpicAchievements = true;
            }
        }

        // 2. Fallback: cross-listed Steam achievements schema if available
        if (!copiedEpicAchievements && !string.IsNullOrWhiteSpace(gameId))
        {
            var steamAchFile = FindSteamAchievementsFile(detection.GameDirectory, gameId);
            if (steamAchFile is not null)
            {
                var epicDbBytes = BuildEpicAchievementsDb(steamAchFile);
                if (epicDbBytes is not null)
                {
                    items.Add(new EnablerPlanItem("write", Path.Combine(settingsDir, "achievements_db.json"), null, epicDbBytes,
                        "Epic achievements database generated from Steam definitions"));
                    notes.Add("Epic achievements database (achievements_db.json) generated from cross-listed Steam catalog.");
                }

                var steamOutput = FindSteamOutput(detection.GameDirectory, gameId);
                var imgDir = steamOutput is null ? null : Path.Combine(steamOutput, "steam_settings", "img");
                if (imgDir is not null && Directory.Exists(imgDir))
                {
                    AddDirectoryCopyPlan(imgDir, Path.Combine(settingsDir, "achievements_images"), items, "Epic achievement artwork");
                }
            }
            else
            {
                warnings.Add($"No Epic achievements_db.json was found for '{namespaceOrAppId}'. Keep Refresh enabled and enter the Epic namespace (or cross-listed Steam AppID) to fetch achievements_db.json and achievement icons.");
            }
        }

        if (executable is not null && File.Exists(executable))
        {
            var exeDir = Path.GetDirectoryName(executable)!;
            var exeLeaf = Path.GetFileName(executable);
            var folderTitle = Path.GetFileName(detection.GameDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var rawGameName = !string.IsNullOrWhiteSpace(folderTitle) && !IsGenericTitle(folderTitle)
                ? folderTitle
                : Path.GetFileNameWithoutExtension(executable);
            var invalid = Path.GetInvalidFileNameChars();
            var safeGameName = new string(rawGameName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            var launchBatPath = Path.Combine(exeDir, $"Launch {safeGameName}.bat");
            var launchBatContent = $"@echo off\r\ncd /d \"%~dp0\"\r\nstart /b \"{rawGameName}\" \"{exeLeaf}\" -AUTH_LOGIN=unused -AUTH_PASSWORD=cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd -AUTH_TYPE=exchangecode -epicapp={namespaceOrAppId} -epicenv=Prod -EpicPortal -epicusername=\"Player\" -epicuserid=4340353b292a150000e7d9b5a47b6343 -epicsandboxid={namespaceOrAppId} -epiclocale=en\r\n";
            items.Add(TextItem(launchBatPath, launchBatContent, $"Epic exchange-code launcher script (Launch {safeGameName}.bat)"));
        }
        else
        {
            warnings.Add("No game executable selected: Launch *.bat (required for -AUTH_TYPE=exchangecode -epicapp / -epicsandboxid) was not created. Select Game executable and regenerate the plan.");
        }

        var nemirSaveDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NemirtingasEpicEmu", epicUserId, namespaceOrAppId);
        notes.Add($"Nemirtingas Epic Emulator (_no_network) configured with namespace '{namespaceOrAppId}' in '{settingsDir}'. Emulator saves unlocks under '{nemirSaveDir}'.");
    }

    private void BuildGogPlan(EnablerDetection detection, string? gameId,
        List<EnablerPlanItem> items, List<string> notes, List<string> warnings, List<string> blockers)
    {
        string? bundledRuntime = null;
        try
        {
            bundledRuntime = EmbeddedUniverseLanRuntime.EnsureReady();
        }
        catch (Exception ex)
        {
            blockers.Add($"Failed to extract bundled UniverseLAN emulator: {ex.Message}");
            return;
        }

        var executable = detection.Executable ?? FindCandidateExecutables(detection.GameDirectory, EnablerTarget.GogUniverseLan).FirstOrDefault();
        var galaxyNames = new[] { "Galaxy64.dll", "Galaxy.dll" };
        var galaxyDll = FindExistingLoader(detection.GameDirectory, executable, galaxyNames, out var ambiguous);

        if (galaxyDll is null)
        {
            if (ambiguous)
            {
                blockers.Add("More than one Galaxy.dll was found. Select a game executable to disambiguate.");
                return;
            }

            var is64 = ReadExecutableIs64Bit(executable) ?? true;
            var defaultDir = executable is not null ? Path.GetDirectoryName(executable)! : detection.GameDirectory;
            var defaultDllName = is64 ? "Galaxy64.dll" : "Galaxy.dll";
            galaxyDll = Path.Combine(defaultDir, defaultDllName);
        }

        var destination = Path.GetDirectoryName(galaxyDll)!;
        var is64Bit = Path.GetFileName(galaxyDll).Equals("Galaxy64.dll", StringComparison.OrdinalIgnoreCase);
        var replacementDllName = is64Bit ? "Galaxy64.dll" : "Galaxy.dll";
        var serverExeName = is64Bit ? "UniverseLANServer64.exe" : "UniverseLANServer.exe";

        // Galaxy SDK version compatibility check (deploy_universelan.ps1)
        string? gameSdkVersion = File.Exists(galaxyDll) ? ReadDllVersionString(galaxyDll) : null;
        string gogSourceFolder = bundledRuntime;
        if (!string.IsNullOrWhiteSpace(gameSdkVersion))
        {
            var matchedFolder = AdapterOnlineMetadataService.FindMatchingUniverseLanFolder(gameSdkVersion, is64Bit, out var matchMethod);
            if (matchedFolder is not null)
            {
                gogSourceFolder = matchedFolder;
                notes.Add($"Matched GOG Galaxy SDK {gameSdkVersion} via {matchMethod}.");
            }
            else
            {
                var bundledSdkVersion = ReadDllVersionString(Path.Combine(bundledRuntime, replacementDllName)) ?? "1.99.0.0";
                blockers.Add(
                    $"No compatible UniverseLAN build matched the game's {replacementDllName} SDK version '{gameSdkVersion}' (bundled version is '{bundledSdkVersion}'). " +
                    "Leaving original DLL untouched to avoid crashing the game. Enable 'Refresh' above to download a matching UniverseLAN release from GitHub, or place it under %LOCALAPPDATA%\\AchievementSync\\tools\\universallan\\releases\\.");
                return;
            }
        }
        else if (File.Exists(galaxyDll))
        {
            notes.Add($"Could not read FileVersion from existing {replacementDllName}; deploying bundled UniverseLAN ({ReadDllVersionString(Path.Combine(bundledRuntime, replacementDllName)) ?? "1.99.0.0"}).");
        }

        var sourceDllPath = Path.Combine(gogSourceFolder, replacementDllName);
        var sourceServerPath = Path.Combine(gogSourceFolder, serverExeName);

        if (!File.Exists(sourceDllPath) || !File.Exists(sourceServerPath))
        {
            blockers.Add($"UniverseLAN files '{replacementDllName}' or '{serverExeName}' not found in '{gogSourceFolder}'.");
            return;
        }

        if (File.Exists(galaxyDll) && !File.Exists(galaxyDll + ".BAK"))
        {
            items.Add(RenameItem(galaxyDll, galaxyDll + ".BAK", $"Backup original Galaxy library ({replacementDllName}) to .BAK"));
        }
        items.Add(CopyItem(galaxyDll, sourceDllPath, $"UniverseLAN Galaxy emulator replacement ({replacementDllName})"));
        items.Add(CopyItem(Path.Combine(destination, serverExeName), sourceServerPath, $"UniverseLAN server executable ({serverExeName})"));

        // Architecture cleanup (deploy_universelan.ps1): remove unneeded opposite-bitness binaries if present
        var unneededFiles = is64Bit
            ? new[] { "Galaxy.dll", "UniverseLANServer.exe" }
            : new[] { "Galaxy64.dll", "UniverseLANServer64.exe" };
        foreach (var unneeded in unneededFiles)
        {
            var unneededPath = Path.Combine(destination, unneeded);
            if (File.Exists(unneededPath))
                items.Add(new EnablerPlanItem("delete", unneededPath, null, null, $"Remove unneeded opposite-architecture file ({unneeded})"));
        }

        var localAppDataUniverseLan = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniverseLAN");
        var iniDest = Path.Combine(localAppDataUniverseLan, "UniverseLAN.ini");
        var iniSource = File.Exists(Path.Combine(gogSourceFolder, "UniverseLAN.ini"))
            ? Path.Combine(gogSourceFolder, "UniverseLAN.ini")
            : Path.Combine(bundledRuntime, "UniverseLAN.ini");
        if (File.Exists(iniSource))
        {
            if (!File.Exists(iniDest))
                items.Add(CopyItem(iniDest, iniSource, "UniverseLAN main configuration (LocalAppData/UniverseLAN/UniverseLAN.ini)"));
            else
                notes.Add("Existing LocalAppData\\UniverseLAN\\UniverseLAN.ini preserved (not overwritten).");
        }

        var gogId = FindGogAppId(detection.GameDirectory) ?? gameId;
        if (!string.IsNullOrWhiteSpace(gogId))
        {
            var appDataTarget = Path.Combine(localAppDataUniverseLan, gogId);

            var dataSrc = Directory.Exists(Path.Combine(gogSourceFolder, "UniverseLANData"))
                ? Path.Combine(gogSourceFolder, "UniverseLANData")
                : Path.Combine(bundledRuntime, "UniverseLANData");
            if (Directory.Exists(dataSrc))
                AddMissingDirectoryCopyPlan(dataSrc, Path.Combine(appDataTarget, "UniverseLANData"), items,
                    "UniverseLAN client data template (missing files only)", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Achievements.ini" });

            var catalogCandidates = new[] { gameId, detection.SteamAppId }
                .Where(candidate => NumericSteamId.IsMatch(candidate ?? string.Empty))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var catalogMatch = catalogCandidates
                .Select(candidate => (AppId: candidate!, Path: FindSteamAchievementsFile(detection.GameDirectory, candidate)))
                .FirstOrDefault(candidate => candidate.Path is not null);
            var catalogAppId = catalogMatch.AppId;
            var steamAchievements = catalogMatch.Path;
            if (steamAchievements is not null)
            {
                var achievementsPath = Path.Combine(appDataTarget, "UniverseLANData", "Achievements.ini");
                var existingAchievements = File.Exists(achievementsPath) ? File.ReadAllText(achievementsPath) : null;
                var generatedAchievements = UniverseLanAchievementDefinitions.Build(steamAchievements, existingAchievements);
                if (generatedAchievements is not null)
                {
                    items.Add(TextItem(achievementsPath, generatedAchievements,
                        $"UniverseLAN achievement definitions generated from AppID {catalogAppId}"));
                    notes.Add($"Generated UniverseLAN achievement definitions from the catalog for AppID {catalogAppId}.");
                }
                else
                {
                    warnings.Add($"The catalog for AppID {catalogAppId} contained no usable achievement definitions; no example Achievements.ini was installed.");
                }
            }
            else
            {
                warnings.Add($"No achievement catalog was found for AppID {catalogAppId}. Refresh or generate that AppID's catalog before planning GOG achievement definitions.");
            }

            var serverDataSrc = Directory.Exists(Path.Combine(gogSourceFolder, "UniverseLANServerData"))
                ? Path.Combine(gogSourceFolder, "UniverseLANServerData")
                : Path.Combine(bundledRuntime, "UniverseLANServerData");
            if (Directory.Exists(serverDataSrc))
                AddMissingDirectoryCopyPlan(serverDataSrc, Path.Combine(appDataTarget, "UniverseLANServerData"), items, "UniverseLAN server data template (missing files only)");

            notes.Add($"UniverseLAN data for GOG App ID {gogId} deployed to '{appDataTarget}'. Existing progress/INI files are preserved.");
        }
        else
        {
            warnings.Add("Could not determine GOG App ID (no goggame-<id>.info found). Enter the numeric GOG ID in Game ID to deploy game-specific UniverseLAN data.");
        }

        // Generate paired UniverseLANServer + game launcher (mirrors gog_universelan\make_shortcut.ps1)
        if (executable is not null && File.Exists(executable))
        {
            var exeDir = Path.GetDirectoryName(executable)!;
            var serverExeDest = Path.Combine(destination, serverExeName);
            var psLauncherPath = Path.Combine(exeDir, "_ae_gog_launch.ps1");
            var psLauncherContent =
                "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
                $"$serverProc = Start-Process -FilePath '{serverExeDest.Replace("'", "''")}' -WorkingDirectory '{destination.Replace("'", "''")}' -PassThru\r\n" +
                "Start-Sleep -Milliseconds 100\r\n" +
                $"$gameProc = Start-Process -FilePath '{executable.Replace("'", "''")}' -WorkingDirectory '{exeDir.Replace("'", "''")}' -PassThru\r\n" +
                "while ((-not $serverProc.HasExited) -and (-not $gameProc.HasExited)) {\r\n" +
                "    Start-Sleep -Milliseconds 500\r\n" +
                "}\r\n" +
                "if (-not $gameProc.HasExited) { Stop-Process -Id $gameProc.Id -Force }\r\n" +
                "if (-not $serverProc.HasExited) { Stop-Process -Id $serverProc.Id -Force }\r\n";
            items.Add(TextItem(psLauncherPath, psLauncherContent, "UniverseLAN linked server + game launcher (_ae_gog_launch.ps1)"));

            var batPath = Path.Combine(exeDir, "Launch with UniverseLAN.bat");
            var batContent = "@echo off\r\npowershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"%~dp0_ae_gog_launch.ps1\"\r\n";
            items.Add(TextItem(batPath, batContent, "GOG UniverseLAN paired launcher (starts UniverseLANServer + game together)"));
        }
        else
        {
            warnings.Add($"No game executable selected: paired launcher (_ae_gog_launch.ps1) was not created. Start '{serverExeName}' alongside the game when playing.");
        }

        notes.Add($"UniverseLAN emulator files placed in '{destination}'.");
    }

    private void BuildEaPlan(EnablerDetection detection, string? gameId,
        List<EnablerPlanItem> items, List<string> notes, List<string> warnings, List<string> blockers)
    {
        string? eaRuntime = null;
        try
        {
            eaRuntime = EmbeddedEaOriginRuntime.EnsureReady();
        }
        catch (Exception ex)
        {
            blockers.Add($"Failed to extract bundled EA / Origin emulator: {ex.Message}");
            return;
        }

        var executable = detection.Executable ?? FindCandidateExecutables(detection.GameDirectory, EnablerTarget.EaOrigin).FirstOrDefault();
        if (executable is null)
        {
            blockers.Add("Select the EA/Origin game executable so anadius DLL, executable import patch, and anadius.cfg can be placed beside it.");
            return;
        }

        var destination = Path.GetDirectoryName(executable)!;
        var installerDataPath = ResolveInstallerDataPath(detection.GameDirectory) ?? string.Empty;

        var inspection = OriginExePatcher.InspectExecutable(executable);
        var is64 = inspection.IsValidPe ? inspection.Is64Bit : (ReadExecutableIs64Bit(executable) ?? true);
        if (File.Exists(installerDataPath))
        {
            var xmlBitness = ReadInstallerDataBitness(installerDataPath, Path.GetFileName(executable));
            if (xmlBitness.HasValue) is64 = xmlBitness.Value;
        }

        var dllName = is64 ? "anadius64.dll" : "anadius32.dll";
        var sourceDllPath = Path.Combine(eaRuntime, dllName);

        if (!File.Exists(sourceDllPath))
        {
            blockers.Add($"Bundled anadius emulator DLL '{dllName}' not found in runtime.");
            return;
        }

        items.Add(CopyItem(Path.Combine(destination, dllName), sourceDllPath, $"anadius EA/Origin emulator library ({dllName})"));

        // Patch the game executable (unwrap .ooa Origin DRM if present + add .anadius import table entry)
        if (inspection.IsValidPe)
        {
            if (inspection.ImportsAnadius)
            {
                notes.Add(inspection.StatusMessage);
            }
            else if (inspection.CanPatch)
            {
                var sourceExe = inspection.HasBak ? executable + ".BAK" : executable;
                items.Add(new EnablerPlanItem(
                    "patch-ea-exe",
                    executable,
                    sourceExe,
                    null,
                    inspection.HasOoaSection
                        ? $"Unwrap Origin DRM (.ooa) & add {dllName} import to {Path.GetFileName(executable)} (original backed up to .BAK)"
                        : $"Add {dllName} import table entry to {Path.GetFileName(executable)} (original backed up to .BAK)"));
            }
            else
            {
                warnings.Add(inspection.StatusMessage);
            }
        }
        else
        {
            notes.Add(inspection.StatusMessage);
        }

        // Preserve original anadius.cfg on first modification (mirrors origin_helper.py)
        var existingCfgPath = Path.Combine(destination, "anadius.cfg");
        if (File.Exists(existingCfgPath) && !File.Exists(existingCfgPath + ".BAK"))
        {
            items.Add(RenameItem(existingCfgPath, existingCfgPath + ".BAK", "Backup original anadius.cfg to .BAK before updating Achievements"));
        }

        var resolvedSteamId = gameId ?? detection.SteamAppId;
        var cfgText = GenerateAnadiusCfg(detection.GameDirectory, destination, installerDataPath, resolvedSteamId, executable, out var effectiveSteamId);
        items.Add(TextItem(existingCfgPath, cfgText, "anadius EA/Origin emulator configuration (anadius.cfg)"));

        var effectiveIdForSave = !string.IsNullOrWhiteSpace(effectiveSteamId) ? effectiveSteamId : "0";
        var saveFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "anadius", "LSX emu", "achievement_watcher", effectiveIdForSave);
        notes.Add($"anadius EA/Origin emulator deployed to '{destination}' ({dllName} + anadius.cfg). Save folder: '{saveFolder}'.");
    }

    private static void AddMissingDirectoryCopyPlan(string sourceDirectory, string destinationDirectory,
        List<EnablerPlanItem> items, string description, IReadOnlySet<string>? excludedFileNames = null)
    {
        foreach (var source in SafeEnumerateFiles(sourceDirectory))
        {
            if (excludedFileNames?.Contains(Path.GetFileName(source)) == true) continue;
            var relative = Path.GetRelativePath(sourceDirectory, source);
            var dest = Path.Combine(destinationDirectory, relative);
            if (!File.Exists(dest))
                items.Add(CopyItem(dest, source, description));
        }
    }

    private static string? ReadDllVersionString(string dllPath)
    {
        try
        {
            if (!File.Exists(dllPath)) return null;
            var vi = FileVersionInfo.GetVersionInfo(dllPath);
            if (!string.IsNullOrWhiteSpace(vi.FileVersion)) return vi.FileVersion.Trim();
            if (!string.IsNullOrWhiteSpace(vi.ProductVersion)) return vi.ProductVersion.Trim();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveInstallerDataPath(string directory)
    {
        var candidates = new[]
        {
            Path.Combine(directory, "__Installer", "installerdata.xml"),
            Path.Combine(directory, "installerdata.xml"),
            Directory.GetParent(directory)?.FullName is { } p ? Path.Combine(p, "__Installer", "installerdata.xml") : null
        };
        return candidates.FirstOrDefault(p => p is not null && File.Exists(p));
    }

    private static (string? Title, string? Version, string? ContentId, string? Languages, string? RegKey) ParseInstallerDataSummary(string? installerDataPath)
    {
        if (string.IsNullOrWhiteSpace(installerDataPath) || !File.Exists(installerDataPath))
            return (null, null, null, null, null);

        try
        {
            var doc = System.Xml.Linq.XDocument.Load(installerDataPath);
            var titleEl = doc.Descendants("gameTitle").FirstOrDefault(e => (string?)e.Attribute("locale") == "en_US")
                          ?? doc.Descendants("gameTitle").FirstOrDefault()
                          ?? doc.Descendants("title").FirstOrDefault();
            var gameName = !string.IsNullOrWhiteSpace(titleEl?.Value) ? titleEl.Value.Trim() : null;

            var verEl = doc.Descendants("gameVersion").FirstOrDefault();
            var version = verEl?.Attribute("version")?.Value?.Trim();

            var cidEl = doc.Descendants("contentID").FirstOrDefault();
            var contentId = !string.IsNullOrWhiteSpace(cidEl?.Value) ? cidEl.Value.Trim() : null;

            var locEl = doc.Descendants("locales").FirstOrDefault();
            string? languages = null;
            if (locEl is not null && !string.IsNullOrWhiteSpace(locEl.Value))
            {
                var sorted = locEl.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .OrderBy(s => s, StringComparer.Ordinal);
                languages = string.Join(",", sorted);
            }

            var rawXml = File.ReadAllText(installerDataPath);
            var regMatch = Regex.Match(rawXml, @"\[HKEY[^\\]+\\(.+?\\)Install Dir\]", RegexOptions.IgnoreCase);
            var regKey = regMatch.Success ? regMatch.Groups[1].Value + "Locale" : null;

            return (gameName, version, contentId, languages, regKey);
        }
        catch
        {
            return (null, null, null, null, null);
        }
    }

    private static string ComputeFileSha1Hex(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(stream));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static byte[]? BuildEpicAchievementsDb(string steamAchievementsFile)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(steamAchievementsFile));
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var array = new JsonArray();
            foreach (var achievement in document.RootElement.EnumerateArray())
            {
                if (!achievement.TryGetProperty("name", out var nameVal) || nameVal.ValueKind != JsonValueKind.String) continue;
                var name = nameVal.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var displayName = ReadLocalizedText(achievement, "displayName");
                var description = ReadLocalizedText(achievement, "description");
                var hidden = achievement.TryGetProperty("hidden", out var hiddenVal) && hiddenVal.GetInt32() != 0;

                var obj = new JsonObject
                {
                    ["AchievementId"] = name,
                    ["UnlockedDisplayName"] = new JsonObject { ["default"] = displayName, ["en"] = displayName },
                    ["UnlockedDescription"] = new JsonObject { ["default"] = description, ["en"] = description },
                    ["LockedDisplayName"] = new JsonObject { ["default"] = displayName, ["en"] = displayName },
                    ["LockedDescription"] = new JsonObject { ["default"] = description, ["en"] = description },
                    ["FlavorText"] = new JsonObject { ["default"] = string.Empty },
                    ["UnlockedIconUrl"] = name,
                    ["LockedIconUrl"] = name + "_locked",
                    ["IsHidden"] = hidden,
                    ["StatsThresholds"] = new JsonArray()
                };
                array.Add(obj);
            }
            return array.Count == 0 ? null : JsonSerializer.SerializeToUtf8Bytes(array, JsonOptions);
        }
        catch { return null; }
    }

    private string GenerateAnadiusCfg(string gameFolder, string destination, string installerDataPath,
        string? steamAppId, string? executablePath, out string? effectiveSteamId)
    {
        var existingCfgPath = Path.Combine(destination, "anadius.cfg");
        string? existingText = null;
        if (File.Exists(existingCfgPath))
        {
            try { existingText = File.ReadAllText(existingCfgPath); } catch { }
        }

        var (xmlTitle, xmlVersion, xmlContentId, xmlLanguages, xmlRegKey) = ParseInstallerDataSummary(installerDataPath);
        var gameName = xmlTitle ?? "EA Game";
        var version = xmlVersion ?? "1.0.0.0";
        var contentId = xmlContentId ?? "0";
        var languages = xmlLanguages ?? "en_US";
        var regKey = xmlRegKey ?? string.Empty;

        var catalog = AdapterOnlineMetadataService.ReadCachedEaCatalog(contentId);
        var ofrId = !string.IsNullOrWhiteSpace(catalog?.OfrId) ? catalog.OfrId : contentId;
        var contentName = !string.IsNullOrWhiteSpace(catalog?.DisplayName) ? catalog.DisplayName : gameName;
        var achievementsSet = !string.IsNullOrWhiteSpace(catalog?.AchievementsSetId)
            ? catalog.AchievementsSetId
            : (!string.IsNullOrWhiteSpace(contentId) && contentId != "0" ? contentId : "PASTE_ACHIEVEMENT_SET_ID_HERE");

        effectiveSteamId = !string.IsNullOrWhiteSpace(steamAppId) ? steamAppId : catalog?.SteamAppId;

        string? dbdataPath = null;
        try
        {
            dbdataPath = Directory.EnumerateFiles(gameFolder, "dbdata.dll", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { }
        var hasDenuvo = dbdataPath is not null;
        var dllHash = dbdataPath is not null ? ComputeFileSha1Hex(dbdataPath) : string.Empty;
        if (string.IsNullOrEmpty(dllHash)) dllHash = "DENUVO_DLL_HASH";

        var inspection = !string.IsNullOrWhiteSpace(executablePath) ? OriginExePatcher.InspectExecutable(executablePath) : null;
        var exeHash = "DENUVO_EXE_HASH";
        if (inspection is not null && !string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
        {
            // If we aren't patching the exe during Apply, hash it now; otherwise Apply hashes the newly patched exe.
            if (!inspection.CanPatch || inspection.ImportsAnadius || !inspection.IsValidPe)
            {
                var computed = ComputeFileSha1Hex(executablePath);
                if (!string.IsNullOrEmpty(computed)) exeHash = computed;
            }
        }

        var achievementLines = new StringBuilder();
        var prefix = string.Empty;
        if (!string.IsNullOrWhiteSpace(effectiveSteamId))
        {
            var achFile = FindSteamAchievementsFile(gameFolder, effectiveSteamId);
            if (achFile is not null && File.Exists(achFile))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(achFile));
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        var names = new List<(string Id, string DisplayName)>();
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            if (el.TryGetProperty("name", out var n) && n.GetString() is { } id)
                            {
                                var disp = ReadLocalizedText(el, "displayName");
                                names.Add((id, string.IsNullOrWhiteSpace(disp) ? id : disp));
                            }
                        }

                        if (names.Count > 0)
                        {
                            prefix = ComputeCommonPrefix(names.Select(n => n.Id).ToList());
                            achievementLines.AppendLine("        \"AchievementNames\"");
                            achievementLines.AppendLine("        {");
                            foreach (var (id, disp) in names)
                            {
                                var key = !string.IsNullOrEmpty(prefix) && id.StartsWith(prefix, StringComparison.Ordinal)
                                    ? id.Substring(prefix.Length)
                                    : id;
                                achievementLines.AppendLine($"            \"{key}\"  \"{disp}\"");
                            }
                            achievementLines.AppendLine("        }");
                        }
                    }
                }
                catch { }
            }
        }

        if (!string.IsNullOrWhiteSpace(existingText))
        {
            var newAchBlock = new StringBuilder();
            newAchBlock.AppendLine("    \"Achievements\"");
            newAchBlock.AppendLine("    {");
            newAchBlock.AppendLine($"        \"AchievementsSet\"  \"{achievementsSet}\"");
            if (achievementLines.Length > 0)
            {
                newAchBlock.Append(achievementLines);
                if (!string.IsNullOrWhiteSpace(effectiveSteamId))
                    newAchBlock.AppendLine($"        \"SteamAppId\"  \"{effectiveSteamId}\"");
                if (!string.IsNullOrWhiteSpace(prefix))
                    newAchBlock.AppendLine($"        \"SteamIdPrefix\"  \"{prefix}\"");
            }
            newAchBlock.Append("    }");

            var updated = ReplaceAnadiusAchievementsBlock(existingText, newAchBlock.ToString());
            if (updated is not null)
            {
                if (hasDenuvo)
                {
                    updated = updated.Replace("\"DENUVO_DLL_HASH\"", $"\"{dllHash}\"", StringComparison.Ordinal);
                    if (exeHash != "DENUVO_EXE_HASH")
                        updated = updated.Replace("\"DENUVO_EXE_HASH\"", $"\"{exeHash}\"", StringComparison.Ordinal);
                }
                return updated;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("\"Config2\"");
        sb.AppendLine("{");
        sb.AppendLine("    \"Game\"");
        sb.AppendLine("    {");
        sb.AppendLine($"        \"Name\"  \"{gameName}\"");
        sb.AppendLine($"        \"Version\"  \"{version}\"");
        sb.AppendLine($"        \"ContentId\"  \"{contentId}\"");
        if (hasDenuvo)
        {
            sb.AppendLine("        \"DenuvoToken\"  \"PASTE_A_VALID_DENUVO_TOKEN_HERE\"");
            sb.AppendLine($"        \"DenuvoExeHash\"  \"{exeHash}\"");
            sb.AppendLine($"        \"DenuvoDllHash\"  \"{dllHash}\"");
        }
        sb.AppendLine($"        \"Languages\"  \"{languages}\"");
        sb.AppendLine("        \"Language\"  \"en_US\"");
        if (!string.IsNullOrWhiteSpace(regKey))
            sb.AppendLine($"        \"LanguageRegistryKey\"  \"{regKey}\"");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    \"Emulator\"");
        sb.AppendLine("    {");
        sb.AppendLine("        \"ServerPort\"  \"default\"");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    \"User\"");
        sb.AppendLine("    {");
        sb.AppendLine("        \"Username\"  \"anadius\"");
        sb.AppendLine("        \"PersonaId\"  \"1144668899\"");
        sb.AppendLine("        \"UserId\"  \"1000200030000\"");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    \"Content\"");
        sb.AppendLine("    {");
        sb.AppendLine($"        \"{ofrId}\"");
        sb.AppendLine("        {");
        sb.AppendLine($"            \"Name\"  \"{contentName}\"");
        sb.AppendLine($"            \"Version\"  \"{version}\"");
        sb.AppendLine("            \"State\"  \"INSTALLED\"");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    \"Entitlements\"");
        sb.AppendLine("    {");
        sb.AppendLine("        \"PUT_ORIGIN_DLC_ID_HERE\"");
        sb.AppendLine("        {");
        sb.AppendLine("            \"Group\"  \"GAME_GROUP_NAME\"");
        sb.AppendLine("            \"Version\"  \"0\"");
        sb.AppendLine("            \"Type\"  \"DEFAULT\"");
        sb.AppendLine("            \"EntitlementTag\"  \"THIS_IS_WHAT_MAKES_THE_DLCS_WORK\"");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    \"Achievements\"");
        sb.AppendLine("    {");
        sb.AppendLine($"        \"AchievementsSet\"  \"{achievementsSet}\"");
        if (achievementLines.Length > 0)
        {
            sb.Append(achievementLines);
            if (!string.IsNullOrWhiteSpace(effectiveSteamId))
                sb.AppendLine($"        \"SteamAppId\"  \"{effectiveSteamId}\"");
            if (!string.IsNullOrWhiteSpace(prefix))
                sb.AppendLine($"        \"SteamIdPrefix\"  \"{prefix}\"");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string? ReplaceAnadiusAchievementsBlock(string existingText, string newAchBlock)
    {
        var marker = Regex.Match(existingText, @"(?m)^[ \t]*""Achievements""[ \t]*\r?$");
        if (marker.Success)
        {
            int braceOpen = existingText.IndexOf('{', marker.Index);
            if (braceOpen >= 0)
            {
                int depth = 0;
                int endIdx = -1;
                for (int i = braceOpen; i < existingText.Length; i++)
                {
                    if (existingText[i] == '{') depth++;
                    else if (existingText[i] == '}')
                    {
                        depth--;
                        if (depth == 0) { endIdx = i; break; }
                    }
                }
                if (endIdx > braceOpen)
                    return existingText[..marker.Index] + newAchBlock + existingText[(endIdx + 1)..];
            }
        }

        var trimmed = existingText.TrimEnd();
        if (trimmed.EndsWith('}'))
        {
            var before = trimmed[..^1].TrimEnd();
            return before + "\r\n\r\n" + newAchBlock + "\r\n}\r\n";
        }
        return null;
    }

    private static string ComputeCommonPrefix(IReadOnlyList<string> strings)
    {
        if (strings.Count < 2) return string.Empty;
        var prefix = strings[0];
        for (int i = 1; i < strings.Count; i++)
        {
            int j = 0;
            while (j < prefix.Length && j < strings[i].Length && prefix[j] == strings[i][j])
                j++;
            prefix = prefix.Substring(0, j);
            if (prefix.Length == 0) return string.Empty;
        }

        if (strings.Any(s => s.Length <= prefix.Length))
        {
            int shortest = strings.Min(s => s.Length);
            prefix = shortest > 1 ? prefix.Substring(0, shortest - 1) : string.Empty;
        }

        if (prefix.Length == 0) return string.Empty;

        var suffixes = strings.Select(s => s[prefix.Length..]).ToList();
        if (suffixes.Distinct(StringComparer.Ordinal).Count() != suffixes.Count)
            return string.Empty;

        return prefix;
    }

    private static bool? ReadInstallerDataBitness(string installerDataPath, string? exeName)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(installerDataPath);
            if (!string.IsNullOrWhiteSpace(exeName))
            {
                foreach (var launcher in doc.Descendants("launcher"))
                {
                    var filePath = (string?)launcher.Element("filePath");
                    if (filePath is not null && filePath.EndsWith(exeName, StringComparison.OrdinalIgnoreCase))
                    {
                        var req = (string?)launcher.Element("requires64BitOS");
                        if (req is not null) return req.Trim() == "1";
                    }
                }
            }

            var reqEl = doc.Descendants("requirements").FirstOrDefault();
            if (reqEl is not null && reqEl.Attribute("osReqs64Bit") is { } attr)
            {
                return string.Equals(attr.Value, "true", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }
        return null;
    }

    private static EnablerPlanItem TextItem(string destination, string text, string description) =>
        new("write", destination, null, Utf8WithoutBom.GetBytes(text), description);

    private static EnablerPlanItem CopyItem(string destination, string source, string description) =>
        new("copy", destination, source, null, description);

    private static EnablerPlanItem RenameItem(string from, string to, string description) =>
        new("rename", to, from, null, description);

    private string? FindPayloadDirectory(string adapterFolder, string glob)
    {
        var candidates = new[]
        {
            Path.Combine(_adaptersRoot, adapterFolder),
            Path.Combine(_adaptersRoot, "Achievement-Enabler-main", "adapters", adapterFolder),
            Path.Combine(AppContext.BaseDirectory, "adapters", adapterFolder)
        };
        foreach (var candidate in candidates.Where(Directory.Exists))
        {
            var match = Directory.EnumerateDirectories(candidate, glob).FirstOrDefault();
            if (match is not null) return match;
            if (Directory.EnumerateFiles(candidate, "*.dll").Any()) return candidate;
        }
        return null;
    }

    private string? FindSteamOutput(string gameDirectory, string gameId)
    {
        var generatorRoots = FindGeneratorRoots();
        var gamesRoot = @"C:\Games";
        if (Directory.Exists(gamesRoot))
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(gamesRoot, "*", SearchOption.TopDirectoryOnly))
                {
                    var root = Path.Combine(directory, "generate_emu_config");
                    if (Directory.Exists(root)) generatorRoots.Add(root);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var outputDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(gameDirectory, "generate_emu_config", "output", gameId),
            Path.Combine(gameDirectory, "generate_emu_config", "_OUTPUT", gameId),
            Path.Combine(appData, "Achievements", "configs", "schema", "steam", gameId),
            Path.Combine(appData, "Achievements", "tools", "schema_parse", "output", gameId)
        };
        foreach (var root in generatorRoots)
        {
            outputDirectories.Add(Path.Combine(root, "output", gameId));
            outputDirectories.Add(Path.Combine(root, "_OUTPUT", gameId));
        }

        return outputDirectories
            .Where(candidate => Directory.Exists(candidate) &&
                (File.Exists(Path.Combine(candidate, "steam_settings", "achievements.json")) ||
                 File.Exists(Path.Combine(candidate, "achievements.json"))))
            .OrderByDescending(candidate =>
            {
                var file = Path.Combine(candidate, "steam_settings", "achievements.json");
                if (!File.Exists(file)) file = Path.Combine(candidate, "achievements.json");
                return File.GetLastWriteTimeUtc(file);
            })
            .FirstOrDefault();
    }

    private HashSet<string> FindGeneratorRoots()
    {
        try { EmbeddedGeneratorRuntime.EnsureReady(); } catch { }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            EmbeddedGeneratorRuntime.RootPath,
            Path.Combine(localAppData, "AchievementSync", "tools", "schema_parse"),
            Path.Combine(appData, "Achievements", "tools", "schema_parse"),
            @"C:\Program Files\Achievements\resources\tools\schema_parse",
            @"C:\Games\generate_emu_config",
            @"C:\Games\GSE Uplay R2 semi-auto AW Joker combined V11\generate_emu_config",
            Path.Combine(_adaptersRoot, "generate_emu_config"),
            Path.Combine(_adaptersRoot, "Achievement-Enabler-main", "generate_emu_config")
        };
        if (Directory.Exists(_adaptersRoot))
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(_adaptersRoot, "*", SearchOption.TopDirectoryOnly))
                {
                    var root = Path.Combine(directory, "generate_emu_config");
                    if (Directory.Exists(root)) roots.Add(root);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return roots.Where(Directory.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private string? FindSteamAchievementsFile(string gameDirectory, string? gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return null;
        var output = FindSteamOutput(gameDirectory, gameId);
        if (output is null) return null;
        var candidate = Path.Combine(output, "steam_settings", "achievements.json");
        if (File.Exists(candidate)) return candidate;
        candidate = Path.Combine(output, "achievements.json");
        return File.Exists(candidate) ? candidate : null;
    }

    private static byte[]? BuildUplaySchema(string achievementFile, out string prefix)
    {
        prefix = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(achievementFile));
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var schema = new JsonObject();
            foreach (var achievement in document.RootElement.EnumerateArray())
            {
                if (!achievement.TryGetProperty("name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String) continue;
                var name = nameValue.GetString() ?? string.Empty;
                var match = Regex.Match(name, @"^(.*?)(?:_)?(\d+)$", RegexOptions.CultureInvariant);
                if (!match.Success) continue;
                if (string.IsNullOrEmpty(prefix)) prefix = match.Groups[1].Value + "_";
                if (!int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId)) continue;
                var key = prefix + numericId.ToString(CultureInfo.InvariantCulture);
                schema[key] = new JsonObject
                {
                    ["displayName"] = ReadLocalizedText(achievement, "displayName"),
                    ["description"] = ReadLocalizedText(achievement, "description"),
                    ["earned"] = 0
                };
            }
            return schema.Count == 0 ? null : JsonSerializer.SerializeToUtf8Bytes(schema, JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ReadLocalizedText(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value)) return string.Empty;
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("english", out var english)
            && english.ValueKind == JsonValueKind.String) return english.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static string SetIniValue(string ini, string key, string value)
    {
        var pattern = $"(?im)^(\\s*{Regex.Escape(key)}\\s*=\\s*).*$";
        if (Regex.IsMatch(ini, pattern, RegexOptions.CultureInvariant))
            return Regex.Replace(ini, pattern, match => match.Groups[1].Value + value, RegexOptions.CultureInvariant);
        return ini.TrimEnd() + Environment.NewLine + key + " = " + value + Environment.NewLine;
    }

    private static string? FindExistingLoader(string gameDirectory, string? selectedExecutable,
        IReadOnlyCollection<string> names, out bool ambiguous)
    {
        ambiguous = false;
        var matches = SafeEnumerateFiles(gameDirectory)
            .Where(file => names.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0) return null;

        var executableIs64Bit = ReadExecutableIs64Bit(selectedExecutable);
        if (executableIs64Bit.HasValue)
        {
            var architectureMatches = matches.Where(path => Is64BitLoader(Path.GetFileName(path)) == executableIs64Bit.Value).ToList();
            if (architectureMatches.Count > 0) matches = architectureMatches;
        }

        var preferredDirectories = new List<string>();
        if (!string.IsNullOrWhiteSpace(selectedExecutable)) preferredDirectories.Add(Path.GetDirectoryName(selectedExecutable)!);
        preferredDirectories.Add(gameDirectory);
        foreach (var directory in preferredDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var preferred = matches.Where(path => string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase)).ToList();
            if (preferred.Count == 1) return preferred[0];
            if (preferred.Count > 1) { ambiguous = true; return null; }
        }

        if (matches.Count == 1) return matches[0];
        ambiguous = true;
        return null;
    }

    private static bool Is64BitLoader(string name) =>
        name.Contains("64", StringComparison.OrdinalIgnoreCase) || name.Contains("Win64", StringComparison.OrdinalIgnoreCase);

    private static bool? ReadExecutableIs64Bit(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return null;
        try
        {
            using var stream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) return null;
            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset < 0 || peOffset > stream.Length - 6) return null;
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return null;
            return reader.ReadUInt16() switch
            {
                0x8664 or 0xAA64 => true,
                0x014c => false,
                _ => null
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? FindGogAppId(string gameDirectory)
    {
        foreach (var file in SafeEnumerateFiles(gameDirectory))
        {
            var match = Regex.Match(Path.GetFileName(file), @"^goggame-(\d+)\.info$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }

    private static void AddDirectoryCopyPlan(string sourceDirectory, string destinationDirectory,
        List<EnablerPlanItem> items, string description)
    {
        foreach (var source in SafeEnumerateFiles(sourceDirectory))
        {
            var relative = Path.GetRelativePath(sourceDirectory, source);
            items.Add(CopyItem(Path.Combine(destinationDirectory, relative), source, description));
        }
    }

    private static List<string> SafeListFiles(string directory) => SafeEnumerateFiles(directory)
        .Select(Path.GetFileName).Where(name => name is not null).Select(name => name!).ToList();

    private static List<string> SafeEnumerateFiles(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        try
        {
            const int maxFiles = 100_000;
            var files = new List<string>();
            foreach (var file in Directory.EnumerateFiles(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false,
                MaxRecursionDepth = 64
            }))
            {
                if (files.Count >= maxFiles)
                    throw new InvalidDataException($"The selected folder contains more than {maxFiles:N0} files; narrow the selection and try again.");
                files.Add(file);
            }
            return files;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static async Task<string?> ReadSteamAppIdAsync(string directory)
    {
        // Do not look for steam_appid.txt or numeric folder names.
        // Instead, extract candidate game titles from executables, platform manifests, and folder names,
        // and fetch the AppID from SteamDB / Steam store search API.
        var candidates = new List<string>();

        // 1. GOG Game Detection: check for goggame-*.info metadata in directory or parent
        try
        {
            var gogDirs = new[] { directory, Directory.GetParent(directory)?.FullName };
            foreach (var d in gogDirs)
            {
                if (d is null || !Directory.Exists(d)) continue;
                foreach (var gogInfo in Directory.GetFiles(d, "goggame-*.info", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var content = File.ReadAllText(gogInfo);
                        if (JsonNode.Parse(content) is JsonObject gogObj &&
                            gogObj.TryGetPropertyValue("name", out var nameNode) && nameNode is not null)
                        {
                            var title = nameNode.ToString();
                            if (!string.IsNullOrWhiteSpace(title))
                                candidates.Insert(0, CleanGameTitle(title));
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        // 2. EA / Origin Game Detection: check installerdata.xml in __Installer or root
        try
        {
            var eaCandidates = new[]
            {
                Path.Combine(directory, "__Installer", "installerdata.xml"),
                Path.Combine(directory, "installerdata.xml"),
                Directory.GetParent(directory)?.FullName is { } eaParent ? Path.Combine(eaParent, "__Installer", "installerdata.xml") : null
            };
            foreach (var eaXml in eaCandidates)
            {
                if (eaXml is not null && File.Exists(eaXml))
                {
                    var xml = File.ReadAllText(eaXml);
                    var match = System.Text.RegularExpressions.Regex.Match(xml, @"<(?:gameTitle|title)[^>]*>(.*?)</(?:gameTitle|title)>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
                    {
                        candidates.Insert(0, CleanGameTitle(match.Groups[1].Value));
                    }
                }
            }
        }
        catch { }

        // 3. Ubisoft Game Detection: check uplay appid mapping first
        var uplayId = ReadUplayAppId(directory) ?? (Directory.GetParent(directory)?.FullName is { } parentUplay ? ReadUplayAppId(parentUplay) : null);
        if (!string.IsNullOrWhiteSpace(uplayId))
        {
            var mapped = UplaySteamMapper.LookupSteamAppId(uplayId);
            if (!string.IsNullOrWhiteSpace(mapped))
                return mapped;
        }

        // 4. Inspect game executables in the directory, parent, and common subfolders (Binaries/Win64, bin, steam)
        try
        {
            var searchDirs = new List<string> { directory };
            if (Directory.GetParent(directory)?.FullName is { } p) searchDirs.Add(p);
            foreach (var sub in new[] { "Binaries\\Win64", "bin", "bin64", "steam", "Win64", "x64" })
            {
                var subPath = Path.Combine(directory, sub);
                if (Directory.Exists(subPath)) searchDirs.Add(subPath);
                if (Directory.GetParent(directory)?.FullName is { } parentDir)
                {
                    var parentSub = Path.Combine(parentDir, sub);
                    if (Directory.Exists(parentSub)) searchDirs.Add(parentSub);
                }
            }

            foreach (var dir in searchDirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(dir)) continue;
                var exeFiles = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
                    .Where(f => !IsUtilityExecutable(Path.GetFileName(f)))
                    .ToList();

                foreach (var exe in exeFiles)
                {
                    try
                    {
                        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
                        if (!string.IsNullOrWhiteSpace(info.ProductName) && !IsGenericTitle(info.ProductName))
                            candidates.Add(CleanGameTitle(info.ProductName));
                        if (!string.IsNullOrWhiteSpace(info.FileDescription) && !IsGenericTitle(info.FileDescription))
                            candidates.Add(CleanGameTitle(info.FileDescription));
                    }
                    catch { }

                    var exeName = Path.GetFileNameWithoutExtension(exe);
                    if (!string.IsNullOrWhiteSpace(exeName) && !IsGenericTitle(exeName))
                        candidates.Add(CleanGameTitle(exeName));
                }
            }
        }
        catch { }

        // 5. Folder name and parent folder name
        var folderName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(folderName) && !IsGenericTitle(folderName))
            candidates.Add(CleanGameTitle(folderName));

        var parentName = Directory.GetParent(directory)?.Name;
        if (!string.IsNullOrWhiteSpace(parentName) && !IsGenericTitle(parentName))
            candidates.Add(CleanGameTitle(parentName));

        // Deduplicate non-empty candidate names
        var distinctCandidates = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c) && c.Length >= 2 && !long.TryParse(c, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var candidate in distinctCandidates)
        {
            try
            {
                var steamDbId = await SteamDbAppIdLookup.LookupAppIdAsync(candidate).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(steamDbId))
                    return steamDbId;
            }
            catch { }
        }

        return null;
    }

    private static bool IsUtilityExecutable(string filename)
    {
        var lower = filename.ToLowerInvariant();
        return lower.StartsWith("unins") || lower.Contains("setup") || lower.Contains("installer") ||
               lower.Contains("crash") || lower.Contains("report") || lower.Contains("launcher") ||
               lower.Contains("start") || lower.Contains("config") || lower.Contains("helper") ||
               lower.Contains("redist") || lower.Contains("dxweb") || lower.Contains("vcredist");
    }

    private static bool IsGenericTitle(string name)
    {
        var lower = name.Trim().ToLowerInvariant();
        return lower is "bin" or "binaries" or "win64" or "win32" or "x64" or "x86" or "game" or "shipping"
            or "retail" or "release" or "launcher" or "main" or "client" or "app" or "build"
            || long.TryParse(name, out _); // Avoid numeric folder names
    }

    private static string CleanGameTitle(string raw)
    {
        var clean = raw;
        // Remove text in brackets/parentheses e.g. [FitGirl Repack], (v1.0), [DODI]
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\[[^\]]*\]|\([^\)]*\)", " ");
        // Remove common release group and tag suffixes
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"(?i)([-_ ]*(CODEX|RUNE|SKIDROW|FLT|CPY|RELOADED|TENOKE|FitGirl|DODI|GOG|Steam|Repack|Multi\d+))+.*$", " ");
        // Remove version numbers e.g. v1.0.4, build 1234
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"(?i)\b(v\d+(\.\d+)*|build\s*\d+|update\s*\d+)\b", " ");
        // Replace separators with spaces
        clean = clean.Replace('_', ' ').Replace('.', ' ').Replace('-', ' ');
        // Split PascalCase if applicable (e.g. NieRAutomata -> NieR Automata)
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"([a-z])([A-Z])", "$1 $2");
        // Collapse spaces
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
        return clean;
    }

    private static string? ReadUplayAppId(string directory)
    {
        // Look for a Uplay app id file or registry-style marker in common locations.
        foreach (var candidate in new[]
                     {
                         Path.Combine(directory, "uplay_appid.txt"),
                         Path.Combine(directory, "appid.txt"),
                         Path.Combine(directory, "steam_settings", "uplay_appid.txt")
                     })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var value = File.ReadAllText(candidate).Trim();
                if (value.Length >= 1 && value.All(char.IsAsciiDigit))
                    return value;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        return null;
    }

    private static string? WritePreparedFile(string destination, string? source, byte[]? content, string allowedRoot)
    {
        var fullDestination = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(fullDestination) ?? throw new InvalidOperationException("The destination has no parent folder.");
        EnsureNoChildReparsePoint(allowedRoot, fullDestination);
        Directory.CreateDirectory(parent);
        EnsureNoChildReparsePoint(allowedRoot, fullDestination);
        var temporary = fullDestination + ".achievementsync-tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            if (source is not null)
            {
                File.Copy(source, temporary, overwrite: false);
            }
            else
            {
                using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    16 * 1024, FileOptions.WriteThrough);
                stream.Write(content ?? []);
                stream.Flush(flushToDisk: true);
            }

            var backup = BackupExistingFile(fullDestination);
            File.Move(temporary, fullDestination, overwrite: true);
            return backup;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private void ValidatePlanPaths(EnablerPlan plan)
    {
        var allowedSourceRoots = GetAllowedCopySourceRoots(plan);
        foreach (var item in plan.Items) ValidateItemPath(plan, item, allowedSourceRoots);
    }

    private IReadOnlyList<string> GetAllowedCopySourceRoots(EnablerPlan plan)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            _adaptersRoot,
            plan.Detection.GameDirectory,
            // Embedded runtime extraction directories under %LOCALAPPDATA%\AchievementSync\tools.
            // Plan items copy bundled emulator binaries and templates into the selected game folder,
            // so they must be allowed copy sources.
            EmbeddedRegularEmulatorRuntime.RootPath,
            EmbeddedColdLoaderRuntime.RootPath,
            EmbeddedColdClientRuntime.RootPath,
            EmbeddedUplayR1Runtime.RootPath,
            EmbeddedUplayR2Runtime.RootPath,
            EmbeddedEpicRuntime.RootPath,
            EmbeddedUniverseLanRuntime.RootPath,
            EmbeddedEaOriginRuntime.RootPath,
            AdapterOnlineMetadataService.EpicOutputRoot,
            AdapterOnlineMetadataService.UniverseLanReleasesRoot,
        };

        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        var gogAeCache = Path.Combine(systemDrive + Path.DirectorySeparatorChar, "steamcmd", "_GOG", "UniverseLAN");
        if (Directory.Exists(gogAeCache)) roots.Add(gogAeCache);

        if (!string.IsNullOrWhiteSpace(plan.Detection.Executable) &&
            Path.GetDirectoryName(Path.GetFullPath(plan.Detection.Executable)) is { } exeDir)
        {
            roots.Add(exeDir);
        }

        // Generator output is allowed for targets that copy generated catalog artwork/INI.
        if (AppIdValidator.IsValid(plan.MetadataGameId))
        {
            var output = FindSteamOutput(plan.Detection.GameDirectory, plan.MetadataGameId!);
            if (output is not null) roots.Add(output);
        }

        return roots.ToArray();
    }

    private void ValidateItemPath(EnablerPlan plan, EnablerPlanItem item, IReadOnlyList<string> allowedSourceRoots)
    {
        var destRoot = AllowedRootFor(plan, item.Destination);
        if (item.Kind.Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            EnsureNoChildReparsePoint(destRoot, item.Destination);
            return;
        }
        if (item.Kind.Equals("patch-ea-exe", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(item.Source))
                _ = AllowedRootFor(plan, item.Source);
            EnsureNoChildReparsePoint(destRoot, item.Destination);
            return;
        }
        if (!item.Kind.Equals("copy", StringComparison.OrdinalIgnoreCase)) return;
        if (item.Source is null) throw new InvalidOperationException("The plan contains a copy without a source file.");
        var sourceRoot = allowedSourceRoots.FirstOrDefault(root => IsPathWithin(root, item.Source));
        if (sourceRoot is null)
            throw new InvalidOperationException("The plan contains a source outside the adapter files, selected game folder, AppID generator output, and bundled Steam launcher files.");
        EnsureNoChildReparsePoint(sourceRoot, item.Source);
    }

    private static string AllowedRootFor(EnablerPlan plan, string destination)
    {
        var gameRoot = Path.GetFullPath(plan.Detection.GameDirectory);
        if (IsPathWithin(gameRoot, destination)) return gameRoot;

        if (!string.IsNullOrWhiteSpace(plan.Detection.Executable) &&
            Path.GetDirectoryName(Path.GetFullPath(plan.Detection.Executable)) is { } exeDir &&
            IsPathWithin(exeDir, destination))
        {
            return exeDir;
        }

        // UniverseLAN configuration and save data are placed under %LOCALAPPDATA%\UniverseLAN
        var universeLanRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniverseLAN");
        if (IsPathWithin(universeLanRoot, destination)) return universeLanRoot;

        throw new InvalidOperationException("The plan contains a destination outside the selected game or emulator folder.");
    }

    private static bool IsPathWithin(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureNoChildReparsePoint(string root, string destination)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullDestination = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(fullDestination) ?? throw new InvalidOperationException("The destination has no parent folder.");
        if (!IsPathWithin(fullRoot, parent))
            throw new InvalidOperationException("The destination parent is outside the allowed folder.");

        var relative = Path.GetRelativePath(fullRoot, parent);
        var current = fullRoot;
        if (relative != ".")
        {
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                current = Path.Combine(current, segment);
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("A destination folder is a symbolic link or junction.");
            }
        }
    }

    private static string? BackupExistingFile(string destination)
    {
        if (!File.Exists(destination)) return null;
        var basePath = destination + ".achievementsync-backup";
        var backup = File.Exists(basePath)
            ? basePath + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)
            : basePath;
        if (File.Exists(backup)) backup += "-" + Guid.NewGuid().ToString("N");
        File.Copy(destination, backup, overwrite: false);
        return backup;
    }

    public static string TargetDisplayName(EnablerTarget target) => target switch
    {
        EnablerTarget.SteamEmulator => "Steam emulator (steam_settings + achievement JSON)",
        EnablerTarget.SteamColdLoaderWithRegularEmulator => "Steam ColdLoader + Regular Emulator (version.dll proxy + Goldberg)",
        EnablerTarget.SteamColdClient => "Steam ColdClient (standalone launcher + generated catalog)",
        EnablerTarget.UplayR2 => "Ubisoft Connect R2 emulator",
        EnablerTarget.UplayR1 => "Ubisoft Connect R1 emulator",
        EnablerTarget.EpicEmulator => "Epic Games (Nemirtingas emulator)",
        EnablerTarget.GogUniverseLan => "GOG (UniverseLAN)",
        EnablerTarget.EaOrigin => "EA / Origin emulator",
        _ => target.ToString()
    };
}
