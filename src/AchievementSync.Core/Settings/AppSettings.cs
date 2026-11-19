namespace AchievementSync.Core.Settings;

public sealed class AppSettings
{
    public Dictionary<string, bool> EnabledLocations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CustomLocationPreference> CustomLocations { get; set; } = [];
    public Dictionary<string, string> ManualGameNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string GitHubOwner { get; set; } = string.Empty;
    public string GitHubRepository { get; set; } = string.Empty;
    public string GitHubBranch { get; set; } = "user";
    public string GitHubLogin { get; set; } = string.Empty;
    public string GitHubDisplayName { get; set; } = string.Empty;
    public string GitHubAvatarUrl { get; set; } = string.Empty;
    public bool AutoSyncAfterScan { get; set; }
    public bool AchievementNotificationsEnabled { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool HasAuthorizedFirstUpload { get; set; }

    // Notification presentation and behaviour. Old settings files keep working: these
    // properties carry defaults and the store ignores keys it does not recognize.
    public string NotificationPosition { get; set; } = "TopCenter";
    public int NotificationOffsetX { get; set; }
    public int NotificationOffsetY { get; set; }
    public bool NotificationSoundEnabled { get; set; } = true;
    public int NotificationSoundVolume { get; set; } = 70; // 0..100
    public string NotificationSoundKind { get; set; } = "AchievementSync"; // AchievementSync | SoftTick | VictoryArpeggio | Custom
    public string NotificationSoundPath { get; set; } = string.Empty; // custom .wav file chosen by the user
    public bool AutoSyncAfterUnlock { get; set; }
}

public sealed class CustomLocationPreference
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Custom folder";
    public string Path { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
