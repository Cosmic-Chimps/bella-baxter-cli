using BellaCli.Services;

namespace BellaBaxter.Cli.Tests.Services;

// spec 020 (T012, T019, US4) — the destination-selection rule.
//
// This is a fail-closed gate: a write must never land on a provider that cannot store secrets,
// and ambiguity with nobody to ask must REFUSE rather than guess. The negative cases below are
// the point of the test file (Constitution V).

public class SecretProviderResolverTests
{
    private static SecretProviderCandidate Secrets(string slug, string type = "Vault") =>
        new(slug, type, "Secrets");

    private static SecretProviderCandidate CertRotation(string slug, string type) =>
        new(slug, type, "CertificateRotation");

    // ── T019: the no-regression guarantee ────────────────────────────────────

    [Fact]
    public void Single_secrets_provider_resolves_with_no_prompt()
    {
        var attached = new[] { Secrets("openbao") };

        // canPrompt: false proves no operator input is needed — the same result a scripted run
        // gets. This is what guarantees existing single-store environments are unaffected.
        var decision = SecretProviderSelection.Decide(attached, explicitSlug: null, canPrompt: false);

        Assert.Equal(SecretProviderDecisionKind.Resolved, decision.Kind);
        Assert.Equal("openbao", decision.Slug);
    }

    [Fact]
    public void Single_secrets_provider_resolves_even_when_cert_providers_are_attached_first()
    {
        // The exact shape that broke providerList[0]: the secrets provider is NOT first.
        var attached = new[]
        {
            CertRotation("gigamon-fm", "Gigamon"),
            CertRotation("prosa-certs", "BellaBaxterSecretsSource"),
            CertRotation("cloudflare-dns", "Dns"),
            Secrets("openbao"),
        };

        var decision = SecretProviderSelection.Decide(attached, null, canPrompt: false);

        Assert.Equal(SecretProviderDecisionKind.Resolved, decision.Kind);
        Assert.Equal("openbao", decision.Slug);
    }

    // ── Ambiguity ────────────────────────────────────────────────────────────

    [Fact]
    public void Several_secrets_providers_ask_the_operator_when_one_is_present()
    {
        var attached = new[] { Secrets("openbao"), Secrets("aws-sm", "AwsSecretsManager") };

        var decision = SecretProviderSelection.Decide(attached, null, canPrompt: true);

        Assert.Equal(SecretProviderDecisionKind.NeedsChoice, decision.Kind);
        Assert.Equal(2, decision.Choices.Count);
        Assert.Null(decision.Slug);
    }

    [Fact]
    public void Several_secrets_providers_refuse_when_nobody_can_be_asked()
    {
        var attached = new[] { Secrets("openbao"), Secrets("aws-sm", "AwsSecretsManager") };

        var decision = SecretProviderSelection.Decide(attached, null, canPrompt: false);

        Assert.Equal(SecretProviderDecisionKind.Failed, decision.Kind);
        Assert.Null(decision.Slug);
        // The refusal must name the candidates and how to pick one, or it is a dead end.
        Assert.Contains("openbao", decision.Error!);
        Assert.Contains("aws-sm", decision.Error!);
        Assert.Contains("--provider", decision.Error!);
    }

    // ── Explicit destination ─────────────────────────────────────────────────

    [Fact]
    public void Explicitly_named_secrets_provider_is_used()
    {
        var attached = new[] { Secrets("openbao"), Secrets("aws-sm", "AwsSecretsManager") };

        var decision = SecretProviderSelection.Decide(attached, "aws-sm", canPrompt: false);

        Assert.Equal(SecretProviderDecisionKind.Resolved, decision.Kind);
        Assert.Equal("aws-sm", decision.Slug);
    }

    [Fact]
    public void Explicitly_named_provider_is_matched_case_insensitively()
    {
        var attached = new[] { Secrets("OpenBao") };

        var decision = SecretProviderSelection.Decide(attached, "openbao", canPrompt: false);

        Assert.Equal(SecretProviderDecisionKind.Resolved, decision.Kind);
        Assert.Equal("OpenBao", decision.Slug);
    }

    [Fact]
    public void Explicitly_named_non_secrets_provider_is_refused()
    {
        var attached = new[] { Secrets("openbao"), CertRotation("gigamon-fm", "Gigamon") };

        var decision = SecretProviderSelection.Decide(attached, "gigamon-fm", canPrompt: true);

        Assert.Equal(SecretProviderDecisionKind.Failed, decision.Kind);
        Assert.Contains("Gigamon", decision.Error!);
        Assert.Contains("does not store", decision.Error!);
    }

    [Fact]
    public void Explicitly_named_provider_that_is_not_attached_is_refused()
    {
        var attached = new[] { Secrets("openbao") };

        var decision = SecretProviderSelection.Decide(attached, "somewhere-else", canPrompt: true);

        Assert.Equal(SecretProviderDecisionKind.Failed, decision.Kind);
        Assert.Contains("not attached", decision.Error!);
    }

    // ── Nothing usable ───────────────────────────────────────────────────────

    [Fact]
    public void No_secrets_provider_attached_is_refused_and_says_so()
    {
        var attached = new[]
        {
            CertRotation("gigamon-fm", "Gigamon"),
            CertRotation("prosa-certs", "BellaBaxterSecretsSource"),
        };

        var decision = SecretProviderSelection.Decide(attached, null, canPrompt: true);

        Assert.Equal(SecretProviderDecisionKind.Failed, decision.Kind);
        Assert.Contains("No secrets provider", decision.Error!);
        Assert.Contains("gigamon-fm", decision.Error!);
    }

    [Fact]
    public void No_providers_at_all_is_refused()
    {
        var decision = SecretProviderSelection.Decide([], null, canPrompt: true);

        Assert.Equal(SecretProviderDecisionKind.Failed, decision.Kind);
        Assert.Contains("No providers", decision.Error!);
    }

    [Fact]
    public void An_unknown_meta_type_never_counts_as_a_secrets_provider()
    {
        // Fail closed on an unrecognised meta-type: a provider we cannot classify is not a
        // destination.
        var attached = new[] { new SecretProviderCandidate("mystery", "HttpRest", "Generic") };

        var decision = SecretProviderSelection.Decide(attached, null, canPrompt: true);

        Assert.Equal(SecretProviderDecisionKind.Failed, decision.Kind);
    }

    [Fact]
    public void Meta_type_matching_is_case_insensitive()
    {
        var attached = new[] { new SecretProviderCandidate("openbao", "Vault", "secrets") };

        var decision = SecretProviderSelection.Decide(attached, null, canPrompt: false);

        Assert.Equal(SecretProviderDecisionKind.Resolved, decision.Kind);
    }
}
