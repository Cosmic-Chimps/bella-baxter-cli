using System.Text.RegularExpressions;

namespace BellaCli.Services.Spiffe;

// Spec 081 (FR-018, FR-020, FR-022) — which Nomad workload identity the agent presents as node evidence.
//
// A task declares a NAMED identity (`identity { name = "bella" aud = [...] file = true env = true }`). Nomad then writes
// the token to `$NOMAD_SECRETS_DIR/nomad_<name>.jwt` and, with `env = true`, also exposes it as `NOMAD_TOKEN_<name>`.
// Measured on Nomad 2.0.7 (research R1): the file is refreshed when Nomad renews the identity, the variable is NOT unless
// the identity's `change_mode` is `restart`. That is why the FILE comes before the variable.
//
// PRECEDENCE: the operator's explicit word first (flag, then BELLA_NOMAD_IDENTITY_PATH), then the named identity's file
// in the task's secrets directory WHEN IT EXISTS, then the named identity's variable. An explicitly configured path is
// returned even when it is missing: the caller REFUSES rather than falling back (FR-020), because falling back would
// quietly present a different identity than the operator named.
//
// WHAT IS NEVER READ.
//   * `NOMAD_TOKEN` (no suffix). In the common deployment it is the ACL token — a secret UUID, not evidence. It is not
//     even looked up; `NomadEvidence` additionally sends only JWT-shaped content (FR-021).
//   * `.bella`. There is deliberately no project-setting parameter here (FR-022): a `.bella` arrives with every cloned
//     repository and names the server a credential is posted to, so letting it choose which local file is sent would let
//     a committed file exfiltrate any readable file. The keyless flows (`run`, `exec`, `sdk run`, `auth oidc`) never
//     resolve a Nomad path at all; `NomadIdentityPathHasOneAuthorTests` fails if they start to.
//
// THE ONE AUTHOR of the identity file-name pattern, as `NodeTokenPath` is of the kubelet path (R9).

/// <summary>Where a <see cref="NomadIdentityLocation"/> came from.</summary>
public enum NomadIdentitySource
{
    /// <summary><c>--nomad-identity-path</c>.</summary>
    Explicit,

    /// <summary><c>BELLA_NOMAD_IDENTITY_PATH</c>.</summary>
    Environment,

    /// <summary>The named identity's file in <c>$NOMAD_SECRETS_DIR</c>, because it exists.</summary>
    SecretsFile,

    /// <summary>The named identity's environment variable <c>NOMAD_TOKEN_&lt;name&gt;</c>, because it is set.</summary>
    TokenVariable,

    /// <summary>Nothing was found (or the identity name was unusable).</summary>
    NotFound,
}

/// <summary>The resolved Nomad identity source. Carries where to read, never the token.</summary>
/// <param name="Source">Where the choice came from.</param>
/// <param name="Name">The identity name in force.</param>
/// <param name="Path">The file to read, for the file-based sources.</param>
/// <param name="Variable">The environment variable to read, for <see cref="NomadIdentitySource.TokenVariable"/>.</param>
/// <param name="Problem">Why nothing could be chosen, when the name itself was refused.</param>
public sealed record NomadIdentityLocation(NomadIdentitySource Source, string Name, string? Path, string? Variable, string? Problem)
{
    /// <summary>True when the operator named the path. A missing explicit path is a refusal, never a fallback.</summary>
    public bool IsExplicit => Source is NomadIdentitySource.Explicit or NomadIdentitySource.Environment;

    /// <summary>Stable display name, for output and JSON.</summary>
    public string SourceName => Source switch
    {
        NomadIdentitySource.Explicit => "explicit",
        NomadIdentitySource.Environment => "environment",
        NomadIdentitySource.SecretsFile => "secrets-file",
        NomadIdentitySource.TokenVariable => "token-variable",
        _ => "not-found",
    };
}

/// <summary>Resolves the Nomad workload identity to present (spec 081 FR-018).</summary>
public static class NomadIdentityPath
{
    /// <summary>Environment variable naming the identity path.</summary>
    public const string PathEnvironmentVariable = "BELLA_NOMAD_IDENTITY_PATH";

    /// <summary>Environment variable naming the identity.</summary>
    public const string NameEnvironmentVariable = "BELLA_NOMAD_IDENTITY";

    /// <summary>The identity name used when none is configured.</summary>
    public const string DefaultName = "bella";

    /// <summary>The task's secrets directory, which Nomad sets and writes named identities into.</summary>
    public const string SecretsDirectoryVariable = "NOMAD_SECRETS_DIR";

    private const string TokenVariablePrefix = "NOMAD_TOKEN_";

    // A name becomes part of a file name and an environment variable name, so it may not carry a separator.
    private static readonly Regex UsableName = new("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);

    /// <summary>The file name Nomad gives a named identity in the secrets directory.</summary>
    public static string FileNameFor(string name) => $"nomad_{name}.jwt";

    /// <summary>Resolves the identity to read, in precedence order.</summary>
    /// <param name="explicitPath">A <c>--nomad-identity-path</c> value.</param>
    /// <param name="identityName">A <c>--nomad-identity</c> value; the environment, then <c>bella</c>, when null.</param>
    /// <param name="getEnvironmentVariable">Injected for testing.</param>
    /// <param name="fileExists">Injected for testing.</param>
    public static NomadIdentityLocation Resolve(
        string? explicitPath = null,
        string? identityName = null,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string, bool>? fileExists = null)
    {
        var env = getEnvironmentVariable ?? System.Environment.GetEnvironmentVariable;
        var exists = fileExists ?? File.Exists;

        var name = FirstNonBlank(identityName, env(NameEnvironmentVariable)) ?? DefaultName;

        if (!string.IsNullOrWhiteSpace(explicitPath))
            return new NomadIdentityLocation(NomadIdentitySource.Explicit, name, explicitPath.Trim(), null, null);

        var fromEnv = env(PathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return new NomadIdentityLocation(NomadIdentitySource.Environment, name, fromEnv.Trim(), null, null);

        if (!UsableName.IsMatch(name))
        {
            return new NomadIdentityLocation(
                NomadIdentitySource.NotFound, name, null, null,
                $"The Nomad identity name '{name}' cannot be used: it may contain only letters, digits, '_' and '-'.");
        }

        var secretsDirectory = env(SecretsDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(secretsDirectory))
        {
            var file = System.IO.Path.Combine(secretsDirectory.Trim(), FileNameFor(name));
            if (exists(file))
                return new NomadIdentityLocation(NomadIdentitySource.SecretsFile, name, file, null, null);
        }

        var variable = TokenVariablePrefix + name;
        if (!string.IsNullOrWhiteSpace(env(variable)))
            return new NomadIdentityLocation(NomadIdentitySource.TokenVariable, name, null, variable, null);

        return new NomadIdentityLocation(NomadIdentitySource.NotFound, name, null, null, null);
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
