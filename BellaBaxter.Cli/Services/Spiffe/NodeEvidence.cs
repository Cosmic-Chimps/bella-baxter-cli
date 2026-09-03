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

/// <summary>Where a Kubernetes service-account token is projected into a pod.</summary>
public static class NodeEvidencePaths
{
    /// <summary>The standard projection path. Present in every pod unless explicitly disabled.</summary>
    public const string KubernetesServiceAccountToken =
        "/var/run/secrets/kubernetes.io/serviceaccount/token";

    /// <summary>The namespace file beside it — useful context, not evidence.</summary>
    public const string KubernetesNamespace =
        "/var/run/secrets/kubernetes.io/serviceaccount/namespace";
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
    string? Problem);

/// <summary>Reads local node-attestation evidence without contacting anything.</summary>
public static class NodeEvidence
{
    /// <summary>Inspects the local environment and reports what evidence is available.</summary>
    /// <param name="fileExists">Injected for testing; defaults to the real filesystem.</param>
    /// <param name="readFile">Injected for testing; defaults to the real filesystem.</param>
    /// <param name="platform">Injected for testing; defaults to real detection.</param>
    public static NodeEvidenceReport Inspect(
        Func<string, bool>? fileExists = null,
        Func<string, string>? readFile = null,
        WorkloadPlatform? platform = null)
    {
        var exists = fileExists ?? File.Exists;
        var read = readFile ?? File.ReadAllText;
        var detected = platform ?? WorkloadIdentityService.DetectPlatform();

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

        var path = NodeEvidencePaths.KubernetesServiceAccountToken;

        if (!exists(path))
        {
            // Real and common: `automountServiceAccountToken: false` on the pod or service account.
            // Naming the setting is the difference between a fix and a support ticket.
            return new NodeEvidenceReport(
                detected, "k8s", path, TokenPresent: false, Namespace: null,
                Problem: $"No service-account token at {path}. The pod may have "
                    + "automountServiceAccountToken disabled, or use a projected volume at another path.");
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
                Problem: $"The service-account token at {path} could not be read: {ex.Message}");
        }

        // A present-but-empty token is the nastiest case: attestation fails with a signature error,
        // and the operator goes looking at the cluster's OIDC configuration rather than at the mount.
        if (string.IsNullOrWhiteSpace(token))
        {
            return new NodeEvidenceReport(
                detected, "k8s", path, TokenPresent: false, Namespace: ReadNamespace(exists, read),
                Problem: $"The service-account token at {path} is empty. Attestation will be refused "
                    + "for a signature failure, which reads like a cluster-trust problem but is not.");
        }

        return new NodeEvidenceReport(
            detected, "k8s", path, TokenPresent: true, Namespace: ReadNamespace(exists, read),
            Problem: null);
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
