using BellaCli.Commands.Spiffe;

namespace BellaBaxter.Cli.Tests.Commands;

/// <summary>
/// Spec 001 T031 (US3, FR-019) — what <c>bella spiffe set-mode</c> refuses before it reaches the API.
/// </summary>
/// <remarks>
/// <para>The validation is worth testing separately from the request because two of these rules are
/// about not doing damage rather than about typing. Attestation policy is per environment and the API
/// deliberately treats an omitted field as "keep what is recorded", so the failure mode of a careless
/// CLI is not an error message — it is quietly erasing settings the operator never mentioned.</para>
///
/// <para>The mode is REQUIRED rather than defaulted for the same reason: defaulting to Lax would
/// silently loosen an environment somebody had deliberately set to Strict, and defaulting to Strict
/// would break attestation for anyone who ran the command to change only the TTL.</para>
/// </remarks>
public class SpiffeSetModeSettingsTests
{
    private static bool Ok(SpiffeSetModeSettings s) => s.Validate().Successful;
    private static string? Why(SpiffeSetModeSettings s) => s.Validate().Message;

    [Fact]
    public void A_mode_is_required()
    {
        // Neither flag: there is no safe default, so ask.
        Assert.False(Ok(new SpiffeSetModeSettings()));
        Assert.Contains("--strict", Why(new SpiffeSetModeSettings())!, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_modes_at_once_is_refused()
    {
        // Not resolved by precedence. Picking one would mean the operator's contradictory intent was
        // silently decided for them, on a setting that decides whether evidence is verified.
        Assert.False(Ok(new SpiffeSetModeSettings { Strict = true, Lax = true }));
    }

    [Fact]
    public void Either_mode_alone_is_enough()
    {
        Assert.True(Ok(new SpiffeSetModeSettings { Strict = true }));
        Assert.True(Ok(new SpiffeSetModeSettings { Lax = true }));
    }

    [Fact]
    public void Setting_and_clearing_the_same_thing_is_refused_not_ordered()
    {
        // "Last flag wins" would be a coin flip on whether the environment ends up verifying
        // Kubernetes evidence at all.
        Assert.False(Ok(new SpiffeSetModeSettings
        {
            Strict = true, K8sOidcUrl = "https://oidc.example/id/c1", ClearK8sOidc = true,
        }));

        Assert.False(Ok(new SpiffeSetModeSettings
        {
            Strict = true, AwsAccounts = ["123456789012"], ClearAwsAccounts = true,
        }));
    }

    [Fact]
    public void Clearing_alone_is_allowed()
    {
        // Withdrawing trust must be possible — that was the whole defect behind the API's three-valued
        // handling of these fields.
        Assert.True(Ok(new SpiffeSetModeSettings { Strict = true, ClearK8sOidc = true }));
        Assert.True(Ok(new SpiffeSetModeSettings { Strict = true, ClearAwsAccounts = true }));
    }

    [Fact]
    public void A_malformed_aws_account_id_is_caught_next_to_the_flag_that_was_typed()
    {
        // The server refuses these too. Catching them here means the operator sees which value is
        // wrong instead of a 400 listing values they have to match back to their command line.
        foreach (var bad in new[] { "12345678901", "1234-5678-9012", "arn:aws:iam::123456789012:root", "abcdefghijkl" })
        {
            var settings = new SpiffeSetModeSettings { Strict = true, AwsAccounts = [bad] };
            Assert.False(Ok(settings), $"'{bad}' should be refused");
            Assert.Contains(bad, Why(settings)!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_well_formed_account_id_passes_padding_and_all()
    {
        Assert.True(Ok(new SpiffeSetModeSettings { Strict = true, AwsAccounts = ["123456789012"] }));

        // Operators paste. A trailing space is not a typo worth a refusal — it is trimmed.
        Assert.True(Ok(new SpiffeSetModeSettings { Strict = true, AwsAccounts = [" 123456789012 "] }));
    }

    [Fact]
    public void An_out_of_range_svid_ttl_is_refused_and_an_omitted_one_is_not()
    {
        Assert.False(Ok(new SpiffeSetModeSettings { Strict = true, SvidTtlMinutes = 0 }));
        Assert.False(Ok(new SpiffeSetModeSettings { Strict = true, SvidTtlMinutes = 1441 }));

        Assert.True(Ok(new SpiffeSetModeSettings { Strict = true, SvidTtlMinutes = 1 }));
        Assert.True(Ok(new SpiffeSetModeSettings { Strict = true, SvidTtlMinutes = 1440 }));

        // Omitted is not zero. If it were validated as a number, every invocation that did not mention
        // the TTL would be refused.
        Assert.True(Ok(new SpiffeSetModeSettings { Strict = true, SvidTtlMinutes = null }));
    }

    // ===== spec 028 (FR-017, FR-019) =====

    [Fact]
    public void The_mode_descriptions_are_the_two_sentences_the_server_publishes()
    {
        // The API's SpiffeModeMeaning is the single author; the CLI carries a copy only because option
        // descriptions are static. These phrases are what the console shows too, so a drift here is
        // visible on both surfaces at once.
        Assert.Contains("not evaluated", SpiffeSetModeSettings.LaxMeaning);
        Assert.Contains("bootstrap token", SpiffeSetModeSettings.LaxMeaning);
        Assert.Contains("verified", SpiffeSetModeSettings.StrictMeaning);
        Assert.Contains("at least one constraint", SpiffeSetModeSettings.StrictMeaning);
    }

    [Fact]
    public void The_acknowledgement_flag_is_valid_with_strict_and_defaults_off()
    {
        var settings = new SpiffeSetModeSettings { Strict = true, AcknowledgeStrictRefusals = true };
        Assert.True(settings.Validate().Successful);
        Assert.False(new SpiffeSetModeSettings { Strict = true }.AcknowledgeStrictRefusals);
    }
}
