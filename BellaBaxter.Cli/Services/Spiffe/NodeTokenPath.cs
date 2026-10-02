namespace BellaCli.Services.Spiffe;

// Spec 064 — which Kubernetes service-account token the agent presents as node evidence.
//
// WHY THIS IS CONFIGURABLE NOW. The kubelet mounts a default token at a fixed path, and its audience is the
// cluster's own API server — so any copy of it replays as node evidence against Bella. An environment that
// enforces token audience refuses it; the agent must instead present a PROJECTED token requested for Bella (a
// `serviceAccountToken` volume with `audience: bella-spiffe`). The rollout depends on the agent being able to do
// that BEFORE an operator switches enforcement on.
//
// PRECEDENCE: the operator's explicit word first (flag → BELLA_NODE_TOKEN_PATH → `.bella` node_token_path), then the
// documented conventional path WHEN IT EXISTS — so the documented manifest works with no flag at all — and only
// then the kubelet default. An explicitly configured path is returned even when it is missing: the caller REFUSES
// rather than falling back (FR-013), because a fallback to the kubelet token passes every check until enforcement
// is switched on and then fails everywhere at once.
//
// THE IMPLICIT KEYLESS FLOWS DO NOT READ `.bella` (`ResolveForKeyless`). `bella run`/`exec`/`sdk run`/`auth oidc` send
// the token they find to the server the `.bella` `url` names, and a `.bella` arrives with any repository you clone. Had
// it also chosen the token path, a committed file could make those commands read an arbitrary local file and post its
// contents to an address of its choosing. Only the agent and `whoami` — which an operator runs deliberately, and
// configures per pod — honour `node_token_path`; the keyless flows take the flag-less precedence without it.
//
// THE ONE AUTHOR of the kubelet path literal. Platform detection, the agent, `whoami` and the keyless exchange
// all resolve through here; `KubeletPathHasOneAuthorTests` fails on a second copy, because two copies are how a
// detector comes to disagree with the reader about which file is the token.

/// <summary>Where a <see cref="NodeTokenLocation"/> came from.</summary>
public enum NodeTokenPathSource
{
    /// <summary><c>--node-token-path</c>.</summary>
    Explicit,

    /// <summary><c>BELLA_NODE_TOKEN_PATH</c>.</summary>
    Environment,

    /// <summary><c>node_token_path</c> in the nearest <c>.bella</c>.</summary>
    ProjectFile,

    /// <summary>The documented projected-token path, because it exists.</summary>
    Conventional,

    /// <summary>The kubelet's default token — the one an audience-enforcing environment refuses.</summary>
    KubeletDefault,
}

/// <summary>The resolved token path and where it came from.</summary>
public sealed record NodeTokenLocation(string Path, NodeTokenPathSource Source)
{
    /// <summary>
    /// True when the operator named the path. A missing explicit path is a refusal, never a reason to fall back.
    /// </summary>
    public bool IsExplicit => Source is NodeTokenPathSource.Explicit or NodeTokenPathSource.Environment or NodeTokenPathSource.ProjectFile;

    /// <summary>Stable display name, for output and JSON.</summary>
    public string SourceName => Source switch
    {
        NodeTokenPathSource.Explicit => "explicit",
        NodeTokenPathSource.Environment => "environment",
        NodeTokenPathSource.ProjectFile => "project-file",
        NodeTokenPathSource.Conventional => "conventional",
        _ => "kubelet-default",
    };
}

/// <summary>Resolves the Kubernetes token to present (spec 064 FR-011).</summary>
public static class NodeTokenPath
{
    /// <summary>Environment variable naming the token path.</summary>
    public const string EnvironmentVariable = "BELLA_NODE_TOKEN_PATH";

    /// <summary><c>.bella</c> key naming the token path.</summary>
    public const string ProjectFileKey = "node_token_path";

    /// <summary>
    /// Where the documented manifest projects a token requested for Bella. Preferred automatically when present.
    /// </summary>
    public const string Conventional = "/var/run/secrets/bella/token";

    /// <summary>The kubelet's default token. Its audience is the cluster's own API server.</summary>
    public const string KubeletDefault = "/var/run/secrets/kubernetes.io/serviceaccount/token";

    /// <summary>The namespace file the kubelet mounts beside its default token.</summary>
    public const string KubeletNamespace = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";

    /// <summary>Resolves the token path, in precedence order.</summary>
    /// <param name="explicitPath">A <c>--node-token-path</c> value.</param>
    /// <param name="getEnvironmentVariable">Injected for testing.</param>
    /// <param name="readProjectSetting">Injected for testing; reads a key from the nearest <c>.bella</c>.</param>
    /// <param name="fileExists">Injected for testing.</param>
    public static NodeTokenLocation Resolve(
        string? explicitPath = null,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string, string?>? readProjectSetting = null,
        Func<string, bool>? fileExists = null) =>
        ResolveCore(explicitPath, getEnvironmentVariable, readProjectSetting, fileExists, honourProjectFile: true);

    /// <summary>
    /// Resolves the token for the implicit keyless flows: the same precedence WITHOUT <c>.bella node_token_path</c>,
    /// because a repository's <c>.bella</c> must not choose which local file is sent to the server it names.
    /// </summary>
    /// <param name="getEnvironmentVariable">Injected for testing.</param>
    /// <param name="readProjectSetting">Injected for testing; proven to be ignored.</param>
    /// <param name="fileExists">Injected for testing.</param>
    public static NodeTokenLocation ResolveForKeyless(
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string, string?>? readProjectSetting = null,
        Func<string, bool>? fileExists = null) =>
        ResolveCore(null, getEnvironmentVariable, readProjectSetting, fileExists, honourProjectFile: false);

    private static NodeTokenLocation ResolveCore(
        string? explicitPath,
        Func<string, string?>? getEnvironmentVariable,
        Func<string, string?>? readProjectSetting,
        Func<string, bool>? fileExists,
        bool honourProjectFile)
    {
        var env = getEnvironmentVariable ?? System.Environment.GetEnvironmentVariable;
        var project = readProjectSetting ?? (key => KeyContextService.ReadBellaSetting(key));
        var exists = fileExists ?? File.Exists;

        if (!string.IsNullOrWhiteSpace(explicitPath))
            return new NodeTokenLocation(explicitPath.Trim(), NodeTokenPathSource.Explicit);

        var fromEnv = env(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return new NodeTokenLocation(fromEnv.Trim(), NodeTokenPathSource.Environment);

        var fromProject = honourProjectFile ? project(ProjectFileKey) : null;
        if (!string.IsNullOrWhiteSpace(fromProject))
            return new NodeTokenLocation(fromProject.Trim(), NodeTokenPathSource.ProjectFile);

        if (exists(Conventional))
            return new NodeTokenLocation(Conventional, NodeTokenPathSource.Conventional);

        return new NodeTokenLocation(KubeletDefault, NodeTokenPathSource.KubeletDefault);
    }
}
