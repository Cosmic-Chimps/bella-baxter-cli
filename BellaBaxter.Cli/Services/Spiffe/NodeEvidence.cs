namespace BellaCli.Services.Spiffe;

// Spec 001 T023 (US2) — what the agent can prove about the node it is running on, read locally.
//
// This is the half of `bella spiffe whoami` that needs no running agent and no network: look at the
// filesystem and the environment, and report what evidence WOULD be presented to /attest. That makes
// it the first thing to run when attestation is being refused, because the two commonest causes are
// visible here — no service-account token mounted at all, and a token that is present but empty.
//
// It deliberately does NOT validate the token or call anything. A local command that quietly made a
// network request would be useless in the situation it exists for: an operator working out why the
// network call fails.
//
// NOTE ON REUSE: WorkloadIdentityService.DetectPlatform() already answers "which platform am I on",
// for the unrelated OIDC-to-bax-token flow. Reused rather than reimplemented — a second detector
// would eventually disagree with the first about what a Kubernetes pod looks like.

// SPEC 064: which token is presented is no longer fixed — NodeTokenPath resolves it (flag, environment, `.bella`,
// the conventional projected path, then the kubelet default), and this report says which one and what it carries:
// its audience, issuer and expiry, decoded LOCALLY and labelled unverified. The audience is the line an operator
// needs before enforcing: a kubelet default token names the cluster's own API server, which an enforcing
// environment refuses.

/// <summary>Where a Kubernetes service-account token is mounted. The paths themselves live in <see cref="NodeTokenPath"/>.</summary>
public static class NodeEvidencePaths
{
    /// <summary>The kubelet's default token path. See <see cref="NodeTokenPath.KubeletDefault"/>.</summary>
    public const string KubernetesServiceAccountToken = NodeTokenPath.KubeletDefault;

    /// <summary>The namespace file beside it — useful context, not evidence.</summary>
    public const string KubernetesNamespace = NodeTokenPath.KubeletNamespace;
}

