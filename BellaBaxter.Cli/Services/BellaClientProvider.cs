using BellaBaxter.Client;
using BellaCli.Infrastructure;

namespace BellaCli.Services;

/// <summary>
/// Creates a configured BellaClient from stored credentials.
/// Also resolves the output mode based on auth type.
/// </summary>
public class BellaClientProvider(
    ConfigService config,
    CredentialStore credentials,
    GlobalSettings settings
)
{
    /// <summary>A BellaClient paired with the raw access token for direct HTTP calls.</summary>
    public record BellaClientWrapper(BellaClient BellaClient, string AccessToken);

    private const string CiJwtError =
        "Running in a CI/CD environment but authenticated via OAuth token.\n"
        + "OAuth tokens are short-lived and not suitable for automation.\n\n"
        + "Use one of:\n"
        + "  BELLA_BAXTER_API_KEY=bax-...   (API key from the WebApp)\n"
        + "  bella login --api-key bax-...  (store it locally)\n"
        + "  Workload identity              (keyless OIDC — no secret needed)\n\n"
        + "See: https://docs.bella.cosmicchimps.io/keyless";

    /// <summary>
    /// Creates a BellaClient using an explicit API key token.
    /// Used by workload identity to create a client with a short-lived exchanged token
    /// without storing it in the CredentialStore.
    /// </summary>
    public BellaClient CreateClientWithApiKey(string rawApiKey)
    {
        settings.OutputMode = OutputMode.Json;
        var appClient = Environment.GetEnvironmentVariable("BELLA_BAXTER_APP_CLIENT");
        return BellaClientFactory.CreateWithHmacApiKey(
            config.ApiUrl,
            rawApiKey,
            DebugLoggingHandler.IsEnabled ? new DebugLoggingHandler() : null,
            bellaClient: "bella-cli",
            appClient: appClient
        );
    }

    public BellaClient CreateClient(string? appClientOverride = null)
    {
        var apiUrl = config.ApiUrl;
        var appClient =
            appClientOverride ?? Environment.GetEnvironmentVariable("BELLA_BAXTER_APP_CLIENT");

        // BELLA_BAXTER_API_KEY env var — standardized name, HMAC-signed (matches SDK convention)
        var envApiKey = Environment.GetEnvironmentVariable("BELLA_BAXTER_API_KEY");
        if (envApiKey is not null)
        {
            settings.OutputMode = OutputMode.Json;
            return BellaClientFactory.CreateWithHmacApiKey(
                apiUrl,
                envApiKey,
                DebugLoggingHandler.IsEnabled ? new DebugLoggingHandler() : null,
                bellaClient: "bella-cli",
                appClient: appClient
            );
        }

        // BELLA_BAXTER_ACCESS_TOKEN — OAuth JWT bearer (injected by `bella exec`)
        var envToken = Environment.GetEnvironmentVariable("BELLA_BAXTER_ACCESS_TOKEN");
        if (envToken is not null)
        {
            if (WorkloadIdentityService.IsWorkloadEnvironment())
                throw new InvalidOperationException(CiJwtError);
            return BellaClientFactory.CreateWithBearerToken(apiUrl, envToken);
        }

        // API key stored via `bella login --api-key`
        var apiKey = credentials.LoadApiKey();
        if (apiKey is not null)
        {
            settings.OutputMode = OutputMode.Json;
            return BellaClientFactory.CreateWithHmacApiKey(
                apiUrl,
                apiKey.Raw,
                DebugLoggingHandler.IsEnabled ? new DebugLoggingHandler() : null,
                bellaClient: "bella-cli",
                appClient: appClient
            );
        }

        var tokens = credentials.LoadTokens();
        if (tokens is not null)
        {
            if (WorkloadIdentityService.IsWorkloadEnvironment())
                throw new InvalidOperationException(CiJwtError);
            return BellaClientFactory.CreateWithBearerToken(
                apiUrl,
                tokens.AccessToken,
                DebugLoggingHandler.IsEnabled ? new DebugLoggingHandler() : null
            );
        }

        throw new InvalidOperationException("Not authenticated. Run 'bella login' first.");
    }

    /// <summary>
    /// Creates a BellaClient and also returns the raw access token so commands can make
    /// direct HTTP calls to endpoints not yet in the generated SDK.
    /// </summary>
    public BellaClientWrapper CreateClientWrapper()
    {
        var apiUrl = config.ApiUrl;

        var appClient = Environment.GetEnvironmentVariable("BELLA_BAXTER_APP_CLIENT");

        // BELLA_BAXTER_API_KEY env var — standardized name, HMAC-signed (matches SDK convention)
        var envApiKey = Environment.GetEnvironmentVariable("BELLA_BAXTER_API_KEY");
        if (envApiKey is not null)
        {
            settings.OutputMode = OutputMode.Json;
            return new BellaClientWrapper(
                BellaClientFactory.CreateWithHmacApiKey(
                    apiUrl,
                    envApiKey,
                    DebugLoggingHandler.IsEnabled ? new DebugLoggingHandler() : null,
                    bellaClient: "bella-cli",
                    appClient: appClient
                ),
                envApiKey
            );
        }

        // BELLA_BAXTER_ACCESS_TOKEN — OAuth JWT bearer (injected by `bella exec`)
        var envToken = Environment.GetEnvironmentVariable("BELLA_BAXTER_ACCESS_TOKEN");
        if (envToken is not null)
        {
            if (WorkloadIdentityService.IsWorkloadEnvironment())
                throw new InvalidOperationException(CiJwtError);
            return new BellaClientWrapper(
                BellaClientFactory.CreateWithBearerToken(apiUrl, envToken),
                envToken
            );
        }

        var apiKey = credentials.LoadApiKey();
        if (apiKey is not null)
        {
            settings.OutputMode = OutputMode.Json;
            return new BellaClientWrapper(
                BellaClientFactory.CreateWithHmacApiKey(
                    apiUrl,
                    apiKey.Raw,
                    DebugLoggingHandler.IsEnabled ? new DebugLoggingHandler() : null,
                    bellaClient: "bella-cli",
                    appClient: appClient
                ),
                apiKey.Raw
            );
        }

        var tokens = credentials.LoadTokens();
        if (tokens is not null)
        {
            if (WorkloadIdentityService.IsWorkloadEnvironment())
                throw new InvalidOperationException(CiJwtError);
            return new BellaClientWrapper(
                BellaClientFactory.CreateWithBearerToken(apiUrl, tokens.AccessToken),
                tokens.AccessToken
            );
        }

        throw new InvalidOperationException("Not authenticated. Run 'bella login' first.");
    }

    /// <summary>Applies OutputMode.Json if --json flag was passed or stdout is redirected.</summary>
    public void ApplyOutputModeOverrides(bool jsonFlag)
    {
        if (jsonFlag || Console.IsOutputRedirected)
            settings.OutputMode = OutputMode.Json;
    }
}
