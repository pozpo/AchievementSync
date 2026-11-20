using System.Text;
using System.Text.Json;
using AchievementSync.Core.Scanning.PlatformAdapters;

namespace AchievementSync.Core.Platforms;

public sealed record PlatformWriteReport(int GamesWritten, int FilesWritten, int Failed, IReadOnlyList<string> Messages);

/// <summary>Converts platform snapshots to the viewer JSON contract and writes them under the local
/// metadata folder. Steam data keeps its numeric AppID folder; every other platform uses
/// <c>_platforms/&lt;platform&gt;/&lt;id&gt;</c> so console and store ids can never overwrite Steam AppIDs.</summary>
public sealed class PlatformSnapshotStore
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _root;

    public PlatformSnapshotStore(string? rootDirectory = null)
    {
        _root = Path.GetFullPath(rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AchievementSync", "metadata"));
    }

    public string RootDirectory => _root;

    public string DirectoryFor(string platform, string gameId)
    {
        var safeId = Sanitize(gameId);
        return platform.Equals("steam", StringComparison.OrdinalIgnoreCase) && IsNumericId(safeId)
            ? Path.Combine(_root, safeId)
            : Path.Combine(_root, "_platforms", Sanitize(platform), safeId);
    }

    public async Task<PlatformWriteReport> SaveAsync(IReadOnlyList<PlatformGameSnapshot> snapshots,
        CancellationToken cancellationToken = default)
    {
        var written = 0;
        var files = 0;
        var failed = 0;
        var messages = new List<string>();
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.Achievements.Count == 0) continue;
            try
            {
                var directory = DirectoryFor(snapshot.Platform, snapshot.GameId);
                Directory.CreateDirectory(directory);
                var states = await MergeStatesAsync(Path.Combine(directory, "achievements.json"), snapshot, cancellationToken)
                    .ConfigureAwait(false);
                await WriteAtomicAsync(Path.Combine(directory, "achievements.json"), states, cancellationToken).ConfigureAwait(false);
                var info = BuildPlatformInfo(snapshot);
                await WriteAtomicAsync(Path.Combine(directory, "platform-info.json"), info, cancellationToken).ConfigureAwait(false);
                written++;
                files += 2;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                failed++;
                messages.Add($"{snapshot.Platform}: {snapshot.GameId} could not be written ({exception.GetType().Name}).");
            }
        }

        return new PlatformWriteReport(written, files, failed, messages);
    }

    private static async Task<byte[]> MergeStatesAsync(string path, PlatformGameSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var merged = new SortedDictionary<string, PlatformAchievementState>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 64
                }, cancellationToken).ConfigureAwait(false);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind != JsonValueKind.Object) continue;
                        var earned = false;
                        long earnedTime = 0;
                        foreach (var field in property.Value.EnumerateObject())
                        {
                            if (field.NameEquals("earned")) earned = field.Value.ValueKind == JsonValueKind.True;
                            else if (field.NameEquals("earned_time") && field.Value.TryGetInt64(out var time)) earnedTime = time;
                        }
                        merged[property.Name] = new PlatformAchievementState(earned, earnedTime);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                // A malformed existing file is replaced by the freshly converted snapshot.
            }
        }

        foreach (var achievement in snapshot.Achievements.Where(item => item.EarnedKnown))
        {
            if (string.IsNullOrWhiteSpace(achievement.Id)) continue;
            if (merged.TryGetValue(achievement.Id, out var existing))
            {
                merged[achievement.Id] = new PlatformAchievementState(
                    existing.Earned || achievement.Earned,
                    Math.Max(existing.EarnedTime, achievement.EarnedTimeUnix));
            }
            else
            {
                merged[achievement.Id] = new PlatformAchievementState(achievement.Earned, achievement.EarnedTimeUnix);
            }
        }

        return JsonSerializer.SerializeToUtf8Bytes(merged, JsonOptions);
    }

    private static byte[] BuildPlatformInfo(PlatformGameSnapshot snapshot)
    {
        var document = new
        {
            platform = snapshot.Platform,
            gameId = snapshot.GameId,
            title = snapshot.Title,
            source = snapshot.SourcePath,
            version = snapshot.Version,
            schemaVersion = 1,
            collectedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            unlocked = snapshot.UnlockedCount,
            total = snapshot.Achievements.Count,
            achievements = snapshot.Achievements.Select(item => new
            {
                id = item.Id,
                name = item.Name,
                description = item.Description,
                earned = item.Earned,
                earnedKnown = item.EarnedKnown,
                earned_time = item.EarnedTimeUnix,
                icon = item.IconPath,
                icon_gray = item.IconGrayPath,
                trophy_type = item.TrophyType,
                rarity = item.RarityPercent,
                hidden = item.Hidden
            }).ToArray()
        };
        return JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
    }

    private static async Task WriteAtomicAsync(string path, byte[] contents, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_');
        var result = builder.ToString().Trim('.', ' ');
        return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
    }

    private static bool IsNumericId(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);
}

