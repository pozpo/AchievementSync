using System.Security.Cryptography;
using AchievementSync.Core.Metadata;
using AchievementSync.Core.Parsing;
using AchievementSync.Core.Scanning;
using AchievementSync.Core.Synchronization;

var tempRoot = Path.Combine(Path.GetTempPath(), "AchievementSync-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);
var checks = 0;

try
{
    var jsonPath = Path.Combine(tempRoot, "achievements.json");
    File.WriteAllText(jsonPath, """
        {
          "ACH_ALPHA": { "earned": true, "earned_time": 1700000000 },
          "ACH_BETA": { "earned": false, "earned_time": 0 }
        }
        """);
    var jsonResult = new JsonAchievementParser().Parse(jsonPath, "100");
    Check(jsonResult.Success, "JSON object profile parses");
    Check(jsonResult.Profile!.HasCompleteCatalog && jsonResult.Profile.Achievements.Count == 2, "JSON object catalog is complete");
    Check(jsonResult.Profile.Achievements.Single(item => item.ApiName == "ACH_ALPHA").IsUnlocked == true, "JSON true unlock state is retained");

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
    File.WriteAllText(Path.Combine(viewerDirectory, "game-info.json"), """
        {"appid":"730","name":"Fixture Game","icon":"fixture-icon","achievements":{"ACH_ALPHA":{"name":"Alpha","description":"Catalog description","icon":"https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/730/alpha.jpg","icongray":"gray","hidden":0},"ACH_BETA":{"name":"Beta","description":"Hidden catalog entry","icongray":"gray","hidden":1}}}
        """);
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
    Check(merged.UnlockedCount == 2 && merged.TotalCount == 3, "merged completion counts are accurate");
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

    Console.WriteLine($"PASS: {checks} functional checks completed.");
    return 0;
}
finally
{
    try { Directory.Delete(tempRoot, recursive: true); } catch { }
}

void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    checks++;
    Console.WriteLine("PASS: " + description);
}
