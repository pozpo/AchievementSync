using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AchievementSync.Core.Synchronization;

/// <summary>Previews and commits only viewer achievement JSON through the GitHub API.</summary>
public sealed class GitHubAchievementSyncClient
{
    private static readonly Uri ApiBase = new("https://api.github.com/");
    private readonly HttpClient _httpClient;

    public GitHubAchievementSyncClient(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<GitHubAccount> GetCurrentUserAsync(string token, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "user", token, null, cancellationToken);
        using var json = await GitHubDeviceFlowClient.ReadJsonAsync(response, cancellationToken);
        var scopes = response.Headers.TryGetValues("X-OAuth-Scopes", out var scopeHeader)
            ? scopeHeader.SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new GitHubAccount(
            GitHubDeviceFlowClient.RequiredString(json.RootElement, "login"),
            GitHubDeviceFlowClient.OptionalString(json.RootElement, "name"),
            GitHubDeviceFlowClient.OptionalString(json.RootElement, "avatar_url"),
            scopes);
    }

    public async Task<GitHubRepository> GetRepositoryAsync(
        string token,
        string owner,
        string repository,
        CancellationToken cancellationToken = default)
    {
        ValidateRepositoryName(owner, nameof(owner));
        ValidateRepositoryName(repository, nameof(repository));
        using var response = await SendAsync(HttpMethod.Get, $"repos/{Escape(owner)}/{Escape(repository)}", token, null, cancellationToken);
        using var json = await GitHubDeviceFlowClient.ReadJsonAsync(response, cancellationToken);
        var root = json.RootElement;
        var canPush = root.TryGetProperty("permissions", out var permissions)
            && permissions.TryGetProperty("push", out var push)
            && push.ValueKind == JsonValueKind.True;
        return new GitHubRepository(
            GitHubDeviceFlowClient.RequiredString(root, "full_name"),
            GitHubDeviceFlowClient.RequiredString(root, "default_branch"),
            canPush,
            root.TryGetProperty("private", out var isPrivate) && isPrivate.ValueKind == JsonValueKind.True);
    }

