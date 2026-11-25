namespace AchievementSync.Core.Settings;

using System.Text.Json.Serialization;

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
    public bool AchievementNotificationsEnabled { get; set; } = true;
    public bool PlaytimeNotificationsEnabled { get; set; } = true;
    public bool AutoBackupSavesAfterPlaytime { get; set; }
    public bool StartWithWindows { get; set; }
    public bool CompactModeEnabled { get; set; } = false;
    public bool AutoDetectActiveGameScreen { get; set; } = true;

    // Notification presentation and behaviour.
    public string NotificationPosition { get; set; } = "TopCenter";
    public int NotificationOffsetX { get; set; }
    public int NotificationOffsetY { get; set; }
    public bool NotificationSoundEnabled { get; set; } = true;
    public int NotificationSoundVolume { get; set; } = 70;
    public string NotificationSoundKind { get; set; } = "AchievementSync";
    public string NotificationSoundPath { get; set; } = string.Empty;
    public bool AutoSyncAfterUnlock { get; set; }

    // Platform source folders (environment-tokenised paths are accepted).
    public Dictionary<string, string> PlatformSourceRoots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Platform login secrets are held in Windows Credential Manager. Keep only the RA account name here.
    public string RetroAchievementsUser { get; set; } = string.Empty;
    [JsonPropertyName("RetroAchievementsApiKey")]
    public string LegacyRetroAchievementsApiKey { get; set; } = string.Empty;
    [JsonPropertyName("EpicAccessToken")]
    public string LegacyEpicAccessToken { get; set; } = string.Empty;
    [JsonPropertyName("GogAccessToken")]
    public string LegacyGogAccessToken { get; set; } = string.Empty;
    // Achievement Enabler adapter archive root.
    public string EnablerAdaptersRoot { get; set; } = string.Empty;
}

public sealed class CustomLocationPreference
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Custom folder";
    public string Path { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
