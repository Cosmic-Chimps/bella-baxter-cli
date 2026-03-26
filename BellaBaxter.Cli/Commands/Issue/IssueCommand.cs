using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Issue;

public class IssueCommand(
    BellaClientProvider clientProvider,
    CredentialStore credentials,
    ContextService contextService,
    ConfigService config,
    IOutputWriter output
) : AsyncCommand<IssueCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("-s|--scope <scopes>")]
        [Description("Required. Comma-separated list of scope names (e.g. stripe,payment)")]
        public string? Scope { get; set; }

        [CommandOption("-t|--ttl <minutes>")]
        [Description("Token lifetime in minutes (default: 15, max: 480)")]
        [DefaultValue(15)]
        public int Ttl { get; set; } = 15;

        [CommandOption("-r|--reason <reason>")]
        [Description("Human-readable reason / client name for audit purposes")]
        [DefaultValue("cli-issued-token")]
        public string Reason { get; set; } = "cli-issued-token";

        [CommandOption("-p|--project <slug>")]
        [Description(
            "Project slug. Falls back to context (.bella file / BELLA_BAXTER_PROJECT env var)"
        )]
        public string? Project { get; set; }

        [CommandOption("-e|--environment <slug>")]
        [Description(
            "Environment slug. Falls back to context (.bella file / BELLA_BAXTER_ENV env var)"
        )]
        public string? Environment { get; set; }

        [CommandOption("-o|--output <format>")]
        [Description(
            "Output format: token (default, just the token string) or json (full response)"
        )]
        [DefaultValue("token")]
        public string Output { get; set; } = "token";
    }

    // ── DTOs for the tokens/issue endpoint ──────────────────────────────────
    private record IssueTokenRequest(
        [property: JsonPropertyName("scopes")] string[] Scopes,
        [property: JsonPropertyName("ttlMinutes")] int TtlMinutes,
        [property: JsonPropertyName("clientName")] string ClientName
    );

    private record IssueTokenResponse(
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("keyPrefix")] string? KeyPrefix,
        [property: JsonPropertyName("expiresAt")] string? ExpiresAt,
        [property: JsonPropertyName("clientName")] string? ClientName
    );

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken ct
    )
    {
        // ── Validate --scope ────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(settings.Scope))
        {
            output.WriteError("--scope is required. Example: bella issue --scope stripe,payment");
            return 1;
        }

        var scopes = settings.Scope.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        if (scopes.Length == 0)
        {
            output.WriteError("--scope must contain at least one scope name.");
            return 1;
        }

        // ── Validate TTL ────────────────────────────────────────────────────
        if (settings.Ttl is < 1 or > 480)
        {
            output.WriteError($"--ttl must be between 1 and 480 minutes (got: {settings.Ttl}).");
            return 1;
        }

        // ── Auth check ──────────────────────────────────────────────────────
        if (!credentials.IsAuthenticated())
        {
            output.WriteError("Not logged in. Run 'bella login' first.");
            return 1;
        }

        BellaClientProvider.BellaClientWrapper wrapper;
        try
        {
            wrapper = clientProvider.CreateClientWrapper();
        }
        catch (Exception ex)
        {
            output.WriteError($"Authentication error: {ex.Message}");
            return 1;
        }

        // ── Resolve project + environment ────────────────────────────────────
        string projectSlug,
            envSlug,
            envId;
        try
        {
            (projectSlug, _, _, envSlug, _, envId) =
                await contextService.ResolveProjectEnvironmentAsync(
                    settings.Project,
                    settings.Environment,
                    wrapper.BellaClient,
                    ct,
                    strictJwtLocal: true,
                    bootstrapBellaFromExplicit: true
                );
        }
        catch (Exception ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }

        // ── POST /api/v1/environments/{envId}/tokens/issue ───────────────────
        // The generated SDK doesn't have this endpoint yet, so we make a raw
        // authenticated HTTP call using the same signing handler.
        var issueUrl = $"{config.ApiUrl.TrimEnd('/')}/api/v1/environments/{envId}/tokens/issue";
        var requestBody = new IssueTokenRequest(scopes, settings.Ttl, settings.Reason);

        using var http = BuildAuthenticatedHttpClient(wrapper.AccessToken);

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await http.PostAsJsonAsync(issueUrl, requestBody, JsonOptions, ct);
        }
        catch (Exception ex)
        {
            output.WriteError($"Network error: {ex.Message}");
            return 1;
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync(ct);
            output.WriteError($"API error ({(int)httpResponse.StatusCode}): {errorBody}");
            return 1;
        }

        IssueTokenResponse? tokenResponse;
        try
        {
            tokenResponse = await httpResponse.Content.ReadFromJsonAsync<IssueTokenResponse>(
                JsonOptions,
                ct
            );
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to parse response: {ex.Message}");
            return 1;
        }

        if (tokenResponse?.Token is null)
        {
            output.WriteError("API returned an empty token.");
            return 1;
        }

        // ── Output ──────────────────────────────────────────────────────────
        var isJsonOutput = settings.Output.Equals("json", StringComparison.OrdinalIgnoreCase);

        if (isJsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(tokenResponse, JsonOptions));
        }
        else
        {
            // Print only the raw token to stdout — safe for: export KEY=$(bella issue --scope stripe)
            Console.WriteLine(tokenResponse.Token);

            // Contextual info goes to stderr so it doesn't pollute the captured token value
            var scopeList = string.Join(", ", scopes);
            Console.Error.WriteLine(
                $"✓ Scoped token issued (scopes: {scopeList} | expires: {settings.Ttl} min)"
            );
            Console.Error.WriteLine("  Save it now — this token will not be shown again.");
        }

        return 0;
    }

    /// <summary>
    /// Builds an HttpClient wired with the correct auth for this session:
    /// - API key (bax-…) → HmacSigningHandler (same scheme as the SDK)
    /// - OAuth2 Bearer token → Authorization: Bearer header
    /// </summary>
    private static HttpClient BuildAuthenticatedHttpClient(string rawAccessToken)
    {
        HttpClient http;

        if (rawAccessToken.StartsWith("bax-", StringComparison.Ordinal))
        {
            var appClient = System.Environment.GetEnvironmentVariable("BELLA_BAXTER_APP_CLIENT");
            var signingHandler = new HmacSigningHandler(rawAccessToken, "bella-cli", appClient)
            {
                InnerHandler = new HttpClientHandler(),
            };
            http = new HttpClient(signingHandler);
        }
        else
        {
            http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                rawAccessToken
            );
        }

        http.DefaultRequestHeaders.Add("Accept", "application/json");
        return http;
    }
}
