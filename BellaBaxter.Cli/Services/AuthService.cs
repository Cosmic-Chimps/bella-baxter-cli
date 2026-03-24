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

    public async Task<AuthConfig> DiscoverConfigAsync(CancellationToken ct = default)
    {
        var url = $"{config.ApiUrl}/api/v1/auth/config";
        var response = await http.GetFromJsonAsync<AuthConfigResponse>(url, JsonOptions, ct)
            ?? throw new InvalidOperationException("Could not fetch auth config from server.");

        return new AuthConfig(response.KeycloakUrl, response.Realm, response.CliClientId);
    }

    // ── OAuth2 PKCE browser flow ─────────────────────────────────────────────

    public async Task<StoredTokens> LoginWithBrowserAsync(CancellationToken ct = default)
    {
        var authConfig = await DiscoverConfigAsync(ct);
        var pkce = OAuth2PkceFlow.GenerateChallenge();
        var (listener, callbackUrl, _) = OAuth2PkceFlow.StartCallbackListener();

        var authUrl = BuildAuthorizationUrl(authConfig, pkce.CodeChallenge, callbackUrl);

        OAuth2PkceFlow.OpenBrowser(authUrl);

        var code = await OAuth2PkceFlow.WaitForCallbackAsync(listener, ct);
        var tokens = await ExchangeCodeAsync(authConfig, code, pkce.CodeVerifier, callbackUrl, ct);

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
            Raw: rawKey
        );

        credentials.ClearTokens(); // API key replaces OAuth session
        credentials.SaveApiKey(stored);
    }

    // ── Token refresh ────────────────────────────────────────────────────────

    public async Task<StoredTokens> RefreshAsync(CancellationToken ct = default)
    {
        var existing = credentials.LoadTokens()
            ?? throw new InvalidOperationException("No stored tokens to refresh.");

        var authConfig = await DiscoverConfigAsync(ct);

        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = authConfig.CliClientId,
            ["refresh_token"] = existing.RefreshToken
        });

        var response = await http.PostAsync(authConfig.GetTokenEndpoint(), body, ct);
        response.EnsureSuccessStatusCode();

        var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Empty token response.");

        var tokens = ToStoredTokens(tokenResponse);
        credentials.SaveTokens(tokens);
        return tokens;
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

    private static string BuildAuthorizationUrl(AuthConfig authConfig, string codeChallenge, string callbackUrl)
    {
        var q = System.Web.HttpUtility.ParseQueryString(string.Empty);
        q["client_id"] = authConfig.CliClientId;
        q["redirect_uri"] = callbackUrl;
        q["response_type"] = "code";
        q["scope"] = "openid profile email offline_access";
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
