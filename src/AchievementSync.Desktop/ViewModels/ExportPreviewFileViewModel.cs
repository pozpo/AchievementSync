using AchievementSync.Core.Synchronization;

namespace AchievementSync.Desktop.ViewModels;

public sealed class ExportPreviewFileViewModel
{
    private readonly AchievementExportFile _file;
    private readonly int? _newUnlocks;

    public ExportPreviewFileViewModel(AchievementExportFile file, int? newUnlocks = null)
    {
        _file = file;
        _newUnlocks = newUnlocks;
    }

    public string RelativePath => _file.RelativePath;
    public string Summary => _newUnlocks is null
        ? $"{_file.AchievementCount:N0} known state(s) · AppID {_file.AppId}"
        : $"{_file.AchievementCount:N0} states · +{_newUnlocks:N0} new unlock(s) · AppID {_file.AppId}";
    public string Json => _file.Json;
}
