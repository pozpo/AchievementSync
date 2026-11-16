namespace AchievementSync.Core.Settings;

public sealed class AppSettings
{
    public Dictionary<string, bool> EnabledLocations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CustomLocationPreference> CustomLocations { get; set; } = [];
    public Dictionary<string, string> ManualGameNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CustomLocationPreference
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Custom folder";
    public string Path { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
