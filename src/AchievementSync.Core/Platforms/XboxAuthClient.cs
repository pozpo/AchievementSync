using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AchievementSync.Core.Platforms;

/// <summary>
/// Microsoft Entra ID OAuth 2.0 Device Code + Xbox Live XSTS authentication client.
/// Requires a user-configured Microsoft Entra application Client ID; never embeds any borrowed client ID.
/// </summary>
public sealed class XboxAuthClient
{
    public const string XboxUserAuthEndpoint = "https://user.auth.xboxlive.com/user/authenticate";
    public const string XboxXstsAuthorizeEndpoint = "https://xsts.auth.xboxlive.com/xsts/authorize";
    public const string RequiredScope = "XboxLive.signin offline_access";

    private readonly HttpClient _httpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public XboxAuthClient(HttpClient httpClient, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _delay = delay ?? Task.Delay;
    }

    public async Task<XboxDeviceCodeSession> StartDeviceCodeAsync(
        string clientId,
        string tenantId = "consumers",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("A Microsoft Entra Application (client) ID is required for Xbox sign-in.", nameof(clientId));
        var effectiveTenant = string.IsNullOrWhiteSpace(tenantId) ? "consumers" : tenantId.Trim();
        var endpoint = $"https://login.microsoftonline.com/{Uri.EscapeDataString(effectiveTenant)}/oauth2/v2.0/devicecode";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId.Trim(),
                ["scope"] = RequiredScope
            })
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Microsoft Entra device code request failed (HTTP {(int)response.StatusCode}).");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new XboxDeviceCodeSession(
            ReadRequiredString(root, "device_code"),
            ReadRequiredString(root, "user_code"),
            ReadRequiredString(root, "verification_uri"),
            root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var expSec) ? expSec : 900,
            Math.Max(1, root.TryGetProperty("interval", out var iv) && iv.TryGetInt32(out var ivSec) ? ivSec : 5),
            effectiveTenant);
    }

    public async Task<XboxStoredCredential> PollForXboxSessionAsync(
        string clientId,
        XboxDeviceCodeSession session,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("A Microsoft Entra Application (client) ID is required.", nameof(clientId));

        var tokenEndpoint = $"https://login.microsoftonline.com/{Uri.EscapeDataString(session.TenantId)}/oauth2/v2.0/token";
        var interval = session.IntervalSeconds;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(session.ExpiresInSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await _delay(TimeSpan.FromSeconds(interval), cancellationToken).ConfigureAwait(false);
            if (DateTimeOffset.UtcNow >= deadline) break;

            using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = clientId.Trim(),
                    ["device_code"] = session.DeviceCode
                })
            };
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (response.IsSuccessStatusCode)
            {
                var accessToken = ReadRequiredString(root, "access_token");
                var refreshToken = ReadOptionalString(root, "refresh_token");
                var expiresIn = root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var sec) ? sec : 3600;
                return await ExchangeMicrosoftTokenForXboxAsync(
                    accessToken, refreshToken, session.TenantId, DateTimeOffset.UtcNow.AddSeconds(expiresIn), cancellationToken)
                    .ConfigureAwait(false);
            }

            var error = ReadOptionalString(root, "error");
            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += 5;
                    continue;
                case "authorization_declined":
                    throw new InvalidOperationException("Microsoft sign-in was declined.");
                case "expired_token":
                    throw new InvalidOperationException("The Microsoft device code expired. Start sign-in again.");
                default:
                    throw new InvalidOperationException($"Microsoft sign-in failed ({error ?? $"HTTP {(int)response.StatusCode}"}).");
            }
        }

        throw new InvalidOperationException("The Microsoft device code expired before authorization completed.");
    }

    public async Task<XboxStoredCredential> RefreshAsync(
        string clientId,
        string refreshToken,
        string tenantId = "consumers",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("A Microsoft Entra Application (client) ID is required.", nameof(clientId));
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("A Microsoft refresh token is required.", nameof(refreshToken));

        var effectiveTenant = string.IsNullOrWhiteSpace(tenantId) ? "consumers" : tenantId.Trim();
        var tokenEndpoint = $"https://login.microsoftonline.com/{Uri.EscapeDataString(effectiveTenant)}/oauth2/v2.0/token";

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId.Trim(),
                ["scope"] = RequiredScope,
                ["refresh_token"] = refreshToken.Trim(),
                ["grant_type"] = "refresh_token"
            })
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Microsoft token refresh failed (HTTP {(int)response.StatusCode}).");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var accessToken = ReadRequiredString(root, "access_token");
        var rotatedRefresh = ReadOptionalString(root, "refresh_token") ?? refreshToken;
        var expiresIn = root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var sec) ? sec : 3600;
        return await ExchangeMicrosoftTokenForXboxAsync(
            accessToken, rotatedRefresh, effectiveTenant, DateTimeOffset.UtcNow.AddSeconds(expiresIn), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<XboxStoredCredential> ExchangeMicrosoftTokenForXboxAsync(
        string microsoftAccessToken,
        string? refreshToken,
        string tenantId,
        DateTimeOffset? microsoftExpiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        // Step 1: Exchange MSA/Entra access token for Xbox User Token
        var userPayload = JsonSerializer.Serialize(new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = $"d={microsoftAccessToken}"
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        });
        using var userRequest = new HttpRequestMessage(HttpMethod.Post, XboxUserAuthEndpoint)
        {
            Content = new StringContent(userPayload, Encoding.UTF8, "application/json")
        };
        userRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        userRequest.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");

        using var userResponse = await _httpClient.SendAsync(userRequest, cancellationToken).ConfigureAwait(false);
        var userBody = await userResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!userResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Xbox User authentication failed (HTTP {(int)userResponse.StatusCode}).");

        using var userDoc = JsonDocument.Parse(userBody);
        var userToken = ReadRequiredString(userDoc.RootElement, "Token");

        // Step 2: Exchange Xbox User Token for XSTS token + XUID + UHS
        var xstsPayload = JsonSerializer.Serialize(new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { userToken }
            },
            RelyingParty = "http://xboxlive.com",
            TokenType = "JWT"
        });
        using var xstsRequest = new HttpRequestMessage(HttpMethod.Post, XboxXstsAuthorizeEndpoint)
        {
            Content = new StringContent(xstsPayload, Encoding.UTF8, "application/json")
        };
        xstsRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        xstsRequest.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");

        using var xstsResponse = await _httpClient.SendAsync(xstsRequest, cancellationToken).ConfigureAwait(false);
        var xstsBody = await xstsResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!xstsResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Xbox XSTS authorization failed (HTTP {(int)xstsResponse.StatusCode}).");

        using var xstsDoc = JsonDocument.Parse(xstsBody);
        var xstsRoot = xstsDoc.RootElement;
        var xstsToken = ReadRequiredString(xstsRoot, "Token");
        DateTimeOffset? xstsExpiresAt = null;
        if (DateTimeOffset.TryParse(ReadOptionalString(xstsRoot, "NotAfter"), out var parsedExpiry))
            xstsExpiresAt = parsedExpiry;

        string? uhs = null;
        string? xuid = null;
        string? gamertag = null;
        if (xstsRoot.TryGetProperty("DisplayClaims", out var claims)
            && claims.TryGetProperty("xui", out var xuiArray)
            && xuiArray.ValueKind == JsonValueKind.Array
            && xuiArray.GetArrayLength() > 0)
        {
            var first = xuiArray[0];
            uhs = ReadOptionalString(first, "uhs");
            xuid = ReadOptionalString(first, "xid");
            gamertag = ReadOptionalString(first, "gtg");
        }

        if (string.IsNullOrWhiteSpace(uhs) || string.IsNullOrWhiteSpace(xuid))
            throw new InvalidOperationException("Xbox XSTS response did not include required user claims (uhs/xid).");

        return new XboxStoredCredential(
            microsoftAccessToken,
            refreshToken,
            tenantId,
            microsoftExpiresAtUtc,
            xstsToken,
            uhs,
            xuid,
            gamertag,
            xstsExpiresAt);
    }

    private static string ReadRequiredString(JsonElement element, string name) =>
        ReadOptionalString(element, name)
        ?? throw new InvalidOperationException($"Missing required field '{name}' in authentication response.");

    private static string? ReadOptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}

public sealed record XboxDeviceCodeSession(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresInSeconds,
    int IntervalSeconds,
    string TenantId);

public sealed record XboxStoredCredential(
    string MicrosoftAccessToken,
    string? RefreshToken,
    string TenantId,
    DateTimeOffset? MicrosoftExpiresAtUtc,
    string XstsToken,
    string Uhs,
    string Xuid,
    string? Gamertag,
    DateTimeOffset? XstsExpiresAtUtc)
{
    public bool NeedsRefresh(DateTimeOffset? now = null)
    {
        var check = (now ?? DateTimeOffset.UtcNow).AddMinutes(2);
        return (XstsExpiresAtUtc is { } xstsExp && xstsExp <= check)
               || (MicrosoftExpiresAtUtc is { } msExp && msExp <= check);
    }

    public string Serialize() => JsonSerializer.Serialize(this);

    public static XboxStoredCredential? Parse(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized)) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<XboxStoredCredential>(serialized);
            return parsed is { XstsToken.Length: > 0, Uhs.Length: > 0, Xuid.Length: > 0 } ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
