using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Reflection;
using System.Diagnostics;
using AchievementSync.Core.Metadata;
using AchievementSync.Core.Models;
using AchievementSync.Core.Parsing;
using AchievementSync.Core.Scanning;
using AchievementSync.Core.Synchronization;
using AchievementSync.Core.Settings;
using AchievementSync.Core.Tracking;
using AchievementSync.Core.Platforms;
using AchievementSync.Core.Enabler;
using AchievementSync.Desktop.Services;
using AchievementSync.Desktop.ViewModels;

var tempRoot = Path.Combine(Path.GetTempPath(), "AchievementSync-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);
DirectoryInfo? repositoryRoot = new(AppContext.BaseDirectory);
while (repositoryRoot is not null
    && !File.Exists(Path.Combine(repositoryRoot.FullName, "src", "AchievementSync.Desktop", "GameDetailsWindow.xaml")))
    repositoryRoot = repositoryRoot.Parent;
if (repositoryRoot is null) throw new DirectoryNotFoundException("Could not locate the repository root for smoke-test fixtures.");
var checks = 0;

try
{
    if (args.Contains("--playtime-wmi-fixture", StringComparer.OrdinalIgnoreCase))
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        return 0;
    }

    if (args.Contains("--mapping-review-only", StringComparer.OrdinalIgnoreCase))
    {
        var mappingReviewOnlyCreated = RunOnStaThread(() =>
        {
            var dialog = new AchievementSync.Desktop.Views.MappingReviewDialog(
                new MappingReviewViewModel(Array.Empty<SteamAppIdResolution>()));
            return dialog.Content is System.Windows.Controls.Grid grid
                && grid.Background is System.Windows.Media.Brush;
        });
        Check(mappingReviewOnlyCreated,
            "Mapping Review dialog initializes on an STA thread with its canvas background resolved");
        return 0;
    }

    if (args.Contains("--online-cache-integration", StringComparer.OrdinalIgnoreCase))
    {
        var integrationMetadataRoot = Path.Combine(tempRoot, "Metadata");
        var screenshotCache = new SteamDbScreenshotCache(integrationMetadataRoot);
        var screenshotCount = await screenshotCache.PrecacheAsync("4115450");
        var screenshotDirectory = Path.Combine(integrationMetadataRoot, "4115450", "screenshots");
        var screenshotFiles = Directory.Exists(screenshotDirectory)
            ? Directory.EnumerateFiles(screenshotDirectory).ToArray()
            : [];
        Check(screenshotCount > 0 && screenshotFiles.Length > 0
            && screenshotFiles.All(path => new FileInfo(path).Length > 0),
            "SteamDB/Steam Store fallback downloads real screenshot files into <AppID>/screenshots");

        var communityClient = new SteamCommunityCatalogClient(integrationMetadataRoot);
        var community = await communityClient.GetCatalogAsync("3008130", "Dying Light: The Beast");
        Check(community is not null && community.Catalog.Achievements.Count > 0,
            "Steam Community returns Dying Light: The Beast achievement definitions");
        var localCatalogCache = new LocalAchievementCatalogCache(integrationMetadataRoot);
        var cachedCatalog = await localCatalogCache.CacheCatalogAsync(
            "3008130", community!.Catalog, source: "steam-community", declaredTotal: community.DeclaredTotal);
        var iconDirectory = Path.Combine(integrationMetadataRoot, "3008130", "images");
        var iconFiles = Directory.Exists(iconDirectory)
            ? Directory.EnumerateFiles(iconDirectory).ToArray()
            : [];
        var validIconFiles = iconFiles.Count(IsImageFile);
        var describedEntries = cachedCatalog?.Achievements.Count(item => !string.IsNullOrWhiteSpace(item.Description)) ?? 0;
        Check(cachedCatalog is not null && cachedCatalog.Achievements.Count > 0
            && iconFiles.Length > 0 && validIconFiles == iconFiles.Length,
            "achievement icons are downloaded as valid image files into <AppID>/images");
        Check(describedEntries > 0, "online achievement descriptions are cached with their catalog entries");

        var darkSouls = await communityClient.GetCatalogAsync("374320", "DARK SOULS™ III");
        Check(darkSouls is not null && darkSouls.Catalog.Achievements.Count > 0,
            "Steam Community returns the DARK SOULS III catalog used by the missing-icon regression");
        var darkSoulsCatalog = await localCatalogCache.CacheCatalogAsync(
            "374320", darkSouls!.Catalog, source: "steam-community", declaredTotal: darkSouls.DeclaredTotal);
        var darkSoulsImages = Path.Combine(integrationMetadataRoot, "374320", "images");
        var darkSoulsIconFiles = Directory.Exists(darkSoulsImages)
            ? Directory.EnumerateFiles(darkSoulsImages).Where(IsImageFile).ToArray()
            : [];
        Check(darkSoulsCatalog is not null && darkSoulsIconFiles.Length > 0,
            "DARK SOULS III achievement icons are downloaded into <AppID>/images");

        var offlineCatalog = localCatalogCache.LoadCatalog("3008130");
        Check(File.Exists(Path.Combine(integrationMetadataRoot, "3008130", "catalog.json"))
            && offlineCatalog is not null && offlineCatalog.Achievements.Count > 0
            && offlineCatalog.Achievements.Any(item => AchievementIdentity.IsMetadataImageUri(item.IconUri)),
            "cached achievement catalog and local icon paths can be reloaded offline");

        Console.WriteLine($"CACHE INTEGRATION: screenshots={screenshotFiles.Length}; achievementIcons={validIconFiles}; descriptions={describedEntries}; metadataRoot={integrationMetadataRoot}.");
        return 0;
    }

    if (args.Contains("--readonly-viewer-compare", StringComparer.OrdinalIgnoreCase))
    {
        var settings = new SettingsStore().Load();
        if (string.IsNullOrWhiteSpace(settings.GitHubOwner) || string.IsNullOrWhiteSpace(settings.GitHubRepository)
            || string.IsNullOrWhiteSpace(settings.GitHubBranch))
        {
            Console.WriteLine("Read-only viewer comparison skipped because no viewer repository is configured.");
            return 2;
        }

        var localScan = await new AchievementScanner().ScanAsync(LocationCatalog.Build(settings));
        var incompleteProfiles = localScan.Games
            .Where(game => !game.HasCompleteCatalog && game.UnlistedAchievementsAreLocked)
            .Concat(localScan.Games.Where(game => !game.HasCompleteCatalog && !game.UnlistedAchievementsAreLocked))
            .Take(8)
            .ToArray();
        using var viewerHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var viewerClient = new ViewerGameInfoClient(viewerHttp, cacheDirectory: Path.Combine(tempRoot, "readonly-viewer-cache"));
        var viewerProgressClient = new ViewerAchievementProgressClient(viewerHttp);
        var gameDataUrl = $"https://raw.githubusercontent.com/{Uri.EscapeDataString(settings.GitHubOwner)}/{Uri.EscapeDataString(settings.GitHubRepository)}/{Uri.EscapeDataString(settings.GitHubBranch)}/game-data.json";
        using var gameDataResponse = await viewerHttp.GetAsync(gameDataUrl);
        if (!gameDataResponse.IsSuccessStatusCode)
        {
            Console.WriteLine($"Viewer game-data readback failed with HTTP {(int)gameDataResponse.StatusCode}; no writes were attempted.");
            return 3;
        }
        using var gameDataDocument = JsonDocument.Parse(await gameDataResponse.Content.ReadAsStringAsync());
        var remoteGames = gameDataDocument.RootElement;
        if (remoteGames.ValueKind == JsonValueKind.Object && TryGetJsonProperty(remoteGames, "games", out var gamesElement))
            remoteGames = gamesElement;
        if (remoteGames.ValueKind != JsonValueKind.Array)
        {
            Console.WriteLine("Viewer game-data has an unsupported structure; no writes were attempted.");
            return 3;
        }
        var matches = 0;
        var cataloguedMatches = 0;
        var measurableMatches = 0;
        var mergedCatalogComplete = 0;
        var mergedCatalogsWithUnknownStates = 0;
        var localIdsOutsideViewerCatalog = 0;
        var catalogEntriesCompared = 0;
        var profilesWithKnownLockedAbsenceSemantics = 0;
        var matchedUnlockedIdentifiers = 0;
        var localOnlyUnlockedIdentifiers = 0;
        var viewerOnlyUnlockedIdentifiers = 0;
        var viewerProgressFilesFound = 0;
        var viewerProfileMeasurable = 0;
        var viewerProfileUnlocked = 0;
        var viewerProgressStatuses = new Dictionary<ViewerProgressStatus, int>();
        var viewerRowsCompared = 0;
        var viewerUnlockedTotal = 0;
        var localKnownStateTotal = 0;
        var localCatalogMatchedUnlocks = 0;
        var matchedCounts = 0;
        var statuses = new Dictionary<ViewerCatalogStatus, int>();
        foreach (var game in incompleteProfiles)
        {
            var lookup = await viewerClient.GetCatalogAsync(
                settings.GitHubOwner, settings.GitHubRepository, settings.GitHubBranch, game.AppId);
            if (!lookup.Success || lookup.Catalog is null)
            {
                statuses[lookup.Status] = statuses.GetValueOrDefault(lookup.Status) + 1;
                continue;
            }

            matches++;
            if (game.UnlistedAchievementsAreLocked) profilesWithKnownLockedAbsenceSemantics++;
            var enriched = AchievementSync.Core.Scanning.ProfileMerger.EnrichWithCatalog(game, lookup.Catalog);
            if (enriched.TotalCount.HasValue) cataloguedMatches++;
            if (enriched.CompletionPercent.HasValue) measurableMatches++;
            if (enriched.HasCompleteCatalog) mergedCatalogComplete++;
            if (enriched.TotalCount.HasValue && enriched.Achievements.Any(item => !item.IsUnlocked.HasValue))
                mergedCatalogsWithUnknownStates++;

            var viewerProgress = await viewerProgressClient.GetProgressAsync(
                settings.GitHubOwner, settings.GitHubRepository, settings.GitHubBranch, game.AppId);
            if (viewerProgress.Success && viewerProgress.Progress is not null)
            {
                viewerProgressFilesFound++;
                var viewerMergedCard = new GameCardViewModel(game);
                viewerMergedCard.ApplyCatalog(lookup.Catalog);
                viewerMergedCard.ApplyViewerProgress(viewerProgress.Progress);
                if (viewerMergedCard.Profile.CompletionPercent.HasValue) viewerProfileMeasurable++;
                viewerProfileUnlocked += viewerMergedCard.Profile.UnlockedCount;
            }
            else
            {
                viewerProgressStatuses[viewerProgress.Status] = viewerProgressStatuses.GetValueOrDefault(viewerProgress.Status) + 1;
            }

            JsonElement remoteGame = default;
            foreach (var item in remoteGames.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && TryGetJsonProperty(item, "appid", out var remoteAppId)
                    && remoteAppId.ValueKind == JsonValueKind.String
                    && string.Equals(remoteAppId.GetString(), game.AppId, StringComparison.Ordinal))
                {
                    remoteGame = item;
                    break;
                }
            }
            if (remoteGame.ValueKind != JsonValueKind.Object
                || !TryGetJsonProperty(remoteGame, "info", out var remoteInfo)
                || !TryGetJsonProperty(remoteInfo, "achievements", out var remoteCatalog)
                || remoteCatalog.ValueKind != JsonValueKind.Object
                || !TryGetJsonProperty(remoteGame, "achievements", out var remoteProgress)
                || remoteProgress.ValueKind != JsonValueKind.Object)
                continue;

            var remoteCount = 0;
            var remoteUnlocked = 0;
            var localKnown = 0;
            var localUnlocked = 0;
            var remoteAchievementIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var remoteUnlockedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var localUnlockedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var catalogEntry in remoteCatalog.EnumerateObject())
            {
                remoteCount++;
                remoteAchievementIds.Add(catalogEntry.Name);
                if (TryGetJsonProperty(remoteProgress, catalogEntry.Name, out var state)
                    && TryGetJsonProperty(state, "earned", out var earned)
                    && (earned.ValueKind == JsonValueKind.True || (earned.ValueKind == JsonValueKind.Number && earned.TryGetInt32(out var number) && number == 1)))
                {
                    remoteUnlocked++;
                    remoteUnlockedIds.Add(catalogEntry.Name);
                }

                var localEntry = game.Achievements.FirstOrDefault(entry =>
                    string.Equals(entry.ApiName, catalogEntry.Name, StringComparison.OrdinalIgnoreCase));
                if (localEntry?.IsUnlocked is { } localState)
                {
                    localKnown++;
                    if (localState)
                    {
                        localUnlocked++;
                        localUnlockedIds.Add(catalogEntry.Name);
                    }
                }
            }
            catalogEntriesCompared += remoteCount;
            localIdsOutsideViewerCatalog += game.Achievements.Count(localEntry => !remoteAchievementIds.Contains(localEntry.ApiName));
            viewerRowsCompared++;
            viewerUnlockedTotal += remoteUnlocked;
            localKnownStateTotal += localKnown;
            localCatalogMatchedUnlocks += localUnlocked;
            matchedUnlockedIdentifiers += localUnlockedIds.Intersect(remoteUnlockedIds, StringComparer.OrdinalIgnoreCase).Count();
            localOnlyUnlockedIdentifiers += localUnlockedIds.Except(remoteUnlockedIds, StringComparer.OrdinalIgnoreCase).Count();
            viewerOnlyUnlockedIdentifiers += remoteUnlockedIds.Except(localUnlockedIds, StringComparer.OrdinalIgnoreCase).Count();
            if (localKnown == remoteCount && localUnlocked == remoteUnlocked) matchedCounts++;
        }

        Console.WriteLine($"Read-only local scan: games={localScan.Games.Count}; files={localScan.CandidateFiles}; issues={localScan.Issues.Count}; incompleteCatalogGames={localScan.Games.Count(game => !game.HasCompleteCatalog)}.");
        Console.WriteLine($"Public viewer read-only comparison: attempted={incompleteProfiles.Length}; catalogsFound={matches}; totalsAvailable={cataloguedMatches}; mergedCatalogsComplete={mergedCatalogComplete}; mergedCatalogsWithUnknownStates={mergedCatalogsWithUnknownStates}; localProgressMeasurable={measurableMatches}; profilesWithKnownLockedAbsenceSemantics={profilesWithKnownLockedAbsenceSemantics}; viewerProgressFilesFound={viewerProgressFilesFound}; mergedWithViewerMeasurable={viewerProfileMeasurable}; mergedViewerUnlocked={viewerProfileUnlocked}; gameDataRowsCompared={viewerRowsCompared}; viewerCatalogEntriesCompared={catalogEntriesCompared}; viewerUnlockedInComparedRows={viewerUnlockedTotal}; localKnownStatesWithinViewerCatalog={localKnownStateTotal}; localUnlockedWithinViewerCatalog={localCatalogMatchedUnlocks}; localIdsOutsideViewerCatalog={localIdsOutsideViewerCatalog}; matchingUnlockedIds={matchedUnlockedIdentifiers}; localOnlyUnlockedIds={localOnlyUnlockedIdentifiers}; viewerOnlyUnlockedIds={viewerOnlyUnlockedIdentifiers}; exactStateCountMatches={matchedCounts}; no uploads or source/settings writes; temporary test cache removed.");
        foreach (var status in statuses.OrderBy(item => item.Key)) Console.WriteLine($"Viewer response {status.Key}: {status.Value}.");
        foreach (var status in viewerProgressStatuses.OrderBy(item => item.Key)) Console.WriteLine($"Viewer progress response {status.Key}: {status.Value}.");
        return 0;
    }

    var jsonPath = Path.Combine(tempRoot, "achievements.json");
    File.WriteAllText(jsonPath, """
        {
          "ACH_ALPHA": { "earned": true, "earned_time": 1700000000 },
          "ACH_BETA": { "earned": false, "earned_time": 0 }
        }
        """);
    var jsonResult = new JsonAchievementParser().Parse(jsonPath, "100");
    Check(jsonResult.Success, "JSON object profile parses");
    Check(!jsonResult.Profile!.HasCompleteCatalog && jsonResult.Profile.Achievements.Count == 2
        && jsonResult.Profile.Achievements.All(item => item.IsUnlocked.HasValue),
        "JSON object keeps explicit player states but does not claim a full catalog without metadata");
    Check(jsonResult.Profile.Achievements.Single(item => item.ApiName == "ACH_ALPHA").IsUnlocked == true, "JSON true unlock state is retained");

    var unlockedOnlyPath = Path.Combine(tempRoot, "unlocked-only.json");
    File.WriteAllText(unlockedOnlyPath, "{\"ACH_ONLY_UNLOCKED\":{\"earned\":true,\"earned_time\":1700000000}}");
    var unlockedOnlyResult = new JsonAchievementParser().Parse(unlockedOnlyPath, "101");
    Check(unlockedOnlyResult.Success && !unlockedOnlyResult.Profile!.HasCompleteCatalog
        && unlockedOnlyResult.Profile.Achievements.Single().IsUnlocked == true,
        "unlocked-only JSON retains confirmed progress without claiming it is the full catalog");

    var emptyJsonPath = Path.Combine(tempRoot, "empty", "achievements.json");
    Directory.CreateDirectory(Path.GetDirectoryName(emptyJsonPath)!);
    File.WriteAllText(emptyJsonPath, "{}");
    var emptyJson = new JsonAchievementParser().Parse(emptyJsonPath, "102");
    Check(emptyJson.Success && emptyJson.Profile!.Achievements.Count == 0 && !emptyJson.Profile.HasCompleteCatalog,
        "an empty achievement object is retained as an empty progress snapshot without inventing a catalog");

    var dbPath = Path.Combine(tempRoot, "200.db");
    File.WriteAllText(dbPath, """
        [
          { "apiname": "ACH_GAMMA", "achieved": 1, "unlocktime": 1700000010 },
          { "apiname": "ACH_DELTA", "achieved": 0, "unlocktime": 0 }
        ]
        """);
    var dbResult = new JsonAchievementParser().Parse(dbPath, "200");
    Check(dbResult.Success && dbResult.Profile!.Achievements.Count == 2, "JSON array .db profile parses");
    Check(dbResult.Profile!.Achievements.Single(item => item.ApiName == "ACH_GAMMA").IsUnlocked == true, ".db achieved flag maps to unlocked");

    var iniPath = Path.Combine(tempRoot, "achievements.ini");
    File.WriteAllText(iniPath, "[ACH_EPSILON]\nachieved=1\ntimestamp=1700000020\n[ACH_ZETA]\nstate=0\n");
    var iniResult = new IniAchievementParser().Parse(iniPath, "300");
    Check(iniResult.Success && iniResult.Profile!.Achievements.Count == 2, "INI profile parses section states");
    Check(!iniResult.Profile!.HasCompleteCatalog, "INI profile does not claim a complete catalog");

    var schemaPath = Path.Combine(tempRoot, "schema.json");
    File.WriteAllText(schemaPath, "[{\"name\":\"ACH_SCHEMA_ONLY\",\"hidden\":0,\"displayName\":{\"english\":\"Schema only\"},\"description\":{\"english\":\"Catalog entry\"},\"icon\":\"https://example.invalid/icon.png\"}]");
    var schemaResult = new JsonAchievementParser().Parse(schemaPath, "400");
    Check(schemaResult.Success && schemaResult.Profile!.HasCompleteCatalog, "metadata-only schema is retained as a complete catalog");
    Check(schemaResult.Profile!.Achievements.Single().IsUnlocked is null, "metadata-only schema does not fabricate unlock states");
    var schemaGame = new AchievementSync.Core.Models.GameProfile("400", null, [schemaPath], schemaResult.Profile!.Achievements, true);
    Check(schemaGame.TotalCount == 1 && schemaGame.CompletionPercent is null, "unknown schema progress is not counted as locked");

    var catalogDirectory = Path.Combine(tempRoot, "catalog", "730");
    var progressDirectory = Path.Combine(tempRoot, "progress", "730");
    Directory.CreateDirectory(catalogDirectory);
    Directory.CreateDirectory(progressDirectory);
    File.WriteAllText(Path.Combine(catalogDirectory, "achievements.json"), "[{\"name\":\"ACH_A\",\"displayName\":{\"english\":\"Alpha\"}},{\"name\":\"ACH_B\",\"displayName\":{\"english\":\"Beta\"}}]");
    File.WriteAllText(Path.Combine(progressDirectory, "achievements.json"), "{\"ACH_A\":{\"earned\":true,\"earned_time\":1700000000}}");
    var catalogMerge = await new AchievementScanner().ScanAsync(
    [
        new ScanLocation("catalog", "Catalog", Path.Combine(tempRoot, "catalog"), true, true, true),
        new ScanLocation("progress", "Progress", Path.Combine(tempRoot, "progress"), true, true, true)
    ]);
    var catalogGame = catalogMerge.Games.Single();
    Check(catalogGame.TotalCount == 2 && catalogGame.UnlockedCount == 1, "metadata catalog enriches matching player progress");
    Check(catalogGame.CompletionPercent is null && catalogGame.Achievements.Single(item => item.ApiName == "ACH_B").IsUnlocked is null,
        "catalog entries without player state remain unknown, not locked");

    var viewerDirectory = Path.Combine(tempRoot, "viewer", "730");
    Directory.CreateDirectory(viewerDirectory);
    var viewerGameInfoJson = """
        {"appid":"730","name":"Fixture Game","icon":"fixture-icon","achievements":{"ACH_ALPHA":{"name":"Alpha","description":"Catalog description","icon":"https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/730/alpha.jpg","icongray":"gray","hidden":0},"ACH_BETA":{"name":"Beta","description":"Hidden catalog entry","icongray":"gray","hidden":1}}}
        """;
    File.WriteAllText(Path.Combine(viewerDirectory, "game-info.json"), viewerGameInfoJson);
    File.WriteAllText(Path.Combine(viewerDirectory, "achievements.json"), """
        {"ACH_ALPHA":{"earned":true,"earned_time":1700000030}}
        """);
    var viewerScan = await new AchievementScanner().ScanAsync(
    [new ScanLocation("viewer", "Viewer", Path.Combine(tempRoot, "viewer"), true, true, true)]);
    var viewerGame = viewerScan.Games.Single();
    Check(viewerGame.Title == "Fixture Game", "viewer game-info.json supplies a display title");
    Check(viewerGame.Achievements.Count == 2 && viewerGame.UnlockedCount == 1, "viewer game-info catalog merges with progress JSON");
    var viewerLockedUnknown = viewerGame.Achievements.Single(item => item.ApiName == "ACH_BETA");
    Check(viewerLockedUnknown.IsUnlocked is null && viewerLockedUnknown.IsHidden, "viewer catalog metadata retains hidden and unknown state");
    Check(viewerGame.Achievements.Single(item => item.ApiName == "ACH_ALPHA").Description == "Catalog description",
        "viewer catalog supplies achievement descriptions and icons");
    var parsedViewerCatalog = JsonAchievementParser.ParseViewerGameInfo(viewerGameInfoJson, "730");
    Check(parsedViewerCatalog.Success && parsedViewerCatalog.Profile!.Achievements.All(item => item.IsUnlocked is null),
        "viewer game-info parser keeps its catalog separate from player progress");

    var gseUnlockListRoot = Path.Combine(tempRoot, "gse-unlock-list");
    var gseUnlockListDirectory = Path.Combine(gseUnlockListRoot, "730");
    Directory.CreateDirectory(gseUnlockListDirectory);
    File.WriteAllText(Path.Combine(gseUnlockListDirectory, "achievements.json"),
        "{\"ACH_ALPHA\":{\"earned\":true,\"earned_time\":1700000030}}");
    var gseUnlockScan = await new AchievementScanner().ScanAsync(
    [new ScanLocation("gse", "GSE Saves", gseUnlockListRoot, true, false, true)]);
    var gseWithCatalog = ProfileMerger.EnrichWithCatalog(gseUnlockScan.Games.Single(), parsedViewerCatalog.Profile!);
    Check(gseWithCatalog.TotalCount == 2 && gseWithCatalog.UnlockedCount == 1 && gseWithCatalog.CompletionPercent == 50
        && gseWithCatalog.Achievements.Single(item => item.ApiName == "ACH_BETA").IsUnlocked == false,
        "GSE unlock-list progress plus viewer catalog matches the viewer's documented missing-entry-is-locked rule");

    var partialJsonRoot = Path.Combine(tempRoot, "partial-json");
    var partialJsonDirectory = Path.Combine(partialJsonRoot, "730");
    Directory.CreateDirectory(partialJsonDirectory);
    File.WriteAllText(Path.Combine(partialJsonDirectory, "achievements.json"),
        "{\"ACH_ALPHA\":{\"earned\":true,\"earned_time\":1700000030}}");
    var partialJsonScan = await new AchievementScanner().ScanAsync(
    [new ScanLocation("custom-partial", "Partial JSON", partialJsonRoot, true, true, true)]);
    var partialWithCatalog = ProfileMerger.EnrichWithCatalog(partialJsonScan.Games.Single(), parsedViewerCatalog.Profile!);
    Check(partialWithCatalog.UnlockedCount == 1 && partialWithCatalog.CompletionPercent == 50
        && partialWithCatalog.Achievements.Single(item => item.ApiName == "ACH_BETA").IsUnlocked == false,
        "catalog enrichment counts absent achievements as locked when saved player states exist");
    var partialGameCard = new GameCardViewModel(partialJsonScan.Games.Single());
    var viewerSavedProgress = JsonAchievementParser.ParseViewerAchievementProgress(
        "{\"ACH_ALPHA\":{\"earned\":true,\"earned_time\":1700000030}}", "730", "viewer://pozpo/viewer/user/AppID/730/achievements.json");
    Check(viewerSavedProgress.Success && viewerSavedProgress.Profile is { HasCompleteCatalog: false, UnlistedAchievementsAreLocked: true },
        "viewer progress parser keeps saved unlocks separate and records the viewer's missing-entry-is-locked rule");
    partialGameCard.ApplyCatalog(parsedViewerCatalog.Profile!);
    partialGameCard.ApplyViewerProgress(viewerSavedProgress.Profile!);
    Check(partialGameCard.Profile.CompletionPercent == 50
        && partialGameCard.Profile.Achievements.Single(item => item.ApiName == "ACH_BETA").IsUnlocked == false
        && partialGameCard.SourceCountText.Contains("public viewer progress", StringComparison.OrdinalIgnoreCase),
        "public viewer saved progress completes a partial local profile without replacing confirmed local unlocks");
    var conflictingViewerProgress = JsonAchievementParser.ParseViewerAchievementProgress(
        "{\"ACH_ALPHA\":{\"earned\":false}}", "730", "viewer://pozpo/viewer/user/AppID/730/achievements.json");
    var localUnlockWinsCard = new GameCardViewModel(partialJsonScan.Games.Single());
    localUnlockWinsCard.ApplyCatalog(parsedViewerCatalog.Profile!);
    localUnlockWinsCard.ApplyViewerProgress(conflictingViewerProgress.Profile!);
    Check(localUnlockWinsCard.Profile.Achievements.Single(item => item.ApiName == "ACH_ALPHA").IsUnlocked == true,
        "viewer progress merging never downgrades a confirmed local unlock");
    var emptyViewerProgress = JsonAchievementParser.ParseViewerAchievementProgress("{}", "730", "viewer://pozpo/viewer/user/AppID/730/achievements.json");
    var emptyViewerCard = new GameCardViewModel(new GameProfile("730", null, [], [], false));
    emptyViewerCard.ApplyCatalog(parsedViewerCatalog.Profile!);
    emptyViewerCard.ApplyViewerProgress(emptyViewerProgress.Profile!);
    Check(emptyViewerCard.Profile.CompletionPercent == 0,
        "an existing empty viewer progress file with a complete catalog represents confirmed zero percent");

    var emptyGseRoot = Path.Combine(tempRoot, "empty-gse");
    var emptyGseDirectory = Path.Combine(emptyGseRoot, "730");
    Directory.CreateDirectory(emptyGseDirectory);
    File.WriteAllText(Path.Combine(emptyGseDirectory, "achievements.json"), "{}");
    var emptyGseScan = await new AchievementScanner().ScanAsync(
    [new ScanLocation("gse", "GSE Saves", emptyGseRoot, true, false, true)]);
    var emptyGseWithCatalog = ProfileMerger.EnrichWithCatalog(emptyGseScan.Games.Single(), parsedViewerCatalog.Profile!);
    Check(emptyGseWithCatalog.UnlockedCount == 0 && emptyGseWithCatalog.CompletionPercent == 0,
        "an empty GSE unlock snapshot with a complete catalog represents zero confirmed unlocks");
    var viewerExport = AchievementExportPlanner.Build([viewerGame]).Single();
    Check(viewerExport.RelativePath == "AppID/730/achievements.json" && viewerExport.AchievementCount == 1,
        "viewer export preview uses the compatible AppID file path and excludes unknown state");
    using (var exportJson = System.Text.Json.JsonDocument.Parse(viewerExport.Json))
    {
        var exportedState = exportJson.RootElement.GetProperty("ACH_ALPHA");
        Check(exportedState.GetProperty("earned").GetBoolean()
            && exportedState.GetProperty("earned_time").GetInt64() == 1700000030,
            "viewer export preview preserves known unlock state and timestamp");
        Check(!exportJson.RootElement.TryGetProperty("ACH_BETA", out _),
            "viewer export preview does not invent a state for catalog-only entries");
    }

    var mergedViewerJson = AchievementJsonMerge.Merge("""
        {"ACH_KEEP":{"earned":false,"earned_time":0},"ACH_NEW":{"earned":true,"earned_time":1700000100}}
        """, """
        {"ACH_KEEP":{"earned":true,"earned_time":1700000000},"ACH_REMOTE":{"earned":true,"earned_time":1700000200}}
        """);
    using (var mergedJson = System.Text.Json.JsonDocument.Parse(mergedViewerJson))
    {
        Check(mergedJson.RootElement.EnumerateObject().Count() == 3, "sync merge unions remote and local achievement identifiers");
        Check(mergedJson.RootElement.GetProperty("ACH_KEEP").GetProperty("earned").GetBoolean()
            && mergedJson.RootElement.GetProperty("ACH_KEEP").GetProperty("earned_time").GetInt64() == 1700000000,
            "sync merge preserves a confirmed remote unlock over a local locked state");
        Check(AchievementJsonMerge.CountNewUnlocks("""
            {"ACH_KEEP":{"earned":true,"earned_time":1700000000},"ACH_REMOTE":{"earned":true,"earned_time":1700000200}}
            """, mergedViewerJson) == 1, "sync merge counts only newly earned achievements");
    }
    var invalidRemoteRejected = false;
    try { _ = AchievementJsonMerge.Merge("{}", "not json"); }
    catch (InvalidDataException) { invalidRemoteRejected = true; }
    Check(invalidRemoteRejected, "invalid remote JSON stops the sync preview safely");
    var blankRemoteRejected = false;
    try { _ = AchievementJsonMerge.Merge("{}", " "); }
    catch (InvalidDataException) { blankRemoteRejected = true; }
    Check(blankRemoteRejected, "blank remote achievement files are not treated as missing files");
    var newSettings = new AppSettings();
    Check(!newSettings.AutoBackupSavesAfterPlaytime && !newSettings.AutoSyncAfterUnlock && newSettings.GitHubBranch == "user",
        "automatic uploads are opt-in and the compatible viewer branch is the default");

    var devicePoll = 0;
    var requestedIntervals = new List<int>();
    using (var oauthHttp = new HttpClient(new TestHttpHandler(async (request, cancellationToken) =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/device/code", StringComparison.Ordinal))
        {
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (!form.Contains("scope=public_repo", StringComparison.Ordinal)) throw new InvalidOperationException("Expected public_repo scope.");
            return JsonResponse("""{"device_code":"synthetic-device-code","user_code":"ABCD-EFGH","verification_uri":"https://github.com/login/device","expires_in":600,"interval":2}""");
        }

        devicePoll++;
        return devicePoll switch
        {
            1 => JsonResponse("""{"error":"authorization_pending"}"""),
            2 => JsonResponse("""{"error":"slow_down"}"""),
            _ => JsonResponse("""{"access_token":"synthetic-token","token_type":"bearer","scope":"public_repo","expires_in":28800,"refresh_token":"synthetic-refresh","refresh_token_expires_in":15897600}""")
        };
    })))
    {
        var deviceFlow = new GitHubDeviceFlowClient(oauthHttp, (delay, _) =>
        {
            requestedIntervals.Add((int)delay.TotalSeconds);
            return Task.CompletedTask;
        });
        var session = await deviceFlow.StartAsync("synthetic-client-id");
        var token = await deviceFlow.PollForTokenAsync("synthetic-client-id", session);
        Check(session.UserCode == "ABCD-EFGH" && token.AccessToken == "synthetic-token"
            && token.RefreshToken == "synthetic-refresh" && token.ExpiresInSeconds == 28800,
            "GitHub device flow parses synthetic authorization and expiry fields");
        Check(requestedIntervals.SequenceEqual([2, 2, 7]), "GitHub device flow honors polling interval and slow_down response");
    }

    using (var refreshHttp = new HttpClient(new TestHttpHandler(async (request, cancellationToken) =>
    {
        var form = await request.Content!.ReadAsStringAsync(cancellationToken);
        Check(form.Contains("grant_type=refresh_token", StringComparison.Ordinal)
            && form.Contains("refresh_token=old-refresh", StringComparison.Ordinal)
            && form.Contains("client_id=synthetic-client-id", StringComparison.Ordinal)
            && !form.Contains("client_secret", StringComparison.Ordinal),
            "OAuth refresh uses the device-flow grant without a client secret");
        return JsonResponse("""{"access_token":"rotated-access","refresh_token":"rotated-refresh","expires_in":28800,"refresh_token_expires_in":15897600,"scope":"public_repo","token_type":"bearer"}""");
    })))
    {
        var flow = new GitHubDeviceFlowClient(refreshHttp);
        var refreshed = await flow.RefreshAsync("synthetic-client-id", "old-refresh");
        var now = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        var stored = refreshed.ToStoredCredential(now);
        var restored = GitHubStoredCredential.Parse(stored.Serialize());
        Check(restored?.AccessToken == "rotated-access" && restored.RefreshToken == "rotated-refresh"
            && restored.AccessTokenExpiresAtUtc == now.AddSeconds(28800)
            && restored.RefreshTokenExpiresAtUtc == now.AddSeconds(15897600),
            "GitHub token expiry and rotated refresh credentials survive secure-store serialization");
        Check(stored.NeedsRefresh(now.AddSeconds(28700)), "GitHub credential refresh begins shortly before access-token expiry");
        Check(GitHubStoredCredential.Parse("legacy-access-token")?.AccessToken == "legacy-access-token",
            "legacy access-token-only Credential Manager entries remain readable");
    }

    var tokenExpiredHandled = false;
    using (var expiredHttp = new HttpClient(new TestHttpHandler((_, _) =>
        Task.FromResult(JsonResponse("""{"error":"token_expired"}""")))))
    {
        var flow = new GitHubDeviceFlowClient(expiredHttp, (_, _) => Task.CompletedTask);
        try
        {
            await flow.PollForTokenAsync("synthetic-client-id",
                new GitHubDeviceSession("expired-device", "ABCD-EFGH", new Uri("https://github.com/login/device"), 60, 1));
        }
        catch (GitHubAuthorizationException exception)
        {
            tokenExpiredHandled = exception.Message.Contains("expired", StringComparison.OrdinalIgnoreCase);
        }
    }
    Check(tokenExpiredHandled, "GitHub device flow handles the documented token_expired response");

    using (var accountHttp = new HttpClient(new TestHttpHandler((_, _) =>
    {
        var response = JsonResponse("""{"login":"pozpo","name":"Test Account","avatar_url":null}""");
        response.Headers.Add("X-OAuth-Scopes", "public_repo");
        return Task.FromResult(response);
    })))
    {
        var account = await new GitHubAchievementSyncClient(accountHttp).GetCurrentUserAsync("synthetic-token");
        Check(account.Login == "pozpo" && account.HasPublicRepositoryAccess,
            "GitHub account identity and granted public_repo scope are read from the authenticated response");
        var insufficientScopes = new GitHubAccount("pozpo", null, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Check(!insufficientScopes.HasPublicRepositoryAccess,
            "GitHub sign-in cannot proceed without the required public_repo grant");
    }

    var unauthorizedMapped = false;
    using (var unauthorizedHttp = new HttpClient(new TestHttpHandler((_, _) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)))))
    {
        try { _ = await new GitHubAchievementSyncClient(unauthorizedHttp).GetCurrentUserAsync("revoked-token"); }
        catch (GitHubAuthorizationException) { unauthorizedMapped = true; }
    }
    Check(unauthorizedMapped, "GitHub revoked-token responses require a fresh sign-in");

    using (var badRefreshHttp = new HttpClient(new TestHttpHandler((_, _) =>
        Task.FromResult(JsonResponse("""{"error":"bad_refresh_token"}""")))))
    {
        var refreshRejected = false;
        try { _ = await new GitHubDeviceFlowClient(badRefreshHttp).RefreshAsync("synthetic-client-id", "expired-refresh"); }
        catch (GitHubAuthorizationException exception)
        {
            refreshRejected = exception.Message.Contains("expired or was revoked", StringComparison.OrdinalIgnoreCase);
        }
        Check(refreshRejected, "GitHub expired or revoked refresh tokens require a fresh device sign-in");
    }

    var remoteAchievementJson = """
        {"ACH_KEEP":{"earned":true,"earned_time":1700000000},"ACH_REMOTE":{"earned":true,"earned_time":1700000200}}
        """;
    var remoteHead = "commit-old";
    var githubPushCalls = 0;
    using (var githubHttp = new HttpClient(new TestHttpHandler((request, _) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && path == "/repos/pozpo/viewer")
            return Task.FromResult(JsonResponse("""{"full_name":"pozpo/viewer","default_branch":"user","private":false,"permissions":{"push":true}}"""));
        if (request.Method == HttpMethod.Get && path.EndsWith("/git/ref/heads/user", StringComparison.Ordinal))
            return Task.FromResult(JsonResponse($"{{\"object\":{{\"sha\":\"{remoteHead}\"}}}}"));
        if (request.Method == HttpMethod.Get && path.EndsWith("/git/commits/commit-old", StringComparison.Ordinal))
            return Task.FromResult(JsonResponse("""{"tree":{"sha":"tree-old"}}"""));
        if (request.Method == HttpMethod.Get && path.EndsWith("/contents/AppID/730/achievements.json", StringComparison.Ordinal))
            return Task.FromResult(JsonResponse("{\"type\":\"file\",\"encoding\":\"base64\",\"content\":\"" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(remoteAchievementJson)) + "\"}"));
        if (request.Method == HttpMethod.Post && path.EndsWith("/git/blobs", StringComparison.Ordinal))
        {
            githubPushCalls++;
            return Task.FromResult(JsonResponse("""{"sha":"blob-new"}"""));
        }
        if (request.Method == HttpMethod.Post && path.EndsWith("/git/trees", StringComparison.Ordinal))
        {
            githubPushCalls++;
            return Task.FromResult(JsonResponse("""{"sha":"tree-new"}"""));
        }
        if (request.Method == HttpMethod.Post && path.EndsWith("/git/commits", StringComparison.Ordinal))
        {
            githubPushCalls++;
            return Task.FromResult(JsonResponse("""{"sha":"commit-new"}"""));
        }
        if (request.Method.Method == "PATCH" && path.EndsWith("/git/refs/heads/user", StringComparison.Ordinal))
        {
            githubPushCalls++;
            remoteHead = "commit-new";
            return Task.FromResult(JsonResponse("""{"object":{"sha":"commit-new"}}"""));
        }
        throw new InvalidOperationException($"Unexpected synthetic GitHub request: {request.Method} {path}");
    })))
    {
        var github = new GitHubAchievementSyncClient(githubHttp);
        var target = new GitHubSyncTarget("pozpo", "viewer", "user");
        var localFile = new AchievementExportFile("AppID/730/achievements.json", "730", 2, """
            {"ACH_KEEP":{"earned":false,"earned_time":0},"ACH_NEW":{"earned":true,"earned_time":1700000100}}
            """);
        var preview = await github.BuildPreviewAsync("synthetic-token", target, [localFile]);
        Check(preview.Changes.Count == 1 && preview.Changes[0].NewUnlockCount == 1,
            "GitHub preview merges local state with the remote file and proposes only changed AppID JSON");
        Check(preview.Changes[0].RelativePath == "AppID/730/achievements.json"
            && preview.Changes[0].ProposedJson.Contains("ACH_REMOTE", StringComparison.Ordinal),
            "GitHub preview preserves remote-only achievements and restricts output paths");
        var push = await github.PushAsync("synthetic-token", preview);
        Check(push.Pushed && push.UpdatedFiles == 1 && githubPushCalls == 4,
            "GitHub API push creates one content-only commit from a reviewed preview");
        var concurrentWriteStopped = false;
        try { _ = await github.PushAsync("synthetic-token", preview); }
        catch (GitHubConcurrentUpdateException) { concurrentWriteStopped = true; }
        Check(concurrentWriteStopped && githubPushCalls == 4,
            "GitHub push refuses a stale preview before creating commit data");
    }
    var numericMerge = AchievementJsonMerge.Merge(
        """{"ACH_LOCAL":{"earned":true,"earned_time":1700000000}}""",
        """{"ACH_REMOTE":{"earned":1,"earned_time":1700000001},"ACH_ZERO":{"earned":0,"earned_time":0}}""");
    Check(numericMerge.Contains("\"ACH_LOCAL\"", StringComparison.Ordinal)
        && numericMerge.Contains("\"ACH_REMOTE\"", StringComparison.Ordinal)
        && numericMerge.Contains("\"ACH_ZERO\"", StringComparison.Ordinal)
        && numericMerge.Contains("\"earned\": true", StringComparison.Ordinal)
        && numericMerge.Contains("\"earned\": false", StringComparison.Ordinal),
        "remote achievement data written with numeric earned values merges without losing states");

    var measurableGame = new GameProfile("570", "Fixture Game", [],
    [
        new AchievementRecord("ACH_EARNED", "Earned", null, true, 1700000000),
        new AchievementRecord("ACH_LOCKED", "Locked", null, false, 0)
    ], HasCompleteCatalog: true);
    var unknownStateGame = new GameProfile("730", "Unknown Fixture", [],
    [new AchievementRecord("ACH_UNKNOWN", "Unknown", null, null, null)], HasCompleteCatalog: true);
    var partialCatalogGame = new GameProfile("440", "Partial Fixture", [],
    [
        new AchievementRecord("ACH_KNOWN", "Known", null, true, 1700000100),
        new AchievementRecord("ACH_UNVERIFIED", "Unverified", null, null, null)
    ], HasCompleteCatalog: false);
    var completeSummary = GameLibrarySummary.Calculate([measurableGame]);
    Check(completeSummary.GameCount == 1 && completeSummary.TotalUnlocked == 1
        && completeSummary.TotalCatalogAchievements == 2 && completeSummary.CompletionPercent == 50,
        "fully measured catalog summary reports accurate counts and completion");
    var mixedSummary = GameLibrarySummary.Calculate([measurableGame, unknownStateGame]);
    Check(mixedSummary.TotalCatalogAchievements == 3 && mixedSummary.TotalKnownAchievements == 2
        && mixedSummary.CompletionPercent == 50 && mixedSummary.MeasurableGameCount == 1
        && mixedSummary.CataloguedGameCount == 2 && mixedSummary.GamesWithUnknownPlayerState == 1,
        "dashboard keeps measurable-game completion and known catalog/state coverage when another game is incomplete");
    var partialSummary = GameLibrarySummary.Calculate([partialCatalogGame]);
    Check(partialSummary.TotalCatalogAchievements is null && partialSummary.TotalKnownAchievements == 1
        && partialSummary.CompletionPercent is null,
        "partial catalog summary counts only known states and leaves completion unavailable");

    Check(AppIdValidator.IsValid("570940") && AppIdValidator.IsValid("1")
        && !AppIdValidator.IsValid("000") && !AppIdValidator.IsValid("not-an-appid")
        && !AppIdValidator.IsValid("12345678901"),
        "shared AppID validation rejects malformed identifiers without network access");

    var pngBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/GhoAAAAASUVORK5CYII=");
    var artworkCachePath = Path.Combine(tempRoot, "artwork-cache");
    var artworkRequests = 0;
    using (var artworkHttp = new HttpClient(new TestHttpHandler((request, _) =>
    {
        artworkRequests++;
        if (request.RequestUri!.Host == "store.steampowered.com")
            return Task.FromResult(JsonResponse("""{"570940":{"success":true,"data":{"name":"DARK SOULS: REMASTERED","header_image":"https://shared.steamstatic.com/header.png"}}}"""));
        if (request.RequestUri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)
            || request.RequestUri.Host == "steamcdn-a.akamaihd.net")
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pngBytes) });
        throw new InvalidOperationException("Unexpected artwork host.");
    })))
    {
        var artworkMetadata = await new SteamMetadataCache(artworkCachePath, artworkHttp).GetAsync("570940");
        Check(artworkMetadata?.Name == "DARK SOULS: REMASTERED" && artworkMetadata.ArtworkPath is { } artworkFile
            && File.Exists(artworkFile)
            && artworkFile == Path.Combine(artworkCachePath, "570940", "570940.cover")
            && File.Exists(Path.Combine(artworkCachePath, "570940", "catalog.json")),
            "Steam artwork and metadata use the AppID-scoped native metadata cache");
        var artworkCard = new GameCardViewModel(new GameProfile("570940", null, [], [], false));
        artworkCard.ApplyMetadata(artworkMetadata!);
        Check(artworkCard.Artwork is not null && artworkCard.FallbackVisibility == System.Windows.Visibility.Collapsed,
            "cached cover art decodes into a WPF image for a game card");
    }

    // The public Steam Store listing reports how many achievements an app defines, hidden ones included.
    // When a local GSE profile already carries a state for every listed achievement, the listing
    // completes the catalog instead of leaving the total permanently unknown.
    const string storeListingJson = """{"4115450":{"success":true,"data":{"name":"Fixture Game","header_image":"https://shared.steamstatic.com/header.png","achievements":{"total":3,"highlighted":[{"icon":"https://shared.steamstatic.com/icon.jpg","localized_name":"Hidden One","archived":0,"hidden":1,"name":"Hidden One","path":"https://shared.steamstatic.com/icon.jpg"}]}}}}""";
    Check(SteamStoreMetadataParser.Parse("4115450", storeListingJson)?.AchievementsTotal == 3,
        "Steam Store listing exposes the achievement total including hidden entries");
    Check(SteamStoreMetadataParser.Parse("4115450", """{"4115450":{"success":true,"data":{"name":"No Achievements","header_image":"https://shared.steamstatic.com/header.png"}}}""")?.AchievementsTotal is null,
        "Steam Store listing without an achievements section yields no total instead of a guess");
    var storeListingCache = Path.Combine(tempRoot, "store-listing-cache");
    using (var storeListingHttp = new HttpClient(new TestHttpHandler((_, _) =>
        Task.FromResult(JsonResponse(storeListingJson)))))
    {
        var listingMetadata = await new SteamMetadataCache(storeListingCache, storeListingHttp).GetAsync("4115450");
        Check(listingMetadata?.AchievementsTotal == 3
            && File.Exists(Path.Combine(storeListingCache, "4115450", "catalog.json")),
            "Steam Store listing total is cached per AppID for later launches");
        using (var listingReopenHttp = new HttpClient(new TestHttpHandler((_, _) =>
            throw new InvalidOperationException("A fresh cache entry should not download again."))))
        {
            var reopenedListing = await new SteamMetadataCache(storeListingCache, listingReopenHttp).GetAsync("4115450");
            Check(reopenedListing?.AchievementsTotal == 3,
                "cached Steam listing total is reused without another request");
        }
        var legacyListingCache = Path.Combine(tempRoot, "legacy-listing-cache");
        var legacyListingDir = Path.Combine(legacyListingCache, "4115452");
        Directory.CreateDirectory(legacyListingDir);
        await File.WriteAllTextAsync(Path.Combine(legacyListingDir, "steam.json"), JsonSerializer.Serialize(new
        {
            AppId = "4115452",
            Name = "Legacy Fixture",
            ArtworkPath = (string?)null,
            LastCheckedUtc = DateTimeOffset.UtcNow.AddHours(-7)
        }));
        var legacyListingHttp = new HttpClient(new TestHttpHandler((_, _) =>
            Task.FromResult(JsonResponse(storeListingJson.Replace("4115450", "4115452", StringComparison.Ordinal)))));
        var backfilledListing = await new SteamMetadataCache(legacyListingCache, legacyListingHttp).GetAsync("4115452");
        Check(backfilledListing?.AchievementsTotal == 3 && backfilledListing?.Name == "Fixture Game",
            "a legacy cache entry without a recorded listing total backfills it once");
    }
    var listingGameCard = new GameCardViewModel(new GameProfile("4115450", null, [],
    [
        new AchievementRecord("ACH_001", null, null, true, 1700000001),
        new AchievementRecord("ACH_002", null, null, false, 0),
        new AchievementRecord("ACH_003", null, null, true, 1700000002)
    ], HasCompleteCatalog: false));
    listingGameCard.ApplyMetadata(new CachedSteamMetadata("4115450", "Fixture Game", null, DateTimeOffset.UtcNow, 3));
    Check(listingGameCard.TotalCount == 3
        && listingGameCard.Profile.HasCompleteCatalog
        && listingGameCard.Progress == 66.7
        && listingGameCard.AchievementCountText.Contains("2 / 3", StringComparison.Ordinal),
        "a local GSE profile whose known states match the Steam listing total completes the catalog");
    var listingMismatchCard = new GameCardViewModel(new GameProfile("4115451", null, [],
    [
        new AchievementRecord("ACH_001", null, null, true, 1700000001)
    ], HasCompleteCatalog: false));
    listingMismatchCard.ApplyMetadata(new CachedSteamMetadata("4115451", "Fixture Game", null, DateTimeOffset.UtcNow, 3));
    Check(listingMismatchCard.TotalCount == 3 && !listingMismatchCard.Profile.HasCompleteCatalog
        && listingMismatchCard.Progress == 33.3,
        "a Steam listing total supplies measurable progress without claiming the local catalog is complete");

    using (var reopenedArtworkHttp = new HttpClient(new TestHttpHandler((_, _) =>
        throw new InvalidOperationException("A fresh cache entry should not trigger another download."))))
    {
        var reopenedArtwork = await new SteamMetadataCache(artworkCachePath, reopenedArtworkHttp).GetAsync("570940");
        Check(reopenedArtwork?.ArtworkPath is { } reopenedPath && File.Exists(reopenedPath) && artworkRequests == 2,
            "cached title and artwork reopen without a second metadata or image download");
    }

    var legacyArtworkCache = Path.Combine(tempRoot, "legacy-art-cache");
    var legacyCover = Path.Combine(legacyArtworkCache, "570940.cover");
    Directory.CreateDirectory(legacyArtworkCache);
    await File.WriteAllBytesAsync(legacyCover, pngBytes);
    await File.WriteAllTextAsync(Path.Combine(legacyArtworkCache, "570940.json"), JsonSerializer.Serialize(new
    {
        AppId = "570940",
        Name = "DARK SOULS: REMASTERED",
        ArtworkPath = legacyCover,
        LastCheckedUtc = DateTimeOffset.UtcNow
    }));
    using (var legacyArtworkHttp = new HttpClient(new TestHttpHandler((_, _) =>
        throw new InvalidOperationException("A fresh legacy cache migration should not make a network request."))))
    {
        var migratedArtwork = await new SteamMetadataCache(legacyArtworkCache, legacyArtworkHttp).GetAsync("570940");
        Check(migratedArtwork?.Name == "DARK SOULS: REMASTERED"
            && migratedArtwork.ArtworkPath == Path.Combine(legacyArtworkCache, "570940", "570940.cover")
            && File.Exists(Path.Combine(legacyArtworkCache, "570940", "catalog.json"))
            && SHA256.HashData(File.ReadAllBytes(legacyCover)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(migratedArtwork.ArtworkPath))),
            "legacy artwork cache migrates by copy into the new AppID directory without changing the old cache");
    }

    using (var corruptedArtworkHttp = new HttpClient(new TestHttpHandler((request, _) =>
    {
        if (request.RequestUri!.Host == "store.steampowered.com")
            return Task.FromResult(JsonResponse("""{"570940":{"success":true,"data":{"name":"DARK SOULS: REMASTERED","header_image":"https://shared.steamstatic.com/header.png"}}}"""));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("not an image"))
        });
    })))
    {
        var fallbackMetadata = await new SteamMetadataCache(Path.Combine(tempRoot, "bad-art-cache"), corruptedArtworkHttp).GetAsync("570940");
        var fallbackCard = new GameCardViewModel(new GameProfile("570940", null, [], [], false));
        if (fallbackMetadata is not null) fallbackCard.ApplyMetadata(fallbackMetadata);
        Check(fallbackMetadata?.Name == "DARK SOULS: REMASTERED" && fallbackMetadata.ArtworkPath is null
            && fallbackCard.Artwork is null && fallbackCard.FallbackVisibility == System.Windows.Visibility.Visible,
            "corrupted cover bytes keep the title and display the existing fallback instead of a broken image");
    }

    using (var failedArtworkHttp = new HttpClient(new TestHttpHandler((_, _) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))))
    {
        Check(await new SteamMetadataCache(Path.Combine(tempRoot, "missing-art-cache"), failedArtworkHttp).GetAsync("570940") is null,
            "unavailable Steam metadata leaves the game card on its fallback artwork without throwing");
    }

    var achievementRowType = typeof(AchievementSync.Desktop.GameDetailsWindow).Assembly
        .GetType("AchievementSync.Desktop.AchievementRow", throwOnError: true)!;
    var detailsWindowType = typeof(AchievementSync.Desktop.GameDetailsWindow);
    var emptyStateVisibility = detailsWindowType.GetMethod(
        "GetAchievementEmptyStateVisibility", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The achievement empty-state visibility check was not found.");
    Check((System.Windows.Visibility)emptyStateVisibility.Invoke(null, [0])! == System.Windows.Visibility.Visible
        && (System.Windows.Visibility)emptyStateVisibility.Invoke(null, [1])! == System.Windows.Visibility.Collapsed,
        "game details show an explanatory empty state only when no achievements match");
    var detailsXaml = File.ReadAllText(Path.Combine(repositoryRoot.FullName,
        "src", "AchievementSync.Desktop", "GameDetailsWindow.xaml"));
    Check(detailsXaml.Contains("x:Name=\"CatalogNote\"", StringComparison.Ordinal)
        && detailsXaml.Contains("x:Name=\"CatalogNote\" FontSize=\"10\" Foreground=\"{StaticResource MutedBrush}\" Margin=\"0,4,0,0\" TextWrapping=\"Wrap\"",
            StringComparison.Ordinal),
        "game details catalog note wraps instead of overlapping the search field at narrow widths");
    var mainWindowXaml = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "src", "AchievementSync.Desktop", "MainWindow.xaml"));
    var mainViewModelSource = File.ReadAllText(Path.Combine(repositoryRoot.FullName,
        "src", "AchievementSync.Desktop", "ViewModels", "MainViewModel.cs"));
    var platformMonitorSource = File.ReadAllText(Path.Combine(repositoryRoot.FullName,
        "src", "AchievementSync.Desktop", "Services", "PlatformSourceFileMonitor.cs"));
    Check(mainWindowXaml.Contains("Text=\"{Binding Description}\"", StringComparison.Ordinal)
        && mainWindowXaml.Contains("Save RA API key", StringComparison.Ordinal),
        "Platforms folder paths explain the source data and the credential button describes what it actually saves");
    Check(mainViewModelSource.Contains("(\"xenia\", \"Xenia\"", StringComparison.Ordinal)
        && mainViewModelSource.Contains("case \"xenia\": options.XeniaRoot", StringComparison.Ordinal)
        && mainViewModelSource.Contains("\"xenia\" => options.XeniaRoot", StringComparison.Ordinal)
        && platformMonitorSource.Contains("\"xbox360\" or \"xenia\" => \"xbox360\"", StringComparison.Ordinal),
        "the Xenia root is independently configurable and watched by the Xbox 360 adapter");
    Check(platformMonitorSource.Contains("\"xboxpc\" => \"xboxpc\"", StringComparison.Ordinal)
        && !platformMonitorSource.Contains("\"xblen\" or \"xboxpc\" => \"xbox\"", StringComparison.Ordinal),
        "Xbox PC folder changes are routed to the separate Xbox Network platform scan");
    Check(mainViewModelSource.Contains("Eglantine / playDB-CNT Switch 2 profile", StringComparison.Ordinal)
        && mainViewModelSource.Contains("Nintendo Switch 2 Recomp ports", StringComparison.Ordinal)
        && mainViewModelSource.Contains("case \"lichii\": options.LichiiRoot", StringComparison.Ordinal)
        && mainViewModelSource.Contains("case \"switch2recomp\": options.Switch2RecompRoot", StringComparison.Ordinal),
        "Switch 2 emulator, playDB-CNT profile, and recomp roots are exposed and wired in Platforms");
    Check(platformMonitorSource.Contains("\".log\"", StringComparison.Ordinal)
        && platformMonitorSource.Contains("\".trp\"", StringComparison.Ordinal)
        && platformMonitorSource.Contains("\".key\"", StringComparison.Ordinal)
        && platformMonitorSource.Contains("\".png\"", StringComparison.Ordinal)
        && platformMonitorSource.Contains("\".py\"", StringComparison.Ordinal)
        && platformMonitorSource.Contains("\".toml\"", StringComparison.Ordinal),
        "platform monitoring rescans after EA/PS4 logs, trophy archives and keys, trophy icons, launcher scripts, and ReXGlue TOML changes");
    var lumaMonitorSource = File.ReadAllText(Path.Combine(repositoryRoot.FullName,
        "src", "AchievementSync.Desktop", "Services", "LumaPlayRegistryMonitor.cs"));
    Check(mainWindowXaml.Contains("Enable LumaPlay registry monitoring", StringComparison.Ordinal)
        && mainViewModelSource.Contains("public bool LumaPlayWatcherEnabled", StringComparison.Ordinal)
        && lumaMonitorSource.Contains("RegNotifyChangeKeyValue", StringComparison.Ordinal)
        && !new AppSettings().LumaPlayWatcherEnabled,
        "LumaPlay monitoring has an opt-in Platforms control, persists its setting, and uses native registry change notifications");
    var appXaml = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "src", "AchievementSync.Desktop", "App.xaml"));
    var modernThemeXaml = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "src", "AchievementSync.Desktop", "Themes", "ModernTheme.xaml"));
    const string libraryDescription = "Browse games with at least one unlocked achievement. Filter by progress or sort by name and recent unlock.";
    var libraryDescriptionAt = mainWindowXaml.IndexOf(libraryDescription, StringComparison.Ordinal);
    var libraryDescriptionStart = libraryDescriptionAt < 0 ? -1 : mainWindowXaml.LastIndexOf("<TextBlock", libraryDescriptionAt, StringComparison.Ordinal);
    var libraryDescriptionEnd = libraryDescriptionAt < 0 ? -1 : mainWindowXaml.IndexOf("/>", libraryDescriptionAt, StringComparison.Ordinal);
    var libraryDescriptionElement = libraryDescriptionStart >= 0 && libraryDescriptionEnd > libraryDescriptionStart
        ? mainWindowXaml[libraryDescriptionStart..(libraryDescriptionEnd + 2)]
        : string.Empty;
    var captionStyleStart = modernThemeXaml.IndexOf("x:Key=\"Modern.Text.Caption\"", StringComparison.Ordinal);
    var captionStyleEnd = captionStyleStart < 0 ? -1 : modernThemeXaml.IndexOf("</Style>", captionStyleStart, StringComparison.Ordinal);
    var captionStyle = captionStyleStart >= 0 && captionStyleEnd > captionStyleStart
        ? modernThemeXaml[captionStyleStart..captionStyleEnd]
        : string.Empty;
    Check(libraryDescriptionElement.Contains("Style=\"{StaticResource Modern.Text.Caption}\"", StringComparison.Ordinal)
        && !libraryDescriptionElement.Contains("NoWrap", StringComparison.Ordinal)
        && captionStyle.Contains("Property=\"TextWrapping\" Value=\"Wrap\"", StringComparison.Ordinal),
        "Game Library description wraps within the page width at narrow window widths");

    Check(!mainWindowXaml.Contains("Overview", StringComparison.OrdinalIgnoreCase)
        && !mainWindowXaml.Contains("IsOverviewVisible", StringComparison.Ordinal),
        "Overview navigation and page markup are removed");
    var statsPosition = mainWindowXaml.IndexOf("GAMES FOUND", StringComparison.Ordinal);
    var knownStatesPosition = mainWindowXaml.IndexOf("KNOWN STATES", StringComparison.Ordinal);
    var unlockedPosition = mainWindowXaml.IndexOf("UNLOCKED", StringComparison.Ordinal);
    var completionPosition = mainWindowXaml.IndexOf("COMPLETION", StringComparison.Ordinal);
    var browsingControlsPosition = mainWindowXaml.IndexOf("Modern.SearchBox", StringComparison.Ordinal);
    Check(statsPosition >= 0 && statsPosition < knownStatesPosition
        && knownStatesPosition < unlockedPosition
        && unlockedPosition < completionPosition
        && completionPosition < browsingControlsPosition,
        "Game Library displays all four statistics before its browsing controls");
    Check(!detailsXaml.Contains("Identify game", StringComparison.OrdinalIgnoreCase)
        && !detailsXaml.Contains("IdentifyGame", StringComparison.Ordinal),
        "manual game identification controls are absent from game details");
    Check(appXaml.Contains("<Border Background=\"#151B25\"", StringComparison.Ordinal)
        && appXaml.Contains("Value=\"#293447\"", StringComparison.Ordinal)
        && appXaml.Contains("x:Key=\"GameCardSurface\"", StringComparison.Ordinal),
        "shared WPF theme supplies opaque dark dropdown surfaces and dark card interaction states");
    var nameDialogXaml = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "src", "AchievementSync.Desktop", "GameNameDialog.xaml"));
    Check(nameDialogXaml.Contains("ResizeMode=\"CanResize\"", StringComparison.Ordinal)
        && nameDialogXaml.Contains("IsCancel=\"True\"", StringComparison.Ordinal)
        && nameDialogXaml.Contains("IsDefault=\"True\"", StringComparison.Ordinal),
        "Edit Name remains resizable and keeps keyboard-accessible Cancel and Confirm actions");
    var detailsCode = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "src", "AchievementSync.Desktop", "GameDetailsWindow.xaml.cs"));
    var nameDialogCode = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "src", "AchievementSync.Desktop", "GameNameDialog.xaml.cs"));
    Check(detailsCode.Contains("WindowAppearance.Apply", StringComparison.Ordinal)
        && nameDialogCode.Contains("WindowAppearance.Apply", StringComparison.Ordinal),
        "owned WPF dialogs apply the same native Windows title-bar appearance as the main window");
    var processMonitorSourceForPowerShellCheck = File.ReadAllText(Path.Combine(repositoryRoot.FullName,
        "src", "AchievementSync.Desktop", "Services", "WindowsGameProcessMonitor.cs"));
    Check(!processMonitorSourceForPowerShellCheck.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase)
        && !processMonitorSourceForPowerShellCheck.Contains("pwsh.exe", StringComparison.OrdinalIgnoreCase)
        && !processMonitorSourceForPowerShellCheck.Contains(".ps1", StringComparison.OrdinalIgnoreCase),
        "native game process monitoring does not execute or depend on PowerShell scripts");

    var iconFactory = achievementRowType.GetMethod("CreateIconImage", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Achievement icon factory was not found.");
    var onDemandIcon = iconFactory.Invoke(null,
        ["https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/730/achievement-icon.png"])
        as System.Windows.Media.Imaging.BitmapImage;
    Check(onDemandIcon is null,
        "achievement detail icons wait for the asynchronous local cache instead of blocking on a remote image");

    var cachePathBlockedByFile = Path.Combine(tempRoot, "cache-path-is-a-file");
    File.WriteAllText(cachePathBlockedByFile, "existing file");
    using (var unusedArtworkHttp = new HttpClient(new TestHttpHandler((_, _) =>
        throw new InvalidOperationException("Cache storage failure must not require a network request."))))
    {
        var uncachedMetadata = await new SteamMetadataCache(cachePathBlockedByFile, unusedArtworkHttp)
            .StoreAsync(new SteamGameMetadata("570940", "DARK SOULS: REMASTERED", new Uri("https://shared.steamstatic.com/header.png")));
        Check(uncachedMetadata?.Name == "DARK SOULS: REMASTERED" && uncachedMetadata.ArtworkPath is null,
            "metadata caching failure preserves successful game identification and uses artwork fallback");
    }

    using (var viewerCatalogHttp = new HttpClient(new TestHttpHandler((request, _) =>
    {
        Check(request.RequestUri!.Host == "raw.githubusercontent.com"
            && request.RequestUri.AbsolutePath.EndsWith("/user/AppID/730/game-info.json", StringComparison.Ordinal),
            "viewer catalog lookup reads only the configured public game-info path");
        return Task.FromResult(JsonResponse("""{"appid":"730","name":"Fixture Game","achievements":{}}"""));
    })))
    {
        var catalog = await new ViewerGameInfoClient(viewerCatalogHttp, cacheDirectory: Path.Combine(tempRoot, "viewer-cache-empty"))
            .GetCatalogAsync("pozpo", "achievement-viewer", "user", "730");
        Check(catalog.Success && catalog.Catalog!.HasCompleteCatalog && catalog.Catalog.Achievements.Count == 0,
            "viewer game-info accepts a valid empty catalog without inventing achievements");
    }

    using (var malformedViewerCatalogHttp = new HttpClient(new TestHttpHandler((_, _) =>
        Task.FromResult(JsonResponse("not json")))))
    {
        Check((await new ViewerGameInfoClient(malformedViewerCatalogHttp, cacheDirectory: Path.Combine(tempRoot, "viewer-cache-malformed"))
            .GetCatalogAsync("pozpo", "viewer", "user", "730")).Status
            == ViewerCatalogStatus.MalformedResponse,
            "viewer catalog lookup reports malformed JSON as unavailable metadata");
    }

    using (var missingViewerCatalogHttp = new HttpClient(new TestHttpHandler((_, _) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))))
    {
        Check((await new ViewerGameInfoClient(missingViewerCatalogHttp, cacheDirectory: Path.Combine(tempRoot, "viewer-cache-missing"))
            .GetCatalogAsync("pozpo", "viewer", "user", "730")).Status
            == ViewerCatalogStatus.NotFound,
            "viewer catalog lookup distinguishes a missing generated catalog from player progress");
    }

    using (var viewerProgressHttp = new HttpClient(new TestHttpHandler((request, _) =>
    {
        Check(request.RequestUri!.Host == "raw.githubusercontent.com"
            && request.RequestUri.AbsolutePath.EndsWith("/user/AppID/730/achievements.json", StringComparison.Ordinal),
            "viewer progress lookup reads only the configured public AppID JSON path");
        return Task.FromResult(JsonResponse("{\"ACH_ALPHA\":{\"earned\":true,\"earned_time\":1700000030}}"));
    })))
    {
        var progress = await new ViewerAchievementProgressClient(viewerProgressHttp)
            .GetProgressAsync("pozpo", "achievement-viewer", "user", "730");
        Check(progress.Success && progress.Progress!.Achievements.Single().IsUnlocked == true,
            "public viewer progress lookup parses the user's saved unlocks");
    }

    using (var missingViewerProgressHttp = new HttpClient(new TestHttpHandler((_, _) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))))
    {
        Check((await new ViewerAchievementProgressClient(missingViewerProgressHttp)
            .GetProgressAsync("pozpo", "viewer", "user", "730")).Status == ViewerProgressStatus.NotFound,
            "missing viewer progress remains distinct from a confirmed zero-unlock profile");
    }

    using (var malformedViewerProgressHttp = new HttpClient(new TestHttpHandler((_, _) =>
        Task.FromResult(JsonResponse("not json")))))
    {
        Check((await new ViewerAchievementProgressClient(malformedViewerProgressHttp)
            .GetProgressAsync("pozpo", "viewer", "user", "730")).Status == ViewerProgressStatus.MalformedResponse,
            "malformed viewer progress is ignored instead of becoming locked progress");
    }

    using (var invalidViewerProgressHttp = new HttpClient(new TestHttpHandler((_, _) =>
        throw new InvalidOperationException("Invalid AppID must not request the viewer."))))
    {
        Check((await new ViewerAchievementProgressClient(invalidViewerProgressHttp)
            .GetProgressAsync("pozpo", "viewer", "user", "not-an-appid")).Status == ViewerProgressStatus.InvalidAppId,
            "viewer progress lookup rejects invalid AppIDs without making a request");
    }

    using (var mainWindowIdentifierHttp = new HttpClient(new TestHttpHandler((_, _) =>
        Task.FromResult(JsonResponse("""{"570940":{"success":true,"data":{"name":"DARK SOULS: REMASTERED"}}}""")))))
    {
        var libraryViewModel = new MainViewModel(
            new AchievementSync.Desktop.Services.SteamMetadataCache(Path.Combine(tempRoot, "identify-cache")),
            new AppSettings(),
            readStoredCredential: false,
            enableDiagnostics: false);
        Check(libraryViewModel.IsGamesVisible && libraryViewModel.CurrentPage == "Games",
            "Game Library is the primary page when the application opens");
        var desktopAssembly = typeof(MainViewModel).Assembly;
        Check(desktopAssembly.GetManifestResourceNames().Any(name =>
                name.EndsWith("epic-games-achievement-sound.wav", StringComparison.OrdinalIgnoreCase))
            && libraryViewModel.NotificationSoundOptions.Any(option => option.Code == "EpicGames"),
            "Epic Games achievement sound is embedded and selectable in notification settings");
        libraryViewModel.NavigationCommand.Execute("Activity");
        Check(libraryViewModel.IsActivityVisible,
            "Sync Activity navigation remains available without an Overview page");
        libraryViewModel.NavigationCommand.Execute("Games");
        Check(libraryViewModel.IsGamesVisible,
            "Game Library navigation remains available without an Overview page");

        var startupOffViewModel = new MainViewModel(
            new AchievementSync.Desktop.Services.SteamMetadataCache(Path.Combine(tempRoot, "startup-cache-off")),
            new AppSettings(),
            readStoredCredential: false,
            enableDiagnostics: false);
        Check(startupOffViewModel.StartupStatus == "Windows startup is off.",
            "startup status label reflects a disabled Windows startup setting at load");
        var startupEnabledViewModel = new MainViewModel(
            new AchievementSync.Desktop.Services.SteamMetadataCache(Path.Combine(tempRoot, "startup-cache-enabled")),
            new AppSettings { StartWithWindows = true },
            readStoredCredential: false,
            enableDiagnostics: false);
        Check(startupEnabledViewModel.StartupStatus == "AchievementSync will start minimized to the tray when you sign in.",
            "startup status label reflects an enabled Windows startup setting at load");

        var completeCard = new GameCardViewModel(new GameProfile("1", "Alpha", [],
        [
            new AchievementRecord("A", "A", null, true, 1700000200),
            new AchievementRecord("B", "B", null, false, 0)
        ], true));
        var perfectCard = new GameCardViewModel(new GameProfile("2", "Beta", [],
            [new AchievementRecord("C", "C", null, true, 1700000300)], true));
        var zeroUnlockCard = new GameCardViewModel(new GameProfile("3", "Gamma", [],
            [new AchievementRecord("D", "D", null, false, 0)], true));
        libraryViewModel.Games.Add(completeCard);
        libraryViewModel.Games.Add(perfectCard);
        libraryViewModel.Games.Add(zeroUnlockCard);
        libraryViewModel.GamesView.Refresh();
        Check(libraryViewModel.GamesView.Cast<GameCardViewModel>().Count() == 2
            && !libraryViewModel.GamesView.Cast<GameCardViewModel>().Any(game => game.AppId == "3"),
            "Game Library hides profiles with no unlocked achievements");
        libraryViewModel.SelectedGameFilter = "Measurable progress";
        Check(libraryViewModel.GamesView.Cast<GameCardViewModel>().Count() == 2,
            "Game Library progress filter retains games with measurable progress");
        libraryViewModel.SelectedGameFilter = "Completed";
        Check(libraryViewModel.GamesView.Cast<GameCardViewModel>().Single().AppId == "2",
            "Game Library completed filter selects only fully completed games");
        libraryViewModel.SelectedGameFilter = "All games";
        libraryViewModel.SearchText = "Alpha";
        Check(libraryViewModel.GamesView.Cast<GameCardViewModel>().Single().Title == "Alpha",
            "Game Library search filters by display title");
        libraryViewModel.SearchText = string.Empty;
        libraryViewModel.SelectedGameSort = "Progress";
        Check(libraryViewModel.GamesView.Cast<GameCardViewModel>().First().AppId == "2",
            "Game Library progress sort orders measurable completion before lower progress");
        libraryViewModel.SelectedGameFilter = "Needs information";
        Check(libraryViewModel.GamesView.IsEmpty,
            "Game Library exposes an empty state when no games match the selected filter");
        zeroUnlockCard.ApplyLocalProfileUpdate(new GameProfile("3", "Gamma", [],
            [new AchievementRecord("D", "D", null, true, 1700000400)], true));
        libraryViewModel.SelectedGameFilter = "All games";
        libraryViewModel.GamesView.Refresh();
        Check(libraryViewModel.GamesView.Cast<GameCardViewModel>().Any(game => game.AppId == "3"),
            "Game Library shows a game as soon as a confirmed unlock is detected");
        libraryViewModel.SelectedGameFilter = "All games";
        GameCardViewModel? openedGame = null;
        libraryViewModel.GameRequested += (_, game) => openedGame = game;
        libraryViewModel.OpenGameCommand.Execute(perfectCard);
        Check(ReferenceEquals(openedGame, perfectCard) && libraryViewModel.CurrentPage == "Games",
            "opening a game from the library raises the details request");
    }

    var viewerCacheRequests = 0;
    using (var cachedViewerHttp = new HttpClient(new TestHttpHandler((_, _) =>
    {
        viewerCacheRequests++;
        return Task.FromResult(JsonResponse("""{"appid":"730","name":"Fixture Game","achievements":{"ACH_A":{"name":"Alpha"}}}"""));
    })))
    {
        var catalogCache = new ViewerGameInfoClient(cachedViewerHttp,
            cacheDirectory: Path.Combine(tempRoot, "viewer-cache-persisted"));
        var firstCatalog = await catalogCache.GetCatalogAsync("pozpo", "viewer", "user", "730");
        var secondCatalog = await catalogCache.GetCatalogAsync("pozpo", "viewer", "user", "730");
        Check(firstCatalog.Success && secondCatalog.Success && viewerCacheRequests == 1
            && secondCatalog.Catalog!.Achievements.Single().ApiName == "ACH_A",
            "viewer catalog metadata is cached and reused without repeated downloads");
    }

    var storeMetadata = SteamStoreMetadataParser.Parse("570940", """
        {"570940":{"success":true,"data":{"name":"DARK SOULS: REMASTERED","header_image":"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/570940/header.jpg"}}}
        """);
    Check(storeMetadata?.Name == "DARK SOULS: REMASTERED" && storeMetadata.HeaderImageUri is not null,
        "Steam Store metadata names and trusted artwork URLs parse");
    var untrustedMetadata = SteamStoreMetadataParser.Parse("570940", """
        {"570940":{"success":true,"data":{"name":"DARK SOULS: REMASTERED","header_image":"https://example.invalid/header.jpg"}}}
        """);
    Check(untrustedMetadata?.Name == "DARK SOULS: REMASTERED" && untrustedMetadata.HeaderImageUri is null,
        "untrusted artwork hosts are rejected");

    var first = Path.Combine(tempRoot, "first", "730");
    var second = Path.Combine(tempRoot, "second", "730");
    Directory.CreateDirectory(first);
    Directory.CreateDirectory(second);
    var firstProfile = Path.Combine(first, "achievements.json");
    var secondProfile = Path.Combine(second, "achievements.json");
    File.WriteAllText(firstProfile, "{\"ACH_SHARED\":{\"earned\":true,\"earned_time\":100},\"ACH_OLD\":{\"earned\":false,\"earned_time\":0}}");
    File.WriteAllText(secondProfile, "{\"ACH_SHARED\":{\"earned\":true,\"earned_time\":200},\"ACH_NEW\":{\"earned\":true,\"earned_time\":50}}");
    var firstHash = SHA256.HashData(File.ReadAllBytes(firstProfile));
    var secondHash = SHA256.HashData(File.ReadAllBytes(secondProfile));
    var missing = Path.Combine(tempRoot, "missing-location");
    var scan = await new AchievementScanner().ScanAsync(
    [
        new ScanLocation("first", "First", Path.Combine(tempRoot, "first"), true, true, true),
        new ScanLocation("second", "Second", Path.Combine(tempRoot, "second"), true, true, true),
        new ScanLocation("missing", "Missing", missing, true, false, false)
    ]);

    Check(scan.Games.Count == 1 && scan.Games[0].AppId == "730", "duplicate profiles group by numeric game folder");
    var merged = scan.Games[0];
    Check(merged.Achievements.Count == 3, "duplicate merge retains the union of achievements");
    Check(merged.UnlockedCount == 2 && merged.KnownStateCount == 3 && merged.TotalCount is null
        && merged.CompletionPercent is null,
        "merged profile retains all known states but withholds a total when duplicate sources disagree on catalog completeness");
    Check(merged.Achievements.Single(item => item.ApiName == "ACH_SHARED").UnlockTimeUnix == 200, "merged unlock uses newest valid timestamp");
    Check(merged.SourceFiles.Count == 2, "merged profile keeps both source paths for preview");
    Check(!scan.Locations.Single(item => item.Id == "missing").IsAvailable, "missing location is reported unavailable");
    Check(firstHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(firstProfile)))
        && secondHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(secondProfile))), "scan leaves source files unchanged");

    var corrupt = Path.Combine(tempRoot, "bad", "999", "achievements.json");
    Directory.CreateDirectory(Path.GetDirectoryName(corrupt)!);
    File.WriteAllText(corrupt, "{ broken json");
    var corruptScan = await new AchievementScanner().ScanAsync(
    [new ScanLocation("bad", "Bad", Path.Combine(tempRoot, "bad"), true, true, true)]);
    Check(corruptScan.Games.Count == 0 && corruptScan.Issues.Count > 0, "corrupt profiles are skipped with a diagnostic");

    var formatsRoot = Path.Combine(tempRoot, "supported-formats");
    var gseDirectory = Path.Combine(formatsRoot, "Goldberg", "nested", "730");
    var watcherDirectory = Path.Combine(formatsRoot, "Achievement Watcher", "cache");
    var uplayDirectory = Path.Combine(formatsRoot, "Uplay", "profile");
    var iniDirectory = Path.Combine(formatsRoot, "ini", "733");
    var malformedDirectory = Path.Combine(formatsRoot, "malformed", "734");
    var unidentifiedDirectory = Path.Combine(formatsRoot, "unidentified");
    var olderDuplicateDirectory = Path.Combine(formatsRoot, "duplicate-old", "735");
    var newerDuplicateDirectory = Path.Combine(formatsRoot, "duplicate-new", "735");
    foreach (var directory in new[] { gseDirectory, watcherDirectory, uplayDirectory, iniDirectory, malformedDirectory, unidentifiedDirectory, olderDuplicateDirectory, newerDuplicateDirectory })
        Directory.CreateDirectory(directory);

    var gsePath = Path.Combine(gseDirectory, "achievements.json");
    File.WriteAllText(gsePath, "{\"GSE_EARNED\":{\"earned\":true,\"earned_time\":1700000300},\"GSE_LOCKED\":{\"earned\":false,\"earned_time\":0}}");
    var scannerDbPath = Path.Combine(watcherDirectory, "731.db");
    File.WriteAllText(scannerDbPath, "[{\"apiname\":\"DB_EARNED\",\"achieved\":1,\"unlocktime\":1700000310},{\"apiname\":\"DB_LOCKED\",\"achieved\":0,\"unlocktime\":0}]");
    var uplayMarker = Path.Combine(uplayDirectory, "steam_appid.txt");
    File.WriteAllText(uplayMarker, "732");
    var uplayIniPath = Path.Combine(uplayDirectory, "achiev.ini");
    File.WriteAllText(uplayIniPath, "[UPLAY_EARNED]\nachieved=1\ntimestamp=1700000320\n[UPLAY_LOCKED]\nstate=0\n[42]\nstate=1\nUnlockTime=1700000330\n[UNKNOWN]\nstate=perhaps\n");
    var iniPath2 = Path.Combine(iniDirectory, "achievements.ini");
    File.WriteAllText(iniPath2, "[INI_EARNED]\nunlocked=true\n[INI_LOCKED]\nunlocked=false\n");
    var malformedPath = Path.Combine(malformedDirectory, "achievements.json");
    File.WriteAllText(malformedPath, "{not valid json");
    var unidentifiedPath = Path.Combine(unidentifiedDirectory, "achievements.json");
    File.WriteAllText(unidentifiedPath, "{\"ORPHAN\":{\"earned\":true}}");
    var olderDuplicatePath = Path.Combine(olderDuplicateDirectory, "achievements.json");
    var newerDuplicatePath = Path.Combine(newerDuplicateDirectory, "achievements.json");
    File.WriteAllText(olderDuplicatePath, "{\"DUPLICATE\":{\"earned\":false,\"earned_time\":0}}");
    File.WriteAllText(newerDuplicatePath, "{\"DUPLICATE\":{\"earned\":true,\"earned_time\":1700000340}}");
    File.SetLastWriteTimeUtc(olderDuplicatePath, DateTime.UtcNow.AddMinutes(-20));
    File.SetLastWriteTimeUtc(newerDuplicatePath, DateTime.UtcNow.AddMinutes(-10));
    var newerDuplicateHash = SHA256.HashData(File.ReadAllBytes(newerDuplicatePath));
    var gseHash = SHA256.HashData(File.ReadAllBytes(gsePath));
    var databaseHash = SHA256.HashData(File.ReadAllBytes(scannerDbPath));
    var uplayIniHash = SHA256.HashData(File.ReadAllBytes(uplayIniPath));
    var supportedScan = await new AchievementScanner().ScanAsync(
    [new ScanLocation("fixtures", "Synthetic format fixtures", formatsRoot, true, true, true)]);
    Check(supportedScan.CandidateFiles == 8 && supportedScan.Locations.Single().FilesFound == 8,
        "recursive scanner discovers supported profile files across synthetic nested folders");
    Check(supportedScan.Games.Select(game => game.AppId).OrderBy(id => id).SequenceEqual(["730", "731", "732", "733", "735"]),
        "scanner identifies AppIDs from folders, database filenames, and steam_appid.txt");
    var gseGame = supportedScan.Games.Single(game => game.AppId == "730");
    var dbGame = supportedScan.Games.Single(game => game.AppId == "731");
    var uplayGame = supportedScan.Games.Single(game => game.AppId == "732");
    var iniGame = supportedScan.Games.Single(game => game.AppId == "733");
    Check(gseGame.UnlockedCount == 1 && gseGame.KnownStateCount == 2 && gseGame.TotalCount is null,
        "Goldberg-style JSON scan preserves explicit states without claiming an unverified total");
    Check(dbGame.UnlockedCount == 1 && dbGame.TotalCount == 2 && dbGame.CompletionPercent == 50,
        "Achievement Watcher JSON .db scan reports correct unlocked count and completion");
    Check(uplayGame.UnlockedCount == 2 && uplayGame.TotalCount is null && uplayGame.CompletionPercent is null,
        "Uplay achiev.ini scan retains progress without inventing a complete catalog");
    Check(iniGame.UnlockedCount == 1 && iniGame.TotalCount is null,
        "achievements.ini scan finds its supported alternate filename");
    Check(supportedScan.Issues.Count == 2
        && supportedScan.Issues.Any(issue => issue.Contains("not valid JSON", StringComparison.OrdinalIgnoreCase))
        && supportedScan.Issues.Any(issue => issue.Contains("Could not identify an AppID", StringComparison.OrdinalIgnoreCase)),
        "malformed and unidentifiable profiles are diagnosed without aborting the scan");
    Check(gseHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(gsePath))),
        "synthetic format scan leaves source achievement files unchanged");

    var metadataRoot = Path.Combine(formatsRoot, "metadata");
    var metadataCache = new LocalAchievementMetadataCache(metadataRoot);
    var metadataWrite = await metadataCache.UpdateAsync(supportedScan.SourceFiles);
    var gseCopy = Path.Combine(metadataRoot, "730", "achievements.json");
    var databaseCopy = Path.Combine(metadataRoot, "731", "731.db");
    var uplayConverted = Path.Combine(metadataRoot, "732", "achievements.json");
    var duplicateCopy = Path.Combine(metadataRoot, "735", "achievements.json");
    Check(metadataWrite.UpdatedFiles == 5 && File.Exists(gseCopy) && File.Exists(databaseCopy)
        && File.Exists(uplayConverted) && File.Exists(duplicateCopy)
        && !File.Exists(Path.Combine(metadataRoot, "734", "achievements.json")),
        "native metadata cache copies supported JSON and database sources, converts INI, and skips malformed data");
    Check(gseHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(gseCopy)))
        && databaseHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(databaseCopy)))
        && newerDuplicateHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(duplicateCopy)))
        && databaseHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(scannerDbPath)))
        && uplayIniHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(uplayIniPath))),
        "metadata cache preserves JSON/database bytes and chooses the newest valid duplicate without modifying sources");
    using (var convertedJson = JsonDocument.Parse(File.ReadAllText(uplayConverted)))
    {
        var convertedRoot = convertedJson.RootElement;
        Check(convertedRoot.GetProperty("UPLAY_EARNED").GetProperty("earned").GetBoolean()
            && convertedRoot.GetProperty("UPLAY_EARNED").GetProperty("earned_time").GetInt64() == 1700000320
            && !convertedRoot.GetProperty("UPLAY_LOCKED").GetProperty("earned").GetBoolean()
            && convertedRoot.GetProperty("42").GetProperty("earned").GetBoolean()
            && convertedRoot.GetProperty("42").GetProperty("earned_time").GetInt64() == 1700000330
            && !convertedRoot.TryGetProperty("UNKNOWN", out _),
            "native INI conversion preserves exact states and timestamps and keeps numeric IDs unmapped");
    }
    Check(File.GetLastWriteTimeUtc(uplayConverted) == File.GetLastWriteTimeUtc(uplayIniPath)
        && !Directory.EnumerateFiles(metadataRoot, "*.tmp-*", SearchOption.AllDirectories).Any(),
        "atomic metadata writes preserve source timestamps and clean temporary files");
    var repeatMetadataWrite = await metadataCache.UpdateAsync(supportedScan.SourceFiles);
    Check(repeatMetadataWrite.UpdatedFiles == 0 && repeatMetadataWrite.UnchangedFiles == 5,
        "metadata conversion is incremental and skips unchanged source files");
    var scanWithGeneratedCache = await new AchievementScanner(metadataRoot).ScanAsync(
    [
        new ScanLocation("fixtures", "Synthetic format fixtures", formatsRoot, true, true, true),
        new ScanLocation("nested", "Overlapping subfolder", Path.Combine(formatsRoot, "Goldberg"), true, true, true)
    ]);
    Check(scanWithGeneratedCache.CandidateFiles == supportedScan.CandidateFiles
        && scanWithGeneratedCache.Games.Count == supportedScan.Games.Count,
        "generated metadata and overlapping source paths are excluded from duplicate scanning");

    var steamMetadataDirectory = Path.Combine(metadataRoot, "1501750");
    Directory.CreateDirectory(steamMetadataDirectory);
    File.WriteAllText(Path.Combine(steamMetadataDirectory, "achievements.json"),
        """{"STEAM_OLD":{"earned":false,"earned_time":0},"STEAM_NEW":{"earned":true,"earned_time":1700000400}}""");
    var metadataLibrary = new LocalMetadataGameLibrary(metadataRoot);
    var restoredProfiles = metadataLibrary.MergeProfiles([]);
    var restoredSteamGame = restoredProfiles.SingleOrDefault(game => game.AppId == "1501750");
    Check(restoredSteamGame is not null
        && restoredSteamGame.Achievements.Single(item => item.ApiName == "STEAM_NEW").IsUnlocked == true,
        "Steam appcache metadata AppIDs absent from normal source folders are restored into the Game Library");
    var priorSteamProfile = new GameProfile("1501750", "Fixture Steam game", ["fixture://steam-profile"],
        [new AchievementRecord("STEAM_OLD", "Old achievement", null, true, 1700000300)], false);
    var mergedSteamGame = metadataLibrary.MergeProfiles([priorSteamProfile]).Single(game => game.AppId == "1501750");
    Check(mergedSteamGame.Achievements.Single(item => item.ApiName == "STEAM_OLD").IsUnlocked == true
        && mergedSteamGame.Achievements.Single(item => item.ApiName == "STEAM_OLD").UnlockTimeUnix == 1700000300
        && mergedSteamGame.Achievements.Single(item => item.ApiName == "STEAM_NEW").IsUnlocked == true
        && mergedSteamGame.SourceFiles.SequenceEqual(priorSteamProfile.SourceFiles),
        "metadata library merge keeps existing unlocks and source associations while adding cached AppID progress");

    var catalogSettings = new AppSettings();
    catalogSettings.EnabledLocations["gse"] = false;
    catalogSettings.CustomLocations.Add(new CustomLocationPreference
    {
        Id = "fixture",
        Name = "Fixture folder",
        Path = formatsRoot,
        Enabled = true
    });
    var catalogLocations = LocationCatalog.Build(catalogSettings);
    var requiredLocationIds = new[]
    {
        "steam-codex-public", "steam-rune-public", "onlinefix-public", "empress-public",
        "goldberg-steam", "goldberg-uplay", "r1-uplay", "gse", "empress", "nemirtingas-epic",
        "anadius-watcher", "steam-codex-appdata", "smartsteamemu", "skidrow", "universelan",
        "steam-public-root", "empress-public-user-root"
    };
    Check(requiredLocationIds.All(id => catalogLocations.Any(location => location.Id == id))
        && !catalogLocations.Single(location => location.Id == "gse").IsEnabled
        && catalogLocations.Single(location => location.Id == "custom-fixture").IsCustom
        && catalogLocations.Select(location => Path.GetFullPath(location.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == catalogLocations.Count,
        "automatic location catalog includes requested roots, honors toggles, deduplicates roots, and retains custom folders");

    var playtimeStatePath = Path.Combine(tempRoot, "playtime", "playtime.json");
    var gameExecutableRoot = Path.Combine(tempRoot, "installed-games");
    Directory.CreateDirectory(Path.Combine(gameExecutableRoot, "730"));
    Directory.CreateDirectory(Path.Combine(gameExecutableRoot, "740"));
    var gameExecutable = Path.Combine(gameExecutableRoot, "730", "game.exe");
    var secondGameProcess = Path.Combine(gameExecutableRoot, "730", "game-helper.exe");
    var otherGameExecutable = Path.Combine(gameExecutableRoot, "740", "other-game.exe");
    File.WriteAllBytes(gameExecutable, [0]);
    File.WriteAllBytes(secondGameProcess, [0]);
    File.WriteAllBytes(otherGameExecutable, [0]);
    var playtime = new GameplaySessionTracker(playtimeStatePath);
    playtime.SetLibraryGames(["730"]);
    Check(playtime.GetSnapshots(DateTimeOffset.UnixEpoch).Single().AppId == "730",
        "playtime snapshots include discovered library games without existing tracking data");
    Check(playtime.AddExecutable("730", gameExecutable)
        && playtime.AddExecutable("730", secondGameProcess)
        && !playtime.AddExecutable("740", gameExecutable),
        "a library game can link multiple executables while one executable cannot be assigned to another game");
    Check(!playtime.ObserveProcessStarted(new RunningProcessObservation(90, otherGameExecutable, DateTimeOffset.UnixEpoch), DateTimeOffset.UnixEpoch)
        && !playtime.ObserveProcessStarted(new RunningProcessObservation(91, Path.Combine(gameExecutableRoot, "730", "unlinked.exe"), DateTimeOffset.UnixEpoch), DateTimeOffset.UnixEpoch),
        "non-library and unassociated processes are ignored");

    var sessionStart = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
    Check(playtime.ObserveProcessStarted(new RunningProcessObservation(100, gameExecutable, sessionStart), sessionStart)
        && playtime.ObserveProcessStarted(new RunningProcessObservation(101, secondGameProcess, sessionStart.AddSeconds(5)), sessionStart.AddSeconds(5))
        && !playtime.ObserveProcessStarted(new RunningProcessObservation(100, gameExecutable, sessionStart), sessionStart.AddSeconds(6)),
        "associated process start events create one game session and duplicate events are ignored");
    Check(playtime.ObserveProcessStopped(100, sessionStart.AddSeconds(10))
        && playtime.GetSnapshots(sessionStart.AddSeconds(15)).Single().ActiveProcessCount == 1
        && playtime.ObserveProcessStopped(101, sessionStart.AddSeconds(20)),
        "multiple processes keep one session active until the last associated process stops");
    var overlapSnapshot = playtime.GetSnapshots(sessionStart.AddSeconds(20)).Single();
    Check(overlapSnapshot.TotalPlaytime == TimeSpan.FromSeconds(20)
        && overlapSnapshot.LastPlayedUtc == sessionStart.AddSeconds(20)
        && !overlapSnapshot.IsRunning,
        "overlapping game processes are counted once and the stopped session records its last-played time");

    playtime.SetLibraryGames(["730", "740"]);
    Check(playtime.AddExecutable("740", otherGameExecutable)
        && playtime.ObserveProcessStarted(new RunningProcessObservation(200, gameExecutable, sessionStart.AddMinutes(1)), sessionStart.AddMinutes(1))
        && playtime.ObserveProcessStarted(new RunningProcessObservation(201, otherGameExecutable, sessionStart.AddMinutes(1).AddSeconds(5)), sessionStart.AddMinutes(1).AddSeconds(5)),
        "two different discovered games can be tracked at the same time");
    Check(playtime.ObserveProcessStopped(200, sessionStart.AddMinutes(1).AddSeconds(15))
        && playtime.ObserveProcessStopped(201, sessionStart.AddMinutes(1).AddSeconds(20))
        && playtime.GetSnapshots(sessionStart.AddMinutes(1).AddSeconds(20)).ToDictionary(item => item.AppId)["730"].TotalPlaytime == TimeSpan.FromSeconds(35)
        && playtime.GetSnapshots(sessionStart.AddMinutes(1).AddSeconds(20)).ToDictionary(item => item.AppId)["740"].TotalPlaytime == TimeSpan.FromSeconds(15),
        "simultaneous games retain separate playtime totals");

    var recoveryStatePath = Path.Combine(tempRoot, "recovery", "playtime.json");
    var recoveryTracker = new GameplaySessionTracker(recoveryStatePath);
    recoveryTracker.SetLibraryGames(["480"]);
    var recoveryExecutable = Path.Combine(gameExecutableRoot, "730", "recovery.exe");
    recoveryTracker.AddExecutable("480", recoveryExecutable);
    var recoveryProcess = new RunningProcessObservation(301, recoveryExecutable, sessionStart);
    recoveryTracker.ObserveProcessStarted(recoveryProcess, sessionStart);
    recoveryTracker.Checkpoint(sessionStart.AddSeconds(60));
    var resumedTracker = new GameplaySessionTracker(recoveryStatePath);
    resumedTracker.SetLibraryGames(["480"]);
    resumedTracker.RecoverAfterRestart([recoveryProcess], sessionStart.AddSeconds(120));
    Check(resumedTracker.GetSnapshots(sessionStart.AddSeconds(120)).Single().TotalPlaytime == TimeSpan.FromSeconds(120)
        && resumedTracker.GetSnapshots(sessionStart.AddSeconds(120)).Single().CurrentSessionStartedAtUtc == sessionStart,
        "an active game process resumes its saved session after an application restart");
    resumedTracker.ObserveProcessStopped(301, sessionStart.AddSeconds(150));
    Check(resumedTracker.GetSnapshots(sessionStart.AddSeconds(150)).Single().TotalPlaytime == TimeSpan.FromSeconds(150),
        "a recovered gameplay session closes without double-counting its saved checkpoint");

    var interruptedPath = Path.Combine(tempRoot, "interrupted", "playtime.json");
    var interruptedTracker = new GameplaySessionTracker(interruptedPath);
    interruptedTracker.SetLibraryGames(["480"]);
    interruptedTracker.AddExecutable("480", recoveryExecutable);
    interruptedTracker.ObserveProcessStarted(recoveryProcess, sessionStart);
    interruptedTracker.Checkpoint(sessionStart.AddSeconds(60));
    var interruptedRecovery = new GameplaySessionTracker(interruptedPath);
    interruptedRecovery.SetLibraryGames(["480"]);
    interruptedRecovery.RecoverAfterRestart([], sessionStart.AddHours(2));
    var interruptedSnapshot = interruptedRecovery.GetSnapshots(sessionStart.AddHours(2)).Single();
    Check(interruptedSnapshot.TotalPlaytime == TimeSpan.FromSeconds(60)
        && interruptedSnapshot.LastPlayedUtc == sessionStart.AddSeconds(60)
        && !interruptedSnapshot.IsRunning,
        "a game that stopped while AchievementSync was down ends at its last durable checkpoint");
    Check(PlaytimeFormatter.Format(TimeSpan.FromMinutes(35)) == "35m"
        && PlaytimeFormatter.Format(TimeSpan.FromHours(12) + TimeSpan.FromMinutes(35)) == "12h 35m"
        && PlaytimeFormatter.Format(TimeSpan.FromDays(2) + TimeSpan.FromHours(3)) == "2d 3h",
        "playtime uses compact minute, hour, and long-duration display formats");
    var protectedProcessPath = ProcessObservationIdentity.UnverifiablePath("PBZ");
    Check(protectedProcessPath.StartsWith("UnverifiableProcessName:", StringComparison.Ordinal)
        && ProcessObservationIdentity.TryReadUnverifiableName(protectedProcessPath, out var protectedName)
        && protectedName == "PBZ",
        "a protected process carries a stable identity token round-tripping its process name");
    var protectedTracker = new GameplaySessionTracker(Path.Combine(tempRoot, "protected-playtime", "playtime.json"));
    protectedTracker.SetLibraryGames(["990"]);
    protectedTracker.AddExecutable("990", Path.Combine(tempRoot, "linked", "PBZ.exe"));
    protectedTracker.ObserveProcessStarted(new RunningProcessObservation(901, protectedProcessPath, sessionStart), sessionStart);
    protectedTracker.ObserveProcessStarted(new RunningProcessObservation(902, protectedProcessPath, sessionStart.AddSeconds(1)), sessionStart.AddSeconds(1));
    var protectedSnapshot = protectedTracker.GetSnapshots(sessionStart.AddSeconds(1)).Single();
    Check(protectedSnapshot.IsRunning && protectedSnapshot.ActiveProcessCount == 2,
        "a protected game whose path cannot be read is matched by process name against the linked executable");
    protectedTracker.ObserveProcessStopped(901, sessionStart.AddMinutes(1));
    protectedTracker.ObserveProcessStopped(902, sessionStart.AddMinutes(2));
    var protectedFinal = protectedTracker.GetSnapshots(sessionStart.AddMinutes(2)).Single();
    Check(!protectedFinal.IsRunning && protectedFinal.TotalPlaytime >= TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(2),
        "the protected-process session is unioned and closes and saves when all processes stop");
    Check(!protectedTracker.ObserveProcessStarted(new RunningProcessObservation(903, ProcessObservationIdentity.UnverifiablePath("OtherApp"), sessionStart), sessionStart),
        "a protected process with no name match to any linked game is ignored");

    var pauseStart = sessionStart.AddHours(1);
    playtime.ObserveProcessStarted(new RunningProcessObservation(401, gameExecutable, pauseStart), pauseStart);
    playtime.StopAllActiveSessions(pauseStart.AddSeconds(30));
    var pauseSnapshot = playtime.GetSnapshots(pauseStart.AddMinutes(1)).Single(item => item.AppId == "730");
    Check(pauseSnapshot.TotalPlaytime == TimeSpan.FromSeconds(65)
        && !pauseSnapshot.IsRunning,
        "pausing monitoring closes active gameplay sessions at the pause time");

    var notificationStatePath = Path.Combine(tempRoot, "notification-state", "unlock-state.json");
    var unlockTracker = new AchievementUnlockTracker(notificationStatePath);
    var baselineProfile = new GameProfile("7654321", "Fixture game", [],
    [
        new AchievementRecord("LOCKED", "Previously locked", null, false, 0),
        new AchievementRecord("ALREADY", "Previously earned", null, true, 1700000000),
        new AchievementRecord("UNKNOWN", "Unverified state", null, null, null)
    ], HasCompleteCatalog: false);
    Check(unlockTracker.Observe(baselineProfile, "Fixture game").Count == 0,
        "notification first-run baseline never reports already-unlocked achievements");
    var newlyUnlockedProfile = baselineProfile with
    {
        Achievements =
        [
            new AchievementRecord("LOCKED", "Previously locked", null, true, 1800000000, "https://example.invalid/icon.png"),
            new AchievementRecord("ALREADY", "Previously earned", null, true, 1700000000),
            new AchievementRecord("UNKNOWN", "Unverified state", null, true, 1800000000),
            new AchievementRecord("ADDED", "Newly discovered", null, true, 1800000000)
        ]
    };
    var unlockEvents = unlockTracker.Observe(newlyUnlockedProfile, "Fixture game");
    Check(unlockEvents.Count == 1 && unlockEvents[0].AchievementId == "LOCKED"
        && unlockEvents[0].AchievementName == "Previously locked"
        && unlockEvents[0].GameTitle == "Fixture game"
        && unlockEvents[0].UnlockTimeUnix == 1800000000,
        "only a confirmed known locked-to-unlocked transition creates a named notification");
    Check(unlockTracker.Observe(newlyUnlockedProfile, "Fixture game").Count == 0,
        "duplicate observations never create a second notification");
    var restartedUnlockTracker = new AchievementUnlockTracker(notificationStatePath);
    Check(restartedUnlockTracker.Observe(newlyUnlockedProfile, "Fixture game").Count == 0,
        "a previously reported unlock remains deduplicated after an application restart");
    var descriptionStatePath = Path.Combine(tempRoot, "description-unlock-state", "unlock-state.json");
    var descriptionTracker = new AchievementUnlockTracker(descriptionStatePath);
    var descriptionBaseline = new GameProfile("7651251", "Description Game", [],
    [
        new AchievementRecord("DESCRIPTION_CHECK", "Hidden title", "Hidden description text", false, 0)
    ], HasCompleteCatalog: true);
    descriptionTracker.Observe(descriptionBaseline, "Description Game");
    var descriptionUnlocked = descriptionTracker.Observe(
        descriptionBaseline with
        {
            Achievements =
            [
                new AchievementRecord("DESCRIPTION_CHECK", "Hidden title", "Hidden description text", true, 1800000000)
            ]
        }, "Description Game").Single();
    Check(string.Equals(descriptionUnlocked.Description, "Hidden description text", StringComparison.Ordinal),
        "a newly unlocked achievement carries its local description into the notification");
    var unknownStatePath = Path.Combine(tempRoot, "unknown-notifications", "unlock-state.json");
    var unknownTracker = new AchievementUnlockTracker(unknownStatePath);
    unknownTracker.Observe(new GameProfile("7654322", null, [], [new AchievementRecord("UNKNOWN", null, null, null, null)], false));
    Check(unknownTracker.Observe(new GameProfile("7654322", null, [], [new AchievementRecord("UNKNOWN", null, null, true, null)], false)).Count == 0,
        "an unknown-to-unlocked state change does not produce a false positive");
    Check(new AppSettings().AchievementNotificationsEnabled
        && new AppSettings().PlaytimeNotificationsEnabled
        && !new AppSettings().StartWithWindows,
        "new installs enable unlock and playtime notifications while Windows startup remains opt-in");

    var enrichTracker = new AchievementUnlockTracker(Path.Combine(tempRoot, "enrich-test", "unlock-state.json"));
    enrichTracker.Observe(new GameProfile("4115450", "Phantom Blade Zero", [], [new AchievementRecord("ACH_046", "Collector's Notes", "Find all secret journals", false, null, "https://example.com/icon.png", false, "bronze")], false));
    var rawLocalAch = new AchievementRecord("ACH_046", null, null, true, 1732308000);
    var catalogDef = new AchievementRecord("ACH_046", "Collector's Notes", "Find all secret journals", null, null, "https://example.com/icon.png", false, "bronze");
    var enrichedAch = rawLocalAch with
    {
        DisplayName = catalogDef.DisplayName,
        Description = catalogDef.Description,
        IconUri = catalogDef.IconUri,
        TrophyType = catalogDef.TrophyType
    };
    var enrichUnlocks = enrichTracker.Observe(new GameProfile("4115450", "Phantom Blade Zero", [], [enrichedAch], false));
    Check(enrichUnlocks.Count == 1
        && enrichUnlocks[0].AchievementId == "ACH_046"
        && enrichUnlocks[0].AchievementName == "Collector's Notes"
        && enrichUnlocks[0].Description == "Find all secret journals"
        && enrichUnlocks[0].IconUri == "https://example.com/icon.png"
        && enrichUnlocks[0].TrophyType == "bronze",
        "raw progress without display names is enriched by catalog metadata so toasts show Collector's Notes instead of ACH_046");
    Check(new AppSettings().NotificationPosition == "TopCenter"
        && new AppSettings().NotificationOffsetX == 0
        && new AppSettings().NotificationOffsetY == 0
        && new AppSettings().NotificationSoundEnabled
        && new AppSettings().NotificationSoundVolume == 70
        && !new AppSettings().AutoSyncAfterUnlock,
        "notification defaults place the toast top-center, play a moderate chime, and keep GitHub auto-sync after unlock disabled");

    var watcherRoot = Path.Combine(tempRoot, "watcher", "7654323");
    Directory.CreateDirectory(watcherRoot);
    var watchedAchievementPath = Path.Combine(watcherRoot, "achievements.json");
    File.WriteAllText(watchedAchievementPath, "{\"WATCHED\":{\"earned\":false}}", new System.Text.UTF8Encoding(false));
    var watcherScan = await new AchievementScanner().ScanAsync(
        [new ScanLocation("fixture", "Synthetic watcher fixture", Path.GetDirectoryName(watcherRoot)!, true, true, true)]);
    var watcherSource = watcherScan.SourceFiles.Single();
    var watcherScanner = new AchievementScanner();
    var watchedUnlockState = Path.Combine(tempRoot, "watcher-notification-state", "unlock-state.json");
    var watchedUnlockTracker = new AchievementUnlockTracker(watchedUnlockState);
    watchedUnlockTracker.Observe(watcherScan.Games.Single());
    var changedSourceSignal = new TaskCompletionSource<IReadOnlyList<DiscoveredAchievementFile>>(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var watcher = new LocalAchievementFileMonitor())
    {
        watcher.SourcesChanged += sources => changedSourceSignal.TrySetResult(sources);
        watcher.Configure([watcherSource]);
        var replacement = watchedAchievementPath + ".new";
        File.WriteAllText(replacement, "{\"WATCHED\":{\"earned\":true}}", new System.Text.UTF8Encoding(false));
        File.Move(replacement, watchedAchievementPath, overwrite: true);
        var changedFiles = await changedSourceSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var changedProfiles = await watcherScanner.ParseKnownSourcesAsync(watcherScan.SourceFiles);
        var watchedUnlocks = watchedUnlockTracker.Observe(changedProfiles.Single());
        Check(changedFiles.Count == 1 && string.Equals(changedFiles[0].Path, watchedAchievementPath, StringComparison.OrdinalIgnoreCase)
            && watchedUnlocks.Count == 1 && watchedUnlocks[0].AchievementId == "WATCHED",
            "an atomically replaced discovered profile produces one parsed, confirmed locked-to-unlocked notification event");
        watcher.SetPaused(true);
        var pausedSignal = new TaskCompletionSource<IReadOnlyList<DiscoveredAchievementFile>>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.SourcesChanged += sources => pausedSignal.TrySetResult(sources);
        File.WriteAllText(watchedAchievementPath, "{\"WATCHED\":{\"earned\":false}}", new System.Text.UTF8Encoding(false));
        await Task.Delay(1000);
        Check(!pausedSignal.Task.IsCompleted, "paused local achievement monitoring does not dispatch file changes");
        watcher.SetPaused(false);
        await pausedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(true, "resuming local monitoring reconciles already-discovered profile files");
        var overflowSignal = new TaskCompletionSource<IReadOnlyList<DiscoveredAchievementFile>>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.SourcesChanged += sources => overflowSignal.TrySetResult(sources);
        watcher.ReconcileKnownFiles();
        var reconciledFiles = await overflowSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var reconciledProfiles = await watcherScanner.ParseKnownSourcesAsync(watcherScan.SourceFiles);
        Check(reconciledFiles.Count == 1 && watchedUnlockTracker.Observe(reconciledProfiles.Single()).Count == 0,
            "watcher overflow recovery reconciles known files without repeating a saved unlock notification");
        var restartedWatchedUnlockTracker = new AchievementUnlockTracker(watchedUnlockState);
        Check(restartedWatchedUnlockTracker.Observe(reconciledProfiles.Single()).Count == 0,
            "a file event observed by the live watcher stays deduplicated after restart");
    }

    var monitorType = typeof(MainViewModel).Assembly.GetType("AchievementSync.Desktop.Services.WindowsGameProcessMonitor", throwOnError: true)!;
    var queryBuilder = monitorType.GetMethod("BuildTraceQuery", BindingFlags.Static | BindingFlags.NonPublic)!;
    var filteredStartQuery = (string)queryBuilder.Invoke(null, ["Win32_ProcessStartTrace", new[] { "game", "helper" }])!;
    var filteredStopQuery = (string)queryBuilder.Invoke(null, ["Win32_ProcessStopTrace", new[] { "game" }])!;
    Check(filteredStartQuery.Contains("ProcessName = \"game.exe\"", StringComparison.Ordinal)
        && filteredStartQuery.Contains("ProcessName = \"helper.exe\"", StringComparison.Ordinal)
        && !filteredStartQuery.Contains("Win32_Process WHERE", StringComparison.Ordinal)
        && filteredStopQuery.Contains("Win32_ProcessStopTrace", StringComparison.Ordinal),
        "WMI monitoring subscribes to filtered start/stop events for associated executable names only");
    // Notification placement is UI-free geometry so positions can be checked exactly.
    Check(NotificationPlacement.Presets.Count == 7 && NotificationPlacement.Presets[0].Code == "TopCenter"
        && NotificationPlacement.Presets.All(item => !string.IsNullOrWhiteSpace(item.Label)),
        "notification placement exposes the seven supported presets with TopCenter first");
    var topCenter = NotificationPlacement.Compute("TopCenter", 460, 140, 0, 0, 0, 0, 1920, 1080);
    Check(topCenter.Left == (1920 - 460) / 2 && topCenter.Top == 14,
        "top-center preset centers the toast horizontally at the top of the work area");
    Check(NotificationPlacement.Compute("center", 460, 140, 0, 0, 0, 0, 1920, 1080).Top == (1080 - 140) / 2
        && NotificationPlacement.Compute("bottomright", 460, 140, 0, 0, 0, 0, 1920, 1080) is { Left: 1920 - 460 - 18, Top: 1080 - 140 - 18 },
        "center and bottom-right presets anchor to the work area with the defined margins");
    var clampedPlacement = NotificationPlacement.Compute("BottomLeft", 460, 140, 5000, -5000, 0, 0, 1920, 1080);
    Check(clampedPlacement is { Left: 1920 - 460 - 8, Top: 8 },
        "extreme offsets clamp the toast back inside the work area");
    Check(!NotificationPlacement.IsKnownPreset("Sideways") && NotificationPlacement.IsKnownPreset("bottomleft"),
        "unknown notification presets are rejected and preset matching ignores casing");

    // A local Goldberg catalog with relative icon paths must resolve them to file URIs.
    var localIconRoot = Path.Combine(tempRoot, "local-icon-catalog", "730", "steam_settings");
    Directory.CreateDirectory(Path.Combine(localIconRoot, "img"));
    await File.WriteAllBytesAsync(Path.Combine(localIconRoot, "img", "unlock.jpg"), pngBytes);
    var localIconCatalog = Path.Combine(localIconRoot, "achievements.json");
    await File.WriteAllTextAsync(localIconCatalog,
        """[{"name":"ACH_LOCAL_ICON","hidden":0,"displayName":{"english":"Local Icon"},"description":{"english":"Icon resolves locally"},"icon":"img/unlock.jpg"}]""");
    var localIconProfile = new JsonAchievementParser().Parse(localIconCatalog, "730");
    Check(localIconProfile.Success && localIconProfile.Profile!.Achievements.Single().IconUri is { } resolvedIcon
        && resolvedIcon.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
        && File.Exists(new Uri(resolvedIcon).LocalPath),
        "a local Goldberg catalog resolves catalog-relative icon paths to local file URIs");
    await File.WriteAllTextAsync(localIconCatalog,
        """[{"name":"ACH_MISSING_ICON","hidden":0,"displayName":{"english":"Missing Icon"},"description":{"english":"No icon file exists"},"icon":"img/does-not-exist.jpg"}]""");
    var missingIconProfile = new JsonAchievementParser().Parse(localIconCatalog, "730");
    Check(missingIconProfile.Success && missingIconProfile.Profile!.Achievements.Single().IconUri is null,
        "a local Goldberg catalog drops a catalog-relative icon path when the icon file is absent");

    // The shared artwork cache decodes a local icon file through the common resolution path.
    var cachedIcon = Path.Combine(tempRoot, "artwork-cache-test", "icon.png");
    Directory.CreateDirectory(Path.GetDirectoryName(cachedIcon)!);
    await File.WriteAllBytesAsync(cachedIcon, pngBytes);
    var artworkCacheType = typeof(AchievementSync.Desktop.AchievementToastWindow).Assembly
        .GetType("AchievementSync.Desktop.Services.AchievementArtworkCache", throwOnError: true)!;
    var artworkCacheMethod = artworkCacheType.GetMethod("GetAsync", BindingFlags.Public | BindingFlags.Static)!;
    var artworkCacheResult = await (Task<System.Windows.Media.Imaging.BitmapImage?>)artworkCacheMethod.Invoke(null, [new Uri(cachedIcon).ToString()])!;
    Check(artworkCacheResult is not null && artworkCacheResult.PixelWidth > 0,
        "the shared achievement-icon cache decodes a local file URI into a usable image");

    // The metadata planner is the only source of files uploaded to GitHub; it must never include covers or other files.
    var plannerRoot = Path.Combine(tempRoot, "metadata-planner");
    Directory.CreateDirectory(Path.Combine(plannerRoot, "730"));
    Directory.CreateDirectory(Path.Combine(plannerRoot, "733"));
    Directory.CreateDirectory(Path.Combine(plannerRoot, "734"));
    Directory.CreateDirectory(Path.Combine(plannerRoot, "ViewerCatalog"));
    Directory.CreateDirectory(Path.Combine(plannerRoot, "noachievements"));
    await File.WriteAllBytesAsync(Path.Combine(plannerRoot, "730", "achievements.json"),
        System.Text.Encoding.UTF8.GetBytes("""{"ACH_A":{"earned":true,"earned_time":1700000000},"ACH_B":{"earned":false,"earned_time":0}}"""));
    File.WriteAllBytes(Path.Combine(plannerRoot, "730", "730.cover"), [0xFF, 0xD8, 0xFF]);
    File.WriteAllBytes(Path.Combine(plannerRoot, "730", "steam.json"), "{}"u8.ToArray());
    File.WriteAllBytes(Path.Combine(plannerRoot, "733", "steam.json"), "{}"u8.ToArray());
    await File.WriteAllTextAsync(Path.Combine(plannerRoot, "733", "achievements.json"),
        """{"ACH_LOCKED":{"earned":false,"earned_time":0}}""");
    await File.WriteAllTextAsync(Path.Combine(plannerRoot, "734", "achievements.json"),
        """{"ACH_NUMERIC":{"earned":1,"earned_time":1700000001}}""");
    var metadataPlan = MetadataAchievementExportPlanner.Build(plannerRoot);
    Check(metadataPlan.Files.Count == 2 && metadataPlan.Failures.Count == 0
        && metadataPlan.Files[0].AppId == "730"
        && metadataPlan.Files[0].RelativePath == "AppID/730/achievements.json"
        && metadataPlan.Files[0].KnownStates == 2
        && metadataPlan.Files[1].AppId == "734",
        "the metadata planner exports only AppID-scoped achievements.json files for games with an unlock, never covers or steam.json");
    await File.WriteAllTextAsync(Path.Combine(plannerRoot, "730", "achievements.json"), "{not json");
    var badMetadataPlan = MetadataAchievementExportPlanner.Build(plannerRoot);
    Check(badMetadataPlan.Files.All(file => file.AppId != "730")
        && badMetadataPlan.Failures.Count == 1 && badMetadataPlan.Failures[0].AppId == "730",
        "an unreadable metadata achievements.json is reported as a per-AppID failure");
    Check(MetadataAchievementExportPlanner.Build(Path.Combine(tempRoot, "metadata-missing")).Files.Count == 0,
        "the metadata planner returns an empty list when the metadata directory does not exist");
    Check(AchievementExportPlanner.Build([
            new GameProfile("735", "No unlocks", [], [new AchievementRecord("LOCKED", "Locked", null, false, 0)], true)
        ]).Count == 0,
        "the profile export planner excludes games with no unlocked achievements");

    // Bulk sync: one commit, changed JSON plus empty viewer markers, per-AppID report. Covers and other files are never uploaded.
    var bulkRemoteHead = "commit-old";
    var bulkCalls = 0;
    var bulkBlobContents = new List<string>();
    var bulkTreePaths = new List<string>();
    const string bulkRemoteAchievement = "{\"ACH_KEEP\":{\"earned\":true,\"earned_time\":1700000000}}";
    using (var githubBulkHttp = new HttpClient(new TestHttpHandler((request, _) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && path == "/repos/pozpo/viewer")
            return Task.FromResult(JsonResponse("""{"full_name":"pozpo/viewer","default_branch":"user","private":false,"permissions":{"push":true}}"""));
        if (request.Method == HttpMethod.Get && path.EndsWith("/git/ref/heads/user", StringComparison.Ordinal))
            return Task.FromResult(JsonResponse($"{{\"object\":{{\"sha\":\"{bulkRemoteHead}\"}}}}"));
        if (request.Method == HttpMethod.Get && path.EndsWith("/git/commits/commit-old", StringComparison.Ordinal))
            return Task.FromResult(JsonResponse("""{"tree":{"sha":"tree-old"}}"""));
        if (request.Method == HttpMethod.Get && (path.EndsWith("/contents/AppID/730/achievements.json", StringComparison.Ordinal) || path.EndsWith("/contents/AppID/570/achievements.json", StringComparison.Ordinal)))
            return Task.FromResult(JsonResponse("{\"type\":\"file\",\"encoding\":\"base64\",\"content\":\"" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(bulkRemoteAchievement)) + "\"}"));
        if (request.Method == HttpMethod.Get && path.EndsWith("/contents/AppID/730/GOG.platform", StringComparison.Ordinal))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        if (request.Method == HttpMethod.Post && path.EndsWith("/git/blobs", StringComparison.Ordinal))
        {
            using var blobBody = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            bulkBlobContents.Add(blobBody.RootElement.GetProperty("content").GetString() ?? string.Empty);
            bulkCalls++;
            return Task.FromResult(JsonResponse("""{"sha":"blob-new"}"""));
        }
        if (request.Method == HttpMethod.Post && path.EndsWith("/git/trees", StringComparison.Ordinal))
        {
            using var treeBody = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            bulkTreePaths.AddRange(treeBody.RootElement.GetProperty("tree").EnumerateArray()
                .Select(entry => entry.GetProperty("path").GetString() ?? string.Empty));
            bulkCalls++;
            return Task.FromResult(JsonResponse("""{"sha":"tree-new"}"""));
        }
        if (request.Method == HttpMethod.Post && path.EndsWith("/git/commits", StringComparison.Ordinal))
        { bulkCalls++; return Task.FromResult(JsonResponse("""{"sha":"commit-new"}""")); }
        if (request.Method.Method == "PATCH" && path.EndsWith("/git/refs/heads/user", StringComparison.Ordinal))
        { bulkCalls++; bulkRemoteHead = "commit-new"; return Task.FromResult(JsonResponse("""{"object":{"sha":"commit-new"}}""")); }
        throw new InvalidOperationException($"Unexpected synthetic GitHub request: {request.Method} {path}");
    })))
    {
        var bulkClient = new GitHubAchievementSyncClient(githubBulkHttp);
        var bulkTarget = new GitHubSyncTarget("pozpo", "viewer", "user");
        var changedFile = new AchievementExportFile("AppID/730/achievements.json", "730", 2,
            """{"ACH_KEEP":{"earned":true,"earned_time":1700000000},"ACH_NEW":{"earned":true,"earned_time":1700000100}}""");
        var unchangedFile = new AchievementExportFile("AppID/570/achievements.json", "570", 1, bulkRemoteAchievement);
        var markerFile = new AchievementExportFile("AppID/730/GOG.platform", "730", 0, string.Empty);
        var bulkResult = await bulkClient.PushBulkAsync("synthetic-token", bulkTarget, [changedFile, unchangedFile, markerFile]);
        Check(bulkResult.Pushed && bulkResult.AchievementFilesPushed == 2 && bulkResult.AchievementFilesUnchanged == 1
            && bulkResult.Entries.Count == 3,
            "bulk sync pushes changed achievement JSON and empty platform markers in one commit, reporting unchanged files");
        Check(bulkResult.CommitUrl is not null && bulkCalls == 5 && bulkResult.Entries.Count(entry => entry.Action == "pushed") == 2,
            "bulk sync returns a commit URL and per-AppID write results");
        Check(bulkBlobContents.Contains(string.Empty, StringComparer.Ordinal)
            && bulkTreePaths.Contains("AppID/730/GOG.platform", StringComparer.Ordinal)
            && bulkBlobContents.Any(content => content.Contains("ACH_NEW", StringComparison.Ordinal)),
            "bulk sync creates a zero-byte platform marker blob and includes its path in the GitHub tree");
        var markerPreviewViewModel = new ExportPreviewFileViewModel(markerFile, newUnlocks: 0);
        Check(markerPreviewViewModel.IsEmptyMarker
            && markerPreviewViewModel.Summary.Contains("uploaded as 0 bytes", StringComparison.Ordinal)
            && markerPreviewViewModel.ContentHint.Contains("zero-byte file", StringComparison.Ordinal),
            "GitHub preview explains that platform marker files intentionally have empty content");
    }

    // INI roundtrip: viewer JSON can be converted back to INI and parsed again without losing states.
    var iniJson = """{"SQUAD_HERO":{"earned":true,"earned_time":1700000100},"CLEAN_SWEEP":{"earned":false,"earned_time":0}}""";
    var iniBytes = IniAchievementWriter.FromViewerJson(iniJson);
    var iniText = System.Text.Encoding.UTF8.GetString(iniBytes);
    Check(iniText.Contains("[SQUAD_HERO]") && iniText.Contains("unlocked=true") && iniText.Contains("earned_time=1700000100")
        && iniText.Contains("unlocked=false"),
        "INI writer emits unlocked flags and earned_time per section");
    var roundtripRoot = Path.Combine(tempRoot, "roundtrip-ini");
    Directory.CreateDirectory(roundtripRoot);
    var roundtripPath = Path.Combine(roundtripRoot, "achievements.ini");
    File.WriteAllBytes(roundtripPath, iniBytes);
    var roundtrip = new IniAchievementParser().Parse(roundtripPath, "99000");
    Check(roundtrip.Profile?.Achievements.Count == 2
        && roundtrip.Profile!.Achievements.Any(a => a.ApiName == "SQUAD_HERO" && a.IsUnlocked == true && a.UnlockTimeUnix == 1700000100)
        && roundtrip.Profile.Achievements.Any(a => a.ApiName == "CLEAN_SWEEP" && a.IsUnlocked == false),
        "round-tripped INI is parsed back into the same states");

    // Manifest serialize / deserialize roundtrips.
    var manifest = RestorationManifest.Create("99000", "json", @"%USERPROFILE%\AppData\Local\AchievementSync\metadata\99000\achievements.json", "achievements.json",
        [
            new ManifestSaveEntry("GSE Saves", "save.dat", @"%USERPROFILE%\AppData\Roaming\GSE Saves\99000\save.dat", "AppID/99000/save/save.dat", 1234, "abcdef0123456789"),
        ]);
    var manifestJson = manifest.ToJson();
    var restoredManifest = RestorationManifest.FromJson(manifestJson);
    Check(restoredManifest is not null && restoredManifest!.AppId == "99000" && restoredManifest.Saves.Count == 1
        && restoredManifest.Saves[0].RepoPath == "AppID/99000/save/save.dat" && restoredManifest.Saves[0].Sha256 == "abcdef0123456789",
        "restoration manifest serializes and round-trips with correct path and hash");

    // Path sanitizer rewrites the user-profile prefix with the most specific environment-variable token and leaves other paths untouched.
    var realProfilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming", "GSE Saves", "42", "achievements.json");
    var sanitizedRound = PathSanitizer.Sanitize(realProfilePath);
    Check((sanitizedRound.StartsWith("%APPDATA%", StringComparison.OrdinalIgnoreCase)
              || sanitizedRound.StartsWith("%USERPROFILE%", StringComparison.OrdinalIgnoreCase))
        && !sanitizedRound.Contains(Environment.UserName, StringComparison.Ordinal),
        "path sanitizer rewrites user-profile prefix with an environment-variable token");
    Check(PathSanitizer.Sanitize(@"D:\Games\Save\slot.sav") == @"D:\Games\Save\slot.sav",
        "path sanitizer leaves non-profile drive paths untouched");
    var realDesktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "notes.txt");
    Check(PathSanitizer.Sanitize(realDesktopPath).Equals("%USERPROFILE%\\notes.txt", StringComparison.Ordinal),
        "path sanitizer rewrites even the profile-root itself, not just known subfolders");
    var roamingFull = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    Check(PathSanitizer.Sanitize(Path.Combine(roamingFull, "GSE Saves", "730", "remote", "save.dat"))
            .Equals("%APPDATA%\\GSE Saves\\730\\remote\\save.dat", StringComparison.Ordinal),
        "path sanitizer prefers %APPDATA% over %USERPROFILE% for the Roaming subtree");

    // Save backup planner: only save files + manifest (achievements data is synced separately).
    var saveRoot = Path.Combine(tempRoot, "backup-planner-saves");
    var saveFile = Path.Combine(saveRoot, "save.dat");
    Directory.CreateDirectory(saveRoot);
    File.WriteAllBytes(saveFile, [1, 2, 3]);
    var plan = await SaveBackupPlanner.BuildAsync("730",
        [
            new DiscoveredSaveFile("730", "GSE Saves", "remote/save.dat", saveFile, 999, "a1b2c3d4", "AppID/730/save/remote/save.dat"),
        ],
        @"%USERPROFILE%\AppData\Local\AchievementSync\metadata\730\achievements.json", "json");
    Check(plan.Items.Count == 1 && plan.Items[0].RepoPath == "AppID/730/save/remote/save.dat"
        && plan.Manifest.AppId == "730" && plan.Manifest.Saves.Count == 1,
        "backup planner packs only save files plus the manifest; achievements are synced separately");

    // Save backup push: skips unchanged saves by SHA-256, merges achievements.json, reports per file.
    var backupCalls = 0;
    var backupRemoteJson = "{\"ACH_X\":{\"earned\":false,\"earned_time\":0}}";
    using (var backupHttp = new HttpClient(new TestHttpHandler((request, _) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && path == "/repos/pozpo/viewer")
            return Task.FromResult(JsonResponse("""{"full_name":"pozpo/viewer","default_branch":"user","private":false,"permissions":{"push":true}}"""));
        if (request.Method == HttpMethod.Get && path.EndsWith("/git/ref/heads/user", StringComparison.Ordinal))
            return Task.FromResult(JsonResponse("""{"object":{"sha":"commit-old"}}"""));
        if (request.Method == HttpMethod.Get && path.EndsWith("/git/commits/commit-old", StringComparison.Ordinal))
            return Task.FromResult(JsonResponse("""{"tree":{"sha":"tree-old"}}"""));
        if (request.Method == HttpMethod.Get && path.EndsWith("/contents/AppID/730/achievements.json", StringComparison.Ordinal))
            return Task.FromResult(JsonResponse("{\"type\":\"file\",\"encoding\":\"base64\",\"content\":\"" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(backupRemoteJson)) + "\"}"));
        if (request.Method == HttpMethod.Get && path.EndsWith("/contents/AppID/730/restoration-manifest.json", StringComparison.Ordinal))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        if (request.Method == HttpMethod.Post && path.EndsWith("/git/blobs", StringComparison.Ordinal))
        { backupCalls++; return Task.FromResult(JsonResponse("""{"sha":"blob-new"}""")); }
        if (request.Method == HttpMethod.Post && path.EndsWith("/git/trees", StringComparison.Ordinal))
        { backupCalls++; return Task.FromResult(JsonResponse("""{"sha":"tree-new"}""")); }
        if (request.Method == HttpMethod.Post && path.EndsWith("/git/commits", StringComparison.Ordinal))
        { backupCalls++; return Task.FromResult(JsonResponse("""{"sha":"commit-new"}""")); }
        if (request.Method.Method == "PATCH" && path.EndsWith("/git/refs/heads/user", StringComparison.Ordinal))
        { backupCalls++; return Task.FromResult(JsonResponse("""{"object":{"sha":"commit-new"}}""")); }
        throw new InvalidOperationException($"Unexpected synthetic GitHub request: {request.Method} {path}");
    })))
    {
        var backupClient = new GitHubAchievementSyncClient(backupHttp);
        var backupTarget = new GitHubSyncTarget("pozpo", "viewer", "user");
        var backupPlan = new SaveBackupPlan("730",
            [
                new SaveBackupItem("730", "AppID/730/achievements.json", @"%USERPROFILE%\AppData\Local\AchievementSync\metadata\730\achievements.json",
                    "dummy-sha-json", System.Text.Encoding.UTF8.GetBytes("""{"ACH_X":{"earned":true,"earned_time":1700000100}}""")),
                new SaveBackupItem("730", "AppID/730/save/save.dat", @"%USERPROFILE%\AppData\Roaming\GSE Saves\730\remote\save.dat",
                    "dummy-sha-save", new byte[0]),
            ],
            RestorationManifest.Create("730", "json", @"%USERPROFILE%\AppData\Local\AchievementSync\metadata\730\achievements.json", "achievements.json",
                [new ManifestSaveEntry("GSE Saves", "remote/save.dat", @"%USERPROFILE%\AppData\Roaming\GSE Saves\730\remote\save.dat", "AppID/730/save/save.dat", 999, "a1b2c3d4")]));
        var backupResult = await backupClient.PushSaveBackupAsync("synthetic-token", backupTarget, [backupPlan]);
        Check(backupResult.Pushed && backupResult.FilesPushed >= 2 && backupCalls >= 4
            && backupResult.Report.Any(line => line.Contains("achievements.json pushed")),
            "save backup push uploads achievements.json merged with remote unlocks plus save files, reporting each file");
    }

    var itemWithSize = new SaveBackupItem("4115450", "AppID/4115450/save/real.sav", "C:\\nonexistent.sav", "sha", null, 12345);
    Check(itemWithSize.SizeBytes == 12345, "SaveBackupItem preserves discovery file size in SizeBytes without reading disk");

    // Platform roots contribute save backups even when their achievement adapters return no game snapshots.
    var configuredXeniaRoot = Path.Combine(tempRoot, "configured-xenia");
    var configuredSavePath = Path.Combine(configuredXeniaRoot, "GameOne", "Save", "slot.sav");
    Directory.CreateDirectory(Path.GetDirectoryName(configuredSavePath)!);
    File.WriteAllBytes(configuredSavePath, [7, 8, 9]);
    var configuredPlatformOptions = new PlatformSourceOptions { XeniaRoot = configuredXeniaRoot };
    var configuredPlatformSaves = await SaveFileDiscoverer.DiscoverPlatformSavesAsync([], configuredPlatformOptions);
    Check(configuredPlatformSaves.Count == 1
        && configuredPlatformSaves[0].SourcePath == configuredSavePath
        && configuredPlatformSaves[0].RootKey == "platform:xenia"
        && configuredPlatformSaves[0].BackupFolder?.StartsWith("PlatformSave/xbox360/xenia/", StringComparison.Ordinal) == true,
        "configured emulator save roots are scanned without requiring an achievement snapshot");
    var recordedCustomPlatformFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "PreviousXeniaRoot", "GameOne", "Save", "slot.sav");
    Check(SaveFileDiscoverer.ResolveLocalPath("platform:xenia", "GameOne", Path.Combine("GameOne", "Save", "slot.sav"),
            PathSanitizer.Sanitize(recordedCustomPlatformFile), configuredPlatformOptions)
        == Path.GetFullPath(recordedCustomPlatformFile),
        "platform save restore retains the exact recorded root when the new PC has a different configured root");

    // Native title-folder roots include %APPDATA%\\<title>\\Save.
    var originalAppData = Environment.GetEnvironmentVariable("APPDATA");
    var isolatedAppData = Path.Combine(tempRoot, "isolated-appdata");
    var roamingSavePath = Path.Combine(isolatedAppData, "NativeTitle", "Save", "profile.sav");
    Directory.CreateDirectory(Path.GetDirectoryName(roamingSavePath)!);
    File.WriteAllBytes(roamingSavePath, [4, 5, 6]);
    try
    {
        Environment.SetEnvironmentVariable("APPDATA", isolatedAppData);
        var nativeSaves = await SaveFileDiscoverer.DiscoverAsync(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["730"] = "NativeTitle" });
        Check(nativeSaves.Any(save => save.SourcePath == roamingSavePath && save.RootKey == "AppData Roaming Save"),
            "native save discovery scans %APPDATA%\\<title>\\Save");
    }
    finally
    {
        Environment.SetEnvironmentVariable("APPDATA", originalAppData);
    }

    // Lichii profile save IDs and optional explicitly present achievement JSON are detected without inventing a native API.
    var lichiiRoot = Path.Combine(tempRoot, "lichii-profile");
    var switchTitleId = "0100A1B2C3D4E500";
    var switchTitleSave = Path.Combine(lichiiRoot, "nand", "user", "save", switchTitleId);
    Directory.CreateDirectory(switchTitleSave);
    File.WriteAllBytes(Path.Combine(switchTitleSave, "user.sav"), [1, 3, 5]);
    File.WriteAllText(Path.Combine(switchTitleSave, "achievements.json"), "{\"SWITCH_TEST\":{\"earned\":true,\"earned_time\":1700000000}}");
    var switchOptions = new PlatformSourceOptions { LichiiRoot = lichiiRoot };
    var switchAdapter = new Switch2EmulatorDataAdapter(switchOptions);
    var switchCollection = await switchAdapter.CollectAsync(switchOptions);
    var switchProfileSaves = await SaveFileDiscoverer.DiscoverPlatformSavesAsync(switchCollection.Games, switchOptions);
    Check(switchCollection.Games.Any(game => game.GameId == switchTitleId && game.Achievements.Any(achievement => achievement.Id == "SWITCH_TEST" && achievement.Earned))
        && switchProfileSaves.Any(save => save.SourcePath.EndsWith("user.sav", StringComparison.Ordinal)),
        "Lichii Switch 2 profile save data and explicitly supplied achievement JSON are detected");

    // Recomp port profile\\save folders are detected even when the folder contains no achievements file.
    var switchRecompRoot = Path.Combine(tempRoot, "switch2-recomp");
    var ocarinaSavePath = Path.Combine(switchRecompRoot, "perfil", "save", "ocarina.sav");
    Directory.CreateDirectory(Path.GetDirectoryName(ocarinaSavePath)!);
    File.WriteAllBytes(ocarinaSavePath, [2, 4, 6]);
    var switchRecompOptions = new PlatformSourceOptions { Switch2RecompRoot = switchRecompRoot };
    var switchRecompAdapter = new OcarinaRecompDataAdapter(switchRecompOptions);
    var recompCollection = await switchRecompAdapter.CollectAsync(switchRecompOptions);
    var recompSaves = await SaveFileDiscoverer.DiscoverPlatformSavesAsync(recompCollection.Games, switchRecompOptions);
    Check(recompCollection.Games.Any(game => game.GameId == "ocarina-recomp")
        && recompSaves.Any(save => save.SourcePath == ocarinaSavePath),
        "Switch 2 recomp profile\\save files are detected for Ocarina-style ports");

    // A clean machine's empty AppID filter enumerates repository manifests and reconstructs original save paths.
    var restoreRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GSE Saves", "730");
    var remoteManifest = RestorationManifest.Create("730", "json",
        "%APPDATA%\\GSE Saves\\730\\achievements.json", "achievements.json",
        [new ManifestSaveEntry("GSE Saves", "save.dat", "%APPDATA%\\GSE Saves\\730\\remote\\save.dat",
            "AppID/730/save/remote/save.dat", 3, "sha256")], "AppID/730").ToJson();
    var encodedManifest = Convert.ToBase64String(Encoding.UTF8.GetBytes(remoteManifest));
    var encodedAchievements = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"ACH_REMOTE\":{\"earned\":true,\"earned_time\":1700000000}}"));
    using (var restoreHttp = new HttpClient(new TestHttpHandler((request, _) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && path.EndsWith("/git/ref/heads/user", StringComparison.Ordinal))
            return Task.FromResult(JsonResponse("""{"object":{"sha":"restore-head"}}"""));
        if (request.Method == HttpMethod.Get && path.EndsWith("/git/commits/restore-head", StringComparison.Ordinal))
            return Task.FromResult(JsonResponse("""{"tree":{"sha":"restore-tree"}}"""));
        if (request.Method == HttpMethod.Get && path.EndsWith("/git/trees/restore-tree", StringComparison.Ordinal))
            return Task.FromResult(JsonResponse("""{"truncated":false,"tree":[{"path":"AppID/730/restoration-manifest.json","type":"blob"},{"path":"AppID/730/achievements.json","type":"blob"},{"path":"AppID/730/save/remote/save.dat","type":"blob"}]}"""));
        if (request.Method == HttpMethod.Get && path.EndsWith("/contents/AppID/730/restoration-manifest.json", StringComparison.Ordinal))
            return Task.FromResult(JsonResponse("{\"type\":\"file\",\"encoding\":\"base64\",\"content\":\"" + encodedManifest + "\"}"));
        if (request.Method == HttpMethod.Get && path.EndsWith("/contents/AppID/730/achievements.json", StringComparison.Ordinal))
            return Task.FromResult(JsonResponse("{\"type\":\"file\",\"encoding\":\"base64\",\"content\":\"" + encodedAchievements + "\"}"));
        throw new InvalidOperationException($"Unexpected synthetic restore request: {request.Method} {path}");
    })))
    {
        var restoreClient = new GitHubAchievementSyncClient(restoreHttp);
        var restorePlans = await restoreClient.PullSaveBackupAsync("synthetic-token",
            new GitHubSyncTarget("pozpo", "viewer", "user"), Array.Empty<string>(), new PlatformSourceOptions());
        Check(restorePlans.Count == 1
            && restorePlans[0].AppId == "730"
            && restorePlans[0].AchievementsJson?.Contains("ACH_REMOTE", StringComparison.Ordinal) == true
            && restorePlans[0].Saves.Count == 1
            && restorePlans[0].Saves[0].TargetPath == Path.Combine(restoreRoot, "remote", "save.dat"),
            "GitHub restore discovers remote achievements and resolves saves to their recorded local paths on a clean PC");
    }

    var restoreSource = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "src", "AchievementSync.Desktop", "ViewModels", "MainViewModel.cs"));
    Check(restoreSource.Contains("Array.Empty<string>(), BuildPlatformSourceOptions()", StringComparison.Ordinal)
        && restoreSource.Contains("FileMode.CreateNew", StringComparison.Ordinal)
        && restoreSource.Contains("kept existing", StringComparison.Ordinal),
        "GitHub restore enumerates all branch backups on a fresh PC and creates files without overwriting local data");
    Check(mainWindowXaml.Contains("empty library", StringComparison.Ordinal)
        && mainWindowXaml.Contains("Existing files are kept.", StringComparison.Ordinal),
        "Sync Activity explains clean-PC restore behavior and that existing local files are preserved");

    var appSource = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "src", "AchievementSync.Desktop", "App.xaml.cs"));
    Check(appSource.Contains("if (!StartInTray)", StringComparison.Ordinal) && appSource.Contains("mainWindow.InitializeAppAsync()", StringComparison.Ordinal),
        "application startup does not show the main window when launched with --tray");

    var vmSource = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "src", "AchievementSync.Desktop", "ViewModels", "MainViewModel.cs"));
    Check(!vmSource.Contains("AutoSyncAfterScan", StringComparison.Ordinal)
        && vmSource.Contains("RequestAutoSyncAfterUnlock(notification.AppId)", StringComparison.Ordinal),
        "automatic achievement sync is separate from library discovery and runs through the unlock watcher");

    var monitorSourcePath = Path.Combine(repositoryRoot.FullName, "src", "AchievementSync.Desktop", "Services", "WindowsGameProcessMonitor.cs");
    var monitorSource = File.ReadAllText(Path.GetFullPath(monitorSourcePath));
    Check(monitorSource.Contains("Process.GetProcessesByName(name)", StringComparison.Ordinal)
        && monitorSource.Contains("process.MainModule?.FileName", StringComparison.Ordinal)
        && !monitorSource.Contains("Process.GetProcesses()", StringComparison.Ordinal),
        "process reconciliation is limited to linked executable names and verifies each full path");

    var smokeTestHost = Path.Combine(AppContext.BaseDirectory, "AchievementSync.SmokeTests.exe");
    var probeRoot = Path.Combine(tempRoot, "wmi-process-probe");
    Directory.CreateDirectory(probeRoot);
    foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(AppContext.BaseDirectory, source);
        var destination = Path.Combine(probeRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }
    var probeExecutable = Path.Combine(probeRoot, "tracker-probe.exe");
    if (File.Exists(smokeTestHost)) File.Copy(smokeTestHost, probeExecutable, overwrite: true);
    var integrationStatePath = Path.Combine(tempRoot, "wmi-integration", "playtime.json");
    var integrationTracker = new GameplaySessionTracker(integrationStatePath);
    integrationTracker.SetLibraryGames(["900001"]);
    integrationTracker.AddExecutable("900001", probeExecutable);
    var monitorConstructor = monitorType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
    var monitor = monitorConstructor.Invoke([integrationTracker, TimeSpan.FromMilliseconds(250)]);
    var monitorStart = (Task)monitorType.GetMethod("StartAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .Invoke(monitor, null)!;
    await monitorStart;
    try
    {
        Check(File.Exists(probeExecutable), "a temporary smoke-test process fixture is ready for native WMI verification");
        using var probeProcess = Process.Start(new ProcessStartInfo(probeExecutable, "--playtime-wmi-fixture")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("The temporary gameplay process fixture could not be started.");
        await probeProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        await Task.Delay(500);
    }
    finally
    {
        await ((IAsyncDisposable)monitor).DisposeAsync();
    }
    var wmiObservedGame = integrationTracker.GetSnapshots(DateTimeOffset.UtcNow).Single();
    Check(wmiObservedGame.TotalPlaytime >= TimeSpan.FromSeconds(2)
        && !wmiObservedGame.IsRunning
        && wmiObservedGame.LastPlayedUtc.HasValue,
        "the native process monitor records a temporary associated process through filtered WMI events or its targeted fallback");

    var embeddedPath = AchievementSync.Core.Enabler.EmbeddedGeneratorRuntime.EnsureReady();
    Check(AchievementSync.Core.Enabler.EmbeddedGeneratorRuntime.IsReady, "embedded generator runtime auto-extracts and is ready");
    Check(File.Exists(AchievementSync.Core.Enabler.EmbeddedGeneratorRuntime.ExecutablePath), "embedded generate_emu_config.exe exists on disk");
    using var schemaGenerator = new AchievementSync.Core.Enabler.SteamSchemaGeneratorService();
    Check(schemaGenerator.IsConfigured, "SteamSchemaGeneratorService is configured with embedded runtime without external apps");

    // Test all embedded emulators extraction and readiness
    var regularPath = AchievementSync.Core.Enabler.EmbeddedRegularEmulatorRuntime.EnsureReady();
    Check(AchievementSync.Core.Enabler.EmbeddedRegularEmulatorRuntime.IsReady, "embedded regular Goldberg emulator runtime auto-extracts and is ready");
    Check(File.Exists(Path.Combine(regularPath, "steam_api64.dll")) && File.Exists(Path.Combine(regularPath, "steam_api.dll")), "embedded Goldberg steam_api DLLs exist on disk");

    var coldLoaderPath = AchievementSync.Core.Enabler.EmbeddedColdLoaderRuntime.EnsureReady();
    Check(AchievementSync.Core.Enabler.EmbeddedColdLoaderRuntime.IsReady, "embedded ColdLoader runtime auto-extracts and is ready");
    Check(File.Exists(Path.Combine(coldLoaderPath, "coldloader.dll"))
        && File.Exists(Path.Combine(coldLoaderPath, "coldloader.ini"))
        && File.Exists(Path.Combine(coldLoaderPath, "version.dll")), "embedded ColdLoader and version.dll proxy files exist on disk");
    var adapterVersionDll = Path.Combine(repositoryRoot.FullName, "Adapters", "version.dll");
    if (!File.Exists(adapterVersionDll))
        adapterVersionDll = Path.Combine(repositoryRoot.FullName, "src", "AchievementSync.Core", "Resources", "ColdLoaderTemplate", "version.dll");
    Check(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(adapterVersionDll))
        .SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(coldLoaderPath, "version.dll")))),
        "embedded version.dll proxy exactly matches Adapters/version.dll");

    var coldClientPath = AchievementSync.Core.Enabler.EmbeddedColdClientRuntime.EnsureReady();
    Check(File.Exists(Path.Combine(coldClientPath, "steamclient64.dll")) && File.Exists(Path.Combine(coldClientPath, "ColdClientLoader.ini")), "embedded ColdClient files exist on disk");

    var uplayR1Path = AchievementSync.Core.Enabler.EmbeddedUplayR1Runtime.EnsureReady();
    Check(AchievementSync.Core.Enabler.EmbeddedUplayR1Runtime.IsReady, "embedded Uplay R1 runtime auto-extracts and is ready");
    Check(File.Exists(Path.Combine(uplayR1Path, "uplay_r1_loader64.dll")) && File.Exists(Path.Combine(uplayR1Path, "uplay_r1.ini")), "embedded Uplay R1 files exist on disk");

    var uplayR2Path = AchievementSync.Core.Enabler.EmbeddedUplayR2Runtime.EnsureReady();
    Check(AchievementSync.Core.Enabler.EmbeddedUplayR2Runtime.IsReady, "embedded Uplay R2 runtime auto-extracts and is ready");
    Check(File.Exists(Path.Combine(uplayR2Path, "uplay_r2_loader64.dll")) && File.Exists(Path.Combine(uplayR2Path, "uplay_r2.ini")), "embedded Uplay R2 files exist on disk");

    var epicPath = AchievementSync.Core.Enabler.EmbeddedEpicRuntime.EnsureReady();
    Check(AchievementSync.Core.Enabler.EmbeddedEpicRuntime.IsReady, "embedded Nemirtingas Epic emulator runtime auto-extracts and is ready");
    Check(File.Exists(Path.Combine(epicPath, "EOSSDK-Win64-Shipping.dll")) && File.Exists(Path.Combine(epicPath, "NemirtingasEpicEmu.json")), "embedded Nemirtingas Epic files exist on disk");

    var gogPath = AchievementSync.Core.Enabler.EmbeddedUniverseLanRuntime.EnsureReady();
    Check(AchievementSync.Core.Enabler.EmbeddedUniverseLanRuntime.IsReady, "embedded GOG UniverseLAN runtime auto-extracts and is ready");
    Check(File.Exists(Path.Combine(gogPath, "Galaxy64.dll")) && File.Exists(Path.Combine(gogPath, "UniverseLANServer64.exe")) && File.Exists(Path.Combine(gogPath, "UniverseLAN.ini")), "embedded UniverseLAN files exist on disk");

    var eaPath = AchievementSync.Core.Enabler.EmbeddedEaOriginRuntime.EnsureReady();
    Check(AchievementSync.Core.Enabler.EmbeddedEaOriginRuntime.IsReady, "embedded EA / Origin emulator runtime auto-extracts and is ready");
    Check(File.Exists(Path.Combine(eaPath, "anadius64.dll")) && File.Exists(Path.Combine(eaPath, "anadius32.dll")), "embedded anadius EA files exist on disk");

    // Test plan generation and application for Uplay R1, Uplay R2, Epic, UniverseLAN, EA Origin
    var enablerService = new AchievementSync.Core.Enabler.AchievementEnablerService();

    // 1. Uplay R2 Plan & Apply
    var mockUplayR2Game = Path.Combine(tempRoot, "MockUplayR2Game");
    Directory.CreateDirectory(mockUplayR2Game);
    File.WriteAllText(Path.Combine(mockUplayR2Game, "uplay_r2_loader64.dll"), "fake-original-dll");
    var uplayR2Detect = await enablerService.DetectAsync(mockUplayR2Game);
    Check(uplayR2Detect.SuggestedTarget == AchievementSync.Core.Enabler.EnablerTarget.UplayR2, "Uplay R2 game detected correctly");
    var uplayR2Plan = enablerService.BuildPlan(uplayR2Detect, AchievementSync.Core.Enabler.EnablerTarget.UplayR2, gameIdOverride: "12345");
    Check(uplayR2Plan.Blockers.Count == 0, "Uplay R2 plan has zero blockers");
    Check(uplayR2Plan.Items.Any(i => i.Destination.EndsWith("uplay_r2_loader64.dll", StringComparison.OrdinalIgnoreCase)), "Uplay R2 plan contains replacement loader");
    Check(uplayR2Plan.Items.Any(i => i.Destination.EndsWith("uplay_r2.ini", StringComparison.OrdinalIgnoreCase)), "Uplay R2 plan contains patched INI");
    var uplayR2Apply = enablerService.Apply(uplayR2Plan);
    Check(uplayR2Apply.FilesWritten > 0, "Uplay R2 plan applied successfully");
    Check(File.Exists(Path.Combine(mockUplayR2Game, "uplay_r2_loader64.dll.BAK")), "Original Uplay loader backed up to .BAK");

    // 2. Uplay R1 Plan & Apply
    var mockUplayR1Game = Path.Combine(tempRoot, "MockUplayR1Game");
    Directory.CreateDirectory(mockUplayR1Game);
    File.WriteAllText(Path.Combine(mockUplayR1Game, "upc_r1_loader64.dll"), "fake-original-dll");
    var uplayR1Detect = await enablerService.DetectAsync(mockUplayR1Game);
    Check(uplayR1Detect.SuggestedTarget == AchievementSync.Core.Enabler.EnablerTarget.UplayR1, "Uplay R1 game detected correctly");
    var uplayR1Plan = enablerService.BuildPlan(uplayR1Detect, AchievementSync.Core.Enabler.EnablerTarget.UplayR1, gameIdOverride: "12345");
    Check(uplayR1Plan.Blockers.Count == 0, "Uplay R1 plan has zero blockers");
    var uplayR1Apply = enablerService.Apply(uplayR1Plan);
    Check(uplayR1Apply.FilesWritten > 0, "Uplay R1 plan applied successfully");

    // 3. Epic Nemirtingas Plan & Apply
    var mockEpicGame = Path.Combine(tempRoot, "MockEpicGame");
    Directory.CreateDirectory(mockEpicGame);
    File.WriteAllText(Path.Combine(mockEpicGame, "EOSSDK-Win64-Shipping.dll"), "fake-original-dll");
    File.WriteAllText(Path.Combine(mockEpicGame, "Game.exe"), "fake-exe");
    var epicDetect = await enablerService.DetectAsync(mockEpicGame);
    Check(epicDetect.SuggestedTarget == AchievementSync.Core.Enabler.EnablerTarget.EpicEmulator, "Epic game detected correctly");
    var epicPlan = enablerService.BuildPlan(epicDetect, AchievementSync.Core.Enabler.EnablerTarget.EpicEmulator, gameIdOverride: "my-epic-namespace");
    Check(epicPlan.Blockers.Count == 0, "Epic plan has zero blockers");
    Check(epicPlan.Items.Any(i => i.Destination.Contains("nepice_settings")), "Epic plan configures nepice_settings");
    var epicApply = enablerService.Apply(epicPlan);
    Check(epicApply.FilesWritten > 0, "Epic plan applied successfully");
    Check(File.Exists(Path.Combine(mockEpicGame, "nepice_settings", "NemirtingasEpicEmu.json")), "NemirtingasEpicEmu.json written to nepice_settings");

    // 4. GOG UniverseLAN Plan & Apply
    var mockGogGame = Path.Combine(tempRoot, "MockGogGame");
    Directory.CreateDirectory(mockGogGame);
    File.WriteAllText(Path.Combine(mockGogGame, "Galaxy64.dll"), "fake-original-dll");
    File.WriteAllText(Path.Combine(mockGogGame, "goggame-123456789.info"), "{\"name\":\"Mock GOG Game\"}");
    var gogDetect = await enablerService.DetectAsync(mockGogGame);
    Check(gogDetect.SuggestedTarget == AchievementSync.Core.Enabler.EnablerTarget.GogUniverseLan, "GOG UniverseLAN game detected correctly");
    var gogPlan = enablerService.BuildPlan(gogDetect, AchievementSync.Core.Enabler.EnablerTarget.GogUniverseLan, gameIdOverride: "123456789");
    Check(gogPlan.Blockers.Count == 0, "GOG UniverseLAN plan has zero blockers");
    Check(gogPlan.Items.Any(i => i.Destination.EndsWith("Galaxy64.dll", StringComparison.OrdinalIgnoreCase)), "GOG plan replaces Galaxy64.dll");
    Check(gogPlan.Items.Any(i => i.Destination.EndsWith("UniverseLANServer64.exe", StringComparison.OrdinalIgnoreCase)), "GOG plan provides UniverseLANServer64.exe");
    var gogApply = enablerService.Apply(gogPlan);
    Check(gogApply.FilesWritten > 0, "GOG UniverseLAN plan applied successfully");

    // 5. EA / Origin Plan & Apply
    var mockEaGame = Path.Combine(tempRoot, "MockEaGame");
    var installerDir = Path.Combine(mockEaGame, "__Installer");
    Directory.CreateDirectory(installerDir);
    File.WriteAllText(Path.Combine(mockEaGame, "EAGame.exe"), "fake-exe");
    File.WriteAllText(Path.Combine(installerDir, "installerdata.xml"), "<gameTitles><gameTitle locale=\"en_US\">Mock EA Game</gameTitle></gameTitles><buildMetaData><gameVersion version=\"1.0.0\"/></buildMetaData><contentIDs><contentID>100200</contentID></contentIDs>");
    var eaDetect = await enablerService.DetectAsync(mockEaGame);
    Check(eaDetect.SuggestedTarget == AchievementSync.Core.Enabler.EnablerTarget.EaOrigin, "EA / Origin game detected correctly");
    var eaPlan = enablerService.BuildPlan(eaDetect, AchievementSync.Core.Enabler.EnablerTarget.EaOrigin, gameIdOverride: "100200");
    Check(eaPlan.Blockers.Count == 0, "EA / Origin plan has zero blockers");
    Check(eaPlan.Items.Any(i => i.Destination.EndsWith("anadius.cfg", StringComparison.OrdinalIgnoreCase)), "EA plan contains anadius.cfg");
    Check(eaPlan.Items.Any(i => i.Destination.EndsWith("anadius64.dll", StringComparison.OrdinalIgnoreCase)), "EA plan contains anadius64.dll");
    var eaApply = enablerService.Apply(eaPlan);
    Check(eaApply.FilesWritten > 0, "EA / Origin plan applied successfully");
    Check(File.Exists(Path.Combine(mockEaGame, "anadius.cfg")), "anadius.cfg written to game folder");

    // 6. ColdClient executable auto-discovery & explicit override
    var mockColdClientGame = Path.Combine(tempRoot, "MockColdClientGame");
    var shippingDir = Path.Combine(mockColdClientGame, "MyGame", "Binaries", "Win64");
    var steamThirdPartyDir = Path.Combine(mockColdClientGame, "Engine", "Binaries", "ThirdParty", "Steamworks", "Win64");
    Directory.CreateDirectory(shippingDir);
    Directory.CreateDirectory(steamThirdPartyDir);
    var shippingExe = Path.Combine(shippingDir, "MyGame-Win64-Shipping.exe");
    var altExe = Path.Combine(mockColdClientGame, "LauncherHelper.exe");
    File.WriteAllText(shippingExe, "fake-shipping-exe");
    File.WriteAllText(altExe, "fake-alt-exe");
    File.WriteAllText(Path.Combine(steamThirdPartyDir, "steam_api64.dll"), "fake-steam-api");
    Directory.CreateDirectory(Path.Combine(mockColdClientGame, "generate_emu_config", "output", "480", "steam_settings"));
    File.WriteAllText(Path.Combine(mockColdClientGame, "generate_emu_config", "output", "480", "steam_settings", "achievements.json"), "[]");
    var ccDetect = await enablerService.DetectAsync(mockColdClientGame);
    Check(string.Equals(ccDetect.Executable, shippingExe, StringComparison.OrdinalIgnoreCase),
        "DetectAsync on folder auto-selects Shipping executable for ColdClient");
    var ccDetectExplicit = await enablerService.DetectAsync(mockColdClientGame, altExe);
    Check(string.Equals(ccDetectExplicit.Executable, altExe, StringComparison.OrdinalIgnoreCase),
        "DetectAsync honors explicit executable selection even when folder is passed");
    var ccPlan = enablerService.BuildPlan(ccDetect, AchievementSync.Core.Enabler.EnablerTarget.SteamColdClient, gameIdOverride: "480");
    Check(ccPlan.Blockers.Count == 0, "ColdClient plan succeeds with auto-selected executable");
    var ccIniItem = ccPlan.Items.FirstOrDefault(i => i.Destination.EndsWith("ColdClientLoader.ini", StringComparison.OrdinalIgnoreCase));
    Check(ccIniItem?.Content is not null && System.Text.Encoding.UTF8.GetString(ccIniItem.Content).Contains("MyGame-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase),
        "ColdClientLoader.ini targets the selected executable");
    Check(ccPlan.Items.Any(i => i.Destination.EndsWith(Path.Combine("_ColdClient", "version.txt"), StringComparison.OrdinalIgnoreCase)
        && i.Content is not null && System.Text.Encoding.UTF8.GetString(i.Content).Contains("AchievementSync", StringComparison.Ordinal)),
        "ColdClient plan writes a bundled-version marker");
    var ccUserDataItem = ccPlan.Items.FirstOrDefault(i => i.Destination.EndsWith(Path.Combine("_ColdClient", "link_steam_userdata.ps1"), StringComparison.OrdinalIgnoreCase));
    Check(ccUserDataItem?.Content is not null && System.Text.Encoding.UTF8.GetString(ccUserDataItem.Content).Contains("ItemType Junction", StringComparison.Ordinal),
        "ColdClient plan includes a safe Steam userdata junction helper");

    // 7. Combined ColdLoader plan includes the version.dll proxy and matching runtime
    var mockColdLoaderGame = Path.Combine(tempRoot, "MockColdLoaderGame");
    Directory.CreateDirectory(mockColdLoaderGame);
    File.WriteAllText(Path.Combine(mockColdLoaderGame, "steam_api64.dll"), "fake-api");
    File.Copy(Path.Combine(gogPath, "UniverseLANServer64.exe"), Path.Combine(mockColdLoaderGame, "Game.exe"));
    Directory.CreateDirectory(Path.Combine(mockColdLoaderGame, "generate_emu_config", "output", "480", "steam_settings"));
    File.WriteAllText(Path.Combine(mockColdLoaderGame, "generate_emu_config", "output", "480", "steam_settings", "achievements.json"), "[]");
    var clDetect = await enablerService.DetectAsync(mockColdLoaderGame);
    var clCombinedPlan = enablerService.BuildPlan(clDetect, AchievementSync.Core.Enabler.EnablerTarget.SteamColdLoaderWithRegularEmulator, gameIdOverride: "480");
    Check(clCombinedPlan.Blockers.Count == 0, "ColdLoader combined plan has zero blockers");
    Check(clCombinedPlan.Items.Any(i => i.Destination.EndsWith("version.dll", StringComparison.OrdinalIgnoreCase)
        && i.Source is not null && Path.GetFileName(i.Source).Equals("version.dll", StringComparison.OrdinalIgnoreCase)),
        "ColdLoader combined plan installs the version.dll proxy");
    Check(clCombinedPlan.Items.Any(i => i.Destination.EndsWith("coldloader.dll", StringComparison.OrdinalIgnoreCase))
        && clCombinedPlan.Items.Any(i => i.Destination.EndsWith("steamclient64.dll", StringComparison.OrdinalIgnoreCase))
        && clCombinedPlan.Items.Any(i => i.Destination.EndsWith("GameOverlayRenderer64.dll", StringComparison.OrdinalIgnoreCase)),
        "ColdLoader combined plan places its proxy target and x64 support files beside the game executable");
    Check(clCombinedPlan.Warnings.Any(w => w.Contains("version.dll proxy", StringComparison.OrdinalIgnoreCase)),
        "ColdLoader combined plan explains when the proxy will load");
    var enablerTargetControl = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "src", "AchievementSync.Desktop", "Controls", "AchievementEnablerControl.xaml.cs"));
    Check(!enablerTargetControl.Contains("new(EnablerTarget.SteamColdLoader,", StringComparison.Ordinal),
        "Achievement Enabler tab no longer offers standalone ColdLoader");
    Check(enablerTargetControl.Contains("new(EnablerTarget.UplayLumaPlay,", StringComparison.Ordinal),
        "Achievement Enabler tab exposes the supported LumaPlay target");

    // 8. Epic .egstore local namespace detection
    var mockEgStoreGame = Path.Combine(tempRoot, "MockEgStoreGame");
    Directory.CreateDirectory(Path.Combine(mockEgStoreGame, ".egstore"));
    File.WriteAllText(Path.Combine(mockEgStoreGame, "EOSSDK-Win64-Shipping.dll"), "fake-eos");
    File.WriteAllText(Path.Combine(mockEgStoreGame, ".egstore", "item.mancpn"), "{\"CatalogNamespace\":\"abcdef1234567890abcdef1234567890\"}");
    var egStoreDetect = await enablerService.DetectAsync(mockEgStoreGame);
    Check(egStoreDetect.SuggestedTarget == AchievementSync.Core.Enabler.EnablerTarget.EpicEmulator &&
          egStoreDetect.SteamAppId == "abcdef1234567890abcdef1234567890",
        "Epic detection reads CatalogNamespace from .egstore/*.mancpn");

    // 9. GOG UniverseLAN preserves existing files, cleans opposite bitness, writes paired launcher, blocks mismatched SDK version
    var mockGogPreserve = Path.Combine(tempRoot, "MockGogPreserve");
    Directory.CreateDirectory(mockGogPreserve);
    var gogExePath = Path.Combine(mockGogPreserve, "GogGame.exe");
    File.Copy(Path.Combine(gogPath, "UniverseLANServer64.exe"), gogExePath);
    File.WriteAllText(Path.Combine(mockGogPreserve, "Galaxy64.dll"), "fake-galaxy64");
    File.WriteAllText(Path.Combine(mockGogPreserve, "Galaxy.dll"), "leftover-32bit");
    File.WriteAllText(Path.Combine(mockGogPreserve, "goggame-987654321.info"), "{\"name\":\"Mock Preserve\"}");
    File.WriteAllText(Path.Combine(mockGogPreserve, "steam_appid.txt"), "480");
    var gogAppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniverseLAN", "987654321", "UniverseLANData");
    Directory.CreateDirectory(gogAppDataDir);
    var gogAchIni = Path.Combine(gogAppDataDir, "Achievements.ini");
    File.WriteAllText(gogAchIni, "[ACH_BOSS]\nUnlocked = 1\nUnlockTime = 123\n\n[ACH_EXTRA]\nUnlocked = 1\nUnlockTime = 456\n");
    try
    {
        var gogPreserveDetect = await enablerService.DetectAsync(mockGogPreserve);
        var gogAchievementSource = Path.Combine(mockGogPreserve, "generate_emu_config", "output", "480", "steam_settings");
        Directory.CreateDirectory(gogAchievementSource);
        File.WriteAllText(Path.Combine(gogAchievementSource, "achievements.json"), """
            [
              { "name": "ACH_BOSS", "displayName": "Defeat the Boss", "description": "Win the fight.", "hidden": 0 },
              { "name": "ACH_ENDING", "displayName": "The Ending", "description": "Finish the story.", "hidden": 1 }
            ]
            """);
        var gogPreservePlan = enablerService.BuildPlan(gogPreserveDetect, AchievementSync.Core.Enabler.EnablerTarget.GogUniverseLan, gameIdOverride: "480");
        var generatedGogAchievements = gogPreservePlan.Items.FirstOrDefault(i =>
            string.Equals(i.Destination, gogAchIni, StringComparison.OrdinalIgnoreCase));
        var generatedGogText = generatedGogAchievements?.Content is null ? string.Empty : System.Text.Encoding.UTF8.GetString(generatedGogAchievements.Content);
        Check(generatedGogText.Contains("[ACH_BOSS]", StringComparison.Ordinal)
            && generatedGogText.Contains("Defeat the Boss", StringComparison.Ordinal)
            && generatedGogText.Contains("[ACH_ENDING]", StringComparison.Ordinal),
            "GOG UniverseLAN generates per-game achievement definitions from the selected AppID catalog");
        Check(gogPreservePlan.Items.Any(i => i.Kind == "delete" && i.Destination.EndsWith("Galaxy.dll", StringComparison.OrdinalIgnoreCase)),
            "GOG UniverseLAN removes unneeded opposite-architecture 32-bit DLL");
        Check(gogPreservePlan.Items.Any(i => i.Destination.EndsWith("_ae_gog_launch.ps1", StringComparison.OrdinalIgnoreCase)),
            "GOG UniverseLAN creates paired server + game launcher script");
        var gogGeneratedAchievements = gogPreservePlan.Items.FirstOrDefault(i =>
            i.Destination.EndsWith(Path.Combine("UniverseLANData", "Achievements.ini"), StringComparison.OrdinalIgnoreCase));
        var gogGeneratedText = gogGeneratedAchievements?.Content is null ? string.Empty : System.Text.Encoding.UTF8.GetString(gogGeneratedAchievements.Content);
        Check(gogGeneratedText.Contains("[ACH_BOSS]", StringComparison.Ordinal)
            && gogGeneratedText.Contains("Defeat the Boss", StringComparison.Ordinal)
            && gogGeneratedText.Contains("[ACH_ENDING]", StringComparison.Ordinal),
            "GOG UniverseLAN generates achievement definitions from the selected Steam AppID catalog");
        Check(gogGeneratedText.Contains("Unlocked = 1", StringComparison.Ordinal)
            && gogGeneratedText.Contains("UnlockTime = 123", StringComparison.Ordinal),
            "GOG UniverseLAN preserves existing achievement unlock state while updating definitions");
        Check(gogGeneratedText.Contains("[ACH_EXTRA]", StringComparison.Ordinal)
            && gogGeneratedText.Contains("UnlockTime = 456", StringComparison.Ordinal),
            "GOG UniverseLAN keeps existing achievement entries that are absent from the source catalog");
    }
    finally
    {
        try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniverseLAN", "987654321"), recursive: true); } catch { }
    }

    // 10. EA real PE patching (OriginExePatcher) + Denuvo SHA-1 hashes + brace-safe anadius.cfg update
    var mockEaRealPe = Path.Combine(tempRoot, "MockEaRealPe");
    Directory.CreateDirectory(Path.Combine(mockEaRealPe, "__Installer"));
    var sourceRealPe = Path.Combine(gogPath, "UniverseLANServer64.exe");
    var targetEaExe = Path.Combine(mockEaRealPe, "RealPeGame.exe");
    File.Copy(sourceRealPe, targetEaExe);
    File.WriteAllText(Path.Combine(mockEaRealPe, "dbdata.dll"), "fake-dbdata-for-sha1");
    File.WriteAllText(Path.Combine(mockEaRealPe, "__Installer", "installerdata.xml"), "<gameTitles><gameTitle locale=\"en_US\">Real PE EA Game</gameTitle></gameTitles><buildMetaData><gameVersion version=\"1.2.3\"/></buildMetaData><contentIDs><contentID>777888</contentID></contentIDs><installMetaData><locales>fr_FR,en_US</locales></installMetaData>");
    File.WriteAllText(Path.Combine(mockEaRealPe, "anadius.cfg"), "\"Config2\"\r\n{\r\n    \"Game\"\r\n    {\r\n        \"DenuvoToken\"  \"KEEP_THIS_TOKEN\"\r\n    }\r\n    \"Achievements\"\r\n    {\r\n        \"AchievementsSet\"  \"old\"\r\n        \"AchievementNames\"\r\n        {\r\n            \"1\"  \"Old\"\r\n        }\r\n    }\r\n}\r\n");
    var eaRealDetect = await enablerService.DetectAsync(mockEaRealPe);
    var eaRealPlan = enablerService.BuildPlan(eaRealDetect, AchievementSync.Core.Enabler.EnablerTarget.EaOrigin, gameIdOverride: "777888");
    Check(eaRealPlan.Blockers.Count == 0, "EA real PE plan has zero blockers");
    Check(eaRealPlan.Items.Any(i => i.Kind == "patch-ea-exe"), "EA real PE plan schedules .anadius PE import patch");
    var eaRealApply = enablerService.Apply(eaRealPlan);
    Check(eaRealApply.FilesWritten > 0 && File.Exists(targetEaExe + ".BAK"), "EA real PE backed up to .BAK and patched");
    var postInspection = AchievementSync.Core.Enabler.OriginExePatcher.InspectExecutable(targetEaExe);
    Check(postInspection.ImportsAnadius, "Patched EA PE executable now imports anadius64 DLL");
    var finalCfgText = File.ReadAllText(Path.Combine(mockEaRealPe, "anadius.cfg"));
    Check(finalCfgText.Contains("KEEP_THIS_TOKEN", StringComparison.Ordinal) &&
          !finalCfgText.Contains("\"old\"", StringComparison.Ordinal) &&
          File.Exists(Path.Combine(mockEaRealPe, "anadius.cfg.BAK")),
        "Existing anadius.cfg backed up to .BAK and Achievements block replaced with brace-depth safety");
    // Verify freshly-created config computes real SHA-1 Denuvo hashes
    var mockEaFreshDenuvo = Path.Combine(tempRoot, "MockEaFreshDenuvo");
    Directory.CreateDirectory(Path.Combine(mockEaFreshDenuvo, "__Installer"));
    var freshEaExe = Path.Combine(mockEaFreshDenuvo, "FreshDenuvo.exe");
    File.Copy(sourceRealPe, freshEaExe);
    File.WriteAllText(Path.Combine(mockEaFreshDenuvo, "dbdata.dll"), "denuvo-dll-bytes");
    File.WriteAllText(Path.Combine(mockEaFreshDenuvo, "__Installer", "installerdata.xml"), "<gameTitles><gameTitle locale=\"en_US\">Fresh Denuvo</gameTitle></gameTitles><contentIDs><contentID>999000</contentID></contentIDs>");
    var freshDetect = await enablerService.DetectAsync(mockEaFreshDenuvo);
    var freshPlan = enablerService.BuildPlan(freshDetect, AchievementSync.Core.Enabler.EnablerTarget.EaOrigin, gameIdOverride: "999000");
    enablerService.Apply(freshPlan);
    var freshCfg = File.ReadAllText(Path.Combine(mockEaFreshDenuvo, "anadius.cfg"));
    Check(!freshCfg.Contains("DENUVO_EXE_HASH", StringComparison.Ordinal) && !freshCfg.Contains("DENUVO_DLL_HASH", StringComparison.Ordinal),
        "EA plan computes real SHA-1 DenuvoExeHash and DenuvoDllHash when dbdata.dll is present");

    // ---------------- 1.3.0 release + UI refresh smoke checks ----------------
    var v130DesktopRoot = Path.Combine(repositoryRoot.FullName, "src", "AchievementSync.Desktop");
    var v130CoreRoot = Path.Combine(repositoryRoot.FullName, "src", "AchievementSync.Core");
    var v130DesktopAssembly = typeof(MainViewModel).Assembly;
    var v130CoreAssembly = typeof(GameProfile).Assembly;

    Check(v130DesktopAssembly.GetName().Version is { Major: 1, Minor: 3, Build: 0 }
        && v130DesktopAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion == "1.3.0"
        && v130DesktopAssembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version == "1.3.0",
        "desktop app reports assembly, file and product version 1.3.0");
    Check(v130CoreAssembly.GetName().Version is { Major: 1, Minor: 3, Build: 0 }
        && (v130CoreAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty)
            .StartsWith("1.3.0", StringComparison.Ordinal),
        "core library reports version 1.3.0");

    var v130CoreProject = File.ReadAllText(Path.Combine(v130CoreRoot, "AchievementSync.Core.csproj"));
    var v130DesktopProject = File.ReadAllText(Path.Combine(v130DesktopRoot, "AchievementSync.Desktop.csproj"));
    Check(v130CoreProject.Contains("<Version>1.3.0</Version>", StringComparison.Ordinal)
        && v130CoreProject.Contains("<DebugType>embedded</DebugType>", StringComparison.Ordinal)
        && v130DesktopProject.Contains("<Version>1.3.0</Version>", StringComparison.Ordinal)
        && v130DesktopProject.Contains("<AssemblyVersion>1.3.0.0</AssemblyVersion>", StringComparison.Ordinal)
        && v130DesktopProject.Contains("<FileVersion>1.3.0</FileVersion>", StringComparison.Ordinal)
        && v130DesktopProject.Contains("<InformationalVersion>1.3.0</InformationalVersion>", StringComparison.Ordinal)
        && v130DesktopProject.Contains("<RuntimeIdentifier Condition=\"'$(IsPublishing)' == 'true' and '$(RuntimeIdentifier)' == ''\">win-x64</RuntimeIdentifier>", StringComparison.Ordinal)
        && v130DesktopProject.Contains("<SelfContained Condition=\"'$(IsPublishing)' == 'true'\">true</SelfContained>", StringComparison.Ordinal)
        && v130DesktopProject.Contains("<PublishSingleFile>true</PublishSingleFile>", StringComparison.Ordinal)
        && v130DesktopProject.Contains("<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>", StringComparison.Ordinal)
        && v130DesktopProject.Contains("<DebugType>embedded</DebugType>", StringComparison.Ordinal)
        && v130DesktopProject.Contains("<CopyOutputSymbolsToPublishDirectory>false</CopyOutputSymbolsToPublishDirectory>", StringComparison.Ordinal)
        && !v130CoreProject.Contains("1.2.0", StringComparison.Ordinal)
        && !v130DesktopProject.Contains("1.2.0", StringComparison.Ordinal),
        "both projects declare 1.3.0 and desktop publish defaults to a self-contained single-file win-x64 executable");
    Check(File.ReadAllText(Path.Combine(v130DesktopRoot, "Services", "GitHubReleaseUpdateService.cs")).Contains("AchievementSync-Update/1.3.0", StringComparison.Ordinal)
        && File.ReadAllText(Path.Combine(v130DesktopRoot, "Services", "SteamCommunityCatalogClient.cs")).Contains("AchievementSync/1.3.0", StringComparison.Ordinal),
        "HTTP user-agent strings advertise version 1.3.0");

    System.Xml.Linq.XNamespace v130XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    var v130Documents = new[]
    {
        System.Xml.Linq.XDocument.Load(Path.Combine(v130DesktopRoot, "MainWindow.xaml")),
        System.Xml.Linq.XDocument.Load(Path.Combine(v130DesktopRoot, "Themes", "ModernTheme.xaml")),
        System.Xml.Linq.XDocument.Load(Path.Combine(v130DesktopRoot, "App.xaml")),
        System.Xml.Linq.XDocument.Load(Path.Combine(v130DesktopRoot, "Views", "MappingReviewDialog.xaml"))
    };
    Check(v130Documents.All(document => document.Root is not null),
        "MainWindow.xaml, ModernTheme.xaml, App.xaml and MappingReviewDialog.xaml are well-formed XML");

    var v130KnownKeys = v130Documents
        .SelectMany(document => document.Descendants())
        .Select(element => (string?)element.Attribute(v130XamlNamespace + "Key"))
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal);
    var mappingReviewXaml = File.ReadAllText(Path.Combine(v130DesktopRoot, "Views", "MappingReviewDialog.xaml"));
    var v130UnresolvedResources = new[] { mainWindowXaml, modernThemeXaml, mappingReviewXaml }
        .SelectMany(text => System.Text.RegularExpressions.Regex
            .Matches(text, @"\{(?:StaticResource|DynamicResource)\s+([\w.]+)\}")
            .Select(match => match.Groups[1].Value))
        .Distinct(StringComparer.Ordinal)
        .Where(key => !v130KnownKeys.Contains(key))
        .ToArray();
    Check(v130UnresolvedResources.Length == 0,
        "every resource key used by MainWindow, ModernTheme and Mapping Review resolves (unresolved: " + string.Join(", ", v130UnresolvedResources) + ")");
    Check(mappingReviewXaml.Contains("<Grid Margin=\"22\" Background=\"{StaticResource Modern.CanvasBrush}\"", StringComparison.Ordinal)
        && !System.Text.RegularExpressions.Regex.IsMatch(mappingReviewXaml,
            "<Window\\b[^>]*Background=\"\\{StaticResource Modern\\.CanvasBrush\\}"),
        "Mapping Review resolves its canvas brush from content after its window resources load");

    var v130ImplicitThemeStyles = v130Documents[1].Root!.Elements()
        .Where(element => element.Name.LocalName == "Style" && element.Attribute(v130XamlNamespace + "Key") is null)
        .ToArray();
    Check(v130ImplicitThemeStyles.Length == 0,
        "ModernTheme declares keyed styles only, so no other window or control changes appearance");

    var v130MainWindowCode = File.ReadAllText(Path.Combine(v130DesktopRoot, "MainWindow.xaml.cs"));
    var v130ClickHandlers = System.Text.RegularExpressions.Regex.Matches(mainWindowXaml, "\\bClick=\"(\\w+)\"")
        .Select(match => match.Groups[1].Value)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
    Check(v130ClickHandlers.Length >= 4
        && v130ClickHandlers.All(handler => v130MainWindowCode.Contains(handler + "(", StringComparison.Ordinal)),
        "every Click handler in MainWindow.xaml still exists in the code-behind");
    Check(new[] { "AchievementEnablerHost", "GitHubTokenBox", "RetroAchievementsApiKeyBox" }
            .All(name => mainWindowXaml.Contains("x:Name=\"" + name + "\"", StringComparison.Ordinal)
                && v130MainWindowCode.Contains(name, StringComparison.Ordinal)),
        "named elements used by the code-behind are still declared in MainWindow.xaml");
    Check(mainWindowXaml.Contains("Binding OpenEnablerWizardCommand", StringComparison.Ordinal)
        && System.Text.RegularExpressions.Regex.IsMatch(mainWindowXaml,
            "Binding IsEnablerVisible, Converter=\\{StaticResource BoolToVisibility\\}\\}\"\\s*>\\s*<ContentControl x:Name=\"AchievementEnablerHost\"\\s*/>"),
        "the Achievement Enabler navigation command and host area are untouched by the redesign");

    var v130PropertyNames = v130DesktopAssembly.GetTypes().Concat(v130CoreAssembly.GetTypes())
        .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        .Select(property => property.Name)
        .ToHashSet(StringComparer.Ordinal);
    var v130MissingBindings = System.Text.RegularExpressions.Regex
        .Matches(mainWindowXaml, @"\{Binding\s+(?:Path=)?(?:DataContext\.)?([A-Za-z_][A-Za-z0-9_]*)")
        .Select(match => match.Groups[1].Value)
        .Distinct(StringComparer.Ordinal)
        .Where(name => name != "ViewportHeight" && !v130PropertyNames.Contains(name))
        .ToArray();
    Check(v130MissingBindings.Length == 0,
        "every MainWindow binding names a real view-model property (missing: " + string.Join(", ", v130MissingBindings) + ")");
    Check(new[] { "IsScanning", "IsLibraryEmpty", "IsGameResultsEmpty", "IsGamesVisible", "IsPlatformsVisible", "IsActivityVisible", "IsEnablerVisible", "IsSettingsVisible" }
            .All(name => typeof(MainViewModel).GetProperty(name)?.PropertyType == typeof(bool))
        && typeof(GameCardViewModel).GetProperty("Progress")?.PropertyType == typeof(double),
        "properties read by the redesigned triggers (active nav, skeletons, completion badge) exist with the expected types");

    var v130ThemeOutcomes = RunOnStaThread(() =>
    {
        var outcomes = new List<(bool Passed, string Description)>();
        var themeText = modernThemeXaml.Replace(
            "clr-namespace:AchievementSync.Desktop.Helpers\"",
            "clr-namespace:AchievementSync.Desktop.Helpers;assembly=" + v130DesktopAssembly.GetName().Name + "\"",
            StringComparison.Ordinal);
        using var themeStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(themeText));
        var theme = (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Load(themeStream);

        (string Key, System.Windows.Controls.Control Control)[] samples =
        [
            ("Modern.PrimaryButton", new System.Windows.Controls.Button { Content = "Primary" }),
            ("Modern.SecondaryButton", new System.Windows.Controls.Button { Content = "Secondary" }),
            ("Modern.GhostButton", new System.Windows.Controls.Button { Content = "Ghost" }),
            ("Modern.IconButton", new System.Windows.Controls.Button()),
            ("Modern.NavButton", new System.Windows.Controls.Button { Content = "Nav" }),
            ("Modern.GameCardButton", new System.Windows.Controls.Button { Content = "Card" }),
            ("Modern.TextBox", new System.Windows.Controls.TextBox()),
            ("Modern.SearchBox", new System.Windows.Controls.TextBox()),
            ("Modern.SecretBox", new System.Windows.Controls.PasswordBox()),
            ("Modern.ComboBox", new System.Windows.Controls.ComboBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0 }),
            ("Modern.CheckBox", new System.Windows.Controls.CheckBox { Content = "Option" }),
            ("Modern.Switch", new System.Windows.Controls.CheckBox { Content = "Switch", IsChecked = true }),
            ("Modern.ProgressBar", new System.Windows.Controls.ProgressBar { Maximum = 100, Value = 40 }),
            ("Modern.Expander", new System.Windows.Controls.Expander { Header = "Header", Content = "Body" }),
            ("Modern.ResultList", new System.Windows.Controls.ListBox { Tag = "Empty hint" })
        ];
        foreach (var (key, control) in samples)
        {
            control.Style = (System.Windows.Style)theme[key];
            var applied = control.ApplyTemplate();
            outcomes.Add((applied && control.Template is not null, "Modern theme style " + key + " builds its control template"));
        }

        var clearCommand = AchievementSync.Desktop.Helpers.SearchBoxBehavior.ClearCommand;
        var searchBox = new System.Windows.Controls.TextBox
        {
            Text = "elden ring",
            Style = (System.Windows.Style)theme["Modern.SearchBox"]
        };
        searchBox.ApplyTemplate();
        var clearButton = searchBox.Template.FindName("ClearButton", searchBox) as System.Windows.Controls.Button;
        outcomes.Add((clearButton is not null && object.ReferenceEquals(clearButton.Command, clearCommand),
            "search box clear button is wired to SearchBoxBehavior.ClearCommand"));
        outcomes.Add((clearCommand.CanExecute(null, searchBox), "clear command is available while the search box has text"));
        clearCommand.Execute(null, searchBox);
        outcomes.Add((searchBox.Text.Length == 0, "clear command empties the search text"));
        outcomes.Add((!clearCommand.CanExecute(null, searchBox), "clear command is unavailable once the search box is empty"));

        var focusProbe = new System.Windows.Controls.TextBox();
        AchievementSync.Desktop.Helpers.SearchBoxBehavior.SetIsFocusTarget(focusProbe, true);
        outcomes.Add((AchievementSync.Desktop.Helpers.SearchBoxBehavior.GetIsFocusTarget(focusProbe),
            "search box can be registered as the Ctrl+F focus target"));
        return outcomes;
    });
    foreach (var outcome in v130ThemeOutcomes)
        Check(outcome.Passed, outcome.Description);

    var mappingReviewDialogCreated = RunOnStaThread(() =>
    {
        var dialog = new AchievementSync.Desktop.Views.MappingReviewDialog(
            new MappingReviewViewModel(Array.Empty<SteamAppIdResolution>()));
        return dialog.Content is System.Windows.Controls.Grid grid
            && grid.Background is System.Windows.Media.Brush;
    });
    Check(mappingReviewDialogCreated,
        "Mapping Review dialog initializes on an STA thread with its canvas background resolved");

    // =========================================================================
    // NEW PLATFORMS & FEATURES VERIFICATION (v1.3.0 Complete Platform Support)
    // =========================================================================

    // 1. SteamAppIdResolver: Normalization & Resolution Pipeline
    Check(SteamAppIdResolver.NormalizeUnixSeconds(1700000000L) == 1700000000L,
        "SteamAppIdResolver normalizes standard Unix seconds");
    Check(SteamAppIdResolver.NormalizeUnixSeconds(1700000000000L) == 1700000000L,
        "SteamAppIdResolver normalizes millisecond timestamps to Unix seconds");
    Check(SteamAppIdResolver.NormalizeUnixSeconds(1700000000000000L) == 1700000000L,
        "SteamAppIdResolver normalizes microsecond timestamps to Unix seconds");
    var fileTimeSample = 133456789000000000L;
    var expectedUnixFromFt = DateTimeOffset.FromFileTime(fileTimeSample).ToUnixTimeSeconds();
    Check(SteamAppIdResolver.NormalizeUnixSeconds(fileTimeSample) == expectedUnixFromFt,
        "SteamAppIdResolver normalizes Windows FILETIME ticks to Unix seconds");
    Check(SteamAppIdResolver.NormalizeUnixSeconds(0) == 0 && SteamAppIdResolver.NormalizeUnixSeconds(-50) == 0,
        "SteamAppIdResolver clamps non-positive timestamps to zero");

    Check(SteamAppIdResolver.NormalizeEarnedFlag(true) == true && SteamAppIdResolver.NormalizeEarnedFlag(false) == false,
        "SteamAppIdResolver normalizes boolean earned flags");
    Check(SteamAppIdResolver.NormalizeEarnedFlag(1) == true && SteamAppIdResolver.NormalizeEarnedFlag(0) == false,
        "SteamAppIdResolver normalizes numeric 1/0 earned flags");
    Check(SteamAppIdResolver.NormalizeEarnedFlag("true") == true && SteamAppIdResolver.NormalizeEarnedFlag("1") == true && SteamAppIdResolver.NormalizeEarnedFlag("0") == false,
        "SteamAppIdResolver normalizes string earned flags");

    var resolverTestDir = Path.Combine(tempRoot, "ResolverTests");
    Directory.CreateDirectory(resolverTestDir);
    var resolver = new SteamAppIdResolver(
        titleLookupAsync: (title, _) => Task.FromResult<string?>(string.Equals(title, "Half-Life 2", StringComparison.OrdinalIgnoreCase) ? "220" : null),
        schemaLoader: appId => appId == "220"
            ? [new AchievementRecord("HL2_KILL_BARNACLE", "Barnacle Killer", "Desc", null, null, null)]
            : null);

    // Override priority
    var snapOverride = new PlatformGameSnapshot("epic", "epic-hl2", "Half-Life 2", "dummy-path",
        [new PlatformAchievement("HL2_KILL_BARNACLE", "Barnacle Killer", "Desc", Earned: true, EarnedTimeUnix: 1700000000)]);
    var userOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "epic:epic-hl2", "220" }
    };
    var overrideRes = await resolver.ResolveAsync(snapOverride, userOverrides: userOverrides);
    Check(overrideRes.Confidence == SteamMappingConfidence.Override && overrideRes.ResolvedSteamAppId == "220" && overrideRes.IsOverride,
        "SteamAppIdResolver resolves explicit user overrides with highest priority and Override confidence");

    // Native Steam AppID
    var snapNativeSteam = new PlatformGameSnapshot("steam", "730", "Counter-Strike 2", "dummy-path",
        [new PlatformAchievement("ACH_WIN", "Winner", "Desc", Earned: true, EarnedTimeUnix: 1700000000)]);
    var nativeSteamRes = await resolver.ResolveAsync(snapNativeSteam);
    Check(nativeSteamRes.Confidence == SteamMappingConfidence.Exact && nativeSteamRes.ResolvedSteamAppId == "730",
        "SteamAppIdResolver resolves native Steam AppID with Exact confidence");

    // Embedded uplay-steam.json mapping
    var snapUplayMapped = new PlatformGameSnapshot("uplay", "4688", "Far Cry 3", "dummy-path",
        [new PlatformAchievement("ACH_FC3_1", "Welcome", "Desc", Earned: true, EarnedTimeUnix: 1700000000)]);
    var uplayMappedRes = await resolver.ResolveAsync(snapUplayMapped);
    if (uplayMappedRes.ResolvedSteamAppId is not null)
    {
        Check(uplayMappedRes.Confidence >= SteamMappingConfidence.High,
            "SteamAppIdResolver resolves Ubisoft product IDs using embedded uplay-steam.json mapping");
    }

    // Title lookup fallback
    var snapTitleLookup = new PlatformGameSnapshot("gog", "123456", "Half-Life 2 (Game of the Year Edition)", "dummy-path",
        [new PlatformAchievement("HL2_KILL_BARNACLE", "Barnacle Killer", "Desc", Earned: true, EarnedTimeUnix: 1700000000)]);
    var titleRes = await resolver.ResolveAsync(snapTitleLookup);
    Check(titleRes.ResolvedSteamAppId == "220" && titleRes.Confidence >= SteamMappingConfidence.Medium,
        "SteamAppIdResolver cleans edition suffixes and resolves Steam AppIDs via title lookup");

    // Schema matching and discarding IDs absent from Steam schema
    var snapWithExtraneous = new PlatformGameSnapshot("epic", "epic-hl2", "Half-Life 2", "dummy-path",
        [
            new PlatformAchievement("HL2_KILL_BARNACLE", "Barnacle Killer", "Desc", Earned: true, EarnedTimeUnix: 1700000000),
            new PlatformAchievement("EPIC_EXCLUSIVE_ACH", "Epic Only", "Desc", Earned: true, EarnedTimeUnix: 1700000000)
        ]);
    var schemaRes = await resolver.ResolveAsync(snapWithExtraneous, userOverrides: userOverrides, explicitSteamSchema:
        [new AchievementRecord("HL2_KILL_BARNACLE", "Barnacle Killer", "Desc", null, null, null)]);
    Check(schemaRes.NormalizedAchievements.Count == 1
        && schemaRes.NormalizedAchievements[0].Id == "HL2_KILL_BARNACLE"
        && schemaRes.DiscardedCount == 1,
        "SteamAppIdResolver discards achievement IDs absent from the official Steam schema when one is present");

    // Blacklist propagation
    var blacklistedRes = await resolver.ResolveAsync(snapOverride, userOverrides: userOverrides,
        blacklistedKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "epic:epic-hl2" });
    Check(blacklistedRes.IsBlacklisted, "SteamAppIdResolver propagates blacklist status correctly");

    // 2. Xbox Network / Entra Device Code Flow & Stored Credential
    var xboxCred = new XboxStoredCredential("ms-tok", "ms-ref", "consumers", DateTimeOffset.UtcNow.AddHours(4), "xsts-tok-xyz", "123456", "2533274890", "GamerX", DateTimeOffset.UtcNow.AddHours(2));
    var serializedXboxCred = xboxCred.Serialize();
    var deserializedXboxCred = XboxStoredCredential.Parse(serializedXboxCred);
    Check(deserializedXboxCred is not null
        && deserializedXboxCred.XstsToken == "xsts-tok-xyz"
        && deserializedXboxCred.Uhs == "123456"
        && deserializedXboxCred.Xuid == "2533274890"
        && deserializedXboxCred.Gamertag == "GamerX"
        && !deserializedXboxCred.NeedsRefresh(),
        "XboxStoredCredential survives secure serialization and parses XSTS token, UHS hash, and XUID");

    // Xbox PC: MicrosoftGame.config parsing
    var mockXboxDir = Path.Combine(tempRoot, "MockXboxPc");
    Directory.CreateDirectory(mockXboxDir);
    var sampleGameConfig = """
        <?xml version="1.0" encoding="utf-8"?>
        <Game configVersion="1">
          <Identity Name="HaloInfinite" Publisher="CN=Microsoft Corporation" Version="1.0.0.0" />
          <TitleId>1234ABCD</TitleId>
          <ExecutableList>
            <Executable Name="HaloInfinite.exe" Id="GameExe" TargetDeviceFamily="PC" />
          </ExecutableList>
          <ShellVisuals DisplayName="Halo Infinite" />
        </Game>
        """;
    File.WriteAllText(Path.Combine(mockXboxDir, "MicrosoftGame.config"), sampleGameConfig);
    var parsedConfig = XboxPcDataAdapter.TryReadMicrosoftGameConfig(Path.Combine(mockXboxDir, "MicrosoftGame.config"));
    Check(parsedConfig is not null
        && parsedConfig.TitleId == "1234ABCD"
        && parsedConfig.DisplayName == "Halo Infinite"
        && parsedConfig.ExecutableName == "HaloInfinite.exe",
        "XboxPcDataAdapter inspects MicrosoftGame.config and extracts TitleId, DisplayName, and Executable");

    // Xbox live achievements API JSON response parsing
    var liveXboxAchievementsJson = """
        {
          "achievements": [
            {
              "id": "1",
              "name": "First Blood",
              "description": "Defeat an enemy",
              "progressState": "Achieved",
              "progression": { "timeUnlocked": "2024-05-10T14:30:00Z" }
            },
            {
              "id": "2",
              "name": "Sharpshooter",
              "description": "Score a headshot",
              "progressState": "NotStarted",
              "progression": { "timeUnlocked": null }
            }
          ]
        }
        """;
    var parsedLiveAchievements = XboxPcDataAdapter.ParseAchievementsResponseJson(liveXboxAchievementsJson);
    Check(parsedLiveAchievements.Count == 2
        && parsedLiveAchievements[0].Id == "1"
        && parsedLiveAchievements[0].Earned
        && parsedLiveAchievements[0].EarnedTimeUnix > 0
        && !parsedLiveAchievements[1].Earned,
        "XboxPcDataAdapter correctly parses official Xbox Live TitleHub/Achievements API payload");

    // 4. EA Desktop: EADesktopVerbose.log Parsing
    var eaLogDir = Path.Combine(tempRoot, "MockEaLogs");
    Directory.CreateDirectory(eaLogDir);
    var sampleEaLogText = """
        [2024-06-01 10:00:00.123] [Info] Starting EA Desktop
        [2024-06-01 10:05:00.456] [Info] Achievement awarded: contentId="Origin.OFR.50.0001234" achievementId="ACH_SPEED_RUN" timestamp="1717236300"
        [2024-06-01 10:06:00.789] [Info] {"event":"achievement_grant","productId":"Origin.OFR.50.0001234","achievementName":"ACH_BOSS_DOWN","unlocked":true,"unlockTime":1717236360}
        """;
    File.WriteAllText(Path.Combine(eaLogDir, "EADesktopVerbose.log"), sampleEaLogText);
    var parsedEaSnapshots = EaDesktopDataAdapter.ParseVerboseLog(Path.Combine(eaLogDir, "EADesktopVerbose.log"));
    Check(parsedEaSnapshots.Count > 0
        && parsedEaSnapshots.Any(s => s.GameId == "Origin.OFR.50.0001234"
            && s.Achievements.Any(a => a.Id == "ACH_SPEED_RUN" && a.Earned && a.EarnedTimeUnix == 1717236300L)
            && s.Achievements.Any(a => a.Id == "ACH_BOSS_DOWN" && a.Earned && a.EarnedTimeUnix == 1717236360L)),
        "EaDesktopDataAdapter parses achievement events from EADesktopVerbose.log text and JSON events");

    // 5. Ubisoft Connect: RUNE achievements.cfg & LumaPlay Enabler Runtime
    var sampleRuneCfgIni = """
        [ACH_LEVEL_10]
        Earned=1
        Timestamp=1710000000

        [ACH_LEVEL_20]
        Earned=0
        Timestamp=0
        """;
    var parsedRuneIni = UplayDataAdapter.ParseRuneAchievementsCfgText(sampleRuneCfgIni);
    Check(parsedRuneIni.Count == 2
        && parsedRuneIni.Any(a => a.Id == "ACH_LEVEL_10" && a.Earned && a.EarnedTimeUnix == 1710000000L)
        && parsedRuneIni.Any(a => a.Id == "ACH_LEVEL_20" && !a.Earned),
        "UplayDataAdapter parses RUNE achievements.cfg INI-section format");

    var sampleRuneCfgJson = """
        {
          "ACH_COMPLETION": { "earned": true, "timestamp": 1710000050 },
          "ACH_COLLECTOR": { "earned": false, "timestamp": 0 }
        }
        """;
    var parsedRuneJson = UplayDataAdapter.ParseRuneAchievementsCfgText(sampleRuneCfgJson);
    Check(parsedRuneJson.Count == 2
        && parsedRuneJson.Any(a => a.Id == "ACH_COMPLETION" && a.Earned && a.EarnedTimeUnix == 1710000050L),
        "UplayDataAdapter parses RUNE achievements.cfg JSON format");

    // LumaPlay embedded runtime extraction and plan generation
    var lumaPath = EmbeddedLumaPlayRuntime.EnsureReady();
    Check(EmbeddedLumaPlayRuntime.IsReady
        && File.Exists(Path.Combine(lumaPath, "x64", "LumaPlay.exe"))
        && File.Exists(Path.Combine(lumaPath, "x86", "LumaPlay.exe")),
        "EmbeddedLumaPlayRuntime auto-extracts official LumaPlay 2.2.6 x64 and x86 runtimes");
    var mockUplayGameDir = Path.Combine(tempRoot, "MockUplayGame");
    Directory.CreateDirectory(mockUplayGameDir);
    var uplayGameExe = Path.Combine(mockUplayGameDir, "FarCryGame.exe");
    File.WriteAllText(uplayGameExe, "mock-game-binary");
    File.WriteAllText(Path.Combine(mockUplayGameDir, "uplay_r1_loader64.dll"), "mock-uplay-dll");
    var enablerServiceInstance = new AchievementEnablerService(null, Path.Combine(tempRoot, "Metadata"));
    var uplayDetect = await enablerServiceInstance.DetectAsync(mockUplayGameDir);
    Check(uplayDetect.SuggestedTarget == EnablerTarget.UplayLumaPlay || uplayDetect.Evidence.Count > 0,
        "AchievementEnablerService detects Uplay LumaPlay target for Uplay games");
    var lumaPlan = enablerServiceInstance.BuildPlan(uplayDetect, EnablerTarget.UplayLumaPlay, gameIdOverride: "4688");
    Check(lumaPlan.Blockers.Count == 0 && lumaPlan.Items.Any(i => i.Kind == "lumaplay-wrapper"),
        "AchievementEnablerService builds complete LumaPlay plan with zero blockers");

    // 6. PlayStation (ShadPS4 modern trophy layout & Kyty PS5 modern format)
    var ps4Root = Path.Combine(tempRoot, "MockShadPS4");
    var trophySchemaDir = Path.Combine(ps4Root, "trophy", "NPWR00123_00", "Xml");
    Directory.CreateDirectory(trophySchemaDir);
    var sampleTropXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <trophyconf version="1.1">
          <npcommid>NPWR00123_00</npcommid>
          <trophies count="2">
            <trophy id="0" ttype="P">
              <name>Platinum Hunter</name>
              <detail>Collect all trophies</detail>
            </trophy>
            <trophy id="1" ttype="B">
              <name>First Step</name>
              <detail>Begin the journey</detail>
            </trophy>
          </trophies>
        </trophyconf>
        """;
    File.WriteAllText(Path.Combine(trophySchemaDir, "TROP.XML"), sampleTropXml);
    var userProgressDir = Path.Combine(ps4Root, "home", "00000001", "trophy");
    Directory.CreateDirectory(userProgressDir);
    var sampleUserProgressXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <trophy_status npcommid="NPWR00123_00">
          <trophy id="1" unlocked="1" unlocked_timestamp="1705000000" />
        </trophy_status>
        """;
    File.WriteAllText(Path.Combine(userProgressDir, "NPWR00123_00.xml"), sampleUserProgressXml);

    var shadPs4Options = PlatformSourceOptions.CreateDefault();
    shadPs4Options.Ps4Root = ps4Root;
    var ps4Adapter = new PlayStationDataAdapter();
    var ps4Collect = await ps4Adapter.CollectAsync(shadPs4Options);
    Check(ps4Collect.Games.Count > 0
        && ps4Collect.Games.Any(g => g.GameId == "NPWR00123_00"
            && g.Achievements.Count == 2
            && g.Achievements.Any(a => a.Id == "1" && a.Earned && a.EarnedTimeUnix == 1705000000L)
            && g.Achievements.Any(a => a.Id == "0" && !a.Earned)),
        "PlayStationDataAdapter parses modern ShadPS4 trophy/NPWR/Xml schema and merges home/<user>/trophy progress");

    // Kyty PS5 modern JSON format
    var kytyRoot = Path.Combine(tempRoot, "MockKytyPS5");
    var kytyTrophiesDir = Path.Combine(kytyRoot, "_Trophies", "CUSA99999");
    Directory.CreateDirectory(kytyTrophiesDir);
    var sampleKytyJson = """
        {
          "trophies": [
            {
              "trophy_id": 0,
              "type": "bronze",
              "unlocked": true,
              "unlocked_timestamp": 1706000000
            },
            {
              "trophy_id": 1,
              "type": "silver",
              "unlocked": false,
              "unlocked_timestamp": 0
            }
          ]
        }
        """;
    File.WriteAllText(Path.Combine(kytyTrophiesDir, "trophies_user0_0.json"), sampleKytyJson);
    var kytyOptions = PlatformSourceOptions.CreateDefault();
    kytyOptions.Ps5Root = kytyRoot;
    var kytyCollect = await ps4Adapter.CollectAsync(kytyOptions);
    Check(kytyCollect.Games.Count > 0
        && kytyCollect.Games.Any(g => g.GameId == "CUSA99999"
            && g.Achievements.Any(a => a.Id == "0" && a.Earned && a.EarnedTimeUnix == 1706000000L)),
        "PlayStationDataAdapter parses modern KytyPS5 _Trophies JSON trophy files");

    // 7. Recomp Ports Adapter (LADXHD, PS3Recomp, PS4, Xbox 360 rules)
    var recompRoot = Path.Combine(tempRoot, "MockRecomps");
    var ladxhdDir = Path.Combine(recompRoot, "LADXHD");
    Directory.CreateDirectory(ladxhdDir);
    var sampleLadxhdJson = """
        {
          "ACH_SWORD": { "earned": true, "earned_time": 1707000000 },
          "ACH_SHIELD": { "earned": false, "earned_time": 0 }
        }
        """;
    File.WriteAllText(Path.Combine(ladxhdDir, "achievements.json"), sampleLadxhdJson);
    var ps3RecompDir = Path.Combine(recompRoot, "PS3Game");
    Directory.CreateDirectory(ps3RecompDir);
    File.WriteAllText(Path.Combine(ps3RecompDir, "ps3recomp_achievements.json"), sampleLadxhdJson);

    var recompOptions = PlatformSourceOptions.CreateDefault();
    recompOptions.RecompRoot = recompRoot;
    recompOptions.LadxhdRoot = ladxhdDir;
    var recompAdapter = new RecompDataAdapter(recompOptions);
    var recompCollect = await recompAdapter.CollectAsync(recompOptions);
    Check(recompCollect.Games.Count >= 2
        && recompCollect.Games.Any(g => g.Platform == "recomp" && g.GameId == "LADXHD" && g.Achievements.Any(a => a.Id == "ACH_SWORD" && a.Earned))
        && recompCollect.Games.Any(g => g.Platform == "recomp" && g.GameId == "PS3Game" && g.Achievements.Any(a => a.Id == "ACH_SWORD" && a.Earned)),
        "RecompDataAdapter collects LADXHD and PS3Recomp achievements seamlessly");

    var unchartedPort = Path.Combine(recompRoot, "Uncharted Collection");
    Directory.CreateDirectory(unchartedPort);
    File.WriteAllText(Path.Combine(unchartedPort, "run.py"), "# launcher fixture");
    foreach (var game in new[] { "CUSA00001", "CUSA00002", "CUSA00003" })
    {
        var xmlDir = Path.Combine(unchartedPort, game, "TrophyFiles", "Xml");
        var iconDir = Path.Combine(unchartedPort, game, "TrophyFiles", "Icons");
        Directory.CreateDirectory(xmlDir);
        Directory.CreateDirectory(iconDir);
        File.WriteAllText(Path.Combine(xmlDir, "TROP.XML"), $"""
            <trophyconf><npcommid>NPWR00001_00</npcommid><trophies>
              <trophy id="3" ttype="G" unlockstate="0" timestamp="0"><name>Chapter</name><detail>Finish a chapter</detail></trophy>
            </trophies></trophyconf>
            """);
        File.WriteAllText(Path.Combine(iconDir, "TROP003.PNG"), "icon fixture");
    }
    File.WriteAllText(Path.Combine(unchartedPort, "launcher.log"),
        "2026-11-27T12:34:56Z CUSA00002 unlocked trophy: 3");
    var ps4RecompCollect = await recompAdapter.CollectAsync(recompOptions);
    var unchartedGames = ps4RecompCollect.Games.Where(game => game.Version == "ps4recomp").ToArray();
    Check(unchartedGames.Length == 3
        && unchartedGames.Select(game => game.GameId).OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(
            new[] { "CUSA00001", "CUSA00002", "CUSA00003" }),
        "PS4 Recomp keeps separate trophy lists as separate configs for collection ports");
    var unchartedTwo = unchartedGames.Single(game => game.GameId == "CUSA00002");
    Check(unchartedTwo.Achievements.Single().Earned
        && unchartedTwo.Achievements.Single().EarnedTimeUnix == 1795782896
        && unchartedTwo.Achievements.Single().IconPath?.EndsWith("TROP003.PNG", StringComparison.OrdinalIgnoreCase) == true,
        "PS4 Recomp merges launcher unlock logs, normalizes ISO timestamps, and keeps local trophy icons");
    Check(!unchartedGames.Single(game => game.GameId == "CUSA00001").Achievements.Single().Earned,
        "PS4 Recomp does not leak an unlock from one collection trophy list into another");

    var encryptedPs4Port = Path.Combine(recompRoot, "Encrypted PS4 Fixture");
    var encryptedPs4Game = Path.Combine(recompRoot, "Encrypted PS4 Game Data", "CUSA20001");
    var encryptedPs4TrophyDir = Path.Combine(encryptedPs4Game, "sce_sys", "trophy");
    Directory.CreateDirectory(encryptedPs4Port);
    Directory.CreateDirectory(encryptedPs4TrophyDir);
    File.WriteAllText(Path.Combine(encryptedPs4Port, "run.py"), "# launcher fixture");
    File.WriteAllText(Path.Combine(encryptedPs4Port, "settings.json"), "{\"game_dir\":\"../Encrypted PS4 Game Data/CUSA20001\"}");
    var npcommid = "NPWR12345_00";
    var rawTrophyXml = "<trophyconf><title-name>Encrypted Trophy Fixture</title-name><trophies>"
        + "<trophy id=\"0\" ttype=\"B\" hidden=\"no\"><name>First trophy</name><detail>Fixture description</detail></trophy>"
        + "</trophies></trophyconf>";
    var encryptedEsfm = EncryptPs4EsfmFixture(npcommid, rawTrophyXml);
    var encryptedTrpPath = Path.Combine(encryptedPs4TrophyDir, "trophy00.trp");
    File.WriteAllBytes(encryptedTrpPath, BuildPs4TrpFixture("TROP_01.ESFM", encryptedEsfm));
    File.WriteAllBytes(Path.Combine(encryptedPs4Game, "sce_sys", "npbind.dat"), BuildPs4NpbindFixture(npcommid, 0));
    var rawArchive = Ps4TrophyArchiveReader.Read(encryptedTrpPath, Path.Combine(encryptedPs4Game, "sce_sys", "npbind.dat"));
    Check(rawArchive.NpCommunicationId == npcommid && rawArchive.Title == "Encrypted Trophy Fixture"
        && rawArchive.Achievements.Single().Id == "0"
        && rawArchive.Achievements.Single().Name == "First trophy"
        && rawArchive.Achievements.Single().TrophyType == "bronze",
        "PS4 Recomp decrypts bounded TRP/ESFM trophy metadata through its npbind slot");
    var rawPs4Collect = await recompAdapter.CollectAsync(recompOptions);
    var rawPs4Game = rawPs4Collect.Games.Single(game => game.GameId == "CUSA20001");
    Check(rawPs4Game.Version == "ps4recomp" && rawPs4Game.Title == "Encrypted Trophy Fixture"
        && rawPs4Game.Achievements.Single().Name == "First trophy",
        "PS4 Recomp follows a launcher game_dir to an external original-game trophy set");

    var overridePs4Port = Path.Combine(recompRoot, "Trophy Key Override Port");
    var overridePs4Game = Path.Combine(recompRoot, "Trophy Key Override Data", "CUSA20002");
    var overrideTrophyDir = Path.Combine(overridePs4Game, "sce_sys", "trophy");
    var overrideKey = "0A1B2C3D4E5F60718293A4B5C6D7E8F9";
    Directory.CreateDirectory(overridePs4Port);
    Directory.CreateDirectory(overrideTrophyDir);
    File.WriteAllText(Path.Combine(overridePs4Port, "run.py"), "# launcher fixture");
    File.WriteAllText(Path.Combine(overridePs4Port, "settings.json"), "{\"game_dir\":\"../Trophy Key Override Data/CUSA20002\"}");
    File.WriteAllText(Path.Combine(overridePs4Port, "trophy.key"), overrideKey);
    var overrideEsfm = EncryptPs4EsfmFixture(npcommid, rawTrophyXml, overrideKey);
    var overrideTrpPath = Path.Combine(overrideTrophyDir, "trophy00.trp");
    var overrideBindingPath = Path.Combine(overridePs4Game, "sce_sys", "npbind.dat");
    File.WriteAllBytes(overrideTrpPath, BuildPs4TrpFixture("TROP_01.ESFM", overrideEsfm));
    File.WriteAllBytes(overrideBindingPath, BuildPs4NpbindFixture(npcommid, 0));
    var overrideArchive = Ps4TrophyArchiveReader.Read(overrideTrpPath, overrideBindingPath,
        Path.Combine(overridePs4Port, "trophy.key"));
    Check(overrideArchive.Title == "Encrypted Trophy Fixture",
        "PS4 TRP reader lets a valid local trophy.key override the built-in retail key");
    var overridePs4Collect = await recompAdapter.CollectAsync(recompOptions);
    Check(overridePs4Collect.Games.Any(game => game.GameId == "CUSA20002"
        && game.Achievements.Single().Name == "First trophy"),
        "PS4 Recomp finds trophy.key beside the launcher when importing encrypted original-game data");

    var malformedTrpPath = Path.Combine(encryptedPs4TrophyDir, "trophy01.trp");
    File.WriteAllBytes(malformedTrpPath, new byte[96]);
    try
    {
        Ps4TrophyArchiveReader.Read(malformedTrpPath, Path.Combine(encryptedPs4Game, "sce_sys", "npbind.dat"));
        Check(false, "PS4 TRP reader rejects invalid magic instead of accepting corrupt data");
    }
    catch (InvalidDataException)
    {
        Check(true, "PS4 TRP reader rejects invalid magic instead of accepting corrupt data");
    }
    var malformedPs4XmlPath = Path.Combine(encryptedPs4TrophyDir, "trophy02.trp");
    File.WriteAllBytes(malformedPs4XmlPath, BuildPs4TrpFixture("TROP.SFM", Encoding.UTF8.GetBytes("<trophyconf>")));
    try
    {
        Ps4TrophyArchiveReader.Read(malformedPs4XmlPath);
        Check(false, "PS4 TRP reader rejects malformed embedded XML as a source diagnostic");
    }
    catch (InvalidDataException)
    {
        Check(true, "PS4 TRP reader rejects malformed embedded XML as a source diagnostic");
    }

    var pythonPs4Port = Path.Combine(recompRoot, "Python Launcher PS4 Fixture");
    var pythonPs4Game = Path.Combine(recompRoot, "Python PS4 Game Data", "CUSA30001");
    Directory.CreateDirectory(pythonPs4Port);
    var pythonPs4XmlDir = Path.Combine(pythonPs4Game, "TrophyFiles", "Xml");
    Directory.CreateDirectory(pythonPs4XmlDir);
    File.WriteAllText(Path.Combine(pythonPs4Port, "run.py"), "GAME_DIR = \"../Python PS4 Game Data/CUSA30001\"\n");
    File.WriteAllText(Path.Combine(pythonPs4XmlDir, "TROP.XML"),
        "<trophyconf><trophies><trophy id=\"1\" ttype=\"S\" unlockstate=\"0\"><name>Python path trophy</name></trophy></trophies></trophyconf>");
    var pythonPathCollect = await recompAdapter.CollectAsync(recompOptions);
    Check(pythonPathCollect.Games.Any(game => game.GameId == "CUSA30001"
        && game.Achievements.Any(item => item.Id == "1" && item.Name == "Python path trophy")),
        "PS4 Recomp reads a static GAME_DIR from run.py without executing the launcher");

    var unknownUnlockPort = Path.Combine(recompRoot, "Unknown Trophy Port");
    var unknownUnlockXml = Path.Combine(unknownUnlockPort, "TrophyFiles", "Xml");
    Directory.CreateDirectory(unknownUnlockXml);
    File.WriteAllText(Path.Combine(unknownUnlockPort, "run.py"), "# launcher fixture");
    File.WriteAllText(Path.Combine(unknownUnlockXml, "TROP.XML"),
        "<trophyconf><trophies><trophy id=\"1\" ttype=\"B\" unlockstate=\"0\"><name>Known trophy</name></trophy></trophies></trophyconf>");
    File.WriteAllText(Path.Combine(unknownUnlockPort, "launcher.log"), "unlocked trophy: 999");
    var unknownUnlockCollect = await recompAdapter.CollectAsync(recompOptions);
    var unknownUnlockGame = unknownUnlockCollect.Games.Single(game => game.GameId == "Unknown Trophy Port");
    Check(unknownUnlockGame.Achievements.Count == 1 && unknownUnlockGame.Achievements[0].Id == "1"
        && unknownUnlockCollect.Messages.Any(message => message.Contains("absent from the trophy schema", StringComparison.OrdinalIgnoreCase)),
        "PS4 Recomp ignores launcher unlock IDs that are absent from the discovered trophy schema");

    var ambiguousPort = Path.Combine(recompRoot, "Ambiguous Collection");
    Directory.CreateDirectory(ambiguousPort);
    File.WriteAllText(Path.Combine(ambiguousPort, "run.py"), "# launcher fixture");
    foreach (var game in new[] { "CUSA10001", "CUSA10002" })
    {
        var xmlDir = Path.Combine(ambiguousPort, game, "TrophyFiles", "Xml");
        Directory.CreateDirectory(xmlDir);
        File.WriteAllText(Path.Combine(xmlDir, "TROP.XML"), "<trophyconf><trophies><trophy id=\"1\" unlockstate=\"0\"><name>One</name></trophy></trophies></trophyconf>");
    }
    File.WriteAllText(Path.Combine(ambiguousPort, "launcher.log"), "2026-11-27T12:34:56Z unlocked trophy: 1");
    var ambiguousCollect = await recompAdapter.CollectAsync(recompOptions);
    Check(ambiguousCollect.Games.Count(game => game.Version == "ps4recomp"
            && game.GameId is "CUSA10001" or "CUSA10002") == 2
        && ambiguousCollect.Games.All(game => game.GameId != "Ambiguous Collection")
        && ambiguousCollect.Messages.Any(message => message.Contains("ambiguous launcher log", StringComparison.OrdinalIgnoreCase)),
        "PS4 Recomp skips launcher logs that cannot be assigned safely to one list in a multi-game collection");
    Check(Xbox360DataAdapter.RecompRules.Length == 16,
        "Xbox360DataAdapter declares all 16 official game-specific Xbox 360 recomp rules");

    // 8. Exporter, Markers, and Multi-Source Idempotent Merging
    var resSource1 = new SteamAppIdResolution(
        "epic", "epic-r1", "Game Title", "730", SteamMappingConfidence.Exact, "Exact match", false, false,
        [
            new PlatformAchievement("ACH_1", Earned: true, EarnedTimeUnix: 1700001000L),
            new PlatformAchievement("ACH_2", Earned: false, EarnedTimeUnix: 0L)
        ], 0);

    var resSource2 = new SteamAppIdResolution(
        "playstation", "CUSA00730", "Game Title", "730", SteamMappingConfidence.High, "High match", false, false,
        [
            new PlatformAchievement("ACH_1", Earned: false, EarnedTimeUnix: 0L),
            new PlatformAchievement("ACH_2", Earned: true, EarnedTimeUnix: 1700002000L),
            new PlatformAchievement("ACH_3", Earned: true, EarnedTimeUnix: 1700003000L)
        ], 0);

    var resBlacklisted = new SteamAppIdResolution(
        "uplay", "uplay-black", "Blacklisted Game", "570", SteamMappingConfidence.High, "High match", false, true,
        [new PlatformAchievement("DOTA_ACH_1", Earned: true, EarnedTimeUnix: 1700000500L)], 0);

    var exportFiles = AchievementExportPlanner.BuildFromResolutions(
        [resSource1, resSource2, resBlacklisted],
        includePlatformMarkers: true,
        includeBlacklistMarker: true);

    // Verify exact contract for AppID 730
    var app730JsonFile = exportFiles.FirstOrDefault(f => f.RelativePath == "AppID/730/achievements.json");
    Check(app730JsonFile is not null, "AchievementExportPlanner generates AppID/730/achievements.json");
    using var app730Doc = JsonDocument.Parse(app730JsonFile!.Json);
    var rootObj = app730Doc.RootElement;
    Check(rootObj.TryGetProperty("ACH_1", out var a1) && a1.GetProperty("earned").GetBoolean() && a1.GetProperty("earned_time").GetInt64() == 1700001000L,
        "Multi-source union preserves Source 1 unlock timestamp for ACH_1");
    Check(rootObj.TryGetProperty("ACH_2", out var a2) && a2.GetProperty("earned").GetBoolean() && a2.GetProperty("earned_time").GetInt64() == 1700002000L,
        "Multi-source union preserves Source 2 unlock timestamp for ACH_2");
    Check(rootObj.TryGetProperty("ACH_3", out var a3) && a3.GetProperty("earned").GetBoolean() && a3.GetProperty("earned_time").GetInt64() == 1700003000L,
        "Multi-source union includes disjoint achievement ACH_3 from Source 2");

    // Verify platform marker files
    Check(exportFiles.Any(f => f.RelativePath == "AppID/730/Epic.platform" && f.Json.Length == 0),
        "AchievementExportPlanner adds empty Epic.platform marker for Epic source");
    Check(exportFiles.Any(f => f.RelativePath == "AppID/730/PlayStation.platform" && f.Json.Length == 0),
        "AchievementExportPlanner adds empty PlayStation.platform marker for PlayStation source");
    Check(exportFiles.Any(f => f.RelativePath == "AppID/570/blacklist" && f.Json.Length == 0),
        "AchievementExportPlanner adds empty blacklist marker for blacklisted game");

    var ps4RecompResolution = resSource2 with { SourceVersion = "ps4recomp" };
    var ps4RecompMarkerFiles = AchievementExportPlanner.BuildFromResolutions([ps4RecompResolution]);
    Check(ps4RecompMarkerFiles.Any(f => f.RelativePath == "AppID/730/PS4Recomp.platform" && f.Json.Length == 0),
        "AchievementExportPlanner identifies PS4 Recomp sources with their own platform marker");

    // Proving existing unlocks and larger valid files are never lost or downgraded
    var existingLargerJson = """
        {
          "ACH_1": { "earned": true, "earned_time": 1700001000 },
          "ACH_LEGACY_ONLY": { "earned": true, "earned_time": 1699999999 }
        }
        """;
    var existingDict = new Dictionary<string, string> { { "730", existingLargerJson } };
    var exportWithExisting = AchievementExportPlanner.BuildFromResolutions([resSource2], existingAppIdJson: existingDict);
    var mergedWithExisting = exportWithExisting.First(f => f.RelativePath == "AppID/730/achievements.json");
    using var mergedDoc = JsonDocument.Parse(mergedWithExisting.Json);
    Check(mergedDoc.RootElement.TryGetProperty("ACH_LEGACY_ONLY", out var leg) && leg.GetProperty("earned").GetBoolean(),
        "AchievementExportPlanner never loses existing unlocks absent from incoming partial snapshot");
    Check(mergedDoc.RootElement.TryGetProperty("ACH_1", out var m1) && m1.GetProperty("earned").GetBoolean() && m1.GetProperty("earned_time").GetInt64() == 1700001000L,
        "AchievementExportPlanner never downgrades existing earned state or timestamps");

    var platformMetadata = new AchievementExportFile(
        "AppID/730/achievements.json", "730", 1,
        """{"BASE_ACHIEVEMENT":{"earned":true,"earned_time":1600000000}}""");
    var platformSnapshots = new[]
    {
        new PlatformGameSnapshot("epic", "epic-ns", "Mapped Platform Game", "", [
            new PlatformAchievement("ACH_1", Earned: true, EarnedTimeUnix: 1700001000),
            new PlatformAchievement("UNKNOWN_STATE", Earned: true, EarnedKnown: false)
        ]),
        new PlatformGameSnapshot("gog", "gog-42", "Mapped Platform Game", "", [
            new PlatformAchievement("ACH_1", Earned: false),
            new PlatformAchievement("ACH_2", Earned: true, EarnedTimeUnix: 1700002000)
        ]),
        new PlatformGameSnapshot("recomp", "CUSA00730", "Mapped Platform Game", "", [
            new PlatformAchievement("ACH_3", Earned: true, EarnedTimeUnix: 1700003000)
        ], "ps4recomp"),
        new PlatformGameSnapshot("epic", "zero-unlock-game", "No unlocks", "", [
            new PlatformAchievement("LOCKED_ONLY", Earned: false)
        ])
    };
    var exportResolver = new SteamAppIdResolver((_, _) => Task.FromResult<string?>("730"));
    var builtPlatformExports = await PlatformAchievementExportBuilder.BuildAsync(
        [platformMetadata, new AchievementExportFile("AppID/731/achievements.json", "731", 1,
            """{"LOCKED":{"earned":false,"earned_time":0}}""")],
        platformSnapshots, exportResolver,
        new Dictionary<string, string> { ["epic:zero-unlock-game"] = "731" });
    var mappedAchievementJson = builtPlatformExports.Files.Single(f => f.RelativePath == "AppID/730/achievements.json");
    using var mappedDoc = JsonDocument.Parse(mappedAchievementJson.Json);
    Check(mappedDoc.RootElement.TryGetProperty("BASE_ACHIEVEMENT", out var baseState)
        && baseState.GetProperty("earned").GetBoolean(),
        "platform exporter seeds from existing AppID metadata so previous unlocks stay present");
    Check(mappedDoc.RootElement.TryGetProperty("ACH_1", out var crossSourceState)
        && crossSourceState.GetProperty("earned").GetBoolean()
        && crossSourceState.GetProperty("earned_time").GetInt64() == 1700001000,
        "production platform exporter unions multiple sources without downgrading earned state or time");
    Check(mappedDoc.RootElement.TryGetProperty("ACH_2", out var secondSourceState)
        && secondSourceState.GetProperty("earned").GetBoolean()
        && mappedDoc.RootElement.TryGetProperty("ACH_3", out var recompState)
        && recompState.GetProperty("earned").GetBoolean(),
        "production platform exporter includes disjoint achievement unlocks from each mapped source");
    Check(!mappedDoc.RootElement.TryGetProperty("UNKNOWN_STATE", out _),
        "production platform exporter omits achievement states that the source did not know");
    Check(new[] { "Epic.platform", "GOG.platform", "PS4Recomp.platform" }
            .All(name => builtPlatformExports.Files.Any(f => f.RelativePath == "AppID/730/" + name && f.Json.Length == 0)),
        "production platform exporter emits empty source markers for non-Steam platforms");
    Check(!builtPlatformExports.Files.Any(file => file.AppId == "731"),
        "GitHub platform export omits achievements and markers for mapped games with no unlocked achievements");
    var repeatedPlatformExports = await PlatformAchievementExportBuilder.BuildAsync(
        builtPlatformExports.Files, platformSnapshots, exportResolver,
        new Dictionary<string, string> { ["epic:zero-unlock-game"] = "731" });
    Check(repeatedPlatformExports.Files.Count == builtPlatformExports.Files.Count
        && repeatedPlatformExports.Files.All(file => builtPlatformExports.Files.Any(original =>
            original.RelativePath == file.RelativePath && original.Json == file.Json)),
        "production platform export is idempotent for identical source snapshots");
    var desktopMainViewModelSource = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "src", "AchievementSync.Desktop", "ViewModels", "MainViewModel.cs"));
    Check(desktopMainViewModelSource.Contains("PlatformAchievementExportBuilder.BuildAsync(", StringComparison.Ordinal)
        && desktopMainViewModelSource.Split("BuildGitHubAchievementExportsAsync();", StringSplitOptions.None).Length - 1 == 2,
        "both GitHub preview and bulk sync use the production multi-platform exporter");

    // 9. GitHub Sync Client Path Validation & Marker Support
    GitHubAchievementSyncClient.ValidateExportFilePath("AppID/730/achievements.json", "730");
    GitHubAchievementSyncClient.ValidateExportFilePath("AppID/730/Epic.platform", "730");
    GitHubAchievementSyncClient.ValidateExportFilePath("AppID/730/PlayStation.platform", "730");
    GitHubAchievementSyncClient.ValidateExportFilePath("AppID/730/blacklist", "730");
    var rejectedInvalidPath = false;
    try { GitHubAchievementSyncClient.ValidateExportFilePath("AppID/730/malicious.exe", "730"); }
    catch (InvalidDataException) { rejectedInvalidPath = true; }
    Check(rejectedInvalidPath, "GitHubAchievementSyncClient validates paths and rejects unknown file types");

    var rejectedPathTraversal = false;
    try { GitHubAchievementSyncClient.ValidateExportFilePath("AppID/730/../../etc/passwd", "730"); }
    catch (InvalidDataException) { rejectedPathTraversal = true; }
    Check(rejectedPathTraversal, "GitHubAchievementSyncClient rejects directory traversal attempts");

    // GitHub Preview with marker files
    var mockHttpClient = new HttpClient(new TestHttpHandler((request, _) =>
    {
        var url = request.RequestUri!.ToString();
        if (request.RequestUri?.AbsolutePath == "/user")
            return Task.FromResult(JsonResponse("""{"login":"testuser","name":"Test","avatar_url":"http://test"}"""));
        if (url.Contains("/repos/test/repo/git/ref/heads/user"))
            return Task.FromResult(JsonResponse("""{"object":{"sha":"commit123"}}"""));
        if (url.Contains("/git/commits/commit123"))
            return Task.FromResult(JsonResponse("""{"tree":{"sha":"tree123"}}"""));
        if (request.RequestUri?.AbsolutePath == "/repos/test/repo")
            return Task.FromResult(JsonResponse("""{"full_name":"test/repo","default_branch":"user","permissions":{"push":true},"private":false}"""));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }));
    var ghClient = new GitHubAchievementSyncClient(mockHttpClient);
    var ghTarget = new GitHubSyncTarget("test", "repo", "user");
    var ghPreviewFiles = new[]
    {
        new AchievementExportFile("AppID/730/achievements.json", "730", 1, """{"ACH_1":{"earned":true,"earned_time":1700000000}}"""),
        new AchievementExportFile("AppID/730/Epic.platform", "730", 0, string.Empty)
    };
    var ghPreview = await ghClient.BuildPreviewAsync("token", ghTarget, ghPreviewFiles);
    Check(ghPreview.Changes.Count == 2
        && ghPreview.Changes.Any(c => c.RelativePath == "AppID/730/Epic.platform" && c.ProposedJson == string.Empty && c.NewUnlockCount == 0),
        "GitHubAchievementSyncClient previews platform marker files with empty blob content and zero unlock count");

    // 10. Regression Coverage for Existing Platform Adapters
    var defaultSourceOptions = PlatformSourceOptions.CreateDefault();
    var existingSteamAdapter = new SteamDataAdapter(defaultSourceOptions);
    Check(existingSteamAdapter.Platform == "steam", "Existing SteamDataAdapter retains platform identity");
    var existingGogAdapter = new GogDataAdapter(defaultSourceOptions);
    Check(existingGogAdapter.Platform == "gog", "Existing GogDataAdapter retains platform identity");
    var existingXbox360Adapter = new Xbox360DataAdapter();
    Check(existingXbox360Adapter.Platform == "xbox360", "Existing Xbox360DataAdapter retains platform identity");
    var existingRaAdapter = new RetroAchievementsDataAdapter();
    Check(existingRaAdapter.Platform == "retroachievements", "Existing RetroAchievementsDataAdapter retains platform identity");
    var epicFixtureRoot = Path.Combine(tempRoot, "EpicLocal", "NemirtingasEpicEmu");
    Directory.CreateDirectory(Path.Combine(epicFixtureRoot, "test-namespace"));
    await File.WriteAllTextAsync(Path.Combine(epicFixtureRoot, "test-namespace", "achievements.json"),
        """{"EPIC_FIRST":{"earned":true,"earned_time":1700000000},"EPIC_SECOND":{"earned":false,"earned_time":0}}""");
    var epicAdapter = new EpicDataAdapter(new PlatformSourceOptions { EpicRoot = epicFixtureRoot });
    var epicResult = await epicAdapter.CollectAsync(new PlatformSourceOptions { EpicRoot = epicFixtureRoot });
    Check(epicAdapter.IsConfigured && epicResult.Games.Count == 1
        && epicResult.Games[0].Achievements.Any(item => item.Id == "EPIC_FIRST" && item.Earned)
        && epicAdapter.ConfigurationHint!.Contains("documented public APIs", StringComparison.Ordinal),
        "Epic platform import reads local emulator progress and clearly describes its documented API limit");
    var epicAdapterSource = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "src", "AchievementSync.Core", "Platforms", "StorePlatformAdapters.cs"));
    var epicDesktopSource = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "src", "AchievementSync.Desktop", "MainWindow.xaml"));
    Check(!epicAdapterSource.Contains("api.epicgames.dev/epic/achievements/v1", StringComparison.Ordinal)
        && !epicDesktopSource.Contains("Connect Epic", StringComparison.Ordinal),
        "Epic platform UI and adapter do not advertise or call an undocumented account-wide achievements endpoint");

    if (args.Contains("--live-github-sync", StringComparer.OrdinalIgnoreCase))
    {
        if (!args.Contains("--confirm-synthetic-upload", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Live sync is disabled unless --confirm-synthetic-upload is also supplied.");
        return await GitHubLiveSyncVerification.RunAsync();
    }

    Console.WriteLine($"PASS: {checks} functional checks completed.");
    return 0;
}
finally
{
    try { Directory.Delete(tempRoot, recursive: true); } catch { }
}

T RunOnStaThread<T>(Func<T> work)
{
    T staResult = default!;
    Exception? staFailure = null;
    var staThread = new Thread(() =>
    {
        try { staResult = work(); }
        catch (Exception exception) { staFailure = exception; }
    });
    staThread.SetApartmentState(ApartmentState.STA);
    staThread.Start();
    staThread.Join();
    if (staFailure is not null)
        throw new InvalidOperationException("FAIL: WPF smoke check threw " + staFailure.GetType().Name + ": " + staFailure.Message, staFailure);
    return staResult;
}

void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    checks++;
    Console.WriteLine("PASS: " + description);
}

