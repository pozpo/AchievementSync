using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchievementSync.Core.Models;
using AchievementSync.Core.Parsing;
using AchievementSync.Core.Scanning;
using AchievementSync.Core.Scanning.PlatformAdapters;

namespace AchievementSync.Core.Metadata;

public sealed record MetadataCacheUpdateResult(int UpdatedFiles, int UnchangedFiles, int FailedFiles);

/// <summary>
/// Maintains a local, AppID-scoped metadata cache from files already discovered by the scanner.
/// Source achievement files are opened read-only and are never modified.
/// </summary>
public sealed class LocalAchievementMetadataCache(string? rootDirectory = null)
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _rootDirectory = rootDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AchievementSync", "metadata");
    private readonly IniAchievementParser _iniParser = new();
    private readonly PlatformAdapterRegistry? _platformAdapters;

    public LocalAchievementMetadataCache(string? rootDirectory, PlatformAdapterRegistry? platformAdapters)
        : this(rootDirectory)
    {
        _platformAdapters = platformAdapters;
    }

    public async Task<MetadataCacheUpdateResult> UpdateAsync(
        IReadOnlyList<DiscoveredAchievementFile> sourceFiles,
        CancellationToken cancellationToken = default)
    {
        return await UpdateAsync(sourceFiles, _platformAdapters, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Scan local sources, then resolve any configured platform adapters for AppIDs that still
    /// lack a cached achievements.json, writing viewer-compatible JSON to Metadata/&lt;AppID&gt;/.</summary>
    public async Task<MetadataCacheUpdateResult> UpdateAsync(
        IReadOnlyList<DiscoveredAchievementFile> sourceFiles,
        PlatformAdapterRegistry? platformAdapters,
        CancellationToken cancellationToken = default)
    {
        var updated = 0;
        var unchanged = 0;
        var failed = 0;

        foreach (var appGroup in sourceFiles
                     .Where(file => AppIdValidator.IsValid(file.AppId))
                     .GroupBy(file => file.AppId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var database in appGroup
                         .Where(file => ExtensionIs(file, ".db"))
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                         .Take(1))
            {
                var outcome = await CopySourceIfNewerAsync(database,
                    Path.Combine(appGroup.Key, appGroup.Key + ".db"), cancellationToken).ConfigureAwait(false);
                Count(outcome, ref updated, ref unchanged, ref failed);
            }

            var profileCandidates = appGroup
                .Where(file => file.ParsedSuccessfully
                    && (string.Equals(file.FileName, "achievements.json", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(file.FileName, "achievements.ini", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(file.FileName, "achiev.ini", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var candidate in profileCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(candidate.FileName, "achievements.json", StringComparison.OrdinalIgnoreCase))
                {
                    if (!await IsViewerCompatibleJsonAsync(candidate.Path, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    var outcome = await CopySourceIfNewerAsync(candidate,
                        Path.Combine(appGroup.Key, "achievements.json"), cancellationToken).ConfigureAwait(false);
                    Count(outcome, ref updated, ref unchanged, ref failed);
                    break;
                }

                if (!TryConvertIni(candidate, out var json)) continue;
                var write = await WriteBytesIfNewerAsync(candidate, Path.Combine(appGroup.Key, "achievements.json"),
                    json, cancellationToken).ConfigureAwait(false);
                Count(write, ref updated, ref unchanged, ref failed);
                break;
            }
        }

        // Platform adapters: fill in Metadata/&lt;AppID&gt;/achievements.json for AppIDs still missing a cache file.
        if (platformAdapters is not null)
        {
            var missing = sourceFiles
                .Where(file => AppIdValidator.IsValid(file.AppId))
                .Select(file => file.AppId)
                .Distinct(StringComparer.Ordinal)
                .Where(appId => !File.Exists(Path.Combine(_rootDirectory, appId, "achievements.json")))
                .ToList();

            foreach (var appId in missing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PlatformAdapter? chosen = null;
                foreach (var platform in platformAdapters.ConfiguredPlatforms())
                {
                    if (platformAdapters.TryGet(platform, out var adapter) && adapter is not null && adapter.IsConfigured)
                    {
                        chosen = adapter;
                        break;
                    }
                }
                if (chosen is null) continue;

                var resolve = await chosen.ResolveAsync(appId, cancellationToken).ConfigureAwait(false);
                if (!resolve.Succeeded || resolve.States is null || resolve.States.Count == 0) continue;

                var states = new SortedDictionary<string, PlatformAchievementState>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in resolve.States)
                    states[kv.Key] = kv.Value;
                var json = JsonSerializer.SerializeToUtf8Bytes(states, JsonOptions);
                var destination = Path.Combine(_rootDirectory, appId, "achievements.json");
                try
                {
                    await WriteAtomicBytesAsync(destination, json, cancellationToken).ConfigureAwait(false);
                    updated++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    failed++;
                }
            }
        }

        return new MetadataCacheUpdateResult(updated, unchanged, failed);
    }

    private async Task<WriteOutcome> CopySourceIfNewerAsync(
        DiscoveredAchievementFile source,
        string relativeDestination,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(_rootDirectory, relativeDestination);
        try
        {
            var info = new FileInfo(source.Path);
            if (!info.Exists || info.LastWriteTimeUtc != source.LastWriteTimeUtc) return WriteOutcome.Failed;
            if (!IsNewerThanDestination(source.LastWriteTimeUtc, destination)) return WriteOutcome.Unchanged;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var input = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }

                if (!IsSourceUnchanged(source, info.Length)
                    || (string.Equals(source.FileName, "achievements.json", StringComparison.OrdinalIgnoreCase)
                        && !await IsViewerCompatibleJsonAsync(temporary, cancellationToken).ConfigureAwait(false)))
                {
                    return WriteOutcome.Failed;
                }

                File.Move(temporary, destination, overwrite: true);
                PreserveSourceTimestamp(destination, source.LastWriteTimeUtc);
                return WriteOutcome.Updated;
            }
            finally
            {
                TryDeleteTemporaryFile(temporary);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return WriteOutcome.Failed;
        }
    }

    private async Task<WriteOutcome> WriteBytesIfNewerAsync(
        DiscoveredAchievementFile source,
        string relativeDestination,
        byte[] contents,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(_rootDirectory, relativeDestination);
        try
        {
            var info = new FileInfo(source.Path);
            if (!info.Exists || info.LastWriteTimeUtc != source.LastWriteTimeUtc) return WriteOutcome.Failed;
            if (!IsNewerThanDestination(source.LastWriteTimeUtc, destination)) return WriteOutcome.Unchanged;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await output.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }

                if (!IsSourceUnchanged(source, info.Length)) return WriteOutcome.Failed;
                File.Move(temporary, destination, overwrite: true);
                PreserveSourceTimestamp(destination, source.LastWriteTimeUtc);
                return WriteOutcome.Updated;
            }
            finally
            {
                TryDeleteTemporaryFile(temporary);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return WriteOutcome.Failed;
        }
    }

    private bool TryConvertIni(DiscoveredAchievementFile source, out byte[] json)
    {
        json = [];
        var parsed = _iniParser.Parse(source.Path, source.AppId);
        if (parsed.Profile is not { } profile
            || profile.LastWriteTimeUtc.UtcDateTime != source.LastWriteTimeUtc
            || profile.Achievements.Count == 0)
        {
            return false;
        }

        var states = new SortedDictionary<string, PlatformAchievementState>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in profile.Achievements
                     .Where(item => item.IsUnlocked.HasValue && !string.IsNullOrWhiteSpace(item.ApiName))
                     .GroupBy(item => item.ApiName.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var records = group.ToArray();
            var earned = records.Any(item => item.IsUnlocked == true);
            var timestamps = records
                .Where(item => earned ? item.IsUnlocked == true : item.IsUnlocked == false)
                .Select(item => item.UnlockTimeUnix ?? 0)
                .ToArray();
            states[group.Key] = new PlatformAchievementState(earned, timestamps.Length == 0 ? 0 : timestamps.Max());
        }

        if (states.Count == 0) return false;
        json = JsonSerializer.SerializeToUtf8Bytes(states, JsonOptions);
        return true;
    }

    private static async Task<bool> IsViewerCompatibleJsonAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream,
                new JsonDocumentOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true },
                cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object
                    || !TryReadBoolean(entry.Value, "earned", out _))
                {
                    return false;
                }
            }

            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool TryReadBoolean(JsonElement element, string propertyName, out bool value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = property.Value.GetBoolean();
                return true;
            }
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var number) && number is 0 or 1)
            {
                value = number == 1;
                return true;
            }
            if (property.Value.ValueKind == JsonValueKind.String
                && bool.TryParse(property.Value.GetString(), out value)) return true;
            value = false;
            return false;
        }

        value = false;
        return false;
    }

    private bool IsNewerThanDestination(DateTime sourceTimeUtc, string destination)
    {
        try
        {
            return !File.Exists(destination) || sourceTimeUtc > File.GetLastWriteTimeUtc(destination);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsSourceUnchanged(DiscoveredAchievementFile source, long originalLength)
    {
        var info = new FileInfo(source.Path);
        info.Refresh();
        return info.Exists && info.Length == originalLength && info.LastWriteTimeUtc == source.LastWriteTimeUtc;
    }

    private static bool ExtensionIs(DiscoveredAchievementFile file, string extension) =>
        string.Equals(file.Extension, extension, StringComparison.OrdinalIgnoreCase);

    private static void PreserveSourceTimestamp(string destination, DateTime sourceTimeUtc)
    {
        try { File.SetLastWriteTimeUtc(destination, sourceTimeUtc); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static async Task WriteAtomicBytesAsync(string destination, byte[] contents, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporary);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void Count(WriteOutcome outcome, ref int updated, ref int unchanged, ref int failed)
    {
        switch (outcome)
        {
            case WriteOutcome.Updated: updated++; break;
            case WriteOutcome.Unchanged: unchanged++; break;
            case WriteOutcome.Failed: failed++; break;
        }
    }

    private enum WriteOutcome { Updated, Unchanged, Failed }
}
