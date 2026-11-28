using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchievementSync.Core.Synchronization;

/// <summary>A save file found under an AppID-scoped emulator/root folder, ready for backup.</summary>
public sealed record DiscoveredSaveFile(
    string AppId,
    string RootKey,
    string RelativePath,
    string SourcePath,
    long Size,
    string Sha256,
    string RepoPath,
    string? BackupFolder = null);

/// <summary>Progress snapshot for the save-backup upload pipeline. FilesProcessed/FilesTotal span the whole batch; BytesProcessed/BytesTotal likewise. Stage is a short human-readable hint.</summary>
public readonly record struct SaveBackupProgress(
    int FilesProcessed,
    int FilesTotal,
    long BytesProcessed,
    long BytesTotal,
    string? Stage);

/// <summary>One file in a backup commit. SHA-256 is already known from discovery; bytes are loaded lazily only when needed.</summary>
public sealed record SaveBackupItem(string AppId, string RepoPath, string SourcePath, string Sha256, byte[]? Content = null, long Size = 0)
{
    /// <summary>Known file size from discovery, used for progress accounting without reading bytes.</summary>
    public long SizeBytes => Content?.Length ?? (Size > 0 ? Size : (System.IO.File.Exists(SourcePath) ? new System.IO.FileInfo(SourcePath).Length : 0));
}

/// <summary>One save entry recorded in the restoration manifest.</summary>
public sealed record ManifestSaveEntry(
    [property: JsonPropertyName("rootKey")] string RootKey,
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("sourcePath")] string SourcePath,
    [property: JsonPropertyName("repoPath")] string RepoPath,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>
/// Per-AppID manifest pushed alongside the backup. It records whether the achievement data was originally
/// an INI file (so restore can convert back) and maps every save file to its original location.
/// </summary>
public sealed record RestorationManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("appId")] string AppId,
    [property: JsonPropertyName("achievementFormat")] string AchievementFormat,
    [property: JsonPropertyName("achievementSourcePath")] string? AchievementSourcePath,
    [property: JsonPropertyName("achievementSourceName")] string? AchievementSourceName,
    [property: JsonPropertyName("saves")] IReadOnlyList<ManifestSaveEntry> Saves,
    [property: JsonPropertyName("repositoryFolder")] string? RepositoryFolder = null)
{
    public static RestorationManifest Create(
        string appId,
        string achievementFormat,
        string? achievementSourcePath,
        string? achievementSourceName,
        IReadOnlyList<ManifestSaveEntry> saves,
        string? repositoryFolder = null) =>
        new(1, appId, achievementFormat, achievementSourcePath, achievementSourceName, saves, repositoryFolder);

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public static RestorationManifest? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<RestorationManifest>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The complete set of files for one AppID backup.</summary>
public sealed record SaveBackupPlan(
    string AppId,
    IReadOnlyList<SaveBackupItem> Items,
    RestorationManifest Manifest,
    string? RepositoryFolder = null)
{
    public string EffectiveRepositoryFolder => RepositoryFolder
        ?? Manifest.RepositoryFolder
        ?? $"AppID/{AppId}";
}

/// <summary>Aggregate result of a save backup push.</summary>
public sealed record SaveBackupResult(
    bool Pushed,
    string? CommitUrl,
    int FilesPushed,
    int FilesUnchanged,
    int FilesFailed,
    IReadOnlyList<string> Report)
{
    public static SaveBackupResult Empty { get; } = new(false, null, 0, 0, 0, []);

    public string Summary
    {
        get
        {
            var pushed = FilesPushed > 0 ? $"{FilesPushed:N0} file(s) backed up" : "nothing new to back up";
            var parts = new List<string> { pushed };
            if (FilesUnchanged > 0) parts.Add($"{FilesUnchanged:N0} already up to date");
            if (FilesFailed > 0) parts.Add($"{FilesFailed:N0} failed");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>One file to write during a restore, with its resolved local destination.</summary>
public sealed record RestoreItem(string RepoPath, string TargetPath, long Size, bool IsAchievementJson);

/// <summary>Everything needed to restore one AppID from the repository.</summary>
public sealed record RestorePlan(
    string AppId,
    string HeadSha,
    RestorationManifest Manifest,
    string? AchievementsJson,
    IReadOnlyList<RestoreItem> Saves,
    string? RepositoryFolder = null);
