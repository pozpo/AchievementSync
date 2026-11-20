using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AchievementSync.Core.Enabler;

public enum EnablerTarget
{
    SteamEmulator,
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
    IReadOnlyList<string> Warnings);

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

    public EnablerDetection Detect(string gamePathOrExecutable)
    {
        var full = Path.GetFullPath(gamePathOrExecutable);
        var isFile = File.Exists(full) && !Directory.Exists(full);
        var executable = isFile ? full : null;
        var directory = isFile ? Path.GetDirectoryName(full)! : full;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);

        var evidence = new List<string>();
        var files = SafeListFiles(directory);
        bool Has(string name) => files.Contains(name, StringComparer.OrdinalIgnoreCase);

        var steamAppId = ReadSteamAppId(directory);
        if (steamAppId is not null) evidence.Add($"steam_appid.txt declares AppID {steamAppId}.");
        if (Has("steam_api64.dll") || Has("steam_api.dll")) evidence.Add("Steam API library found.");

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
        else if (Directory.Exists(Path.Combine(directory, "__Installer")) && File.Exists(Path.Combine(directory, "__Installer", "installerdata.xml")))
        {
            target = EnablerTarget.EaOrigin;
            evidence.Add("EA/Origin installer marker found.");
        }
        else if (steamAppId is not null || Has("steam_api64.dll") || Has("steam_api.dll"))
        {
            target = EnablerTarget.SteamEmulator;
            evidence.Add("Steam game detected; the Steam emulator configuration is suggested.");
        }
        else
        {
            evidence.Add("No store marker was found; the Steam emulator configuration is a safe default.");
        }

        return new EnablerDetection(directory, executable, steamAppId, target, evidence);
    }

    public EnablerPlan BuildPlan(EnablerDetection detection, EnablerTarget? target = null, string? gameIdOverride = null)
    {
        var chosen = target ?? detection.SuggestedTarget;
        var items = new List<EnablerPlanItem>();
        var warnings = new List<string>();
        var normalizedDirectory = Path.GetFullPath(detection.GameDirectory);
        if (!Directory.Exists(normalizedDirectory)) throw new DirectoryNotFoundException(normalizedDirectory);
        detection = detection with { GameDirectory = normalizedDirectory };
        var gameId = string.IsNullOrWhiteSpace(gameIdOverride) ? null : gameIdOverride.Trim();
        if (gameId is null && chosen is (EnablerTarget.SteamEmulator or EnablerTarget.UplayR1 or EnablerTarget.UplayR2 or EnablerTarget.EaOrigin))
            gameId = detection.SteamAppId;
        if (!string.IsNullOrWhiteSpace(gameId))
        {
            if (!SafeGameId.IsMatch(gameId) || gameId is "." or "..")
                throw new ArgumentException("The game ID can contain only letters, numbers, periods, underscores, and dashes.", nameof(gameIdOverride));
            if (chosen == EnablerTarget.SteamEmulator && !NumericSteamId.IsMatch(gameId))
                throw new ArgumentException("Steam AppIDs must contain 1 to 10 digits and cannot be zero.", nameof(gameIdOverride));
        }
        switch (chosen)
        {
            case EnablerTarget.SteamEmulator:
                BuildSteamPlan(detection, gameId, items, warnings);
                break;
            case EnablerTarget.UplayR1:
            case EnablerTarget.UplayR2:
                BuildUplayPlan(detection, chosen, gameId, items, warnings);
                break;
            case EnablerTarget.EpicEmulator:
                BuildEpicPlan(detection, gameId, items, warnings);
                break;
            case EnablerTarget.GogUniverseLan:
                BuildGogPlan(detection, gameId, items, warnings);
                break;
            case EnablerTarget.EaOrigin:
                BuildEaPlan(detection, gameId, items, warnings);
                break;
        }

        var plan = new EnablerPlan(detection, chosen, gameId, items, warnings);
        var overwriteCount = items.Select(item => item.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count(File.Exists);
        if (overwriteCount > 0)
            warnings.Add($"{overwriteCount} existing file(s) will be backed up with a timestamp before replacement.");
        ValidatePlanPaths(plan);
        return plan;
    }

    public EnablerApplyResult Apply(EnablerPlan plan)
    {
        var written = 0;
        var skipped = 0;
        var messages = new List<string>();
        foreach (var item in plan.Items)
        {
            try
            {
                ValidateItemPath(plan, item);
                if (item.Kind.Equals("copy", StringComparison.OrdinalIgnoreCase))
                {
                    if (item.Source is null || !File.Exists(item.Source)
                        || (!IsPathWithin(_adaptersRoot, item.Source) && !IsPathWithin(plan.Detection.GameDirectory, item.Source)))
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
                var savedBackup = WritePreparedFile(item.Destination, null, item.Content, AllowedRootFor(plan, item.Destination));
                if (savedBackup is not null) messages.Add($"Backup saved: {Path.GetFileName(savedBackup)}");
                written++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or InvalidOperationException)
            {
                skipped++;
                messages.Add($"{Path.GetFileName(item.Destination)} could not be written ({exception.GetType().Name}).");
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

        var generatorRoot = FindGeneratorRoots().FirstOrDefault(root =>
            File.Exists(Path.Combine(root, "generate_emu_config.exe")));
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

        var credentialStore = new SteamCredentialStore();
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

    private void BuildSteamPlan(EnablerDetection detection, string? gameId, List<EnablerPlanItem> items, List<string> warnings)
    {
        var coldClient = Path.Combine(detection.GameDirectory, "_ColdClient");
        var settings = Path.Combine(coldClient, "steam_settings");
        var extraDlls = Path.Combine(coldClient, "extra_dlls");
        var template = Path.Combine(detection.GameDirectory, "release", "steamclient_experimental");
        if (Directory.Exists(template))
        {
            AddDirectoryCopyPlan(template, coldClient, items, "ColdClient launcher and emulator files");
            var loaderIni = Path.Combine(template, "ColdClientLoader.ini");
            var hasLoaderExecutable = SafeListFiles(template).Any(name =>
                name.Equals("steamclient_loader_x64.exe", StringComparison.OrdinalIgnoreCase)
                || name.Equals("steamclient_loader_x86.exe", StringComparison.OrdinalIgnoreCase));
            if (!hasLoaderExecutable) warnings.Add("The staged ColdClient template has no steamclient_loader_x64.exe or steamclient_loader_x86.exe.");
            if (File.Exists(loaderIni) && detection.Executable is not null)
            {
                var relativeExe = Path.GetRelativePath(coldClient, detection.Executable);
                var ini = File.ReadAllText(loaderIni);
                ini = SetIniValue(ini, "Exe", relativeExe);
                if (!string.IsNullOrWhiteSpace(gameId)) ini = SetIniValue(ini, "AppId", gameId);
                ini = SetIniValue(ini, "DllsToInjectFolder", "extra_dlls");
                items.RemoveAll(item => string.Equals(item.Destination, Path.Combine(coldClient, "ColdClientLoader.ini"), StringComparison.OrdinalIgnoreCase));
                items.Add(TextItem(Path.Combine(coldClient, "ColdClientLoader.ini"), ini,
                    "ColdClient launcher config pointing to the selected game executable"));
            }
            else if (detection.Executable is null)
            {
                warnings.Add("Select the game executable as well as its folder so the ColdClient launcher can be pointed at it.");
            }
            else
            {
                warnings.Add("The staged ColdClient template has no ColdClientLoader.ini to point at the selected executable.");
            }
        }
        else
        {
            warnings.Add("The Goldberg ColdClient template is not staged at <game>\\release\\steamclient_experimental. This wizard will not invent the missing launcher; the adapter needs that template before it can launch the game.");
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
                warnings.Add($"Steam definitions, INI, and artwork are copied from {outputSettings}. AchievementSync's {Path.Combine(_metadataRoot, gameId, "achievements.json")} stores earned state only, so it is not used as the emulator catalog.");
            }
            else
            {
                warnings.Add($"No generated Steam catalog was found for AppID {gameId}. The adapter expects generate_emu_config\\output\\{gameId}\\steam_settings\\achievements.json (or the bundled _OUTPUT cache), plus its img folder for artwork.");
            }
        }
        else
        {
            warnings.Add("No Steam AppID was detected. Set the numeric AppID in the wizard before applying.");
        }

        items.Add(TextItem(Path.Combine(settings, "configs.user.ini"),
            "[user::general]\r\naccount_name=AchievementSync\r\nlanguage=english\r\n",
            "Steam emulator user configuration"));

        var stub32 = Path.Combine(_adaptersRoot, "steam_coldclient", "steamstub_x32.dll");
        var stub64 = Path.Combine(_adaptersRoot, "steam_coldclient", "steamstub_x64.dll");
        if (File.Exists(stub64)) items.Add(CopyItem(Path.Combine(extraDlls, "steamstub_x64.dll"), stub64, "64-bit SteamStub helper (ColdClient extra_dlls)"));
        if (File.Exists(stub32)) items.Add(CopyItem(Path.Combine(extraDlls, "steamstub_x32.dll"), stub32, "32-bit SteamStub helper (ColdClient extra_dlls)"));
        if (!File.Exists(stub32) && !File.Exists(stub64))
            warnings.Add("The SteamStub helper DLLs are not bundled. If required, the adapter places them under <game>\\_ColdClient\\extra_dlls, not beside the game executable.");
    }

    private void BuildUplayPlan(EnablerDetection detection, EnablerTarget target, string? gameId,
        List<EnablerPlanItem> items, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            warnings.Add("Ubisoft's adapter requires the cross-listed Steam AppID to fetch its achievement definitions and choose the per-game save folder. Enter that AppID in Game ID before planning.");
            return;
        }

        var variant = target == EnablerTarget.UplayR2 ? "ubisoft_uplay_r2" : "ubisoft_uplay_r1";
        var isR2 = target == EnablerTarget.UplayR2;
        var loaderNames = isR2
            ? new[] { "upc_r2_loader.dll", "upc_r2_loader64.dll", "uplay_r2_loader.dll", "uplay_r2_loader64.dll" }
            : new[] { "upc_r1_loader.dll", "upc_r1_loader64.dll", "uplay_r1_loader.dll", "uplay_r1_loader64.dll" };
        var loaderPath = FindExistingLoader(detection.GameDirectory, detection.Executable, loaderNames, out var ambiguous);
        if (loaderPath is null)
        {
            warnings.Add(ambiguous
                ? "More than one Ubisoft loader DLL was found. The adapter needs the exact loader location; the wizard did not choose one automatically."
                : "No matching Ubisoft loader DLL was found. The replacement loader and matching INI belong beside the game's existing upc_r1/uplay_r1 or upc_r2/uplay_r2 loader, which may be in a nested Binary folder.");
            return;
        }

        var destination = Path.GetDirectoryName(loaderPath)!;
        var loaderName = Path.GetFileName(loaderPath);
        var payloadDirectory = FindPayloadDirectory(variant, isR2 ? "GoldbergUplayR2-*" : "UplayR1-*");
        if (payloadDirectory is null)
        {
            warnings.Add($"The {variant} payload folder was not found under {_adaptersRoot}.");
            return;
        }

        var iniName = loaderName.StartsWith("upc_", StringComparison.OrdinalIgnoreCase)
            ? (isR2 ? "upc_r2.ini" : "upc_r1.ini")
            : (isR2 ? "uplay_r2.ini" : "uplay_r1.ini");
        var loaderSource = Path.Combine(payloadDirectory, loaderName);
        var iniSource = Path.Combine(payloadDirectory, iniName);
        if (!File.Exists(loaderSource) || !File.Exists(iniSource))
        {
            warnings.Add($"The adapter payload is missing the matching pair {loaderName} + {iniName}; no Ubisoft files were planned.");
            return;
        }

        items.Add(CopyItem(loaderPath, loaderSource, "Matching Ubisoft achievement loader (placed beside the detected loader)"));

        var achievementFile = FindSteamAchievementsFile(detection.GameDirectory, gameId);
        var achievementPrefix = string.Empty;
        var schema = achievementFile is null ? null : BuildUplaySchema(achievementFile, out achievementPrefix);
        if (schema is null)
        {
            achievementPrefix = string.Empty;
            warnings.Add("The Uplay schema is generated from generate_emu_config\\output\\<Steam AppID>\\steam_settings\\achievements.json, which supplies names, descriptions, and IDs. The viewer's achievement state JSON is not a schema.");
        }
        else
        {
            var schemaRoot = Path.Combine(detection.GameDirectory, "achievements_schema.json");
            items.Add(new EnablerPlanItem("write", schemaRoot, null, schema, "Uplay schema generated from Steam catalog definitions"));
            if (!string.Equals(destination, detection.GameDirectory, StringComparison.OrdinalIgnoreCase))
                items.Add(new EnablerPlanItem("write", Path.Combine(destination, "achievements_schema.json"), null, schema,
                    "Uplay schema beside the loader INI"));
        }

        var iniText = File.ReadAllText(iniSource);
        var savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GSE Saves", gameId ?? "unknown");
        iniText = SetIniValue(iniText, "Achievements", "1");
        iniText = SetIniValue(iniText, "SaveType", "2");
        iniText = SetIniValue(iniText, "AchSaveType", "1");
        iniText = SetIniValue(iniText, "SavePath", savePath);
        iniText = SetIniValue(iniText, "AchSavePath", savePath);
        iniText = SetIniValue(iniText, "AchKeyPrefix", achievementPrefix);
        items.Add(TextItem(Path.Combine(destination, iniName), iniText, "Matching Ubisoft INI with achievements enabled and save/schema paths set"));
        warnings.Add($"Ubisoft loader and {iniName} will be placed beside {loaderPath}; the schema is read from the Steam catalog output and also copied beside the INI when that folder is nested.");
    }

    private void BuildEpicPlan(EnablerDetection detection, string? gameId, List<EnablerPlanItem> items, List<string> warnings)
    {
        var sdkNames = new[] { "EOSSDK-Win64-Shipping.dll", "EOSSDK-Win32-Shipping.dll" };
        var sdkDll = FindExistingLoader(detection.GameDirectory, detection.Executable, sdkNames, out var ambiguous);
        if (sdkDll is null)
        {
            warnings.Add(ambiguous
                ? "More than one EOSSDK loader DLL was found. Select a game executable/folder with one clear SDK path before planning Epic files."
                : "No EOSSDK loader DLL was found. Epic settings belong in the nepice_settings folder next to the game's EOSSDK loader DLL.");
            return;
        }

        var settings = Path.Combine(Path.GetDirectoryName(sdkDll)!, "nepice_settings");
        var template = Path.Combine(_adaptersRoot, "Achievement-Enabler-main", "adapters", "epic_nemirtingas_epic_emulator", "NemirtingasEpicEmu.json");
        if (File.Exists(template) && !string.IsNullOrWhiteSpace(gameId))
        {
            try
            {
                var json = JsonNode.Parse(File.ReadAllBytes(template));
                if (json?["EOSEmu"]?["Application"] is JsonObject application)
                {
                    application["AppId"] = gameId;
                    items.Add(new EnablerPlanItem("write", Path.Combine(settings, "NemirtingasEpicEmu.json"), null,
                        JsonSerializer.SerializeToUtf8Bytes(json, JsonOptions), "Epic emulator config with the selected namespace"));
                }
                else warnings.Add("The bundled Nemirtingas config has an unexpected shape; no Epic config was planned.");
            }
            catch (JsonException)
            {
                warnings.Add("The bundled Nemirtingas config is not valid JSON; no Epic config was planned.");
            }
        }
        else if (string.IsNullOrWhiteSpace(gameId))
        {
            warnings.Add("Enter the Epic product namespace/AppId. The upstream adapter looks this up by title before writing NemirtingasEpicEmu.json.");
        }
        else
        {
            warnings.Add("The Nemirtingas Epic config template was not found in adapters.");
        }

        var epicOutput = Path.Combine(_adaptersRoot, "Achievement-Enabler-main", "adapters", "epic_nemirtingas_epic_emulator", "epic_output");
        var database = Path.Combine(epicOutput, "achievements_db.json");
        if (File.Exists(database))
        {
            items.Add(CopyItem(Path.Combine(settings, "achievements_db.json"), database, "Epic catalog fetched from the public Epic achievements API"));
            var images = Path.Combine(epicOutput, "achievements_images");
            if (Directory.Exists(images)) AddDirectoryCopyPlan(images, Path.Combine(settings, "achievements_images"), items, "Epic achievement artwork fetched from the API");
        }
        else
        {
            warnings.Add($"Epic achievement definitions and icons are fetched by the adapter's Epic API helper into epic_output\\achievements_db.json and epic_output\\achievements_images, then copied beside the emulator config. AchievementSync's {Path.Combine(_metadataRoot, gameId ?? "<namespace>", "achievements.json")} stores earned state only.");
        }

        warnings.Add($"Epic config, achievements_db.json, and images belong in {settings}. The matching Nemirtingas EOSSDK replacement is a separate release payload and is not bundled here, so the wizard does not replace the game's SDK DLL.");
    }

    private void BuildGogPlan(EnablerDetection detection, string? gameId, List<EnablerPlanItem> items, List<string> warnings)
    {
        var galaxyDll = FindExistingLoader(detection.GameDirectory, detection.Executable,
            new[] { "Galaxy.dll", "Galaxy64.dll" }, out var ambiguous);
        var gogId = gameId ?? FindGogAppId(detection.GameDirectory);
        if (galaxyDll is null)
        {
            warnings.Add(ambiguous
                ? "More than one Galaxy.dll was found. UniverseLAN must match the SDK version of the selected Galaxy DLL."
                : "No Galaxy.dll or Galaxy64.dll was found. The UniverseLAN replacement belongs beside the game's existing Galaxy DLL.");
        }
        else
        {
            warnings.Add($"UniverseLAN chooses a release compatible with the SDK version of {galaxyDll}; the replacement Galaxy DLL and matching server executable go beside that file.");
        }

        if (string.IsNullOrWhiteSpace(gogId))
            warnings.Add("The GOG App ID comes from goggame-<id>.info in the game folder; select it in the Game ID field if automatic detection cannot find that file.");
        else
            warnings.Add($"UniverseLAN.ini belongs in %LOCALAPPDATA%\\UniverseLAN; UniverseLANData and UniverseLANServerData belong in %LOCALAPPDATA%\\UniverseLAN\\{gogId}. This adapter does not generate an achievements.json file in the game folder.");

        warnings.Add("UniverseLAN binaries and its compatible release are not bundled in adapters, so this wizard only reports their actual destinations instead of writing a fake progress JSON.");
    }

    private void BuildEaPlan(EnablerDetection detection, string? gameId, List<EnablerPlanItem> items, List<string> warnings)
    {
        if (detection.Executable is null)
        {
            warnings.Add("EA's adapter needs the selected game's executable, not only the install folder.");
            return;
        }

        var exeDirectory = Path.GetDirectoryName(detection.Executable)!;
        warnings.Add($"The EA adapter places anadius.cfg and the matching 32-bit or 64-bit anadius emulator DLL beside the selected executable: {exeDirectory}.");
        warnings.Add("The bundled EA adapter derives its achievement definitions from installerdata.xml and the cross-listed Steam catalog, then patches the selected executable with Origin Unwrapper. Those emulator binaries/tools are not included here; the wizard will not create a made-up anadius_achievements.json or patch the executable.");
    }

    private static EnablerPlanItem TextItem(string destination, string text, string description) =>
        new("write", destination, null, Utf8WithoutBom.GetBytes(text), description);

    private static EnablerPlanItem CopyItem(string destination, string source, string description) =>
        new("copy", destination, source, null, description);

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
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(appData, "Achievements", "tools", "schema_parse"),
            Path.Combine(localAppData, "AchievementSync", "tools", "schema_parse"),
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

    private static string? ReadSteamAppId(string directory)
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(directory, "steam_appid.txt"),
                     Path.Combine(directory, "steam_settings", "steam_appid.txt")
                 })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var value = File.ReadAllText(candidate).Trim();
                if (value.Length is >= 1 and <= 10 && value.All(char.IsAsciiDigit) && value.Any(character => character != '0'))
                    return value;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        var folderName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));
        return folderName.Length is >= 1 and <= 10 && folderName.All(char.IsAsciiDigit) && folderName.Any(character => character != '0')
            ? folderName : null;
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
        foreach (var item in plan.Items) ValidateItemPath(plan, item);
    }

    private void ValidateItemPath(EnablerPlan plan, EnablerPlanItem item)
    {
        _ = AllowedRootFor(plan, item.Destination);
        if (!item.Kind.Equals("copy", StringComparison.OrdinalIgnoreCase)) return;
        if (item.Source is null) throw new InvalidOperationException("The plan contains a copy without a source file.");
        var sourceRoot = IsPathWithin(_adaptersRoot, item.Source) ? _adaptersRoot
            : IsPathWithin(plan.Detection.GameDirectory, item.Source) ? plan.Detection.GameDirectory
            : null;
        if (sourceRoot is null)
            throw new InvalidOperationException("The plan contains a source outside the adapter files and selected game folder.");
        EnsureNoChildReparsePoint(sourceRoot, item.Source);
    }

    private static string AllowedRootFor(EnablerPlan plan, string destination)
    {
        var gameRoot = Path.GetFullPath(plan.Detection.GameDirectory);
        if (IsPathWithin(gameRoot, destination)) return gameRoot;
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
        EnablerTarget.UplayR2 => "Ubisoft Connect R2 emulator",
        EnablerTarget.UplayR1 => "Ubisoft Connect R1 emulator",
        EnablerTarget.EpicEmulator => "Epic Games (Nemirtingas emulator)",
        EnablerTarget.GogUniverseLan => "GOG (UniverseLAN)",
        EnablerTarget.EaOrigin => "EA / Origin emulator",
        _ => target.ToString()
    };
}
