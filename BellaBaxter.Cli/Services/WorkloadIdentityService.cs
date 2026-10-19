using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BellaBaxter.Client;
using BellaBaxter.Client.Models;
using Microsoft.Kiota.Abstractions;
using BellaCli.Commands.Issue;
using BellaCli.Commands.Shell;
using BellaCli.Infrastructure;

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
    // Used only for external platform OIDC token fetching (GitHub, Azure, AWS, GCP endpoints).
    // Bella API calls use BellaClientFactory.CreateAnonymous() via the SDK instead.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // ── OIDC audience (spec 064) ──────────────────────────────────────────────

    /// <summary>The audience requested when nothing names one — what the CLI has always requested.</summary>
    public const string DefaultOidcAudience = "bella-baxter";

    /// <summary>Environment variable naming the audience to request.</summary>
    public const string OidcAudienceVariable = "BELLA_OIDC_AUDIENCE";

    /// <summary>
    /// Spec 064 (FR-020) — the audience the keyless flow asks the CI provider for: <c>--audience</c>, then
    /// <c>BELLA_OIDC_AUDIENCE</c>, then <c>bella-baxter</c>. A trust domain that enforces its audience admits only tokens
    /// minted for one of its accepted values; the default is the platform's, so CLI-based CI jobs work against a default
    /// domain with no change at all.
    /// </summary>
    /// <remarks>
    /// <c>.bella</c> is deliberately NOT a source. The token minted for this audience is sent to the server the
    /// <c>.bella</c> <c>url</c> names, and a <c>.bella</c> arrives with any repository a CI job checks out. Letting it
    /// also choose the audience would let a committed file request a token minted for ANOTHER service (a cloud STS,
    /// say) and post it to an address of its choosing. The job's own configuration names the audience.
    /// </remarks>
    public static string ResolveOidcAudience(
        string? explicitAudience,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        var env = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        foreach (var candidate in new[] { explicitAudience, env(OidcAudienceVariable) })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate.Trim();
        }
        return DefaultOidcAudience;
    }

    // ── Platform detection ────────────────────────────────────────────────────

    /// <summary>Detects which workload platform the process is running on.</summary>
    public static WorkloadPlatform DetectPlatform() => DetectPlatform(null, null, null);

    /// <summary>
    /// Detects the platform, with the environment, filesystem and resolved Kubernetes token injectable for testing.
    /// </summary>
    /// <remarks>
    /// Spec 064 (FR-012): Kubernetes is recognised by the token path ACTUALLY IN USE, not only by the kubelet's
    /// default path. Before, a pod that disabled the default mount and projected a token for Bella was detected as
    /// "no platform" and presented nothing — silently — which is exactly the pod the documented rollout produces.
    /// Unless a location is passed in, that path is resolved WITHOUT <c>.bella</c> (<c>ResolveForKeyless</c>): a
    /// committed <c>node_token_path</c> must not be able to switch a laptop's <c>bella run</c> into the keyless flow.
    /// </remarks>
    internal static WorkloadPlatform DetectPlatform(
        Func<string, string?>? getEnvironmentVariable, Func<string, bool>? fileExists, Spiffe.NodeTokenLocation? nodeToken)
    {
        var env = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var exists = fileExists ?? File.Exists;

        // GitHub Actions sets both env vars when `id-token: write` permission is granted
        if (
            !string.IsNullOrEmpty(
                env("ACTIONS_ID_TOKEN_REQUEST_URL")
            )
            && !string.IsNullOrEmpty(
                env("ACTIONS_ID_TOKEN_REQUEST_TOKEN")
            )
        )
            return WorkloadPlatform.GitHubActions;

        // Azure Pipelines sets TF_BUILD=True and exposes the OIDC token REST endpoint via system vars
        if (
            !string.IsNullOrEmpty(env("TF_BUILD"))
            && !string.IsNullOrEmpty(
                env("SYSTEM_TEAMFOUNDATIONCOLLECTIONURI")
            )
        )
            return WorkloadPlatform.AzurePipelines;

        // GitLab CI sets CI_JOB_JWT_V2 (direct token, no extra request needed)
        if (
            !string.IsNullOrEmpty(env("GITLAB_CI"))
            && !string.IsNullOrEmpty(env("CI_JOB_JWT_V2"))
        )
            return WorkloadPlatform.GitLabCI;

        // AWS CodeBuild sets CODEBUILD_BUILD_ID and provides an OIDC token via AWS_CONTAINER_CREDENTIALS_RELATIVE_URI
        if (!string.IsNullOrEmpty(env("CODEBUILD_BUILD_ID")))
            return WorkloadPlatform.AwsCodeBuild;

        // Google Cloud Build sets GOOGLE_CLOUD_BUILD or the metadata server is reachable
        if (
            !string.IsNullOrEmpty(env("GOOGLE_CLOUD_PROJECT"))
            && !string.IsNullOrEmpty(env("BUILD_ID"))
        )
            return WorkloadPlatform.GoogleCloudBuild;

        // Kubernetes: the token the agent would present exists — the projected one when configured or mounted at the
        // conventional path, else the kubelet default (FR-012).
        var token = nodeToken ?? Spiffe.NodeTokenPath.ResolveForKeyless(getEnvironmentVariable: env, fileExists: exists);
        if (exists(token.Path))
            return WorkloadPlatform.Kubernetes;

        return WorkloadPlatform.None;
    }

    public static bool IsWorkloadEnvironment() => DetectPlatform() != WorkloadPlatform.None;

    // ── OIDC token acquisition ────────────────────────────────────────────────

    /// <summary>
    /// Obtains the raw platform OIDC token.
    /// Returns null if the platform is not detected or the token cannot be obtained.
    /// </summary>
    public virtual async Task<string?> GetOidcTokenAsync(
        string? audience = null,
        CancellationToken ct = default
    )
    {
        // Spec 064 — one resolution for every keyless entry point (flag > BELLA_OIDC_AUDIENCE > default; .bella is deliberately not a source).
        audience = ResolveOidcAudience(audience);
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

    private static string? GetKubernetesServiceAccountToken() =>
        ReadKubernetesToken(Spiffe.NodeTokenPath.ResolveForKeyless(), File.Exists, File.ReadAllText);

    /// <summary>
    /// The token the keyless flow sends, or null. Spec 064 — the same resolution the SPIFFE agent uses (minus
    /// <c>.bella</c>), and the same acceptance: only a file that decodes as a JWT is sent. Anything else cannot be an
    /// OIDC token, so posting it could only ever leak a local file's contents.
    /// </summary>
    internal static string? ReadKubernetesToken(
        Spiffe.NodeTokenLocation location, Func<string, bool> fileExists, Func<string, string> readFile)
    {
        try
        {
            var evidence = Spiffe.NodeEvidence.Inspect(
                location, fileExists, readFile, WorkloadPlatform.Kubernetes, static _ => null);
            return evidence.TokenPresent ? readFile(location.Path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    // ── Slug resolution ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolves tenant + project + environment slugs for workload identity, FIELD BY FIELD (#1370). Each field takes
    /// the first of:
    ///   1. the explicit argument passed to the command (<c>-t</c>, <c>-p</c>, <c>-e</c>)
    ///   2. the ambient context — <c>BELLA_BAXTER_TENANT</c>/<c>BELLA_BAXTER_PROJECT</c>/<c>BELLA_BAXTER_ENV</c>, else the
    ///      nearest <c>.bella</c> (<c>org</c> = tenant). That order, and the rule that a source supplies project and
    ///      environment together, are <see cref="ContextCommand.ResolveContextWithOrg"/>'s and unchanged
    ///   3. a lone variable of its own (<c>BELLA_BAXTER_PROJECT=api bella run -e prod</c>) — only for a field the two
    ///      above left empty, so it never outranks anything that used to be chosen
    /// A field no source names is null. Never all-or-nothing: before this, one explicit value was silently dropped
    /// unless all three were given, and the exchange went to whichever environment the ambient context named.
    /// Only the three slugs are merged — a <c>.bella</c> supplies nothing else here.
    /// </summary>
    public (string? TenantSlug, string? ProjectSlug, string? EnvironmentSlug) ResolveSlugs(
        string? explicitTenant = null,
        string? explicitProject = null,
        string? explicitEnvironment = null
    ) =>
        MergeSlugs(
            explicitTenant, explicitProject, explicitEnvironment,
            ContextCommand.ResolveContextWithOrg(config),
            Environment.GetEnvironmentVariable);

    internal static (string? TenantSlug, string? ProjectSlug, string? EnvironmentSlug) MergeSlugs(
        string? explicitTenant,
        string? explicitProject,
        string? explicitEnvironment,
        (string? Project, string? Env, string? Org, string Source) ambient,
        Func<string, string?> getEnvironmentVariable)
    {
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        string? FirstEnvironmentVariable(params string[] names) =>
            names.Select(n => Clean(getEnvironmentVariable(n))).FirstOrDefault(v => v is not null);

        return (
            Clean(explicitTenant) ?? Clean(ambient.Org) ?? FirstEnvironmentVariable("BELLA_BAXTER_TENANT"),
            Clean(explicitProject) ?? Clean(ambient.Project) ?? FirstEnvironmentVariable("BELLA_BAXTER_PROJECT", "BELLA_PROJECT"),
            Clean(explicitEnvironment) ?? Clean(ambient.Env) ?? FirstEnvironmentVariable("BELLA_BAXTER_ENV", "BELLA_ENV")
        );
    }

    /// <summary>The slugs <see cref="ResolveSlugs"/> left empty, in tenant, project, environment order.</summary>
    public static IReadOnlyList<SlugField> MissingSlugs(string? tenant, string? project, string? environment)
    {
        var missing = new List<SlugField>();
        if (string.IsNullOrWhiteSpace(tenant)) missing.Add(SlugField.Tenant);
        if (string.IsNullOrWhiteSpace(project)) missing.Add(SlugField.Project);
        if (string.IsNullOrWhiteSpace(environment)) missing.Add(SlugField.Environment);
        return missing;
    }

    // ── Exchange ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Exchanges an OIDC token for a short-lived Bella API key.
    /// Requires tenant + project + environment slugs to locate the matching TrustDomain.
    /// </summary>
    /// <param name="publicKey">
    /// Spec 085 — base64 SPKI P-256 public key of the device this key is bound to. A tenant that requires registered
    /// devices admits only a key bound to one, and the server answers 400 <c>device_key_malformed</c> for a value that
    /// cannot be one. It is the trailing parameter so a caller (and a test double) that never sends one keeps compiling.
    /// </param>
    /// <returns>The key, or null when the exchange could not be completed.</returns>
    /// <exception cref="OidcExchangeRefusedException">
    /// The server ANSWERED with a problem (a refusal, not a transport failure): its status, <c>type</c> and <c>detail</c>
    /// are carried, because reducing them to null is how "this tenant needs a device key" once read as "the trust domain
    /// is wrong".
    /// </exception>
    public virtual async Task<OidcExchangeResult?> ExchangeGlobalAsync(
        string oidcToken,
        string? tenantSlug = null,
        string? projectSlug = null,
        string? environmentSlug = null,
        CancellationToken ct = default,
        string? publicKey = null
    )
    {
        try
        {
            var client = BellaClientFactory.CreateAnonymous(
                // Advisory clients-1 — the workload's OIDC token is not sent to a server a .bella chose.
                config.ApiUrlForSuppliedCredential(),
                DebugLoggingHandler.IsEnabled ? new DebugLoggingHandler() : null);
            var resp = await client.Api.V1.Token.PostAsync(
                new ExchangeOidcTokenBySlugCommand
                {
                    OidcToken = oidcToken,
                    TenantSlug = tenantSlug,
                    ProjectSlug = projectSlug,
                    EnvironmentSlug = environmentSlug,
                    PublicKey = publicKey,
                },
                cancellationToken: ct
            );
            return resp is null ? null : new OidcExchangeResult(resp.Token!, resp.ExpiresAt!.Value);
        }
        catch (ApiException ex)
        {
            throw new OidcExchangeRefusedException(OidcExchangeRefusal.From(ex));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bella/oidc] ExchangeGlobalAsync failed: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null)
                Console.Error.WriteLine($"[bella/oidc] Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            return null;
        }
    }

    // ── High-level auto-exchange ──────────────────────────────────────────────

    /// <summary>
    /// Full auto-detection flow:
    ///   detect platform → get OIDC token → resolve slugs → exchange → return short-lived token.
    ///
    /// The result says WHICH of four things happened (#1370 — a bare null used to mean all of them):
    ///   - <see cref="KeylessExchange.NotApplicable"/>: not a workload platform, a Bella key is already in the
    ///     environment, or no OIDC token could be obtained
    ///   - <see cref="KeylessExchange.MissingContext"/>: an exchange would have run but tenant, project or
    ///     environment is still empty; the endpoint was NOT called
    ///   - <see cref="KeylessExchange.Exchanged"/>: the short-lived key
    ///   - <see cref="KeylessExchange.Failed"/>: the exchange endpoint rejected the token
    ///
    /// Never throws. Never persists anything.
    /// </summary>
    public async Task<KeylessExchange> TryAutoExchangeAsync(
        string? explicitTenant = null,
        string? explicitProject = null,
        string? explicitEnvironment = null,
        string? audience = null,
        CancellationToken ct = default
    )
    {
        if (CurrentPlatform() == WorkloadPlatform.None)
            return new KeylessExchange.NotApplicable();

        // If a Bella API key is already in the environment (e.g. exported by a prior
        // 'bella auth oidc' step), skip the OIDC exchange entirely — no need to obtain
        // another key, and attempting it would just produce a noisy 422 error.
        if (!string.IsNullOrEmpty(ApiKeyInEnvironment()))
            return new KeylessExchange.NotApplicable();

        var oidcToken = await GetOidcTokenAsync(audience, ct);
        if (string.IsNullOrEmpty(oidcToken))
            return new KeylessExchange.NotApplicable();

        var (tenantSlug, projectSlug, environmentSlug) = ResolveSlugs(explicitTenant, explicitProject, explicitEnvironment);

        // #1370 — without all three the server can only answer 422 missing_context, which this method used to turn
        // into a bare null and the caller into "Not logged in". Say what is missing, and send nothing.
        var missing = MissingSlugs(tenantSlug, projectSlug, environmentSlug);
        if (missing.Count > 0)
            return new KeylessExchange.MissingContext(CurrentPlatform(), missing);

        // Spec 085 — a throwaway device key, made here and kept in memory: its public half is what the server binds to the
        // key it issues, and the SAME key backs the client that reads, so a tenant that requires registered devices
        // accepts this one command. It is never written anywhere; the caller disposes it with the result.
        var deviceKey = DeviceKeypair.CreateKey();
        try
        {
            var key = await ExchangeGlobalAsync(
                oidcToken, tenantSlug, projectSlug, environmentSlug, ct, DeviceKeypair.PublicKeyOf(deviceKey));
            if (key is null)
            {
                deviceKey.Dispose();
                return new KeylessExchange.Failed();
            }

            return new KeylessExchange.Exchanged(key with { DeviceKey = deviceKey });
        }
        catch (OidcExchangeRefusedException refused)
        {
            deviceKey.Dispose();
            return new KeylessExchange.Failed(refused.Refusal);
        }
        catch
        {
            deviceKey.Dispose();
            throw;
        }
    }

    /// <summary>Tests only: stands in for the machine-dependent platform probe.</summary>
    internal virtual WorkloadPlatform CurrentPlatform() => DetectPlatform();

    /// <summary>Tests only: stands in for the process environment.</summary>
    internal virtual string? ApiKeyInEnvironment() =>
        Environment.GetEnvironmentVariable("BELLA_BAXTER_API_KEY")
        ?? Environment.GetEnvironmentVariable("BELLA_API_KEY");
}

// ── Response types ────────────────────────────────────────────────────────────

/// <summary>
/// The key a trust-domain exchange issued.
/// </summary>
/// <param name="DeviceKey">
/// Spec 085 — present when this process made the device keypair whose public half the key is bound to (the in-process
/// keyless flow). It lives in memory only and is disposed with the result; a caller that needs the private half
/// exports it to a CHILD, never to a file. Kept here rather than on <see cref="KeylessExchange"/> so the context
/// resolution that type models stays untouched.
/// </param>
public sealed record OidcExchangeResult(string Token, DateTimeOffset ExpiresAt, System.Security.Cryptography.ECDiffieHellman? DeviceKey = null)
    : IDisposable
{
    public void Dispose() => DeviceKey?.Dispose();
}

/// <summary>What the server said when it refused an exchange: the HTTP status and the problem's stable code and detail.</summary>
public sealed record OidcExchangeRefusal(int? Status, string? Code, string? Detail)
{
    /// <summary>The refusals a CLI can do something about, by the code the server gives them (spec 085).</summary>
    public bool IsDeviceKeyRefusal => Code is "device_key_required" or "device_key_malformed";

    /// <summary>One line naming the server's reason — never the generic "verify the trust domain".</summary>
    public string Describe()
    {
        var reason = string.Join(" — ", new[] { Code, Detail }.Where(v => !string.IsNullOrWhiteSpace(v)));
        var status = Status is { } code ? $" (HTTP {code})" : string.Empty;
        return reason.Length == 0
            ? $"OIDC exchange refused by the server{status}."
            : $"OIDC exchange refused by the server{status}: {reason}";
    }

    /// <summary>
    /// Reads the problem a Kiota <see cref="ApiException"/> carries. The code is the problem's <c>error</c> extension,
    /// else its <c>type</c> when that is a bare code (not a URL), else its <c>title</c>.
    /// </summary>
    internal static OidcExchangeRefusal From(ApiException ex)
    {
        if (ex is not ProblemDetails problem)
            return new OidcExchangeRefusal(ex.ResponseStatusCode, null, null);

        static string? Text(object? value) => value switch
        {
            string s => s,
            Microsoft.Kiota.Abstractions.Serialization.UntypedString u => u.GetValue(),
            _ => null,
        };

        var code = problem.AdditionalData.TryGetValue("error", out var extension) ? Text(extension) : null;
        if (string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(problem.Type) && !problem.Type.Contains("://", StringComparison.Ordinal))
            code = problem.Type;
        if (string.IsNullOrWhiteSpace(code))
            code = problem.Title;

        return new OidcExchangeRefusal(problem.Status ?? ex.ResponseStatusCode, code, problem.Detail);
    }
}

/// <summary>Thrown by <see cref="WorkloadIdentityService.ExchangeGlobalAsync"/> when the server answered with a refusal.</summary>
public sealed class OidcExchangeRefusedException(OidcExchangeRefusal refusal) : Exception(refusal.Describe())
{
    public OidcExchangeRefusal Refusal { get; } = refusal;
}

file record GitHubOidcResponse([property: System.Text.Json.Serialization.JsonPropertyName("value")] string Value);

file record AzurePipelinesOidcResponse([property: System.Text.Json.Serialization.JsonPropertyName("oidcToken")] string OidcToken);

file record AwsOidcResponse([property: System.Text.Json.Serialization.JsonPropertyName("IdToken")] string IdToken);
