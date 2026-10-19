using System.Security.Cryptography;
using BellaCli.Commands.Issue;
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
/// The exchange needs the tenant, project and environment slugs (--tenant/--project/--env, or
/// BELLA_BAXTER_TENANT/BELLA_BAXTER_PROJECT/BELLA_BAXTER_ENV, or a .bella file): the server looks for the
/// TrustDomain of THAT environment and answers 422 missing_context without them. The role of the issued key
/// (Consumer/Manager) is determined by the TrustDomain configured in Bella.
///
/// Typical CI usage:
///   bella auth oidc --tenant acme --project my-api --env production
///   → exports BELLA_BAXTER_API_KEY (and the legacy alias BELLA_API_KEY) to GITHUB_ENV, masked,
///     or prints it for other platforms
///
/// Spec 085 — every sign-in also makes a throwaway device keypair IN MEMORY and sends its public half, so the key it
/// issues is bound to a device and is accepted by a tenant that requires registered devices. On GitHub the private
/// half is exported to later steps as BELLA_BAXTER_PRIVATE_KEY (masked FIRST: GitHub masks only what it has already
/// been told to mask). Anywhere else it is never printed — `bella run` signs in and reads in one command and keeps it
/// in memory.
/// </summary>
public class AuthOidcSettings : CommandSettings
{
    [CommandOption("--audience <AUDIENCE>")]
    [System.ComponentModel.Description(
        "OIDC audience to request. Also BELLA_OIDC_AUDIENCE; default bella-baxter. "
        + "Must be one the trust domain accepts.")]
    public string? Audience { get; init; }

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

public class AuthOidcCommand(WorkloadIdentityService workloadIdentity, ConfigService config, IOutputWriter output)
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
            await output.StatusAsync($"Obtaining OIDC token from {platform}...", async () =>
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

        // Global exchange — the server finds the TrustDomain of the named tenant/project/environment
        OidcExchangeResult? result = null;
        OidcExchangeRefusal? refusal = null;

        // Field by field: --tenant/--project/--env, then the variables and .bella (#1370). When one is still empty
        // the exchange is not called; the error names exactly which, and how to supply each.
        var (tenantSlug, projectSlug, envSlug) = workloadIdentity.ResolveSlugs(
            settings.Tenant, settings.Project, settings.Env);
        var missing = WorkloadIdentityService.MissingSlugs(tenantSlug, projectSlug, envSlug);
        if (missing.Count > 0)
        {
            output.WriteError(
                new KeylessExchange.MissingContext(WorkloadIdentityService.DetectPlatform(), missing).Describe(includeLoginHint: false),
                "missing_context");
            return 1;
        }

        // Spec 085 — a fresh keypair for THIS sign-in, in memory. Its public half is bound to the key the server issues;
        // its private half goes only where the platform below can keep it secret.
        using var deviceKey = DeviceKeypair.CreateKey();

        // #1317 — the exchange reads the server address; the question about a .bella's server is asked here,
        // outside the spinner (a prompt cannot run inside it).
        config.ResolveServer();
        await output.StatusAsync("Exchanging OIDC token for Bella key...", async () =>
                {
                    try
                    {
                        result = await workloadIdentity.ExchangeGlobalAsync(
                            oidcToken, tenantSlug, projectSlug, envSlug, ct, DeviceKeypair.PublicKeyOf(deviceKey));
                    }
                    catch (OidcExchangeRefusedException refused)
                    {
                        refusal = refused.Refusal;
                    }
                }
            );

        if (refusal is not null)
        {
            // The server ANSWERED. Say what it said, by its own name — "verify the trust domain" is wrong advice for a
            // tenant that needs a device key, and a runner with a stale CLI needs to know that is the reason.
            var message = refusal.Describe();
            if (refusal.Code == "device_key_required")
                message += " Update the CLI: current versions send a device key with the sign-in.";
            else if (!refusal.IsDeviceKeyRefusal)
                message += " Verify that a TrustDomain is configured in Bella for this environment and matches the issuer.";
            output.WriteError(message, refusal.Code ?? "exchange_refused");
            return 1;
        }

        if (result is null)
        {
            // Name the platform that was detected; with --token there is none, so say "the supplied token".
            var source = WorkloadIdentityService.DetectPlatform() is var detected && detected != WorkloadPlatform.None
                ? $"{detected} OIDC issuer"
                : "issuer of the supplied token";
            output.WriteError(
                $"OIDC exchange failed. Verify that a TrustDomain is configured in Bella "
                    + $"for this environment and matches the {source}.",
                "exchange_failed"
            );
            return 1;
        }

        // Export to CI environment — best-effort, no platform needed when --token used
        var exportPlatform = WorkloadIdentityService.DetectPlatform();
        ExportToCI(result.Token, exportPlatform, deviceKey, announceNote: !settings.Json);

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

    /// <summary>The line that tells a non-GitHub caller where the private half is, and why it is not here.</summary>
    internal const string DeviceKeyNote =
        "The device key bound to this API key is not printed. A later step cannot use the API key alone on a tenant that "
        + "requires registered devices: `bella run` signs in and reads in one command and keeps the device key in memory.";

    /// <summary>
    /// Hands the issued key — and, on GitHub, its device key — to later steps.
    /// </summary>
    /// <remarks>
    /// <para><b>The order is the control.</b> GitHub's runner masks a value in the log only if it has already seen
    /// <c>::add-mask::</c> for it, and it reads <c>$GITHUB_ENV</c> when the step ends — so every secret is masked BEFORE
    /// the file is written, the API key and then EVERY line of the PEM (a multi-line secret is masked line by line, or the
    /// lines print in clear). Writing first and masking after, as this did for the key alone, leaves the value
    /// unmasked for as long as it takes the step to finish.</para>
    /// <para>The multi-line value uses the runner's <c>NAME&lt;&lt;DELIMITER</c> form with a random delimiter, so the PEM
    /// cannot close its own block early. Elsewhere the private half is never printed: a pipeline log is not a secret
    /// store, and <c>bella run</c> needs no exported key at all.</para>
    /// </remarks>
    private static void ExportToCI(string token, WorkloadPlatform platform, ECDiffieHellman deviceKey, bool announceNote)
    {
        if (platform == WorkloadPlatform.GitHubActions)
        {
            var githubEnv = Environment.GetEnvironmentVariable("GITHUB_ENV");
            if (!string.IsNullOrEmpty(githubEnv))
            {
                var pem = DeviceKeypair.ExportPrivateKeyPem(deviceKey);
                var pemLines = pem.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                // 1. Mask first: the API key, then every line of the PEM.
                Console.WriteLine($"::add-mask::{token}");
                foreach (var line in pemLines)
                    Console.WriteLine($"::add-mask::{line}");

                // 2. Only then write.
                var delimiter = "BELLA_EOF_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                var nl = Environment.NewLine;
                File.AppendAllText(githubEnv, $"BELLA_BAXTER_API_KEY={token}{nl}");
                File.AppendAllText(githubEnv, $"BELLA_API_KEY={token}{nl}"); // legacy alias
                File.AppendAllText(
                    githubEnv,
                    $"BELLA_BAXTER_PRIVATE_KEY<<{delimiter}{nl}{string.Join(nl, pemLines)}{nl}{delimiter}{nl}");
                return;
            }
        }

        if (platform == WorkloadPlatform.GitLabCI)
            Console.WriteLine($"export BELLA_BAXTER_API_KEY={token}");
        else
            // Generic: print as dotenv — caller can source or eval
            Console.WriteLine($"BELLA_BAXTER_API_KEY={token}");

        // A comment, so `eval`/`source` of the output stays valid; and never the private half.
        if (announceNote)
            Console.WriteLine($"# {DeviceKeyNote}");
    }
}
