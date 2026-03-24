using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BellaCli.Commands.Shell;

namespace BellaCli.Services;

/// <summary>
/// Which CI/workload platform the CLI is running on.
/// </summary>
public enum WorkloadPlatform
{
    None,
    GitHubActions,
    GitLabCI,
    AzurePipelines,
    AwsCodeBuild,
    GoogleCloudBuild,
    Kubernetes,
}

/// <summary>
/// Detects and exchanges a platform-issued OIDC token for a short-lived Bella API key.
///
/// Flow:
///   1. Detect platform (GitHub Actions / Kubernetes)
///   2. Obtain the platform OIDC token (GitHub: request via OIDC token URL; K8s: read SA file)
///   3. Resolve project slug + environment slug from args / env vars / .bella / global config
///   4. POST /api/v1/token  { oidcToken, projectSlug, environmentSlug }
///   5. Return the short-lived bax-… token (never persisted — ephemeral)
/// </summary>
public class WorkloadIdentityService(HttpClient httpClient, ConfigService config)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // ── Platform detection ────────────────────────────────────────────────────

    /// <summary>Detects which workload platform the process is running on.</summary>
    public static WorkloadPlatform DetectPlatform()
    {
        // GitHub Actions sets both env vars when `id-token: write` permission is granted
        if (
            !string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_URL")
            )
            && !string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_TOKEN")
            )
        )
            return WorkloadPlatform.GitHubActions;

        // Azure Pipelines sets TF_BUILD=True and exposes the OIDC token REST endpoint via system vars
        if (
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD"))
            && !string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable("SYSTEM_TEAMFOUNDATIONCOLLECTIONURI")
            )
        )
            return WorkloadPlatform.AzurePipelines;

        // GitLab CI sets CI_JOB_JWT_V2 (direct token, no extra request needed)
        if (
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITLAB_CI"))
            && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI_JOB_JWT_V2"))
        )
            return WorkloadPlatform.GitLabCI;

        // AWS CodeBuild sets CODEBUILD_BUILD_ID and provides an OIDC token via AWS_CONTAINER_CREDENTIALS_RELATIVE_URI
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODEBUILD_BUILD_ID")))
            return WorkloadPlatform.AwsCodeBuild;

        // Google Cloud Build sets GOOGLE_CLOUD_BUILD or the metadata server is reachable
        if (
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT"))
            && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BUILD_ID"))
        )
            return WorkloadPlatform.GoogleCloudBuild;

        // Kubernetes injects the ServiceAccount token file into every pod
        if (File.Exists("/var/run/secrets/kubernetes.io/serviceaccount/token"))
            return WorkloadPlatform.Kubernetes;

        return WorkloadPlatform.None;
    }

    public static bool IsWorkloadEnvironment() => DetectPlatform() != WorkloadPlatform.None;

    // ── OIDC token acquisition ────────────────────────────────────────────────

    /// <summary>
    /// Obtains the raw platform OIDC token.
    /// Returns null if the platform is not detected or the token cannot be obtained.
    /// </summary>
    public async Task<string?> GetOidcTokenAsync(
        string audience = "bella-baxter",
        CancellationToken ct = default
    )
    {
        return DetectPlatform() switch
        {
            WorkloadPlatform.GitHubActions => await GetGitHubActionsTokenAsync(audience, ct),
            WorkloadPlatform.AzurePipelines => await GetAzurePipelinesTokenAsync(audience, ct),
            WorkloadPlatform.GitLabCI => GetGitLabToken(),
            WorkloadPlatform.AwsCodeBuild => await GetAwsCodeBuildTokenAsync(audience, ct),
            WorkloadPlatform.GoogleCloudBuild => await GetGoogleCloudBuildTokenAsync(audience, ct),
            WorkloadPlatform.Kubernetes => GetKubernetesServiceAccountToken(),
            _ => null,
        };
    }

    private async Task<string?> GetGitHubActionsTokenAsync(string audience, CancellationToken ct)
    {
        var requestUrl = Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_URL");
        var requestToken = Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_TOKEN");

        if (string.IsNullOrEmpty(requestUrl) || string.IsNullOrEmpty(requestToken))
            return null;

        var separator = requestUrl.Contains('?') ? "&" : "?";
        var url = $"{requestUrl}{separator}audience={Uri.EscapeDataString(audience)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", $"bearer {requestToken}");
            request.Headers.Add("Accept", "application/json");

            var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<GitHubOidcResponse>(JsonOpts, ct);
            return body?.Value;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> GetAzurePipelinesTokenAsync(string audience, CancellationToken ct)
    {
        // Azure Pipelines OIDC: POST to the distributedtask OIDC token endpoint
        // sub claim format: p://{org}/{project}/{pipelineId}
        var collectionUri = Environment.GetEnvironmentVariable(
            "SYSTEM_TEAMFOUNDATIONCOLLECTIONURI"
        );
        var projectId = Environment.GetEnvironmentVariable("SYSTEM_TEAMPROJECTID");
        var planId = Environment.GetEnvironmentVariable("SYSTEM_PLANID");
        var jobId = Environment.GetEnvironmentVariable("SYSTEM_JOBID");
        var accessToken = Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN");

        if (
            string.IsNullOrEmpty(collectionUri)
            || string.IsNullOrEmpty(projectId)
            || string.IsNullOrEmpty(planId)
            || string.IsNullOrEmpty(jobId)
            || string.IsNullOrEmpty(accessToken)
        )
            return null;

        var url =
            $"{collectionUri.TrimEnd('/')}/{projectId}/_apis/distributedtask/hubs/build/plans/{planId}/jobs/{jobId}/oidctoken?audience={Uri.EscapeDataString(audience)}&api-version=7.1-preview.1";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Authorization", $"Bearer {accessToken}");
            request.Headers.Add("Accept", "application/json");
            request.Content = new StringContent(
                "{}",
                System.Text.Encoding.UTF8,
                "application/json"
            );

            var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<AzurePipelinesOidcResponse>(
                JsonOpts,
                ct
            );
            return body?.OidcToken;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetGitLabToken()
    {
        // GitLab CI injects CI_JOB_JWT_V2 directly — no HTTP request needed
        return Environment.GetEnvironmentVariable("CI_JOB_JWT_V2");
    }

    private async Task<string?> GetAwsCodeBuildTokenAsync(string audience, CancellationToken ct)
    {
        // AWS CodeBuild: fetch OIDC token from the container credentials endpoint
        // CODEBUILD_BUILD_ID and AWS_CONTAINER_CREDENTIALS_RELATIVE_URI are set automatically
        var relativeUri = Environment.GetEnvironmentVariable(
            "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI"
        );
        if (string.IsNullOrEmpty(relativeUri))
            return null;

        try
        {
            var url =
                $"http://169.254.170.2{relativeUri}?audience={Uri.EscapeDataString(audience)}";
            var response = await httpClient.GetFromJsonAsync<AwsOidcResponse>(url, JsonOpts, ct);
            return response?.IdToken;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> GetGoogleCloudBuildTokenAsync(string audience, CancellationToken ct)
    {
        // Google Cloud Build: fetch OIDC token from the GCE metadata server
        try
        {
            var url =
                $"http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/identity?audience={Uri.EscapeDataString(audience)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Metadata-Flavor", "Google");

            var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadAsStringAsync(ct)).Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetKubernetesServiceAccountToken()
    {
        const string tokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token";
        try
        {
            return File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    // ── Slug resolution ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolves project + environment slugs for workload identity from (priority order):
    ///   1. Explicit arguments passed to the command
    ///   2. BELLA_BAXTER_PROJECT / BELLA_BAXTER_ENV environment variables
    ///   3. .bella file in the current directory tree
    ///   4. Global default project/environment stored in config
    /// Returns (null, null) if no context can be determined.
    /// </summary>
    public (string? ProjectSlug, string? EnvironmentSlug) ResolveSlugs(
        string? explicitProject = null,
        string? explicitEnvironment = null
    )
    {
        // 1. Explicit command arguments take highest priority
        if (
            !string.IsNullOrWhiteSpace(explicitProject)
            && !string.IsNullOrWhiteSpace(explicitEnvironment)
        )
            return (explicitProject, explicitEnvironment);

        // 2. Env vars (same ones used by `bella context use`)
        var (ctxProject, ctxEnv, _) = ContextCommand.ResolveContext(config);
        if (!string.IsNullOrWhiteSpace(ctxProject) && !string.IsNullOrWhiteSpace(ctxEnv))
            return (ctxProject, ctxEnv);

        return (null, null);
    }

    // ── Exchange ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Exchanges an OIDC token for a short-lived Bella API key using project + environment slugs.
    /// Calls POST /api/v1/token — no Bella auth required.
    /// Returns null if the exchange fails.
    /// </summary>
    public async Task<OidcExchangeResult?> ExchangeBySlugAsync(
        string projectSlug,
        string environmentSlug,
        string oidcToken,
        CancellationToken ct = default
    )
    {
        var url = $"{config.ApiUrl.TrimEnd('/')}/api/v1/token";
        try
        {
            var response = await httpClient.PostAsJsonAsync(
                url,
                new
                {
                    oidcToken,
                    projectSlug,
                    environmentSlug,
                },
                JsonOpts,
                ct
            );

            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<OidcExchangeResult>(JsonOpts, ct)
                : null;
        }
        catch
        {
            return null;
        }
    }

    // ── High-level auto-exchange ──────────────────────────────────────────────

    /// <summary>
    /// Full auto-detection flow:
    ///   detect platform → resolve slugs → get OIDC token → exchange → return short-lived token.
    ///
    /// Returns null (silently) when:
    ///   - Not running in a recognised workload environment
    ///   - Cannot determine project/environment slugs
    ///   - OIDC token cannot be obtained
    ///   - Exchange endpoint rejects the token
    ///
    /// Never throws. Never persists anything.
    /// </summary>
    public async Task<OidcExchangeResult?> TryAutoExchangeAsync(
        string? explicitProject = null,
        string? explicitEnvironment = null,
        string audience = "bella-baxter",
        CancellationToken ct = default
    )
    {
        if (DetectPlatform() == WorkloadPlatform.None)
            return null;

        var (projectSlug, environmentSlug) = ResolveSlugs(explicitProject, explicitEnvironment);
        if (string.IsNullOrWhiteSpace(projectSlug) || string.IsNullOrWhiteSpace(environmentSlug))
            return null;

        var oidcToken = await GetOidcTokenAsync(audience, ct);
        if (string.IsNullOrEmpty(oidcToken))
            return null;

        return await ExchangeBySlugAsync(projectSlug, environmentSlug, oidcToken, ct);
    }
}

// ── Response types ────────────────────────────────────────────────────────────

public record OidcExchangeResult(string Token, DateTimeOffset ExpiresAt);

file record GitHubOidcResponse([property: JsonPropertyName("value")] string Value);

file record AzurePipelinesOidcResponse([property: JsonPropertyName("oidcToken")] string OidcToken);

file record AwsOidcResponse([property: JsonPropertyName("IdToken")] string IdToken);
