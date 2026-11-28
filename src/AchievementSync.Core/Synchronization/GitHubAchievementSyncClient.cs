using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchievementSync.Core.Metadata;
using AchievementSync.Core.Platforms;

namespace AchievementSync.Core.Synchronization;

/// <summary>Previews and commits viewer achievement JSON plus supported empty marker files through the GitHub API.</summary>
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
        if (exportedFiles.GroupBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidDataException("A sync preview contained duplicate files for the same path.");

        var changes = new List<GitHubSyncChange>();
        foreach (var file in exportedFiles.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            ValidateExportFilePath(file.RelativePath, file.AppId);

            if (file.RelativePath.EndsWith("achievements.json", StringComparison.Ordinal))
            {
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
            else
            {
                var remoteContent = await ReadRemoteTextAsync(token, target, baseCommit, file.RelativePath, cancellationToken);
                var isChanged = remoteContent is null || !string.Equals(remoteContent, file.Json, StringComparison.Ordinal);
                if (!isChanged) continue;
                changes.Add(new GitHubSyncChange(
                    file.RelativePath,
                    file.AppId,
                    remoteContent,
                    file.Json,
                    0,
                    0));
            }
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

    /// <summary>
    /// Pushes viewer-compatible achievement JSON and empty marker files in a single non-forced commit.
    /// Achievement JSON is merged with the remote file (remote unlocks are preserved); unchanged files
    /// are skipped. Per-file failures are collected in the report instead of aborting the whole sync.
    /// </summary>
    public async Task<BulkSyncResult> PushBulkAsync(
        string token,
        GitHubSyncTarget target,
        IEnumerable<AchievementExportFile> achievementFiles,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
        var repository = await GetRepositoryAsync(token, target.Owner, target.Repository, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(repository.FullName, $"{target.Owner}/{target.Repository}", StringComparison.OrdinalIgnoreCase))
            throw new GitHubApiException("GitHub redirected the repository name. Verify the owner and repository again.");
        if (!repository.CanPush) throw new GitHubApiException("The connected GitHub account does not have push access to this repository.");
        if (repository.IsPrivate) throw new GitHubApiException("AchievementSync requests access to public repositories only. Choose a public fork.");

        using var referenceResponse = await SendAsync(HttpMethod.Get, RefPath(target), token, null, cancellationToken).ConfigureAwait(false);
        using var reference = await GitHubDeviceFlowClient.ReadJsonAsync(referenceResponse, cancellationToken).ConfigureAwait(false);
        var baseCommit = GitHubDeviceFlowClient.RequiredString(reference.RootElement.GetProperty("object"), "sha");
        using var commitResponse = await SendAsync(HttpMethod.Get, $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/git/commits/{Escape(baseCommit)}", token, null, cancellationToken).ConfigureAwait(false);
        using var commit = await GitHubDeviceFlowClient.ReadJsonAsync(commitResponse, cancellationToken).ConfigureAwait(false);
        var baseTree = GitHubDeviceFlowClient.RequiredString(commit.RootElement.GetProperty("tree"), "sha");

        var entries = new List<BulkSyncEntry>();
        var treeEntries = new List<object>();
        var achievementFilesPushed = 0;
        var achievementFilesUnchanged = 0;
        var achievementFilesFailed = 0;

        foreach (var file in achievementFiles.OrderBy(file => file.AppId, StringComparer.Ordinal))
        {
            try
            {
                ValidateExportFilePath(file.RelativePath, file.AppId);

                if (file.RelativePath.EndsWith("achievements.json", StringComparison.Ordinal))
                {
                    var remoteContent = await ReadAchievementFileAsync(token, target, baseCommit, file.AppId, cancellationToken).ConfigureAwait(false);
                    var merged = AchievementJsonMerge.Merge(file.Json, remoteContent);
                    var isChanged = remoteContent is null || !AchievementJsonMerge.SemanticallyEqual(remoteContent, merged);
                    if (!isChanged)
                    {
                        achievementFilesUnchanged++;
                        entries.Add(new BulkSyncEntry(file.AppId, "unchanged", "achievements.json is up to date"));
                        continue;
                    }

                    var sha = await CreateBlobAsync(token, target, merged, encoding: "utf-8", cancellationToken).ConfigureAwait(false);
                    treeEntries.Add(new { path = file.RelativePath, mode = "100644", type = "blob", sha });
                    achievementFilesPushed++;
                    entries.Add(new BulkSyncEntry(file.AppId, "pushed", $"achievements.json committed ({AchievementJsonMerge.CountNewUnlocks(remoteContent, merged):N0} new unlock(s) merged with remote)"));
                }
                else
                {
                    var remoteContent = await ReadRemoteTextAsync(token, target, baseCommit, file.RelativePath, cancellationToken).ConfigureAwait(false);
                    var isChanged = remoteContent is null || !string.Equals(remoteContent, file.Json, StringComparison.Ordinal);
                    if (!isChanged)
                    {
                        achievementFilesUnchanged++;
                        entries.Add(new BulkSyncEntry(file.AppId, "unchanged", $"{Path.GetFileName(file.RelativePath)} is up to date"));
                        continue;
                    }

                    var sha = await CreateBlobAsync(token, target, file.Json, encoding: "utf-8", cancellationToken).ConfigureAwait(false);
                    treeEntries.Add(new { path = file.RelativePath, mode = "100644", type = "blob", sha });
                    achievementFilesPushed++;
                    entries.Add(new BulkSyncEntry(file.AppId, "pushed", $"{Path.GetFileName(file.RelativePath)} committed"));
                }
            }
            catch (Exception exception) when (exception is GitHubApiException or InvalidDataException or IOException or ArgumentException)
            {
                achievementFilesFailed++;
                entries.Add(new BulkSyncEntry(file.AppId, "failed", $"{Path.GetFileName(file.RelativePath)}: {exception.Message}"));
            }
        }

        if (treeEntries.Count == 0)
        {
            return new BulkSyncResult(false, null, achievementFilesPushed, achievementFilesUnchanged,
                achievementFilesFailed, entries);
        }

        using var treeResponse = await SendAsync(HttpMethod.Post,
            $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/git/trees",
            token,
            new { base_tree = baseTree, tree = treeEntries },
            cancellationToken).ConfigureAwait(false);
        using var tree = await GitHubDeviceFlowClient.ReadJsonAsync(treeResponse, cancellationToken).ConfigureAwait(false);
        var treeSha = GitHubDeviceFlowClient.RequiredString(tree.RootElement, "sha");

        using var bulkCommitResponse = await SendAsync(HttpMethod.Post,
            $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/git/commits",
            token,
            new { message = "AchievementSync: sync achievement data", tree = treeSha, parents = new[] { baseCommit } },
            cancellationToken).ConfigureAwait(false);
        using var bulkNewCommit = await GitHubDeviceFlowClient.ReadJsonAsync(bulkCommitResponse, cancellationToken).ConfigureAwait(false);
        var newCommitSha = GitHubDeviceFlowClient.RequiredString(bulkNewCommit.RootElement, "sha");

        using var updateResponse = await SendAsync(new HttpMethod("PATCH"), UpdateRefPath(target), token,
            new { sha = newCommitSha, force = false }, cancellationToken).ConfigureAwait(false);
        _ = await GitHubDeviceFlowClient.ReadJsonAsync(updateResponse, cancellationToken).ConfigureAwait(false);

        return new BulkSyncResult(true,
            $"https://github.com/{target.Owner}/{target.Repository}/commit/{newCommitSha}",
            achievementFilesPushed, achievementFilesUnchanged, achievementFilesFailed, entries);
    }

    /// <summary>
    /// Pushes one commit containing the achievements.json, the discovered save files, and the restoration
    /// manifest for each AppID. Save files are skipped when the remote manifest already records the same
    /// SHA-256; achievements.json files are merged with the remote file (remote unlocks preserved). Only
    /// AppID-scoped paths are ever written. Per-file failures are reported without aborting the sync.
    /// </summary>
    public async Task<SaveBackupResult> PushSaveBackupAsync(
        string token,
        GitHubSyncTarget target,
        IReadOnlyList<SaveBackupPlan> plans,
        IProgress<SaveBackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);

        var totalFiles = plans.Sum(plan => plan.Items.Count + 1); // one extra slot per plan for the manifest
        var totalBytes = plans.Sum(plan => plan.Manifest.Saves.Sum(s => s.Size)); // Content is lazy now
        var filesProcessed = 0;
        long bytesProcessed = 0;
        progress?.Report(new SaveBackupProgress(filesProcessed, totalFiles, bytesProcessed, totalBytes, "Preparing backup plan…"));

        var repository = await GetRepositoryAsync(token, target.Owner, target.Repository, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(repository.FullName, $"{target.Owner}/{target.Repository}", StringComparison.OrdinalIgnoreCase))
            throw new GitHubApiException("GitHub redirected the repository name. Verify the owner and repository again.");
        if (!repository.CanPush) throw new GitHubApiException("The connected GitHub account does not have push access to this repository.");
        if (repository.IsPrivate) throw new GitHubApiException("AchievementSync requests access to public repositories only. Choose a public fork.");

        using var referenceResponse = await SendAsync(HttpMethod.Get, RefPath(target), token, null, cancellationToken).ConfigureAwait(false);
        using var reference = await GitHubDeviceFlowClient.ReadJsonAsync(referenceResponse, cancellationToken).ConfigureAwait(false);
        var baseCommit = GitHubDeviceFlowClient.RequiredString(reference.RootElement.GetProperty("object"), "sha");
        using var commitResponse = await SendAsync(HttpMethod.Get, $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/git/commits/{Escape(baseCommit)}", token, null, cancellationToken).ConfigureAwait(false);
        using var commit = await GitHubDeviceFlowClient.ReadJsonAsync(commitResponse, cancellationToken).ConfigureAwait(false);
        var baseTree = GitHubDeviceFlowClient.RequiredString(commit.RootElement.GetProperty("tree"), "sha");

        // Parallel manifest prefetch: one GET per backup folder, all at once. Previously sequential per AppID,
        // which dominated startup time when there are many games.
        var remoteManifests = new System.Collections.Concurrent.ConcurrentDictionary<string, RestorationManifest>(StringComparer.OrdinalIgnoreCase);
        await Task.WhenAll(plans.Select(async plan =>
        {
            var folder = plan.EffectiveRepositoryFolder;
            try
            {
                ValidateBackupFolder(folder);
                var json = await ReadRemoteTextAsync(token, target, baseCommit, $"{folder}/restoration-manifest.json", cancellationToken).ConfigureAwait(false);
                var manifest = RestorationManifest.FromJson(json);
                if (manifest is not null) remoteManifests[folder] = manifest;
            }
            catch (Exception ex) when (ex is GitHubApiException or IOException or ArgumentException)
            {
                remoteManifests[folder] = RestorationManifest.Create(plan.AppId, "json", null, null, [], folder);
            }
        })).ConfigureAwait(false);

        var report = new List<string>();
        var treeEntries = new List<object>();
        var pushed = 0;
        var unchanged = 0;
        var failed = 0;

        foreach (var plan in plans.OrderBy(plan => plan.AppId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repositoryFolder = plan.EffectiveRepositoryFolder;
            ValidateBackupFolder(repositoryFolder);
            var manifestPath = $"{repositoryFolder}/restoration-manifest.json";
            progress?.Report(new SaveBackupProgress(filesProcessed, totalFiles, bytesProcessed, totalBytes, $"Reading {plan.AppId} manifest…"));

            var remoteManifest = remoteManifests.GetValueOrDefault(repositoryFolder);
            var remoteSaves = remoteManifest?.Saves
                .GroupBy(entry => entry.RepoPath, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Sha256, StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);

            // Phase B: classify items and detect no-ops. SHA-256 comes from discovery (stored on the item),
            // so no re-hashing or byte loading happens here. Items needing upload are queued for Phase C.
            var toUpload = new List<SaveBackupItem>();
            foreach (var item in plan.Items.OrderBy(item => item.RepoPath, StringComparer.Ordinal))
            {
                try
                {
                    var expectedPrefix = repositoryFolder + "/";
                    if (string.IsNullOrEmpty(item.RepoPath)
                        || !item.RepoPath.StartsWith(expectedPrefix, StringComparison.Ordinal)
                        || item.RepoPath.Contains("..", StringComparison.Ordinal))
                        throw new InvalidDataException("A backup item contained a path outside its backup folder.");

                    if (string.Equals(item.RepoPath, $"AppID/{plan.AppId}/achievements.json", StringComparison.Ordinal))
                    {
                        var remoteContent = await ReadAchievementFileAsync(token, target, baseCommit, plan.AppId, cancellationToken).ConfigureAwait(false);
                        var merged = AchievementJsonMerge.Merge(
                            item.Content is not null ? System.Text.Encoding.UTF8.GetString(item.Content) : string.Empty, remoteContent);
                        if (remoteContent is not null && AchievementJsonMerge.SemanticallyEqual(remoteContent, merged))
                        {
                            unchanged++;
                            report.Add($"{plan.AppId} — achievements.json unchanged");
                            filesProcessed++;
                            bytesProcessed += item.Content?.Length ?? 0;
                            progress?.Report(new SaveBackupProgress(filesProcessed, totalFiles, bytesProcessed, totalBytes, $"{filesProcessed}/{totalFiles} files: {plan.AppId}"));
                            continue;
                        }
                        var sha = await CreateBlobAsync(token, target, merged, encoding: "utf-8", cancellationToken).ConfigureAwait(false);
                        treeEntries.Add(new { path = item.RepoPath, mode = "100644", type = "blob", sha });
                        pushed++;
                        report.Add($"{plan.AppId} — achievements.json pushed");
                        filesProcessed++;
                        bytesProcessed += item.Content?.Length ?? 0;
                        progress?.Report(new SaveBackupProgress(filesProcessed, totalFiles, bytesProcessed, totalBytes, $"{filesProcessed}/{totalFiles} files: {plan.AppId}"));
                        continue;
                    }

                    if (!item.RepoPath.StartsWith(repositoryFolder + "/save/", StringComparison.Ordinal))
                        throw new InvalidDataException("A backup item contained an unexpected path.");

                    var sha256 = item.Sha256;
                    if (remoteSaves.TryGetValue(item.RepoPath, out var recorded) && string.Equals(recorded, sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        unchanged++;
                        report.Add($"{plan.AppId} — {item.RepoPath} unchanged");
                        filesProcessed++;
                        bytesProcessed += item.SizeBytes;
                        progress?.Report(new SaveBackupProgress(filesProcessed, totalFiles, bytesProcessed, totalBytes, $"{filesProcessed}/{totalFiles} files: {plan.AppId}"));
                        continue;
                    }

                    toUpload.Add(item);
                }
                catch (Exception exception) when (exception is GitHubApiException or InvalidDataException or IOException or ArgumentException)
                {
                    failed++;
                    report.Add($"{plan.AppId} — {item.RepoPath} failed: {exception.Message}");
                    filesProcessed++;
                    bytesProcessed += item.SizeBytes;
                }
            }

            // Phase C (parallel): upload blobs that need creating. Throttled to 2 concurrent
            // requests — any higher risks GitHub closing the connection (HttpRequestException,
            // socket forcibly closed). The inner CreateBlobAsync also retries transient failures.
            if (toUpload.Count > 0)
            {
                var blobShaOf = new ConcurrentDictionary<SaveBackupItem, string?>(ReferenceEqualityComparer.Instance);
                var failMessages = new ConcurrentDictionary<SaveBackupItem, string>(ReferenceEqualityComparer.Instance);
                await Parallel.ForEachAsync(
                    toUpload,
                    new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken },
                    async (item, ct) =>
                    {
                        try
                        {
                            byte[] content;
                            try
                            {
                                await using var stream = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                    256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                                using var buffer = new MemoryStream();
                                await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
                                content = buffer.ToArray();
                            }
                            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                            {
                                blobShaOf[item] = null;
                                failMessages[item] = exception.Message;
                                return;
                            }
                            var saveSha = await CreateBlobAsync(token, target, Convert.ToBase64String(content), encoding: "base64", ct).ConfigureAwait(false);
                            blobShaOf[item] = saveSha;
                        }
                        catch (Exception exception) when (exception is GitHubApiException or InvalidDataException or IOException or ArgumentException)
                        {
                            blobShaOf[item] = null;
                            failMessages[item] = exception.Message;
                        }
                    }).ConfigureAwait(false);

                // Drain results in deterministic order
                foreach (var item in toUpload)
                {
                    if (blobShaOf.TryGetValue(item, out var saveSha) && saveSha is not null)
                    {
                        treeEntries.Add(new { path = item.RepoPath, mode = "100644", type = "blob", sha = saveSha });
                        pushed++;
                        report.Add($"{plan.AppId} — {item.RepoPath} pushed");
                    }
                    else
                    {
                        failed++;
                        var msg = failMessages.TryGetValue(item, out var m) ? m : "unknown error";
                        report.Add($"{plan.AppId} — {item.RepoPath} failed: {msg}");
                    }
                    filesProcessed++;
                    bytesProcessed += item.SizeBytes;
                    progress?.Report(new SaveBackupProgress(filesProcessed, totalFiles, bytesProcessed, totalBytes, $"{filesProcessed}/{totalFiles} files: {plan.AppId}"));
                }
            }

            try
            {
                var mergedSaves = (remoteManifest?.Saves ?? [])
                    .Concat(plan.Manifest.Saves)
                    .GroupBy(entry => entry.RepoPath, StringComparer.Ordinal)
                    .Select(group => group.Last())
                    .OrderBy(entry => entry.RepoPath, StringComparer.Ordinal)
                    .ToArray();
                var mergedManifest = plan.Manifest with
                {
                    AchievementSourcePath = plan.Manifest.AchievementSourcePath ?? remoteManifest?.AchievementSourcePath,
                    AchievementSourceName = plan.Manifest.AchievementSourceName ?? remoteManifest?.AchievementSourceName,
                    Saves = mergedSaves,
                    RepositoryFolder = repositoryFolder
                };
                var manifestText = mergedManifest.ToJson();
                var currentManifest = await ReadRemoteTextAsync(token, target, baseCommit, manifestPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(currentManifest, manifestText, StringComparison.Ordinal))
                {
                    var manifestSha = await CreateBlobAsync(token, target, manifestText, encoding: "utf-8", cancellationToken).ConfigureAwait(false);
                    treeEntries.Add(new { path = manifestPath, mode = "100644", type = "blob", sha = manifestSha });
                    pushed++;
                    report.Add($"{plan.AppId} — restoration-manifest.json pushed");
                }
                else
                {
                    unchanged++;
                    report.Add($"{plan.AppId} — restoration-manifest.json unchanged");
                }
            }
            catch (Exception exception) when (exception is GitHubApiException or IOException or ArgumentException)
            {
                failed++;
                report.Add($"{plan.AppId} — restoration-manifest.json failed: {exception.Message}");
            }

            // Account for the per-plan manifest as a virtual file slot so totals match.
            filesProcessed++;
            progress?.Report(new SaveBackupProgress(filesProcessed, totalFiles, bytesProcessed, totalBytes, $"{filesProcessed}/{totalFiles} files: {plan.AppId} manifest done"));
        }

        if (treeEntries.Count == 0)
        {
            progress?.Report(new SaveBackupProgress(totalFiles, totalFiles, totalBytes, totalBytes, "Nothing to upload"));
            return new SaveBackupResult(false, null, pushed, unchanged, failed, report);
        }

        using var treeResponse = await SendAsync(HttpMethod.Post,
            $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/git/trees",
            token,
            new { base_tree = baseTree, tree = treeEntries },
            cancellationToken).ConfigureAwait(false);
        using var tree = await GitHubDeviceFlowClient.ReadJsonAsync(treeResponse, cancellationToken).ConfigureAwait(false);
        var treeSha = GitHubDeviceFlowClient.RequiredString(tree.RootElement, "sha");

        using var backupCommitResponse = await SendAsync(HttpMethod.Post,
            $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/git/commits",
            token,
            new { message = "AchievementSync: back up game saves and achievements", tree = treeSha, parents = new[] { baseCommit } },
            cancellationToken).ConfigureAwait(false);
        progress?.Report(new SaveBackupProgress(totalFiles, totalFiles, totalBytes, totalBytes, "Pushed commit to branch"));
        using var backupNewCommit = await GitHubDeviceFlowClient.ReadJsonAsync(backupCommitResponse, cancellationToken).ConfigureAwait(false);
        var newCommitSha = GitHubDeviceFlowClient.RequiredString(backupNewCommit.RootElement, "sha");

        using var updateResponse = await SendAsync(new HttpMethod("PATCH"), UpdateRefPath(target), token,
            new { sha = newCommitSha, force = false }, cancellationToken).ConfigureAwait(false);
        _ = await GitHubDeviceFlowClient.ReadJsonAsync(updateResponse, cancellationToken).ConfigureAwait(false);

        progress?.Report(new SaveBackupProgress(totalFiles, totalFiles, totalBytes, totalBytes, "Backup pushed"));
        return new SaveBackupResult(true,
            $"https://github.com/{target.Owner}/{target.Repository}/commit/{newCommitSha}",
            pushed, unchanged, failed, report);
    }

    /// <summary>Reads one AppID's backup from the repository: the manifest, achievements.json, and every save file.</summary>
    public async Task<IReadOnlyList<RestorePlan>> PullSaveBackupAsync(
        string token,
        GitHubSyncTarget target,
        IReadOnlyCollection<string>? appIds = null,
        PlatformSourceOptions? localOptions = null,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
        using var referenceResponse = await SendAsync(HttpMethod.Get, RefPath(target), token, null, cancellationToken).ConfigureAwait(false);
        using var reference = await GitHubDeviceFlowClient.ReadJsonAsync(referenceResponse, cancellationToken).ConfigureAwait(false);
        var headSha = GitHubDeviceFlowClient.RequiredString(reference.RootElement.GetProperty("object"), "sha");
        using var commitResponse = await SendAsync(HttpMethod.Get,
            $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/git/commits/{Escape(headSha)}",
            token, null, cancellationToken).ConfigureAwait(false);
        using var commit = await GitHubDeviceFlowClient.ReadJsonAsync(commitResponse, cancellationToken).ConfigureAwait(false);
        var treeSha = GitHubDeviceFlowClient.RequiredString(commit.RootElement.GetProperty("tree"), "sha");
        using var treeResponse = await SendAsync(HttpMethod.Get,
            $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/git/trees/{Escape(treeSha)}?recursive=1",
            token, null, cancellationToken).ConfigureAwait(false);
        using var tree = await GitHubDeviceFlowClient.ReadJsonAsync(treeResponse, cancellationToken).ConfigureAwait(false);
        if (tree.RootElement.TryGetProperty("truncated", out var truncated)
            && truncated.ValueKind == System.Text.Json.JsonValueKind.True)
            throw new GitHubApiException("The repository tree is too large to enumerate for restore. Restore specific games after they appear in the library.");

        var paths = tree.RootElement.TryGetProperty("tree", out var entries)
            && entries.ValueKind == System.Text.Json.JsonValueKind.Array
            ? entries.EnumerateArray()
                .Select(item => item.TryGetProperty("path", out var path) && path.ValueKind == System.Text.Json.JsonValueKind.String
                    ? path.GetString()
                    : null)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var requestedIds = appIds?.Where(AppIdValidator.IsValid).ToHashSet(StringComparer.Ordinal);
        var manifestPaths = paths.Where(path => path.EndsWith("/restoration-manifest.json", StringComparison.Ordinal)
                && (path.StartsWith("AppID/", StringComparison.Ordinal)
                    || path.StartsWith("PlatformSave/", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var plans = new List<RestorePlan>();
        var restoredAppIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var manifestPath in manifestPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repositoryFolder = manifestPath[..^"/restoration-manifest.json".Length];
            ValidateBackupFolder(repositoryFolder);
            var segments = repositoryFolder.Split('/');
            var isAppIdFolder = segments.Length == 2 && segments[0] == "AppID" && AppIdValidator.IsValid(segments[1]);
            var pathAppId = isAppIdFolder ? segments[1] : string.Empty;
            if (isAppIdFolder && requestedIds is { Count: > 0 } && !requestedIds.Contains(pathAppId)) continue;

            var manifest = RestorationManifest.FromJson(await ReadRemoteTextAsync(token, target, headSha,
                manifestPath, cancellationToken).ConfigureAwait(false));
            if (manifest is null) continue;
            var planId = isAppIdFolder ? pathAppId : manifest.AppId;
            if (string.IsNullOrWhiteSpace(planId)) continue;
            var effectiveManifest = manifest with { RepositoryFolder = repositoryFolder };
            string? achievementsJson = isAppIdFolder
                ? await ReadAchievementFileAsync(token, target, headSha, pathAppId, cancellationToken).ConfigureAwait(false)
                : null;
            var saves = new List<RestoreItem>();
            foreach (var entry in effectiveManifest.Saves)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.RepoPath.StartsWith(repositoryFolder + "/save/", StringComparison.Ordinal)
                    || !IsSafeRepositoryRelativePath(entry.RepoPath)
                    || !paths.Contains(entry.RepoPath)) continue;
                var targetPath = SaveFileDiscoverer.ResolveLocalPath(entry.RootKey, planId, entry.RelativePath,
                    entry.SourcePath, localOptions);
                if (targetPath is null) continue;
                saves.Add(new RestoreItem(entry.RepoPath, targetPath, entry.Size, false));
            }

            plans.Add(new RestorePlan(planId, headSha, effectiveManifest, achievementsJson, saves, repositoryFolder));
            if (isAppIdFolder) restoredAppIds.Add(pathAppId);
        }

        // Achievement exports do not need a save-backup manifest. Restore them into the local metadata cache
        // so a clean PC can rebuild its library from the user's GitHub branch.
        foreach (var path in paths.Where(path => path.StartsWith("AppID/", StringComparison.Ordinal)
                     && path.EndsWith("/achievements.json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var segments = path.Split('/');
            if (segments.Length != 3 || segments[0] != "AppID" || !AppIdValidator.IsValid(segments[1])) continue;
            var appId = segments[1];
            if (restoredAppIds.Contains(appId) || (requestedIds is { Count: > 0 } && !requestedIds.Contains(appId))) continue;
            var json = await ReadAchievementFileAsync(token, target, headSha, appId, cancellationToken).ConfigureAwait(false);
            if (json is null) continue;
            var manifest = RestorationManifest.Create(appId, "json", null, null, [], $"AppID/{appId}");
            plans.Add(new RestorePlan(appId, headSha, manifest, json, [], $"AppID/{appId}"));
        }

        return plans.OrderBy(plan => plan.RepositoryFolder, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Reads one file from the repository as raw bytes (streamed through a single base64 response).</summary>
    public async Task<byte[]?> ReadRestoreFileAsync(
        string token,
        GitHubSyncTarget target,
        string headSha,
        string repoPath,
        CancellationToken cancellationToken = default)
    {
        var path = $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/contents/{repoPath}?ref={Uri.EscapeDataString(headSha)}";
        using var response = await SendAsync(HttpMethod.Get, path, token, null, cancellationToken, allowNotFound: true).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var json = await GitHubDeviceFlowClient.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        if (GitHubDeviceFlowClient.OptionalString(root, "encoding") != "base64"
            || !root.TryGetProperty("content", out var content))
        {
            return null;
        }

        var encoded = GitHubDeviceFlowClient.RequiredString(root, "content").Replace("\n", "").Replace("\r", "");
        try { return Convert.FromBase64String(encoded); }
        catch (FormatException exception) { throw new GitHubApiException("A remote backup file is not valid Base64.", exception); }
    }

    private async Task<string?> ReadRemoteTextAsync(
        string token,
        GitHubSyncTarget target,
        string commitSha,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var path = $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/contents/{relativePath}?ref={Uri.EscapeDataString(commitSha)}";
        using var response = await SendAsync(HttpMethod.Get, path, token, null, cancellationToken, allowNotFound: true);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var json = await GitHubDeviceFlowClient.ReadJsonAsync(response, cancellationToken);
        var root = json.RootElement;
        if (GitHubDeviceFlowClient.OptionalString(root, "encoding") != "base64"
            || !root.TryGetProperty("content", out var content))
        {
            return null;
        }

        var encoded = GitHubDeviceFlowClient.RequiredString(root, "content").Replace("\n", "").Replace("\r", "");
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); }
        catch (FormatException exception) { throw new GitHubApiException("A remote backup file is not valid Base64.", exception); }
    }

    private static readonly HttpMethod GetMethod = HttpMethod.Get;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];

    /// <summary>Classify an exception as transient (eligible for retry).</summary>
    private static bool IsTransient(Exception exception)
    {
        // HttpRequestException wraps IOException / SocketException; 5xx / 429 / 408 all surface as ApiException here handled inside SendAsync.
        return exception is HttpRequestException or IOException or InvalidOperationException
            or TaskCanceledException or TimeoutException or System.Net.Sockets.SocketException;
    }

    private async Task<string> CreateBlobAsync(string token, GitHubSyncTarget target, string content, string encoding, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            try
            {
                using var blobResponse = await SendAsync(HttpMethod.Post,
                    $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/git/blobs",
                    token,
                    new { content, encoding },
                    cancellationToken).ConfigureAwait(false);
                using var blob = await GitHubDeviceFlowClient.ReadJsonAsync(blobResponse, cancellationToken).ConfigureAwait(false);
                return GitHubDeviceFlowClient.RequiredString(blob.RootElement, "sha");
            }
            catch (Exception exception) when (IsTransient(exception) && attempt < RetryDelays.Length && !cancellationToken.IsCancellationRequested)
            {
                lastError = exception;
                await Task.Delay(RetryDelays[attempt], cancellationToken).ConfigureAwait(false);
            }
        }

        throw new GitHubApiException($"The GitHub restore remote closed the connection while uploading a blob. Last error: {lastError?.Message ?? "unknown"}. Please try the backup again.");
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
        request.Headers.UserAgent.ParseAdd("AchievementSync/1.0.0");
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

    public static void ValidateExportFilePath(string relativePath, string appId)
    {
        if (string.IsNullOrEmpty(appId) || !appId.All(char.IsAsciiDigit))
            throw new InvalidDataException("A sync preview contained a path outside AppID/<numeric id>/achievements.json.");

        var prefix = $"AppID/{appId}/";
        if (!relativePath.StartsWith(prefix, StringComparison.Ordinal) || relativePath.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("A sync preview contained a path outside AppID/<numeric id>/achievements.json.");

        var filename = relativePath[prefix.Length..];
        if (string.Equals(filename, "achievements.json", StringComparison.Ordinal))
            return;
        if (string.Equals(filename, "blacklist", StringComparison.OrdinalIgnoreCase))
            return;
        if (filename.EndsWith(".platform", StringComparison.OrdinalIgnoreCase)
            && filename.Length > ".platform".Length
            && filename[..^".platform".Length].All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            return;

        throw new InvalidDataException($"A sync preview contained an unexpected file name: {filename}");
    }

    private static void ValidateChange(GitHubSyncChange change)
    {
        ValidateExportFilePath(change.RelativePath, change.AppId);
        if (change.RelativePath.EndsWith("achievements.json", StringComparison.Ordinal))
        {
            _ = AchievementJsonMerge.Merge(change.ProposedJson, change.ExistingJson);
        }
    }

    private static string RefPath(GitHubSyncTarget target) =>
        $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/git/ref/heads/{target.Branch}";

    private static string UpdateRefPath(GitHubSyncTarget target) =>
        $"repos/{Escape(target.Owner)}/{Escape(target.Repository)}/git/refs/heads/{target.Branch}";

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static void ValidateBackupFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !IsSafeRepositoryRelativePath(folder))
            throw new InvalidDataException("A backup folder contained an invalid repository path.");

        var segments = folder.Split('/');
        var validAppId = segments.Length == 2
            && segments[0] == "AppID"
            && AppIdValidator.IsValid(segments[1]);
        var validPlatformSave = segments.Length == 4
            && segments[0] == "PlatformSave"
            && segments.Skip(1).All(IsSafeRepositorySegment);
        if (!validAppId && !validPlatformSave)
            throw new InvalidDataException("A backup folder must be AppID/<id> or PlatformSave/<platform>/<root>/<game>.");
    }

    private static bool IsSafeRepositoryRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !path.StartsWith("/", StringComparison.Ordinal)
        && !path.Contains('\\')
        && path.Split('/').All(IsSafeRepositoryFileSegment);

    private static bool IsSafeRepositoryFileSegment(string segment) =>
        !string.IsNullOrWhiteSpace(segment)
        && segment is not "." and not ".."
        && segment.All(character => !char.IsControl(character) && "<>:\"|?*".IndexOf(character) < 0);

    private static bool IsSafeRepositorySegment(string segment) =>
        !string.IsNullOrWhiteSpace(segment)
        && segment is not "." and not ".."
        && !segment.Contains("..", StringComparison.Ordinal)
        && segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

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
