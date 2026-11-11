using System.Reflection;
using BellaCli.Commands.Spiffe;
using Spectre.Console.Cli;

namespace BellaBaxter.Cli.Tests.Commands;

/// <summary>
/// Spec 081 T018 (FR-016) — the Nomad flags of <c>bella spiffe set-mode</c>: what is refused before the request
/// leaves, and which flags exist.
/// </summary>
/// <remarks>
/// Same posture as the Kubernetes flags: a flag that is not passed is not sent, clearing is its own flag, and
/// contradictory flags are refused rather than ordered. Nomad has NO enforce/observe pair — its audience is always
/// enforced — so a flag for one would promise a switch the server does not have.
/// </remarks>
public class SpiffeSetModeNomadTests
{
    private static bool Ok(SpiffeSetModeSettings s) => s.Validate().Successful;
    private static string? Why(SpiffeSetModeSettings s) => s.Validate().Message;

    [Fact]
    public void The_five_nomad_flags_exist_and_no_nomad_enforcement_flag_does()
    {
        var options = typeof(SpiffeSetModeSettings)
            .GetProperties()
            .Select(p => p.GetCustomAttribute<CommandOptionAttribute>())
            .Where(a => a is not null)
            .SelectMany(a => a!.LongNames)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var flag in new[] { "nomad-oidc", "clear-nomad-oidc", "nomad-audience", "nomad-audience-recommended", "clear-nomad-audience" })
            Assert.Contains(flag, options);

        Assert.DoesNotContain(options, o => o.Contains("nomad", StringComparison.Ordinal) && o.Contains("enforce", StringComparison.Ordinal));
    }

    [Fact]
    public void Setting_and_clearing_the_nomad_issuer_is_refused_not_ordered()
    {
        var settings = new SpiffeSetModeSettings { Strict = true, NomadOidcUrl = "https://nomad.example.com", ClearNomadOidc = true };

        Assert.False(Ok(settings));
        Assert.Contains("not both", Why(settings)!, StringComparison.Ordinal);
    }

    [Fact]
    public void Clearing_the_nomad_issuer_alone_is_allowed()
    {
        Assert.True(Ok(new SpiffeSetModeSettings { Strict = true, ClearNomadOidc = true }));
    }

    [Fact]
    public void Only_one_way_of_naming_the_nomad_audience_at_a_time()
    {
        Assert.False(Ok(new SpiffeSetModeSettings { Strict = true, NomadAudience = "x", ClearNomadAudience = true }));
        Assert.False(Ok(new SpiffeSetModeSettings { Strict = true, NomadAudience = "x", NomadAudienceRecommended = true }));
        Assert.False(Ok(new SpiffeSetModeSettings { Strict = true, NomadAudienceRecommended = true, ClearNomadAudience = true }));

        Assert.True(Ok(new SpiffeSetModeSettings { Strict = true, NomadAudience = "x" }));
        Assert.True(Ok(new SpiffeSetModeSettings { Strict = true, NomadAudienceRecommended = true }));
        Assert.True(Ok(new SpiffeSetModeSettings { Strict = true, ClearNomadAudience = true }));
    }

    [Fact]
    public void The_kubernetes_and_nomad_audiences_are_independent()
    {
        // One invocation may set both: they are two settings with two flags.
        Assert.True(Ok(new SpiffeSetModeSettings { Strict = true, K8sAudienceRecommended = true, NomadAudienceRecommended = true }));
    }

    [Theory]
    [InlineData("http://nomad.example.com")]
    [InlineData("HTTP://nomad.example.com:4646")]
    public void An_unencrypted_nomad_oidc_url_is_refused_locally_in_nomads_words(string bad)
    {
        var settings = new SpiffeSetModeSettings { Strict = true, NomadOidcUrl = bad };

        Assert.False(Ok(settings));
        var why = Why(settings)!;
        Assert.Contains("--nomad-oidc must use https", why, StringComparison.Ordinal);
        Assert.Contains("'http'", why, StringComparison.Ordinal);
        Assert.Contains("Nomad server's token signing keys", why, StringComparison.Ordinal);
        Assert.DoesNotContain("cluster", why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nomad.example.com")]
    [InlineData("ftp://nomad.example.com")]
    [InlineData("not a url")]
    public void A_malformed_or_non_web_nomad_oidc_url_is_refused_locally(string bad)
    {
        Assert.False(Ok(new SpiffeSetModeSettings { Strict = true, NomadOidcUrl = bad }));
    }

    [Theory]
    [InlineData("https://nomad.example.com")]
    [InlineData("HTTPS://nomad.example.com:4646")]
    [InlineData("  https://nomad.example.com  ")]
    public void An_encrypted_nomad_oidc_url_passes(string good)
    {
        Assert.True(Ok(new SpiffeSetModeSettings { Strict = true, NomadOidcUrl = good }));
    }

    [Fact]
    public void Nothing_nomad_is_required_and_nothing_is_sent_by_default()
    {
        // Not passed is not sent: the defaults are all "absent", never "".
        var settings = new SpiffeSetModeSettings { Strict = true };

        Assert.True(Ok(settings));
        Assert.Null(settings.NomadOidcUrl);
        Assert.Null(settings.NomadAudience);
        Assert.False(settings.ClearNomadOidc);
        Assert.False(settings.ClearNomadAudience);
        Assert.False(settings.NomadAudienceRecommended);
    }
}
