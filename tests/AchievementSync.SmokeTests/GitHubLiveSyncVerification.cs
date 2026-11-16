using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AchievementSync.Core.Settings;
using AchievementSync.Core.Synchronization;

internal static class GitHubLiveSyncVerification
{
    private const string SyntheticAchievementName = "ACHIEVEMENTSYNC_SYNTHETIC_TEST";
    private const string SyntheticAppId = "9999999998";
    private const string CredentialTarget = "AchievementSync/GitHub/OAuthToken";
    private static readonly Uri ApiBase = new("https://api.github.com/");

    public static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("FAIL: The live GitHub check requires Windows Credential Manager.");
            return 1;
        }

        string? accessToken = null;
        string? syntheticAppId = null;
        GitHubSyncTarget? target = null;
        GitHubAchievementSyncClient? syncClient = null;
        HttpClient? http = null;
        string? fixtureJson = null;
        var pushedByThisRun = false;

        try
        {
            var root = FindRepositoryRoot();
            var desktopAssemblyPath = Path.Combine(root, "src", "AchievementSync.Desktop", "bin", "Release", "net10.0-windows", "AchievementSync.dll");
            if (!File.Exists(desktopAssemblyPath))
                throw new FileNotFoundException("Build the Release solution before the live GitHub check.", desktopAssemblyPath);

            var desktopAssembly = Assembly.LoadFrom(desktopAssemblyPath);
            var credentialStoreType = desktopAssembly.GetType("AchievementSync.Desktop.Services.WindowsCredentialTokenStore", throwOnError: true)!;
            var storeConstructor = credentialStoreType.GetConstructor([typeof(string)])
                ?? throw new InvalidOperationException("The Windows Credential Manager adapter could not be loaded.");
            var store = storeConstructor.Invoke([CredentialTarget]);
            var readCredential = credentialStoreType.GetMethod("Read")!;
            var writeCredential = credentialStoreType.GetMethod("Write")!;
            var serializedCredential = readCredential.Invoke(store, null) as string;
            var credential = GitHubStoredCredential.Parse(serializedCredential)
                ?? throw new GitHubAuthorizationException("Connect your GitHub account in AchievementSync before running the live check.");

            http = CreateHttpClient();
            if (credential.NeedsRefresh())
            {
                if (string.IsNullOrWhiteSpace(credential.RefreshToken))
                    throw new GitHubAuthorizationException("The saved GitHub access token has expired. Connect your account again.");
                var clientId = desktopAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(attribute => attribute.Key == "AchievementSync.GitHubOAuthClientId")?.Value
                    ?? Environment.GetEnvironmentVariable("ACHIEVEMENTSYNC_GITHUB_CLIENT_ID");
                if (string.IsNullOrWhiteSpace(clientId))
                    throw new GitHubAuthorizationException("The public GitHub OAuth Client ID is missing from this build.");

                var rotated = await new GitHubDeviceFlowClient(http).RefreshAsync(clientId, credential.RefreshToken);
                credential = rotated.ToStoredCredential();
                writeCredential.Invoke(store, [credential.Serialize()]);
                Console.WriteLine("Refreshed the saved GitHub authorization.");
            }
            accessToken = credential.AccessToken;

            var settings = new SettingsStore().Load();
            if (settings.AutoSyncAfterScan)
                throw new InvalidOperationException("Turn off automatic sync before running this synthetic-only check.");
            if (string.IsNullOrWhiteSpace(settings.GitHubOwner)
                || string.IsNullOrWhiteSpace(settings.GitHubRepository)
                || settings.GitHubBranch != "user")
                throw new InvalidOperationException("Configure the owned achievement-viewer repository and user branch in AchievementSync first.");

            syncClient = new GitHubAchievementSyncClient(http);
            var account = await syncClient.GetCurrentUserAsync(accessToken);
            if (!account.HasPublicRepositoryAccess)
                throw new GitHubAuthorizationException("The connected account has not granted the required public_repo permission.");
            if (!string.Equals(settings.GitHubOwner, account.Login, StringComparison.OrdinalIgnoreCase))
                throw new GitHubApiException("The configured repository owner does not match the authenticated GitHub account.");

            var repository = await syncClient.GetRepositoryAsync(accessToken, settings.GitHubOwner, settings.GitHubRepository);
            if (!string.Equals(repository.FullName, $"{settings.GitHubOwner}/{settings.GitHubRepository}", StringComparison.OrdinalIgnoreCase)
                || repository.IsPrivate || !repository.CanPush)
                throw new GitHubApiException("The selected repository must be public and writable by the authenticated account.");

            target = new GitHubSyncTarget(settings.GitHubOwner, settings.GitHubRepository, settings.GitHubBranch);
            syntheticAppId = await FindWebsiteIndexedAppIdAsync(http, accessToken, target, repository.DefaultBranch);
            var appIdIsIndexedByViewer = syntheticAppId is not null;
            if (syntheticAppId is null)
            {
                syntheticAppId = SyntheticAppId;
                await EnsureFileAbsentAsync(http, accessToken, target.Owner, target.Repository, repository.DefaultBranch, syntheticAppId);
                await EnsureFileAbsentAsync(http, accessToken, target.Owner, target.Repository, target.Branch, syntheticAppId);
                Console.WriteLine($"No free indexed AppID path exists; using reserved synthetic fixture ID {SyntheticAppId}. The viewer's user-branch data endpoint will be checked, but this temporary test will not be listed as a game on the site.");
            }

            var fixturePath = Path.Combine(root, "tests", "Fixtures", "GitHubSynthetic", "AppID", SyntheticAppId, "achievements.json");
            fixtureJson = await File.ReadAllTextAsync(fixturePath);
            using (var fixture = JsonDocument.Parse(fixtureJson))
            {
                if (fixture.RootElement.EnumerateObject().Count() != 1
                    || !fixture.RootElement.TryGetProperty(SyntheticAchievementName, out var state)
                    || !state.GetProperty("earned").GetBoolean())
                    throw new InvalidDataException("The live sync fixture must contain only the labelled synthetic achievement.");
            }

            var path = $"AppID/{syntheticAppId}/achievements.json";
            var localFile = new AchievementExportFile(path, syntheticAppId, 1, fixtureJson);
            var preview = await syncClient.BuildPreviewAsync(accessToken, target, [localFile]);
            if (preview.Changes.Count != 1
                || preview.Changes[0].RelativePath != path
                || preview.Changes[0].ExistingJson is not null
                || preview.Changes[0].AchievementCount != 1)
                throw new InvalidOperationException("The synthetic test preview was not exactly one previously absent AppID JSON file.");

            var result = await syncClient.PushAsync(accessToken, preview);
            if (!result.Pushed || result.UpdatedFiles != 1 || string.IsNullOrWhiteSpace(result.CommitUrl))
                throw new InvalidOperationException("GitHub did not create the expected synthetic test commit.");
            pushedByThisRun = true;

            var commitSha = new Uri(result.CommitUrl).Segments.Last().Trim('/');
            var commitJson = await GetApiJsonAsync(http, accessToken,
                $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/commits/{Escape(commitSha)}");
            using (commitJson)
            {
                if (!commitJson.RootElement.TryGetProperty("files", out var files)
                    || files.ValueKind != JsonValueKind.Array
                    || files.GetArrayLength() != 1
                    || OptionalString(files[0], "filename") != path)
                    throw new InvalidOperationException("The verified GitHub commit contains unexpected paths.");
            }
            Console.WriteLine($"Verified single-path upload commit: {result.CommitUrl}");

            var remote = await ReadRemoteFileAsync(http, accessToken, target, syntheticAppId);
            if (remote is null || !AchievementJsonMerge.SemanticallyEqual(remote.Value.Json, fixtureJson))
                throw new InvalidOperationException("The uploaded synthetic JSON did not match its reviewed content.");

            var websiteJson = await ReadViewerRawFileWithRetryAsync(http, target, path);
            if (!AchievementJsonMerge.SemanticallyEqual(websiteJson, fixtureJson))
                throw new InvalidOperationException("The viewer website's raw path returned different test JSON.");

            Console.WriteLine($"Authenticated account: {account.Login} (public_repo granted).");
            Console.WriteLine($"Verified target: {repository.FullName} branch {target.Branch}; synthetic AppID {syntheticAppId}{(appIdIsIndexedByViewer ? " (already indexed by the viewer)" : " (reserved fixture ID)")}.");
            Console.WriteLine("The viewer website's raw user-branch data endpoint returned the reviewed synthetic JSON.");

            await RemoveSyntheticFileAsync(http, accessToken, target, syntheticAppId, fixtureJson);
            Console.WriteLine("Removed only the verified synthetic file after the GitHub and website readback checks.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: Live synthetic GitHub sync check: {exception.Message}");
            if (pushedByThisRun && accessToken is not null && target is not null && syntheticAppId is not null && fixtureJson is not null && http is not null)
            {
                try
                {
                    await RemoveSyntheticFileAsync(http, accessToken, target, syntheticAppId, fixtureJson);
                    Console.Error.WriteLine("Removed the exact synthetic test file created during this check.");
                }
                catch (Exception cleanupError)
                {
                    Console.Error.WriteLine($"Cleanup needs attention for AppID/{syntheticAppId}/achievements.json: {cleanupError.Message}");
                }
            }
            return 1;
        }
        finally
        {
            http?.Dispose();
        }
    }

    private static async Task<string?> FindWebsiteIndexedAppIdAsync(
        HttpClient http,
        string token,
        GitHubSyncTarget target,
        string defaultBranch)
    {
        for (var page = 1; page <= 10; page++)
        {
            using var json = await GetApiJsonAsync(http, token,
                $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/contents/AppID?ref={Uri.EscapeDataString(defaultBranch)}&per_page=100&page={page}");
            if (json.RootElement.ValueKind != JsonValueKind.Array) break;
            var items = json.RootElement.EnumerateArray().ToArray();
            foreach (var item in items)
            {
                if (OptionalString(item, "type") != "dir") continue;
                var appId = OptionalString(item, "name");
                if (string.IsNullOrEmpty(appId) || !appId.All(char.IsAsciiDigit)) continue;

                var candidateDirectory = $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/contents/AppID/{appId}?ref={Uri.EscapeDataString(target.Branch)}";
                using var candidateResponse = await SendApiAsync(http, token, HttpMethod.Get, candidateDirectory);
                if (candidateResponse.StatusCode == HttpStatusCode.NotFound) return appId;
                using var candidate = await ReadApiJsonAsync(candidateResponse);
                if (candidate.RootElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException($"GitHub returned unexpected metadata for AppID/{appId}.");
                var hasAchievementsFile = candidate.RootElement.EnumerateArray()
                    .Any(entry => OptionalString(entry, "name") == "achievements.json");
                if (!hasAchievementsFile) return appId;
            }
            if (items.Length < 100) break;
        }

        return null;
    }

    private static async Task EnsureFileAbsentAsync(HttpClient http, string token, string owner, string repository, string branch, string appId)
    {
        var relative = $"repos/{Escape(owner)}/{Escape(repository)}/contents/AppID/{appId}?ref={Uri.EscapeDataString(branch)}";
        using var response = await SendApiAsync(http, token, HttpMethod.Get, relative);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        using var json = await ReadApiJsonAsync(response);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Synthetic AppID path is not an absent-or-directory path on branch '{branch}'. No upload was made.");
        if (json.RootElement.GetArrayLength() != 0)
            throw new InvalidOperationException($"Synthetic AppID directory is not empty on branch '{branch}'. No upload was made.");
    }

    private static async Task<string> ReadViewerRawFileWithRetryAsync(HttpClient http, GitHubSyncTarget target, string path)
    {
        int[] delaysMilliseconds = [0, 1000, 2000, 4000, 8000, 12000];
        HttpStatusCode? lastStatus = null;
        for (var attempt = 0; attempt < delaysMilliseconds.Length; attempt++)
        {
            if (delaysMilliseconds[attempt] > 0)
                await Task.Delay(delaysMilliseconds[attempt]);
            var cacheBuster = Guid.NewGuid().ToString("N");
            var url = $"https://raw.githubusercontent.com/{Escape(target.Owner)}/{Escape(target.Repository)}/{Escape(target.Branch)}/{path}?verify={cacheBuster}";
            using var response = await http.GetAsync(url);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadAsStringAsync();

            lastStatus = response.StatusCode;
            if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or (HttpStatusCode)429))
                throw new HttpRequestException($"The viewer raw data endpoint returned HTTP {(int)response.StatusCode}.");
        }

        throw new HttpRequestException($"The viewer raw user-branch path remained unavailable after retries (HTTP {(int?)lastStatus}).");
    }

    private static async Task<(string Json, string BlobSha)?> ReadRemoteFileAsync(
        HttpClient http,
        string token,
        GitHubSyncTarget target,
        string appId)
    {
        var relative = $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/contents/AppID/{appId}/achievements.json?ref={Uri.EscapeDataString(target.Branch)}";
        using var response = await SendApiAsync(http, token, HttpMethod.Get, relative);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var json = await ReadApiJsonAsync(response);
        var content = RequiredString(json.RootElement, "content").Replace("\r", "").Replace("\n", "");
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(content));
        return (decoded, RequiredString(json.RootElement, "sha"));
    }

    private static async Task RemoveSyntheticFileAsync(
        HttpClient http,
        string token,
        GitHubSyncTarget target,
        string appId,
        string expectedJson)
    {
        var current = await ReadRemoteFileAsync(http, token, target, appId);
        if (current is null) return;
        if (!AchievementJsonMerge.SemanticallyEqual(current.Value.Json, expectedJson))
            throw new InvalidOperationException("The remote file no longer matches the synthetic fixture; cleanup refused to remove changed content.");

        var relative = $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/contents/AppID/{appId}/achievements.json";
        var body = new
        {
            message = "AchievementSync: remove synthetic sync verification",
            sha = current.Value.BlobSha,
            branch = target.Branch
        };
        using var response = await SendApiAsync(http, token, HttpMethod.Delete, relative, body);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub did not remove the confirmed synthetic test file (HTTP {(int)response.StatusCode}).");

        if (await ReadRemoteFileAsync(http, token, target, appId) is not null)
            throw new InvalidOperationException("GitHub still reports the synthetic test file after cleanup.");
    }

    private static async Task<JsonDocument> GetApiJsonAsync(HttpClient http, string token, string relative)
    {
        using var response = await SendApiAsync(http, token, HttpMethod.Get, relative);
        return await ReadApiJsonAsync(response);
    }

    private static async Task<HttpResponseMessage> SendApiAsync(HttpClient http, string token, HttpMethod method, string relative, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(ApiBase, relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await http.SendAsync(request);
    }

    private static async Task<JsonDocument> ReadApiJsonAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw status == 401
                ? new GitHubAuthorizationException("GitHub rejected the saved authorization. Sign in again.")
                : new HttpRequestException($"GitHub returned HTTP {status} during live verification.");
        }
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AchievementSync-live-verification");
        return client;
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        OptionalString(element, propertyName) ?? throw new InvalidDataException($"GitHub response is missing '{propertyName}'.");

    private static string? OptionalString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AchievementSync.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the AchievementSync solution root.");
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
