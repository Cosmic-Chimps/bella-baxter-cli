namespace BellaCli.Services.Spiffe;

// Spec 081 (FR-019, FR-021, FR-023) — what the agent can prove about the Nomad task it runs in, read locally.
//
// Like `NodeEvidence` it makes no network call and verifies nothing: it reads the identity `NomadIdentityPath` chose,
// decodes the claims for DISPLAY, and says plainly that they are unverified — Bella verifies at attestation.
//
// TWO RULES THAT ARE THE POINT OF THIS FILE.
//   * Only JWT-shaped content is ever presented (FR-021). `ReadPresentable` returns a token only when it has three
//     segments and a payload that decodes; a Nomad ACL token (a UUID secret) never does, whichever source it arrives
//     through, so it cannot be posted to the server by mistake.
//   * The token never leaves this file except through `ReadPresentable`, which is the agent's attestation reader.
//     The report carries claims, never the token, its payload segment or its signature.
//
// RE-READ ON EVERY CALL (FR-019): Nomad renews an identity at about half its TTL and rewrites the file, so nothing is
// cached here. A reader that captured the token once would present an expired one after the first renewal.

/// <summary>What the agent found locally for Nomad, for display.</summary>
/// <param name="NodeType">Always <c>nomad</c>.</param>
/// <param name="Source">Where the identity came from (<c>explicit</c>, <c>environment</c>, <c>secrets-file</c>, <c>token-variable</c>, <c>not-found</c>).</param>
/// <param name="Path">The file read, for the file-based sources.</param>
/// <param name="Variable">The environment variable read (its NAME, never its value), for the variable source.</param>
/// <param name="IdentityName">The named identity looked for.</param>
/// <param name="TokenPresent">Whether a presentable JWT was found.</param>
/// <param name="Problem">What is wrong, in terms an operator can act on. Null when the evidence looks usable.</param>
/// <param name="Issuer">The token's <c>iss</c>, decoded locally, unverified.</param>
/// <param name="Audiences">The token's <c>aud</c>, decoded locally, unverified.</param>
/// <param name="ExpiresAt">The token's <c>exp</c>, decoded locally, unverified.</param>
/// <param name="IssuedAt">The token's <c>iat</c>, decoded locally, unverified.</param>
/// <param name="LifetimeSeconds"><c>exp</c> minus <c>iat</c>: the identity's TTL, when both are present.</param>
/// <param name="NomadClaims">The token's <c>nomad_*</c> claims, decoded locally, unverified.</param>
/// <param name="Unverified">Always true: nothing here was verified, and the output says so.</param>
public sealed record NomadEvidenceReport(
    string NodeType,
    string Source,
    string? Path,
    string? Variable,
    string IdentityName,
    bool TokenPresent,
    string? Problem,
    string? Issuer = null,
    IReadOnlyList<string>? Audiences = null,
    DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? IssuedAt = null,
    long? LifetimeSeconds = null,
    IReadOnlyDictionary<string, string>? NomadClaims = null,
    bool Unverified = true);

/// <summary>Reads local Nomad node-attestation evidence without contacting anything.</summary>
public static class NomadEvidence
{
    /// <summary>The node type the agent sends to attestation.</summary>
    public const string NodeType = "nomad";

    /// <summary>Inspects the identity and reports what the agent would present.</summary>
    /// <param name="location">The identity to inspect, resolved by the caller (the agent resolves once).</param>
    /// <param name="fileExists">Injected for testing; defaults to the real filesystem.</param>
    /// <param name="readFile">Injected for testing; defaults to the real filesystem.</param>
    /// <param name="getEnvironmentVariable">Injected for testing; defaults to the real environment.</param>
    public static NomadEvidenceReport Inspect(
        NomadIdentityLocation location,
        Func<string, bool>? fileExists = null,
        Func<string, string>? readFile = null,
        Func<string, string?>? getEnvironmentVariable = null) =>
        Load(location, fileExists, readFile, getEnvironmentVariable).Report;

