using BellaCli.Commands.Spiffe;

namespace BellaBaxter.Cli.Tests.Commands;

/// <summary>
/// Spec 028 — what <c>bella spiffe revoke</c> refuses before it reaches the API.
/// </summary>
/// <remarks>
/// <para><see cref="WorkloadResolverTests"/> covers what an id resolves TO. These cover whether the
/// command accepts the id at all, which is a separate rule and the one carrying the safety argument:
/// <c>--name</c> and <c>--id</c> that disagree are refused rather than resolved in some precedence
/// order. Honouring one of them silently revokes something the operator did not name — the exact
/// outcome the ambiguity refusal exists to prevent, reintroduced by the escape hatch built to avoid
/// it.</para>
///
/// <para>That rule is one <c>if</c> in a method with no dependencies, so nothing stops a later change
/// from replacing it with "id wins" and leaving every other test green. Revocation cascades and is not
/// undoable, so the refusal is pinned here rather than left to review.</para>
/// </remarks>
public class SpiffeRevokeSettingsTests
{
    private static bool Ok(SpiffeRevokeSettings s) => s.Validate().Successful;
    private static string? Why(SpiffeRevokeSettings s) => s.Validate().Message;

    [Fact]
    public void A_handle_is_required()
    {
        // Neither: there is no default row to revoke, so ask.
        var settings = new SpiffeRevokeSettings();

        Assert.False(Ok(settings));
        Assert.Contains("--name", Why(settings)!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_ALONE_is_accepted()
    {
        Assert.True(Ok(new SpiffeRevokeSettings { Name = "billing-service" }));
    }

    [Fact]
    public void An_id_ALONE_is_accepted()
    {
        Assert.True(Ok(new SpiffeRevokeSettings { Id = Guid.NewGuid().ToString() }));
    }

    [Fact]
    public void A_name_AND_an_id_together_are_REFUSED_never_ranked()
    {
        // The rule this file exists for. Picking either one revokes a row the operator did not name
        // in the argument the CLI chose to ignore.
        var settings = new SpiffeRevokeSettings
        {
            Name = "billing-service",
            Id = Guid.NewGuid().ToString(),
        };

        Assert.False(Ok(settings));
        Assert.Contains("not both", Why(settings)!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_id_that_is_not_a_GUID_fails_BEFORE_any_network_call()
    {
        // Validation runs ahead of ExecuteAsync, so a typo costs nothing and quotes the value back
        // rather than reporting it as a workload that does not exist.
        var settings = new SpiffeRevokeSettings { Id = "app-a-revoked" };

        Assert.False(Ok(settings));
        Assert.Contains("app-a-revoked", Why(settings)!, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsedId_and_Validate_read_the_SAME_value()
    {
        // Two parses of one string is how a command validates one thing and then acts on another.
        var id = Guid.NewGuid();
        var settings = new SpiffeRevokeSettings { Id = $"  {id}  " };

        Assert.True(Ok(settings));
        Assert.Equal(id, settings.ParsedId);
    }

    [Fact]
    public void A_whitespace_only_id_is_treated_as_ABSENT_not_as_malformed()
    {
        // `--id ""` is an unset shell variable, not a typo. Reporting it as a bad GUID sends the
        // operator looking at the value instead of at the variable that was empty.
        var settings = new SpiffeRevokeSettings { Id = "   " };

        Assert.False(Ok(settings));
        Assert.Contains("--name", Why(settings)!, StringComparison.Ordinal);
    }
}
