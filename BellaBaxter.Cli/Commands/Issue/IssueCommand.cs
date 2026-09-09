using System.ComponentModel;
using System.Text.Json;
using BellaBaxter.Client.Models;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Microsoft.Kiota.Abstractions;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Issue;

public class IssueCommand(
    BellaClientProvider clientProvider,
    CredentialStore credentials,
    ContextService contextService,
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

        [CommandOption("-e|--env|--environment <slug>")]
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

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    protected override async Task<int> ExecuteAsync(
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
        // Mirrors IssueEnvironmentToken's own bounds (DefaultTtlMinutes 15 / MaxTtlMinutes 480);
        // the API is authoritative, this only saves a round trip.
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

        // The route is {environmentId:guid}; envId is the SLUG whenever the environment came from
        // /keys/me or .bella, which matches no route and reads as a 404.
        Guid environmentId;
        try
        {
            environmentId = await contextService.ResolveEnvironmentIdAsync(
                wrapper.BellaClient,
                projectSlug,
                envId,
                ct
            );
        }
        catch (Exception ex)
        {
            output.WriteError(
                $"Could not resolve environment '{envSlug}' in project '{projectSlug}': {ex.Message}"
            );
            return 1;
        }

        // ── POST /api/v1/environments/{environmentId}/tokens/issue ───────────
        CreatedApiKeyResponse? response;
        try
        {
            response = await wrapper
                .BellaClient.Api.V1.Environments[environmentId]
                .Tokens.Issue.PostAsync(
                    new IssueEnvironmentTokenRequest
                    {
                        Scopes = [.. scopes],
                        TtlMinutes = settings.Ttl,
                        Reason = settings.Reason,
                    },
                    cancellationToken: ct
                );
        }
        catch (ApiException ex)
        {
            var detail = string.IsNullOrWhiteSpace(ex.Message) ? "" : $": {ex.Message}";
            output.WriteError($"API error ({ex.ResponseStatusCode}){detail}");
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to issue token: {ex.Message}");
            return 1;
        }

        if (string.IsNullOrEmpty(response?.ApiKey))
        {
            output.WriteError("API returned an empty token.");
            return 1;
        }

        // ── Output ──────────────────────────────────────────────────────────
        var isJsonOutput = settings.Output.Equals("json", StringComparison.OrdinalIgnoreCase);

        if (isJsonOutput)
        {
            // The API's own field names — a caller that greps for `token` was never given one.
            Console.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        apiKey = response.ApiKey,
                        keyPrefix = response.KeyPrefix,
                        id = response.Id,
                        expiresAt = response.ExpiresAt,
                    },
                    JsonOptions
                )
            );
        }
        else
        {
            // Print only the raw token to stdout — safe for: export KEY=$(bella issue --scope stripe)
            Console.WriteLine(response.ApiKey);

            // Contextual info goes to stderr so it doesn't pollute the captured token value
            var scopeList = string.Join(", ", scopes);
            Console.Error.WriteLine(
                $"✓ Scoped token issued (scopes: {scopeList} | expires: {settings.Ttl} min)"
            );
            Console.Error.WriteLine("  Save it now — this token will not be shown again.");
        }

        return 0;
    }
}
