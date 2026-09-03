using BellaCli.Commands.Certs;

namespace BellaBaxter.Cli.Tests.Commands.Certs;

// spec 020 (T024, US1) — the provider gate.
//
// This is the check that stops an import being aimed at the wrong provider by typo, and it must
// refuse BEFORE any certificate is read. Negative cases are the point (Constitution V).

public class ImportProviderGateTests
{
    [Fact]
    public void A_certificate_source_with_a_prefix_is_accepted()
    {
        var refusal = ImportPlanner.RefuseSource(
            "prosa-certs",
            "BellaBaxterSecretsSource",
            "GIGAMON_CERT_"
        );

        Assert.Null(refusal);
    }

    [Fact]
    public void The_type_is_matched_case_insensitively()
    {
        var refusal = ImportPlanner.RefuseSource(
            "prosa-certs",
            "bellabaxtersecretssource",
            "GIGAMON_CERT_"
        );

        Assert.Null(refusal);
    }

    [Theory]
    [InlineData("Gigamon")]
    [InlineData("Dns")]
    [InlineData("GitScripts")]
    [InlineData("Acme")]
    [InlineData("Vault")]
    [InlineData("CertStorage")]
    public void Any_other_provider_type_is_refused(string providerType)
    {
        var refusal = ImportPlanner.RefuseSource("some-provider", providerType, "GIGAMON_CERT_");

        Assert.NotNull(refusal);
        Assert.Contains(providerType, refusal);
        Assert.Contains("BellaBaxterSecretsSource", refusal);
    }

    [Fact]
    public void An_unknown_provider_type_is_refused()
    {
        var refusal = ImportPlanner.RefuseSource("some-provider", null, "GIGAMON_CERT_");

        Assert.NotNull(refusal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_source_with_no_prefix_is_refused(string? prefix)
    {
        var refusal = ImportPlanner.RefuseSource(
            "prosa-certs",
            "BellaBaxterSecretsSource",
            prefix
        );

        Assert.NotNull(refusal);
        Assert.Contains("secret_prefix", refusal);
    }

    // ── Secret naming (FR-007, research D8) ──────────────────────────────────

    [Fact]
    public void A_common_name_with_dots_makes_a_valid_secret_key()
    {
        // The env-var pattern used by `bella secrets set` would reject this. Certificates need it.
        Assert.True(ImportPlanner.IsValidSecretKey("GIGAMON_CERT_adyen.prosa.com.mx"));
    }

    [Fact]
    public void A_mixed_case_common_name_is_preserved_in_the_key()
    {
        Assert.True(ImportPlanner.IsValidSecretKey("GIGAMON_CERT_ADkushki.prosa.com.mx"));
    }

    [Fact]
    public void A_hyphenated_common_name_makes_a_valid_secret_key()
    {
        Assert.True(ImportPlanner.IsValidSecretKey("GIGAMON_CERT_B425-HH-E.prosa.com.mx"));
    }

    [Theory]
    [InlineData("GIGAMON_CERT_app example.com")] // whitespace
    [InlineData("GIGAMON_CERT_app/../etc")] // path traversal characters
    [InlineData("GIGAMON_CERT_wild*card.com")] // wildcard
    [InlineData(".leading-dot")] // must start alphanumeric
    [InlineData("")]
    public void A_key_with_disallowed_characters_is_rejected(string key)
    {
        Assert.False(ImportPlanner.IsValidSecretKey(key));
    }
}
