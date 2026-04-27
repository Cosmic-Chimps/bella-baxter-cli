using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Auth;

/// <summary>
/// bella auth oidc — exchange a platform OIDC token for a short-lived Bella API key
/// and export it to the CI environment.
///
/// Works on any platform that WorkloadIdentityService supports:
///   GitHub Actions, GitLab CI, Azure Pipelines, AWS CodeBuild, GCP Cloud Build, Kubernetes
///
/// The server finds the matching TrustDomain automatically by OIDC issuer —
/// no project/environment context needed. The role of the issued key (Consumer/Manager)
/// is determined by the TrustDomain configured in Bella.
///
/// Typical CI usage:
///   bella auth oidc
///   → exports BELLA_API_KEY to GITHUB_ENV (or prints for other platforms)
/// </summary>
public class AuthOidcSettings : CommandSettings
{
    [CommandOption("--audience <AUDIENCE>")]
    [System.ComponentModel.Description("OIDC audience claim (default: bella-baxter)")]
    public string Audience { get; init; } = "bella-baxter";

    [CommandOption("--token <TOKEN>")]
    [System.ComponentModel.Description("Use a pre-obtained OIDC token directly (skips platform detection and token fetch — useful for local testing)")]
    public string? Token { get; init; }

    [CommandOption("--tenant <TENANT>")]
    [System.ComponentModel.Description("Tenant slug (overrides BELLA_BAXTER_TENANT and .bella file)")]
    public string? Tenant { get; init; }

    [CommandOption("--project <PROJECT>")]
    [System.ComponentModel.Description("Project slug (overrides BELLA_BAXTER_PROJECT and .bella file)")]
    public string? Project { get; init; }

    [CommandOption("--env <ENV>")]
    [System.ComponentModel.Description("Environment slug (overrides BELLA_BAXTER_ENV and .bella file)")]
    public string? Env { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class AuthOidcCommand(WorkloadIdentityService workloadIdentity, IOutputWriter output)
    : AsyncCommand<AuthOidcSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        AuthOidcSettings settings,
        CancellationToken ct
    )
    {
        string? oidcToken = settings.Token;

        if (string.IsNullOrEmpty(oidcToken))
        {
            var platform = WorkloadIdentityService.DetectPlatform();

            if (platform == WorkloadPlatform.None)
            {
                output.WriteError(
                    "Not running in a recognised workload environment. "
                        + "Supported: GitHub Actions, GitLab CI, Azure Pipelines, AWS CodeBuild, GCP Cloud Build, Kubernetes. "
                        + "Use --token <TOKEN> to supply an OIDC token directly for local testing.",
                    "not_workload_env"
                );
                return 1;
            }

            // Obtain OIDC token from platform
            await AnsiConsole
                .Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(
                    $"Obtaining OIDC token from {platform}...",
                    async _ =>
                    {
                        oidcToken = await workloadIdentity.GetOidcTokenAsync(settings.Audience, ct);
                    }
                );

            if (string.IsNullOrEmpty(oidcToken))
            {
                output.WriteError(
                    $"Failed to obtain OIDC token from {platform}. "
                        + "Ensure the workflow has `id-token: write` permission.",
                    "oidc_token_failed"
                );
                return 1;
            }
        }

        // Global exchange — server finds matching TrustDomain by issuer + env context
        OidcExchangeResult? result = null;
        string? exchangeError = null;
        await AnsiConsole
            .Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(
                "Exchanging OIDC token for Bella key...",
                async _ =>
                {
                    var (tenantSlug, projectSlug, envSlug) = workloadIdentity.ResolveSlugs(
                        settings.Tenant, settings.Project, settings.Env);

                    if (string.IsNullOrWhiteSpace(tenantSlug)
                        || string.IsNullOrWhiteSpace(projectSlug)
                        || string.IsNullOrWhiteSpace(envSlug))
                    {
                        exchangeError =
                            "No project context found. Provide --tenant, --project, --env " +
                            "or set BELLA_BAXTER_TENANT, BELLA_BAXTER_PROJECT, BELLA_BAXTER_ENV " +
                            "or add an org/project/environment entry to your .bella file.";
                        return;
                    }

                    result = await workloadIdentity.ExchangeGlobalAsync(
                        oidcToken, tenantSlug, projectSlug, envSlug, ct);
                }
            );

        if (exchangeError is not null)
        {
            output.WriteError(exchangeError, "missing_context");
            return 1;
        }

        if (result is null)
        {
            output.WriteError(
                $"OIDC exchange failed. Verify that a TrustDomain is configured in Bella "
                    + $"matching the GitHubActions OIDC issuer.",
                "exchange_failed"
            );
            return 1;
        }

        // Export to CI environment — best-effort, no platform needed when --token used
        var exportPlatform = WorkloadIdentityService.DetectPlatform();
        ExportToCI(result.Token, exportPlatform);

        if (settings.Json)
        {
            output.WriteObject(
                new
                {
                    apiKey = result.Token,
                    expiresAt = result.ExpiresAt,
                    platform = exportPlatform.ToString(),
                }
            );
        }
        else
        {
            output.WriteSuccess(
                $"Bella API key issued. Expires at {result.ExpiresAt:u}. "
                    + $"Key exported as BELLA_BAXTER_API_KEY."
            );
        }

        return 0;
    }

    private static void ExportToCI(string token, WorkloadPlatform platform)
    {
        if (platform == WorkloadPlatform.GitHubActions)
        {
            var githubEnv = Environment.GetEnvironmentVariable("GITHUB_ENV");
            if (!string.IsNullOrEmpty(githubEnv))
            {
                File.AppendAllText(githubEnv, $"BELLA_BAXTER_API_KEY={token}{Environment.NewLine}");
                File.AppendAllText(githubEnv, $"BELLA_API_KEY={token}{Environment.NewLine}"); // legacy alias
                Console.WriteLine($"::add-mask::{token}");
                return;
            }
        }

        if (platform == WorkloadPlatform.GitLabCI)
        {
            Console.WriteLine($"export BELLA_BAXTER_API_KEY={token}");
            return;
        }

        // Generic: print as dotenv — caller can source or eval
        Console.WriteLine($"BELLA_BAXTER_API_KEY={token}");
    }
}