public sealed record PlatformRunStatus(
    string Platform,
    string DisplayName,
    bool IsConfigured,
    string? Hint,
    int GamesFound,
    string Status);

public sealed record PlatformScanReport(
    IReadOnlyList<PlatformRunStatus> Platforms,
    int GamesConverted,
    int FilesWritten,
    IReadOnlyList<string> Messages);

/// <summary>Runs every registered platform data adapter and writes the converted viewer JSON.</summary>
public sealed class PlatformScanService
{
    private readonly List<PlatformDataAdapter> _adapters = [];
    private readonly PlatformSnapshotStore _store;

    public PlatformScanService(PlatformSnapshotStore? store = null)
    {
        _store = store ?? new PlatformSnapshotStore();
    }

    public IReadOnlyList<PlatformDataAdapter> Adapters => _adapters;

    public void Register(PlatformDataAdapter adapter)
    {
        if (_adapters.All(item => !string.Equals(item.Platform, adapter.Platform, StringComparison.OrdinalIgnoreCase)))
            _adapters.Add(adapter);
    }

    public async Task<PlatformScanReport> ScanAsync(PlatformSourceOptions? options = null,
        IReadOnlyCollection<string>? onlyPlatforms = null,
        CancellationToken cancellationToken = default)
    {
        options ??= PlatformSourceOptions.CreateDefault();
        var statuses = new List<PlatformRunStatus>();
        var snapshots = new List<PlatformGameSnapshot>();
        var messages = new List<string>();
        foreach (var adapter in _adapters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (onlyPlatforms is not null && onlyPlatforms.Count > 0
                && !onlyPlatforms.Contains(adapter.Platform, StringComparer.OrdinalIgnoreCase)) continue;
            var isConfigured = adapter.IsConfiguredFor(options);
            if (!isConfigured)
            {
                statuses.Add(new PlatformRunStatus(adapter.Platform, adapter.DisplayName, false,
                    adapter.ConfigurationHint, 0, "Not configured"));
                continue;
            }

            try
            {
                var collected = await adapter.CollectAsync(options, cancellationToken).ConfigureAwait(false);
                snapshots.AddRange(collected.Games);
                messages.AddRange(collected.Messages);
                statuses.Add(new PlatformRunStatus(adapter.Platform, adapter.DisplayName, isConfigured, adapter.ConfigurationHint,
                    collected.Games.Count,
                    collected.Games.Count == 0 ? "No local achievement data found" : $"{collected.Games.Count} game(s) converted"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or InvalidDataException or HttpRequestException or TaskCanceledException)
            {
                statuses.Add(new PlatformRunStatus(adapter.Platform, adapter.DisplayName, isConfigured, adapter.ConfigurationHint, 0,
                    $"Failed: {exception.GetType().Name}"));
            }
        }

        var write = await _store.SaveAsync(snapshots, cancellationToken).ConfigureAwait(false);
        messages.AddRange(write.Messages);
        return new PlatformScanReport(statuses, write.GamesWritten, write.FilesWritten, messages);
    }
}
