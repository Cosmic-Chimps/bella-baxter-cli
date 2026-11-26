using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BellaCli.Services;

using BellaCli.Infrastructure;

public class AuthService(ConfigService config, CredentialStore credentials, HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ── Discover auth config ─────────────────────────────────────────────────

    public Task<AuthConfig> DiscoverConfigAsync(CancellationToken ct = default) =>
        DiscoverConfigAsync(config.ApiUrl, ct);

    private async Task<AuthConfig> DiscoverConfigAsync(string apiUrl, CancellationToken ct)
    {
        var url = $"{apiUrl}/api/v1/auth/config";
        var response = await http.GetFromJsonAsync<AuthConfigResponse>(url, JsonOptions, ct)
            ?? throw new InvalidOperationException("Could not fetch auth config from server.");

        return new AuthConfig(response.KeycloakUrl, response.Realm, response.CliClientId);
    }

    // ── OAuth2 PKCE browser flow ─────────────────────────────────────────────

    public virtual async Task<StoredTokens> LoginWithBrowserAsync(CancellationToken ct = default)
    {
        // Advisory clients-1 — the tokens belong to the server they are obtained from; recorded so they
        // are never presented anywhere else.
        var apiUrl = config.ApiUrl;
        var authConfig = await DiscoverConfigAsync(apiUrl, ct);
        var pkce = OAuth2PkceFlow.GenerateChallenge();
        var (listener, callbackUrl, _) = OAuth2PkceFlow.StartCallbackListener();

        var authUrl = BuildAuthorizationUrl(authConfig, pkce.CodeChallenge, callbackUrl);

        OAuth2PkceFlow.OpenBrowser(authUrl);

        var code = await OAuth2PkceFlow.WaitForCallbackAsync(listener, ct);
        var tokens = await ExchangeCodeAsync(authConfig, code, pkce.CodeVerifier, callbackUrl, ct)
            with { Origin = ConfigService.OriginOf(apiUrl) };

        credentials.SaveTokens(tokens);
        return tokens;
    }

    // ── API key storage ──────────────────────────────────────────────────────

    public void LoginWithApiKey(string rawKey)
    {
        // Format: bax-{keyId}-{signingSecret}
        var parts = rawKey.Split('-', 3);
        if (parts.Length != 3 || parts[0] != "bax")
            throw new ArgumentException("Invalid API key format. Expected: bax-{keyId}-{signingSecret}");

        var stored = new StoredApiKey(
            KeyId: parts[1],
            SigningSecret: parts[2],
            Raw: rawKey,
            // Advisory clients-1 — stored for the server it is being logged in to, and only ever sent there.
            Origin: ConfigService.OriginOf(config.ApiUrl)
        );

        credentials.ClearTokens(); // API key replaces OAuth session
        credentials.SaveApiKey(stored);
    }

    // ── Token refresh ────────────────────────────────────────────────────────

    /// <summary>
    /// Exchanges the stored refresh token for a new token set. Serialised across this process and
    /// every other <c>bella</c> process (#824): with refresh-token rotation a second, concurrent
    /// refresh of the same token ends the login — see <see cref="RefreshCoordinator"/>.
    /// </summary>
    public virtual Task<StoredTokens> RefreshAsync(CancellationToken ct = default) =>
        RefreshCoordinator.Shared.RefreshAsync(
            credentials.LoadTokens,
            ExchangeRefreshTokenAsync,
            credentials.AcquireRefreshLockAsync,
            ct);

    internal async Task<StoredTokens> ExchangeRefreshTokenAsync(StoredTokens existing, CancellationToken ct)
    {
        // Advisory clients-1 — the refresh token goes to the token endpoint the API names, so the API must
        // be the server the login belongs to. A legacy login (no recorded origin) is bound to the machine
        // default here and from now on.
        var apiUrl = config.ApiUrlForStoredCredential(existing.Origin);
        var origin = existing.Origin ?? ConfigService.OriginOf(apiUrl);
        var authConfig = await DiscoverConfigAsync(apiUrl, ct);

        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = authConfig.CliClientId,
            ["refresh_token"] = existing.RefreshToken
        });

        var response = await http.PostAsync(authConfig.GetTokenEndpoint(), body, ct);

        // #2002 — the server's refusal of the refresh token, classified EXACTLY (the body is read for its `error` field only and
        // never shown). Keycloak answers an ended session (idle, maximum lifespan, revoked) 400 invalid_grant, and a client it
        // does not accept 401; it never answers 403 here, so a 403 (or any other 4xx) comes from something in front of it and says
        // nothing about the session. 408, 429 and 5xx are an outage or a throttle: they fall through to EnsureSuccessStatusCode.
        var refusal = await ClassifyRefusalAsync(response, authConfig.GetTokenEndpoint(), ct);
        if (refusal is not null)
        {
            response.Dispose();
            throw refusal;
        }

        response.EnsureSuccessStatusCode();

        var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Empty token response.");

        var tokens = ToStoredTokens(tokenResponse) with { Origin = origin };
        credentials.SaveTokens(tokens);
        return tokens;
    }

    /// <summary>
    /// What a non-success answer from the token endpoint to a refresh means; null when it is not a refusal of the token
    /// (success, 408, 429, 5xx). <c>400 invalid_grant</c> is an expiry; <c>401</c> a client the server does not accept; every
    /// other 4xx (a 400 with another or no readable <c>error</c>, 403, 404, 418 ...) is "refused" and never an expiry.
    /// </summary>
    internal static async Task<SessionExpiredException?> ClassifyRefusalAsync(HttpResponseMessage response, string tokenEndpoint, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (status is < 400 or >= 500 || status is 408 or 429)
            return null;

        if (status == 400 && await ReadOAuthErrorAsync(response, ct) == "invalid_grant")
            return SessionExpiredException.Refused();

        var host = Uri.TryCreate(tokenEndpoint, UriKind.Absolute, out var uri) ? uri.Host : "the-sign-in-server";
        return status == 401
            ? SessionExpiredException.ClientRejected(host)
            : SessionExpiredException.ServerRefused(status, host);
    }

    /// <summary>The OAuth <c>error</c> field of a token-endpoint error body, or null when there is none readable. Nothing else is read or kept.</summary>
    private static async Task<string?> ReadOAuthErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[16 * 1024]; // an OAuth error is a few dozen bytes; never buffer what a proxy sends back
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), ct)) > 0)
                read += n;
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, read));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    // ── Logout ───────────────────────────────────────────────────────────────

    public void Logout()
    {
        credentials.ClearTokens();
        credentials.ClearApiKey();
    }

    // ── Token validation ─────────────────────────────────────────────────────

    public bool IsTokenExpired()
    {
        var tokens = credentials.LoadTokens();
        return tokens is null || tokens.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30);
    }

    public async Task<StoredTokens> EnsureValidTokenAsync(CancellationToken ct = default)
    {
        if (IsTokenExpired())
            return await RefreshAsync(ct);

        return credentials.LoadTokens()!;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// #1610 — what a browser login asks for. Deliberately NO <c>offline_access</c>: an offline token is the one session the server's
    /// limits for <c>bella-cli</c> (12 h idle, 24 h max) bound only through their offline twins, and the CLI has no use for it. A
    /// normal refresh token (bound to the SSO session) is all it needs; <see cref="RefreshCoordinator"/> and
    /// <see cref="ExchangeRefreshTokenAsync"/> never relied on an offline one. The server still OFFERS the scope, because Keycloak
    /// refuses (invalid_scope) a request naming a scope the client lacks: an older CLI that still asks keeps logging in, and its
    /// offline token is capped by the same limits.
    /// </summary>
    internal const string LoginScope = "openid profile email";

    internal static string BuildAuthorizationUrl(AuthConfig authConfig, string codeChallenge, string callbackUrl)
    {
        var q = System.Web.HttpUtility.ParseQueryString(string.Empty);
        q["client_id"] = authConfig.CliClientId;
        q["redirect_uri"] = callbackUrl;
        q["response_type"] = "code";
        q["scope"] = LoginScope;
        q["code_challenge"] = codeChallenge;
        q["code_challenge_method"] = "S256";
        return $"{authConfig.GetAuthorizationEndpoint()}?{q}";
    }

    private async Task<StoredTokens> ExchangeCodeAsync(
        AuthConfig authConfig, string code, string codeVerifier, string callbackUrl, CancellationToken ct)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = authConfig.CliClientId,
            ["code"] = code,
            ["redirect_uri"] = callbackUrl,
            ["code_verifier"] = codeVerifier
        });

        var response = await http.PostAsync(authConfig.GetTokenEndpoint(), body, ct);
        response.EnsureSuccessStatusCode();

        var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Empty token response.");

        return ToStoredTokens(tokenResponse);
    }

    private static StoredTokens ToStoredTokens(TokenResponse r)
    {
        var claims = DecodeJwtPayload(r.AccessToken);
        var orgId = GetClaim(claims, "tenant_id");
        var orgName = GetClaim(claims, "tenant_name");
        var orgSlug = GetClaim(claims, "tenant_slug");

        return new StoredTokens(
            AccessToken: r.AccessToken,
            RefreshToken: r.RefreshToken,
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(r.ExpiresIn),
            TokenType: r.TokenType,
            OrgId: orgId,
            OrgName: orgName,
            OrgSlug: orgSlug
        );
    }

    internal static Dictionary<string, JsonElement> DecodeJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return [];
        var payload = parts[1];
        payload = (payload.Length % 4) switch
        {
            2 => payload + "==",
            3 => payload + "=",
            _ => payload,
        };
        payload = payload.Replace('-', '+').Replace('_', '/');
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
        }
        catch { return []; }
    }

    internal static string? GetClaim(Dictionary<string, JsonElement> claims, string key) =>
        claims.TryGetValue(key, out var val) ? val.ToString() : null;

    // ── Response DTOs ────────────────────────────────────────────────────────

    private record AuthConfigResponse(
        [property: JsonPropertyName("keycloakUrl")] string KeycloakUrl,
        [property: JsonPropertyName("realm")] string Realm,
        [property: JsonPropertyName("cliClientId")] string CliClientId
    );

    private record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string TokenType
    );
}
