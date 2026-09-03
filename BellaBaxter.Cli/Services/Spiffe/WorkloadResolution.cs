namespace BellaCli.Services.Spiffe;

// Spec 001 T036 (US4) — turning a workload NAME into the id the API revokes by.
//
// `bella spiffe revoke --name billing-service` is the operator-facing shape (contracts/cli.md), but
// the endpoint deletes by id. That translation is the whole risk in the command: revocation kills
// live credentials, so resolving the wrong row is not an inconvenience, it is an outage for a
// workload nobody meant to touch.
//
// Kept pure and separate from the command for one reason: the rule that matters is what happens when
// the answer is NOT exactly one, and that rule is untestable through a Kiota client. A first-match
// `.First(w => w.Name == name)` would compile, read fine, and quietly pick a row — the same
// first-match-over-an-implicit-list pattern spec 021 spent a whole feature removing from the API.
//
// An already-revoked workload resolves rather than being filtered out: revoking it again is a no-op
// the caller should be TOLD about, and hiding it would report "no such workload" for something the
// operator can see in the list.

/// <summary>What a name resolved to.</summary>
public enum WorkloadResolutionKind
{
    /// <summary>Exactly one live workload carries the name.</summary>
    Resolved,

    /// <summary>Nothing in this environment carries the name.</summary>
    NotFound,

    /// <summary>The only match is already revoked — nothing left to do.</summary>
    AlreadyRevoked,

    /// <summary>Two or more carry the name. Refused, never guessed.</summary>
    Ambiguous,
}

/// <summary>One candidate, reduced to the fields the decision needs.</summary>
/// <param name="Id">The workload's id, or null when the API did not return one.</param>
/// <param name="Name">The registered name.</param>
/// <param name="SpiffeId">Shown in a refusal so an operator can tell two same-named rows apart.</param>
/// <param name="IsRevoked">Whether it is already revoked.</param>
public sealed record WorkloadCandidate(Guid? Id, string? Name, string? SpiffeId, bool IsRevoked);

/// <summary>The outcome, with everything a message needs to explain itself.</summary>
public sealed record WorkloadResolution(
    WorkloadResolutionKind Kind,
    Guid Id,
    string? SpiffeId,
    IReadOnlyList<WorkloadCandidate> Candidates)
{
    /// <summary>An operator-facing explanation, or null when resolution succeeded.</summary>
    public string? Problem => Kind switch
    {
        WorkloadResolutionKind.Resolved => null,
        WorkloadResolutionKind.NotFound =>
            "No workload identity by that name in this environment. Run 'bella spiffe list' to see "
            + "what is registered here — the name is per environment, so check your context too.",
        WorkloadResolutionKind.AlreadyRevoked =>
            "That workload identity is already revoked. Its leases were terminated when it was revoked; "
            + "nothing further to do.",
        WorkloadResolutionKind.Ambiguous =>
            "More than one workload identity carries that name in this environment, so revoking by name "
            + "would be a guess. Refusing. Candidates: "
            + string.Join(", ", Candidates.Select(c =>
                $"{c.Id?.ToString() ?? "(no id)"}{(c.IsRevoked ? " (revoked)" : string.Empty)}")),
        _ => "Could not resolve that workload identity.",
    };
}

/// <summary>Resolves a workload name against the environment's registered identities.</summary>
public static class WorkloadResolver
{
    /// <summary>Finds the one workload a name refers to, or explains why it cannot.</summary>
    /// <param name="name">The name the operator typed.</param>
    /// <param name="candidates">Everything the environment has registered.</param>
    public static WorkloadResolution Resolve(string name, IEnumerable<WorkloadCandidate>? candidates)
    {
        var wanted = name?.Trim() ?? string.Empty;
        var all = (candidates ?? []).ToList();

        // Ordinal: a workload name is an identifier, and a culture-sensitive comparison on an
        // identifier is how two values that look identical stop matching on someone else's machine.
        var matches = all
            .Where(c => string.Equals(c.Name?.Trim(), wanted, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0)
        {
            return new WorkloadResolution(WorkloadResolutionKind.NotFound, Guid.Empty, null, []);
        }

        if (matches.Count > 1)
        {
            return new WorkloadResolution(WorkloadResolutionKind.Ambiguous, Guid.Empty, null, matches);
        }

        var match = matches[0];

        // A row the API returned without an id cannot be acted on, and must not be reported as
        // "not found" — that would send the operator looking for a registration that is right there.
        if (match.Id is null || match.Id == Guid.Empty)
        {
            return new WorkloadResolution(WorkloadResolutionKind.NotFound, Guid.Empty, null, matches);
        }

        return match.IsRevoked
            ? new WorkloadResolution(
                WorkloadResolutionKind.AlreadyRevoked, match.Id.Value, match.SpiffeId, matches)
            : new WorkloadResolution(
                WorkloadResolutionKind.Resolved, match.Id.Value, match.SpiffeId, matches);
    }
}
