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
    public bool IsEmptyMarker => _file.Json.Length == 0
        && (_file.RelativePath.EndsWith(".platform", StringComparison.OrdinalIgnoreCase)
            || _file.RelativePath.EndsWith("/blacklist", StringComparison.OrdinalIgnoreCase));
    public string Summary => _newUnlocks is null
        ? IsEmptyMarker
            ? $"Empty marker file · AppID {_file.AppId}"
            : $"{_file.AchievementCount:N0} known state(s) · AppID {_file.AppId}"
        : IsEmptyMarker
            ? $"Empty marker file · uploaded as 0 bytes · AppID {_file.AppId}"
            : $"{_file.AchievementCount:N0} states · +{_newUnlocks:N0} new unlock(s) · AppID {_file.AppId}";
    public string ContentHint => IsEmptyMarker
        ? "This is an intentionally empty marker file. The selected path identifies its platform or blacklist status; GitHub receives a zero-byte file."
        : "Select a file from the list to inspect its exact JSON. Nothing is uploaded until you push.";
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