bool TryGetJsonProperty(JsonElement element, string name, out JsonElement value)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
    }

    value = default;
    return false;
}

bool IsImageFile(string path)
{
    try
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[12];
        var count = stream.Read(header);
        return count >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF
            || count >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
            || count >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8)
            || count >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8))
            || count >= 2 && header[0] == 0x42 && header[1] == 0x4D;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        return false;
    }
}

byte[] EncryptPs4EsfmFixture(string npcommid, string xml,
    string keyHex = "21F41A6BAD8A1D3ECA7AD586C101B7A9")
{
    var key = Convert.FromHexString(keyHex);
    var identity = new byte[16];
    Encoding.ASCII.GetBytes(npcommid).CopyTo(identity, 0);
    using var keyGenerator = Aes.Create();
    keyGenerator.Key = key;
    keyGenerator.IV = new byte[16];
    keyGenerator.Mode = CipherMode.CBC;
    keyGenerator.Padding = PaddingMode.None;
    using var keyEncryptor = keyGenerator.CreateEncryptor();
    var derivedKey = keyEncryptor.TransformFinalBlock(identity, 0, identity.Length);

    var plain = Encoding.UTF8.GetBytes(xml);
    Array.Resize(ref plain, (plain.Length + 15) / 16 * 16);
    var iv = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
    using var encryptAes = Aes.Create();
    encryptAes.Key = derivedKey;
    encryptAes.IV = iv;
    encryptAes.Mode = CipherMode.CBC;
    encryptAes.Padding = PaddingMode.None;
    using var encryptor = encryptAes.CreateEncryptor();
    var cipherText = encryptor.TransformFinalBlock(plain, 0, plain.Length);
    return [.. iv, .. cipherText];
}

