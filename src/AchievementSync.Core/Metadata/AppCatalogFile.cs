using System.Collections.Concurrent;
using System.Text.Json;
using AchievementSync.Core.Models;

namespace AchievementSync.Core.Metadata;

/// <summary>The single on-disk catalog and per-game metadata document.</summary>
public sealed record AppCatalogFile(
    string AppId,
    string? Name,
    int? AchievementsTotal,
    DateTimeOffset? SteamMetadataCheckedAtUtc,
    DateTimeOffset? CommunityFetchedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    bool IsComplete,
    string? Source,
    int? DeclaredTotal,
    IReadOnlyList<AppCatalogAchievement> Achievements);

public sealed record AppCatalogAchievement(
    string Name,
    string? DisplayName,
    string? Description,
    string? Icon,
    bool Hidden,
    string? IconGray);

public static class AppCatalogFileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static AppCatalogFile? Read(string path, string appId, bool persistRepairs = false)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 64 });
            var legacyArray = document.RootElement.ValueKind == JsonValueKind.Array;
            AppCatalogFile? catalog;
            if (legacyArray)
            {
                var rows = JsonSerializer.Deserialize<IReadOnlyList<AppCatalogAchievement>>(document.RootElement.GetRawText(), JsonOptions);
                catalog = new AppCatalogFile(appId, null, null, null, null, File.GetLastWriteTimeUtc(path), false, null, null, rows ?? []);
            }
            else
            {
                catalog = JsonSerializer.Deserialize<AppCatalogFile>(document.RootElement.GetRawText(), JsonOptions);
                if (catalog is null) return null;
                catalog = catalog with { AppId = appId, Achievements = catalog.Achievements ?? [] };
            }

            var sanitizedRows = catalog.Achievements.Select(row => row with
            {
                Icon = NormalizeLocalIcon(path, row.Icon),
                IconGray = NormalizeLocalIcon(path, row.IconGray)
            }).ToArray();
            var repaired = legacyArray || !catalog.Achievements.SequenceEqual(sanitizedRows);
            catalog = catalog with { Achievements = sanitizedRows };
            if (persistRepairs && repaired) PersistRepair(path, catalog);
            return catalog;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? NormalizeLocalIcon(string catalogPath, string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        var appDirectory = Path.GetDirectoryName(Path.GetFullPath(catalogPath))!;
        var imagesDirectory = Path.Combine(appDirectory, "images");
        string fullPath;
        try
        {
            if (Uri.TryCreate(icon, UriKind.Absolute, out var uri) && uri.IsFile)
                fullPath = Path.GetFullPath(uri.LocalPath);
            else if (!Path.IsPathRooted(icon))
                fullPath = Path.GetFullPath(Path.Combine(appDirectory, icon.Replace('/', Path.DirectorySeparatorChar)));
            else
                return null;

            var relativeToImages = Path.GetRelativePath(imagesDirectory, fullPath);
            if (Path.IsPathRooted(relativeToImages)
                || relativeToImages.Equals("..", StringComparison.Ordinal)
                || relativeToImages.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(fullPath)) return null;
            return "images/" + relativeToImages.Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static void PersistRepair(string path, AppCatalogFile catalog)
    {
        var fullPath = Path.GetFullPath(path);
        var gate = Gates.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            if (!File.Exists(fullPath)) return;
            var temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(catalog, JsonOptions));
                File.Move(temporary, fullPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task<AppCatalogFile> WriteAsync(string path, AppCatalogFile incoming, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var gate = Gates.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = Read(fullPath, incoming.AppId);
            var merged = incoming with
            {
                Name = incoming.Name ?? existing?.Name,
                AchievementsTotal = incoming.AchievementsTotal ?? existing?.AchievementsTotal,
                SteamMetadataCheckedAtUtc = incoming.SteamMetadataCheckedAtUtc ?? existing?.SteamMetadataCheckedAtUtc,
                CommunityFetchedAtUtc = incoming.CommunityFetchedAtUtc ?? existing?.CommunityFetchedAtUtc,
                IsComplete = incoming.IsComplete || existing?.IsComplete == true,
                Source = incoming.Source ?? existing?.Source,
                DeclaredTotal = incoming.DeclaredTotal ?? existing?.DeclaredTotal,
                Achievements = incoming.Achievements.Count > 0 ? incoming.Achievements : existing?.Achievements ?? []
            };
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            var temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(output, merged, JsonOptions, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, fullPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            return merged;
        }
        finally
        {
            gate.Release();
        }
    }
}

public static class AppMetadataLayout
{
    private static readonly string[] LegacyAppFiles =
    ["steam.json", "catalog-state.json", "steam-community-catalog.json", "platform-info.json"];

    public static void RemoveLegacyFiles(string metadataRoot, string appId)
    {
        var directory = Path.Combine(metadataRoot, appId);
        if (!Directory.Exists(directory)) return;
        foreach (var filename in LegacyAppFiles)
        {
            try { File.Delete(Path.Combine(directory, filename)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        var screenshots = Path.Combine(directory, "screenshots");
        if (Directory.Exists(screenshots))
        {
            try { File.Delete(Path.Combine(screenshots, "index.json")); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
