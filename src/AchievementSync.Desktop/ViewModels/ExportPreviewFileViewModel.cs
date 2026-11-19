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
    public string FormattedJson => PrettyPrint(_file.Json);

    private static string PrettyPrint(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json,
                new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            using var stream = new System.IO.MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true }))
            {
                document.WriteTo(writer);
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (System.Text.Json.JsonException)
        {
            return json;
        }
    }
}