byte[] BuildPs4TrpFixture(string entryName, byte[] payload)
{
    const int entryOffset = 96;
    const int entryStride = 64;
    var dataOffset = entryOffset + entryStride;
    var bytes = new byte[dataOffset + payload.Length];
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 0xdca24d00);
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 1);
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), entryStride);
    Encoding.ASCII.GetBytes(entryName).CopyTo(bytes, entryOffset);
    BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(entryOffset + 32, 8), (ulong)dataOffset);
    BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(entryOffset + 40, 8), (ulong)payload.Length);
    payload.CopyTo(bytes, dataOffset);
    return bytes;
}

byte[] BuildPs4NpbindFixture(string npcommid, int trophySlot)
{
    var bytes = new byte[128 + 4 * 4 + Encoding.ASCII.GetByteCount(npcommid) + 1 + 152];
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 0xd294a018);
    BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(24, 8), 1);
    var position = 128;
    void WriteField(ushort type, byte[] value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(position, 2), type);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(position + 2, 2), checked((ushort)value.Length));
        position += 4;
        value.CopyTo(bytes, position);
        position += value.Length;
    }
    WriteField(0x10, Encoding.ASCII.GetBytes(npcommid));
    WriteField(0x11, Encoding.ASCII.GetBytes(trophySlot.ToString(CultureInfo.InvariantCulture)));
    WriteField(0, []);
    WriteField(0, []);
    return bytes;
}

HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK)
{
    Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
};

sealed class TestHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        send(request, cancellationToken);
}
