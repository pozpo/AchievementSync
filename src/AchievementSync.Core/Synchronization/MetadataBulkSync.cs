using System.IO;
using System.Linq;

namespace AchievementSync.Core.Synchronization;

/// <summary>One per-AppID outcome for the bulk sync report.</summary>
public sealed record BulkSyncEntry(string AppId, string Action, string Detail);

/// <summary>Aggregate result of a bulk push of achievement JSON and viewer marker files.</summary>
public sealed record BulkSyncResult(
    bool Pushed,
    string? CommitUrl,
    int AchievementFilesPushed,
    int AchievementFilesUnchanged,
    int AchievementFilesFailed,
    IReadOnlyList<BulkSyncEntry> Entries)
{
    public static BulkSyncResult Empty { get; } = new(false, null, 0, 0, 0, []);

    public string Summary
    {
        get
        {
            var pushedText = AchievementFilesPushed > 0
                ? $"{AchievementFilesPushed:N0} file(s)"
                : "nothing new to upload";
            var parts = new List<string> { pushedText };
            if (AchievementFilesUnchanged > 0) parts.Add($"{AchievementFilesUnchanged:N0} already up to date");
            if (AchievementFilesFailed > 0) parts.Add($"{AchievementFilesFailed:N0} failed");
            return string.Join(" · ", parts);
        }
    }
}
