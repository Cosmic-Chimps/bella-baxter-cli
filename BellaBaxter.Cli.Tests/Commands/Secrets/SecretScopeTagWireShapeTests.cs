using BellaCli.Commands.Secrets;

namespace BellaBaxter.Cli.Tests.Commands.Secrets;

/// <summary>
/// Issue #611: the CLI could not assign scope tags at all, so the pilot's Ansible/CI flow could not
/// be completed without opening the console.
///
/// <para><b>What these tests actually protect.</b> "Scope tags" are not arbitrary tags. Both server
/// enforcement sites read ONE entry whose key is literally <c>scope</c> and whose value is a
/// SPACE-SEPARATED list — <c>GetSecret.cs</c> does <c>Tags.GetValueOrDefault("scope").Split(' ')</c>
/// and <c>GetSecretsByProvider.cs</c> filters the same way — and the console writes exactly that
/// (<c>{ scope: scopes.join(" ") }</c>). The obvious generic reading, <c>--tag drive</c> becoming
/// <c>{"drive":"true"}</c>, would persist a tag that no token can ever match: a tag visible in the
/// console and a token that reads nothing. That is pilot F2's failure again — a silent contract
/// mismatch that looks like working code — which is why the shape is pinned here rather than left to
/// a reviewer's eye.</para>
/// </summary>
public class SecretScopeTagWireShapeTests
{
    private static Dictionary<string, string> Compose(string[] scopes, string[] tags)
    {
        var ok = SecretMetadataArguments.TryCompose(scopes, tags, out var composed, out var error);
        Assert.True(ok, error);
        return composed;
    }

    [Fact]
    public void One_scope_is_written_under_the_scope_key()
    {
        var composed = Compose(["drive"], []);

        Assert.Equal("drive", Assert.Contains("scope", composed));
        Assert.Single(composed);
    }

    [Fact]
    public void Several_scopes_share_one_space_separated_entry()
    {
        var composed = Compose(["drive", "ci"], []);

        // NOT one entry per scope: the server splits a single value on spaces.
        Assert.Single(composed);
        Assert.Equal("drive ci", composed["scope"]);
    }

    [Fact]
    public void The_scope_value_is_what_the_server_splits_back_into_the_scopes_given()
    {
        var composed = Compose(["drive", "ci", "deploy"], []);

        var roundTripped = composed["scope"].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(["drive", "ci", "deploy"], roundTripped);
    }

    [Fact]
    public void Other_tags_are_kept_alongside_the_scope_entry()
    {
        var composed = Compose(["drive"], ["owner=platform", "bella:env-specific=true"]);

        Assert.Equal("drive", composed["scope"]);
        Assert.Equal("platform", composed["owner"]);
        Assert.Equal("true", composed["bella:env-specific"]);
    }

    [Fact]
    public void A_tag_value_may_contain_the_separator_character()
    {
        var composed = Compose([], ["note=a=b"]);

        Assert.Equal("a=b", composed["note"]);
    }

    [Fact]
    public void A_scope_containing_whitespace_is_refused()
    {
        var ok = SecretMetadataArguments.TryCompose(["two words"], [], out _, out var error);

        // Storing it would silently become TWO scopes, and nothing would show the operator which.
        Assert.False(ok);
        Assert.Contains("whitespace", error);
    }

    [Fact]
    public void Scope_given_twice_over_both_spellings_is_refused_rather_than_resolved()
    {
        var ok = SecretMetadataArguments.TryCompose(["drive"], ["scope=ci"], out _, out var error);

        Assert.False(ok);
        Assert.Contains("--scope", error);
    }

    [Fact]
    public void A_tag_without_a_value_separator_is_refused()
    {
        var ok = SecretMetadataArguments.TryCompose([], ["drive"], out _, out var error);

        Assert.False(ok);
        Assert.Contains("KEY=VALUE", error);
    }

    [Fact]
    public void The_same_tag_key_twice_is_refused_rather_than_last_wins()
    {
        var ok = SecretMetadataArguments.TryCompose(
            [],
            ["owner=a", "owner=b"],
            out _,
            out var error
        );

        Assert.False(ok);
        Assert.Contains("more than once", error);
    }

    [Fact]
    public void Nothing_requested_composes_nothing()
    {
        Assert.Empty(Compose([], []));
    }

    [Fact]
    public void Merging_carries_through_tags_the_caller_never_mentioned()
    {
        // Issue #614: the metadata endpoint REPLACES the whole dictionary, so anything not carried
        // through is destroyed. `bella:env-specific` excludes a secret from drift — losing it is
        // silent and only shows up as a drift report that stopped reporting.
        var merged = SecretMetadataArguments.MergeOver(
            new Dictionary<string, string>
            {
                ["bella:env-specific"] = "true",
                ["owner"] = "platform",
            },
            Compose(["drive"], [])
        );

        Assert.Equal("drive", merged["scope"]);
        Assert.Equal("true", merged["bella:env-specific"]);
        Assert.Equal("platform", merged["owner"]);
    }

    [Fact]
    public void Merging_lets_the_caller_replace_a_tag_they_did_mention()
    {
        var merged = SecretMetadataArguments.MergeOver(
            new Dictionary<string, string> { ["scope"] = "old", ["keep"] = "me" },
            Compose(["new"], [])
        );

        Assert.Equal("new", merged["scope"]);
        Assert.Equal("me", merged["keep"]);
    }

    [Fact]
    public void Merging_over_nothing_is_just_what_the_caller_asked_for()
    {
        // The create case: a secret this command minted has no prior tags to read or preserve.
        var merged = SecretMetadataArguments.MergeOver(null, Compose(["drive"], ["a=b"]));

        Assert.Equal(2, merged.Count);
        Assert.Equal("drive", merged["scope"]);
        Assert.Equal("b", merged["a"]);
    }

    [Fact]
    public void The_request_leaves_unspecified_members_null_so_the_server_does_not_touch_them()
    {
        // The endpoint documents null as "don't touch"; sending a default would overwrite.
        var request = SecretMetadataArguments.BuildRequest(null, null);

        Assert.Null(request.Tags);
        Assert.Null(request.IgnoreInScan);
    }

    [Fact]
    public void The_request_carries_tags_as_the_wire_dictionary()
    {
        var request = SecretMetadataArguments.BuildRequest(
            new Dictionary<string, string> { ["scope"] = "drive ci" },
            ignoreInScan: true
        );

        Assert.NotNull(request.Tags);
        Assert.Equal("drive ci", request.Tags!.AdditionalData["scope"]);
        Assert.True(request.IgnoreInScan);
    }
}