    public async Task<GitHubSyncPreview> BuildPreviewAsync(
        string token,
        GitHubSyncTarget target,
        IEnumerable<AchievementExportFile> localFiles,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
        var repository = await GetRepositoryAsync(token, target.Owner, target.Repository, cancellationToken);
        if (!string.Equals(repository.FullName, $"{target.Owner}/{target.Repository}", StringComparison.OrdinalIgnoreCase))
            throw new GitHubApiException("GitHub redirected the repository name. Verify the owner and repository again.");
        if (!repository.CanPush) throw new GitHubApiException("The connected GitHub account does not have push access to this repository.");
        if (repository.IsPrivate) throw new GitHubApiException("AchievementSync requests access to public repositories only. Choose a public fork.");

        using var referenceResponse = await SendAsync(HttpMethod.Get, RefPath(target), token, null, cancellationToken);
        using var reference = await GitHubDeviceFlowClient.ReadJsonAsync(referenceResponse, cancellationToken);
        var baseCommit = GitHubDeviceFlowClient.RequiredString(reference.RootElement.GetProperty("object"), "sha");
        using var commitResponse = await SendAsync(HttpMethod.Get, $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/git/commits/{Escape(baseCommit)}", token, null, cancellationToken);
        using var commit = await GitHubDeviceFlowClient.ReadJsonAsync(commitResponse, cancellationToken);
        var baseTree = GitHubDeviceFlowClient.RequiredString(commit.RootElement.GetProperty("tree"), "sha");

        var exportedFiles = localFiles.ToArray();
        if (exportedFiles.GroupBy(file => file.AppId, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidDataException("A sync preview contained duplicate files for the same AppID.");

        var changes = new List<GitHubSyncChange>();
        foreach (var file in exportedFiles.OrderBy(file => file.AppId, StringComparer.Ordinal))
        {
            var expectedPath = $"AppID/{file.AppId}/achievements.json";
            if (!string.Equals(file.RelativePath, expectedPath, StringComparison.Ordinal)
                || string.IsNullOrEmpty(file.AppId)
                || !file.AppId.All(char.IsAsciiDigit))
                throw new InvalidDataException("A sync preview contained a path outside AppID/<numeric id>/achievements.json.");

            var remoteContent = await ReadAchievementFileAsync(token, target, baseCommit, file.AppId, cancellationToken);
            var merged = AchievementJsonMerge.Merge(file.Json, remoteContent);
            var isChanged = remoteContent is null || !AchievementJsonMerge.SemanticallyEqual(remoteContent, merged);
            if (!isChanged) continue;
            changes.Add(new GitHubSyncChange(
                file.RelativePath,
                file.AppId,
                remoteContent,
                merged,
                CountAchievements(merged),
                AchievementJsonMerge.CountNewUnlocks(remoteContent, merged)));
        }

        return new GitHubSyncPreview(target, baseCommit, baseTree, changes);
    }

    public async Task<GitHubPushResult> PushAsync(
        string token,
        GitHubSyncPreview preview,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(preview.Target);
        if (preview.Changes.Count == 0) return new GitHubPushResult(false, null, 0, 0);

        using var referenceResponse = await SendAsync(HttpMethod.Get, RefPath(preview.Target), token, null, cancellationToken);
        using var reference = await GitHubDeviceFlowClient.ReadJsonAsync(referenceResponse, cancellationToken);
        var currentCommit = GitHubDeviceFlowClient.RequiredString(reference.RootElement.GetProperty("object"), "sha");
        if (!string.Equals(currentCommit, preview.BaseCommitSha, StringComparison.Ordinal))
            throw new GitHubConcurrentUpdateException("The repository changed after this preview. Build a new preview before pushing.");

        var treeEntries = new List<object>();
        foreach (var change in preview.Changes)
        {
            ValidateChange(change);
            using var blobResponse = await SendAsync(HttpMethod.Post,
                $"repos/{Escape(preview.Target.Owner)}/{Escape(preview.Target.Repository)}/git/blobs",
                token,
                new { content = change.ProposedJson, encoding = "utf-8" },
                cancellationToken);
            using var blob = await GitHubDeviceFlowClient.ReadJsonAsync(blobResponse, cancellationToken);
            treeEntries.Add(new
            {
                path = change.RelativePath,
                mode = "100644",
                type = "blob",
                sha = GitHubDeviceFlowClient.RequiredString(blob.RootElement, "sha")
            });
        }

        using var treeResponse = await SendAsync(HttpMethod.Post,
            $"repos/{Escape(preview.Target.Owner)}/{Escape(preview.Target.Repository)}/git/trees",
            token,
            new { base_tree = preview.BaseTreeSha, tree = treeEntries },
            cancellationToken);
        using var tree = await GitHubDeviceFlowClient.ReadJsonAsync(treeResponse, cancellationToken);
        var treeSha = GitHubDeviceFlowClient.RequiredString(tree.RootElement, "sha");

        using var commitResponse = await SendAsync(HttpMethod.Post,
            $"repos/{Escape(preview.Target.Owner)}/{Escape(preview.Target.Repository)}/git/commits",
            token,
            new { message = "AchievementSync: sync achievement data", tree = treeSha, parents = new[] { preview.BaseCommitSha } },
            cancellationToken);
        using var newCommit = await GitHubDeviceFlowClient.ReadJsonAsync(commitResponse, cancellationToken);
        var newCommitSha = GitHubDeviceFlowClient.RequiredString(newCommit.RootElement, "sha");

        using var updateResponse = await SendAsync(new HttpMethod("PATCH"), UpdateRefPath(preview.Target), token,
            new { sha = newCommitSha, force = false }, cancellationToken);
        using var updatedReference = await GitHubDeviceFlowClient.ReadJsonAsync(updateResponse, cancellationToken);

        return new GitHubPushResult(
            true,
            $"https://github.com/{preview.Target.Owner}/{preview.Target.Repository}/commit/{newCommitSha}",
            preview.Changes.Count,
            preview.Changes.Sum(change => change.NewUnlockCount));
    }

    private async Task<string?> ReadAchievementFileAsync(
        string token,
        GitHubSyncTarget target,
        string commitSha,
        string appId,
        CancellationToken cancellationToken)
    {
        var path = $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/contents/AppID/{appId}/achievements.json?ref={Uri.EscapeDataString(commitSha)}";
        using var response = await SendAsync(HttpMethod.Get, path, token, null, cancellationToken, allowNotFound: true);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var json = await GitHubDeviceFlowClient.ReadJsonAsync(response, cancellationToken);
        var root = json.RootElement;
        if (GitHubDeviceFlowClient.OptionalString(root, "encoding") != "base64"
            || GitHubDeviceFlowClient.OptionalString(root, "type") != "file")
            throw new GitHubApiException("A remote achievement path is not a regular Base64 file.");
        var encoded = GitHubDeviceFlowClient.RequiredString(root, "content").Replace("\n", "").Replace("\r", "");
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); }
        catch (FormatException exception) { throw new GitHubApiException("A remote achievement file is not valid Base64.", exception); }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativePath,
        string token,
        object? body,
        CancellationToken cancellationToken,
        bool allowNotFound = false)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("GitHub authorization is required.", nameof(token));
        using var request = new HttpRequestMessage(method, new Uri(ApiBase, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("AchievementSync/0.1.2");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode && !(allowNotFound && response.StatusCode == HttpStatusCode.NotFound))
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            if (status == 409 || status == 422)
                throw new GitHubConcurrentUpdateException("GitHub rejected the update because the branch changed or the repository no longer matches the preview. Refresh and preview again.");
            if (status == 401)
                throw new GitHubAuthorizationException("GitHub rejected the saved sign-in. It may have expired or been revoked; connect your account again.");
            if (status == 403)
                throw new GitHubApiException("GitHub denied this request. Check that your account can write public repositories and that the selected repository is public.");
            if (status == 404)
                throw new GitHubApiException("GitHub could not find the selected repository or branch. Check the owner, repository name, and branch.");
            throw new GitHubApiException($"GitHub returned HTTP {status}. Check repository access and try again.");
        }
        return response;
    }

