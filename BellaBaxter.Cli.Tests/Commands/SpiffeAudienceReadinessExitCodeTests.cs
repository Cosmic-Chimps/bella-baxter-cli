using BellaBaxter.Client.Models;
using BellaCli.Commands.Spiffe;

namespace BellaBaxter.Cli.Tests.Commands;

/// <summary>
/// Spec 064 T041 — <c>bella spiffe audience-readiness</c> says, by exit code alone, whether enforcement can be
/// switched on: 0 everything ready, 1 something would be refused (or a trust domain is critical), 2 nothing would be
/// refused but something is unproven. A script must not have to parse the output to know whether to stop.
/// </summary>
public class SpiffeAudienceReadinessExitCodeTests
{
    private static AudienceReadinessSection Section(string verdict) => new() { Verdict = verdict };

    private static AudienceReadinessResponse Response(string node, params (string Verdict, string? Critical)[] domains) => new()
    {
        NodeEvidence = Section(node),
        TrustDomains = domains.Select(d => new TrustDomainAudienceReadiness { Section = Section(d.Verdict), Critical = d.Critical }).ToList(),
    };

    [Fact]
    public void Everything_ready_is_0() =>
        Assert.Equal(0, SpiffeAudienceReadinessCommand.ExitCodeFor(Response("ready", ("ready", null))));

    [Fact]
    public void Anything_not_ready_is_1() =>
        Assert.Equal(1, SpiffeAudienceReadinessCommand.ExitCodeFor(Response("ready", ("not-ready", null))));

    [Theory]
    [InlineData("no-claim-rules")]
    [InlineData("claim-rule-not-accepted")] // advisory machine-3: a Contains or unanchored StartsWith rule
    public void A_critical_trust_domain_is_1_even_when_its_audience_is_ready(string critical) =>
        Assert.Equal(1, SpiffeAudienceReadinessCommand.ExitCodeFor(Response("ready", ("ready", critical))));

    [Theory]
    [InlineData("no-evidence")]
    [InlineData("not-measured")]
    public void Unproven_without_refusals_is_2_never_0(string verdict) =>
        Assert.Equal(2, SpiffeAudienceReadinessCommand.ExitCodeFor(Response(verdict, ("ready", null))));

    [Fact]
    public void Not_ready_outranks_unproven() =>
        Assert.Equal(1, SpiffeAudienceReadinessCommand.ExitCodeFor(Response("no-evidence", ("not-ready", null))));

    [Fact]
    public void An_unknown_verdict_is_never_read_as_ready() =>
        Assert.Equal(2, SpiffeAudienceReadinessCommand.ExitCodeFor(Response("something-new")));
}