    /// <summary>
    /// The token to present NOW, or null when there is none worth sending. Reads the source again on every call.
    /// </summary>
    public static string? ReadPresentable(
        NomadIdentityLocation location,
        Func<string, bool>? fileExists = null,
        Func<string, string>? readFile = null,
        Func<string, string?>? getEnvironmentVariable = null) =>
        Load(location, fileExists, readFile, getEnvironmentVariable).Token;

    private static (NomadEvidenceReport Report, string? Token) Load(
        NomadIdentityLocation location,
        Func<string, bool>? fileExists,
        Func<string, string>? readFile,
        Func<string, string?>? getEnvironmentVariable)
    {
        var exists = fileExists ?? File.Exists;
        var read = readFile ?? File.ReadAllText;
        var env = getEnvironmentVariable ?? System.Environment.GetEnvironmentVariable;

        NomadEvidenceReport Fail(string problem) => new(
            NodeType, location.SourceName, location.Path, location.Variable, location.Name,
            TokenPresent: false, Problem: problem);

        if (location.Source == NomadIdentitySource.NotFound)
        {
            return (Fail(location.Problem
                ?? $"No Nomad identity '{location.Name}' was found. Give the task an identity block named '{location.Name}' with "
                + $"file = true (read from {NomadIdentityPath.SecretsDirectoryVariable}) or env = true, or point "
                + $"{NomadIdentityPath.PathEnvironmentVariable} at the token file."), null);
        }

        string content;
        string where;
        try
        {
            if (location.Variable is { } variable)
            {
                content = env(variable) ?? string.Empty;
                where = $"The environment variable {variable}";
            }
            else
            {
                var path = location.Path!;
                where = $"The Nomad identity at {path}";
                if (!exists(path))
                {
                    return (Fail(location.IsExplicit
                        // FR-020: never fall back. Presenting another identity instead would pass until the operator
                        // looks at which identity the workload is registered against, and then fail confusingly.
                        ? $"No Nomad identity at {path}, which was configured by {DescribeSource(location)}. "
                            + "The agent will not fall back to another source; write the identity there, or fix the setting."
                        : $"The Nomad identity at {path} is gone."), null);
                }

                content = read(path);
            }
        }
        catch (Exception ex)
        {
            return (Fail($"The Nomad identity for '{location.Name}' could not be read: {ex.Message}"), null);
        }

        var token = content.Trim();
        if (token.Length == 0)
            return (Fail($"{where} is empty."), null);

        // FR-021: only a JWT is evidence. A Nomad ACL token is a UUID secret and is refused here, never sent.
        if (!UnverifiedJwtClaims.TryRead(token, out var claims))
        {
            return (Fail($"{where} is not a JWT, so it cannot be a Nomad workload identity. "
                + "A Nomad ACL token is not evidence and is never sent."), null);
        }

        var issuedAt = UnverifiedJwtClaims.Time(claims, "iat");
        var expiresAt = UnverifiedJwtClaims.Time(claims, "exp");
        var nomadClaims = claims
            .Where(c => c.Key.StartsWith("nomad_", StringComparison.Ordinal))
            .ToDictionary(c => c.Key, c => c.Value.ToString(), StringComparer.Ordinal);

        var report = new NomadEvidenceReport(
            NodeType, location.SourceName, location.Path, location.Variable, location.Name,
            TokenPresent: true,
            Problem: null,
            Issuer: UnverifiedJwtClaims.Text(claims, "iss"),
            Audiences: UnverifiedJwtClaims.Audiences(claims),
            ExpiresAt: expiresAt,
            IssuedAt: issuedAt,
            LifetimeSeconds: issuedAt is { } iat && expiresAt is { } exp ? (long)(exp - iat).TotalSeconds : null,
            NomadClaims: nomadClaims);
        return (report, token);
    }

    /// <summary>How an operator named a configured source, for the refusal text.</summary>
    internal static string DescribeSource(NomadIdentityLocation location) => location.Source switch
    {
        NomadIdentitySource.Explicit => "--nomad-identity-path",
        NomadIdentitySource.Environment => NomadIdentityPath.PathEnvironmentVariable,
        _ => location.SourceName,
    };
}
