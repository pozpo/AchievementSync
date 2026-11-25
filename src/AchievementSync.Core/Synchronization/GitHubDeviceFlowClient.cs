using System.Net.Http.Headers;
using System.Text.Json;

namespace AchievementSync.Core.Synchronization;

public sealed class GitHubDeviceFlowClient
{
    private static readonly Uri DeviceCodeEndpoint = new("https://github.com/login/device/code");
    private static readonly Uri AccessTokenEndpoint = new("https://github.com/login/oauth/access_token");
    private readonly HttpClient _httpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public GitHubDeviceFlowClient(
        HttpClient httpClient,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _httpClient = httpClient;
        _delay = delay ?? Task.Delay;
    }

    public async Task<GitHubDeviceSession> StartAsync(string clientId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("A GitHub OAuth App Client ID is required.", nameof(clientId));
        using var request = CreateFormRequest(DeviceCodeEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = "public_repo"
        });
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        var root = json.RootElement;
        ThrowOAuthError(root);

        return new GitHubDeviceSession(
            RequiredString(root, "device_code"),
            RequiredString(root, "user_code"),
            RequiredUri(root, "verification_uri"),
            RequiredInt(root, "expires_in"),
            Math.Max(1, OptionalInt(root, "interval") ?? 5));
    }

    public async Task<GitHubDeviceToken> PollForTokenAsync(
        string clientId,
        GitHubDeviceSession session,
        CancellationToken cancellationToken = default)
    {
        var interval = session.IntervalSeconds;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(session.ExpiresInSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await _delay(TimeSpan.FromSeconds(interval), cancellationToken);
            if (DateTimeOffset.UtcNow >= deadline) break;
            using var request = CreateFormRequest(AccessTokenEndpoint, new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["device_code"] = session.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            });
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            using var json = await ReadJsonAsync(response, cancellationToken);
            var root = json.RootElement;
            var error = OptionalString(root, "error");
            switch (error)
            {
                case null:
                    return ReadToken(root);
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval = Math.Max(checked(interval + 5), OptionalInt(root, "interval") ?? 0);
                    continue;
                case "access_denied":
                    throw new GitHubAuthorizationException("GitHub authorization was declined.");
                case "token_expired":
                case "expired_token":
                    throw new GitHubAuthorizationException("The GitHub device code expired. Start sign-in again.");
                default:
                    throw new GitHubAuthorizationException("GitHub sign-in could not be completed.");
            }
        }
        throw new GitHubAuthorizationException("The GitHub device code expired. Start sign-in again.");
    }

    /// <summary>Rotates an expiring device-flow token using GitHub's OAuth refresh grant.</summary>
    public async Task<GitHubDeviceToken> RefreshAsync(
        string clientId,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("A GitHub OAuth App Client ID is required.", nameof(clientId));
        if (string.IsNullOrWhiteSpace(refreshToken)) throw new ArgumentException("A GitHub refresh token is required.", nameof(refreshToken));
        using var request = CreateFormRequest(AccessTokenEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        });
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        var root = json.RootElement;
        if (OptionalString(root, "error") is { } error)
        {
            throw new GitHubAuthorizationException(error switch
            {
                "bad_refresh_token" => "GitHub sign-in expired or was revoked. Connect your account again.",
                "incorrect_client_credentials" => "The GitHub OAuth Client ID is not recognized.",
                _ => "GitHub sign-in could not be refreshed. Connect your account again."
            });
        }
        return ReadToken(root);
    }

    private static HttpRequestMessage CreateFormRequest(Uri endpoint, IReadOnlyDictionary<string, string> values)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(values)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("AchievementSync/0.1");
        return request;
    }

    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new GitHubApiException($"GitHub returned HTTP {(int)response.StatusCode}.");
        try
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (JsonException exception)
        {
            throw new GitHubApiException("GitHub returned an invalid response.", exception);
        }
    }

    internal static void ThrowOAuthError(JsonElement root)
    {
        if (OptionalString(root, "error") is { } error)
            throw new GitHubAuthorizationException(error switch
            {
                "incorrect_client_credentials" => "The GitHub OAuth Client ID is not recognized.",
                "device_flow_disabled" => "Device Flow is not enabled for the configured GitHub OAuth App.",
                _ => "GitHub sign-in could not be started."
            });
    }

    internal static GitHubDeviceToken ReadToken(JsonElement root) => new(
        RequiredString(root, "access_token"),
        OptionalString(root, "token_type") ?? "bearer",
        OptionalString(root, "scope") ?? string.Empty,
        OptionalInt(root, "expires_in"),
        OptionalString(root, "refresh_token"),
        OptionalInt(root, "refresh_token_expires_in"));

    internal static string RequiredString(JsonElement element, string propertyName) =>
        OptionalString(element, propertyName) ?? throw new GitHubApiException("GitHub returned an incomplete response.");

    internal static string? OptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Uri RequiredUri(JsonElement element, string propertyName)
    {
        var value = RequiredString(element, propertyName);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : throw new GitHubApiException("GitHub returned an invalid authorization URL.");
    }

    private static int RequiredInt(JsonElement element, string propertyName) =>
        OptionalInt(element, propertyName) ?? throw new GitHubApiException("GitHub returned an incomplete response.");

    private static int? OptionalInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number) ? number : null;
}

public sealed record GitHubDeviceSession(string DeviceCode, string UserCode, Uri VerificationUri, int ExpiresInSeconds, int IntervalSeconds);
public sealed record GitHubDeviceToken(
    string AccessToken,
    string TokenType,
    string Scope,
    int? ExpiresInSeconds = null,
    string? RefreshToken = null,
    int? RefreshTokenExpiresInSeconds = null)
{
    public GitHubStoredCredential ToStoredCredential(DateTimeOffset? now = null)
    {
        var created = now ?? DateTimeOffset.UtcNow;
        return new GitHubStoredCredential(
            AccessToken,
            TokenType,
            Scope,
            ExpiresInSeconds is > 0 ? created.AddSeconds(ExpiresInSeconds.Value) : null,
            RefreshToken,
            RefreshTokenExpiresInSeconds is > 0 ? created.AddSeconds(RefreshTokenExpiresInSeconds.Value) : null);
    }
}

public sealed record GitHubStoredCredential(
    string AccessToken,
    string TokenType,
    string Scope,
    DateTimeOffset? AccessTokenExpiresAtUtc,
    string? RefreshToken,
    DateTimeOffset? RefreshTokenExpiresAtUtc)
{
    public bool NeedsRefresh(DateTimeOffset? now = null) =>
        AccessTokenExpiresAtUtc is { } expiry && expiry <= (now ?? DateTimeOffset.UtcNow).AddMinutes(2);

    /// <summary>Reads the current credential payload and migrates legacy plain-text tokens in memory.</summary>
    public static GitHubStoredCredential? Parse(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized)) return null;
        try
        {
            var stored = JsonSerializer.Deserialize<GitHubStoredCredential>(serialized);
            if (stored is { AccessToken.Length: > 0 }) return stored;
            if (serialized.TrimStart().StartsWith('{')) return null;
        }
        catch (JsonException)
        {
            // Older builds stored only the access token; keep it usable until GitHub rejects it.
        }
        return new GitHubStoredCredential(serialized, "bearer", string.Empty, null, null, null);
    }

    public string Serialize() => JsonSerializer.Serialize(this);
}

public sealed class GitHubAuthorizationException(string message) : Exception(message);
