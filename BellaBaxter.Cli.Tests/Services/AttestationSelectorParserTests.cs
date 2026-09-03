using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Spec 001 T019 (US1) — parsing <c>--node</c> and <c>--selector</c> arguments.
/// </summary>
/// <remarks>
/// <para>The format looks trivial and is not. A selector is <c>type=value</c> where the TYPE contains a
/// colon (<c>k8s:namespace</c>) and the VALUE may contain almost anything, including <c>=</c> in a
/// base64 fragment or an ARN. Every plausible shortcut — split on all <c>=</c>, split on the last one,
/// split on <c>:</c> — silently corrupts a real input.</para>
///
/// <para>And the corruption is invisible at the point it happens: the registration succeeds and looks
/// right on screen, then attestation is refused later with <c>missing_selector</c>, with nothing
/// pointing back at the command line that caused it.</para>
/// </remarks>
public class AttestationSelectorParserTests
{
    [Fact]
    public void A_plain_type_and_value_parses()
    {
        var selector = AttestationSelectorParser.TryParse("k8s:namespace=payments", "--selector", out var error);

        Assert.Null(error);
        Assert.Equal("k8s:namespace", selector!.Type);
        Assert.Equal("payments", selector.Value);
    }

    [Fact]
    public void The_COLON_in_the_type_is_not_a_separator()
    {
        // Splitting on ':' would yield type "k8s" and lose the rest — and "k8s" is a plausible-looking
        // selector type, so nothing downstream would object.
        var selector = AttestationSelectorParser.TryParse("aws:account=123456789012", "--node", out _);

        Assert.Equal("aws:account", selector!.Type);
        Assert.Equal("123456789012", selector.Value);
    }

    [Fact]
    public void Only_the_FIRST_equals_splits_so_a_value_may_contain_more()
    {
        // The case that makes this a real parser. An ARN, a base64 fragment or a query string in the
        // value survives; split on the last '=' (or on all of them) and it does not.
        var selector = AttestationSelectorParser.TryParse(
            "custom:token=abc=def==", "--selector", out var error);

        Assert.Null(error);
        Assert.Equal("custom:token", selector!.Type);
        Assert.Equal("abc=def==", selector.Value);
    }

    [Fact]
    public void An_argument_with_NO_equals_is_refused_with_an_example()
    {
        var selector = AttestationSelectorParser.TryParse("k8s:namespace", "--selector", out var error);

        Assert.Null(selector);
        Assert.Contains("type=value", error!, StringComparison.Ordinal);

        // Naming the flag matters: a command carries both --node and --selector, and "not in type=value
        // form" without saying which flag sends the reader hunting.
        Assert.Contains("--selector", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_EMPTY_VALUE_is_refused_because_it_could_never_match()
    {
        // `--selector k8s:namespace=` would register a selector that only matches evidence with an empty
        // namespace — i.e. never. The workload becomes unattestable while its registration looks fine.
        var selector = AttestationSelectorParser.TryParse("k8s:namespace=", "--selector", out var error);

        Assert.Null(selector);
        Assert.Contains("can never match", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_TYPE_is_refused()
    {
        Assert.Null(AttestationSelectorParser.TryParse("=payments", "--selector", out var error));
        Assert.Contains("no selector type", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Whitespace_around_either_side_is_trimmed()
    {
        // Shell quoting leaves these behind routinely, and a trailing space baked into a selector value
        // is a registration that never matches.
        var selector = AttestationSelectorParser.TryParse(
            "  k8s:namespace = payments  ", "--selector", out var error);

        Assert.Null(error);
        Assert.Equal("k8s:namespace", selector!.Type);
        Assert.Equal("payments", selector.Value);
    }

    [Fact]
    public void ParseAll_collects_EVERY_error_not_just_the_first()
    {
        // A registration carries several selectors. Reporting one error per invocation means one deploy
        // cycle per typo.
        var (parsed, errors) = AttestationSelectorParser.ParseAll(
            ["k8s:namespace=payments", "broken", "alsobroken", "k8s:sa=billing"], "--selector");

        Assert.Equal(2, parsed.Count);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void A_repeated_type_with_DIFFERENT_values_is_refused_not_last_one_wins()
    {
        // Both would have to match, so the workload could never attest. Last-one-wins would silently
        // discard the operator's first intent and produce a registration nobody asked for.
        var (parsed, errors) = AttestationSelectorParser.ParseAll(
            ["k8s:namespace=payments", "k8s:namespace=ledger"], "--selector");

        Assert.Single(parsed);
        Assert.Single(errors);
        Assert.Contains("could never attest", errors[0], StringComparison.Ordinal);

        // Both values are named so the operator can see which two they typed.
        Assert.Contains("payments", errors[0], StringComparison.Ordinal);
        Assert.Contains("ledger", errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_repeated_type_with_the_SAME_value_is_still_reported_but_differently()
    {
        // Harmless duplication, so the message says so rather than warning about an impossible match.
        var (parsed, errors) = AttestationSelectorParser.ParseAll(
            ["k8s:sa=billing", "k8s:sa=billing"], "--selector");

        Assert.Single(parsed);
        Assert.Single(errors);
        Assert.Contains("same value", errors[0], StringComparison.Ordinal);
        Assert.DoesNotContain("could never attest", errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Different_types_are_not_treated_as_duplicates()
    {
        var (parsed, errors) = AttestationSelectorParser.ParseAll(
            ["k8s:namespace=payments", "k8s:sa=billing-sa"], "--selector");

        Assert.Equal(2, parsed.Count);
        Assert.Empty(errors);
    }

    [Fact]
    public void ToString_round_trips_to_what_was_typed()
    {
        // Printed back in `bella spiffe add --json` and in the list table, so it must be the same text
        // the operator would type again.
        Assert.Equal("k8s:namespace=payments",
            AttestationSelectorParser.TryParse("k8s:namespace=payments", "--selector", out _)!.ToString());
    }
}
