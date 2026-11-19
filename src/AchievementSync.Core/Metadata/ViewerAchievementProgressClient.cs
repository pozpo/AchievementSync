using System.Net;
using AchievementSync.Core.Models;
using AchievementSync.Core.Parsing;

namespace AchievementSync.Core.Metadata;

public enum ViewerProgressStatus
{
    Found,
    NotConfigured,
    InvalidAppId,
    NotFound,
    MalformedResponse,
    HttpFailure,
    Timeout
}

public sealed record ViewerProgressResult(ViewerProgressStatus Status, ParsedProfile? Progress, string Message)
{
    public bool Success => Status == ViewerProgressStatus.Found && Progress is not null;
}

/// <summary>Reads only the selected public viewer repository's per-game progress JSON; it never uploads local data.</summary>
public sealed class ViewerAchievementProgressClient(HttpClient httpClient, TimeSpan? requestTimeout = null)
{
    private readonly TimeSpan _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5);

    public async Task<ViewerProgressResult> GetProgressAsync(
        string? owner,
        string? repository,
        string? branch,
        string? appId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(branch)
            || owner.Contains('/') || repository.Contains('/') || branch.Contains('/')
            || owner.Contains('\\') || repository.Contains('\\') || branch.Contains('\\'))
        {
            return Failure(ViewerProgressStatus.NotConfigured, "Connect a public Achievement Viewer repository to read its saved progress.");
        }
        if (!AppIdValidator.IsValid(appId))
        {
            return Failure(ViewerProgressStatus.InvalidAppId, "This profile does not have a valid numeric AppID for viewer progress lookup.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            var endpoint = new Uri(
                $"https://raw.githubusercontent.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/{Uri.EscapeDataString(branch)}/AppID/{appId}/achievements.json");
            using var response = await httpClient.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Failure(ViewerProgressStatus.NotFound, "The configured viewer branch has no saved progress file for this AppID.");
            }
            if (!response.IsSuccessStatusCode)
            {
                return Failure(ViewerProgressStatus.HttpFailure, $"The viewer returned HTTP {(int)response.StatusCode} while loading this game's saved progress.");
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var source = $"viewer://{owner}/{repository}/{branch}/AppID/{appId}/achievements.json";
            var parsed = JsonAchievementParser.ParseViewerAchievementProgress(json, appId, source);
            return parsed.Profile is not null
                ? new ViewerProgressResult(ViewerProgressStatus.Found, parsed.Profile,
                    $"Loaded {parsed.Profile.Achievements.Count:N0} saved achievement states from the public viewer.")
                : Failure(ViewerProgressStatus.MalformedResponse, parsed.Error ?? "The viewer progress file is not in a supported format.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(ViewerProgressStatus.Timeout, "The viewer progress request timed out.");
        }
        catch (HttpRequestException)
        {
            return Failure(ViewerProgressStatus.HttpFailure, "The configured public viewer could not be reached.");
        }
    }

    private static ViewerProgressResult Failure(ViewerProgressStatus status, string message) => new(status, null, message);
}
