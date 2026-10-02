using BellaCli.Commands.Spiffe;
using BellaCli.Services.Spiffe;

namespace BellaBaxter.Cli.Tests.Services;

/// <summary>
/// Backlog §2.31 / issue #710 — <c>bella spiffe agent</c> sends its bootstrap token to, and receives the
/// SVID private key from, the configured API address, so it refuses plain http off this machine.
/// </summary>
/// <remarks>
/// The rule is <c>BellaBaxter.Client.BellaApiAddress</c>'s and is tested beside it. These prove the
/// agent applies it at both places a credential could leave: the command, before anything is prepared,
/// and each HTTP source, at construction — so no other caller can build one that attests over http.
/// </remarks>
public class SvidAgentRefusesPlainHttpTests
{
    private static readonly SvidAttestationRequest Request =
        new(Guid.NewGuid(), "billing", "bax-bootstrap", "k8s", () => null);

    [Fact]
    public void The_command_refuses_plain_http_off_this_machine_and_names_where_the_address_came_from()
    {
        var refusal = SpiffeAgentCommand.ApiAddressRefusal("http://bella.lan:5522", "BELLA_BAXTER_URL");

        Assert.NotNull(refusal);
        Assert.Contains("from BELLA_BAXTER_URL", refusal, StringComparison.Ordinal);
        Assert.Contains("must use https", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://api.bella-baxter.io")]
    [InlineData("http://localhost:5522")]
    [InlineData("http://127.0.0.1:5522")]
    public void The_command_accepts_https_and_loopback(string address)
    {
        Assert.Null(SpiffeAgentCommand.ApiAddressRefusal(address, "config.json"));
    }

    [Fact]
    public void The_attestation_source_cannot_be_built_over_plain_http()
    {
        var http = new HttpClient { BaseAddress = new Uri("http://10.0.0.5:5522") };

        Assert.Throws<ArgumentException>(() => new HttpSvidSource(http, Request));
    }

    [Fact]
    public void The_JWT_SVID_source_cannot_be_built_over_plain_http()
    {
        var http = new HttpClient { BaseAddress = new Uri("http://host.docker.internal:5522") };

        Assert.Throws<ArgumentException>(() => new HttpJwtSvidSource(http, Request));
    }

    [Fact]
    public void A_source_with_no_base_address_at_all_is_refused_too()
    {
        // An unset BaseAddress would make the relative attest URL fail later with an opaque
        // InvalidOperationException; refusing at construction says what is actually wrong.
        Assert.Throws<ArgumentException>(() => new HttpSvidSource(new HttpClient(), Request));
    }

    [Theory]
    [InlineData("https://api.bella-baxter.io")]
    [InlineData("http://localhost:5522")]
    [InlineData("http://[::1]:5522")]
    public void Both_sources_accept_https_and_loopback(string address)
    {
        var http = new HttpClient { BaseAddress = new Uri(address) };

        _ = new HttpSvidSource(http, Request);
        _ = new HttpJwtSvidSource(http, Request);
    }
}