/// <summary>What the agent found locally, for display.</summary>
/// <param name="Platform">The detected platform, or None.</param>
/// <param name="NodeType">
/// The <c>nodeType</c> the agent would send to <c>/attest</c> (<c>k8s</c>, <c>aws-iid</c>), or null
/// when no node evidence is available and attestation would be workload-selector only.
/// </param>
/// <param name="TokenPath">Where the evidence was found, so an operator can go and look at it.</param>
/// <param name="TokenPresent">Whether a token was found AND is non-empty.</param>
/// <param name="Namespace">The pod namespace, when readable.</param>
/// <param name="Problem">
/// What is wrong, in terms an operator can act on. Null when the evidence looks usable.
/// </param>
public sealed record NodeEvidenceReport(
    // Serialised as a NAME, not an ordinal. `"platform":0` in a machine-readable report tells a
    // consumer nothing and silently changes meaning if a member is ever inserted into the enum.
    [property: System.Text.Json.Serialization.JsonConverter(
        typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    WorkloadPlatform Platform,
    string? NodeType,
    string? TokenPath,
    bool TokenPresent,
    string? Namespace,
    string? Problem,
    /// <summary>Spec 064 — where <paramref name="TokenPath"/> came from (<c>explicit</c>, <c>environment</c>, <c>project-file</c>, <c>conventional</c>, <c>kubelet-default</c>).</summary>
    string? TokenPathSource = null,
    /// <summary>Spec 064 — the token's <c>aud</c>, decoded locally. Not verified: Bella verifies at attestation.</summary>
    IReadOnlyList<string>? Audiences = null,
    /// <summary>Spec 064 — the token's <c>iss</c>, decoded locally, unverified.</summary>
    string? Issuer = null,
    /// <summary>Spec 064 — the token's <c>exp</c>, decoded locally, unverified.</summary>
    DateTimeOffset? ExpiresAt = null,
    /// <summary>Always true: nothing here was verified, and the output says so rather than implying otherwise.</summary>
    bool Unverified = true);

/// <summary>Reads local node-attestation evidence without contacting anything.</summary>
public static class NodeEvidence
{
    /// <summary>Inspects the local environment and reports what evidence is available.</summary>
    /// <param name="location">
    /// The token to inspect, resolved by the caller (the agent resolves once, from its flag). Null resolves it here.
    /// </param>
    /// <param name="fileExists">Injected for testing; defaults to the real filesystem.</param>
    /// <param name="readFile">Injected for testing; defaults to the real filesystem.</param>
    /// <param name="platform">Injected for testing; defaults to real detection.</param>
    /// <param name="getEnvironmentVariable">Injected for testing; defaults to the real environment.</param>
    public static NodeEvidenceReport Inspect(
        NodeTokenLocation? location = null,
        Func<string, bool>? fileExists = null,
        Func<string, string>? readFile = null,
        WorkloadPlatform? platform = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        var exists = fileExists ?? File.Exists;
        var read = readFile ?? File.ReadAllText;
        var env = getEnvironmentVariable ?? System.Environment.GetEnvironmentVariable;
        var resolved = location ?? NodeTokenPath.Resolve(getEnvironmentVariable: env, fileExists: exists);
        var detected = platform ?? WorkloadIdentityService.DetectPlatform(env, exists, resolved);
        // An operator who names a node token path is on Kubernetes by their own statement, whatever detection says.
        if (detected == WorkloadPlatform.None && resolved.IsExplicit)
            detected = WorkloadPlatform.Kubernetes;

        if (detected != WorkloadPlatform.Kubernetes)
        {
            // Not an error. A workload on a VM or a laptop attests on workload selectors alone, and
            // saying "no node evidence" plainly is more useful than implying something is broken.
            return new NodeEvidenceReport(
                detected,
                NodeType: null,
                TokenPath: null,
                TokenPresent: false,
                Namespace: null,
                Problem: null);
        }

        var path = resolved.Path;
        var source = resolved.SourceName;

        if (!exists(path))
        {
            if (resolved.IsExplicit)
            {
                // FR-013: never fall back. Presenting the kubelet token instead would pass every check until
                // audience enforcement is switched on, and then fail everywhere at once.
                return new NodeEvidenceReport(
                    detected, "k8s", path, TokenPresent: false, Namespace: null,
                    Problem: $"No token at {path}, which was configured by {DescribeSource(resolved)}. "
                        + "The agent will not fall back to another token; mount the projected token there, or fix the setting.",
                    TokenPathSource: source);
            }

            // Real and common: `automountServiceAccountToken: false` on the pod or service account.
            // Naming the setting is the difference between a fix and a support ticket.
            return new NodeEvidenceReport(
                detected, "k8s", path, TokenPresent: false, Namespace: null,
                Problem: $"No service-account token at {path}. The pod may have "
                    + "automountServiceAccountToken disabled, or use a projected volume at another path "
                    + $"(set {NodeTokenPath.EnvironmentVariable}, or mount it at {NodeTokenPath.Conventional}).",
                TokenPathSource: source);
        }

        string token;
        try
        {
            token = read(path);
        }
        catch (Exception ex)
        {
            return new NodeEvidenceReport(
                detected, "k8s", path, TokenPresent: false, Namespace: null,
                Problem: $"The service-account token at {path} could not be read: {ex.Message}",
                TokenPathSource: source);
        }

        // A present-but-empty token is the nastiest case: attestation fails with a signature error,
        // and the operator goes looking at the cluster's OIDC configuration rather than at the mount.
        if (string.IsNullOrWhiteSpace(token))
        {
            return new NodeEvidenceReport(
                detected, "k8s", path, TokenPresent: false, Namespace: ReadNamespace(exists, read),
                Problem: $"The service-account token at {path} is empty. Attestation will be refused "
                    + "for a signature failure, which reads like a cluster-trust problem but is not.",
                TokenPathSource: source);
        }

        // Spec 064 (FR-015) — what the token says about itself, decoded locally and NOT verified. Only the claims are
        // kept; the token, and its signature segment in particular, never enter the report.
        if (!TryDecode(token.Trim(), out var audiences, out var issuer, out var expiresAt))
        {
            return new NodeEvidenceReport(
                detected, "k8s", path, TokenPresent: false, Namespace: ReadNamespace(exists, read),
                Problem: $"The file at {path} is not a JWT, so it cannot be a service-account token. "
                    + "Attestation will be refused for a signature failure.",
                TokenPathSource: source);
        }

        return new NodeEvidenceReport(
            detected, "k8s", path, TokenPresent: true, Namespace: ReadNamespace(exists, read),
            Problem: null,
            TokenPathSource: source,
            Audiences: audiences,
            Issuer: issuer,
            ExpiresAt: expiresAt);
    }

    private static string DescribeSource(NodeTokenLocation location) => location.Source switch
    {
        NodeTokenPathSource.Explicit => "--node-token-path",
        NodeTokenPathSource.Environment => NodeTokenPath.EnvironmentVariable,
        NodeTokenPathSource.ProjectFile => $"{NodeTokenPath.ProjectFileKey} in .bella",
        _ => location.SourceName,
    };

    private static bool TryDecode(string token, out IReadOnlyList<string> audiences, out string? issuer, out DateTimeOffset? expiresAt)
    {
        audiences = [];
        issuer = null;
        expiresAt = null;
        if (token.Split('.').Length != 3)
            return false;
        try
        {
            var claims = AuthService.DecodeJwtPayload(token);
            if (claims.Count == 0)
                return false;
            if (claims.TryGetValue("aud", out var aud))
            {
                audiences = aud.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => [aud.GetString()!],
                    System.Text.Json.JsonValueKind.Array => aud.EnumerateArray()
                        .Where(a => a.ValueKind == System.Text.Json.JsonValueKind.String)
                        .Select(a => a.GetString()!)
                        .ToList(),
                    _ => [],
                };
            }
            if (claims.TryGetValue("iss", out var iss) && iss.ValueKind == System.Text.Json.JsonValueKind.String)
                issuer = iss.GetString();
            if (claims.TryGetValue("exp", out var exp) && exp.TryGetInt64(out var seconds))
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadNamespace(Func<string, bool> exists, Func<string, string> read)
    {
        var path = NodeEvidencePaths.KubernetesNamespace;
        if (!exists(path))
        {
            return null;
        }

        try
        {
            var value = read(path).Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch
        {
            // Context, not evidence. Losing it costs a line of display and nothing else.
            return null;
        }
    }
}
