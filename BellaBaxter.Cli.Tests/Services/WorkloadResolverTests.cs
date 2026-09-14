using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 001 T036 (US4) — resolving <c>--name</c> to the id revocation acts on.
/// </summary>
/// <remarks>
/// <para>This is the whole risk in <c>bella spiffe revoke</c>. Revocation cascades: every outstanding
/// lease the workload holds stops working before the call returns. Resolving the wrong row is not an
/// inconvenience, it is an outage for a workload nobody meant to touch — so the interesting cases are
/// all the ones where the answer is NOT exactly one.</para>
///
/// <para>A first-match <c>.First(w =&gt; w.Name == name)</c> would compile, read fine, and quietly pick
/// a row: the same first-match-over-an-implicit-list pattern spec 021 spent a feature removing from the
/// API. Nothing but a test makes the difference visible.</para>
/// </remarks>
public class WorkloadResolverTests
{
    private static WorkloadCandidate Live(string name, Guid? id = null, string? spiffeId = null) =>
        new(id ?? Guid.NewGuid(), name, spiffeId ?? $"spiffe://t/p/e/{name}", IsRevoked: false);

    private static WorkloadCandidate Revoked(string name, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), name, $"spiffe://t/p/e/{name}", IsRevoked: true);

    [Fact]
    public void One_match_resolves_to_its_id()
    {
        var id = Guid.NewGuid();
        var result = WorkloadResolver.Resolve("billing-service",
            [Live("reporting-service"), Live("billing-service", id), Live("audit-service")]);

        Assert.Equal(WorkloadResolutionKind.Resolved, result.Kind);
        Assert.Equal(id, result.Id);
        Assert.Null(result.Problem);
    }

    [Fact]
    public void TWO_matches_REFUSE_rather_than_pick_one()
    {
        // The case the whole class exists for. Two rows, one name: revoking either would be a guess,
        // and the wrong guess cuts the credentials of a running workload.
        var result = WorkloadResolver.Resolve("billing-service",
            [Live("billing-service"), Live("billing-service")]);

        Assert.Equal(WorkloadResolutionKind.Ambiguous, result.Kind);
        Assert.Equal(Guid.Empty, result.Id);

        // And the refusal names the candidates, so the operator can act on it rather than being stuck.
        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains("Refusing", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ambiguous_refusal_lists_every_candidates_id()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var result = WorkloadResolver.Resolve("dup", [Live("dup", a), Revoked("dup", b)]);

        Assert.Equal(WorkloadResolutionKind.Ambiguous, result.Kind);
        Assert.Contains(a.ToString(), result.Problem!, StringComparison.Ordinal);
        Assert.Contains(b.ToString(), result.Problem!, StringComparison.Ordinal);
        Assert.Contains("revoked", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void No_match_says_the_name_is_per_environment()
    {
        // The commonest real cause is the wrong context, not a wrong name. "Not found" alone would send
        // the operator hunting for a typo in a name that is spelled correctly somewhere else.
        var result = WorkloadResolver.Resolve("billing-service", [Live("reporting-service")]);

        Assert.Equal(WorkloadResolutionKind.NotFound, result.Kind);
        Assert.Contains("per environment", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_already_revoked_match_is_reported_as_such_not_as_missing()
    {
        // Distinct from NotFound on purpose: the operator can see the row in `bella spiffe list`, and
        // being told it does not exist would contradict their own screen. It also lets the command
        // exit 0, so re-running a playbook does not fail on work it already did.
        var result = WorkloadResolver.Resolve("billing-service", [Revoked("billing-service")]);

        Assert.Equal(WorkloadResolutionKind.AlreadyRevoked, result.Kind);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Contains("already revoked", result.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Surrounding_whitespace_on_either_side_does_not_prevent_a_match()
    {
        var id = Guid.NewGuid();

        Assert.Equal(WorkloadResolutionKind.Resolved,
            WorkloadResolver.Resolve("  billing-service  ", [Live("billing-service", id)]).Kind);

        Assert.Equal(WorkloadResolutionKind.Resolved,
            WorkloadResolver.Resolve("billing-service", [Live(" billing-service\n", id)]).Kind);
    }

    [Fact]
    public void Matching_is_case_SENSITIVE()
    {
        // A workload name is an identifier and the API matches it exactly, so resolving case-insensitively
        // here would let the CLI revoke a row the server considers a different workload.
        Assert.Equal(WorkloadResolutionKind.NotFound,
            WorkloadResolver.Resolve("Billing-Service", [Live("billing-service")]).Kind);
    }

    [Fact]
    public void A_partial_name_is_NOT_a_match()
    {
        // Guards against a StartsWith/Contains implementation, which would make `--name billing` revoke
        // `billing-service` — a plausible-looking convenience with no safe version.
        Assert.Equal(WorkloadResolutionKind.NotFound,
            WorkloadResolver.Resolve("billing", [Live("billing-service")]).Kind);
    }

    [Fact]
    public void An_empty_candidate_list_and_a_null_one_behave_the_same()
    {
        // An API response with no items and a failure that yielded nothing must not diverge here — the
        // command decides what to do from the KIND, and a null must never crash the resolution.
        Assert.Equal(WorkloadResolutionKind.NotFound, WorkloadResolver.Resolve("x", []).Kind);
        Assert.Equal(WorkloadResolutionKind.NotFound, WorkloadResolver.Resolve("x", null).Kind);
    }

    [Fact]
    public void A_match_with_no_usable_id_is_not_reported_as_resolved()
    {
        // A row the API returned without an id cannot be acted on. Reporting it as Resolved would send
        // Guid.Empty to a DELETE route.
        var result = WorkloadResolver.Resolve("billing-service",
            [new WorkloadCandidate(null, "billing-service", "spiffe://t/p/e/billing-service", false)]);

        Assert.NotEqual(WorkloadResolutionKind.Resolved, result.Kind);
        Assert.Equal(Guid.Empty, result.Id);
    }

    // ── ResolveById: the way OUT of an ambiguous name ────────────────────────────────────────────
    //
    // The ambiguous refusal prints the candidate ids. Without a way to act on one, an operator can
    // read the answer and still not revoke the row they mean — which is how a duplicate registration
    // becomes permanent. These pin that the escape hatch is not itself a way to guess.

    [Fact]
    public void An_id_picks_the_row_a_shared_name_could_not()
    {
        // The case the option exists for: two rows, one name, and the operator names the id.
        var keep = Guid.NewGuid();
        var drop = Guid.NewGuid();
        var candidates = new[] { Live("app-a-revoked", keep), Live("app-a-revoked", drop) };

        Assert.Equal(WorkloadResolutionKind.Ambiguous,
            WorkloadResolver.Resolve("app-a-revoked", candidates).Kind);

        var result = WorkloadResolver.ResolveById(drop, candidates);

        Assert.Equal(WorkloadResolutionKind.Resolved, result.Kind);
        Assert.Equal(drop, result.Id);
    }

    [Fact]
    public void An_id_this_environment_does_not_hold_is_REFUSED_not_forwarded()
    {
        // The whole reason an id resolves against the list instead of going straight to DELETE. An id
        // from another environment would otherwise be posted to a route scoped to this one, and the
        // operator would read whatever the API said about a row they cannot see.
        var result = WorkloadResolver.ResolveById(Guid.NewGuid(), [Live("app-a"), Live("billing")]);

        Assert.Equal(WorkloadResolutionKind.NotFound, result.Kind);
        Assert.Equal(Guid.Empty, result.Id);
    }

    [Fact]
    public void An_empty_id_never_resolves()
    {
        // Guid.Empty is what a failed parse leaves behind. Resolving it would aim a DELETE at a route
        // segment of all zeroes.
        Assert.Equal(WorkloadResolutionKind.NotFound,
            WorkloadResolver.ResolveById(Guid.Empty, [new WorkloadCandidate(Guid.Empty, "x", null, false)]).Kind);
    }

    [Fact]
    public void An_already_revoked_id_reports_that_rather_than_erroring()
    {
        // Same rule as by-name: the desired state already holds, so a re-run of a playbook must not
        // fail on work it already did.
        var id = Guid.NewGuid();
        var result = WorkloadResolver.ResolveById(id, [Revoked("app-a-revoked", id)]);

        Assert.Equal(WorkloadResolutionKind.AlreadyRevoked, result.Kind);
        Assert.Equal(id, result.Id);
    }

    [Fact]
    public void A_resolved_id_carries_the_row_so_the_confirmation_can_name_it()
    {
        // The command prints the name and SPIFFE ID before revoking. With --id the operator typed
        // neither, so both have to come off the resolved candidate — echoing the id back would show
        // them nothing they did not already type.
        var id = Guid.NewGuid();
        var result = WorkloadResolver.ResolveById(id, [Live("billing-service", id)]);

        Assert.Equal("spiffe://t/p/e/billing-service", result.SpiffeId);
        Assert.Equal("billing-service", Assert.Single(result.Candidates).Name);
    }

    [Fact]
    public void An_ambiguous_refusal_says_how_to_get_past_it()
    {
        // A refusal that names ids but not the option that takes one leaves the operator stuck.
        var result = WorkloadResolver.Resolve("dup", [Live("dup"), Live("dup")]);

        Assert.Contains("--id", result.Problem!, StringComparison.Ordinal);
    }
}
