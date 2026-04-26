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

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class AuthOidcCommand(
    WorkloadIdentityService workloadIdentity,
    IOutputWriter output
) : AsyncCommand<AuthOidcSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        AuthOidcSettings settings,
        CancellationToken ct
    )
    {
        var platform = WorkloadIdentityService.DetectPlatform();

        if (platform == WorkloadPlatform.None)
        {
            output.WriteError(
                "Not running in a recognised workload environment. " +
                "Supported: GitHub Actions, GitLab CI, Azure Pipelines, AWS CodeBuild, GCP Cloud Build, Kubernetes.",
                "not_workload_env"
            );
            return 1;
        }

        // Obtain OIDC token from platform
        string? oidcToken = null;
        await AnsiConsole.Status().Spinner(Spinner.Known.Dots)
            .StartAsync($"Obtaining OIDC token from {platform}...", async _ =>
            {
                oidcToken = await workloadIdentity.GetOidcTokenAsync(settings.Audience, ct);
            });

        if (string.IsNullOrEmpty(oidcToken))
        {
            output.WriteError(
                $"Failed to obtain OIDC token from {platform}. " +
                "Ensure the workflow has `id-token: write` permission.",
                "oidc_token_failed"
            );
            return 1;
        }

        // Global exchange — server finds matching TrustDomain by issuer
        OidcExchangeResult? result = null;
        await AnsiConsole.Status().Spinner(Spinner.Known.Dots)
            .StartAsync("Exchanging OIDC token for Bella key...", async _ =>
            {
                result = await workloadIdentity.ExchangeGlobalAsync(oidcToken, ct);
            });

        if (result is null)
        {
            output.WriteError(
                $"OIDC exchange failed. Verify that a TrustDomain is configured in Bella " +
                $"matching the {platform} OIDC issuer.",
                "exchange_failed"
            );
            return 1;
        }

        // Export to CI environment
        ExportToCI(result.Token, platform);

        if (settings.Json)
        {
            output.WriteObject(new
            {
                apiKey = result.Token,
                expiresAt = result.ExpiresAt,
                platform = platform.ToString(),
            });
        }
        else
        {
            output.WriteSuccess(
                $"Bella API key issued. Expires at {result.ExpiresAt:u}. " +
                $"Key exported as BELLA_API_KEY."
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
                File.AppendAllText(githubEnv, $"BELLA_API_KEY={token}{Environment.NewLine}");
                Console.WriteLine($"::add-mask::{token}");
                return;
            }
        }

        if (platform == WorkloadPlatform.GitLabCI)
        {
            Console.WriteLine($"export BELLA_API_KEY={token}");
            return;
        }

        // Generic: print as dotenv — caller can source or eval
        Console.WriteLine($"BELLA_API_KEY={token}");
    }
}
