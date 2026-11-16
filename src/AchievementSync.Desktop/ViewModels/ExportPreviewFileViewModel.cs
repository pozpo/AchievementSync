using AchievementSync.Core.Synchronization;

namespace AchievementSync.Desktop.ViewModels;

public sealed class ExportPreviewFileViewModel(AchievementExportFile file)
{
    public string RelativePath => file.RelativePath;
    public string Summary => $"{file.AchievementCount:N0} known state(s) · AppID {file.AppId}";
    public string Json => file.Json;
}
