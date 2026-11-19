namespace AchievementSync.Core.Tracking;

/// <summary>
/// Some games ship protected processes whose image path cannot be read by any caller
/// on the machine (QueryFullProcessImageName / WMI ExecutablePath / MainModule all fail).
/// In that case the tracker cannot verify a full path; a stable synthetic token carries
/// the process name instead so the linked-executable matching can still run.
/// </summary>
public static class ProcessObservationIdentity
{
    public const string UnverifiablePrefix = "UnverifiableProcessName:";

    public static string UnverifiablePath(string processName) =>
        UnverifiablePrefix + (processName ?? string.Empty);

    public static bool TryReadUnverifiableName(string path, out string processName)
    {
        processName = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!path.StartsWith(UnverifiablePrefix, StringComparison.Ordinal)) return false;
        processName = path[UnverifiablePrefix.Length..];
        return processName.Length > 0;
    }
}