    private static int CountAchievements(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().Count();
    }

    private static void ValidateChange(GitHubSyncChange change)
    {
        if (string.IsNullOrEmpty(change.AppId) || !change.AppId.All(char.IsAsciiDigit)
            || !string.Equals(change.RelativePath, $"AppID/{change.AppId}/achievements.json", StringComparison.Ordinal))
            throw new InvalidDataException("A sync change contained a path outside the AppID achievement data directory.");
        _ = AchievementJsonMerge.Merge(change.ProposedJson, change.ExistingJson);
    }

    private static string RefPath(GitHubSyncTarget target) =>
        $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/git/ref/heads/{target.Branch}";

    private static string UpdateRefPath(GitHubSyncTarget target) =>
        $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/git/refs/heads/{target.Branch}";

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static void ValidateTarget(GitHubSyncTarget target)
    {
        ValidateRepositoryName(target.Owner, nameof(target.Owner));
        ValidateRepositoryName(target.Repository, nameof(target.Repository));
        if (string.IsNullOrWhiteSpace(target.Branch)
            || target.Branch.StartsWith('/') || target.Branch.EndsWith('/')
            || target.Branch.Contains("..", StringComparison.Ordinal)
            || target.Branch.Contains("//", StringComparison.Ordinal)
            || target.Branch.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/')))
            throw new ArgumentException("The branch name is invalid.", nameof(target));
    }

    private static void ValidateRepositoryName(string name, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new ArgumentException("Use a GitHub owner or repository name, not a URL.", parameterName);
    }
}

public sealed record GitHubAccount(string Login, string? DisplayName, string? AvatarUrl, IReadOnlySet<string> OAuthScopes)
{
    public bool HasPublicRepositoryAccess => OAuthScopes.Contains("public_repo");
}
public sealed record GitHubRepository(string FullName, string DefaultBranch, bool CanPush, bool IsPrivate);
public sealed record GitHubSyncTarget(string Owner, string Repository, string Branch);
public sealed record GitHubSyncChange(string RelativePath, string AppId, string? ExistingJson, string ProposedJson, int AchievementCount, int NewUnlockCount);
public sealed record GitHubSyncPreview(GitHubSyncTarget Target, string BaseCommitSha, string BaseTreeSha, IReadOnlyList<GitHubSyncChange> Changes);
public sealed record GitHubPushResult(bool Pushed, string? CommitUrl, int UpdatedFiles, int NewUnlocks);
public class GitHubApiException(string message, Exception? innerException = null) : Exception(message, innerException);
public sealed class GitHubConcurrentUpdateException(string message) : GitHubApiException(message);
