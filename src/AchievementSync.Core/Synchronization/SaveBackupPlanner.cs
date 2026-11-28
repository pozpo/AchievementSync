using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace AchievementSync.Core.Synchronization;

/// <summary>
/// Builds one AppID's backup: the achievements.json (from the metadata cache), the discovered save files,
/// and the restoration manifest that maps every file back to its original location and records whether the
/// achievement data was originally an INI file.
/// </summary>
public static class SaveBackupPlanner
{
    /// <summary>Finds the newest achievement source file (json or ini) for an AppID under the AppID-scoped roots.</summary>
    public static (string Path, string Format)? ResolveAchievementSource(string appId)
    {
        var candidates = new List<(string Path, string Format, DateTime Time)>();
        foreach (var (key, template) in SaveFileDiscoverer.SaveRoots)
        {
            _ = key;
            var root = Environment.ExpandEnvironmentVariables(template).Replace("{appid}", appId, StringComparison.Ordinal);
            foreach (var directory in new[] { root, Path.Combine(root, "remote") })
            {
                if (!Directory.Exists(directory)) continue;
                string[] files;
                try { files = Directory.GetFiles(directory, "achievements.*", SearchOption.TopDirectoryOnly); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
                foreach (var file in files)
                {
                    var name = Path.GetFileName(file);
                    var format = name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ? "ini" : "json";
                    try { candidates.Add((file, format, File.GetLastWriteTimeUtc(file))); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
            }
        }

        var newest = candidates.OrderByDescending(candidate => candidate.Time).FirstOrDefault();
        return newest.Path is null ? null : (newest.Path, newest.Format);
    }

    /// <summary>
    /// Builds the backup plan for one AppID. Save files are packaged under AppID/&lt;id&gt;/save/; the achievements
    /// data is intentionally NOT included — it is synchronized separately through the achievements pipeline.
    /// The manifest records the achievement format and the exact original source path so a restore can
    /// recreate the original file untouched. File bytes are NOT loaded here — they are streamed on demand
    /// during upload only for files that actually changed, keeping memory bounded regardless of library size.
    /// </summary>
    public static Task<SaveBackupPlan> BuildAsync(
        string appId,
        IReadOnlyList<DiscoveredSaveFile> saves,
        string? achievementSourcePath,
        string achievementFormat,
        CancellationToken cancellationToken = default,
        string? repositoryFolder = null)
    {
        // No file I/O: the SHA-256 was already computed during discovery. Items carry the source path
        // and hash; content is loaded lazily during the upload phase for changed files only.
        var items = new List<SaveBackupItem>();
        var manifestSaves = new List<ManifestSaveEntry>();
        foreach (var save in saves.OrderBy(save => save.RepoPath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(new SaveBackupItem(appId, save.RepoPath, save.SourcePath, save.Sha256, null, save.Size));
            manifestSaves.Add(new ManifestSaveEntry(
                save.RootKey, save.RelativePath, PathSanitizer.Sanitize(save.SourcePath), save.RepoPath, save.Size, save.Sha256));
        }

        var manifest = RestorationManifest.Create(
            appId,
            achievementFormat,
            achievementSourcePath is null ? null : PathSanitizer.Sanitize(achievementSourcePath),
            achievementSourcePath is null ? null : Path.GetFileName(achievementSourcePath),
            manifestSaves,
            repositoryFolder);

        return Task.FromResult(new SaveBackupPlan(appId, items, manifest, repositoryFolder));
    }

    private static IEnumerable<(string Key, string Template)> SaveFileDiscovererRoots() =>
        SaveFileDiscoverer.SaveRoots;

    /// <summary>Computes the git blob SHA-1 for content (matches the GitHub contents API "sha" for blobs).</summary>
    public static string GitBlobSha(byte[] content)
    {
        var header = System.Text.Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        using var sha1 = SHA1.Create();
        sha1.TransformBlock(header, 0, header.Length, null, 0);
        sha1.TransformFinalBlock(content, 0, content.Length);
        return Convert.ToHexString(sha1.Hash!);
    }
}
