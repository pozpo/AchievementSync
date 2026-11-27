using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AchievementSync.Core.Platforms;

/// <summary>
/// Official Epic Account Services (EAS) OAuth 2.0 client using Authorization Code + PKCE
/// against <c>https://www.epicgames.com/id/authorize</c> and <c>https://api.epicgames.dev/epic/oauth/v2/token</c>.
/// Requires the user's own Epic Developer Portal Client ID (and optional secret); never embeds private or borrowed credentials.
/// </summary>
public sealed class EpicAuthClient
{
    public const string AuthorizeEndpoint = "https://www.epicgames.com/id/authorize";
    public const string TokenEndpoint = "https://api.epicgames.dev/epic/oauth/v2/token";
    public const string UserInfoEndpoint = "https://api.epicgames.dev/epic/oauth/v2/userInfo";

    private readonly HttpClient _httpClient;

    public EpicAuthClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public static EpicPkceChallenge CreatePkceChallenge()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Base64UrlEncode(verifierBytes);
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Base64UrlEncode(challengeBytes);
        var state = Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        return new EpicPkceChallenge(verifier, challenge, state);
    }

    public static Uri BuildAuthorizeUri(string clientId, string redirectUri, EpicPkceChallenge pkce, string scope = "basic_profile")
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("An Epic Developer Portal Client ID is required.", nameof(clientId));
        if (string.IsNullOrWhiteSpace(redirectUri))
            throw new ArgumentException("A redirect URI is required.", nameof(redirectUri));

        var query = string.Join("&", new[]
        {
            $"client_id={Uri.EscapeDataString(clientId.Trim())}",
            "response_type=code",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            $"scope={Uri.EscapeDataString(scope)}",
            $"state={Uri.EscapeDataString(pkce.State)}",
            $"code_challenge={Uri.EscapeDataString(pkce.CodeChallenge)}",
            "code_challenge_method=S256"
        });
        return new Uri($"{AuthorizeEndpoint}?{query}");
    }

    public async Task<EpicStoredCredential> ExchangeAuthorizationCodeAsync(
        string clientId,
        string? clientSecret,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("An Epic Client ID is required.", nameof(clientId));
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("An authorization code is required.", nameof(code));

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code.Trim(),
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier
        };
        if (string.IsNullOrWhiteSpace(clientSecret))
            form["client_id"] = clientId.Trim();

        return await RequestTokenAsync(clientId.Trim(), clientSecret, form, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EpicStoredCredential> RefreshAsync(
        string clientId,
        string? clientSecret,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("An Epic Client ID is required.", nameof(clientId));
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("An Epic refresh token is required.", nameof(refreshToken));

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken.Trim()
        };
        if (string.IsNullOrWhiteSpace(clientSecret))
            form["client_id"] = clientId.Trim();

        return await RequestTokenAsync(clientId.Trim(), clientSecret, form, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> FetchDisplayNameAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await SendWithRateLimitRetryAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        if (doc.RootElement.TryGetProperty("preferred_username", out var preferred) && preferred.ValueKind == JsonValueKind.String)
            return preferred.GetString();
        if (doc.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            return name.GetString();
        return null;
    }

    private async Task<EpicStoredCredential> RequestTokenAsync(
        string clientId,
        string? clientSecret,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret.Trim()}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        using var response = await SendWithRateLimitRetryAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Epic OAuth returned HTTP {(int)response.StatusCode}.");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var accessToken = ReadString(root, "access_token")
            ?? throw new InvalidOperationException("Epic OAuth response did not include an access_token.");
        var refreshToken = ReadString(root, "refresh_token");
        var accountId = ReadString(root, "account_id") ?? ReadString(root, "sub");
        var displayName = ReadString(root, "displayName") ?? ReadString(root, "preferred_username");
        var expiresIn = root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var seconds) ? seconds : 7200;
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn));

        return new EpicStoredCredential(
            accessToken,
            refreshToken,
            accountId,
            displayName,
            expiresAt);
    }

    internal async Task<HttpResponseMessage> SendWithRateLimitRetryAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
            if (delay > TimeSpan.FromSeconds(15)) delay = TimeSpan.FromSeconds(15);
            response.Dispose();
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            using var retry = CloneRequest(request);
            response = await _httpClient.SendAsync(retry, cancellationToken).ConfigureAwait(false);
        }
        return response;
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var header in source.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (source.Content is not null)
        {
            var bytes = source.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            clone.Content = new ByteArrayContent(bytes);
            if (source.Content.Headers.ContentType is { } ct) clone.Content.Headers.ContentType = ct;
        }
        return clone;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record EpicPkceChallenge(string CodeVerifier, string CodeChallenge, string State);

public sealed record EpicStoredCredential(
    string AccessToken,
    string? RefreshToken,
    string? AccountId,
    string? DisplayName,
    DateTimeOffset? AccessTokenExpiresAtUtc)
{
    public bool NeedsRefresh(DateTimeOffset? now = null) =>
        AccessTokenExpiresAtUtc is { } expiry && expiry <= (now ?? DateTimeOffset.UtcNow).AddMinutes(2);

    public string Serialize() => JsonSerializer.Serialize(this);

    public static EpicStoredCredential? Parse(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized)) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<EpicStoredCredential>(serialized);
            return parsed is { AccessToken.Length: > 0 } ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
