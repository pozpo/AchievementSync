using System.Security.Cryptography;
using System.Net;
using AchievementSync.Core.Metadata;
using AchievementSync.Core.Parsing;
using AchievementSync.Core.Scanning;
using AchievementSync.Core.Synchronization;
using AchievementSync.Core.Settings;

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
    Check(!newSettings.AutoSyncAfterScan && newSettings.GitHubBranch == "user",
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

HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK)
{
    Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
};

sealed class TestHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        send(request, cancellationToken);
}
