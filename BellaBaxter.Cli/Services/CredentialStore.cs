using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace BellaCli.Services;

public record StoredTokens(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string TokenType = "Bearer",
    string? OrgId = null,
    string? OrgName = null,
    string? OrgSlug = null
);

public record StoredApiKey(
    string KeyId,
    string SigningSecret,
    string Raw  // full "bax-{keyId}-{signingSecret}" string
);

public class CredentialStore
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "bella-cli");

    private static readonly string KeysDir = Path.Combine(ConfigDir, "keys");
    private static readonly string TokensFile = Path.Combine(ConfigDir, "tokens.json");
    private static readonly string ApiKeyFile = Path.Combine(ConfigDir, "apikey.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IDataProtector _protector;

    public CredentialStore()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(KeysDir);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(KeysDir))
            .SetApplicationName("bella-cli");

        var provider = services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
        _protector = provider.CreateProtector("bella-cli.credentials.v1");
    }

    // ── OAuth2 tokens ────────────────────────────────────────────────────────

    public void SaveTokens(StoredTokens tokens)
    {
        var json = JsonSerializer.Serialize(tokens, JsonOptions);
        var encrypted = _protector.Protect(json);
        File.WriteAllText(TokensFile, encrypted);
    }

    public StoredTokens? LoadTokens()
    {
        if (!File.Exists(TokensFile)) return null;
        try
        {
            var encrypted = File.ReadAllText(TokensFile);
            var json = _protector.Unprotect(encrypted);
            return JsonSerializer.Deserialize<StoredTokens>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void ClearTokens()
    {
        if (File.Exists(TokensFile))
            File.Delete(TokensFile);
    }

    // ── API keys ─────────────────────────────────────────────────────────────

    public void SaveApiKey(StoredApiKey key)
    {
        var json = JsonSerializer.Serialize(key, JsonOptions);
        var encrypted = _protector.Protect(json);
        File.WriteAllText(ApiKeyFile, encrypted);
    }

    public StoredApiKey? LoadApiKey()
    {
        if (!File.Exists(ApiKeyFile)) return null;
        try
        {
            var encrypted = File.ReadAllText(ApiKeyFile);
            var json = _protector.Unprotect(encrypted);
            return JsonSerializer.Deserialize<StoredApiKey>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void ClearApiKey()
    {
        if (File.Exists(ApiKeyFile))
            File.Delete(ApiKeyFile);
    }

    // ── Auth type detection ──────────────────────────────────────────────────

    public bool HasOAuthTokens() => LoadTokens() is not null;
    public bool HasApiKey() => LoadApiKey() is not null;
    public bool IsAuthenticated() => HasOAuthTokens() || HasApiKey();

    /// <summary>Returns true when the stored credentials are a baxter-opaque-token (API key),
    /// which should default the CLI to JSON output mode.</summary>
    public bool IsApiKeyMode() => HasApiKey() && !HasOAuthTokens();
}
