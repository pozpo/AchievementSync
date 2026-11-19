namespace AchievementSync.Core.Tracking;

/// <summary>Pure geometry for placing a notification inside a work area. UI-free so it can be unit-tested.</summary>
public static class NotificationPlacement
{
    /// <summary>Supported notification position presets.</summary>
    public static IReadOnlyList<NotificationPositionPreset> Presets { get; } =
    [
        new("TopCenter", "Top center"),
        new("Center", "Screen center"),
        new("TopLeft", "Top left"),
        new("TopRight", "Top right"),
        new("BottomCenter", "Bottom center"),
        new("BottomLeft", "Bottom left"),
        new("BottomRight", "Bottom right"),
    ];

    /// <summary>Returns the top-left anchor for a toast inside the given work area, clamped to stay on screen.</summary>
    public static (double Left, double Top) Compute(
        string? preset,
        double width,
        double height,
        double offsetX,
        double offsetY,
        double workLeft,
        double workTop,
        double workWidth,
        double workHeight)
    {
        var (left, top) = Normalize(preset) switch
        {
            "center" => (workLeft + (workWidth - width) / 2 + offsetX, workTop + (workHeight - height) / 2 + offsetY),
            "topleft" => (workLeft + 18 + offsetX, workTop + 18 + offsetY),
            "topright" => (workLeft + workWidth - width - 18 + offsetX, workTop + 18 + offsetY),
            "bottomcenter" => (workLeft + (workWidth - width) / 2 + offsetX, workTop + workHeight - height - 18 + offsetY),
            "bottomleft" => (workLeft + 18 + offsetX, workTop + workHeight - height - 18 + offsetY),
            "bottomright" => (workLeft + workWidth - width - 18 + offsetX, workTop + workHeight - height - 18 + offsetY),
            _ => (workLeft + (workWidth - width) / 2 + offsetX, workTop + 14 + offsetY), // TopCenter is the default
        };

        left = Math.Clamp(left, workLeft + 8, Math.Max(workLeft + 8, workLeft + workWidth - width - 8));
        top = Math.Clamp(top, workTop + 8, Math.Max(workTop + 8, workTop + workHeight - height - 8));
        return (left, top);
    }

    public static bool IsKnownPreset(string? preset) =>
        Presets.Any(item => string.Equals(item.Code, Normalize(preset), StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string? preset) =>
        string.IsNullOrWhiteSpace(preset) ? "TopCenter" : preset.Trim().ToLowerInvariant();
}

public sealed record NotificationPositionPreset(string Code, string Label)
{
    public override string ToString() => Label;
}
